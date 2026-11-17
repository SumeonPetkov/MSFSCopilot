using SkyLink.Core;
using Xunit.Abstractions;

namespace SkyLink.Chaos.Tests;

/// <summary>
/// Randomized two-pilot sessions over a hostile network. Each run: warm up, then 20 s of pilots
/// flipping switches, turning knobs, pressing buttons and swapping controls while the link
/// drops packets, duplicates, reorders and disconnects; then the network heals and we require
/// both cockpits to be identical, every button press delivered exactly once in order, and
/// exactly one pilot flying.
/// </summary>
public sealed class ConvergenceTests
{
    private readonly ITestOutputHelper _out;

    public ConvergenceTests(ITestOutputHelper output) => _out = output;

    public static SyncSchema BuildSchema()
    {
        var groups = new[]
        {
            new GroupSpec("electrical", 10),
            new GroupSpec("fuel", 20),
            new GroupSpec("lights", 30),
            new GroupSpec("autopilot", 80),
            new GroupSpec("controls", 0),
        };
        var vars = new List<VarSpec>();
        for (int i = 0; i < 8; i++) vars.Add(new VarSpec($"elec{i}", SyncClass.State, "electrical"));
        for (int i = 0; i < 6; i++) vars.Add(new VarSpec($"fuel{i}", SyncClass.State, "fuel"));
        for (int i = 0; i < 8; i++) vars.Add(new VarSpec($"light{i}", SyncClass.State, "lights"));
        for (int i = 0; i < 4; i++) vars.Add(new VarSpec($"knob{i}", SyncClass.Analog, "autopilot", 0.01));
        for (int i = 0; i < 6; i++) vars.Add(new VarSpec($"cmd{i}", SyncClass.Event, "autopilot"));
        for (int i = 0; i < 4; i++) vars.Add(new VarSpec($"ctl{i}", SyncClass.Control, "controls", 0.001));
        vars.Add(new VarSpec("bus_volts", SyncClass.Derived, "electrical", 0.1));
        vars.Add(new VarSpec("dome", SyncClass.Local, "lights"));
        return new SyncSchema(groups, vars);
    }

    public static IEnumerable<object[]> Seeds()
    {
        int runs = int.TryParse(Environment.GetEnvironmentVariable("SKYLINK_CHAOS_RUNS"), out int r) ? r : 300;
        for (int i = 0; i < runs; i++) yield return [i];
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Converges_under_chaos(int seed) => RunScenario(seed, assert: true);

    public static ScenarioResult RunScenario(int seed, bool assert, ITestOutputHelper? output = null, EngineOptions? options = null)
    {
        var rng = new Random(seed);
        var schema = BuildSchema();
        int maxSimLatency = rng.Next(0, 150);
        var world = new World(schema, rng, maxSimLatency, rng.Next(-3000, 3000), rng.Next(-3000, 3000),
            options ?? new EngineOptions { MaxEventAgeMs = int.MaxValue });

        var spring = new[] { schema.IndexOf("light6"), schema.IndexOf("light7") };
        var toggle = new[] { schema.IndexOf("elec6"), schema.IndexOf("elec7") };
        foreach (var p in world.Peers)
        {
            p.Sim.SpringVars.UnionWith(spring);
            p.Sim.ToggleVars.UnionWith(toggle);
        }

        // Each cockpit starts in a different state: the host is the truth.
        var stateVars = Enumerable.Range(0, schema.Vars.Count).Where(v => schema.Vars[v].Class == SyncClass.State).ToArray();
        var analogVars = Enumerable.Range(0, schema.Vars.Count).Where(v => schema.Vars[v].Class == SyncClass.Analog).ToArray();
        var eventVars = Enumerable.Range(0, schema.Vars.Count).Where(v => schema.Vars[v].Class == SyncClass.Event).ToArray();
        var controlVars = Enumerable.Range(0, schema.Vars.Count).Where(v => schema.Vars[v].Class == SyncClass.Control).ToArray();
        foreach (var p in world.Peers)
            foreach (int v in stateVars)
                if (!spring.Contains(v)) p.Sim.Values[v] = toggle.Contains(v) ? rng.Next(2) : rng.Next(3);

        world.Run(500);
        world.Connect();
        world.Run(4000);

        bool evil = rng.NextDouble() < 0.5;
        var chaos = new LinkConditions(rng.NextDouble() * 0.3, rng.NextDouble() * 0.1, rng.Next(1, 60), rng.Next(60, 300), evil);
        world.Link.Conditions = chaos;
        long reconnectAt = long.MaxValue;
        int disconnects = 0;

        void Actions(World w)
        {
            long t = w.T;
            // Network chaos: random outages of 0.5–8 s.
            if (w.Link.Connected && rng.NextDouble() < 0.0008)
            {
                w.Disconnect();
                disconnects++;
                reconnectAt = t + rng.Next(500, 8000);
            }
            else if (!w.Link.Connected && t >= reconnectAt)
            {
                w.Connect();
            }

            foreach (var p in w.Peers)
            {
                long now = p.Local(t);
                double roll = rng.NextDouble();
                if (roll < 0.004)
                {
                    int v = stateVars[rng.Next(stateVars.Length)];
                    double value = toggle.Contains(v) ? 1 - p.Sim.Values[v] : spring.Contains(v) ? 1 : rng.Next(3);
                    p.Sim.UserSet(v, value, now);
                }
                else if (roll < 0.008)
                {
                    int v = analogVars[rng.Next(analogVars.Length)];
                    p.Sim.UserSet(v, Math.Round(rng.NextDouble() * 1000, 2), now);
                }
                else if (roll < 0.011)
                {
                    int v = eventVars[rng.Next(eventVars.Length)];
                    double param = rng.Next(100);
                    p.GeneratedEvents.Add((v, param));
                    p.Engine.OnLocalEvent(v, param, now);
                }
                else if (roll < 0.03 && p.Adapter.IsPilotFlying)
                {
                    int v = controlVars[rng.Next(controlVars.Length)];
                    p.Sim.UserSet(v, Math.Round(rng.NextDouble() * 2 - 1, 3), now);
                }
                else if (roll < 0.0303 && !p.Adapter.IsPilotFlying)
                {
                    if (rng.Next(2) == 0) p.Engine.RequestControl(now);
                    else p.Engine.TakeControl(now);
                }
                w.Drain(p, now);
            }
        }

        world.Run(20_000, Actions);

        // Heal: perfect, connected network; nobody touches anything.
        world.Link.Conditions = LinkConditions.Perfect;
        if (!world.Link.Connected) world.Connect();
        world.Run(int.TryParse(Environment.GetEnvironmentVariable("SKYLINK_HEAL_MS"), out int heal) ? heal : 3000); // plan gate: converged within 3 s of the network healing

        var result = Check(world, schema, spring);
        var (ha, hb) = (world.Peers[0].Engine.Health, world.Peers[1].Engine.Health);
        output?.WriteLine(
            $"seed {seed}: evil={evil} loss={chaos.Loss:P0} dup={chaos.Duplicate:P0} lat={chaos.LatencyMin}-{chaos.LatencyMax} simLat={maxSimLatency} " +
            $"outages={disconnects} handovers={world.Peers[0].Engine.Authority.Epoch} packets={world.Link.PacketsSent} bytes={world.Link.BytesSent} | " +
            $"localChanges={ha.LocalChanges}/{hb.LocalChanges} events={world.Peers[0].GeneratedEvents.Count}/{world.Peers[1].GeneratedEvents.Count} " +
            $"dupEvents={ha.EventDuplicates + hb.EventDuplicates} repairs={ha.SnapshotsSent + hb.SnapshotsSent} stale={ha.StaleIgnored + hb.StaleIgnored} retries={ha.WritesIssued - ha.WritesVerified}/{hb.WritesIssued - hb.WritesVerified}");
        if (assert && result.Failures.Count > 0)
        {
            var details = string.Join("\n", result.Failures.Concat(world.Log.TakeLast(40)));
            Assert.Fail($"seed {seed} (evil={evil}, loss={chaos.Loss:P0}, dup={chaos.Duplicate:P0}, lat={chaos.LatencyMin}-{chaos.LatencyMax}ms, simLat={maxSimLatency}ms):\n{details}");
        }
        return result;
    }

    public sealed record ScenarioResult(List<string> Failures, World World);

    private static ScenarioResult Check(World world, SyncSchema schema, int[] spring)
    {
        var f = new List<string>();
        var (a, b) = (world.Peers[0], world.Peers[1]);

        if (a.Engine.Authority != b.Engine.Authority)
            f.Add($"authority differs: {a.Engine.Authority} vs {b.Engine.Authority}");
        if (a.Adapter.IsPilotFlying == b.Adapter.IsPilotFlying)
            f.Add($"pilot flying: host={a.Adapter.IsPilotFlying} joiner={b.Adapter.IsPilotFlying} (exactly one expected)");

        for (int v = 0; v < schema.Vars.Count; v++)
        {
            var spec = schema.Vars[v];
            if (spec.Class is SyncClass.State or SyncClass.Analog)
            {
                bool ha = a.Engine.TryGetEntry(v, out var ea);
                bool hb = b.Engine.TryGetEntry(v, out var eb);
                if (ha != hb || ea.Value != eb.Value || ea.Stamp != eb.Stamp)
                    f.Add($"store '{spec.Id}': host={ea.Value}@{ea.Stamp} joiner={eb.Value}@{eb.Stamp}");
                if (Math.Abs(a.Sim.Values[v] - b.Sim.Values[v]) > Math.Max(spec.Epsilon, 1e-9))
                    f.Add($"sim '{spec.Id}': host={a.Sim.Values[v]} joiner={b.Sim.Values[v]}");
                if (spring.Contains(v) && (a.Sim.Values[v] != 0 || b.Sim.Values[v] != 0))
                    f.Add($"spring '{spec.Id}' did not return to 0");
            }
            else if (spec.Class == SyncClass.Control)
            {
                var pf = a.Adapter.IsPilotFlying ? a : b;
                var pm = a.Adapter.IsPilotFlying ? b : a;
                if (pf.Sim.Values[v] != pm.Sim.Values[v])
                    f.Add($"control '{spec.Id}': flying={pf.Sim.Values[v]} monitoring={pm.Sim.Values[v]}");
            }
        }

        CheckEvents("host->joiner", a.GeneratedEvents, b.Sim.AppliedEvents, f);
        CheckEvents("joiner->host", b.GeneratedEvents, a.Sim.AppliedEvents, f);

        foreach (var p in world.Peers)
        {
            var h = p.Engine.Health;
            string who = p.Engine.IsHost ? "host" : "joiner";
            if (p.Engine.PendingWriteCount != 0) f.Add($"{who}: {p.Engine.PendingWriteCount} writes still pending");
            if (p.Engine.UnackedEventCount != 0) f.Add($"{who}: {p.Engine.UnackedEventCount} events unacked");
            if (h.WriteRejections != 0) f.Add($"{who}: {h.WriteRejections} write rejections");
            if (p.Engine.IsInitialSync) f.Add($"{who}: still in initial sync");
            if (h.Incompatible) f.Add($"{who}: incompatible: {h.IncompatibleReason}");
        }
        return new ScenarioResult(f, world);
    }

    private static void CheckEvents(string dir, List<(int, double)> sent, List<(int, double)> applied, List<string> f)
    {
        if (sent.SequenceEqual(applied)) return;
        int i = 0;
        while (i < sent.Count && i < applied.Count && sent[i] == applied[i]) i++;
        f.Add($"events {dir}: sent {sent.Count}, applied {applied.Count}, first difference at #{i}");
    }

    /// <summary>
    /// Mutation check: with anti-entropy and event retransmission switched off, the same
    /// scenarios must fail. Proves the harness can actually see desyncs.
    /// </summary>
    [Fact]
    public void Harness_detects_an_engine_without_repair()
    {
        var broken = new EngineOptions
        {
            MaxEventAgeMs = int.MaxValue,
            DigestIntervalMs = int.MaxValue / 4,
            EventRetransmitMs = int.MaxValue / 4,
        };
        int failed = 0;
        for (int seed = 0; seed < 30; seed++)
            if (RunScenario(seed, assert: false, options: broken).Failures.Count > 0) failed++;
        _out.WriteLine($"{failed}/30 runs desynced without repair");
        Assert.True(failed >= 10, $"only {failed}/30 runs failed; the harness is not hostile enough");
    }

    [Fact]
    public void Prints_sample_runs()
    {
        for (int seed = 0; seed < 5; seed++) RunScenario(seed, assert: false, _out);
    }
}
