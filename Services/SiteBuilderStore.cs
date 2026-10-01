using System.Text.Json;
using System.Text.RegularExpressions;
using VisualMonitor.Web.Models;

namespace VisualMonitor.Web.Services;

public sealed class BuilderConflictException(string message) : Exception(message);
public sealed class SiteBuilderStore(IWebHostEnvironment environment, IConfiguration configuration)
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly string _root = Path.GetFullPath(configuration["SiteBuilder:Root"] ?? Path.Combine(environment.ContentRootPath, "App_Data", "site-builder"));
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    private static readonly HashSet<string> Types = ["hero", "links", "cards", "stats", "list", "news", "contact"];

    public static string NormalizeDomain(string? domain)
    {
        var raw = domain?.Trim() ?? "";
        if (raw.Length is < 1 or > 253 || raw.Contains('/') || raw.Contains(':') || raw.Contains('@') || raw.Any(char.IsWhiteSpace))
            throw new ArgumentException("Nhập tên miền, không kèm giao thức, cổng hoặc đường dẫn.");
        if (!Uri.TryCreate("https://" + raw, UriKind.Absolute, out var uri) || uri.HostNameType != UriHostNameType.Dns)
            throw new ArgumentException("Tên miền không hợp lệ.");
        var host = uri.IdnHost.TrimEnd('.').ToLowerInvariant();
        if (!host.Contains('.') || host.Split('.').Any(label => !Regex.IsMatch(label, @"^[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?$")))
            throw new ArgumentException("Tên miền không hợp lệ.");
        return host;
    }

    public static void Validate(BuilderConfiguration config)
    {
        static void Text(string? value, int max) { if (value is null || value.Length > max) throw new ArgumentException("Nội dung cấu hình không hợp lệ hoặc quá dài."); }
        static void Id(string? value) { if (value is null || !Regex.IsMatch(value, "^[a-zA-Z][a-zA-Z0-9_-]{0,79}$")) throw new ArgumentException("ID không hợp lệ."); }
        static void Url(string? value)
        {
            Text(value, 2000);
            if (value == "") return;
            if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || !new[] { "https", "http", "mailto", "tel" }.Contains(uri.Scheme) || ((uri.Scheme == "https" || uri.Scheme == "http") && !string.IsNullOrEmpty(uri.UserInfo)))
                throw new ArgumentException("Liên kết không hợp lệ; chỉ hỗ trợ HTTP(S), email, điện thoại.");
        }
        if (config is null || config.Version != 1 || config.Modules is null || config.Modules.Count > 30 ||
            !new[] { "glass", "administration", "party" }.Contains(config.Theme)) throw new ArgumentException("Phiên bản, giao diện hoặc số lượng module không hợp lệ.");
        Id(config.Id); Text(config.Name, 150); Text(config.ShortName, 20); Text(config.Subtitle, 150); Url(config.Source); Text(config.CheckedAt, 30);
        if (string.IsNullOrWhiteSpace(config.Name)) throw new ArgumentException("Tên website không được để trống.");
        var ids = new HashSet<string>();
        foreach (var module in config.Modules)
        {
            if (module is null || !Types.Contains(module.Type) || module.Items is null || module.Items.Count > 20) throw new ArgumentException("Module không hợp lệ.");
            Id(module.Id); if (!ids.Add(module.Id)) throw new ArgumentException("ID module trùng nhau.");
            Text(module.Title, 200); Text(module.Text, 2000); Url(module.Source);
            foreach (var item in module.Items)
            {
                if (item is null) throw new ArgumentException("Mục nội dung không hợp lệ.");
                Text(item.Title, 200); Text(item.Text, 2000); Url(item.Url);
            }
        }
        if (JsonSerializer.SerializeToUtf8Bytes(config, Json).Length > 500000) throw new ArgumentException("Cấu hình vượt quá 500 KB.");
    }

    private async Task<List<BuilderSite>> ReadAsync()
    {
        var file = Path.Combine(_root, "sites.json");
        if (!File.Exists(file))
        {
            // Seed only when the store has never existed, never recreate deliberately deleted domains.
            if (File.Exists(Path.Combine(_root, "sites.previous.json"))) throw new IOException("Thiếu dữ liệu chính; cần kiểm tra bản sao lưu.");
            var list = new List<BuilderSite>();
            foreach (var (preset, domain) in new[] { ("tvu", "www.tvu.edu.vn"), ("hr", "nhansu.tvu.edu.vn"), ("party", "danguy.tvu.edu.vn") })
            {
                using var stream = typeof(SiteBuilderStore).Assembly.GetManifestResourceStream($"BuilderPresets.{preset}.json")
                    ?? throw new IOException($"Missing embedded builder preset: {preset}");
                var config = (await JsonSerializer.DeserializeAsync<BuilderConfiguration>(stream, Json))!;
                Validate(config);
                list.Add(new(config.Id, domain, 1, DateTime.UtcNow, config));
            }
            await WriteAsync(list);
            return list;
        }
        try
        {
            var list = JsonSerializer.Deserialize<List<BuilderSite>>(await File.ReadAllTextAsync(file), Json) ?? throw new JsonException();
            if (list.Count > 100 || list.Select(x => x.Domain).Distinct().Count() != list.Count || list.Select(x => x.Id).Distinct().Count() != list.Count) throw new JsonException();
            foreach (var site in list) { Validate(site.Config); if (site.Config.Id != site.Id || site.Revision < 1 || NormalizeDomain(site.Domain) != site.Domain) throw new JsonException(); }
            return list;
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or NullReferenceException)
        { throw new IOException("Dữ liệu quản lý miền không hợp lệ; không tự ghi đè.", ex); }
    }

    private async Task WriteAsync(List<BuilderSite> sites)
    {
        Directory.CreateDirectory(_root);
        var target = Path.Combine(_root, "sites.json");
        var temp = target + ".tmp";
        await using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
        {
            await JsonSerializer.SerializeAsync(stream, sites, Json);
            stream.Flush(true);
        }
        if (File.Exists(target))
        {
            var previous = Path.Combine(_root, "sites.previous.json");
            File.Copy(target, previous + ".tmp", true);
            File.Move(previous + ".tmp", previous, true);
        }
        File.Move(temp, target, true);
    }

    public async Task<List<BuilderSite>> ListAsync()
    {
        await _gate.WaitAsync(); try { return await ReadAsync(); } finally { _gate.Release(); }
    }
    public async Task<BuilderSite> SaveAsync(string? id, BuilderWriteRequest request)
    {
        if (request is null) throw new ArgumentException("Thiếu dữ liệu website.");
        Validate(request.Config); var domain = NormalizeDomain(request.Domain);
        await _gate.WaitAsync();
        try
        {
            var sites = await ReadAsync(); var old = id is null ? null : sites.SingleOrDefault(x => x.Id == id);
            if (id is not null && old is null) throw new KeyNotFoundException();
            if (old is not null && old.Revision != request.Revision) throw new BuilderConflictException("Bố cục đã được cập nhật ở phiên khác. Xuất JSON để giữ bản đang sửa, rồi tải lại dữ liệu server.");
            if (sites.Any(x => x.Id != id && x.Domain == domain)) throw new BuilderConflictException("Tên miền đã có trong danh sách.");
            if (id is null && sites.Count >= 100) throw new ArgumentException("Tối đa 100 miền.");
            var assignedId = id ?? "site-" + Guid.NewGuid().ToString("N");
            var result = new BuilderSite(assignedId, domain, (old?.Revision ?? 0) + 1, DateTime.UtcNow, request.Config with { Id = assignedId });
            if (old is not null) sites.Remove(old);
            sites.Add(result); await WriteAsync(sites); return result;
        }
        finally { _gate.Release(); }
    }
    public async Task DeleteAsync(string id, long revision)
    {
        await _gate.WaitAsync();
        try
        {
            var sites = await ReadAsync(); var site = sites.SingleOrDefault(x => x.Id == id) ?? throw new KeyNotFoundException();
            if (site.Revision != revision) throw new BuilderConflictException("Dữ liệu đã thay đổi. Tải lại trước khi xóa.");
            sites.Remove(site); await WriteAsync(sites);
        }
        finally { _gate.Release(); }
    }
}
