using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using SkyLink.Definitions;
using SkyLink.Lobby;
using SkyLink.Session;
using SkyLink.Sim;
using Xunit.Abstractions;

namespace SkyLink.Integration.Tests;

/// <summary>
/// Two complete desktop-app back ends on this PC (fake sims, real UDP): find each other, form a
/// party, agree on an aircraft both have, import a plan, press Start once, and end up seated in
/// a synced shared cockpit. Then swap seats mid-flight and end the flight.
/// </summary>
public sealed class PartyFlowTests(ITestOutputHelper output)
{
    private static readonly AircraftDefinition C172 =
        DefinitionCatalog.Load(Cli.Program.FindDefinitions()).Aircraft.Single(d => d.Id == "asobo-c172sp-g1000");

    private static int FreeUdpPort()
    {
        using var u = new UdpClient(0);
        return ((IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private sealed class App : IDisposable
    {
        public required LocalPlayer Me { get; init; }
        public required FakeSimBridge Sim { get; init; }
        public required SimHost Host { get; init; }
        public required Discovery Discovery { get; init; }
        public required LaunchCoordinator Launch { get; init; }
        public PartyNode? Party { get; set; }
        public List<string> Log { get; } = [];

        public void Tick()
        {
            long now = Host.Now;
            Host.StepOnce();
            Discovery.Poll(now);
            Party?.Poll(now);
            if (Party is not null) Launch.Tick(Party);
        }

        public void Dispose()
        {
            Party?.Dispose();
            Discovery.Dispose();
            Host.Dispose();
        }
    }

    private static App MakeApp(string id, string name, int beaconPort, int peerBeaconPort, int flightPort, List<(string, string)> installed)
    {
        var sim = new FakeSimBridge(C172, id.GetHashCode()) { InstalledAircraft = installed };
        var host = new SimHost(sim);
        var disc = new Discovery(id, beaconPort, [new IPEndPoint(IPAddress.Loopback, peerBeaconPort)]);
        var app = new App
        {
            Me = new LocalPlayer(id, name),
            Sim = sim,
            Host = host,
            Discovery = disc,
            Launch = new LaunchCoordinator(host, flightPort, isSimRunning: () => true, launchSim: () => "fake")
            {
                LoadRetryAfter = TimeSpan.FromSeconds(2),
            },
        };
        app.Launch.Log += m => app.Log.Add(m);
        host.Log += m => app.Log.Add(m);
        disc.CurrentBeacon = () => new Beacon(Beacon.Version, id, name, true, sim.AircraftTitle,
            app.Party?.State is { } s ? new BeaconParty(s.PartyId, s.Name, app.Party.Port, s.Members.Count, s.Captain?.Name, s.FirstOfficer?.Name, s.Phase) : null);
        return app;
    }

    private static bool RunUntil(Func<bool> done, int ms, params App[] apps)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var a in apps) a.Tick();
            if (done()) return true;
            Thread.Sleep(2);
        }
        return false;
    }

    private static void Publish(App a)
    {
        a.Sim.AircraftCatalog += list => a.Party?.Send(new PublishCatalog(AircraftCatalog.Group(list)));
        a.Host.Post(() => a.Sim.RequestAircraftCatalog());
    }

    [Fact]
    public void Two_players_find_each_other_form_a_party_and_one_click_seats_them_in_a_synced_cockpit()
    {
        int beaconA = FreeUdpPort(), beaconB = FreeUdpPort(), partyPort = FreeUdpPort(), flightPort = FreeUdpPort();
        using var a = MakeApp("pilot-a", "Simeon", beaconA, beaconB, flightPort,
            [("A320neo V2", "Default"), ("A320neo V2", "Lufthansa"), ("Cessna 172 Skyhawk G1000", "White")]);
        using var b = MakeApp("pilot-b", "Alex", beaconB, beaconA, flightPort,
            [("A320neo V2", "Lufthansa"), ("A320neo V2", "easyJet"), ("Boeing 737 MAX 8", "Default")]);

        // 1) Discovery.
        a.Party = PartyNode.Host(a.Me, "Saturday flight", partyPort);
        Assert.True(RunUntil(() => b.Discovery.Players.Any(p => p.Beacon.Id == "pilot-a" && p.CanJoin), 8000, a, b), "B never saw A's party");
        var found = b.Discovery.Players.Single(p => p.Beacon.Id == "pilot-a");
        output.WriteLine($"B sees {found.Beacon.Name} at {found.Address}, party '{found.Beacon.Party!.Name}'");

        // 2) Join.
        b.Party = PartyNode.Join(b.Me, found.Address, found.Beacon.Party.Port, found.Beacon.Party.Id);
        Assert.True(RunUntil(() => b.Party.IsConnected && a.Party.State!.Members.Count == 2, 8000, a, b), "join failed");
        Assert.Equal(("pilot-a", "pilot-b"), (a.Party.State!.CaptainId, a.Party.State.FirstOfficerId));

        // 3) Shared aircraft list: only what both PCs have.
        Publish(a);
        Publish(b);
        Assert.True(RunUntil(() => b.Party.SharedCatalog.Count == 1, 5000, a, b), "catalog never arrived");
        Assert.Equal("A320neo V2", b.Party.SharedCatalog[0].Title);
        Assert.Equal(["Lufthansa"], b.Party.SharedCatalog[0].Liveries);

        // 4) Both pilots edit the setup; the edits show up on both screens.
        b.Party.Send(new SetAircraft("A320neo V2", "Lufthansa"));
        a.Party.Send(new SetCallsign("DLH4AB", "LUFTHANSA", "4"));
        var plan = SimBrief.Parse(SimBriefSample.Json, "Alex", DateTimeOffset.UtcNow).Plan;
        b.Party.Send(new SetPlan(plan));
        Assert.True(RunUntil(() => b.Party.State?.Setup is { AircraftTitle: "A320neo V2", Callsign: "DLH4AB", Plan.Origin: "EDDF" }
                                   && a.Party.State?.Setup.Plan?.Destination == "LFPG", 5000, a, b), "setup did not sync");
        Assert.Null(a.Party.CanStart());

        // 5) One click. The First Officer's MSFS drops the first load request without a word, as
        // MSFS does while it is still booting to the main menu: SkyLink must ask again.
        b.Sim.IgnoreLoads = 1;
        var start = FlightFiles.ChooseStart(a.Party.State!.Setup, null)!;
        a.Party.Send(new StartFlight(start));
        bool seated = RunUntil(() => a.Party.State?.Phase == PartyPhase.InFlight
                                     && a.Launch.Stage == LaunchStage.InCockpit && b.Launch.Stage == LaunchStage.InCockpit, 20_000, a, b);
        foreach (var line in a.Log.TakeLast(12)) output.WriteLine("A: " + line);
        foreach (var line in b.Log.TakeLast(12)) output.WriteLine("B: " + line);
        Assert.True(seated, $"launch stalled: A={a.Launch.Stage} {a.Launch.Error}, B={b.Launch.Stage} {b.Launch.Error}");

        // Both loaded the same flight, with the agreed aircraft, callsign and route.
        Assert.Equal("A320neo V2", a.Sim.AircraftTitle);
        Assert.Equal("A320neo V2", b.Sim.AircraftTitle);
        Assert.Equal(("DLH4AB", "LUFTHANSA", "4"), b.Sim.Callsign);
        Assert.NotNull(b.Sim.LastPlanLoaded);
        Assert.Contains("Livery=Lufthansa", File.ReadAllText(b.Sim.LastFlightLoaded!, FlightFiles.FltEncoding));
        Assert.Equal(b.Sim.LastFlightLoaded, b.Sim.LoadedFlight);
        Assert.Contains(b.Log, l => l.Contains("asking again"));
        // The Captain hosts the cockpit and flies; the First Officer follows.
        Assert.True(a.Sim.IsPilotFlying);
        Assert.False(b.Sim.IsPilotFlying);
        Assert.True(RunUntil(() => b.Sim.IsFollowing, 3000, a, b), "FO's aircraft is not following the Captain's");

        // 6) Seats change mid-flight: labels swap, who flies does not.
        b.Party.Send(new SwapSeats());
        Assert.True(RunUntil(() => a.Party.State?.CaptainId == "pilot-b" && b.Party.State?.CaptainId == "pilot-b", 3000, a, b));
        RunUntil(() => false, 500, a, b);
        Assert.True(a.Sim.IsPilotFlying, "a seat swap must not hand over the controls");
        Assert.Equal(LaunchStage.InCockpit, b.Launch.Stage);

        // 7) End the flight: both sides drop the cockpit session and return to the lobby.
        a.Party.Send(new EndFlight());
        Assert.True(RunUntil(() => b.Party.State?.Phase == PartyPhase.Lobby && a.Launch.Stage == LaunchStage.Idle
                                   && b.Launch.Stage == LaunchStage.Idle && !a.Host.SessionRunning && !b.Host.SessionRunning, 5000, a, b));
    }

    [Fact]
    public void A_member_whose_connection_drops_gets_their_seat_back()
    {
        int partyPort = FreeUdpPort();
        using var host = PartyNode.Host(new LocalPlayer("h", "Host"), "x", partyPort);
        var guestMe = new LocalPlayer("g", "Guest");
        var guest = PartyNode.Join(guestMe, "127.0.0.1", partyPort, host.PartyId);
        void Pump(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                host.Poll(sw.ElapsedMilliseconds);
                guest.Poll(sw.ElapsedMilliseconds);
                Thread.Sleep(2);
            }
        }
        Pump(1500);
        guest.Send(new SwapSeats());
        Pump(500);
        Assert.Equal("g", host.State!.CaptainId);

        guest.DropWithoutGoodbye(); // app crashed / network gone: no goodbye
        Pump(9000);
        Assert.False(host.State!.Member("g")!.Connected);
        Assert.NotNull(host.CanStart());

        using var again = PartyNode.Join(guestMe, "127.0.0.1", partyPort, host.PartyId);
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000 && !(again.IsConnected && host.State!.Member("g")!.Connected))
        {
            host.Poll(sw.ElapsedMilliseconds);
            again.Poll(sw.ElapsedMilliseconds);
            Thread.Sleep(2);
        }
        Assert.True(host.State!.Member("g")!.Connected);
        Assert.Equal("g", host.State.CaptainId); // same seat as before
    }

    [Fact]
    public void A_third_player_is_turned_away_with_a_reason()
    {
        int partyPort = FreeUdpPort();
        using var host = PartyNode.Host(new LocalPlayer("h", "Host"), "x", partyPort);
        using var g1 = PartyNode.Join(new LocalPlayer("g1", "One"), "127.0.0.1", partyPort, host.PartyId);
        using var g2 = PartyNode.Join(new LocalPlayer("g2", "Two"), "127.0.0.1", partyPort, host.PartyId);
        string? ended = null;
        g2.Ended += r => ended = r;
        var sw = Stopwatch.StartNew();
        bool g1First = false;
        while (sw.ElapsedMilliseconds < 5000 && ended is null)
        {
            host.Poll(sw.ElapsedMilliseconds);
            g1.Poll(sw.ElapsedMilliseconds);
            if (g1First || g1.IsConnected) { g1First = true; g2.Poll(sw.ElapsedMilliseconds); }
            Thread.Sleep(2);
        }
        Assert.Contains("full", ended);
        Assert.Equal(2, host.State!.Members.Count);
    }
}

/// <summary>A trimmed, made-up OFP in SimBrief's JSON shape (strings, as SimBrief sends them).</summary>
internal static class SimBriefSample
{
    public const string Json = """
        {
          "fetch": { "status": "Success" },
          "params": { "units": "kgs" },
          "general": { "icao_airline": "DLH", "flight_number": "4AB", "route": "ANEKI Y163 NATOR UN850 ROLIS", "initial_altitude": "36000", "passengers": "142" },
          "origin": { "icao_code": "EDDF", "name": "FRANKFURT/MAIN", "pos_lat": "50.033333", "pos_long": "8.570556", "elevation": "364", "plan_rwy": "25C" },
          "destination": { "icao_code": "LFPG", "name": "PARIS/CHARLES DE GAULLE", "pos_lat": "49.009722", "pos_long": "2.547778", "elevation": "392" },
          "atc": { "callsign": "DLH4AB" },
          "aircraft": { "icaocode": "A20N", "reg": "D-AINA" },
          "fuel": { "plan_ramp": "6512" },
          "weights": { "pax_count": "142" },
          "fms_downloads": { "directory": "https://www.simbrief.com/ofp/flightplans/", "mfs": { "name": "MSFS", "link": "EDDFLFPG_MFS_1.pln" } }
        }
        """;
}
