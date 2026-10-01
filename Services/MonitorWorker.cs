using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisualMonitor.Web.Data;
using VisualMonitor.Web.Options;

namespace VisualMonitor.Web.Services;

public sealed class MonitorWorker(IServiceScopeFactory scopes, MonitorRunner runner,
    IOptions<VisualMonitorOptions> options, ILogger<MonitorWorker> logger, SslCheckService ssl) : BackgroundService
{
    private readonly Channel<int> _queue = Channel.CreateUnbounded<int>();
    private readonly ConcurrentDictionary<int, string> _activity = new();
    private readonly ConcurrentDictionary<int, DateTime> _next = new();
    public string Activity(int id) => _activity.GetValueOrDefault(id, "idle");
    public DateTime? NextCheck(int id) => _next.TryGetValue(id, out var time) ? time : null;
    public bool Enqueue(int id)
    {
        if (!_activity.TryAdd(id, "queued")) return false;
        if (_queue.Writer.TryWrite(id)) return true;
        _activity.TryRemove(id, out _);
        return false;
    }

    public bool TryBeginEdit(int id) => _activity.TryAdd(id, "editing");
    public void EndEdit(int id, bool changed, bool deleted = false)
    {
        if (changed || deleted) _next.TryRemove(id, out _);
        _activity.TryRemove(id, out _);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.WhenAll(ScheduleAsync(stoppingToken), ConsumeAsync(stoppingToken), ConsumeAsync(stoppingToken));
    }

    private async Task ScheduleAsync(CancellationToken ct)
    {
        var first = true;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        do
        {
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var sites = await db.MonitoredSites.AsNoTracking().Include(x => x.State)
                    .Where(x => x.Enabled).ToListAsync(ct);
                foreach (var site in sites)
                {
                    var due = _next.GetOrAdd(site.Id, _ => first && options.Value.CaptureOnStartup
                        ? DateTime.UtcNow
                        : (site.State?.LastCheckedAtUtc ?? DateTime.UtcNow.AddSeconds(-site.IntervalSeconds))
                            .AddSeconds(Math.Max(30, site.IntervalSeconds)));
                    if (due <= DateTime.UtcNow) Enqueue(site.Id);
                }
                first = false;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception ex) { logger.LogError(ex, "Could not schedule websites."); }
        } while (await timer.WaitForNextTickAsync(ct));
    }

    private async Task ConsumeAsync(CancellationToken ct)
    {
        await foreach (var id in _queue.Reader.ReadAllAsync(ct))
        {
            _activity[id] = "running";
            var interval = Math.Max(30, options.Value.IntervalSeconds);
            try
            {
                using var scope = scopes.CreateScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var site = await db.MonitoredSites.Include(x => x.State).SingleOrDefaultAsync(x => x.Id == id, ct);
                if (site is null || !site.Enabled) continue;
                interval = Math.Max(30, site.IntervalSeconds);
                try { await ssl.CheckAsync(site.Url, ct); await runner.RunOnceAsync(id, ct); }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Capture failed for site {SiteId}", id);
                    site.State!.DesktopOk = site.State.TabletOk = site.State.MobileOk = false;
                    site.State.DesktopError = site.State.TabletError = site.State.MobileError = "Monitor: " + ex.Message;
                    site.State.DesktopHttpStatus = site.State.TabletHttpStatus = site.State.MobileHttpStatus = 0;
                    site.State.OverallStatus = "critical";
                    site.State.LastError = ex.Message;
                    site.State.LastCheckedAtUtc = DateTime.UtcNow;
                    await db.SaveChangesAsync(ct);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { logger.LogError(ex, "Could not process site {SiteId}", id); }
            finally
            {
                _next[id] = DateTime.UtcNow.AddSeconds(interval);
                _activity.TryRemove(id, out _);
            }
        }
    }
}
