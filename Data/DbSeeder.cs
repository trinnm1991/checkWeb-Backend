using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using VisualMonitor.Web.Models;
using VisualMonitor.Web.Options;

namespace VisualMonitor.Web.Data;

public static class DbSeeder
{
    public static async Task SeedAsync(IServiceProvider services)
    {
        using var scope = services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var options = scope.ServiceProvider.GetRequiredService<IOptions<VisualMonitorOptions>>().Value;

        // Configuration bootstraps a new database only; later changes belong to the UI.
        if (!await db.Database.EnsureCreatedAsync()) return;
        db.MonitoredSites.Add(new MonitoredSite
        {
            SiteKey = options.SiteKey, Name = options.Name, Url = NormalizeUrl(options.Url),
            Enabled = options.Enabled, IntervalSeconds = Math.Max(30, options.IntervalSeconds),
            State = new MonitorState()
        });

        await db.SaveChangesAsync();
    }

    private static string NormalizeUrl(string url)
    {
        var value = url.Trim();
        if (!value.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
            !value.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            value = "https://" + value;
        }
        return value;
    }
}
