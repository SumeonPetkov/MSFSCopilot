using System.IO;
using System.Text.Json;

namespace SkyLink.App;

/// <summary>Per-PC settings, in %LOCALAPPDATA%\SkyLink\settings.json (SKYLINK_SETTINGS overrides the path).</summary>
public sealed class AppSettings
{
    public string PlayerId { get; set; } = Guid.NewGuid().ToString("N")[..12];
    public string PlayerName { get; set; } = Environment.UserName;
    public string? SimBriefUser { get; set; }
    public List<string> ManualPeers { get; set; } = [];
    public string? SimConnectDll { get; set; }

    private static string PathFor() =>
        Environment.GetEnvironmentVariable("SKYLINK_SETTINGS") is { Length: > 0 } p ? p
            : System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyLink", "settings.json");

    public static AppSettings Load()
    {
        AppSettings s;
        try
        {
            s = File.Exists(PathFor()) ? JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(PathFor())) ?? new() : new();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            s = new();
        }
        if (Environment.GetEnvironmentVariable("SKYLINK_PLAYER") is { Length: > 0 } name) s.PlayerName = name;
        if (Environment.GetEnvironmentVariable("SKYLINK_ID") is { Length: > 0 } id) s.PlayerId = id;
        s.Save();
        return s;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(PathFor())!);
            File.WriteAllText(PathFor(), JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; never fail the app over them.
        }
    }

    public static int Port(string env, int fallback) =>
        int.TryParse(Environment.GetEnvironmentVariable(env), out int p) ? p : fallback;
}
