using System.Globalization;
using System.Security;
using System.Text;

namespace SkyLink.Lobby;

/// <summary>Where the flight starts: on the ground at a point, facing a heading.</summary>
public sealed record StartPosition(double LatDeg, double LonDeg, double AltitudeFt, double HeadingDeg, bool OnGround, string Description);

public sealed record WrittenFlight(string FltPath, string? PlnPath, StartPosition Start);

/// <summary>
/// Writes the MSFS flight (.flt) and flight plan (.pln) every pilot in the party loads, so all
/// seats start with the same aircraft, livery, callsign and route.
/// Note: the .flt layout follows the documented MSFS 2020 format, which MSFS 2024 still reads;
/// it is the part of the one-click start that must be confirmed in the live sim.
/// </summary>
public static class FlightFiles
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string FlightsFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyLink", "flights");

    /// <summary>
    /// The start point: the SimBrief origin airport if a plan is loaded, else where the Captain's
    /// aircraft is now. Null if neither is known.
    /// </summary>
    public static StartPosition? ChooseStart(FlightSetup setup, StartPosition? captainPosition)
    {
        if (setup.Plan is { OriginLat: { } lat, OriginLon: { } lon } p)
            return new StartPosition(lat, lon, p.OriginElevationFt ?? 0, RunwayHeading(p.DepartureRunway) ?? 0, true,
                $"{p.Origin}{(p.OriginName is { } n ? " " + n : "")}");
        return captainPosition;
    }

    /// <summary>"25C" faces 250°, "09" faces 90°.</summary>
    public static double? RunwayHeading(string? runway)
    {
        if (runway is null) return null;
        string digits = new(runway.TakeWhile(char.IsDigit).ToArray());
        return int.TryParse(digits, out int n) && n is >= 1 and <= 36 ? n * 10 % 360 : null;
    }

    public static WrittenFlight Write(string partyId, FlightSetup setup, StartPosition start, string? folder = null)
    {
        if (setup.AircraftTitle is null) throw new ArgumentException("no aircraft chosen");
        folder ??= Path.Combine(FlightsFolder, Sanitize(partyId));
        Directory.CreateDirectory(folder);

        string? plnPath = null;
        string? pln = setup.Plan is { } plan ? plan.PlnXml ?? DirectPln(plan) : null;
        if (pln is not null)
        {
            plnPath = Path.Combine(folder, "SkyLink.pln");
            File.WriteAllText(plnPath, pln, new UTF8Encoding(false));
        }

        string fltPath = Path.Combine(folder, "SkyLink.flt");
        File.WriteAllText(fltPath, Flt(setup, start, plnPath), new UTF8Encoding(true));
        return new WrittenFlight(fltPath, plnPath, start);
    }

    public static string Flt(FlightSetup s, StartPosition start, string? plnPath)
    {
        var b = new StringBuilder();
        void Section(string name) => b.Append('[').Append(name).AppendLine("]");
        void Kv(string k, object? v) => b.Append(k).Append('=').AppendLine(Convert.ToString(v, Inv));

        string title = s.Plan is { } p ? $"SkyLink {p.Origin}-{p.Destination}" : "SkyLink shared flight";
        Section("Main");
        Kv("Title", title);
        Kv("Description", "Shared-cockpit flight prepared by SkyLink");
        Kv("MissionType", "FreeFlight");
        Kv("FlightVersion", 1);
        b.AppendLine();

        Section("Sim.0");
        Kv("Sim", s.AircraftTitle);
        if (s.Livery is { } lv) Kv("Livery", lv);
        b.AppendLine();

        Section("SimVars.0");
        Kv("Latitude", Dms(start.LatDeg, 'N', 'S'));
        Kv("Longitude", Dms(start.LonDeg, 'E', 'W'));
        Kv("Altitude", start.AltitudeFt.ToString("+000000.00;-000000.00", Inv));
        Kv("Pitch", 0);
        Kv("Bank", 0);
        Kv("Heading", start.HeadingDeg.ToString("0.0", Inv));
        Kv("SimOnGround", start.OnGround ? "True" : "False");
        Kv("OnGround", start.OnGround ? "True" : "False");
        b.AppendLine();

        Section("ATC_Aircraft.0");
        Kv("ActiveFlightPlan", plnPath is null ? "False" : "True");
        Kv("RequestedFlightPlan", "False");
        if (s.Callsign is { } cs) Kv("TailNumber", cs);
        if (s.AirlineCallsign is { } al) Kv("AirlineCallSign", al);
        if (s.FlightNumber is { } fn) Kv("FlightNumber", fn);
        b.AppendLine();

        if (plnPath is not null)
        {
            Section("ATC_ActiveFlightPlan.0");
            Kv("path", plnPath);
            b.AppendLine();
            Section("GPS_Engine.1");
            Kv("Filename", plnPath);
            b.AppendLine();
        }
        if (s.Plan is { } plan)
        {
            Section("Departure");
            Kv("ICAO", plan.Origin);
            if (plan.DepartureRunway is { } rw) Kv("RunwayNumber", new string(rw.TakeWhile(char.IsDigit).ToArray()));
            b.AppendLine();
            Section("Arrival");
            Kv("ICAO", plan.Destination);
            b.AppendLine();
        }
        return b.ToString();
    }

    /// <summary>A direct origin-to-destination MSFS flight plan, when SimBrief did not provide one.</summary>
    public static string DirectPln(SimBriefPlan p)
    {
        string Lla(double? lat, double? lon, int? elevFt) =>
            lat is null || lon is null ? "" : $"{Dms(lat.Value, 'N', 'S')},{Dms(lon.Value, 'E', 'W')},{(elevFt ?? 0).ToString("+000000.00;-000000.00", Inv)}";
        string X(string? s) => SecurityElement.Escape(s ?? "");
        int alt = p.CruiseAltitudeFt ?? 10000;
        return $"""
            <?xml version="1.0" encoding="UTF-8"?>
            <SimBase.Document Type="AceXML" version="1,0">
                <Descr>AceXML Document</Descr>
                <FlightPlan.FlightPlan>
                    <Title>{X(p.Origin)} to {X(p.Destination)}</Title>
                    <FPType>IFR</FPType>
                    <RouteType>HighAlt</RouteType>
                    <CruisingAlt>{alt}</CruisingAlt>
                    <DepartureID>{X(p.Origin)}</DepartureID>
                    <DepartureLLA>{Lla(p.OriginLat, p.OriginLon, p.OriginElevationFt)}</DepartureLLA>
                    <DestinationID>{X(p.Destination)}</DestinationID>
                    <DestinationLLA>{Lla(p.DestinationLat, p.DestinationLon, p.DestinationElevationFt)}</DestinationLLA>
                    <Descr>{X(p.Origin)}, {X(p.Destination)}</Descr>
                    <DeparturePosition>{X(p.DepartureRunway)}</DeparturePosition>
                    <DepartureName>{X(p.OriginName)}</DepartureName>
                    <DestinationName>{X(p.DestinationName)}</DestinationName>
                    <ATCWaypoint id="{X(p.Origin)}">
                        <ATCWaypointType>Airport</ATCWaypointType>
                        <WorldPosition>{Lla(p.OriginLat, p.OriginLon, p.OriginElevationFt)}</WorldPosition>
                        <ICAO><ICAOIdent>{X(p.Origin)}</ICAOIdent></ICAO>
                    </ATCWaypoint>
                    <ATCWaypoint id="{X(p.Destination)}">
                        <ATCWaypointType>Airport</ATCWaypointType>
                        <WorldPosition>{Lla(p.DestinationLat, p.DestinationLon, p.DestinationElevationFt)}</WorldPosition>
                        <ICAO><ICAOIdent>{X(p.Destination)}</ICAOIdent></ICAO>
                    </ATCWaypoint>
                </FlightPlan.FlightPlan>
            </SimBase.Document>
            """;
    }

    /// <summary>MSFS degree-minute-second format, e.g. <c>N50° 1' 59.88"</c>.</summary>
    public static string Dms(double deg, char pos, char neg)
    {
        char h = deg < 0 ? neg : pos;
        double a = Math.Abs(deg);
        int d = (int)a;
        double minutesF = (a - d) * 60;
        int m = (int)minutesF;
        double sec = Math.Round((minutesF - m) * 60, 2);
        if (sec >= 60) { sec = 0; m++; }
        if (m >= 60) { m = 0; d++; }
        return $"{h}{d}° {m}' {sec.ToString("0.00", Inv)}\"";
    }

    private static string Sanitize(string s) => new(s.Where(c => char.IsLetterOrDigit(c) || c is '-' or '_').ToArray());
}
