using SkyLink.Core;

namespace SkyLink.Chaos.Tests;

/// <summary>
/// A stand-in for MSFS: values with write latency, spring-loaded switches that snap back,
/// toggle-only switches (the classic double-flip trap), and an event log.
/// </summary>
public sealed class FakeSim
{
    public const int SpringMs = 400;

    private readonly Random _rng;
    private readonly int _maxLatency;
    private readonly List<(long At, int Var, double Value, bool Toggle)> _writes = [];
    private readonly List<(long At, int Var)> _springs = [];

    public double[] Values { get; }
    public HashSet<int> SpringVars { get; } = [];
    public HashSet<int> ToggleVars { get; } = [];
    public HashSet<int> RejectVars { get; } = [];
    public List<(int Var, double Param)> AppliedEvents { get; } = [];
    public bool Frozen { get; set; }

    public FakeSim(int varCount, Random rng, int maxLatencyMs)
    {
        Values = new double[varCount];
        _rng = rng;
        _maxLatency = maxLatencyMs;
    }

    public void UserSet(int v, double value, long now)
    {
        Values[v] = value;
        OnSet(v, now);
    }

    public void ScheduleWrite(int v, double value, long now)
    {
        if (RejectVars.Contains(v)) return;
        _writes.Add((now + _rng.Next(0, _maxLatency + 1), v, value, false));
    }

    public void ScheduleToggle(int v, long now) =>
        _writes.Add((now + _rng.Next(0, _maxLatency + 1), v, 0, true));

    public void Step(long now)
    {
        for (int i = 0; i < _writes.Count;)
        {
            var w = _writes[i];
            if (w.At > now) { i++; continue; }
            _writes.RemoveAt(i);
            Values[w.Var] = w.Toggle ? 1 - Values[w.Var] : w.Value;
            OnSet(w.Var, now);
        }
        for (int i = 0; i < _springs.Count;)
        {
            var s = _springs[i];
            if (s.At > now) { i++; continue; }
            _springs.RemoveAt(i);
            if (Values[s.Var] == 1) Values[s.Var] = 0;
        }
    }

    private void OnSet(int v, long now)
    {
        if (SpringVars.Contains(v) && Values[v] == 1) _springs.Add((now + SpringMs, v));
    }
}

/// <summary>Executes engine commands against <see cref="FakeSim"/>, like the real SimConnect adapter will.</summary>
/// <summary>
/// A kinematic aircraft: while this seat flies, it holds a constant-rate turn at constant speed;
/// while monitoring, it shows whatever the follower renders. Records every pose for checks.
/// </summary>
public sealed class FakeFlight
{
    public double TurnRateDps { get; set; } = 3;
    public FlightSample Pose { get; set; } = new(47.45, -122.31, 5000, -2, 20, 10,
        253 * Math.Sin(10 * Math.PI / 180), 0, 253 * Math.Cos(10 * Math.PI / 180), 0, 0, 253, 0, 3, 0, false);
    public FlightFollower Follower { get; } = new();
    public bool IsPilotFlying { get; private set; }
    public bool TrackMoves { get; set; }
    public double MaxStepMoveM { get; private set; }
    public List<(long T, FlightSample P)> History { get; } = [];
    public int Handovers { get; private set; }

    public void OnAuthority(bool pilotFlying, long now)
    {
        if (pilotFlying == IsPilotFlying) return;
        IsPilotFlying = pilotFlying;
        Handovers++;
        if (!pilotFlying)
        {
            Follower.Reset();
            Follower.SeedOutput(Pose, now); // ease from where we are into the other seat's stream
        }
    }

    public void Step(long now, int dtMs, SyncEngine engine)
    {
        var before = Pose;
        if (IsPilotFlying)
        {
            Pose = Fly(Pose, dtMs / 1000.0);
            History.Add((now, Pose));
            engine.OnFlightSample(Pose, now);
        }
        else if (Follower.Sample(now) is { } p)
        {
            Pose = p;
        }
        if (TrackMoves) MaxStepMoveM = Math.Max(MaxStepMoveM, FlightFollower.DistanceM(before, Pose));
    }

    private FlightSample Fly(FlightSample s, double dt)
    {
        double speed = Math.Sqrt(s.VWorldEastFps * s.VWorldEastFps + s.VWorldNorthFps * s.VWorldNorthFps);
        double hdg = (s.HeadingDeg + TurnRateDps * dt + 360) % 360;
        var moved = FlightFollower.Extrapolate(s, dt);
        return moved with
        {
            HeadingDeg = hdg,
            VWorldEastFps = speed * Math.Sin(hdg * Math.PI / 180),
            VWorldNorthFps = speed * Math.Cos(hdg * Math.PI / 180),
        };
    }

    /// <summary>The flying seat's pose at one of its own clock times (linear between records).</summary>
    public FlightSample? PoseAt(double t)
    {
        int i = History.FindIndex(h => h.T >= t);
        if (i <= 0) return null;
        var (t0, a) = History[i - 1];
        var (t1, b) = History[i];
        return FlightFollower.Interpolate(a, b, (t1 - t0) / 1000.0, (t - t0) / (t1 - t0));
    }
}

public sealed class FakeAdapter
{
    /// <summary>Never re-fire a toggle within this window: the previous one may still be in flight.</summary>
    public const int ToggleGuardMs = 600;

    private readonly FakeSim _sim;
    private readonly long[] _lastToggle;

    public bool IsPilotFlying { get; private set; }
    public FakeFlight Flight { get; } = new();

    public FakeAdapter(FakeSim sim)
    {
        _sim = sim;
        _lastToggle = new long[sim.Values.Length];
        Array.Fill(_lastToggle, long.MinValue / 2);
    }

    public void Execute(SimCommand c, long now)
    {
        switch (c)
        {
            case WriteVar w when _sim.ToggleVars.Contains(w.Var):
                if (_sim.Values[w.Var] != w.Value && now - _lastToggle[w.Var] >= ToggleGuardMs)
                {
                    _lastToggle[w.Var] = now;
                    _sim.ScheduleToggle(w.Var, now);
                }
                break;
            case WriteVar w:
                _sim.ScheduleWrite(w.Var, w.Value, now);
                break;
            case WriteControl w:
                _sim.Values[w.Var] = w.Value;
                break;
            case FireEvent f:
                _sim.AppliedEvents.Add((f.Var, f.Param));
                break;
            case AuthorityChanged a:
                IsPilotFlying = a.IsPilotFlying;
                _sim.Frozen = !a.IsPilotFlying;
                Flight.OnAuthority(a.IsPilotFlying, now);
                break;
            case FlightStateUpdate u:
                Flight.Follower.Add(u.RemoteTime, u.Sample, now);
                break;
        }
    }
}

public sealed record LinkConditions(double Loss, double Duplicate, int LatencyMin, int LatencyMax, bool Evil)
{
    public static readonly LinkConditions Perfect = new(0, 0, 5, 5, false);
}

/// <summary>
/// A hostile network between two seats. In "realistic" mode the reliable channel behaves like
/// LiteNetLib (in order, no loss) until a disconnect wipes everything in flight. In "evil" mode
/// every channel loses, duplicates and reorders packets.
/// </summary>
public sealed class ChaosLink
{
    private readonly Random _rng;
    private readonly List<Packet> _inflight = [];
    private readonly ulong[] _seqOut = new ulong[2];
    private readonly ulong[] _seqDelivered = new ulong[2];
    private readonly long[] _lastReliableAt = new long[2];
    private long _order;

    public LinkConditions Conditions { get; set; } = LinkConditions.Perfect;
    public bool Connected { get; private set; }
    public long BytesSent { get; private set; }
    public long PacketsSent { get; private set; }

    public ChaosLink(Random rng) => _rng = rng;

    public void SetConnected(bool connected)
    {
        Connected = connected;
        if (!connected) _inflight.Clear();
        Array.Clear(_seqOut);
        Array.Clear(_seqDelivered);
    }

    public void Send(int from, Outgoing o, long now)
    {
        if (!Connected) return;
        byte[] data = MessageCodec.Encode(o.Message);
        BytesSent += data.Length;
        PacketsSent++;
        int to = 1 - from;
        var c = Conditions;
        bool hostile = o.Channel != Channel.ReliableOrdered || c.Evil;
        ulong seq = o.Channel == Channel.Sequenced ? ++_seqOut[from] : 0;

        if (hostile && _rng.NextDouble() < c.Loss) return;
        int copies = hostile && _rng.NextDouble() < c.Duplicate ? 2 : 1;
        for (int i = 0; i < copies; i++)
        {
            long at = now + _rng.Next(c.LatencyMin, c.LatencyMax + 1);
            if (!hostile)
            {
                at = Math.Max(at, _lastReliableAt[to]);
                _lastReliableAt[to] = at;
            }
            _inflight.Add(new Packet(at, _order++, to, o.Channel, seq, data));
        }
    }

    public void Deliver(long now, Action<int, Message> deliver)
    {
        var due = _inflight.Where(p => p.At <= now).OrderBy(p => p.At).ThenBy(p => p.Order).ToList();
        foreach (var p in due)
        {
            _inflight.Remove(p);
            if (p.Channel == Channel.Sequenced)
            {
                if (p.Seq <= _seqDelivered[p.To]) continue;
                _seqDelivered[p.To] = p.Seq;
            }
            deliver(p.To, MessageCodec.Decode(p.Data));
            if (!Connected) return;
        }
    }

    private sealed record Packet(long At, long Order, int To, Channel Channel, ulong Seq, byte[] Data);
}

public sealed class Peer
{
    public SyncEngine Engine { get; }
    public FakeSim Sim { get; }
    public FakeAdapter Adapter { get; }
    public long Skew { get; }
    public int SamplePhase { get; }
    public List<(int Var, double Param)> GeneratedEvents { get; } = [];

    public Peer(SyncEngine engine, FakeSim sim, long skew, int samplePhase)
    {
        Engine = engine;
        Sim = sim;
        Adapter = new FakeAdapter(sim);
        Skew = skew;
        SamplePhase = samplePhase;
    }

    public long Local(long t) => t + Skew;
}

/// <summary>Drives two peers over a <see cref="ChaosLink"/> on a virtual clock.</summary>
public sealed class World
{
    public const int StepMs = 5;
    public const int SampleMs = 30;

    private readonly SyncSchema _schema;
    private readonly EngineOptions? _options;
    private ulong _nextInstance = 0x1000;

    public Peer[] Peers { get; }
    public ChaosLink Link { get; }
    public long T { get; private set; } = 100_000;
    public bool FlightEnabled { get; set; }
    public List<string> Log { get; } = [];

    public World(SyncSchema schema, Random rng, int maxSimLatency, long skewA, long skewB, EngineOptions? options = null)
    {
        _schema = schema;
        _options = options;
        Link = new ChaosLink(rng);
        var host = new SyncEngine(schema, 0, true, 0xA11CE, 0x5E55, options);
        var join = new SyncEngine(schema, 1, false, 0xB0B, 0, options);
        Peers =
        [
            new Peer(host, new FakeSim(schema.Vars.Count, new Random(rng.Next()), maxSimLatency), skewA, 0),
            new Peer(join, new FakeSim(schema.Vars.Count, new Random(rng.Next()), maxSimLatency), skewB, 15),
        ];
        foreach (var p in Peers)
        {
            p.Engine.Log = s => Log.Add($"[{T}] {s}");
            p.Engine.Start();
            Drain(p, p.Local(T));
        }
    }

    /// <summary>
    /// Simulate the SkyLink app on one seat crashing and being relaunched: a fresh engine (new
    /// instance id; a restarted host also creates a new session) attached to the same sim.
    /// </summary>
    public void Restart(int index)
    {
        var old = Peers[index];
        bool host = old.Engine.IsHost;
        // A relaunch takes seconds; whatever the dead process already sent to the sim has landed.
        old.Sim.Step(old.Local(T) + 2000);
        var e = new SyncEngine(_schema, old.Engine.Seat, host, ++_nextInstance, host ? ++_nextInstance : 0, _options);
        var p = new Peer(e, old.Sim, old.Skew, old.SamplePhase);
        Peers[index] = p;
        Log.Add($"[{T}] RESTART seat {e.Seat}");
        e.Log = s => Log.Add($"[{T}] {s}");
        e.Start();
        Drain(p, p.Local(T));
        if (Link.Connected)
        {
            Disconnect();
            Connect();
        }
    }

    public void Connect()
    {
        Log.Add($"[{T}] LINK UP");
        Link.SetConnected(true);
        foreach (var p in Peers)
        {
            p.Engine.OnConnected(p.Local(T));
            Drain(p, p.Local(T));
        }
    }

    public void Disconnect()
    {
        Log.Add($"[{T}] LINK DOWN");
        Link.SetConnected(false);
        foreach (var p in Peers) p.Engine.OnDisconnected(p.Local(T));
    }

    public void Step(Action<World>? actions = null)
    {
        T += StepMs;
        Link.Deliver(T, (to, m) =>
        {
            var p = Peers[to];
            p.Engine.OnMessage(m, p.Local(T));
        });
        foreach (var p in Peers)
        {
            long now = p.Local(T);
            p.Sim.Step(now);
            if (FlightEnabled) p.Adapter.Flight.Step(now, StepMs, p.Engine);
            if ((T + p.SamplePhase) % SampleMs < StepMs)
                for (int v = 0; v < _schema.Vars.Count; v++)
                    if (_schema.Vars[v].Class != SyncClass.Event)
                        p.Engine.OnSimSample(v, p.Sim.Values[v], now);
            p.Engine.Tick(now);
            Drain(p, now);
        }
        actions?.Invoke(this);
    }

    public void Run(int ms, Action<World>? actions = null)
    {
        for (int i = 0; i < ms / StepMs; i++) Step(actions);
    }

    public void Drain(Peer p, long now)
    {
        int from = Array.IndexOf(Peers, p);
        while (p.Engine.SimCommands.TryDequeue(out var c)) p.Adapter.Execute(c, now);
        while (p.Engine.Outbox.TryDequeue(out var o)) Link.Send(from, o, T); // network runs on global time
    }
}
