using SkyLink.Core;
using SkyLink.Definitions;

namespace SkyLink.Sim;

/// <summary>The simulator side of a session: reads values, captures cockpit events, executes engine commands.</summary>
public interface ISimBridge : IDisposable
{
    /// <summary>One-line state for the UI ("waiting for MSFS", "C172 loaded", ...).</summary>
    string Status { get; }

    /// <summary>The definition for the loaded aircraft, or null while none is active.</summary>
    AircraftDefinition? Definition { get; }

    MatchResult? Match { get; }

    /// <summary>Incremented whenever <see cref="Definition"/> changes; the session rebuilds its engine.</summary>
    int DefinitionGeneration { get; }

    /// <summary>Signalled when the sim has new data (may be null).</summary>
    WaitHandle? DataAvailable { get; }

    /// <summary>Process pending simulator messages.</summary>
    void Pump(long now);

    /// <summary>Feed every known value and every captured cockpit event into the engine.</summary>
    void Feed(SyncEngine engine, long now);

    void Execute(SimCommand command, long now);

    /// <summary>Feed what the sim knows about the shared world (traffic scans, time/rate/pause, warnings).</summary>
    void FeedWorld(WorldSync world, long now);

    /// <summary>The shared world as this sim sees it, for the UI.</summary>
    WorldStatus World { get; }

    event Action<string>? Log;
}

/// <param name="TrafficSeen">Captain: AI aircraft found in this sim. FO: AI aircraft the Captain announced.</param>
/// <param name="TrafficMirrored">FO: AI aircraft created here and following the Captain's.</param>
/// <param name="StandIns">FO: of those, shown with a different model (the Captain's is not installed here).</param>
/// <param name="ForeignTraffic">FO: AI aircraft in this sim that SkyLink did not create (MSFS traffic still on).</param>
public sealed record WorldStatus(
    WorldRole Role,
    int TrafficSeen,
    int TrafficMirrored,
    int StandIns,
    int TrafficFailed,
    int ForeignTraffic,
    bool WeatherMismatch,
    string? WeatherDetail,
    WorldSample? Local,
    WorldSample? Remote)
{
    public static readonly WorldStatus None = new(WorldRole.None, 0, 0, 0, 0, 0, false, null, null, null);
}

/// <summary>
/// The parts of a bridge that do not depend on how the sim is reached: value tracking, the
/// echo ledger for replayed events, setter selection, the toggle guard and INC/DEC stepping.
/// </summary>
public abstract class SimBridgeBase : ISimBridge
{
    /// <summary>Never re-fire a toggle within this window: the previous one may still be in flight.</summary>
    public const int ToggleGuardMs = 600;
    public const int IncDecGuardMs = 400;
    public const int MaxIncDecSteps = 60;
    public const int EchoTtlMs = 2000;

    private readonly List<(int Var, double Param)> _capturedEvents = [];
    private readonly List<(int Var, double Param, long Expires)> _echoLedger = [];
    private AircraftDefinition? _definition;
    private double[] _current = [];
    private bool[] _has = [];
    private long[] _lastToggle = [];
    private long[] _lastIncDec = [];
    private readonly HashSet<string> _warnedOnce = [];

    public abstract string Status { get; }
    public AircraftDefinition? Definition => _definition;
    public MatchResult? Match { get; private set; }
    public int DefinitionGeneration { get; private set; }
    public virtual WaitHandle? DataAvailable => null;
    public event Action<string>? Log;

    /// <summary>The local pilot triggered a synced event (raised on the sim thread).</summary>
    public event Action<VarBinding, double>? EventCaptured;

    /// <summary>The other pilot's event was replayed into this sim (raised on the sim thread).</summary>
    public event Action<VarBinding, double>? EventReplayed;

    public abstract void Pump(long now);
    public abstract void Dispose();

    protected void Info(string message) => Log?.Invoke(message);

    protected void WarnOnce(string key, string message)
    {
        if (_warnedOnce.Add(key)) Info(message);
    }

    protected void SetDefinition(MatchResult? match)
    {
        Match = match;
        _definition = match?.Definition;
        int n = _definition?.Vars.Count ?? 0;
        _current = new double[n];
        _has = new bool[n];
        _lastToggle = new long[n];
        _lastIncDec = new long[n];
        Array.Fill(_lastToggle, long.MinValue / 2);
        Array.Fill(_lastIncDec, long.MinValue / 2);
        _capturedEvents.Clear();
        _echoLedger.Clear();
        DefinitionGeneration++;
    }

    protected bool TryGetCurrent(int v, out double value)
    {
        value = (uint)v < (uint)_current.Length ? _current[v] : 0;
        return (uint)v < (uint)_has.Length && _has[v];
    }

    /// <summary>A value read from the sim (A:/L: data or a B: input event).</summary>
    protected void OnValue(int v, double value, long now)
    {
        if (_definition is null || (uint)v >= (uint)_current.Length) return;
        var b = _definition.Vars[v];
        double before = _current[v];
        bool had = _has[v];
        _current[v] = value;
        _has[v] = true;

        // An L-var command pulse (0 -> non-zero) is a button press.
        if (b.Spec.Class == SyncClass.Event && b.Capture?.Kind == CaptureKind.LVarEdge && had && before == 0 && value != 0)
            OnCapturedEvent(v, value, now);
    }

    /// <summary>A cockpit event captured from the sim (K: notification, B: change, L: edge).</summary>
    protected void OnCapturedEvent(int v, double param, long now)
    {
        _echoLedger.RemoveAll(e => e.Expires < now);
        int echo = _echoLedger.FindIndex(e => e.Var == v && Math.Abs(e.Param - param) < 1e-6);
        if (echo >= 0)
        {
            _echoLedger.RemoveAt(echo); // our own replay of the other pilot's press
            return;
        }
        _capturedEvents.Add((v, param));
        if (_definition is not null) EventCaptured?.Invoke(_definition.Vars[v], param);
    }

    public void Feed(SyncEngine engine, long now)
    {
        if (_definition is null) return;
        for (int v = 0; v < _current.Length; v++)
            if (_has[v] && _definition.Vars[v].Spec.Class != SyncClass.Event)
                engine.OnSimSample(v, _current[v], now);
        foreach (var (v, p) in _capturedEvents) engine.OnLocalEvent(v, p, now);
        _capturedEvents.Clear();
        if (IsPilotFlying && _latestFlight is { } f) engine.OnFlightSample(f, now);
    }

    // ------------------------------------------------------------------ flight model

    private FlightSample? _latestFlight;

    /// <summary>Follows the other seat's aircraft while this seat is monitoring.</summary>
    protected FlightFollower Follower { get; } = new();

    public bool IsPilotFlying { get; private set; }

    /// <summary>The local aircraft as last read from the sim.</summary>
    public FlightSample? LatestFlight => _latestFlight;

    /// <summary>The local aircraft's pose and motion, read every frame.</summary>
    protected void OnFlightRead(FlightSample s) => _latestFlight = s;

    public void Execute(SimCommand command, long now)
    {
        if (ExecuteWorld(command, now)) return;
        if (_definition is null) return;
        switch (command)
        {
            case WriteVar w:
                Write(w.Var, w.Value, w.Attempt, now);
                break;
            case WriteControl c:
                Write(c.Var, c.Value, 0, now);
                break;
            case FireEvent f:
                Fire(f.Var, f.Param, now);
                break;
            case AuthorityChanged a:
                bool was = IsPilotFlying;
                IsPilotFlying = a.IsPilotFlying;
                if (!a.IsPilotFlying)
                {
                    // Start following from where we are, so the switch to the stream is eased.
                    Follower.Reset();
                    if (_latestFlight is { } here) Follower.SeedOutput(here, now);
                }
                Info(a.IsPilotFlying ? "you are PILOT FLYING" : "you are PILOT MONITORING (the other pilot flies)");
                OnAuthorityChanged(a.IsPilotFlying, was, now);
                break;
            case FlightStateUpdate u:
                Follower.Add(u.RemoteTime, u.Sample, now);
                break;
        }
    }

    // ------------------------------------------------------------------ shared world

    /// <summary>The First Officer's injected copies of the Captain's AI aircraft.</summary>
    protected TrafficMirror Mirror { get; } = new();
    /// <summary>The First Officer's time/rate/pause follower and weather comparison.</summary>
    protected WorldFollower WorldFollow { get; } = new();
    public WorldRole WorldRole { get; private set; }

    private readonly List<(TrafficKind Kind, List<(uint, TrafficSample)> Objects, long At)> _scans = [];
    private readonly List<(uint Id, TrafficIdentity Identity)> _identities = [];
    private bool _trafficLost;
    private WorldSample? _localWorld;
    private readonly List<TrafficAction> _trafficActions = [];
    private readonly List<WorldAction> _worldActions = [];

    /// <summary>FO: AI aircraft in this sim that SkyLink did not create.</summary>
    protected int ForeignTraffic { get; set; }
    /// <summary>Captain: AI aircraft in the last scans.</summary>
    protected int TrafficSeen { get; set; }

    public virtual WorldStatus World => new(WorldRole,
        WorldRole == WorldRole.Mirror ? Mirror.Known : TrafficSeen,
        Mirror.Spawned, Mirror.StandIns, Mirror.Failed, ForeignTraffic,
        WorldFollow.WeatherMismatch, WorldFollow.WeatherMismatch ? WorldFollow.WeatherDetail : null,
        _localWorld, WorldFollow.Remote);

    /// <summary>Captain: a complete scan of one kind of AI object around the aircraft.</summary>
    protected void OnTrafficScan(TrafficKind kind, List<(uint, TrafficSample)> objects, long now)
    {
        if (WorldRole != WorldRole.Source) return;
        _scans.RemoveAll(s => s.Kind == kind); // only the newest scan per kind matters
        _scans.Add((kind, objects, now));
    }

    protected void OnTrafficIdentity(uint id, TrafficIdentity identity)
    {
        if (WorldRole == WorldRole.Source) _identities.Add((id, identity));
    }

    /// <summary>Captain: the sim connection is gone, so is all its traffic.</summary>
    protected void OnTrafficLost()
    {
        _scans.Clear();
        _identities.Clear();
        _trafficLost = true;
        TrafficSeen = 0;
    }

    /// <summary>The local sim's time, rate, pause and weather (read about once a second).</summary>
    protected void OnWorldRead(WorldSample s, long now)
    {
        _localWorld = s;
        WorldFollow.OnLocal(s, now);
    }

    public void FeedWorld(WorldSync world, long now)
    {
        if (_trafficLost)
        {
            _trafficLost = false;
            world.OnTrafficLost();
        }
        foreach (var (kind, objects, at) in _scans) world.OnTrafficScan(kind, objects, at);
        _scans.Clear();
        foreach (var (id, identity) in _identities) world.OnTrafficIdentity(id, identity);
        _identities.Clear();
        if (_latestFlight is { } me) world.OnUserPosition(me);
        if (_localWorld is { } w) world.OnLocalWorld(w);
        if (WorldRole == WorldRole.Mirror) world.OnLocalNotice(ForeignTraffic, WorldFollow.WeatherMismatch);
    }

    private bool ExecuteWorld(SimCommand command, long now)
    {
        switch (command)
        {
            case SetWorldRole r:
                var old = WorldRole;
                if (old == r.Role) return true;
                WorldRole = r.Role;
                if (old == WorldRole.Mirror)
                {
                    Mirror.Clear();
                    WorldFollow.Reset();
                    StepTraffic(now);
                }
                if (old == WorldRole.Source) OnTrafficLost();
                Info(r.Role switch
                {
                    WorldRole.Source => "shared world: this sim is the source of AI traffic, time and pause",
                    WorldRole.Mirror => "shared world: mirroring the Captain's AI traffic, time and pause",
                    _ => "shared world: off",
                });
                OnWorldRoleChanged(old, r.Role, now);
                return true;
            case MirrorTraffic m:
                if (WorldRole == WorldRole.Mirror) Mirror.OnMessage(m.Message, now);
                return true;
            case WorldStateUpdate u:
                if (WorldRole == WorldRole.Mirror) WorldFollow.OnRemote(u.Sample, now);
                return true;
            case ClearTraffic:
                Mirror.Clear();
                WorldFollow.Reset();
                StepTraffic(now); // remove them now, not at the next pump
                return true;
        }
        return false;
    }

    /// <summary>Run the mirror and the world follower; concrete bridges call this from <see cref="Pump"/>.</summary>
    protected void StepWorld(long now)
    {
        StepTraffic(now);
        if (WorldRole != WorldRole.Mirror) return;
        _worldActions.Clear();
        WorldFollow.Step(now, _worldActions);
        foreach (var a in _worldActions)
        {
            Info(a switch
            {
                SetPause p => p.Paused ? "the Captain paused: pausing" : "the Captain resumed: unpausing",
                StepSimRate r => $"matching the Captain's sim rate ({(r.Direction > 0 ? "faster" : "slower")})",
                SetZuluTime t => $"matching the Captain's time: {t.Year}, day {t.DayOfYear}, {t.Hour:00}:{t.Minute:00}Z",
                _ => a.ToString(),
            });
            ApplyWorldAction(a);
        }
    }

    /// <summary>Spawn, move and remove injected aircraft as the mirror says. Never skipped: removals must happen.</summary>
    protected void StepTraffic(long now)
    {
        _trafficActions.Clear();
        Mirror.Step(now, _latestFlight, _trafficActions);
        foreach (var a in _trafficActions)
        {
            switch (a)
            {
                case SpawnTraffic s: SpawnTrafficObject(s, now); break;
                case MoveTraffic m: MoveTrafficObject(m.LocalId, m.Pose, now); break;
                case SetTrafficAppearance ap: SetTrafficObjectAppearance(ap.LocalId, ap.Lights, ap.GearDown); break;
                case RemoveTraffic r: RemoveTrafficObject(r.LocalId); break;
            }
        }
    }

    protected virtual void OnWorldRoleChanged(WorldRole old, WorldRole role, long now) { }
    /// <summary>Create the object; answer with <c>Mirror.OnSpawned</c>/<c>Mirror.OnSpawnFailed</c> for <see cref="SpawnTraffic.Token"/>.</summary>
    protected virtual void SpawnTrafficObject(SpawnTraffic spawn, long now) => Mirror.OnSpawnFailed(spawn.Token);
    protected virtual void MoveTrafficObject(uint localId, FlightSample pose, long now) { }
    protected virtual void SetTrafficObjectAppearance(uint localId, ushort lights, bool gearDown) { }
    protected virtual void RemoveTrafficObject(uint localId) { }
    protected virtual void ApplyWorldAction(WorldAction action) { }

    /// <summary>
    /// Becoming pilot monitoring: freeze the local flight model and follow. Becoming pilot flying:
    /// put the aircraft where the stream says it is, give it the stream's velocities, unfreeze.
    /// </summary>
    protected virtual void OnAuthorityChanged(bool isPilotFlying, bool wasPilotFlying, long now) { }

    private void Write(int v, double value, int attempt, long now)
    {
        var b = _definition!.Vars[v];
        if (b.Write.Count == 0) return;
        // Attempts 0-1 use the primary setter; later retries fall back to the alternate.
        var s = attempt >= 2 && b.Write.Count > 1 ? b.Write[1] : b.Write[0];
        Apply(v, b, s, value, now, isEvent: false);
    }

    private void Fire(int v, double param, long now)
    {
        var b = _definition!.Vars[v];
        if (b.Write.Count == 0) return;
        _echoLedger.Add((v, param, now + EchoTtlMs));
        Apply(v, b, b.Write[0], param, now, isEvent: true);
        EventReplayed?.Invoke(b, param);
    }

    private void Apply(int v, VarBinding b, Setter s, double value, long now, bool isEvent)
    {
        switch (s.Kind)
        {
            case SetterKind.SimVar:
                WriteSimVar(v, s.Var!, s.Param.Evaluate(value));
                break;

            case SetterKind.KEvent when s.Mode == WriteMode.Toggle && !isEvent:
                if (!TryGetCurrent(v, out double cur)) return;
                if (Math.Abs(cur - value) <= b.Spec.Epsilon + 1e-9) return;   // already there
                if (now - _lastToggle[v] < ToggleGuardMs) return;               // previous flip in flight
                _lastToggle[v] = now;
                // A bare toggle takes no meaningful parameter; only pass one if the definition says so.
                TransmitKEvent(s.Target, s.Param == ParamExpr.Identity || s.Param.Source == "value" ? 0 : ToData(s.Param.Evaluate(value)));
                break;

            case SetterKind.KEvent:
                TransmitKEvent(s.Target, ToData(s.Param.Evaluate(value)));
                break;

            case SetterKind.BEvent:
                SetInputEvent(s.Target, s.Param.Evaluate(value));
                break;

            case SetterKind.IncDec:
            {
                if (!TryGetCurrent(v, out double at) || now - _lastIncDec[v] < IncDecGuardMs) return;
                int steps = (int)Math.Round((value - at) / s.Step);
                if (steps == 0) return;
                _lastIncDec[v] = now;
                string evt = steps > 0 ? s.Target : s.DecEvent!;
                for (int i = 0; i < Math.Min(Math.Abs(steps), MaxIncDecSteps); i++) TransmitKEvent(evt, 0);
                break;
            }

            case SetterKind.Rpn:
                ExecuteRpn(v, s.Target.Replace("{value}", s.Param.Evaluate(value).ToString("R", System.Globalization.CultureInfo.InvariantCulture)));
                break;

            case SetterKind.HEvent:
                ExecuteRpn(v, $"(>H:{s.Target})");
                break;
        }
    }

    /// <summary>K: event data is a DWORD; negative values travel as two's complement.</summary>
    public static uint ToData(double d)
    {
        double r = Math.Round(d, MidpointRounding.AwayFromZero);
        return r >= 0 ? (uint)Math.Min(r, uint.MaxValue) : unchecked((uint)(int)Math.Max(r, int.MinValue));
    }

    protected abstract void WriteSimVar(int v, SimRef target, double value);
    protected abstract void TransmitKEvent(string name, uint data);
    protected abstract void SetInputEvent(string name, double value);

    /// <summary>Run calculator code in the sim (needs the bridge module).</summary>
    protected virtual void ExecuteRpn(int v, string code) =>
        WarnOnce("rpn:" + v, $"'{_definition!.Vars[v].Spec.Id}' needs the SkyLink Bridge package (rpn/hevent); not written");
}
