using System.Text.Json;
using VisualMonitor.Web.Models;

namespace VisualMonitor.Web.Services;

// Written last: a manifest marks a complete attempt, including attempts without images.
public static class CaptureManifest
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public static void Write(string directory, SiteCaptureResult result)
    {
        Directory.CreateDirectory(directory);
        var target = Path.Combine(directory, "result.json");
        var temporary = target + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(result, Json));
        File.Move(temporary, target, true);
    }

    public static SiteCaptureResult? Read(string directory)
    {
        try { return JsonSerializer.Deserialize<SiteCaptureResult>(File.ReadAllText(Path.Combine(directory, "result.json")), Json); }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (JsonException) { return null; }
    }
}
