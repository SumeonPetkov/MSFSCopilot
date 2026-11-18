using System.Diagnostics;
using System.Net.Sockets;
using SkyLink.Core;
using SkyLink.Definitions;
using SkyLink.Net;
using SkyLink.Session;
using SkyLink.Sim;
using Xunit.Abstractions;

namespace SkyLink.Integration.Tests;

/// <summary>
/// One shared world over real UDP (LiteNetLib with simulated loss and latency) between two fake
/// sims: the Captain's AI traffic appears in the First Officer's sim and follows it, vanishes
/// when it vanishes, uninstalled models get stand-ins; the Captain's time, sim rate and pause are
/// applied on the FO; the FO's "own traffic still on" warning reaches the Captain; and when the
/// session ends nothing SkyLink created is left in the FO's sim.
/// </summary>
public sealed class WorldUdpTests(ITestOutputHelper output)
{
    private static readonly AircraftDefinition C172 =
        DefinitionCatalog.Load(Cli.Program.FindDefinitions()).Aircraft.Single(d => d.Id == "asobo-c172sp-g1000");

    private static FlightSample Pose(double lat, double lon, double hdg, double ktsSpeed, double alt = 4000) =>
        new(lat, lon, alt, 0, 0, hdg, ktsSpeed / 0.592484 * Math.Sin(hdg * Math.PI / 180), 0, ktsSpeed / 0.592484 * Math.Cos(hdg * Math.PI / 180), 0, 0, 0, 0, 0, 0, ktsSpeed == 0);

    [Fact]
    public void Fo_mirrors_the_captains_traffic_time_rate_and_pause_over_udp()
    {
        int port;
        using (var u = new UdpClient(0)) port = ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
        var opts = new LinkOptions { SessionCode = "WORLD1", Port = port, SimulateLossPercent = 10, SimulateLatencyMinMs = 20, SimulateLatencyMaxMs = 60 };
        var captain = new FakeSimBridge(C172, 1);
        var fo = new FakeSimBridge(C172, 2);
        var rng = new Random(5);
        var titles = new[] { ("A320neo V2", "Lufthansa"), ("Boeing 737 MAX 8", "Default"), ("Cessna 172 Skyhawk G1000", "White"), ("FSLTL B738 Ryanair", "") };
        var ids = new List<uint>();
        for (int i = 0; i < 20; i++)
        {
            var (title, livery) = titles[i % titles.Length];
            ids.Add(captain.AddAi(new TrafficIdentity(TrafficKind.Airplane, title, livery, $"N{100 + i}", "", "", ""),
                Pose(47.4 + rng.NextDouble() * 0.3, -122.4 + rng.NextDouble() * 0.3, rng.Next(360), i % 5 == 0 ? 0 : rng.Next(100, 300)),
                turnRateDps: rng.Next(-2, 3)));
        }

        var host = new SessionRunner(captain, Link.Host(opts));
        var join = new SessionRunner(fo, Link.Join($"127.0.0.1:{port}", opts));
        void RunFor(int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                host.Step();
                join.Step();
                Thread.Sleep(2);
            }
        }

        try
        {
            RunFor(6000);
            Assert.True(join.Status.Established);
            Assert.Equal(WorldRole.Source, captain.WorldRole);
            Assert.Equal(WorldRole.Mirror, fo.WorldRole);

            // Every Captain aircraft exists once on the FO, near its track; the uninstalled one as a stand-in.
            var byRemote = fo.InjectedTraffic.Values.GroupBy(o => o.Spawn.RemoteId).ToDictionary(g => g.Key, g => g.Single());
            Assert.Equal(ids.Order(), byRemote.Keys.Order());
            double worst = 0;
            foreach (var (id, o) in byRemote)
            {
                var ai = captain.AiTraffic[id];
                double err = ai.History.Select(h => FlightFollower.DistanceM(h.P, o.Pose)).Min();
                worst = Math.Max(worst, err);
                if (ai.Identity.Title.StartsWith("FSLTL")) Assert.True(o.Spawn.StandIn, "uninstalled model must get a stand-in");
                else Assert.Equal((ai.Identity.Title, ai.Identity.Livery), (o.Spawn.Title, o.Spawn.Livery));
                Assert.Equal(ai.Identity.AtcId, o.Spawn.TailNumber);
                Assert.True(o.Moves > 10, "mirrored aircraft must be moved continuously");
            }
            output.WriteLine($"{byRemote.Count} AI aircraft mirrored, worst distance from the true track {worst:F2} m, {fo.World.StandIns} stand-in(s)");
            Assert.True(worst < 3, $"worst track error {worst:F2} m");

            // Five aircraft land and are removed in the Captain's sim.
            foreach (uint id in ids.Take(5)) captain.AiTraffic.Remove(id);
            RunFor(2500);
            Assert.Equal(15, fo.InjectedTraffic.Count);
            Assert.DoesNotContain(fo.InjectedTraffic.Values, o => ids.Take(5).Contains(o.Spawn.RemoteId));

            // The FO forgot to turn MSFS traffic off: the Captain hears about it.
            fo.ForeignAi = 3;
            RunFor(1500);
            Assert.Equal(3, host.Status.PeerWorld?.ForeignTraffic);
            Assert.Equal(3, join.Status.World.ForeignTraffic);

            // Time (five hours apart) and sim rate follow the Captain; then pause does.
            captain.WorldNow = captain.WorldNow with { ZuluUnixSec = captain.WorldNow.ZuluUnixSec + 5 * 3600, SimRate = 4 };
            RunFor(5000);
            Assert.Equal(4, fo.WorldNow.SimRate);
            double dt = Math.Abs(fo.WorldNow.ZuluUnixSec - captain.WorldNow.ZuluUnixSec);
            Assert.True(dt < 120, $"FO clock {dt:F0} s from the Captain's");
            captain.WorldNow = captain.WorldNow with { Paused = true };
            RunFor(2500);
            Assert.True(fo.WorldNow.Paused);
            captain.WorldNow = captain.WorldNow with { Paused = false };
            RunFor(2500);
            Assert.False(fo.WorldNow.Paused);
            output.WriteLine("FO world actions: " + string.Join(", ", fo.AppliedWorldActions));
        }
        finally
        {
            join.Dispose(); // the FO ends the session (sim stays: ownsSim false would keep it; here the runner owns it)
            host.Dispose();
        }
        Assert.Empty(fo.InjectedTraffic);
        Assert.Equal(0, fo.BogusRemovals);
        output.WriteLine($"session ended: {fo.Removals} removals, none left behind");
    }

    [Fact]
    public void Ending_the_session_removes_mirrored_traffic_but_keeps_the_sim()
    {
        int port;
        using (var u = new UdpClient(0)) port = ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
        var opts = new LinkOptions { SessionCode = "WORLD2", Port = port };
        var captain = new FakeSimBridge(C172, 1);
        var fo = new FakeSimBridge(C172, 2);
        for (int i = 0; i < 5; i++)
            captain.AddAi(new TrafficIdentity(TrafficKind.Airplane, "A320neo V2", "Default", $"T{i}", "", "", ""), Pose(47 + i * 0.01, 8, 90, 200));
        using var host = new SessionRunner(captain, Link.Host(opts));
        var join = new SessionRunner(fo, Link.Join($"127.0.0.1:{port}", opts), ownsSim: false); // like the desktop app
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 4000)
        {
            host.Step();
            join.Step();
            Thread.Sleep(2);
        }
        Assert.Equal(5, fo.InjectedTraffic.Count);
        join.Dispose();
        Assert.Empty(fo.InjectedTraffic);
        Assert.Equal(WorldRole.None, fo.WorldRole);
        fo.Pump(Environment.TickCount64);
        Assert.Empty(fo.InjectedTraffic);
    }
}
