namespace VisualMonitor.Web.Models;

public sealed record DeviceProfile(
    string Key,
    string Label,
    int Width,
    int Height,
    string UserAgent,
    bool IsMobile,
    bool HasTouch,
    float DeviceScaleFactor = 1f);

public sealed class ProfileCaptureResult
{
    public string Key { get; init; } = string.Empty;
    public string Label { get; init; } = string.Empty;
    public int Width { get; init; }
    public int Height { get; init; }
    public string? FailureKind { get; set; }
    public bool HasImage { get; set; }
    public bool Partial { get; set; }
    public string? CaptureError { get; set; }
    public bool Ok { get; set; }
    public int HttpStatus { get; set; }
    public string Title { get; set; } = string.Empty;
    public string FinalUrl { get; set; } = string.Empty;
    public double LoadMs { get; set; }
    public bool RedirectedOffsite { get; set; }
    public string? Error { get; set; }
    public string CurrentImageUrl { get; set; } = string.Empty;
    public string CurrentFullImageUrl { get; set; } = string.Empty;
    public string PreviousImageUrl { get; set; } = string.Empty;
    public string PreviousFullImageUrl { get; set; } = string.Empty;
}

public sealed class SiteCaptureResult
{
    public VisualMonitor.Web.Services.SslCheckResult? Ssl { get; set; }
    public int SiteId { get; init; }
    public string SiteKey { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Url { get; init; } = string.Empty;
    public DateTime CheckedAtUtc { get; init; }
    public string Status { get; set; } = "pending";
    public double ElapsedMs { get; set; }
    public Dictionary<string, ProfileCaptureResult> Profiles { get; init; } = new();
}
