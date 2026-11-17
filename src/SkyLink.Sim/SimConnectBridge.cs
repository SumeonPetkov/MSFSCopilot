using SkyLink.Core;
using SkyLink.Definitions;
using static SkyLink.Sim.SimConnectNative;

namespace SkyLink.Sim;

/// <summary>
/// The real simulator: SimConnect to MSFS 2024. Detects the loaded aircraft, picks its
/// definition, reads every A:/L: variable each sim frame (changed values only, tagged by
/// index), tracks B: input events by subscription, captures K: events from the cockpit and
/// executes writes. Every SimConnect exception is mapped back to the variable or event that
/// caused it, so a wrong name in a definition is reported, never silently ignored.
/// </summary>
public sealed class SimConnectBridge : SimBridgeBase
{
    private const uint DefTitle = 1, ReqTitle = 1;
    private const uint ReqAircraftState = 2;
    private const uint ReqEnumInputEvents = 3;
    private const uint DefReadBase = 100;
    private const int ReadChunk = 100;
    private const uint DefWriteBase = 10_000;
    private const uint ReqGetInputEventBase = 50_000;
    private const uint EventIdBase = 1000;
    private const uint CaptureGroup = 1;
    private const uint EvtSimStart = 1, EvtSimStop = 2, EvtAircraftLoaded = 3, EvtFlightLoaded = 4;
    private const int OpenRetryMs = 2000;

    private readonly DefinitionCatalog _catalog;
    private readonly AutoResetEvent _signal = new(false);
    private IntPtr _h;
    private bool _open;
    private long _nextOpenAttempt;
    private string _status = "waiting for Microsoft Flight Simulator 2024";

    private string? _aircraftPath;
    private string? _title;
    private string? _atcModel;

    private readonly Dictionary<uint, string> _sent = [];
    private readonly HashSet<uint> _reportedExceptions = [];
    private readonly List<uint> _readDefs = [];
    private readonly HashSet<int> _writeDefs = [];
    private readonly Dictionary<string, uint> _eventIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<uint, int> _captureByEventId = [];
    private readonly Dictionary<string, ulong> _inputHashes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ulong, List<int>> _varsByInputHash = [];
    private bool _inputEventsEnumerated;
    private long _now;

    /// <summary>Problems found while binding the definition to the sim (for <c>skylink probe</c>).</summary>
    public List<string> Problems { get; } = [];
    public string? AircraftTitle => _title;
    public string? AircraftAtcModel => _atcModel;
    public string? AircraftPath => _aircraftPath;
    public string? SimConnectDll { get; }

    public SimConnectBridge(DefinitionCatalog catalog)
    {
        _catalog = catalog;
        SimConnectDll = InstallResolver();
    }

    public override string Status => _status;
    public override WaitHandle DataAvailable => _signal;

    public bool TryGetValue(int v, out double value) => TryGetCurrent(v, out value);

    public override void Pump(long now)
    {
        _now = now;
        if (!_open)
        {
            if (now < _nextOpenAttempt) return;
            _nextOpenAttempt = now + OpenRetryMs;
            TryOpen();
            if (!_open) return;
        }

        for (int i = 0; i < 2000; i++)
        {
            if (SimConnect_GetNextDispatch(_h, out var ptr, out uint size) != SOk || ptr == IntPtr.Zero) break;
            try
            {
                Dispatch(new Recv(ptr, size));
            }
            catch (InvalidDataException e)
            {
                WarnOnce("bad-recv:" + e.Message, "ignored a malformed SimConnect message: " + e.Message);
            }
            if (!_open) break;
        }
    }

    private void TryOpen()
    {
        if (SimConnectDll is null)
        {
            _status = "SimConnect.dll not found: install the MSFS 2024 SDK or put SimConnect.dll next to SkyLink";
            return;
        }
        int hr;
        try
        {
            hr = SimConnect_Open(out _h, "SkyLink", IntPtr.Zero, 0, _signal.SafeWaitHandle.DangerousGetHandle(), 0);
        }
        catch (DllNotFoundException e)
        {
            _status = "cannot load SimConnect.dll: " + e.Message;
            return;
        }
        if (hr != SOk)
        {
            _status = "waiting for Microsoft Flight Simulator 2024";
            return;
        }
        _open = true;
        _status = "connected to MSFS, waiting for an aircraft";
        Info($"connected to MSFS (SimConnect: {SimConnectDll})");
    }

    private void Dispatch(Recv r)
    {
        switch (r.Id)
        {
            case RecvId.Open:
                OnOpen();
                break;
            case RecvId.Quit:
                Info("MSFS closed");
                Close();
                break;
            case RecvId.Exception:
                OnException(r.U32(12), r.U32(16), r.U32(20));
                break;
            case RecvId.Event:
                OnEvent(r.U32(16), r.U32(20));
                break;
            case RecvId.EventFilename:
                OnFilenameEvent(r.U32(16), r.AnsiString(24, 260));
                break;
            case RecvId.SystemState:
                if (r.U32(12) == ReqAircraftState) OnAircraftPath(r.AnsiString(24, 260));
                break;
            case RecvId.SimObjectData:
                OnData(r);
                break;
            case RecvId.EnumerateInputEvents:
                OnInputEventList(r);
                break;
            case RecvId.GetInputEvent:
            {
                uint req = r.U32(12);
                if (req >= ReqGetInputEventBase && r.U32(16) == 0 && r.Length >= 28)
                    OnValue((int)(req - ReqGetInputEventBase), r.F64(20), _now);
                break;
            }
            case RecvId.SubscribeInputEvent:
            {
                ulong hash = r.U64(12);
                if (r.U32(20) != 0 || r.Length < 32 || !_varsByInputHash.TryGetValue(hash, out var vars)) break;
                double value = r.F64(24);
                foreach (int v in vars)
                {
                    if (Definition!.Vars[v].Spec.Class == SyncClass.Event) OnCapturedEvent(v, value, _now);
                    else OnValue(v, value, _now);
                }
                break;
            }
        }
    }

    private void OnOpen()
    {
        Call(SimConnect_SubscribeToSystemEvent(_h, EvtSimStart, "SimStart"), "subscribe SimStart");
        Call(SimConnect_SubscribeToSystemEvent(_h, EvtSimStop, "SimStop"), "subscribe SimStop");
        Call(SimConnect_SubscribeToSystemEvent(_h, EvtAircraftLoaded, "AircraftLoaded"), "subscribe AircraftLoaded");
        Call(SimConnect_SubscribeToSystemEvent(_h, EvtFlightLoaded, "FlightLoaded"), "subscribe FlightLoaded");
        Call(SimConnect_AddToDataDefinition(_h, DefTitle, "TITLE", null, DataType.String256, 0, Unused), "read TITLE");
        Call(SimConnect_AddToDataDefinition(_h, DefTitle, "ATC MODEL", null, DataType.String64, 0, Unused), "read ATC MODEL");
        RequestAircraftIdentity();
    }

    private void RequestAircraftIdentity()
    {
        Call(SimConnect_RequestSystemState(_h, ReqAircraftState, "AircraftLoaded"), "request AircraftLoaded state");
        Call(SimConnect_RequestDataOnSimObject(_h, ReqTitle, DefTitle, ObjectIdUser, Period.Once, DataRequestFlag.Default, 0, 0, 0), "request aircraft title");
    }

    private void OnEvent(uint eventId, uint data)
    {
        switch (eventId)
        {
            case EvtSimStart:
                RequestAircraftIdentity();
                return;
            case EvtSimStop:
                return;
        }
        if (_captureByEventId.TryGetValue(eventId, out int v))
            OnCapturedEvent(v, unchecked((int)data), _now);
    }

    private void OnFilenameEvent(uint eventId, string file)
    {
        if (eventId is EvtAircraftLoaded or EvtFlightLoaded)
        {
            if (eventId == EvtAircraftLoaded) OnAircraftPath(file);
            RequestAircraftIdentity();
        }
    }

    private void OnAircraftPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path == _aircraftPath) return;
        _aircraftPath = path;
        _title = null; // a new aircraft: wait for its title before matching
        Call(SimConnect_RequestDataOnSimObject(_h, ReqTitle, DefTitle, ObjectIdUser, Period.Once, DataRequestFlag.Default, 0, 0, 0), "request aircraft title");
    }

    private void OnData(Recv r)
    {
        uint req = r.U32(12);
        if (req == ReqTitle)
        {
            string title = r.AnsiString(40, 256);
            string atc = r.AnsiString(40 + 256, 64);
            if (title == _title && atc == _atcModel && Definition is not null) return;
            _title = title;
            _atcModel = atc;
            SelectDefinition();
            return;
        }
        if (req < DefReadBase || req >= DefReadBase + 1000) return;
        uint count = r.U32(36);
        for (int i = 0, off = 40; i < count; i++, off += 12)
            OnValue((int)r.U32(off), r.F64(off + 4), _now);
    }

    // ------------------------------------------------------------------ definition binding

    private void SelectDefinition()
    {
        var match = _catalog.Match(_title ?? "", _atcModel ?? "", _aircraftPath ?? "");
        if (match?.Definition.Id == Definition?.Id && Definition is not null) return;

        UnbindDefinition();
        SetDefinition(match);
        if (match is null)
        {
            _status = $"no definition for '{_title}' and no generic fallback";
            Info(_status);
            return;
        }
        Info($"aircraft: '{_title}' (ATC model '{_atcModel}', {_aircraftPath})");
        Info($"definition: {match.Definition.Id} v{match.Definition.Version} — {match.Reason}");
        if (match.IsFallback) Info("WARNING: no aircraft-specific definition. Only standard switches, radios and autopilot will sync.");
        BindDefinition(match.Definition);
        _status = $"{match.Definition.Name}{(match.IsFallback ? " (GENERIC FALLBACK)" : "")}";
    }

    private void BindDefinition(AircraftDefinition def)
    {
        Problems.Clear();
        uint nextEvent = EventIdBase;
        uint EventId(string name)
        {
            if (_eventIds.TryGetValue(name, out uint id)) return id;
            id = nextEvent++;
            _eventIds[name] = id;
            Call(SimConnect_MapClientEventToSimEvent(_h, id, name), $"K:{name}");
            return id;
        }

        // Reads: all A:/L: variables, chunked, tagged by variable index, changes only, every sim frame.
        var reads = new List<(int V, string Name, string Unit)>();
        bool needsInputEvents = false;
        for (int v = 0; v < def.Vars.Count; v++)
        {
            var b = def.Vars[v];
            if (b.Read is { } rd)
            {
                if (rd.Kind == SimRefKind.A) reads.Add((v, rd.Name, rd.Unit));
                else if (rd.Kind == SimRefKind.L) reads.Add((v, "L:" + rd.Name, rd.Unit));
                else needsInputEvents = true;
            }
            if (b.Capture is { Kind: CaptureKind.LVarEdge } lc) reads.Add((v, "L:" + lc.Name, "Number"));
            if (b.Capture is { Kind: CaptureKind.BEvent }) needsInputEvents = true;
            if (b.Capture is { Kind: CaptureKind.KEvent } kc)
            {
                uint id = EventId(kc.Name);
                _captureByEventId[id] = v;
                Call(SimConnect_AddClientEventToNotificationGroup(_h, CaptureGroup, id, 0), $"capture K:{kc.Name} for '{b.Spec.Id}'");
            }
            foreach (var s in b.Write)
            {
                if (s.Kind == SetterKind.KEvent) EventId(s.Target);
                if (s.Kind == SetterKind.IncDec) { EventId(s.Target); EventId(s.DecEvent!); }
                if (s.Kind == SetterKind.BEvent) needsInputEvents = true;
            }
        }
        if (_captureByEventId.Count > 0)
            Call(SimConnect_SetNotificationGroupPriority(_h, CaptureGroup, GroupPriorityHighest), "capture group priority");

        for (int c = 0; c * ReadChunk < reads.Count; c++)
        {
            uint defId = DefReadBase + (uint)c;
            _readDefs.Add(defId);
            foreach (var (v, name, unit) in reads.Skip(c * ReadChunk).Take(ReadChunk))
                Call(SimConnect_AddToDataDefinition(_h, defId, name, unit, DataType.Float64, 0, (uint)v), $"read {name} ({unit}) for '{def.Vars[v].Spec.Id}'");
            Call(SimConnect_RequestDataOnSimObject(_h, defId, defId, ObjectIdUser, Period.SimFrame, DataRequestFlag.Changed | DataRequestFlag.Tagged, 0, 0, 0), "request read block " + c);
        }

        if (needsInputEvents)
        {
            _inputEventsEnumerated = false;
            Call(SimConnect_EnumerateInputEvents(_h, ReqEnumInputEvents), "enumerate input events");
        }
    }

    private void OnInputEventList(Recv r)
    {
        if (r.U32(12) != ReqEnumInputEvents || Definition is null) return;
        uint arraySize = r.U32(16), entry = r.U32(20), outOf = r.U32(24);
        if (arraySize > 0)
        {
            int stride = (r.Length - 28) / (int)arraySize;
            if (stride < 72) throw new InvalidDataException($"input event descriptor stride {stride} too small");
            for (int i = 0; i < arraySize; i++)
            {
                int off = 28 + i * stride;
                _inputHashes[r.AnsiString(off, 64)] = r.U64(off + 64);
            }
        }
        if (entry + 1 < outOf || _inputEventsEnumerated) return;
        _inputEventsEnumerated = true;

        var def = Definition;
        for (int v = 0; v < def.Vars.Count; v++)
        {
            var b = def.Vars[v];
            var names = new List<(string Name, bool Subscribe)>();
            if (b.Read is { Kind: SimRefKind.B } rb) names.Add((rb.Name, true));
            if (b.Capture is { Kind: CaptureKind.BEvent } cb) names.Add((cb.Name, true));
            names.AddRange(b.Write.Where(s => s.Kind == SetterKind.BEvent).Select(s => (s.Target, false)));
            foreach (var (name, subscribe) in names)
            {
                if (!_inputHashes.TryGetValue(name, out ulong hash))
                {
                    Problem($"'{b.Spec.Id}': this aircraft has no input event B:{name}");
                    continue;
                }
                if (!subscribe) continue;
                if (!_varsByInputHash.TryGetValue(hash, out var list)) _varsByInputHash[hash] = list = [];
                if (list.Contains(v)) continue;
                list.Add(v);
                Call(SimConnect_SubscribeInputEvent(_h, hash), $"subscribe B:{name}");
                if (b.Spec.Class != SyncClass.Event)
                    Call(SimConnect_GetInputEvent(_h, ReqGetInputEventBase + (uint)v, hash), $"get B:{name}");
            }
        }
        Info($"aircraft exposes {_inputHashes.Count} input events");
    }

    private void UnbindDefinition()
    {
        if (!_open) return;
        foreach (uint d in _readDefs)
        {
            SimConnect_RequestDataOnSimObject(_h, d, d, ObjectIdUser, Period.Never, 0, 0, 0, 0);
            SimConnect_ClearDataDefinition(_h, d);
        }
        foreach (int v in _writeDefs) SimConnect_ClearDataDefinition(_h, DefWriteBase + (uint)v);
        _readDefs.Clear();
        _writeDefs.Clear();
        _captureByEventId.Clear(); // K: mappings stay valid for the connection and are reused
        _varsByInputHash.Clear();
        _inputHashes.Clear();
    }

    // ------------------------------------------------------------------ writes

    protected override unsafe void WriteSimVar(int v, SimRef target, double value)
    {
        if (!_open) return;
        uint defId = DefWriteBase + (uint)v;
        if (_writeDefs.Add(v))
        {
            string name = target.Kind == SimRefKind.L ? "L:" + target.Name : target.Name;
            Call(SimConnect_AddToDataDefinition(_h, defId, name, target.Unit, DataType.Float64, 0, Unused), $"write {name} for '{Definition!.Vars[v].Spec.Id}'");
        }
        Call(SimConnect_SetDataOnSimObject(_h, defId, ObjectIdUser, 0, 0, sizeof(double), &value), $"set '{Definition!.Vars[v].Spec.Id}'");
    }

    protected override void TransmitKEvent(string name, uint data)
    {
        if (!_open || !_eventIds.TryGetValue(name, out uint id)) return;
        Call(SimConnect_TransmitClientEvent_EX1(_h, ObjectIdUser, id, GroupPriorityHighest, EventFlagGroupIdIsPriority, data, 0, 0, 0, 0), $"transmit K:{name}");
    }

    protected override unsafe void SetInputEvent(string name, double value)
    {
        if (!_open) return;
        if (!_inputHashes.TryGetValue(name, out ulong hash))
        {
            WarnOnce("noB:" + name, $"cannot set B:{name}: input events not enumerated yet or unknown to this aircraft");
            return;
        }
        Call(SimConnect_SetInputEvent(_h, hash, sizeof(double), &value), $"set B:{name}");
    }

    // ------------------------------------------------------------------ errors

    private void Call(int hr, string what)
    {
        if (hr != SOk)
        {
            Problem($"{what}: SimConnect call failed (0x{hr:X8})");
            return;
        }
        if (SimConnect_GetLastSentPacketID(_h, out uint id) == SOk)
        {
            _sent[id] = what;
            if (_sent.Count > 20_000) _sent.Clear();
        }
    }

    private void OnException(uint code, uint sendId, uint index)
    {
        string what = _sent.TryGetValue(sendId, out var w) ? w : $"packet {sendId}";
        if (!_reportedExceptions.Add(sendId)) return;
        Problem($"{what}: MSFS rejected it ({ExceptionName(code)})");
    }

    private void Problem(string message)
    {
        Problems.Add(message);
        Info("PROBLEM: " + message);
    }

    private static string ExceptionName(uint code) => code switch
    {
        1 => "ERROR",
        2 => "SIZE_MISMATCH",
        3 => "UNRECOGNIZED_ID",
        7 => "NAME_UNRECOGNIZED: unknown variable, unit or event name",
        12 => "TOO_MANY_REQUESTS",
        18 => "INVALID_DATA_TYPE",
        19 => "INVALID_DATA_SIZE",
        20 => "DATA_ERROR: the variable is probably not settable",
        25 => "ILLEGAL_OPERATION",
        _ => $"exception {code}",
    };

    private void Close()
    {
        if (_open) SimConnect_Close(_h);
        _open = false;
        _h = IntPtr.Zero;
        _aircraftPath = _title = _atcModel = null;
        _readDefs.Clear();
        _writeDefs.Clear();
        _eventIds.Clear();
        _captureByEventId.Clear();
        _varsByInputHash.Clear();
        _inputHashes.Clear();
        _sent.Clear();
        _reportedExceptions.Clear();
        SetDefinition(null);
        _status = "waiting for Microsoft Flight Simulator 2024";
        _nextOpenAttempt = _now + OpenRetryMs;
    }

    public override void Dispose()
    {
        Close();
        _signal.Dispose();
    }
}
