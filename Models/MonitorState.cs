namespace VisualMonitor.Web.Models;

public sealed class MonitorState
{
    public int Id { get; set; }
    public int MonitoredSiteId { get; set; }
    public MonitoredSite MonitoredSite { get; set; } = null!;

    public DateTime? LastCheckedAtUtc { get; set; }
    public string OverallStatus { get; set; } = "pending";
    public string? LastError { get; set; }

    public int DesktopHttpStatus { get; set; }
    public string DesktopTitle { get; set; } = string.Empty;
    public string DesktopFinalUrl { get; set; } = string.Empty;
    public double DesktopLoadMs { get; set; }
    public bool DesktopOk { get; set; }
    public bool DesktopRedirectedOffsite { get; set; }
    public string? DesktopError { get; set; }

    public int TabletHttpStatus { get; set; }
    public string TabletTitle { get; set; } = string.Empty;
    public string TabletFinalUrl { get; set; } = string.Empty;
    public double TabletLoadMs { get; set; }
    public bool TabletOk { get; set; }
    public bool TabletRedirectedOffsite { get; set; }
    public string? TabletError { get; set; }

    public int MobileHttpStatus { get; set; }
    public string MobileTitle { get; set; } = string.Empty;
    public string MobileFinalUrl { get; set; } = string.Empty;
    public double MobileLoadMs { get; set; }
    public bool MobileOk { get; set; }
    public bool MobileRedirectedOffsite { get; set; }
    public string? MobileError { get; set; }
}
