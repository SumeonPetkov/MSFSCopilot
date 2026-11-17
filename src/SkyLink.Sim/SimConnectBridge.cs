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
public sealed partial class SimConnectBridge : SimBridgeBase, IFlightSim
{
    private const uint DefTitle = 1, ReqTitle = 1;
    private const uint DefFlightRead = 30, ReqFlight = 30, DefFlightPose = 31, DefFlightMotion = 32;
    private const int PoseWriteIntervalMs = 15;
    private const int FreezeReassertMs = 2000;

    /// <summary>Read every frame, in this order, into <see cref="FlightSample"/>.</summary>
    private static readonly (string Name, string Unit)[] FlightReadLayout =
    [
        ("PLANE LATITUDE", "degrees"), ("PLANE LONGITUDE", "degrees"), ("PLANE ALTITUDE", "feet"),
        ("PLANE PITCH DEGREES", "degrees"), ("PLANE BANK DEGREES", "degrees"), ("PLANE HEADING DEGREES TRUE", "degrees"),
        ("VELOCITY WORLD X", "feet per second"), ("VELOCITY WORLD Y", "feet per second"), ("VELOCITY WORLD Z", "feet per second"),
        ("VELOCITY BODY X", "feet per second"), ("VELOCITY BODY Y", "feet per second"), ("VELOCITY BODY Z", "feet per second"),
        ("ROTATION VELOCITY BODY X", "degrees per second"), ("ROTATION VELOCITY BODY Y", "degrees per second"),
        ("ROTATION VELOCITY BODY Z", "degrees per second"), ("SIM ON GROUND", "bool"),
    ];

    private static readonly string[] FreezeEvents = ["FREEZE_LATITUDE_LONGITUDE_SET", "FREEZE_ALTITUDE_SET", "FREEZE_ATTITUDE_SET"];
    private const uint ReqAircraftState = 2;
    private const uint ReqEnumInputEvents = 3;
    private const uint ReqCatalogAircraft = 5, ReqCatalogHelicopter = 6;
    private const uint DefAtcId = 33, DefAtcAirline = 34, DefAtcFlightNumber = 35;
    private const uint DefReadBase = 100;
    private const int ReadChunk = 100;
    private const uint DefWriteBase = 10_000;
    private const uint ReqGetInputEventBase = 50_000;
    private const uint EventIdBase = 1000;
    private const uint CaptureGroup = 1;
    private const uint EvtSimStart = 1, EvtSimStop = 2, EvtAircraftLoaded = 3, EvtFlightLoaded = 4;
    private const int OpenRetryMs = 2000;
    private const uint CdaCommand = 0x534B0001, CdaResponse = 0x534B0002;
    private const uint DefCdaCommand = 20_000, DefCdaResponse = 20_001; // outside every other id range
    private const uint ReqBridgeResponse = 4;
    private const int LVarRecheckMs = 5000;
    private const int LVarGiveUpMs = 60_000;

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
    private uint _nextReadDef = DefReadBase;
    private uint _nextEventId = EventIdBase;
    private bool _frozen;
    private long _lastPoseWrite;
    private long _lastFreezeAssert;

    /// <summary>True while SkyLink holds this aircraft frozen and moves it to follow the other seat.</summary>
    public bool IsFollowing => _frozen;

    /// <summary>MSFS is running and SimConnect is open.</summary>
    public bool IsConnected => _open;

    /// <summary>Between MSFS's SimStart and SimStop: the pilot is in an aircraft, not in the menus.</summary>
    public bool IsInFlight { get; private set; }

    /// <summary>Installed aircraft and liveries, after <see cref="RequestAircraftCatalog"/>.</summary>
    public event Action<List<(string Title, string Livery)>>? AircraftCatalog;

    private readonly List<(string Title, string Livery)> _aircraftList = [];
    private readonly HashSet<uint> _catalogDone = [];

    /// <summary>Ask MSFS 2024 for every installed aircraft (planes and helicopters) with its liveries.</summary>
    public bool RequestAircraftCatalog()
    {
        if (!_open) return false;
        _aircraftList.Clear();
        _catalogDone.Clear();
        Call(SimConnect_EnumerateSimObjectsAndLiveries(_h, ReqCatalogAircraft, SimObjectType.Aircraft), "list installed aircraft");
        Call(SimConnect_EnumerateSimObjectsAndLiveries(_h, ReqCatalogHelicopter, SimObjectType.Helicopter), "list installed helicopters");
        return true;
    }

    /// <summary>Load a .flt (aircraft, livery, position). Works from the main menu or in flight.</summary>
    public bool LoadFlight(string fltPath)
    {
        if (!_open) return false;
        Call(SimConnect_FlightLoad(_h, fltPath), $"load flight {fltPath}");
        return true;
    }

    public bool LoadFlightPlan(string plnPath)
    {
        if (!_open) return false;
        Call(SimConnect_FlightPlanLoad(_h, plnPath), $"load flight plan {plnPath}");
        return true;
    }

    /// <summary>Set the ATC identity of the user's aircraft (tail number / callsign, airline, flight number).</summary>
    public unsafe void SetCallsign(string? atcId, string? airline, string? flightNumber)
    {
        if (!_open) return;
        void Set(uint def, string name, DataType type, int size, string? value)
        {
            if (value is null) return;
            if (_atcDefined.Add(def)) Call(SimConnect_AddToDataDefinition(_h, def, name, null, type, 0, Unused), $"ATC field {name}");
            var buf = new byte[size];
            var bytes = System.Text.Encoding.ASCII.GetBytes(value);
            Array.Copy(bytes, buf, Math.Min(bytes.Length, size - 1));
            fixed (byte* p = buf)
                Call(SimConnect_SetDataOnSimObject(_h, def, ObjectIdUser, 0, 0, (uint)size, p), $"set {name}");
        }
        Set(DefAtcId, "ATC ID", DataType.String32, 32, atcId);
        Set(DefAtcAirline, "ATC AIRLINE", DataType.String64, 64, airline);
        Set(DefAtcFlightNumber, "ATC FLIGHT NUMBER", DataType.String8, 8, flightNumber);
    }

    private readonly HashSet<uint> _atcDefined = [];

    private void OnCatalogPage(Recv r)
    {
        uint req = r.U32(12);
        if (req is not (ReqCatalogAircraft or ReqCatalogHelicopter)) return;
        uint arraySize = r.U32(16), entry = r.U32(20), outOf = r.U32(24);
        if (arraySize > 0)
        {
            int stride = (r.Length - 28) / (int)arraySize;
            if (stride < 512) throw new InvalidDataException($"aircraft list entry stride {stride} too small");
            for (int i = 0; i < arraySize; i++)
            {
                int off = 28 + i * stride;
                string title = r.AnsiString(off, 256);
                if (title.Length > 0) _aircraftList.Add((title, r.AnsiString(off + 256, 256)));
            }
        }
        if (entry + 1 < outOf) return;
        _catalogDone.Add(req);
        if (_catalogDone.Count < 2) return;
        var list = _aircraftList.Distinct().ToList();
        Info($"MSFS reports {list.Select(x => x.Title).Distinct().Count()} installed aircraft ({list.Count} liveries)");
        OnMirrorCatalog(list);
        AircraftCatalog?.Invoke(list);
    }
    private bool _channelReady;

    // Bridge module and L-var validation.
    private readonly ModuleClient _module;
    private enum LVarPhase { Idle, WaitingForModule, Listing, Rechecking }
    private LVarPhase _lvarPhase;
    private List<(int V, string Name, string Unit)> _lvarsToCheck = [];
    private long _lvarFirstCheck;
    private long _lvarNextCheck;

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
        _module = new ModuleClient(SendToModule, (uint)Random.Shared.Next(1, int.MaxValue));
        _module.Log += Info;
    }

    /// <summary>"connected (build)", "NOT INSTALLED" or "checking".</summary>
    public string ModuleStatus => _module.IsAvailable ? $"connected ({_module.ModuleBuild})" : _module.IsAbsent ? "NOT INSTALLED" : "checking";

    /// <summary>L-vars of this aircraft still unconfirmed by the bridge module.</summary>
    public int UnconfirmedLVars => _lvarsToCheck.Count;

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
        if (!_open || !_channelReady) return; // never talk to the module before its areas are mapped
        StepFollowing(now);
        PumpWorld(now);
        _module.Tick(now);
        AdvanceLVarValidation(now);
    }

    // ------------------------------------------------------------------ flight model

    /// <summary>While monitoring: once the other seat's stream arrives, freeze and follow it every frame.</summary>
    private void StepFollowing(long now)
    {
        if (IsPilotFlying || Definition is null || !Follower.HasData) return;
        if (now - _lastPoseWrite < PoseWriteIntervalMs) return;
        if (Follower.Sample(now) is not { } pose) return;
        // Freeze lazily (only when there is something to follow) and re-assert now and then:
        // menus, slew or a flight reload can clear the freeze.
        if (!_frozen || now - _lastFreezeAssert >= FreezeReassertMs) SetFreeze(true, now);
        WritePose(pose);
        _lastPoseWrite = now;
    }

    protected override void OnAuthorityChanged(bool isPilotFlying, bool wasPilotFlying, long now)
    {
        if (!_open || !isPilotFlying || !_frozen) return;
        // Taking the controls: put the aircraft exactly where the stream has it, hand the flight
        // model the stream's velocities, then let go, so physics continues without a jolt.
        if (Follower.Sample(now) is { } pose)
        {
            WritePose(pose);
            WriteMotion(pose);
        }
        SetFreeze(false, now);
        Info("flight model released: you have the aircraft");
    }

    private void SetFreeze(bool on, long now)
    {
        foreach (var e in FreezeEvents) TransmitKEvent(e, on ? 1u : 0u);
        if (on && !_frozen) Info("flight model frozen: following the pilot flying");
        _frozen = on;
        _lastFreezeAssert = now;
    }

    private unsafe void WritePose(in FlightSample p)
    {
        double* d = stackalloc double[6];
        d[0] = p.LatDeg; d[1] = p.LonDeg; d[2] = p.AltFt; d[3] = p.PitchDeg; d[4] = p.BankDeg; d[5] = p.HeadingDeg;
        Call(SimConnect_SetDataOnSimObject(_h, DefFlightPose, ObjectIdUser, 0, 0, 6 * sizeof(double), d), "set aircraft pose");
    }

    private unsafe void WriteMotion(in FlightSample p)
    {
        double* d = stackalloc double[6];
        d[0] = p.VBodyXFps; d[1] = p.VBodyYFps; d[2] = p.VBodyZFps; d[3] = p.RotBodyXDps; d[4] = p.RotBodyYDps; d[5] = p.RotBodyZDps;
        Call(SimConnect_SetDataOnSimObject(_h, DefFlightMotion, ObjectIdUser, 0, 0, 6 * sizeof(double), d), "set aircraft velocities");
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
                if (OnSpawnException(r.U32(RecvLayout.ExceptionCode), r.U32(RecvLayout.ExceptionSendId))) break;
                OnException(r.U32(RecvLayout.ExceptionCode), r.U32(RecvLayout.ExceptionSendId), r.U32(RecvLayout.ExceptionIndex));
                break;
            case RecvId.Event:
            case RecvId.EventObjectAddRemove:
                if (OnWorldEvent(r.U32(RecvLayout.EventId), r.U32(RecvLayout.EventData))) break;
                OnEvent(r.U32(RecvLayout.EventId), r.U32(RecvLayout.EventData));
                break;
            case RecvId.AssignedObjectId:
                OnAssignedObjectId(r);
                break;
            case RecvId.EventFilename:
                OnFilenameEvent(r.U32(16), r.AnsiString(24, 260));
                break;
            case RecvId.SystemState:
                if (r.U32(12) == ReqAircraftState) OnAircraftPath(r.AnsiString(24, 260));
                break;
            case RecvId.SimObjectData:
            case RecvId.SimObjectDataBytype:
                OnData(r);
                break;
            case RecvId.ClientData:
                if (r.U32(12) == ReqBridgeResponse && r.Length >= 40 + BridgeProtocol.FrameSize)
                    _module.OnFrame(r.Bytes(40, BridgeProtocol.FrameSize));
                break;
            case RecvId.EnumerateSimobjectAndLiveryList:
                OnCatalogPage(r);
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

        // Bridge module channel. Either side may create the areas; "already created" is fine.
        _module.Reset();
        Call(SimConnect_MapClientDataNameToID(_h, BridgeProtocol.CommandArea, CdaCommand), "map bridge command area");
        Call(SimConnect_MapClientDataNameToID(_h, BridgeProtocol.ResponseArea, CdaResponse), "map bridge response area");
        SimConnect_CreateClientData(_h, CdaCommand, BridgeProtocol.FrameSize, 0);
        SkipException();
        SimConnect_CreateClientData(_h, CdaResponse, BridgeProtocol.FrameSize, 0);
        SkipException();
        Call(SimConnect_AddToClientDataDefinition(_h, DefCdaCommand, 0, BridgeProtocol.FrameSize, 0, Unused), "bridge command layout");
        Call(SimConnect_AddToClientDataDefinition(_h, DefCdaResponse, 0, BridgeProtocol.FrameSize, 0, Unused), "bridge response layout");
        Call(SimConnect_RequestClientData(_h, CdaResponse, ReqBridgeResponse, DefCdaResponse, ClientDataPeriod.OnSet, 0, 0, 0, 0), "subscribe bridge responses");
        _channelReady = true;

        // Flight model: read the aircraft every frame; pose/motion definitions for following.
        foreach (var (name, unit) in FlightReadLayout)
            Call(SimConnect_AddToDataDefinition(_h, DefFlightRead, name, unit, DataType.Float64, 0, Unused), $"read {name}");
        Call(SimConnect_RequestDataOnSimObject(_h, ReqFlight, DefFlightRead, ObjectIdUser, Period.SimFrame, DataRequestFlag.Default, 0, 0, 0), "request flight state");
        foreach (var (name, unit) in FlightReadLayout[..6])
            Call(SimConnect_AddToDataDefinition(_h, DefFlightPose, name, unit, DataType.Float64, 0, Unused), $"write {name}");
        foreach (var (name, unit) in FlightReadLayout[9..15])
            Call(SimConnect_AddToDataDefinition(_h, DefFlightMotion, name, unit, DataType.Float64, 0, Unused), $"write {name}");
        foreach (var e in FreezeEvents) MapEvent(e);
        OpenWorld();

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
                IsInFlight = true;
                RequestAircraftIdentity();
                return;
            case EvtSimStop:
                IsInFlight = false;
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
        if (OnWorldDataMessage(r, req)) return;
        if (req == ReqFlight)
        {
            OnUserObjectId(r.U32(RecvLayout.DataObjectId));
            if (r.Length < 40 + FlightReadLayout.Length * 8) return;
            Span<double> f = stackalloc double[16];
            for (int i = 0; i < f.Length; i++) f[i] = r.F64(40 + i * 8);
            OnFlightRead(new FlightSample(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8],
                f[9], f[10], f[11], f[12], f[13], f[14], f[15] != 0));
            return;
        }
        if (req < DefReadBase || req >= DefWriteBase) return;
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

    /// <summary>
    /// Mappings live as long as the SimConnect connection, across aircraft changes, so ids must
    /// never be reused within one connection.
    /// </summary>
    private uint MapEvent(string name)
    {
        if (_eventIds.TryGetValue(name, out uint id)) return id;
        id = _nextEventId++;
        _eventIds[name] = id;
        Call(SimConnect_MapClientEventToSimEvent(_h, id, name), $"K:{name}");
        return id;
    }

    private void BindDefinition(AircraftDefinition def)
    {
        uint EventId(string name) => MapEvent(name);

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

        // A: variables are subscribed now. L: variables wait for the bridge module to confirm they
        // exist, because subscribing would silently create a misspelled one.
        SubscribeReads(reads.Where(r => !r.Name.StartsWith("L:")).ToList());
        _lvarsToCheck = reads.Where(r => r.Name.StartsWith("L:")).ToList();
        _lvarPhase = _lvarsToCheck.Count > 0 ? LVarPhase.WaitingForModule : LVarPhase.Idle;
        _lvarFirstCheck = _now;

        if (needsInputEvents)
        {
            _inputEventsEnumerated = false;
            Call(SimConnect_EnumerateInputEvents(_h, ReqEnumInputEvents), "enumerate input events");
        }
    }

    private void SubscribeReads(List<(int V, string Name, string Unit)> reads)
    {
        var def = Definition!;
        for (int c = 0; c * ReadChunk < reads.Count; c++)
        {
            // Ids of unbound (cleared) aircraft may be reused once the range is exhausted.
            if (_nextReadDef >= DefWriteBase) _nextReadDef = DefReadBase;
            uint defId = _nextReadDef++;
            _readDefs.Add(defId);
            foreach (var (v, name, unit) in reads.Skip(c * ReadChunk).Take(ReadChunk))
                Call(SimConnect_AddToDataDefinition(_h, defId, name, unit, DataType.Float64, 0, (uint)v), $"read {name} ({unit}) for '{def.Vars[v].Spec.Id}'");
            Call(SimConnect_RequestDataOnSimObject(_h, defId, defId, ObjectIdUser, Period.SimFrame, DataRequestFlag.Changed | DataRequestFlag.Tagged, 0, 0, 0), $"request read block {defId}");
        }
    }

    private void AdvanceLVarValidation(long now)
    {
        if (Definition is null) return;
        switch (_lvarPhase)
        {
            case LVarPhase.WaitingForModule when _module.IsAbsent:
                Info($"cannot verify {_lvarsToCheck.Count} L-var names without the SkyLink Bridge package; a misspelled name would silently never sync");
                SubscribeReads(_lvarsToCheck);
                _lvarsToCheck = [];
                _lvarPhase = LVarPhase.Idle;
                break;
            case LVarPhase.WaitingForModule when _module.IsAvailable:
            case LVarPhase.Rechecking when now >= _lvarNextCheck:
                _lvarPhase = LVarPhase.Listing;
                int generation = DefinitionGeneration;
                _module.ListLVars(names =>
                {
                    if (generation != DefinitionGeneration || Definition is null) return;
                    OnLVarList(names);
                });
                break;
        }
    }

    private void OnLVarList(List<string> names)
    {
        var existing = new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        var present = _lvarsToCheck.Where(r => existing.Contains(r.Name[2..])).ToList();
        _lvarsToCheck = _lvarsToCheck.Where(r => !existing.Contains(r.Name[2..])).ToList();
        if (present.Count > 0) SubscribeReads(present);
        if (_lvarsToCheck.Count == 0)
        {
            Info($"all L-vars confirmed ({existing.Count} defined in the sim)");
            _lvarPhase = LVarPhase.Idle;
            return;
        }
        if (_now - _lvarFirstCheck >= LVarGiveUpMs)
        {
            foreach (var (v, name, _) in _lvarsToCheck)
                Problem($"'{Definition!.Vars[v].Spec.Id}': {name} does not exist in this aircraft (misspelled, or not created by this variant); subscribing anyway");
            SubscribeReads(_lvarsToCheck);
            _lvarsToCheck = [];
            _lvarPhase = LVarPhase.Idle;
            return;
        }
        // Gauges create some L-vars only once the cockpit has initialised: look again shortly.
        _lvarPhase = LVarPhase.Rechecking;
        _lvarNextCheck = _now + LVarRecheckMs;
    }

    private unsafe void SendToModule(byte[] frame)
    {
        if (!_open || !_channelReady) return;
        fixed (byte* p = frame)
            Call(SimConnect_SetClientData(_h, CdaCommand, DefCdaCommand, 0, 0, (uint)frame.Length, p), "send to bridge module");
    }

    protected override void ExecuteRpn(int v, string code)
    {
        if (!_module.IsAvailable)
        {
            base.ExecuteRpn(v, code);
            return;
        }
        string id = Definition!.Vars[v].Spec.Id;
        _module.ExecuteRpn(code, (ok, _) =>
        {
            if (!ok) WarnOnce("rpnfail:" + v, $"'{id}': MSFS could not run calculator code '{code}'");
        });
    }

    /// <summary>The previous call may legitimately fail (e.g. area already created): ignore its exception.</summary>
    private void SkipException()
    {
        if (SimConnect_GetLastSentPacketID(_h, out uint id) == SOk) _sent[id] = "<ignored>";
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
        _lvarsToCheck = [];
        _lvarPhase = LVarPhase.Idle;
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
        if (what == "<ignored>" || !_reportedExceptions.Add(sendId)) return;
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
        // Never leave the user's aircraft frozen when SkyLink stops or MSFS disconnects.
        if (_open && _frozen) SetFreeze(false, _now);
        _frozen = false;
        CloseWorld(); // and never leave mirrored AI aircraft behind
        IsInFlight = false;
        _atcDefined.Clear();
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
        _module.Reset();
        _channelReady = false;
        _nextReadDef = DefReadBase;
        _nextEventId = EventIdBase;
        _lvarsToCheck = [];
        _lvarPhase = LVarPhase.Idle;
        Problems.Clear(); // a new MSFS connection starts with a clean slate
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
