namespace VisualMonitor.Web.Models;

public sealed class MonitoredSite
{
    public int Id { get; set; }
    public string SiteKey { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 300;
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public MonitorState? State { get; set; }
}
