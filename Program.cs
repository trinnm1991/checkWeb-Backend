using Microsoft.OpenApi;
using Microsoft.Extensions.FileProviders;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisualMonitor.Web.Data;
using VisualMonitor.Web.Models;
using VisualMonitor.Web.Options;
using VisualMonitor.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.Configure<VisualMonitorOptions>(builder.Configuration.GetSection("VisualMonitor"));
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddSingleton<SiteBuilderStore>();
builder.Services.AddSingleton<VisualCaptureService>();
builder.Services.AddSingleton<SslCheckService>();
builder.Services.AddSingleton<MonitorRunner>();
builder.Services.AddSingleton<MonitorWorker>();
builder.Services.AddHostedService<MonitorWorker>(services => services.GetRequiredService<MonitorWorker>());

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(o => o.SwaggerDoc("v1", new OpenApiInfo
{
    Title = "CheckWeb Backend", Version = "v1", Description = "Website capture, SSL and site builder API."
}));
var app = builder.Build();
if (app.Configuration.GetValue<bool>("Swagger:Enabled"))
{
    app.UseSwagger();
    app.UseSwaggerUI(o => o.SwaggerEndpoint("v1/swagger.json", "CheckWeb v1"));
}

await DbSeeder.SeedAsync(app.Services);

// Dashboard is served exclusively by Nginx on server A.

{
    var o = app.Services.GetRequiredService<IOptions<VisualMonitorOptions>>().Value;
    var env = app.Environment;
    var root = Path.IsPathRooted(o.ScreenshotRoot)
        ? o.ScreenshotRoot
        : Path.Combine(env.ContentRootPath, o.ScreenshotRoot);

    Directory.CreateDirectory(root);

    var contentTypeProvider = new FileExtensionContentTypeProvider();
    contentTypeProvider.Mappings[".webp"] = "image/webp";

    app.UseStaticFiles(new StaticFileOptions
    {
        FileProvider = new PhysicalFileProvider(root),
        RequestPath = "/captures",
        ContentTypeProvider = contentTypeProvider
    });
}

app.MapGet("/api/health", () => Results.Ok(new { status = "ok", utc = DateTime.UtcNow }));

app.MapGet("/api/monitor", async (IWebHostEnvironment env, AppDbContext db, IOptions<VisualMonitorOptions> options, MonitorRunner runner, MonitorWorker worker, SslCheckService ssl, int? siteId) =>
{
    var site = await db.MonitoredSites
        .AsNoTracking()
        .Include(x => x.State)
        .SingleOrDefaultAsync(x => siteId.HasValue ? x.Id == siteId.Value : x.SiteKey == options.Value.SiteKey);
    if (site is null) return Results.NotFound();

    var state = site.State!;
    var cacheBust = state.LastCheckedAtUtc?.Ticks ?? 0;
    var root = Path.IsPathRooted(options.Value.ScreenshotRoot) ? options.Value.ScreenshotRoot : Path.Combine(env.ContentRootPath, options.Value.ScreenshotRoot);
    var attempt = CaptureManifest.Read(Path.Combine(root, site.SiteKey, "current"));
    // PostgreSQL stores microseconds; reject manifests from a different attempt.
    if (attempt is not null && (!state.LastCheckedAtUtc.HasValue || Math.Abs((attempt.CheckedAtUtc - state.LastCheckedAtUtc.Value).Ticks) >= 10)) attempt = null;

    object Profile(string key, bool ok, int http, string title, string finalUrl, double loadMs, bool offsite, string? error) => new
    {
        key,
        ok,
        httpStatus = http,
        title,
        finalUrl,
        loadMs = Math.Round(loadMs, 1),
        redirectedOffsite = offsite,
        error,
        failureKind = attempt?.Profiles.GetValueOrDefault(key)?.FailureKind ?? (error is null ? null : "unknown"),
        partial = attempt?.Profiles.GetValueOrDefault(key)?.Partial ?? false,
        captureError = attempt?.Profiles.GetValueOrDefault(key)?.CaptureError,
        image = attempt?.Profiles.GetValueOrDefault(key) is { HasImage: true } p ? p.CurrentImageUrl + $"?v={cacheBust}" : null,
        fullImage = attempt?.Profiles.GetValueOrDefault(key) is { HasImage: true } f ? f.CurrentFullImageUrl + $"?v={cacheBust}" : null,
        previousImage = $"/captures/{site.SiteKey}/previous/{key}.webp?v={cacheBust}",
        previousFullImage = $"/captures/{site.SiteKey}/previous/{key}-full.webp?v={cacheBust}"
    };

    return Results.Ok(new
    {
        site = new { site.Id, site.SiteKey, site.Name, site.Url, site.Enabled, site.IntervalSeconds },
        status = state.OverallStatus,
        ssl = ssl.GetCached(site.Url),
        lastCheckedAtUtc = state.LastCheckedAtUtc,
        nextCheckAtUtc = site.Enabled ? worker.NextCheck(site.Id) : null,
        isRunning = worker.Activity(site.Id) != "idle",
        activity = worker.Activity(site.Id),
        lastError = state.LastError,
        profiles = new
        {
            desktop = Profile("desktop", state.DesktopOk, state.DesktopHttpStatus, state.DesktopTitle, state.DesktopFinalUrl, state.DesktopLoadMs, state.DesktopRedirectedOffsite, state.DesktopError),
            tablet = Profile("tablet", state.TabletOk, state.TabletHttpStatus, state.TabletTitle, state.TabletFinalUrl, state.TabletLoadMs, state.TabletRedirectedOffsite, state.TabletError),
            mobile = Profile("mobile", state.MobileOk, state.MobileHttpStatus, state.MobileTitle, state.MobileFinalUrl, state.MobileLoadMs, state.MobileRedirectedOffsite, state.MobileError)
        }
    });
});

app.MapGet("/api/monitor/ssl", async (AppDbContext db, IOptions<VisualMonitorOptions> options, SslCheckService ssl, int? siteId, CancellationToken ct) =>
{
    var site = await db.MonitoredSites.AsNoTracking().SingleOrDefaultAsync(x => siteId.HasValue ? x.Id == siteId : x.SiteKey == options.Value.SiteKey, ct);
    if (site is null) return Results.NotFound();
    return Results.Ok(await ssl.CheckAsync(site.Url, ct));
});

app.MapPost("/api/monitor/scan", async (AppDbContext db, IOptions<VisualMonitorOptions> options, MonitorWorker worker, int? siteId) =>
{
    var site = await db.MonitoredSites.SingleOrDefaultAsync(x => siteId.HasValue ? x.Id == siteId : x.SiteKey == options.Value.SiteKey);
    if (site is null) return Results.NotFound();
    if (!site.Enabled || !worker.Enqueue(site.Id)) return Results.Conflict(new { message = "Website đã có trong hàng đợi hoặc đã tắt theo dõi." });
    return Results.Accepted($"/api/monitor?siteId={site.Id}", new { site.Id, activity = "queued" });
});

app.MapGet("/api/sites", async (AppDbContext db, MonitorWorker worker, SslCheckService ssl) =>
{
    var sites = await db.MonitoredSites.AsNoTracking().Include(x => x.State).OrderBy(x => x.Name).ToListAsync();
    return Results.Ok(sites.Select(x => new {
        x.Id, x.Name, x.Url, x.Enabled, x.IntervalSeconds,
        status = x.State?.OverallStatus ?? "pending",
        ssl = ssl.GetCached(x.Url),
        lastCheckedAtUtc = x.State?.LastCheckedAtUtc,
        lastError = x.State?.LastError,
        nextCheckAtUtc = x.Enabled ? worker.NextCheck(x.Id) : null,
        activity = worker.Activity(x.Id)
    }));
});

app.MapPost("/api/sites", async (AddSiteRequest request, AppDbContext db, MonitorWorker worker, IOptions<VisualMonitorOptions> options) =>
{
    var name = request.Name?.Trim() ?? "";
    var rawUrl = request.Url?.Trim() ?? "";
    if (!rawUrl.Contains("://")) rawUrl = "https://" + rawUrl;
    if (name.Length is < 1 or > 300 || rawUrl.Length > 2000 ||
        !Uri.TryCreate(rawUrl, UriKind.Absolute, out var uri) ||
        (uri.Scheme != "http" && uri.Scheme != "https") || string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        return Results.BadRequest(new { message = "Nhập tên (1–300 ký tự) và URL HTTP/HTTPS hợp lệ, không chứa thông tin đăng nhập." });
    var url = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    if (url.Length > 2000) return Results.BadRequest(new { message = "URL quá dài." });
    // Serialize duplicate checks across concurrent requests without changing the existing schema.
    await using var transaction = await db.Database.BeginTransactionAsync();
    await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(86241903)");
    if (await db.MonitoredSites.AnyAsync(x => x.Url == url || x.Url == url.TrimEnd('/')))
        return Results.Conflict(new { message = "Website này đã được theo dõi." });
    var site = new MonitoredSite {
        SiteKey = "site-" + Guid.NewGuid().ToString("N"), Name = name, Url = url,
        Enabled = true, IntervalSeconds = Math.Max(30, options.Value.IntervalSeconds), State = new MonitorState()
    };
    db.MonitoredSites.Add(site);
    await db.SaveChangesAsync();
    await transaction.CommitAsync();
    worker.Enqueue(site.Id);
    return Results.Created($"/api/monitor?siteId={site.Id}", new { site.Id, site.Name, site.Url, activity = "queued" });
});

app.MapPut("/api/sites/{id:int}", async (int id, UpdateSiteRequest request, AppDbContext db, MonitorWorker worker) =>
{
    var name = request.Name?.Trim() ?? "";
    var raw = request.Url?.Trim() ?? "";
    if (!raw.Contains("://")) raw = "https://" + raw;
    if (name.Length is < 1 or > 300 || raw.Length > 2000 || request.IntervalSeconds is < 30 or > 604800 ||
        !Uri.TryCreate(raw, UriKind.Absolute, out var uri) || (uri.Scheme != "http" && uri.Scheme != "https") ||
        string.IsNullOrEmpty(uri.Host) || !string.IsNullOrEmpty(uri.UserInfo))
        return Results.BadRequest(new { message = "Tên/URL không hợp lệ hoặc chu kỳ ngoài khoảng 30–604800 giây." });
    var url = new UriBuilder(uri) { Fragment = "" }.Uri.AbsoluteUri;
    if (url.Length > 2000) return Results.BadRequest(new { message = "URL quá dài." });
    if (!worker.TryBeginEdit(id)) return Results.Conflict(new { message = "Website đang chụp, chờ chụp hoặc đang được sửa. Vui lòng thử lại khi hoàn tất." });
    var changed = false;
    try
    {
        await using var transaction = await db.Database.BeginTransactionAsync();
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(86241903)");
        var site = await db.MonitoredSites.Include(x => x.State).SingleOrDefaultAsync(x => x.Id == id);
        if (site is null) return Results.NotFound(new { message = "Website không còn tồn tại." });
        if (await db.MonitoredSites.AnyAsync(x => x.Id != id && (x.Url == url || x.Url == url.TrimEnd('/'))))
            return Results.Conflict(new { message = "URL này đã được website khác theo dõi." });
        var urlChanged = site.Url != url;
        site.Name = name; site.Url = url; site.Enabled = request.Enabled; site.IntervalSeconds = request.IntervalSeconds;
        if (urlChanged && site.State is not null)
            db.Entry(site.State).CurrentValues.SetValues(new MonitorState { Id = site.State.Id, MonitoredSiteId = site.Id });
        await db.SaveChangesAsync();
        await transaction.CommitAsync();
        changed = true;
        return Results.Ok(new { site.Id, site.Name, site.Url });
    }
    finally { worker.EndEdit(id, changed); }
});

app.MapDelete("/api/sites/{id:int}", async (int id, AppDbContext db, MonitorWorker worker) =>
{
    if (!worker.TryBeginEdit(id)) return Results.Conflict(new { message = "Website đang chụp, chờ chụp hoặc đang được sửa. Vui lòng thử lại khi hoàn tất." });
    var deleted = false;
    try
    {
        var site = await db.MonitoredSites.SingleOrDefaultAsync(x => x.Id == id);
        if (site is null) return Results.NotFound(new { message = "Website không còn tồn tại." });
        db.MonitoredSites.Remove(site);
        await db.SaveChangesAsync();
        deleted = true;
        return Results.Ok(new { message = "Đã xóa website khỏi danh sách theo dõi. Ảnh đã lưu được giữ trên máy chủ." });
    }
    finally { worker.EndEdit(id, false, deleted); }
});

app.MapGet("/api/monitor/history", async (IWebHostEnvironment env, IOptions<VisualMonitorOptions> options, AppDbContext db, int? siteId) =>
{
    var o = options.Value;
    var root = Path.IsPathRooted(o.ScreenshotRoot)
        ? o.ScreenshotRoot
        : Path.Combine(env.ContentRootPath, o.ScreenshotRoot);
    var site = await db.MonitoredSites.AsNoTracking().SingleOrDefaultAsync(x => siteId.HasValue ? x.Id == siteId : x.SiteKey == o.SiteKey);
    if (site is null) return Results.NotFound();
    var historyRoot = Path.Combine(root, site.SiteKey, "history");

    if (!Directory.Exists(historyRoot))
        return Results.Ok(Array.Empty<object>());

    var items = Directory.EnumerateDirectories(historyRoot)
        .Select(path => new DirectoryInfo(path))
        .Where(x => x.Name.Length == 15 || File.Exists(Path.Combine(x.FullName, "result.json")))
        .OrderByDescending(x => x.Name)
        .Take(Math.Max(1, o.HistoryMaxItemsApi))
        .Select(x =>
        {
            var attempt = CaptureManifest.Read(x.FullName);
            string? Image(string device, bool full = false)
            {
                if (attempt is not null && (!attempt.Profiles.TryGetValue(device, out var profile) || !profile.HasImage)) return null;
                var file = device + (full ? "-full" : "") + ".webp";
                return File.Exists(Path.Combine(x.FullName, file)) ? $"/captures/{site.SiteKey}/history/{x.Name}/{file}" : null;
            }
            return new
            {
                key = x.Name,
                capturedAt = attempt is not null ? new DateTimeOffset(attempt.CheckedAtUtc).ToOffset(TimeSpan.FromHours(7)) : ParseHistoryTime(x.Name),
                status = attempt?.Status ?? "unknown",
                ssl = attempt?.Ssl,
                sourceUrl = attempt?.Url,
                profiles = attempt?.Profiles,
                desktop = Image("desktop"), tablet = Image("tablet"), mobile = Image("mobile"),
                desktopFull = Image("desktop", true), tabletFull = Image("tablet", true), mobileFull = Image("mobile", true)
            };
        }).ToArray();

    return Results.Ok(items);
});

app.MapSiteBuilder();


app.Run();

static DateTimeOffset? ParseHistoryTime(string value)
{
    return DateTime.TryParseExact(
        value,
        "yyyyMMdd_HHmmss",
        System.Globalization.CultureInfo.InvariantCulture,
        System.Globalization.DateTimeStyles.None,
        out var date)
        ? new DateTimeOffset(date, TimeSpan.FromHours(7))
        : null;
}

record AddSiteRequest(string? Name, string? Url);

record UpdateSiteRequest(string? Name, string? Url, int IntervalSeconds, bool Enabled);
