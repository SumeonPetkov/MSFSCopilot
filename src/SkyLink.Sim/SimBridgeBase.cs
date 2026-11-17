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

    event Action<string>? Log;
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
    }

    public void Feed(SyncEngine engine, long now)
    {
        if (_definition is null) return;
        for (int v = 0; v < _current.Length; v++)
            if (_has[v] && _definition.Vars[v].Spec.Class != SyncClass.Event)
                engine.OnSimSample(v, _current[v], now);
        foreach (var (v, p) in _capturedEvents) engine.OnLocalEvent(v, p, now);
        _capturedEvents.Clear();
    }

    public void Execute(SimCommand command, long now)
    {
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
                OnAuthorityChanged(a.IsPilotFlying);
                break;
        }
    }

    /// <summary>Phase 4: freeze/unfreeze the flight model. Phase 2 only reports it.</summary>
    protected virtual void OnAuthorityChanged(bool isPilotFlying) =>
        Info(isPilotFlying ? "you are PILOT FLYING" : "you are PILOT MONITORING (the other pilot flies)");

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
                ExecuteRpn(v, s.Target, value);
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

    protected virtual void ExecuteRpn(int v, string code, double value) =>
        WarnOnce("rpn:" + v, $"'{_definition!.Vars[v].Spec.Id}' uses an rpn setter, which needs the SkyLink in-sim bridge (phase 3); not written");
}
