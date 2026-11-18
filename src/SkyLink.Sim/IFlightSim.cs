using SkyLink.Core;
using SkyLink.Definitions;

namespace SkyLink.Sim;

/// <summary>What the desktop app needs from a simulator beyond cockpit sync: setting up a flight.</summary>
public interface IFlightSim : ISimBridge
{
    bool IsConnected { get; }
    bool IsInFlight { get; }

    /// <summary>The .flt the sim last reported loading, if it said.</summary>
    string? LoadedFlight { get; }

    /// <summary>Increases whenever the sim reports flight or aircraft loading activity.</summary>
    int LoadActivity { get; }
    string? AircraftTitle { get; }
    FlightSample? LatestFlight { get; }
    string ModuleStatus { get; }
    bool IsFollowing { get; }

    /// <summary>Raised (on the sim thread) with every installed aircraft title and livery.</summary>
    event Action<List<(string Title, string Livery)>>? AircraftCatalog;

    /// <summary>The local pilot triggered a synced event (sim thread).</summary>
    event Action<VarBinding, double>? EventCaptured;

    /// <summary>The other pilot's event was replayed into this sim (sim thread).</summary>
    event Action<VarBinding, double>? EventReplayed;

    bool RequestAircraftCatalog();
    bool LoadFlight(string fltPath);
    bool LoadFlightPlan(string plnPath);
    void SetCallsign(string? atcId, string? airline, string? flightNumber);
}
