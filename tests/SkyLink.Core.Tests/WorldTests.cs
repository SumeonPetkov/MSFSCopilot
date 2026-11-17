using SkyLink.Core;

namespace SkyLink.Core.Tests;

/// <summary>The shared world: Captain's traffic → stream → FO's registry of injected aircraft; time/rate/pause follower.</summary>
public sealed class WorldTests
{
    private static TrafficIdentity Id(string title, string atc = "N123", TrafficKind kind = TrafficKind.Airplane, string livery = "") =>
        new(kind, title, livery, atc, "A320", "", "");

    private static FlightSample Pose(double lat, double lon, double hdg = 90, double speedFps = 250) =>
        new(lat, lon, 3000, 0, 0, hdg, speedFps * Math.Sin(hdg * Math.PI / 180), 0, speedFps * Math.Cos(hdg * Math.PI / 180), 0, 0, 0, 0, 0, 0, false);

    /// <summary>A sim that creates objects instantly with sequential ids and records what exists.</summary>
    private sealed class FakeInjector
    {
        public readonly Dictionary<uint, (SpawnTraffic Spawn, FlightSample Pose)> Objects = [];
        public readonly List<SpawnTraffic> Spawns = [];
        public readonly HashSet<string> Rejects = [];
        public int BogusRemovals;
        public bool Manual;
        public readonly List<SpawnTraffic> Pending = [];
        private uint _next = 1000;

        public void Run(TrafficMirror m, long now, FlightSample? user = null)
        {
            var actions = new List<TrafficAction>();
            m.Step(now, user, actions);
            foreach (var a in actions)
            {
                switch (a)
                {
                    case SpawnTraffic s:
                        Spawns.Add(s);
                        if (Manual) Pending.Add(s);
                        else Complete(m, s);
                        break;
                    case MoveTraffic mv:
                        Objects[mv.LocalId] = (Objects[mv.LocalId].Spawn, mv.Pose);
                        break;
                    case RemoveTraffic r:
                        if (!Objects.Remove(r.LocalId)) BogusRemovals++;
                        break;
                }
            }
        }

        public void Complete(TrafficMirror m, SpawnTraffic s)
        {
            if (Rejects.Contains(s.Title))
            {
                m.OnSpawnFailed(s.Token);
                return;
            }
            uint id = _next++;
            Objects[id] = (s, s.Pose);
            m.OnSpawned(s.Token, id);
        }
    }

    private static void Deliver(TrafficSource src, TrafficMirror mirror, long now, Func<Outgoing, bool>? keep = null)
    {
        var sent = new List<Outgoing>();
        src.Tick(now, true, sent.Add);
        foreach (var o in sent)
        {
            var decoded = MessageCodec.Decode(MessageCodec.Encode(o.Message)); // exactly what goes on the wire
            if (o.Channel == Channel.Sequenced && keep is not null && !keep(o)) continue;
            mirror.OnMessage(decoded, now);
        }
    }

    [Fact]
    public void Codec_round_trips_world_messages()
    {
        var pose = new FlightSample(47.123456789, -122.987654321, 3500.5, 1.5, -10.25, 359.5, 100, -5, 200, 0, 0, 0, 0, 0, 0, true);
        var frame = (TrafficFrame)MessageCodec.Decode(MessageCodec.Encode(new TrafficFrame(7, 123456, [new TrafficEntry(42, 15, new TrafficSample(pose, 0x1F, true))])));
        var e = frame.Objects.Single();
        Assert.Equal((7ul, 123456L, 42u, (ushort)15, (ushort)0x1F, true), (frame.Seq, frame.Time, e.Id, e.AgeMs, e.Sample.Lights, e.Sample.GearDown));
        Assert.Equal(pose.LatDeg, e.Sample.Pose.LatDeg);
        Assert.Equal(pose.LonDeg, e.Sample.Pose.LonDeg);
        Assert.Equal(pose.AltFt, e.Sample.Pose.AltFt, 1);
        Assert.Equal(pose.HeadingDeg, e.Sample.Pose.HeadingDeg, 3);
        Assert.True(e.Sample.Pose.OnGround);

        var identity = new TrafficIdentity(TrafficKind.Helicopter, "Airbus H135 ✈ Ödön", "Rettung", "D-HXYZ", "H135", "ADAC", "12");
        var added = (TrafficAdded)MessageCodec.Decode(MessageCodec.Encode(new TrafficAdded([new TrafficInfo(9, identity)])));
        Assert.Equal(identity, added.Objects.Single().Identity);
        var longTitle = new TrafficIdentity(TrafficKind.Airplane, new string('é', 300), "", "", "", "", "");
        var cut = ((TrafficAdded)MessageCodec.Decode(MessageCodec.Encode(new TrafficAdded([new TrafficInfo(1, longTitle)])))).Objects[0].Identity.Title;
        Assert.True(cut.Length is > 100 and <= 128 && cut.All(c => c == 'é'), "long strings are cut on a character boundary");

        Assert.Equal([1u, 2u, 3u], ((TrafficRemoved)MessageCodec.Decode(MessageCodec.Encode(new TrafficRemoved([1, 2, 3])))).Ids);
        Assert.Equal([5u], ((TrafficReset)MessageCodec.Decode(MessageCodec.Encode(new TrafficReset([5])))).LiveIds);
        var ws = new WorldSample(1.8e9, 4, true, 1009.5, 270, 25, -3.5);
        Assert.Equal(ws, ((WorldState)MessageCodec.Decode(MessageCodec.Encode(new WorldState(3, 99, ws)))).Sample);
        Assert.Equal(new WorldNotice(12, true), MessageCodec.Decode(MessageCodec.Encode(new WorldNotice(12, true))));

        // A batch of 16 objects stays far below one UDP packet.
        var big = new TrafficFrame(1, 1, [.. Enumerable.Range(0, 16).Select(i => new TrafficEntry((uint)i, 0, new TrafficSample(pose, 0, false)))]);
        Assert.True(MessageCodec.Encode(big).Length < 1000);
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(MessageCodec.Encode(big).AsSpan(0, 50)));
    }

    [Fact]
    public void Traffic_is_spawned_moved_and_removed()
    {
        var src = new TrafficSource();
        var mirror = new TrafficMirror();
        var sim = new FakeInjector();
        long t = 0;
        var objs = new Dictionary<uint, FlightSample> { [1] = Pose(47, 8), [2] = Pose(47.1, 8.1), [3] = Pose(47.2, 8.2) };
        void Scan() => src.OnScan(TrafficKind.Airplane, [.. objs.Select(kv => (kv.Key, new TrafficSample(kv.Value, 0x2, true)))], t);
        Scan();
        foreach (uint id in objs.Keys) src.OnIdentity(id, Id("A320neo", "N" + id));

        for (; t < 3000; t += 20)
        {
            if (t % 200 == 0)
            {
                foreach (var k in objs.Keys) objs[k] = FlightFollower.Extrapolate(objs[k], 0.2);
                Scan();
            }
            Deliver(src, mirror, t);
            sim.Run(mirror, t);
        }
        Assert.Equal(3, sim.Objects.Count);
        Assert.Equal(3, mirror.Spawned);
        Assert.All(sim.Objects.Values, o => Assert.Equal("A320neo", o.Spawn.Title));
        Assert.Contains(sim.Objects.Values, o => o.Spawn.TailNumber == "N2");
        // Each injected aircraft is where its original was ~buffer ms ago (moving at 76 m/s).
        foreach (var (local, o) in sim.Objects)
        {
            var truth = objs[o.Spawn.RemoteId];
            double d = FlightFollower.DistanceM(truth, o.Pose);
            Assert.InRange(d, 0, 76 * 0.5 + 5);
        }

        objs.Remove(2); // MSFS removed AI aircraft 2
        for (; t < 4000; t += 20)
        {
            if (t % 200 == 0) Scan();
            Deliver(src, mirror, t);
            sim.Run(mirror, t);
        }
        Assert.Equal(2, sim.Objects.Count);
        Assert.DoesNotContain(sim.Objects.Values, o => o.Spawn.RemoteId == 2);

        mirror.Clear(); // session over
        sim.Run(mirror, t);
        Assert.Empty(sim.Objects);
        Assert.Equal(0, sim.BogusRemovals);
        Assert.Empty(mirror.LocalObjects);
    }

    [Fact]
    public void A_spawn_that_lands_after_its_removal_is_removed_at_once()
    {
        var mirror = new TrafficMirror();
        var sim = new FakeInjector { Manual = true };
        mirror.OnMessage(new TrafficAdded([new TrafficInfo(1, Id("C172")), new TrafficInfo(2, Id("C172", "N2"))]), 0);
        mirror.OnMessage(new TrafficFrame(1, 0, [new TrafficEntry(1, 0, new TrafficSample(Pose(1, 1), 0, false)), new TrafficEntry(2, 0, new TrafficSample(Pose(1, 1.01), 0, false))]), 0);
        sim.Run(mirror, 400);
        Assert.Equal(2, sim.Pending.Count);

        mirror.OnMessage(new TrafficRemoved([1]), 500); // gone while MSFS is still creating it
        mirror.Clear();                                   // and then the session ends
        foreach (var s in sim.Pending) sim.Complete(mirror, s);
        sim.Run(mirror, 600);
        Assert.Empty(sim.Objects);
        Assert.Equal(0, sim.BogusRemovals);
        Assert.False(mirror.HasPendingWork);
    }

    [Fact]
    public void A_spawn_that_never_answers_times_out_and_is_removed_if_it_lands_later()
    {
        var mirror = new TrafficMirror(new TrafficOptions { SpawnTimeoutMs = 1000 });
        var sim = new FakeInjector { Manual = true };
        mirror.OnMessage(new TrafficAdded([new TrafficInfo(1, Id("C172"))]), 0);
        mirror.OnMessage(new TrafficFrame(1, 0, [new TrafficEntry(1, 0, new TrafficSample(Pose(1, 1), 0, false))]), 0);
        sim.Run(mirror, 400);
        var lost = sim.Pending.Single();
        sim.Pending.Clear();
        mirror.OnMessage(new TrafficFrame(2, 1500, [new TrafficEntry(1, 0, new TrafficSample(Pose(1, 1), 0, false))]), 1500);
        sim.Run(mirror, 1500); // timed out: try again (same model: the second candidate is the default livery)
        sim.Run(mirror, 1600);
        Assert.Equal(2, sim.Spawns.Count);
        foreach (var s in sim.Pending.ToList()) sim.Complete(mirror, s);
        sim.Complete(mirror, lost); // the first one turns up after all
        sim.Run(mirror, 1700);
        Assert.Single(sim.Objects);
        Assert.Equal(1, mirror.Spawned);
    }

    [Fact]
    public void Uninstalled_models_fall_back_to_a_stand_in()
    {
        var installed = new List<(string, string)> { ("Airbus A320neo V2", "Default"), ("Cessna 172 Skyhawk", "White"), ("Airbus H125", "Red") };
        var c = TrafficModels.Candidates(new TrafficIdentity(TrafficKind.Airplane, "FSLTL A320 Air France", "", "AF123", "A320", "AFR", "123"), installed);
        Assert.Equal(("FSLTL A320 Air France", false), (c[0].Title, c[0].StandIn)); // the exact model is always tried first
        var firstStandIn = c.First(x => x.StandIn);
        Assert.Equal("Airbus A320neo V2", firstStandIn.Title);
        var heli = TrafficModels.Candidates(new TrafficIdentity(TrafficKind.Helicopter, "Bell 407", "", "", "", "", ""), installed);
        Assert.Contains(heli, x => x.Title == "Airbus H125");
        var exact = TrafficModels.Candidates(new TrafficIdentity(TrafficKind.Airplane, "Cessna 172 Skyhawk", "White", "", "", "", ""), installed);
        Assert.Equal(("Cessna 172 Skyhawk", "White", false), exact[0]);

        var mirror = new TrafficMirror();
        mirror.SetInstalledModels(installed);
        var sim = new FakeInjector();
        sim.Rejects.Add("FSLTL A320 Air France"); // not installed here
        sim.Rejects.Add("Airbus A320neo V2");     // and even the best stand-in cannot be created
        mirror.OnMessage(new TrafficAdded([new TrafficInfo(1, new TrafficIdentity(TrafficKind.Airplane, "FSLTL A320 Air France", "", "AF123", "A320", "AFR", "123"))]), 0);
        mirror.OnMessage(new TrafficFrame(1, 0, [new TrafficEntry(1, 0, new TrafficSample(Pose(1, 1), 0, false))]), 0);
        for (long t = 400; t < 1000; t += 50) sim.Run(mirror, t);
        Assert.Single(sim.Objects);
        Assert.NotEqual("Airbus A320neo V2", sim.Objects.Values.Single().Spawn.Title);
        Assert.Equal(1, mirror.StandIns);
    }

    [Fact]
    public void A_model_that_failed_is_not_tried_again_for_every_aircraft()
    {
        var mirror = new TrafficMirror();
        mirror.SetInstalledModels([("Airbus A320neo V2", "Default")]);
        var sim = new FakeInjector();
        sim.Rejects.Add("FSLTL A320 Air France");
        var who = new TrafficIdentity(TrafficKind.Airplane, "FSLTL A320 Air France", "", "AF1", "A320", "", "");
        mirror.OnMessage(new TrafficAdded([.. Enumerable.Range(1, 10).Select(i => new TrafficInfo((uint)i, who with { AtcId = "AF" + i }))]), 0);
        mirror.OnMessage(new TrafficFrame(1, 0, [.. Enumerable.Range(1, 10).Select(i => new TrafficEntry((uint)i, 0, new TrafficSample(Pose(1, 1 + i * 0.01), 0, false)))]), 0);
        for (long t = 400; t < 3000; t += 50) sim.Run(mirror, t);
        Assert.Equal(10, sim.Objects.Count);
        // The uninstalled model was tried (with and without livery) for the first batch in flight only.
        Assert.True(sim.Spawns.Count(s => s.Title == "FSLTL A320 Air France") <= 8, $"{sim.Spawns.Count(s => s.Title == "FSLTL A320 Air France")} attempts");
    }

    [Fact]
    public void A_reused_object_id_replaces_the_aircraft()
    {
        var src = new TrafficSource();
        var mirror = new TrafficMirror();
        var sim = new FakeInjector();
        long t = 0;
        void Step(int ms)
        {
            for (long end = t + ms; t < end; t += 50)
            {
                if (t % 200 == 0) src.OnScan(TrafficKind.Airplane, [(7u, new TrafficSample(Pose(10, 10), 0, false))], t);
                Deliver(src, mirror, t);
                sim.Run(mirror, t);
            }
        }
        src.OnScan(TrafficKind.Airplane, [(7u, new TrafficSample(Pose(10, 10), 0, false))], 0);
        src.OnIdentity(7, Id("Boeing 737", "UAL1"));
        Step(1500);
        Assert.Equal("Boeing 737", sim.Objects.Values.Single().Spawn.Title);
        src.OnIdentity(7, Id("Airbus A321", "DLH2")); // MSFS gave id 7 to a different aircraft
        Step(1500);
        Assert.Equal("Airbus A321", sim.Objects.Values.Single().Spawn.Title);
        Assert.Equal(0, sim.BogusRemovals);
    }

    [Fact]
    public void Reconnecting_keeps_unchanged_traffic_and_drops_what_vanished()
    {
        var src = new TrafficSource();
        var mirror = new TrafficMirror();
        var sim = new FakeInjector();
        var scan = new List<(uint, TrafficSample)> { (1, new TrafficSample(Pose(1, 1), 0, false)), (2, new TrafficSample(Pose(1, 1.1), 0, false)) };
        long t = 0;
        void Run(int ms, bool connected)
        {
            for (long end = t + ms; t < end; t += 50)
            {
                if (t % 200 == 0) src.OnScan(TrafficKind.Airplane, scan, t);
                var sent = new List<Outgoing>();
                src.Tick(t, connected, sent.Add);
                foreach (var o in sent) mirror.OnMessage(o.Message, t);
                sim.Run(mirror, t);
            }
        }
        src.OnScan(TrafficKind.Airplane, scan, 0);
        src.OnIdentity(1, Id("A"));
        src.OnIdentity(2, Id("B"));
        Run(1000, true);
        Assert.Equal(2, sim.Objects.Count);
        int spawns = sim.Spawns.Count;

        scan.RemoveAt(1); // aircraft 2 disappears while the link is down
        Run(1000, false);
        Run(1000, true);
        Assert.Single(sim.Objects);
        Assert.Equal(1u, sim.Objects.Values.Single().Spawn.RemoteId);
        Assert.Equal(spawns, sim.Spawns.Count); // aircraft 1 was not respawned
    }

    [Fact]
    public void Without_frames_from_the_captain_everything_is_removed()
    {
        var mirror = new TrafficMirror(new TrafficOptions { StaleClearMs = 3000 });
        var sim = new FakeInjector();
        mirror.OnMessage(new TrafficAdded([new TrafficInfo(1, Id("C172"))]), 0);
        mirror.OnMessage(new TrafficFrame(1, 0, [new TrafficEntry(1, 0, new TrafficSample(Pose(1, 1), 0, false))]), 0);
        sim.Run(mirror, 500);
        Assert.Single(sim.Objects);
        sim.Run(mirror, 2500);
        Assert.Single(sim.Objects); // a short gap: hold
        sim.Run(mirror, 3100);
        Assert.Empty(sim.Objects);
    }

    [Fact]
    public void Lossy_jittery_delivery_still_tracks_every_aircraft()
    {
        var rng = new Random(3);
        var src = new TrafficSource();
        var mirror = new TrafficMirror();
        var sim = new FakeInjector();
        var truth = Enumerable.Range(1, 30).ToDictionary(i => (uint)i, i => Pose(47 + i * 0.01, 8, rng.Next(360)));
        var history = new List<(long T, Dictionary<uint, FlightSample> P)>();
        var inFlight = new List<(long At, Message M)>();
        long t = 0;
        double worst = 0;
        for (; t < 20_000; t += 10)
        {
            if (t % 200 == 0)
            {
                foreach (var k in truth.Keys) truth[k] = FlightFollower.Extrapolate(truth[k], 0.2);
                history.Add((t, truth.ToDictionary(kv => kv.Key, kv => kv.Value)));
                src.OnScan(TrafficKind.Airplane, [.. truth.Select(kv => (kv.Key, new TrafficSample(kv.Value, 0, false)))], t);
                foreach (var k in truth.Keys) src.OnIdentity(k, Id("A320", "X" + k));
            }
            var sent = new List<Outgoing>();
            src.Tick(t, true, sent.Add);
            foreach (var o in sent)
            {
                if (o.Channel == Channel.Sequenced && rng.NextDouble() < 0.3) continue; // 30 % of pose frames lost
                inFlight.Add((t + rng.Next(20, 150), o.Message)); // and reordered by jitter
            }
            foreach (var m in inFlight.Where(x => x.At <= t).OrderBy(x => x.At).ToList())
            {
                inFlight.Remove(m);
                // Reliable messages must stay in order; only pose frames may be reordered/dropped.
                mirror.OnMessage(m.M, t);
            }
            sim.Run(mirror, t);
            if (t > 5000 && t % 1000 == 0)
            {
                foreach (var (_, o) in sim.Objects)
                {
                    // Compare against where the original was ~350 ms ago (render delay), nearest scan.
                    var past = history.Last(h => h.T <= t - 350 - 100).P[o.Spawn.RemoteId];
                    double err = FlightFollower.DistanceM(past, o.Pose);
                    worst = Math.Max(worst, err);
                }
            }
        }
        Assert.Equal(30, sim.Objects.Count);
        Assert.True(worst < 76 * 0.35, $"worst tracking error {worst:F1} m");
    }

    [Fact]
    public void Only_the_nearest_aircraft_are_mirrored()
    {
        var src = new TrafficSource(new TrafficOptions { MaxObjects = 2 });
        src.OnUserPosition(Pose(0, 0));
        src.OnScan(TrafficKind.Airplane, [(1, new TrafficSample(Pose(0, 0.5), 0, false)), (2, new TrafficSample(Pose(0, 0.1), 0, false)), (3, new TrafficSample(Pose(0, 0.2), 0, false))], 0);
        foreach (uint id in new uint[] { 1, 2, 3 }) src.OnIdentity(id, Id("X" + id));
        var sent = new List<Outgoing>();
        src.Tick(0, true, sent.Add);
        var announced = sent.Select(o => o.Message).OfType<TrafficAdded>().SelectMany(a => a.Objects).Select(o => o.Id).Order().ToArray();
        Assert.Equal([2u, 3u], announced);

        // The far one comes close, a near one leaves: the far-away copy is replaced.
        src.OnUserPosition(Pose(0, 0.5));
        sent.Clear();
        src.Tick(100, true, sent.Add);
        Assert.Contains(sent.Select(o => o.Message).OfType<TrafficRemoved>().SelectMany(r => r.Ids), id => id == 2);
        Assert.Contains(sent.Select(o => o.Message).OfType<TrafficAdded>().SelectMany(a => a.Objects), o => o.Id == 1);
    }

    [Fact]
    public void World_sync_sends_nothing_before_the_cockpit_link_is_established()
    {
        var w = new WorldSync(isSource: true);
        Assert.IsType<SetWorldRole>(w.SimCommands.Dequeue());
        w.OnLocalWorld(new WorldSample(1e9, 1, false, 1013, 0, 0, 15));
        w.Tick(0);
        Assert.Empty(w.Outbox);
        w.SetEstablished(true);
        w.Tick(10);
        Assert.Contains(w.Outbox, o => o.Message is WorldState);
        Assert.Contains(w.Outbox, o => o.Message is TrafficReset);

        var fo = new WorldSync(isSource: false);
        fo.SimCommands.Clear();
        fo.OnMessage(new TrafficAdded([]), 0);
        Assert.Empty(fo.SimCommands); // not established: ignored
        fo.SetEstablished(true);
        fo.OnMessage(new WorldState(2, 0, new WorldSample(1e9, 1, false, 1013, 0, 0, 15)), 0);
        fo.OnMessage(new WorldState(1, 0, new WorldSample(2e9, 1, false, 1013, 0, 0, 15)), 0); // older: dropped
        Assert.Single(fo.SimCommands);
        fo.Stop();
        Assert.Contains(fo.SimCommands, c => c is ClearTraffic);
    }

    // ------------------------------------------------------------------ time, rate, pause, weather

    private static WorldSample W(double zulu, double rate = 1, bool paused = false, double qnh = 1013, double wind = 10) =>
        new(zulu, rate, paused, qnh, 270, wind, 15);

    [Fact]
    public void Zulu_time_is_computed_from_msfs_fields()
    {
        double u = WorldSample.ToUnix(13 * 3600 + 5 * 60 + 7, 32, 2026); // day 32 = 1 February
        Assert.Equal(new DateTime(2026, 2, 1, 13, 5, 7, DateTimeKind.Utc), DateTime.UnixEpoch.AddSeconds(u));
    }

    [Fact]
    public void Pause_follows_each_captain_change_with_bounded_retries()
    {
        var f = new WorldFollower();
        var actions = new List<WorldAction>();
        f.OnLocal(W(1e9), 0);
        f.OnRemote(W(1e9, paused: true), 0);
        for (long t = 0; t < 20_000; t += 100)
        {
            f.OnLocal(W(1e9), t); // this sim refuses to pause
            f.Step(t, actions);
        }
        Assert.Equal(3, actions.OfType<SetPause>().Count(p => p.Paused));

        actions.Clear();
        f.OnRemote(W(1e9, paused: false), 20_000);
        f.Step(20_000, actions);
        Assert.Empty(actions); // already unpaused here

        f.OnRemote(W(1e9, paused: true), 21_000);
        f.Step(21_000, actions);
        Assert.Single(actions.OfType<SetPause>());
    }

    [Fact]
    public void Sim_rate_steps_to_the_captains()
    {
        var f = new WorldFollower();
        var actions = new List<WorldAction>();
        double local = 1;
        f.OnRemote(W(1e9, rate: 8), 0);
        for (long t = 0; t < 10_000; t += 100)
        {
            f.OnLocal(W(1e9, rate: local), t);
            int before = actions.Count;
            f.Step(t, actions);
            foreach (var a in actions.Skip(before).OfType<StepSimRate>()) local *= a.Direction > 0 ? 2 : 0.5;
        }
        Assert.Equal(8, local);
        Assert.Equal(3, actions.Count);
    }

    [Fact]
    public void Sim_rate_waits_for_a_reading_taken_after_each_step()
    {
        // The local rate is only read once a second, and the reading in the same millisecond as a
        // step was taken before it: stepping again on that would overshoot.
        var f = new WorldFollower();
        var actions = new List<WorldAction>();
        double local = 1, reported = 1;
        f.OnRemote(W(1e9, rate: 2), 0);
        for (long t = 0; t < 12_000; t += 100)
        {
            if (t % 1000 == 0)
            {
                reported = local;
                f.OnLocal(W(1e9, rate: reported), t);
            }
            int before = actions.Count;
            f.Step(t, actions);
            foreach (var a in actions.Skip(before).OfType<StepSimRate>()) local *= a.Direction > 0 ? 2 : 0.5;
        }
        Assert.Equal(2, local);
        Assert.Single(actions);
    }

    [Fact]
    public void Time_is_set_only_when_really_different()
    {
        var f = new WorldFollower();
        var actions = new List<WorldAction>();
        double captain = WorldSample.ToUnix(18 * 3600, 100, 2026);
        f.OnRemote(W(captain), 0);
        f.OnLocal(W(captain + 60), 0); // a minute apart: fine
        f.Step(0, actions);
        Assert.Empty(actions);

        f.OnLocal(W(captain - 5 * 3600), 0); // five hours behind
        f.Step(0, actions);
        var set = Assert.IsType<SetZuluTime>(Assert.Single(actions));
        Assert.Equal((2026, 100, 18, 0), (set.Year, set.DayOfYear, set.Hour, set.Minute));
    }

    [Fact]
    public void Weather_mismatch_is_reported_once_it_persists()
    {
        var f = new WorldFollower(new WorldOptions { WeatherSettleMs = 5000 });
        var actions = new List<WorldAction>();
        for (long t = 0; t < 4000; t += 1000)
        {
            f.OnRemote(W(1e9, qnh: 1020), t);
            f.OnLocal(W(1e9, qnh: 1005), t);
            f.Step(t, actions);
        }
        Assert.False(f.WeatherMismatch);
        for (long t = 4000; t < 7000; t += 1000)
        {
            f.OnRemote(W(1e9, qnh: 1020), t);
            f.OnLocal(W(1e9, qnh: 1005), t);
            f.Step(t, actions);
        }
        Assert.True(f.WeatherMismatch);
        Assert.Contains("pressure", f.WeatherDetail);
    }
}
