using System.Text.Json.Serialization;

namespace SkyLink.Lobby;

public enum Seat { None, Captain, FirstOfficer }

public enum PartyPhase { Lobby, Launching, InFlight }

/// <summary>One installed aircraft and the liveries installed for it, on one PC.</summary>
public sealed record CatalogAircraft(string Title, List<string> Liveries);

public sealed record MemberInfo(
    string Id,
    string Name,
    string Address,
    bool Connected,
    bool SimRunning,
    string? SimAircraft,
    int CatalogCount,
    string? LaunchStage,
    bool InCockpit);

/// <summary>The parts of a SimBrief OFP the flight is built from (plus the MSFS flight plan file).</summary>
public sealed record SimBriefPlan(
    string Origin,
    string Destination,
    string? OriginName,
    string? DestinationName,
    string? Route,
    int? CruiseAltitudeFt,
    string? AirlineIcao,
    string? FlightNumber,
    string? Callsign,
    string? Registration,
    string? AircraftIcao,
    double? OriginLat,
    double? OriginLon,
    int? OriginElevationFt,
    double? DestinationLat,
    double? DestinationLon,
    int? DestinationElevationFt,
    string? DepartureRunway,
    double? BlockFuel,
    string? WeightUnit,
    int? Passengers,
    string? PlnXml,
    string FetchedBy,
    DateTimeOffset FetchedAt);

public sealed record FlightSetup(
    string? AircraftTitle,
    string? Livery,
    string? Callsign,
    string? AirlineCallsign,
    string? FlightNumber,
    SimBriefPlan? Plan,
    string? DepartureIcao,
    string? EditedBy)
{
    public static readonly FlightSetup Empty = new(null, null, null, null, null, null, null, null);

    /// <summary>Departure airport: the SimBrief origin if a plan is loaded, else the manual one.</summary>
    public string? EffectiveDeparture => Plan?.Origin ?? DepartureIcao;
}

public sealed record PartyState(
    string PartyId,
    string Name,
    long Version,
    string HostId,
    List<MemberInfo> Members,
    string? CaptainId,
    string? FirstOfficerId,
    FlightSetup Setup,
    PartyPhase Phase,
    string? SessionCode,
    long CatalogVersion,
    StartPosition? LaunchStart = null)
{
    public MemberInfo? Member(string? id) => id is null ? null : Members.FirstOrDefault(m => m.Id == id);

    public Seat SeatOf(string id) => id == CaptainId ? Seat.Captain : id == FirstOfficerId ? Seat.FirstOfficer : Seat.None;

    public MemberInfo? Captain => Member(CaptainId);
    public MemberInfo? FirstOfficer => Member(FirstOfficerId);
}

// ---------------------------------------------------------------- requests from a member

[JsonPolymorphic(TypeDiscriminatorPropertyName = "k")]
[JsonDerivedType(typeof(TakeSeat), "seat")]
[JsonDerivedType(typeof(SwapSeats), "swap")]
[JsonDerivedType(typeof(SetAircraft), "aircraft")]
[JsonDerivedType(typeof(SetCallsign), "callsign")]
[JsonDerivedType(typeof(SetPlan), "plan")]
[JsonDerivedType(typeof(SetDeparture), "departure")]
[JsonDerivedType(typeof(UpdateStatus), "status")]
[JsonDerivedType(typeof(PublishCatalog), "catalog")]
[JsonDerivedType(typeof(StartFlight), "start")]
[JsonDerivedType(typeof(EndFlight), "end")]
[JsonDerivedType(typeof(Kick), "kick")]
[JsonDerivedType(typeof(RenameParty), "rename")]
public abstract record PartyIntent;

/// <summary>Move to a seat; if the other pilot sits there, the two swap.</summary>
public sealed record TakeSeat(Seat Seat) : PartyIntent;
public sealed record SwapSeats : PartyIntent;
public sealed record SetAircraft(string Title, string Livery) : PartyIntent;
public sealed record SetCallsign(string? Callsign, string? AirlineCallsign, string? FlightNumber) : PartyIntent;
public sealed record SetPlan(SimBriefPlan? Plan) : PartyIntent;
public sealed record SetDeparture(string? Icao) : PartyIntent;
public sealed record UpdateStatus(bool SimRunning, string? SimAircraft, bool InCockpit, string? LaunchStage) : PartyIntent;
public sealed record PublishCatalog(List<CatalogAircraft> Aircraft) : PartyIntent;
/// <summary>The Captain starts the flight from <paramref name="Start"/> (SimBrief origin or their own position).</summary>
public sealed record StartFlight(StartPosition Start) : PartyIntent;
public sealed record EndFlight : PartyIntent;
public sealed record Kick(string MemberId) : PartyIntent;
public sealed record RenameParty(string Name) : PartyIntent;
