using SkyLink.Core;

namespace SkyLink.Sim;

/// <summary>What the desktop app needs from a simulator beyond cockpit sync: setting up a flight.</summary>
public interface IFlightSim : ISimBridge
{
    bool IsConnected { get; }
    bool IsInFlight { get; }
    string? AircraftTitle { get; }
    FlightSample? LatestFlight { get; }
    string ModuleStatus { get; }
    bool IsFollowing { get; }

    /// <summary>Raised (on the sim thread) with every installed aircraft title and livery.</summary>
    event Action<List<(string Title, string Livery)>>? AircraftCatalog;

    bool RequestAircraftCatalog();
    bool LoadFlight(string fltPath);
    bool LoadFlightPlan(string plnPath);
    void SetCallsign(string? atcId, string? airline, string? flightNumber);
}
