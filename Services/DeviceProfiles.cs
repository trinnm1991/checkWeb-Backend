using VisualMonitor.Web.Models;

namespace VisualMonitor.Web.Services;

public static class DeviceProfiles
{
    public static readonly IReadOnlyList<DeviceProfile> All = new[]
    {
        new DeviceProfile(
            "desktop", "Desktop", 1920, 1080,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36",
            false, false, 1f),

        new DeviceProfile(
            "tablet", "Tablet", 768, 1024,
            "Mozilla/5.0 (Linux; Android 15; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Safari/537.36",
            true, true, 1f),

        new DeviceProfile(
            "mobile", "Mobile", 390, 844,
            "Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/151.0.0.0 Mobile Safari/537.36",
            true, true, 1f)
    };
}
