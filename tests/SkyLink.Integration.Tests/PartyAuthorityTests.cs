using SkyLink.Lobby;

namespace SkyLink.Integration.Tests;

public sealed class PartyAuthorityTests
{
    private static readonly List<CatalogAircraft> HostCatalog =
    [
        new("A320neo V2", ["Default", "iniBuilds House", "Lufthansa"]),
        new("Cessna 172 Skyhawk G1000", ["White", "Blue"]),
        new("Only On Host", ["X"]),
    ];

    private static readonly List<CatalogAircraft> GuestCatalog =
    [
        new("A320neo V2", ["Default", "Lufthansa", "Only On Guest"]),
        new("Cessna 172 Skyhawk G1000", ["Blue"]),
    ];

    private static SimBriefPlan Plan() => new("EDDF", "LFPG", "Frankfurt", "Paris CDG", "ANEKI Y163 ...", 36000, "DLH", "1234",
        "DLH1234", "D-AINA", "A20N", 50.0333, 8.5706, 364, 49.0097, 2.5479, 392, "25C", 6500, "kgs", 150, "<SimBase.Document/>", "Simeon", DateTimeOffset.UnixEpoch);

    private static readonly StartPosition Start = new(50.0333, 8.5706, 364, 250, true, "EDDF");

    private static PartyAuthority TwoPilots()
    {
        var p = new PartyAuthority("host", "Simeon", "Saturday flight");
        Assert.Equal(JoinOutcome.Accepted, p.Join("guest", "Alex", "100.64.0.2"));
        p.Apply("host", new PublishCatalog(HostCatalog));
        p.Apply("guest", new PublishCatalog(GuestCatalog));
        return p;
    }

    [Fact]
    public void Host_is_captain_and_the_joiner_takes_the_free_seat()
    {
        var p = new PartyAuthority("host", "Simeon", "Saturday flight");
        Assert.Equal("host", p.State.CaptainId);
        Assert.Null(p.State.FirstOfficerId);
        p.Join("guest", "Alex", "100.64.0.2");
        Assert.Equal("guest", p.State.FirstOfficerId);
    }

    [Fact]
    public void A_party_holds_exactly_two_players()
    {
        var p = TwoPilots();
        Assert.Equal(JoinOutcome.Full, p.Join("third", "Sam", "100.64.0.3"));
        Assert.Equal(2, p.State.Members.Count);
    }

    [Fact]
    public void Either_pilot_can_swap_or_take_a_seat_and_every_change_bumps_the_version()
    {
        var p = TwoPilots();
        long v = p.State.Version;
        Assert.Null(p.Apply("guest", new TakeSeat(Seat.Captain)));
        Assert.Equal(("guest", "host"), (p.State.CaptainId, p.State.FirstOfficerId));
        Assert.Null(p.Apply("host", new SwapSeats()));
        Assert.Equal(("host", "guest"), (p.State.CaptainId, p.State.FirstOfficerId));
        Assert.True(p.State.Version > v);
    }

    [Fact]
    public void Seats_can_change_during_a_flight_but_the_setup_cannot()
    {
        var p = ReadyToStart();
        Assert.Null(p.Apply("host", new StartFlight(Start)));
        Assert.Equal(PartyPhase.Launching, p.State.Phase);
        Assert.Null(p.Apply("guest", new SwapSeats()));
        Assert.Equal("guest", p.State.CaptainId);
        Assert.Contains("locked", p.Apply("host", new SetCallsign("X", null, null)));
    }

    [Fact]
    public void Only_aircraft_and_liveries_installed_on_both_pcs_are_offered_and_accepted()
    {
        var p = TwoPilots();
        Assert.Equal(["A320neo V2", "Cessna 172 Skyhawk G1000"], p.SharedCatalog.Select(c => c.Title));
        Assert.Equal(["Default", "Lufthansa"], p.SharedCatalog[0].Liveries);
        Assert.Equal(["Blue"], p.SharedCatalog[1].Liveries);
        Assert.Contains("not installed on both", p.Apply("host", new SetAircraft("Only On Host", "X")));
        Assert.Contains("livery", p.Apply("guest", new SetAircraft("A320neo V2", "Only On Guest")));
        Assert.Null(p.Apply("guest", new SetAircraft("A320neo V2", "Lufthansa")));
        Assert.Equal("guest", p.State.Setup.EditedBy);
    }

    [Fact]
    public void Both_pilots_edit_the_setup_and_a_plan_fills_in_the_callsign()
    {
        var p = TwoPilots();
        Assert.Null(p.Apply("guest", new SetPlan(Plan())));
        Assert.Equal("DLH1234", p.State.Setup.Callsign);
        Assert.Equal("EDDF", p.State.Setup.EffectiveDeparture);
        Assert.Null(p.Apply("host", new SetCallsign("DLH9", "LUFTHANSA", "9")));
        Assert.Equal(("DLH9", "LUFTHANSA", "9"), (p.State.Setup.Callsign, p.State.Setup.AirlineCallsign, p.State.Setup.FlightNumber));
    }

    private static PartyAuthority ReadyToStart()
    {
        var p = TwoPilots();
        p.Apply("host", new SetAircraft("A320neo V2", "Lufthansa"));
        p.Apply("host", new SetPlan(Plan()));
        return p;
    }

    [Fact]
    public void Start_needs_two_connected_pilots_a_shared_aircraft_and_a_departure()
    {
        var p = TwoPilots();
        Assert.Contains("aircraft", p.CanStart());
        p.Apply("host", new SetAircraft("A320neo V2", "Default"));
        Assert.Null(p.CanStart()); // no plan: the flight starts where the Captain's aircraft is

        p.Disconnected("guest");
        Assert.Contains("offline", p.CanStart());
        p.Join("guest", "Alex", "100.64.0.2"); // back after a blip, same seat
        Assert.Equal("guest", p.State.FirstOfficerId);
        Assert.Null(p.CanStart());

        Assert.Contains("only the Captain", p.Apply("guest", new StartFlight(Start)));
        Assert.Null(p.Apply("host", new StartFlight(Start)));
        Assert.NotNull(p.State.SessionCode);
    }

    [Fact]
    public void The_flight_is_on_once_both_pilots_report_being_in_the_cockpit()
    {
        var p = ReadyToStart();
        p.Apply("host", new StartFlight(Start));
        p.Apply("host", new UpdateStatus(true, "A320neo V2", true, "in cockpit"));
        Assert.Equal(PartyPhase.Launching, p.State.Phase);
        p.Apply("guest", new UpdateStatus(true, "A320neo V2", true, "in cockpit"));
        Assert.Equal(PartyPhase.InFlight, p.State.Phase);
        Assert.Null(p.Apply("guest", new EndFlight()));
        Assert.Equal(PartyPhase.Lobby, p.State.Phase);
        Assert.Null(p.State.SessionCode);
    }

    [Fact]
    public void When_the_captain_leaves_the_first_officer_moves_up_and_the_catalog_reopens()
    {
        var p = new PartyAuthority("host", "Simeon", "x");
        p.Join("guest", "Alex", "a");
        p.Apply("host", new SwapSeats()); // guest is Captain, host FO
        p.Remove("guest");
        Assert.Equal("host", p.State.CaptainId);
        Assert.Null(p.State.FirstOfficerId);
        Assert.Equal(JoinOutcome.Accepted, p.Join("new", "Sam", "b"));
    }

    [Fact]
    public void Only_the_host_kicks_and_renames()
    {
        var p = TwoPilots();
        Assert.Contains("host", p.Apply("guest", new Kick("host")));
        Assert.Contains("host", p.Apply("guest", new RenameParty("mine")));
        Assert.Null(p.Apply("host", new Kick("guest")));
        Assert.Single(p.State.Members);
    }
}
