using SkyLink.Core;
using Xunit.Abstractions;

namespace SkyLink.Chaos.Tests;

/// <summary>
/// Two seats flying one aircraft: the pilot flying's kinematic aircraft streams its pose, the
/// monitoring seat follows it, and the controls change hands again and again over a lossy,
/// jittery network. Requires: no visible jumps (also not at a handover), exactly one pilot
/// flying, and the monitoring aircraft exactly where the flying one was at the render time.
/// </summary>
public sealed class FlightChaosTests(ITestOutputHelper output)
{
    private static SyncSchema Schema() => new([new GroupSpec("g", 1)], [new VarSpec("x", SyncClass.State, "g")]);

    public static IEnumerable<object[]> Seeds()
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("SKYLINK_CHAOS_RUNS"), out int r) ? Math.Max(1, r / 20) : 30;
        for (int i = 0; i < runs; i++) yield return [i];
    }

    private static (World W, Random Rng) Setup(int seed)
    {
        var rng = new Random(seed);
        var w = new World(Schema(), rng, 20, rng.Next(-2000, 2000), rng.Next(-2000, 2000)) { FlightEnabled = true };
        foreach (var p in w.Peers) p.Adapter.Flight.TurnRateDps = rng.Next(-3, 4);
        w.Connect();
        w.Run(3000);
        foreach (var p in w.Peers) p.Adapter.Flight.TrackMoves = true;
        return (w, rng);
    }

    private static void HandOverNowAndThen(World w, Random rng, ref long nextHandover)
    {
        if (w.T < nextHandover) return;
        nextHandover = w.T + rng.Next(2500, 6000);
        // During an outage both seats may be flying (a takeover nobody else could hear about);
        // that is resolved on reconnect, and there is nobody to hand over from meanwhile.
        var monitoring = w.Peers.FirstOrDefault(p => !p.Adapter.IsPilotFlying);
        if (monitoring is null) return;
        long now = monitoring.Local(w.T);
        if (rng.Next(2) == 0) monitoring.Engine.RequestControl(now);
        else monitoring.Engine.TakeControl(now);
        w.Drain(monitoring, now);
    }

    /// <summary>Where the monitoring aircraft is vs. where the flying one was at the render time.</summary>
    private static double TrackingErrorM(World w)
    {
        var pf = w.Peers.Single(p => p.Adapter.IsPilotFlying);
        var pm = w.Peers.Single(p => !p.Adapter.IsPilotFlying);
        double renderAt = pm.Adapter.Flight.Follower.RenderRemoteTime(pm.Local(w.T));
        var truth = pf.Adapter.Flight.PoseAt(renderAt) ?? throw new InvalidOperationException("no history at render time");
        return FlightFollower.DistanceM(truth, pm.Adapter.Flight.Pose);
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Handovers_are_seamless_on_a_lossy_network(int seed)
    {
        var (w, rng) = Setup(seed);
        w.Link.Conditions = new LinkConditions(rng.NextDouble() * 0.15, rng.NextDouble() * 0.05, rng.Next(10, 40), rng.Next(60, 140), false);
        long nextHandover = w.T + 2000;
        w.Run(40_000, x => HandOverNowAndThen(x, rng, ref nextHandover));
        w.Link.Conditions = LinkConditions.Perfect;
        w.Run(2500);

        var (a, b) = (w.Peers[0], w.Peers[1]);
        Assert.Equal(a.Engine.Authority, b.Engine.Authority);
        Assert.True(a.Adapter.IsPilotFlying ^ b.Adapter.IsPilotFlying, "exactly one pilot flying");
        int handovers = a.Engine.Authority.Epoch;
        double maxMove = Math.Max(a.Adapter.Flight.MaxStepMoveM, b.Adapter.Flight.MaxStepMoveM);
        double err = TrackingErrorM(w);
        output.WriteLine($"seed {seed}: {handovers} handovers, largest 5 ms move {maxMove:F2} m (nominal 0.39), tracking error {err:F3} m");
        Assert.True(handovers >= 5, $"only {handovers} handovers happened");
        // 77 m/s is 0.39 m per 5 ms step; easing a handover adds a little. A snap would be 15 m+.
        Assert.True(maxMove < 1.0, $"seed {seed}: an aircraft jumped {maxMove:F2} m in 5 ms");
        Assert.True(err < 1.0, $"seed {seed}: monitoring aircraft {err:F2} m off the flying one");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Recovers_from_outages_with_one_pilot_flying(int seed)
    {
        var (w, rng) = Setup(seed + 1000);
        w.Link.Conditions = new LinkConditions(rng.NextDouble() * 0.1, 0, 10, rng.Next(40, 120), false);
        long nextHandover = w.T + 2000, reconnectAt = long.MaxValue;
        w.Run(30_000, x =>
        {
            HandOverNowAndThen(x, rng, ref nextHandover);
            if (x.Link.Connected && rng.NextDouble() < 0.0005)
            {
                x.Disconnect();
                reconnectAt = x.T + rng.Next(500, 4000);
            }
            else if (!x.Link.Connected && x.T >= reconnectAt)
            {
                x.Connect();
            }
        });
        if (!w.Link.Connected) w.Connect();
        w.Link.Conditions = LinkConditions.Perfect;
        w.Run(3000);

        var (a, b) = (w.Peers[0], w.Peers[1]);
        Assert.Equal(a.Engine.Authority, b.Engine.Authority);
        Assert.True(a.Adapter.IsPilotFlying ^ b.Adapter.IsPilotFlying, "exactly one pilot flying");
        double err = TrackingErrorM(w);
        output.WriteLine($"seed {seed}: {a.Engine.Authority.Epoch} handovers, tracking error after heal {err:F3} m");
        Assert.True(err < 1.0, $"seed {seed}: monitoring aircraft {err:F2} m off the flying one after the network healed");
    }
}
