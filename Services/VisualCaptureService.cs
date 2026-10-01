using System.Diagnostics;
using Microsoft.Extensions.Options;
using Microsoft.Playwright;
using VisualMonitor.Web.Models;
using VisualMonitor.Web.Options;

namespace VisualMonitor.Web.Services;

public sealed class VisualCaptureService : IAsyncDisposable
{
    private readonly IWebHostEnvironment _environment;
    private readonly VisualMonitorOptions _options;
    private readonly ILogger<VisualCaptureService> _logger;
    private readonly SemaphoreSlim _browserInitLock = new(1, 1);
    private readonly SslCheckService? _ssl;
    private IPlaywright? _playwright;
    private IBrowser? _browser;

    public VisualCaptureService(
        IWebHostEnvironment environment,
        IOptions<VisualMonitorOptions> options,
        ILogger<VisualCaptureService> logger, SslCheckService? ssl = null)
    {
        _environment = environment;
        _options = options.Value;
        _logger = logger;
        _ssl = ssl;
    }

    public async Task<SiteCaptureResult> CaptureAsync(MonitoredSite site, CancellationToken cancellationToken)
    {

        var overall = Stopwatch.StartNew();
        var checkedAt = DateTime.UtcNow;
        
	var vietnamTime = TimeZoneInfo.ConvertTimeFromUtc(
    	    checkedAt,
    	    TimeZoneInfo.FindSystemTimeZoneById("Asia/Ho_Chi_Minh")
	);

	var historyKey = vietnamTime.ToString("yyyyMMdd_HHmmss_fffffff", System.Globalization.CultureInfo.InvariantCulture);
	var result = new SiteCaptureResult
        {
            Ssl = _ssl?.GetCached(site.Url),
            SiteId = site.Id,
            SiteKey = site.SiteKey,
            Name = site.Name,
            Url = site.Url,
            CheckedAtUtc = checkedAt
        };

        foreach (var profile in DeviceProfiles.All)
        {
            cancellationToken.ThrowIfCancellationRequested();
            result.Profiles[profile.Key] = await CaptureProfileAsync(site, profile, historyKey, cancellationToken);
        }

        overall.Stop();
        result.ElapsedMs = overall.Elapsed.TotalMilliseconds;

        if (result.Profiles.Values.Any(x => !x.Ok || x.HttpStatus >= 500))
        {
            result.Status = "critical";
        }
        else if (result.Profiles.Values.Any(x => x.RedirectedOffsite || x.HttpStatus < 200 || x.HttpStatus >= 400))
        {
            result.Status = "warning";
        }
        else
        {
            result.Status = "ok";
        }

        var manifestPaths = BuildPaths(site.SiteKey, "desktop", historyKey);
        if (_options.SaveHistory)
            CaptureManifest.Write(manifestPaths.HistoryDir, result);
        CaptureManifest.Write(manifestPaths.CurrentDir, result);
        CleanupHistory(site.SiteKey);
        return result;
    }

    private async Task<ProfileCaptureResult> CaptureProfileAsync(
        MonitoredSite site,
        DeviceProfile profile,
        string historyKey,
        CancellationToken cancellationToken)
    {
        var result = new ProfileCaptureResult
        {
            Key = profile.Key,
            Label = profile.Label,
            Width = profile.Width,
            Height = profile.Height,
            CurrentImageUrl = $"/captures/{site.SiteKey}/current/{profile.Key}.webp",
            CurrentFullImageUrl = $"/captures/{site.SiteKey}/current/{profile.Key}-full.webp",
            PreviousImageUrl = $"/captures/{site.SiteKey}/previous/{profile.Key}.webp",
            PreviousFullImageUrl = $"/captures/{site.SiteKey}/previous/{profile.Key}-full.webp"
        };

        IBrowserContext? context = null;
        var sw = Stopwatch.StartNew();
        var phase = "monitor";
        try
        {
            await EnsureBrowserAsync();
            context = await _browser!.NewContextAsync(new BrowserNewContextOptions
            {
                ViewportSize = new() { Width = profile.Width, Height = profile.Height },
                UserAgent = profile.UserAgent, IsMobile = profile.IsMobile, HasTouch = profile.HasTouch,
                DeviceScaleFactor = profile.DeviceScaleFactor, IgnoreHTTPSErrors = false, Locale = "vi-VN"
            });
            // Closing this isolated context interrupts navigation/screenshots on shutdown.
            using var registration = cancellationToken.Register(() => { _ = context.CloseAsync().ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default); });
            var page = await context.NewPageAsync();
            page.SetDefaultTimeout(_options.NavigationTimeoutMs);
            page.Response += (_, response) =>
            {
                if (response.Request.IsNavigationRequest && response.Frame == page.MainFrame)
                    result.HttpStatus = response.Status;
            };
            phase = "navigation";
            try
            {
                var response = await page.GotoAsync(site.Url, new() { WaitUntil = WaitUntilState.DOMContentLoaded, Timeout = _options.NavigationTimeoutMs });
                result.HttpStatus = response?.Status ?? result.HttpStatus;
                if (_options.RenderWaitMs > 0) await page.WaitForTimeoutAsync(_options.RenderWaitMs);
                result.Ok = true;
                if (result.HttpStatus >= 400)
                {
                    result.FailureKind = "http";
                    result.Error = $"HTTP {result.HttpStatus}";
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                result.FailureKind = ClassifyFailure(ex);
                result.Error = ex.Message;
                result.Partial = result.FailureKind == "timeout" && result.HttpStatus > 0;
                _logger.LogWarning("Navigation failed for {Site}/{Profile}: {Kind}", site.SiteKey, profile.Key, result.FailureKind);
            }
            result.LoadMs = sw.Elapsed.TotalMilliseconds;
            result.FinalUrl = page.Url;
            result.RedirectedOffsite = IsOffsiteRedirect(site.Url, page.Url);
            cancellationToken.ThrowIfCancellationRequested();
            // Network/TLS failures have no trustworthy website document to photograph.
            if (result.Ok || result.Partial)
            {
                phase = "screenshot";
                result.Title = await page.TitleAsync();
                var paths = BuildPaths(site.SiteKey, profile.Key, historyKey);
                Directory.CreateDirectory(paths.CurrentDir);
                Directory.CreateDirectory(paths.PreviousDir);
                var tempViewport = paths.CurrentViewport + ".tmp.webp";
                var tempFull = paths.CurrentFull + ".tmp.webp";
                try
                {
                    await page.ScreenshotAsync(new() { Path = tempViewport, Type = ScreenshotType.Webp, Quality = 82, FullPage = false, Animations = ScreenshotAnimations.Disabled, Scale = ScreenshotScale.Css, Timeout = 15000 });
                    await page.ScreenshotAsync(new() { Path = tempFull, Type = ScreenshotType.Webp, Quality = 76, FullPage = true, Animations = ScreenshotAnimations.Disabled, Scale = ScreenshotScale.Css, Timeout = 15000 });
                    phase = "storage";
                    cancellationToken.ThrowIfCancellationRequested();
                    if (_options.SaveHistory)
                    {
                        Directory.CreateDirectory(paths.HistoryDir);
                        File.Copy(tempViewport, Path.Combine(paths.HistoryDir, $"{profile.Key}.webp"));
                        File.Copy(tempFull, Path.Combine(paths.HistoryDir, $"{profile.Key}-full.webp"));
                        result.CurrentImageUrl = $"/captures/{site.SiteKey}/history/{historyKey}/{profile.Key}.webp";
                        result.CurrentFullImageUrl = $"/captures/{site.SiteKey}/history/{historyKey}/{profile.Key}-full.webp";
                    }
                    Rotate(paths.CurrentViewport, paths.PreviousViewport);
                    Rotate(paths.CurrentFull, paths.PreviousFull);
                    File.Move(tempViewport, paths.CurrentViewport, true);
                    File.Move(tempFull, paths.CurrentFull, true);
                    result.HasImage = true;
                }
                finally
                {
                    foreach (var file in new[] { tempViewport, tempFull })
                        try { File.Delete(file); } catch (IOException) { }
                }
            }
        }
        catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
        {
            result.Ok = false;
            result.CaptureError = ex.Message;
            result.FailureKind ??= phase == "navigation" ? ClassifyFailure(ex) : phase;
            result.Error ??= ex.Message;
            _logger.LogError(ex, "Capture failed for {Site} / {Profile}", site.SiteKey, profile.Key);
        }
        finally
        {
            result.LoadMs = sw.Elapsed.TotalMilliseconds;
            if (context is not null)
                try { await context.CloseAsync(); } catch (Exception ex) { _logger.LogWarning(ex, "Could not close capture context"); }
        }
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.HasImage) { result.CurrentImageUrl = ""; result.CurrentFullImageUrl = ""; }
        return result;
    }

    public static string ClassifyFailure(Exception ex)
    {
        var message = ex.Message;
        if (message.Contains("ERR_NAME_NOT_RESOLVED")) return "dns";
        if (message.Contains("ERR_CERT_") || message.Contains("ERR_SSL_")) return "tls";
        if (ex is TimeoutException || message.Contains("Timeout", StringComparison.OrdinalIgnoreCase) || message.Contains("TIMED_OUT")) return "timeout";
        if (message.Contains("net::ERR_")) return "network";
        return "monitor";
    }

    private async Task EnsureBrowserAsync()
    {
        if (_browser?.IsConnected == true)
            return;

        await _browserInitLock.WaitAsync();
        try
        {
            if (_browser?.IsConnected == true)
                return;

            _playwright ??= await Playwright.CreateAsync();
            _browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
            {
                Headless = true
            });
        }
        finally
        {
            _browserInitLock.Release();
        }
    }

    private CapturePaths BuildPaths(string siteKey, string profileKey, string historyKey)
    {
        var root = Path.IsPathRooted(_options.ScreenshotRoot)
            ? _options.ScreenshotRoot
            : Path.Combine(_environment.ContentRootPath, _options.ScreenshotRoot);

        var siteRoot = Path.Combine(root, siteKey);
        var currentDir = Path.Combine(siteRoot, "current");
        var previousDir = Path.Combine(siteRoot, "previous");
        var historyDir = Path.Combine(siteRoot, "history", historyKey);

        return new CapturePaths(
            currentDir,
            previousDir,
            historyDir,
            Path.Combine(currentDir, $"{profileKey}.webp"),
            Path.Combine(currentDir, $"{profileKey}-full.webp"),
            Path.Combine(previousDir, $"{profileKey}.webp"),
            Path.Combine(previousDir, $"{profileKey}-full.webp"));
    }

    private static void Rotate(string current, string previous)
    {
        if (!File.Exists(current))
            return;

        Directory.CreateDirectory(Path.GetDirectoryName(previous)!);
        File.Copy(current, previous, true);
    }

    private void CleanupHistory(string siteKey)
    {
        if (!_options.SaveHistory || _options.HistoryRetentionDays <= 0)
            return;

        var root = Path.IsPathRooted(_options.ScreenshotRoot)
            ? _options.ScreenshotRoot
            : Path.Combine(_environment.ContentRootPath, _options.ScreenshotRoot);
        var historyRoot = Path.Combine(root, siteKey, "history");
        if (!Directory.Exists(historyRoot))
            return;

        var cutoff = DateTime.UtcNow.AddDays(-_options.HistoryRetentionDays);
        foreach (var directory in Directory.EnumerateDirectories(historyRoot))
        {
            var info = new DirectoryInfo(directory);
            if (info.CreationTimeUtc < cutoff)
            {
                try { info.Delete(true); }
                catch (Exception ex) { _logger.LogWarning(ex, "Could not remove history folder {Folder}", directory); }
            }
        }
    }

    private static bool IsOffsiteRedirect(string originalUrl, string finalUrl)
    {
        if (!Uri.TryCreate(originalUrl, UriKind.Absolute, out var original) ||
            !Uri.TryCreate(finalUrl, UriKind.Absolute, out var final))
            return false;

        static string NormalizeHost(string host) =>
            host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? host[4..].ToLowerInvariant() : host.ToLowerInvariant();

        return !string.Equals(NormalizeHost(original.Host), NormalizeHost(final.Host), StringComparison.OrdinalIgnoreCase);
    }

    public async ValueTask DisposeAsync()
    {
        if (_browser?.IsConnected == true)
            await _browser.CloseAsync();
        _playwright?.Dispose();
        _browserInitLock.Dispose();
    }

    private sealed record CapturePaths(
        string CurrentDir,
        string PreviousDir,
        string HistoryDir,
        string CurrentViewport,
        string CurrentFull,
        string PreviousViewport,
        string PreviousFull);
}
