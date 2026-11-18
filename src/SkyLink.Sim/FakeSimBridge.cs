using SkyLink.Core;
using SkyLink.Definitions;

namespace SkyLink.Sim;

/// <summary>
/// A simulated cockpit for testing without MSFS: values with write latency, K: events that
/// move the variable they set, and an optional "fake pilot" that flips switches, turns knobs
/// and presses buttons. Used by <c>skylink host/join --fake-sim</c> and the integration tests.
/// </summary>
public sealed class FakeSimBridge : SimBridgeBase, IFlightSim
{
    private readonly Random _rng;
    private readonly int _latencyMs;
    private readonly List<(long At, int Var, double Value, bool Toggle)> _pending = [];
    private readonly double[] _values;
    private readonly Dictionary<string, List<(int Var, Setter Setter)>> _byEvent = new(StringComparer.OrdinalIgnoreCase);
    private long _now;
    private long _nextPilotAction;

    public bool FakePilot { get; set; }
    public int FakePilotIntervalMs { get; set; } = 3000;
    public List<(int Var, double Param)> AppliedEvents { get; } = [];
    public List<(int Var, double Param)> PressedEvents { get; } = [];

    /// <summary>The fake aircraft: a constant-rate turn while flying, the followed pose while monitoring.</summary>
    public FlightSample Pose { get; set; } = new(47.45, -122.31, 5000, -2, 20, 10,
        253 * Math.Sin(10 * Math.PI / 180), 0, 253 * Math.Cos(10 * Math.PI / 180), 0, 0, 253, 0, 3, 0, false);
    public double TurnRateDps { get; set; } = 3;
    public bool TrackMoves { get; set; }
    /// <summary>Largest move between two pumps, divided by the time between them (m/s).</summary>
    public double MaxApparentSpeedMps { get; private set; }
    public List<(long T, FlightSample P)> FlownHistory { get; } = [];
    private long _lastPoseAt = -1;

    public FakeSimBridge(AircraftDefinition definition, int seed = 0, int latencyMs = 30)
    {
        _rng = new Random(seed);
        _latencyMs = latencyMs;
        _values = new double[definition.Vars.Count];
        for (int v = 0; v < definition.Vars.Count; v++)
            foreach (var s in definition.Vars[v].Write)
                if (s.Kind is SetterKind.KEvent or SetterKind.BEvent or SetterKind.IncDec)
                {
                    foreach (string name in s.DecEvent is null ? [s.Target] : new[] { s.Target, s.DecEvent })
                    {
                        if (!_byEvent.TryGetValue(name, out var list)) _byEvent[name] = list = [];
                        list.Add((v, s));
                    }
                }
        SetDefinition(new MatchResult(definition, "fake sim", definition.IsFallback));
    }

    public override string Status => $"fake sim: {Definition!.Name}";

    // ---- flight setup (IFlightSim): a pretend MSFS with an installed-aircraft list

    public List<(string Title, string Livery)> InstalledAircraft { get; set; } =
    [
        ("A320neo V2", "Default"), ("A320neo V2", "Lufthansa"), ("A320neo V2", "easyJet"),
        ("Cessna 172 Skyhawk G1000", "White"), ("Cessna 172 Skyhawk G1000", "Blue Stripe"),
        ("Boeing 737 MAX 8", "Default"),
    ];

    public bool IsConnected => true;
    public bool IsInFlight { get; private set; } = true;
    public string? AircraftTitle { get; private set; } = "Cessna 172 Skyhawk G1000";
    public string ModuleStatus => "fake";
    public bool IsFollowing => !IsPilotFlying && Follower.HasData;
    public string? LastFlightLoaded { get; private set; }
    public string? LoadedFlight { get; private set; }
    public int LoadActivity { get; private set; }

    /// <summary>Like MSFS still booting: this many load requests are ignored without any error.</summary>
    public int IgnoreLoads { get; set; }
    public string? LastPlanLoaded { get; private set; }
    public (string? AtcId, string? Airline, string? FlightNumber) Callsign { get; private set; }
    public event Action<List<(string Title, string Livery)>>? AircraftCatalog;
    private long _flightLoadedAt = -1;
    private string? _loadingTitle;

    public bool RequestAircraftCatalog()
    {
        AircraftCatalog?.Invoke([.. InstalledAircraft]);
        return true;
    }

    public bool LoadFlight(string fltPath)
    {
        LastFlightLoaded = fltPath;
        if (IgnoreLoads > 0) { IgnoreLoads--; return true; }
        // Read the aircraft and start point back out of the .flt, as MSFS would.
        string[] lines = File.ReadAllLines(fltPath, System.Text.Encoding.Latin1);
        _loadingTitle = lines.FirstOrDefault(l => l.StartsWith("Sim="))?[4..];
        IsInFlight = false;
        _flightLoadedAt = _now;
        LoadActivity++;
        return true;
    }

    public bool LoadFlightPlan(string plnPath)
    {
        LastPlanLoaded = plnPath;
        return true;
    }

    public void SetCallsign(string? atcId, string? airline, string? flightNumber) => Callsign = (atcId, airline, flightNumber);

    public double Get(int v) => _values[v];

    /// <summary>The local pilot moves something in the cockpit.</summary>
    public void UserSet(int v, double value) => _values[v] = value;

    /// <summary>The local pilot presses a button.</summary>
    public void UserPress(int v, double param)
    {
        PressedEvents.Add((v, param));
        OnCapturedEvent(v, param, _now);
    }

    public override void Pump(long now)
    {
        _now = now;
        if (_flightLoadedAt >= 0 && now - _flightLoadedAt >= 1500)
        {
            // The fake "loading screen" is over.
            _flightLoadedAt = -1;
            AircraftTitle = _loadingTitle ?? AircraftTitle;
            LoadedFlight = LastFlightLoaded;
            LoadActivity++;
            IsInFlight = true;
        }
        for (int i = 0; i < _pending.Count;)
        {
            var p = _pending[i];
            if (p.At > now) { i++; continue; }
            _pending.RemoveAt(i);
            _values[p.Var] = p.Toggle ? (_values[p.Var] == 0 ? 1 : 0) : p.Value;
        }
        if (FakePilot && now >= _nextPilotAction)
        {
            _nextPilotAction = now + _rng.Next(FakePilotIntervalMs / 2, FakePilotIntervalMs * 3 / 2);
            ActLikeAPilot();
        }
        for (int v = 0; v < _values.Length; v++)
            if (Definition!.Vars[v].Spec.Class != SyncClass.Event) OnValue(v, _values[v], now);
        StepFlight(now);
        StepWorldSim(now);
    }

    private void StepFlight(long now)
    {
        if (_lastPoseAt < 0) _lastPoseAt = now;
        double dt = (now - _lastPoseAt) / 1000.0;
        if (dt <= 0) return;
        var before = Pose;
        if (IsPilotFlying)
        {
            double speed = Math.Sqrt(Pose.VWorldEastFps * Pose.VWorldEastFps + Pose.VWorldNorthFps * Pose.VWorldNorthFps);
            double hdg = (Pose.HeadingDeg + TurnRateDps * dt + 360) % 360;
            Pose = FlightFollower.Extrapolate(Pose, dt) with
            {
                HeadingDeg = hdg,
                VWorldEastFps = speed * Math.Sin(hdg * Math.PI / 180),
                VWorldNorthFps = speed * Math.Cos(hdg * Math.PI / 180),
            };
            FlownHistory.Add((now, Pose));
        }
        else if (Follower.Sample(now) is { } p)
        {
            Pose = p;
        }
        if (TrackMoves) MaxApparentSpeedMps = Math.Max(MaxApparentSpeedMps, FlightFollower.DistanceM(before, Pose) / dt);
        _lastPoseAt = now;
        OnFlightRead(Pose);
    }

    // ---- shared world: a pretend sim with AI traffic (as the Captain) or injected copies (as the FO)

    public sealed class FakeAi
    {
        public required TrafficIdentity Identity;
        public FlightSample Pose;
        public double TurnRateDps;
        public ushort Lights;
        public bool GearDown;
        public List<(long T, FlightSample P)> History { get; } = [];
    }

    public sealed class Injected
    {
        public required SpawnTraffic Spawn;
        public FlightSample Pose;
        public ushort Lights;
        public bool GearDown;
        public int Moves;
    }

    /// <summary>AI aircraft flying in this fake sim (the Captain's MSFS traffic).</summary>
    public Dictionary<uint, FakeAi> AiTraffic { get; } = [];
    /// <summary>Objects SkyLink created in this fake sim (the FO's mirrored traffic).</summary>
    public Dictionary<uint, Injected> InjectedTraffic { get; } = [];
    public int SpawnsRequested { get; private set; }
    public int SpawnsFailed { get; private set; }
    public int Removals { get; private set; }
    /// <summary>Removals of ids that were not (or no longer) injected: a bookkeeping bug if ever non-zero.</summary>
    public int BogusRemovals { get; private set; }
    public int TrafficScanIntervalMs { get; set; } = 200;
    /// <summary>Spawns only succeed for installed titles (<see cref="InstalledAircraft"/>), like MSFS.</summary>
    public bool RequireInstalledModels { get; set; } = true;
    /// <summary>The FO forgot to turn MSFS traffic off: this many AI aircraft SkyLink did not create.</summary>
    public int ForeignAi { get; set; }

    public WorldSample WorldNow { get; set; } = new(WorldSample.ToUnix(12 * 3600, 200, 2026), 1, false, 1013, 270, 10, 15);
    public List<WorldAction> AppliedWorldActions { get; } = [];

    private readonly List<(long At, SpawnTraffic Spawn, bool Ok)> _spawning = [];
    private uint _nextAiId = 100;
    private uint _nextLocalId = 10_000;
    private long _lastScan = long.MinValue / 2;
    private long _lastWorldRead = long.MinValue / 2;
    private readonly HashSet<uint> _identified = [];
    private long _lastWorldStep = -1;

    public uint AddAi(TrafficIdentity identity, FlightSample pose, double turnRateDps = 0)
    {
        uint id = _nextAiId++;
        AiTraffic[id] = new FakeAi { Identity = identity, Pose = pose, TurnRateDps = turnRateDps };
        return id;
    }

    /// <summary>MSFS reuses object ids: put a different aircraft under an existing id.</summary>
    public void ReplaceAi(uint id, TrafficIdentity identity)
    {
        AiTraffic[id].Identity = identity;
        _identified.Remove(id);
    }

    protected override void OnWorldRoleChanged(WorldRole old, WorldRole role, long now)
    {
        if (role == WorldRole.Mirror) Mirror.SetInstalledModels(RequireInstalledModels ? InstalledAircraft : null);
        _identified.Clear();
    }

    private void StepWorldSim(long now)
    {
        if (_lastWorldStep < 0) _lastWorldStep = now;
        double dt = (now - _lastWorldStep) / 1000.0;
        _lastWorldStep = now;
        if (!WorldNow.Paused) WorldNow = WorldNow with { ZuluUnixSec = WorldNow.ZuluUnixSec + dt * WorldNow.SimRate };

        foreach (var ai in AiTraffic.Values)
        {
            if (dt <= 0 || WorldNow.Paused) continue;
            double speed = Math.Sqrt(ai.Pose.VWorldEastFps * ai.Pose.VWorldEastFps + ai.Pose.VWorldNorthFps * ai.Pose.VWorldNorthFps);
            double hdg = (ai.Pose.HeadingDeg + ai.TurnRateDps * dt + 360) % 360;
            ai.Pose = FlightFollower.Extrapolate(ai.Pose, dt) with
            {
                HeadingDeg = hdg,
                VWorldEastFps = speed * Math.Sin(hdg * Math.PI / 180),
                VWorldNorthFps = speed * Math.Cos(hdg * Math.PI / 180),
            };
            ai.History.Add((now, ai.Pose));
            if (ai.History.Count > 2000) ai.History.RemoveRange(0, 1000);
        }

        if (now - _lastWorldRead >= 1000)
        {
            _lastWorldRead = now;
            OnWorldRead(WorldNow, now);
        }

        if (WorldRole == WorldRole.Source && now - _lastScan >= TrafficScanIntervalMs)
        {
            _lastScan = now;
            foreach (var kind in new[] { TrafficKind.Airplane, TrafficKind.Helicopter })
                OnTrafficScan(kind, [.. AiTraffic.Where(kv => kv.Value.Identity.Kind == kind)
                    .Select(kv => (kv.Key, new TrafficSample(kv.Value.Pose, kv.Value.Lights, kv.Value.GearDown)))], now);
            // Like the real bridge: identify an aircraft when its id (re)appears.
            foreach (var (id, ai) in AiTraffic)
                if (_identified.Add(id)) OnTrafficIdentity(id, ai.Identity);
            _identified.IntersectWith(AiTraffic.Keys);
            TrafficSeen = AiTraffic.Count;
        }

        for (int i = 0; i < _spawning.Count;)
        {
            var (at, spawn, ok) = _spawning[i];
            if (at > now) { i++; continue; }
            _spawning.RemoveAt(i);
            if (!ok)
            {
                SpawnsFailed++;
                Mirror.OnSpawnFailed(spawn.Token);
                continue;
            }
            uint local = _nextLocalId++;
            InjectedTraffic[local] = new Injected { Spawn = spawn, Pose = spawn.Pose };
            Mirror.OnSpawned(spawn.Token, local);
        }
        ForeignTraffic = WorldRole == WorldRole.Mirror ? ForeignAi : 0;
        StepWorld(now);
    }

    protected override void SpawnTrafficObject(SpawnTraffic spawn, long now)
    {
        SpawnsRequested++;
        bool installed = !RequireInstalledModels || InstalledAircraft.Any(m => m.Title == spawn.Title && (spawn.Livery.Length == 0 || m.Livery == spawn.Livery));
        _spawning.Add((now + _rng.Next(20, 300), spawn, installed));
    }

    protected override void MoveTrafficObject(uint localId, FlightSample pose, long now)
    {
        if (!InjectedTraffic.TryGetValue(localId, out var o)) return;
        o.Pose = pose;
        o.Moves++;
    }

    protected override void SetTrafficObjectAppearance(uint localId, ushort lights, bool gearDown)
    {
        if (!InjectedTraffic.TryGetValue(localId, out var o)) return;
        o.Lights = lights;
        o.GearDown = gearDown;
    }

    protected override void RemoveTrafficObject(uint localId)
    {
        if (InjectedTraffic.Remove(localId)) Removals++;
        else BogusRemovals++;
    }

    protected override void ApplyWorldAction(WorldAction action)
    {
        AppliedWorldActions.Add(action);
        WorldNow = action switch
        {
            SetPause p => WorldNow with { Paused = p.Paused },
            StepSimRate r => WorldNow with { SimRate = Math.Clamp(WorldNow.SimRate * (r.Direction > 0 ? 2 : 0.5), 0.25, 128) },
            SetZuluTime t => WorldNow with
            {
                ZuluUnixSec = WorldSample.ToUnix(t.Hour * 3600 + t.Minute * 60, t.DayOfYear, t.Year),
            },
            _ => WorldNow,
        };
    }

    /// <summary>The flying seat's clock time that the current <see cref="Pose"/> was rendered for.</summary>
    public double FollowerRenderTime() => Follower.RenderRemoteTime(_lastPoseAt);

    private void ActLikeAPilot()
    {
        var vars = Definition!.Vars;
        var candidates = Enumerable.Range(0, vars.Count)
            .Where(v => vars[v].Spec.Class is SyncClass.State or SyncClass.Analog or SyncClass.Event).ToArray();
        if (candidates.Length == 0) return;
        int pick = candidates[_rng.Next(candidates.Length)];
        var b = vars[pick];
        switch (b.Spec.Class)
        {
            case SyncClass.Event:
                UserPress(pick, 0);
                Info($"[fake pilot] pressed {b.Spec.Id}");
                break;
            case SyncClass.Analog:
                _values[pick] = Math.Round(_values[pick] + _rng.Next(-30, 31), 1);
                Info($"[fake pilot] turned {b.Spec.Id} to {_values[pick]}");
                break;
            default:
                _values[pick] = _values[pick] == 0 ? 1 : 0;
                Info($"[fake pilot] switched {b.Spec.Id} {(_values[pick] == 0 ? "OFF" : "ON")}");
                break;
        }
    }


    protected override void WriteSimVar(int v, SimRef target, double value) => Schedule(v, value, false);

    protected override void TransmitKEvent(string name, uint data)
    {
        if (!_byEvent.TryGetValue(name, out var targets))
        {
            Info($"[fake sim] K:{name} ({data}) has no effect");
            return;
        }
        foreach (var (v, s) in targets)
        {
            var cls = Definition!.Vars[v].Spec.Class;
            if (cls == SyncClass.Event)
            {
                AppliedEvents.Add((v, unchecked((int)data)));
                continue;
            }
            if (s.Mode == WriteMode.Toggle) Schedule(v, 0, true);
            else if (s.Kind == SetterKind.IncDec) Schedule(v, _values[v] + (string.Equals(name, s.Target, StringComparison.OrdinalIgnoreCase) ? s.Step : -s.Step), false);
            else Schedule(v, InverseParam(s, unchecked((int)data)), false);
        }
    }

    protected override void SetInputEvent(string name, double value)
    {
        if (!_byEvent.TryGetValue(name, out var targets)) return;
        foreach (var (v, _) in targets)
        {
            if (Definition!.Vars[v].Spec.Class == SyncClass.Event) AppliedEvents.Add((v, value));
            else Schedule(v, value, false);
        }
    }

    private void Schedule(int v, double value, bool toggle) => _pending.Add((_now + _rng.Next(0, _latencyMs + 1), v, value, toggle));

    /// <summary>Recover the variable value from a K: event parameter by searching the param expression.</summary>
    private static double InverseParam(Setter s, double data)
    {
        if (s.Param.Source == "value") return data;
        // Expressions are monotonic in practice (value * k, round(value * k), bcd16(value)...):
        // pick the candidate whose encoding matches.
        for (int scale = 0; scale <= 4; scale++)
        {
            double step = Math.Pow(10, -scale);
            double guess = Math.Round(data / Math.Max(1e-9, s.Param.Evaluate(1) - s.Param.Evaluate(0)) / step) * step;
            if (Math.Abs(s.Param.Evaluate(guess) - data) < 0.5) return guess;
        }
        for (int i = 0; i <= 100_000; i++)
            if (Math.Abs(s.Param.Evaluate(i) - data) < 0.5) return i;
        return data;
    }

    public override void Dispose()
    {
        // Like the real bridge: SkyLink exiting removes every object it created.
        Mirror.Clear();
        StepTraffic(_now);
    }
}
