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
/// Shared flight over real UDP (LiteNetLib with simulated loss and latency): one fake aircraft
/// flies a turn, the other follows; the controls change hands twice. Checks tracking accuracy,
/// that no aircraft ever jumps, and that the roles really swap.
/// </summary>
public sealed class FlightUdpTests(ITestOutputHelper output)
{
    private static readonly AircraftDefinition C172 =
        DefinitionCatalog.Load(Cli.Program.FindDefinitions()).Aircraft.Single(d => d.Id == "asobo-c172sp-g1000");

    [Fact]
    public void Monitoring_aircraft_follows_and_handovers_are_seamless_over_udp()
    {
        int port;
        using (var u = new UdpClient(0)) port = ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
        var opts = new LinkOptions { SessionCode = "FLY123", Port = port, SimulateLossPercent = 10, SimulateLatencyMinMs = 20, SimulateLatencyMaxMs = 60 };
        var hostSim = new FakeSimBridge(C172, 1) { TurnRateDps = 3 };
        var joinSim = new FakeSimBridge(C172, 2) { TurnRateDps = -2 };
        using var host = new SessionRunner(hostSim, Link.Host(opts));
        using var join = new SessionRunner(joinSim, Link.Join($"127.0.0.1:{port}", opts));

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

        (double Err, string Who) Tracking()
        {
            var (pf, pm) = hostSim.IsPilotFlying ? (hostSim, joinSim) : (joinSim, hostSim);
            double t = pm.FollowerRenderTime();
            int i = pf.FlownHistory.FindIndex(h => h.T >= t);
            Assert.True(i > 0, "no flown history at the render time");
            var (t0, a) = pf.FlownHistory[i - 1];
            var (t1, b) = pf.FlownHistory[i];
            var truth = FlightFollower.Interpolate(a, b, (t1 - t0) / 1000.0, (t - t0) / Math.Max(1, t1 - t0));
            return (FlightFollower.DistanceM(truth, pm.Pose), hostSim.IsPilotFlying ? "host flying" : "joiner flying");
        }

        RunFor(4000);
        Assert.True(join.Status.Established);
        Assert.True(hostSim.IsPilotFlying && !joinSim.IsPilotFlying);
        hostSim.TrackMoves = joinSim.TrackMoves = true;

        var (e1, w1) = Tracking();
        output.WriteLine($"{w1}: monitoring aircraft {e1:F2} m from where the flying one was at render time");

        join.RequestControl();        // the first officer asks for the controls (auto-granted)
        RunFor(4000);
        Assert.True(joinSim.IsPilotFlying && !hostSim.IsPilotFlying, "request/grant handover did not happen");
        var (e2, w2) = Tracking();
        output.WriteLine($"{w2}: {e2:F2} m");

        host.TakeControl();           // the captain takes them back (priority takeover)
        RunFor(4000);
        Assert.True(hostSim.IsPilotFlying && !joinSim.IsPilotFlying, "takeover did not happen");
        var (e3, w3) = Tracking();
        output.WriteLine($"{w3}: {e3:F2} m");

        double maxSpeed = Math.Max(hostSim.MaxApparentSpeedMps, joinSim.MaxApparentSpeedMps);
        output.WriteLine($"fastest apparent movement of either aircraft: {maxSpeed:F0} m/s (true speed 77 m/s; a snap would show as thousands)");
        Assert.True(e1 < 2 && e2 < 2 && e3 < 2, $"tracking errors {e1:F2}/{e2:F2}/{e3:F2} m");
        Assert.True(maxSpeed < 200, $"an aircraft jumped (apparent {maxSpeed:F0} m/s)");
        Assert.True(host.Engine!.FlightStatesApplied > 50 && join.Engine!.FlightStatesApplied > 50);
    }
}
