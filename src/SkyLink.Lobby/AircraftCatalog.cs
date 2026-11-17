using System.Text.Json;

namespace SkyLink.Lobby;

/// <summary>
/// The installed-aircraft list for this PC, grouped by aircraft and cached on disk, so the party
/// knows what this PC can fly even while MSFS is closed.
/// </summary>
public static class AircraftCatalog
{
    public const string DefaultLivery = "Default";

    public static string CachePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyLink", "aircraft.json");

    public static List<CatalogAircraft> Group(IEnumerable<(string Title, string Livery)> entries) =>
        entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Title))
            .GroupBy(e => e.Title.Trim(), StringComparer.Ordinal)
            .Select(g => new CatalogAircraft(g.Key, g
                .Select(e => string.IsNullOrWhiteSpace(e.Livery) ? DefaultLivery : e.Livery.Trim())
                .Distinct(StringComparer.Ordinal)
                .Order(StringComparer.OrdinalIgnoreCase)
                .ToList()))
            .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public static List<CatalogAircraft>? LoadCache(string? path = null)
    {
        path ??= CachePath;
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<List<CatalogAircraft>>(File.ReadAllText(path), PartyNode.Json) : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static void SaveCache(List<CatalogAircraft> aircraft, string? path = null)
    {
        path ??= CachePath;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, JsonSerializer.Serialize(aircraft, PartyNode.Json));
    }
}
