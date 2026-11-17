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
/// Two complete sessions (fake cockpit + engine + LiteNetLib UDP) on localhost, with
/// LiteNetLib's own packet-loss and latency simulation. This exercises the real transport,
/// codec, HMAC, handshake, reconnect and the session loop end to end.
/// </summary>
public sealed class UdpSessionTests(ITestOutputHelper output)
{
    private static readonly AircraftDefinition C172 =
        DefinitionCatalog.Load(Cli.Program.FindDefinitions()).Aircraft.Single(d => d.Id == "asobo-c172sp-g1000");

    private static int FreePort()
    {
        using var u = new UdpClient(0);
        return ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private sealed class Seat : IDisposable
    {
        public FakeSimBridge Sim { get; }
        public SessionRunner Runner { get; }
        public List<string> Log { get; } = [];

        public Seat(Link link, int seed)
        {
            Sim = new FakeSimBridge(C172, seed, latencyMs: 40);
            Runner = new SessionRunner(Sim, link);
            Runner.Log += m => { lock (Log) Log.Add(m); };
        }

        public void Dispose() => Runner.Dispose();
    }

    private static void RunFor(int ms, Action? each = null, params Seat[] seats)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var s in seats) s.Runner.Step();
            each?.Invoke();
            Thread.Sleep(2);
        }
    }

    private static int[] VarsOf(params SyncClass[] classes) =>
        Enumerable.Range(0, C172.Vars.Count).Where(v => classes.Contains(C172.Vars[v].Spec.Class)).ToArray();

    [Fact]
    public void Cockpits_converge_over_lossy_udp_and_presses_arrive_exactly_once()
    {
        int port = FreePort();
        var opts = new LinkOptions { SessionCode = "ABC-234", Port = port, SimulateLossPercent = 15, SimulateLatencyMinMs = 10, SimulateLatencyMaxMs = 60 };
        using var host = new Seat(Link.Host(opts), 1);
        using var join = new Seat(Link.Join($"127.0.0.1:{port}", opts), 2);

        var rng = new Random(42);
        var states = VarsOf(SyncClass.State);
        var analogs = VarsOf(SyncClass.Analog);
        var events = VarsOf(SyncClass.Event);
        // Different cockpits before connecting; the host's must win.
        foreach (int v in states)
        {
            host.Sim.UserSet(v, rng.Next(2));
            join.Sim.UserSet(v, rng.Next(2));
        }
        RunFor(3000, null, host, join);
        Assert.True(host.Runner.Status.Established && join.Runner.Status.Established, string.Join("\n", join.Log.TakeLast(20)));

        // Both pilots work the cockpit for 8 seconds.
        RunFor(8000, () =>
        {
            var seat = rng.Next(2) == 0 ? host : join;
            double r = rng.NextDouble();
            if (r < 0.02) seat.Sim.UserSet(states[rng.Next(states.Length)], rng.Next(2));
            else if (r < 0.035) seat.Sim.UserSet(analogs[rng.Next(analogs.Length)], rng.Next(0, 360));
            else if (r < 0.045) seat.Sim.UserPress(events[rng.Next(events.Length)], rng.Next(1, 50));
        }, host, join);

        RunFor(5000, null, host, join);

        foreach (int v in states.Concat(analogs))
            Assert.True(Math.Abs(host.Sim.Get(v) - join.Sim.Get(v)) <= C172.Vars[v].Spec.Epsilon + 1e-9,
                $"'{C172.Vars[v].Spec.Id}': host={host.Sim.Get(v)} joiner={join.Sim.Get(v)}");
        Assert.Equal(host.Sim.PressedEvents, join.Sim.AppliedEvents);
        Assert.Equal(join.Sim.PressedEvents, host.Sim.AppliedEvents);
        Assert.NotEmpty(host.Sim.PressedEvents);

        var hs = host.Runner.Status;
        var js = join.Runner.Status;
        output.WriteLine($"host: changes={hs.LocalChanges} applied={hs.RemoteApplied} events={hs.EventsSent}/{hs.EventsApplied} repairs={hs.Repairs} rtt={hs.LinkStats.RttMs} loss={hs.LinkStats.LossPercent:F1}% sent={hs.LinkStats.BytesSent}B");
        output.WriteLine($"join: changes={js.LocalChanges} applied={js.RemoteApplied} events={js.EventsSent}/{js.EventsApplied} repairs={js.Repairs}");
        Assert.Equal(0, hs.Rejections + js.Rejections);
        Assert.Equal(0, hs.LinkStats.BadPackets + js.LinkStats.BadPackets);
        Assert.True(hs.PilotFlying ^ js.PilotFlying);
    }

    [Fact]
    public void Wrong_session_code_is_refused()
    {
        int port = FreePort();
        using var host = new Seat(Link.Host(new LinkOptions { SessionCode = "RIGHT1", Port = port }), 1);
        using var join = new Seat(Link.Join($"127.0.0.1:{port}", new LinkOptions { SessionCode = "WRONG1", Port = port }), 2);
        RunFor(3000, null, host, join);
        Assert.Equal(LinkState.Listening, host.Runner.Status.Link);
        Assert.NotEqual(LinkState.Connected, join.Runner.Status.Link);
        Assert.Contains(host.Log, l => l.Contains("wrong session code"));
    }

    [Fact]
    public void Joiner_reconnects_by_itself_when_the_host_restarts()
    {
        int port = FreePort();
        var opts = new LinkOptions { SessionCode = "XYZ789", Port = port, ReconnectMinMs = 300, ReconnectMaxMs = 1000, DisconnectTimeoutMs = 2000 };
        var host = new Seat(Link.Host(opts), 1);
        using var join = new Seat(Link.Join($"127.0.0.1:{port}", opts), 2);
        int beacon = C172.Schema.IndexOf("beacon");
        RunFor(2500, null, host, join);
        Assert.True(join.Runner.Status.Established);

        host.Dispose(); // host app closes
        RunFor(1500, null, join);
        Assert.False(join.Runner.Status.Established);

        using var host2 = new Seat(Link.Host(opts), 3); // relaunched, beacon on
        host2.Sim.UserSet(beacon, 1);
        RunFor(6000, null, host2, join);

        Assert.True(join.Runner.Status.Established, string.Join("\n", join.Log.TakeLast(15)));
        Assert.Equal(1, join.Sim.Get(beacon));
    }
}
