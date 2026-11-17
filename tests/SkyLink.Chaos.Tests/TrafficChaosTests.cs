using SkyLink.Core;
using Xunit.Abstractions;

namespace SkyLink.Chaos.Tests;

/// <summary>
/// The Captain's AI traffic mirrored into the First Officer's sim under hostile conditions:
/// aircraft appear and vanish, MSFS reuses object ids, the nearest-N selection churns, pose
/// frames are lost/duplicated/reordered, reliable messages are delayed, the link drops (briefly
/// and for longer than the stale timeout), spawns are slow, fail for uninstalled models, or
/// time out and land late, and MSFS deletes injected objects by itself. Requires: once things
/// calm down the FO shows exactly the Captain's aircraft, each once, close to where they are; and
/// after the session ends, not a single injected object is left.
/// </summary>
public sealed class TrafficChaosTests(ITestOutputHelper output)
{
    public static IEnumerable<object[]> Seeds()
    {
        // SKYLINK_TRAFFIC_SEEDS=12,34 replays just those seeds.
        if (Environment.GetEnvironmentVariable("SKYLINK_TRAFFIC_SEEDS") is { Length: > 0 } list) { foreach (var x in list.Split(",")) yield return [int.Parse(x)]; yield break; }
        int runs = int.TryParse(Environment.GetEnvironmentVariable("SKYLINK_CHAOS_RUNS"), out int r) ? Math.Max(1, r / 20) : 30;
        for (int i = 0; i < runs; i++) yield return [i];
    }

    private sealed class Ai
    {
        public required TrafficIdentity Identity;
        public FlightSample Pose;
        public readonly List<(long T, FlightSample P)> History = [];
    }

    private sealed class Injected
    {
        public required SpawnTraffic Spawn;
        public FlightSample Pose;
    }

    private static readonly string[] Installed = ["Airbus A320neo V2", "Boeing 737 MAX 8", "Cessna 172 Skyhawk", "Airbus H125"];
    private static readonly string[] Titles = ["Airbus A320neo V2", "Boeing 737 MAX 8", "FSLTL B738 Ryanair", "Cessna 172 Skyhawk", "AIG A320 Lufthansa", "Bell 407 Medevac"];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Fo_world_converges_to_the_captains_and_leaves_no_ghosts(int seed)
    {
        var rng = new Random(seed);
        var options = new TrafficOptions { MaxObjects = rng.Next(2) == 0 ? 120 : rng.Next(5, 20), StaleClearMs = 8000, SpawnTimeoutMs = 3000, FailedRetryMs = 5000 };
        var src = new TrafficSource(options);
        var mirror = new TrafficMirror(options);
        mirror.SetInstalledModels([.. Installed.Select(t => (t, "Default"))]);

        var ai = new Dictionary<uint, Ai>();
        uint nextAi = 1;
        var user = new FlightSample(47, 8, 5000, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, false);
        double loss = rng.NextDouble() * 0.4, dup = rng.NextDouble() * 0.1;
        int latMin = rng.Next(5, 40), latMax = latMin + rng.Next(0, 200);
        long skew = rng.Next(-5000, 5000); // the two PCs' clocks differ

        var injected = new Dictionary<uint, Injected>();
        uint nextLocal = 50_000;
        var spawning = new List<(long At, SpawnTraffic S, bool Ok, bool Lost)>();
        int bogusRemovals = 0;

        var reliable = new List<(long At, Message M)>();
        var sequenced = new List<(long At, Message M)>();
        long newestSequencedSent = -1, newestSequencedDelivered = -1;
        var seqStamp = new Dictionary<Message, long>(ReferenceEqualityComparer.Instance);
        bool connected = true;
        bool sessionOver = false;
        long nextOutage = rng.Next(3000, 15000), outageEnds = -1;

        TrafficIdentity NewIdentity()
        {
            string title = Titles[rng.Next(Titles.Length)];
            var kind = title.StartsWith("Bell") ? TrafficKind.Helicopter : TrafficKind.Airplane;
            return new TrafficIdentity(kind, title, rng.Next(2) == 0 ? "Default" : "Other", "N" + rng.Next(1000, 9999), "", "", "");
        }

        void AddAi(long t)
        {
            double brg = rng.NextDouble() * 2 * Math.PI, dist = rng.Next(500, 60_000);
            double hdg = rng.Next(360), spd = rng.Next(0, 400);
            var p = new FlightSample(user.LatDeg + dist * Math.Cos(brg) / 111_000, user.LonDeg + dist * Math.Sin(brg) / 75_000, rng.Next(0, 30_000), 0, 0, hdg,
                spd * Math.Sin(hdg * Math.PI / 180), rng.Next(-20, 20), spd * Math.Cos(hdg * Math.PI / 180), 0, 0, 0, 0, 0, 0, spd == 0);
            ai[nextAi++] = new Ai { Identity = NewIdentity(), Pose = p };
        }

        for (int i = rng.Next(3, 40); i > 0; i--) AddAi(0);
        var identified = new HashSet<uint>(); // ids in the previous scan whose identity was asked for
        long t = 0;
        const int Dt = 10;

        void Step(bool chaos)
        {
            t += Dt;
            long foT = t + skew;

            // --- the Captain's world moves; now and then aircraft come and go, ids get reused
            foreach (var a in ai.Values)
            {
                a.Pose = FlightFollower.Extrapolate(a.Pose, Dt / 1000.0);
                a.History.Add((t, a.Pose));
                if (a.History.Count > 600) a.History.RemoveRange(0, 300);
            }
            if (chaos && rng.Next(400) == 0) AddAi(t);
            if (chaos && rng.Next(500) == 0 && ai.Count > 0) ai.Remove(ai.Keys.ElementAt(rng.Next(ai.Count)));
            if (chaos && rng.Next(1500) == 0 && ai.Count > 0)
            {
                var k = ai.Keys.ElementAt(rng.Next(ai.Count));
                ai[k] = new Ai { Identity = NewIdentity(), Pose = ai[k].Pose }; // MSFS reused the id
                identified.Remove(k);
            }
            if (chaos && rng.Next(300) == 0) user = user with { LatDeg = user.LatDeg + (rng.NextDouble() - 0.5) * 0.3 }; // selection churn

            if (t % 200 == 0)
            {
                src.OnUserPosition(user);
                src.OnScan(TrafficKind.Airplane, [.. ai.Where(kv => kv.Value.Identity.Kind == TrafficKind.Airplane).Select(kv => (kv.Key, new TrafficSample(kv.Value.Pose, 0, false)))], t);
                src.OnScan(TrafficKind.Helicopter, [.. ai.Where(kv => kv.Value.Identity.Kind == TrafficKind.Helicopter).Select(kv => (kv.Key, new TrafficSample(kv.Value.Pose, 0, false)))], t);
                // Like the bridge: ask what an aircraft is when its id (re)appears in a scan.
                foreach (var (id, a) in ai)
                    if (identified.Add(id)) src.OnIdentity(id, a.Identity);
                identified.IntersectWith(ai.Keys);
            }

            // --- the link
            if (chaos && outageEnds < 0 && t >= nextOutage)
            {
                connected = false;
                outageEnds = t + (rng.Next(3) == 0 ? rng.Next(9000, 14000) : rng.Next(200, 3000));
                reliable.Clear();   // a new connection: nothing in flight survives
                sequenced.Clear();
            }
            if (outageEnds >= 0 && t >= outageEnds)
            {
                connected = true;
                outageEnds = -1;
                nextOutage = t + rng.Next(5000, 20000);
                newestSequencedDelivered = -1;
            }
            src.Tick(t, connected, o =>
            {
                var m = MessageCodec.Decode(MessageCodec.Encode(o.Message));
                if (o.Channel == Channel.ReliableOrdered)
                {
                    long at = Math.Max(t + rng.Next(latMin, latMax + 1), reliable.Count > 0 ? reliable[^1].At : 0);
                    reliable.Add((at, m));
                    return;
                }
                seqStamp[m] = ++newestSequencedSent;
                if (chaos && rng.NextDouble() < loss) return;
                sequenced.Add((t + rng.Next(latMin, latMax + 1), m));
                if (chaos && rng.NextDouble() < dup) sequenced.Add((t + rng.Next(latMin, latMax + 1), m));
            });
            while (reliable.Count > 0 && reliable[0].At <= t)
            {
                if (!sessionOver) mirror.OnMessage(reliable[0].M, foT);
                reliable.RemoveAt(0);
            }
            foreach (var d in sequenced.Where(x => x.At <= t).OrderBy(x => x.At).ToList())
            {
                sequenced.Remove(d);
                long stamp = seqStamp[d.M];
                if (stamp <= newestSequencedDelivered) continue; // LiteNetLib Sequenced: older than the newest is dropped
                newestSequencedDelivered = stamp;
                if (!sessionOver) mirror.OnMessage(d.M, foT);
            }

            // --- the First Officer's sim
            foreach (var s in spawning.Where(x => x.At <= foT).ToList())
            {
                spawning.Remove(s);
                if (!s.Ok)
                {
                    mirror.OnSpawnFailed(s.S.Token);
                    continue;
                }
                uint local = nextLocal++;
                injected[local] = new Injected { Spawn = s.S, Pose = s.S.Pose };
                mirror.OnSpawned(s.S.Token, local);
            }
            if (chaos && rng.Next(3000) == 0 && injected.Count > 0)
            {
                uint gone = injected.Keys.ElementAt(rng.Next(injected.Count)); // e.g. MSFS reloaded something
                injected.Remove(gone);
                mirror.OnLocalObjectGone(gone);
            }
            var actions = new List<TrafficAction>();
            mirror.Step(foT, user, actions);
            foreach (var a in actions)
            {
                switch (a)
                {
                    case SpawnTraffic s:
                        bool ok = Installed.Contains(s.Title);
                        bool slow = chaos && rng.Next(20) == 0; // answers only after the mirror gave up
                        spawning.Add((foT + (slow ? options.SpawnTimeoutMs + rng.Next(100, 2000) : rng.Next(0, 800)), s, ok, slow));
                        break;
                    case MoveTraffic mv:
                        if (injected.TryGetValue(mv.LocalId, out var o)) o.Pose = mv.Pose;
                        break;
                    case RemoveTraffic r:
                        if (!injected.Remove(r.LocalId)) bogusRemovals++;
                        break;
                }
            }
        }

        for (int i = 0; i < 4000; i++) Step(chaos: true); // 40 s of chaos
        while (!connected || spawning.Count > 0) Step(chaos: false);
        for (int i = 0; i < 700; i++) Step(chaos: false); // 7 s calm
        // Aircraft near the nearest-N cut-off swap in and out as they move: require those that were
        // selected throughout the last 3 s, allow those selected at any moment of it.
        HashSet<uint>? stable = null;
        var recent = new HashSet<uint>();
        for (int i = 0; i < 300; i++)
        {
            Step(chaos: false);
            if (i % 20 != 0) continue;
            var sel = Select(ai, user, options.MaxObjects);
            if (stable is null) stable = [.. sel];
            else stable.IntersectWith(sel);
            recent.UnionWith(sel);
        }
        recent.UnionWith(Select(ai, user, options.MaxObjects));

        // The FO shows exactly the Captain's selected aircraft, each once.
        var expected = stable!;
        var shown = injected.Values.GroupBy(o => o.Spawn.RemoteId).ToDictionary(g => g.Key, g => g.ToList());
        var dupes = shown.Where(kv => kv.Value.Count > 1).Select(kv => kv.Key).ToList();
        var missing = expected.Where(id => !shown.ContainsKey(id)).ToList();
        var extra = shown.Keys.Where(id => !recent.Contains(id)).ToList();
        Assert.True(dupes.Count == 0 && missing.Count == 0 && extra.Count == 0,
            $"seed {seed}: duplicates [{string.Join(",", dupes)}] missing [{string.Join("; ", missing.Select(id => $"{id}: {mirror.Describe(id)} / source: {src.Describe(id)}"))}] extra [{string.Join(",", extra)}] of {expected.Count}");

        // ...with the right model (or a stand-in when it is not installed), near where it really is.
        double worst = 0;
        foreach (var (id, list) in shown)
        {
            var o = list.Single();
            var a = ai[id];
            Assert.True(o.Spawn.Title == a.Identity.Title || (!Installed.Contains(a.Identity.Title) && o.Spawn.StandIn), $"seed {seed}: {id} shown as '{o.Spawn.Title}', is '{a.Identity.Title}'");
            // Rendered ~BufferMs behind; allow for that at its speed plus a margin.
            double speed = Math.Sqrt(a.Pose.VWorldEastFps * a.Pose.VWorldEastFps + a.Pose.VWorldNorthFps * a.Pose.VWorldNorthFps + a.Pose.VWorldUpFps * a.Pose.VWorldUpFps) * 0.3048;
            double err = a.History.Select(h => FlightFollower.DistanceM(h.P, o.Pose)).Min();
            worst = Math.Max(worst, err);
            Assert.True(err < 5 + speed * 0.05, $"seed {seed}: {id} is {err:F1} m off its track");
        }
        Assert.Equal(0, bogusRemovals);

        // Session over: everything goes, including spawns that land afterwards.
        mirror.Clear();
        sessionOver = true;
        for (int i = 0; i < 600; i++) Step(chaos: false);
        Assert.True(injected.Count == 0, $"seed {seed}: {injected.Count} ghost(s) left after the session ended");
        Assert.Empty(mirror.LocalObjects);
        Assert.Equal(0, bogusRemovals);
        if (seed == 0) output.WriteLine($"seed 0: {expected.Count} aircraft mirrored, worst track error {worst:F2} m, {src.FramesSent} frames");
    }

    private static HashSet<uint> Select(Dictionary<uint, Ai> ai, FlightSample user, int max) =>
        [.. ai.OrderBy(kv => FlightFollower.DistanceM(user, kv.Value.Pose)).ThenBy(kv => kv.Key).Take(max).Select(kv => kv.Key)];
}
