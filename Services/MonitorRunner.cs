using Microsoft.EntityFrameworkCore;
using VisualMonitor.Web.Data;
using VisualMonitor.Web.Models;

namespace VisualMonitor.Web.Services;

public sealed class MonitorRunner
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly VisualCaptureService _captureService;
    private readonly System.Collections.Concurrent.ConcurrentDictionary<int, SemaphoreSlim> _locks = new();

    public MonitorRunner(
        IServiceScopeFactory scopeFactory,
        VisualCaptureService captureService)
    {
        _scopeFactory = scopeFactory;
        _captureService = captureService;
    }

    public async Task<SiteCaptureResult?> RunOnceAsync(int siteId, CancellationToken cancellationToken = default)
    {
        var runLock = _locks.GetOrAdd(siteId, _ => new SemaphoreSlim(1, 1));
        if (!await runLock.WaitAsync(0, cancellationToken))
            return null;

        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var site = await db.MonitoredSites
                .Include(x => x.State)
                .SingleAsync(x => x.Id == siteId, cancellationToken);

            if (!site.Enabled)
                return null;

            var result = await _captureService.CaptureAsync(site, cancellationToken);
            ApplyState(site.State!, result);
            await db.SaveChangesAsync(cancellationToken);
            return result;
        }
        finally
        {
            runLock.Release();
        }
    }

    private static void ApplyState(MonitorState state, SiteCaptureResult capture)
    {
        state.LastCheckedAtUtc = capture.CheckedAtUtc;
        state.OverallStatus = capture.Status;

        var desktop = capture.Profiles["desktop"];
        state.DesktopOk = desktop.Ok;
        state.DesktopHttpStatus = desktop.HttpStatus;
        state.DesktopTitle = desktop.Title;
        state.DesktopFinalUrl = desktop.FinalUrl;
        state.DesktopLoadMs = desktop.LoadMs;
        state.DesktopRedirectedOffsite = desktop.RedirectedOffsite;
        state.DesktopError = desktop.Error;

        var tablet = capture.Profiles["tablet"];
        state.TabletOk = tablet.Ok;
        state.TabletHttpStatus = tablet.HttpStatus;
        state.TabletTitle = tablet.Title;
        state.TabletFinalUrl = tablet.FinalUrl;
        state.TabletLoadMs = tablet.LoadMs;
        state.TabletRedirectedOffsite = tablet.RedirectedOffsite;
        state.TabletError = tablet.Error;

        var mobile = capture.Profiles["mobile"];
        state.MobileOk = mobile.Ok;
        state.MobileHttpStatus = mobile.HttpStatus;
        state.MobileTitle = mobile.Title;
        state.MobileFinalUrl = mobile.FinalUrl;
        state.MobileLoadMs = mobile.LoadMs;
        state.MobileRedirectedOffsite = mobile.RedirectedOffsite;
        state.MobileError = mobile.Error;

        state.LastError = new[] { desktop.Error, tablet.Error, mobile.Error }
            .FirstOrDefault(x => !string.IsNullOrWhiteSpace(x));
    }
}
