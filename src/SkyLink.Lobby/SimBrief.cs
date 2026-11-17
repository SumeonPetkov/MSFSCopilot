using System.Globalization;
using System.Text.Json;

namespace SkyLink.Lobby;

/// <summary>
/// Imports the pilot's latest SimBrief OFP through SimBrief's public fetcher
/// (<c>xml.fetcher.php?username=...&amp;json=1</c>), plus the MSFS flight plan (.pln) SimBrief
/// generates for it.
/// </summary>
public static class SimBrief
{
    public static Uri LatestOfpUri(string user)
    {
        user = user.Trim();
        string key = user.All(char.IsDigit) ? "userid" : "username";
        return new Uri($"https://www.simbrief.com/api/xml.fetcher.php?{key}={Uri.EscapeDataString(user)}&json=1");
    }

    public static async Task<SimBriefPlan> FetchLatestAsync(HttpClient http, string user, string fetchedBy, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(user)) throw new ArgumentException("enter your SimBrief username or pilot ID");
        using var resp = await http.GetAsync(LatestOfpUri(user), ct);
        string json = await resp.Content.ReadAsStringAsync(ct);
        var (plan, plnUrl) = Parse(json, fetchedBy, DateTimeOffset.UtcNow);
        if (plnUrl is null) return plan;
        try
        {
            string pln = await http.GetStringAsync(plnUrl, ct);
            return pln.Contains("<FlightPlan.FlightPlan", StringComparison.Ordinal) ? plan with { PlnXml = pln } : plan;
        }
        catch (HttpRequestException)
        {
            return plan; // the route is still in the plan; SkyLink writes a direct .pln instead
        }
    }

    /// <summary>Parse an OFP (SimBrief's JSON) into a plan, and find its MSFS .pln download.</summary>
    public static (SimBriefPlan Plan, Uri? PlnUrl) Parse(string json, string fetchedBy, DateTimeOffset now)
    {
        using var doc = JsonDocument.Parse(json);
        var r = doc.RootElement;
        if (r.TryGetProperty("fetch", out var fetch) && Str(fetch, "status") is { } status && !status.Equals("Success", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException($"SimBrief: {status}");
        if (!r.TryGetProperty("origin", out var origin) || !r.TryGetProperty("destination", out var dest))
            throw new InvalidOperationException("SimBrief returned no flight plan (has this pilot generated an OFP?)");

        var general = Obj(r, "general");
        var atc = Obj(r, "atc");
        var aircraft = Obj(r, "aircraft");
        var fuel = Obj(r, "fuel");
        var weights = Obj(r, "weights");
        var units = Obj(r, "params");

        var plan = new SimBriefPlan(
            Origin: Str(origin, "icao_code") ?? throw new InvalidOperationException("SimBrief plan has no origin"),
            Destination: Str(dest, "icao_code") ?? throw new InvalidOperationException("SimBrief plan has no destination"),
            OriginName: Str(origin, "name"),
            DestinationName: Str(dest, "name"),
            Route: Str(general, "route"),
            CruiseAltitudeFt: Int(general, "initial_altitude"),
            AirlineIcao: Str(general, "icao_airline"),
            FlightNumber: Str(general, "flight_number"),
            Callsign: Str(atc, "callsign"),
            Registration: Str(aircraft, "reg"),
            AircraftIcao: Str(aircraft, "icaocode"),
            OriginLat: Num(origin, "pos_lat"),
            OriginLon: Num(origin, "pos_long"),
            OriginElevationFt: Int(origin, "elevation"),
            DestinationLat: Num(dest, "pos_lat"),
            DestinationLon: Num(dest, "pos_long"),
            DestinationElevationFt: Int(dest, "elevation"),
            DepartureRunway: Str(origin, "plan_rwy"),
            BlockFuel: Num(fuel, "plan_ramp"),
            WeightUnit: Str(units, "units"),
            Passengers: Int(weights, "pax_count") ?? Int(general, "passengers"),
            PlnXml: null,
            FetchedBy: fetchedBy,
            FetchedAt: now);

        Uri? pln = null;
        if (r.TryGetProperty("fms_downloads", out var dl) && Str(dl, "directory") is { } dir)
            foreach (var key in new[] { "mfs24", "msfs2024", "mfs" })
                if (dl.TryGetProperty(key, out var f) && Str(f, "link") is { Length: > 0 } link)
                {
                    pln = new Uri(new Uri(dir.EndsWith('/') ? dir : dir + "/"), link);
                    break;
                }
        return (plan, pln);
    }

    private static JsonElement Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : default;

    private static string? Str(JsonElement e, string name)
    {
        if (e.ValueKind != JsonValueKind.Object || !e.TryGetProperty(name, out var v)) return null;
        string? s = v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            _ => null,
        };
        return string.IsNullOrWhiteSpace(s) ? null : s.Trim();
    }

    private static double? Num(JsonElement e, string name) =>
        Str(e, name) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double d) ? d : null;

    private static int? Int(JsonElement e, string name) => Num(e, name) is { } d ? (int)Math.Round(d) : null;
}
