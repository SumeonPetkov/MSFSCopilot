using SkyLink.Core;
using Xunit.Abstractions;

namespace SkyLink.Chaos.Tests;

public sealed class RestartAndRejectTests
{
    private readonly ITestOutputHelper _out;

    public RestartAndRejectTests(ITestOutputHelper output) => _out = output;

    public static IEnumerable<object[]> Seeds()
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("SKYLINK_CHAOS_RUNS"), out int r) ? Math.Max(1, r / 4) : 100;
        for (int i = 0; i < runs; i++) yield return [i];
    }

    /// <summary>
    /// One seat's app crashes and relaunches mid-flight (the sim keeps running). The cockpits
    /// must re-converge, the host's cockpit must win if the joiner restarted, and button presses
    /// made after the relaunch must be delivered exactly once.
    /// </summary>
    [Theory]
    [MemberData(nameof(Seeds))]
    public void Recovers_from_an_app_restart(int seed)
    {
        var rng = new Random(seed);
        var schema = ConvergenceTests.BuildSchema();
        var world = new World(schema, rng, rng.Next(0, 120), rng.Next(-2000, 2000), rng.Next(-2000, 2000),
            new EngineOptions { MaxEventAgeMs = int.MaxValue });
        var stateVars = Enumerable.Range(0, schema.Vars.Count).Where(v => schema.Vars[v].Class == SyncClass.State).ToArray();
        var eventVars = Enumerable.Range(0, schema.Vars.Count).Where(v => schema.Vars[v].Class == SyncClass.Event).ToArray();

        world.Connect();
        world.Run(3000);
        world.Link.Conditions = new LinkConditions(rng.NextDouble() * 0.2, rng.NextDouble() * 0.05, 5, rng.Next(20, 200), rng.Next(2) == 0);

        bool pilotsActive = true;
        void Pilots(World w)
        {
            if (!pilotsActive) return;
            foreach (var p in w.Peers)
            {
                long now = p.Local(w.T);
                double roll = rng.NextDouble();
                if (roll < 0.004) p.Sim.UserSet(stateVars[rng.Next(stateVars.Length)], rng.Next(3), now);
                else if (roll < 0.007)
                {
                    int v = eventVars[rng.Next(eventVars.Length)];
                    p.GeneratedEvents.Add((v, rng.Next(100)));
                    p.Engine.OnLocalEvent(v, p.GeneratedEvents[^1].Param, now);
                }
            }
        }

        var lossy = world.Link.Conditions;
        world.Run(rng.Next(1000, 6000), Pilots);

        // Pilots stop and the network is briefly good, so both cockpits settle.
        pilotsActive = false;
        world.Link.Conditions = LinkConditions.Perfect;
        world.Run(3000);
        int victim = rng.Next(2);
        double[] hostBefore = (double[])world.Peers[0].Sim.Values.Clone();
        double[] joinerSettled = (double[])world.Peers[1].Sim.Values.Clone();
        foreach (int v in stateVars) Assert.Equal(hostBefore[v], joinerSettled[v]);

        // The app on one seat crashes; it relaunches on a bad network. In the meantime the
        // joiner's own cockpit drifts (a relaunching joiner may have been touching switches).
        world.Link.Conditions = lossy;
        if (victim == 1)
            foreach (int v in stateVars)
                if (rng.Next(3) == 0) world.Peers[1].Sim.Values[v] = rng.Next(3);
        world.Restart(victim);
        for (int i = 0; i < 4000 && !(world.Peers[0].Engine.IsEstablished && world.Peers[1].Engine.IsEstablished); i++) world.Step();
        Assert.True(world.Peers[0].Engine.IsEstablished && world.Peers[1].Engine.IsEstablished, "session not re-established after restart");
        world.Run(2000);

        // Once the network heals the host's cockpit must have won everywhere, untouched.
        world.Link.Conditions = LinkConditions.Perfect;
        world.Run(3000);
        foreach (int v in stateVars)
        {
            string ctx = $"seed {seed} victim={victim} '{schema.Vars[v].Id}' hostBefore={hostBefore[v]} host={world.Peers[0].Sim.Values[v]} joiner={world.Peers[1].Sim.Values[v]}";
            Assert.True(hostBefore[v] == world.Peers[0].Sim.Values[v], "host cockpit changed by the restart: " + ctx);
            Assert.True(hostBefore[v] == world.Peers[1].Sim.Values[v], "joiner did not adopt the host cockpit: " + ctx);
        }

        // From here on every button press must arrive exactly once.
        foreach (var p in world.Peers)
        {
            p.GeneratedEvents.Clear();
            p.Sim.AppliedEvents.Clear();
        }
        world.Link.Conditions = lossy;

        pilotsActive = true;
        world.Run(5000, Pilots);
        pilotsActive = false;
        world.Link.Conditions = LinkConditions.Perfect;
        world.Run(3000);

        var (a, b) = (world.Peers[0], world.Peers[1]);
        foreach (int v in stateVars)
        {
            Assert.True(a.Engine.TryGetEntry(v, out var ea) == b.Engine.TryGetEntry(v, out var eb) && ea == eb,
                $"seed {seed}: '{schema.Vars[v].Id}' differs: {ea} vs {eb}");
            Assert.Equal(a.Sim.Values[v], b.Sim.Values[v]);
        }
        Assert.Equal(a.GeneratedEvents, b.Sim.AppliedEvents);
        Assert.Equal(b.GeneratedEvents, a.Sim.AppliedEvents);
        Assert.Equal(a.Engine.Authority, b.Engine.Authority);
        Assert.NotEqual(a.Adapter.IsPilotFlying, b.Adapter.IsPilotFlying);
    }

    /// <summary>
    /// A switch the joiner's sim will not accept (e.g. a guarded switch with a wrong setter in the
    /// definition). It must be reported, retried a bounded number of times, and must never start
    /// a ping-pong that flips the captain's switch back.
    /// </summary>
    [Fact]
    public void Rejected_switch_is_reported_without_ping_pong()
    {
        var rng = new Random(1);
        var schema = ConvergenceTests.BuildSchema();
        var world = new World(schema, rng, 30, 0, 0);
        int guarded = schema.IndexOf("fuel5");
        world.Peers[1].Sim.RejectVars.Add(guarded);

        world.Connect();
        world.Run(3000);
        long packetsBefore = world.Link.PacketsSent;

        for (int i = 0; i < 5; i++)
        {
            var host = world.Peers[0];
            host.Sim.UserSet(guarded, i % 2 == 0 ? 1 : 0, host.Local(world.T));
            world.Run(3000);
            Assert.Equal(i % 2 == 0 ? 1 : 0, host.Sim.Values[guarded]); // never flipped back
        }
        world.Run(10_000);

        var joiner = world.Peers[1].Engine;
        long packets = world.Link.PacketsSent - packetsBefore;
        _out.WriteLine($"packets over 25 s: {packets}; joiner rejections: {joiner.Health.WriteRejections}");
        Assert.Equal(VarStatus.Rejected, joiner.Health.Status[guarded]);
        Assert.Equal(3, joiner.Health.WriteRejections); // the three changes to 1 (0 already matched)
        Assert.True(packets < 200, $"{packets} packets in 25 s suggests a feedback loop");
        Assert.Equal(0, world.Peers[0].Engine.Health.WriteRejections);
    }
}
