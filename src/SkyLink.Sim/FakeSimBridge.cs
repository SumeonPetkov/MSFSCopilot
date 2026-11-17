using SkyLink.Core;
using SkyLink.Definitions;

namespace SkyLink.Sim;

/// <summary>
/// A simulated cockpit for testing without MSFS: values with write latency, K: events that
/// move the variable they set, and an optional "fake pilot" that flips switches, turns knobs
/// and presses buttons. Used by <c>skylink host/join --fake-sim</c> and the integration tests.
/// </summary>
public sealed class FakeSimBridge : SimBridgeBase
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

    public override void Dispose() { }
}
