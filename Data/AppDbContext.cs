using Microsoft.EntityFrameworkCore;
using VisualMonitor.Web.Models;

namespace VisualMonitor.Web.Data;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<MonitoredSite> MonitoredSites => Set<MonitoredSite>();
    public DbSet<MonitorState> MonitorStates => Set<MonitorState>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<MonitoredSite>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.SiteKey).IsUnique();
            entity.Property(x => x.SiteKey).HasMaxLength(100).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(300).IsRequired();
            entity.Property(x => x.Url).HasMaxLength(2000).IsRequired();

            entity.HasOne(x => x.State)
                .WithOne(x => x.MonitoredSite)
                .HasForeignKey<MonitorState>(x => x.MonitoredSiteId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<MonitorState>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.MonitoredSiteId).IsUnique();
            entity.Property(x => x.OverallStatus).HasMaxLength(30).IsRequired();
            entity.Property(x => x.DesktopTitle).HasMaxLength(1000);
            entity.Property(x => x.TabletTitle).HasMaxLength(1000);
            entity.Property(x => x.MobileTitle).HasMaxLength(1000);
            entity.Property(x => x.DesktopFinalUrl).HasMaxLength(3000);
            entity.Property(x => x.TabletFinalUrl).HasMaxLength(3000);
            entity.Property(x => x.MobileFinalUrl).HasMaxLength(3000);
        });
    }
}
