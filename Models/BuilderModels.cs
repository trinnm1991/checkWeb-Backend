namespace VisualMonitor.Web.Models;

public sealed record BuilderItem(string Title, string Text, string Url);
public sealed record BuilderModule(string Id, string Type, string Title, string Text, bool Enabled, List<BuilderItem> Items, string Source = "");
public sealed record BuilderConfiguration(int Version, string Id, string Name, string ShortName, string Subtitle,
    string Source, string CheckedAt, List<BuilderModule> Modules, string Theme = "glass");
public sealed record BuilderSite(string Id, string Domain, long Revision, DateTime UpdatedAtUtc, BuilderConfiguration Config);
public sealed record BuilderWriteRequest(string Domain, BuilderConfiguration Config, long Revision = 0);
