using System.Text.Json;

namespace SkyLink.Lobby;

/// <summary>
/// The installed-aircraft list for this PC, grouped by aircraft and cached on disk, so the party
/// knows what this PC can fly even while MSFS is closed.
/// </summary>
public static class AircraftCatalog
{
    public const string DefaultLivery = "Default";

    /// <summary>%LOCALAPPDATA%\SkyLink\aircraft.json; SKYLINK_AIRCRAFT_CACHE overrides it (several copies on one PC).</summary>
    public static string CachePath =>
        Environment.GetEnvironmentVariable("SKYLINK_AIRCRAFT_CACHE") is { Length: > 0 } p ? p
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyLink", "aircraft.json");

    /// <summary>
    /// Group MSFS's (title, livery) list by aircraft. AI-only "passive" aircraft are left out: they
    /// are listed by MSFS but cannot be flown.
    /// </summary>
    public static List<CatalogAircraft> Group(IEnumerable<(string Title, string Livery)> entries) =>
        entries
            .Where(e => !string.IsNullOrWhiteSpace(e.Title) && !AircraftNames.IsPassive(e.Title))
            .GroupBy(e => e.Title.Trim(), StringComparer.Ordinal)
            .Select(g => new CatalogAircraft(g.Key, Liveries(g.Select(e => e.Livery))))
            .OrderBy(a => a.Title, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// An aircraft's liveries. MSFS lists the aircraft once with no livery name (stored as
    /// <see cref="DefaultLivery"/>); when it also lists a real default livery ("DEFAULT"), the nameless
    /// entry is dropped so the list doesn't show "Default" twice.
    /// </summary>
    private static List<string> Liveries(IEnumerable<string> names)
    {
        var list = names
            .Select(l => string.IsNullOrWhiteSpace(l) ? DefaultLivery : l.Trim())
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (list.Contains(DefaultLivery) && list.Any(l => l != DefaultLivery && AircraftNames.LiveryDisplay(l).Equals(DefaultLivery, StringComparison.OrdinalIgnoreCase)))
            list.Remove(DefaultLivery);
        return list;
    }

    /// <summary>Shown order: by developer (unknown last), then by name.</summary>
    public static IEnumerable<CatalogAircraft> ForDisplay(IEnumerable<CatalogAircraft> aircraft) =>
        aircraft
            .OrderBy(a => a.Developer is null ? 1 : 0)
            .ThenBy(a => a.Developer, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(a => a.Title, StringComparer.Ordinal);

    /// <summary>Read the cache. Files written before names/developers existed load with them empty.</summary>
    public static List<CatalogAircraft>? LoadCache(string? path = null)
    {
        path ??= CachePath;
        try
        {
            var list = File.Exists(path) ? JsonSerializer.Deserialize<List<CatalogAircraft>>(File.ReadAllText(path), PartyNode.Json) : null;
            return list?.Where(a => a is { Title.Length: > 0, Liveries: not null } && !AircraftNames.IsPassive(a.Title)).ToList();
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
