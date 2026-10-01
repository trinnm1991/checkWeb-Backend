namespace VisualMonitor.Web.Options;

public sealed class VisualMonitorOptions
{
    public string SiteKey { get; set; } = "site";
    public string Name { get; set; } = "Website";
    public string Url { get; set; } = "https://example.com";
    public bool Enabled { get; set; } = true;
    public int IntervalSeconds { get; set; } = 300;
    public bool CaptureOnStartup { get; set; } = true;
    public int NavigationTimeoutMs { get; set; } = 60000;
    public int RenderWaitMs { get; set; } = 3500;
    public string ScreenshotRoot { get; set; } = "App_Data/captures";
    public bool SaveHistory { get; set; } = true;
    public int HistoryRetentionDays { get; set; } = 3;
    public int HistoryMaxItemsApi { get; set; } = 30;
}
