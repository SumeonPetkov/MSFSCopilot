using System.Diagnostics;
using System.Text.Json;
using SkyLink.Core;
using static SkyLink.Sim.SimConnectNative;

namespace SkyLink.Sim;

/// <summary>
/// The shared world over SimConnect. Captain: scans every AI aircraft and helicopter around the
/// user aircraft ~5 times a second, asks each new one what it is, and reads time/rate/pause/
/// weather. First Officer: creates a look-alike per remote aircraft
/// (<c>AICreateNonATCAircraft_EX1</c>), takes it away from the AI, freezes it and moves it every
/// frame; scans for AI traffic SkyLink did not create (MSFS traffic still on); applies the
/// Captain's time, rate and pause. Every object SkyLink creates is removed again, also after a
/// crash: created ids are kept in a ledger and cleaned up on the next connection to the same MSFS.
/// </summary>
public sealed partial class SimConnectBridge
{
    private const uint DefTrafficRead = 40, DefTrafficIdentity = 41, DefTrafficPose = 42, DefWorld = 43;
    private const uint ReqTrafficAirplane = 40, ReqTrafficHelicopter = 41, ReqWorld = 43, ReqTrafficRemove = 44, ReqTrafficRelease = 45;
    private const uint ReqIdentityBase = 300_000, ReqIdentityRange = 100_000;
    private const uint ReqSpawnBase = 400_000, ReqSpawnRange = 100_000;
    private const uint ReqGhostBase = 500_000;
    private const uint EvtPause = 5, EvtObjectRemoved = 6;

    /// <summary>Captain scans this often; the FO scans for foreign traffic less often.</summary>
    public int TrafficScanIntervalMs { get; set; } = 200;
    public int ForeignScanIntervalMs { get; set; } = 2000;
    public uint TrafficRadiusMeters { get; set; } = 80_000;
    private const int IdentityRefreshMs = 30_000;
    private const int InjectedFreezeReassertMs = 5000;
    private const int CatalogWaitMs = 8000;

    /// <summary>The flight sample layout plus visible state, for every AI object.</summary>
    private static readonly (string Name, string Unit)[] TrafficReadLayout =
    [
        .. FlightReadLayout,
        ("LIGHT ON STATES", "mask"), ("GEAR HANDLE POSITION", "bool"),
    ];

    private static readonly (string Name, SimConnectNative.DataType Type, int Size)[] TrafficIdentityLayout =
    [
        ("TITLE", DataType.String256, 256), ("ATC ID", DataType.String32, 32), ("ATC MODEL", DataType.String64, 64),
        ("ATC AIRLINE", DataType.String64, 64), ("ATC FLIGHT NUMBER", DataType.String8, 8),
        ("LIVERY NAME", DataType.String256, 256), // MSFS 2024; last, so if unknown the rest still parses
    ];

    private static readonly (string Name, string Unit)[] WorldReadLayout =
    [
        ("ZULU TIME", "seconds"), ("ZULU DAY OF YEAR", "number"), ("ZULU YEAR", "number"), ("SIMULATION RATE", "number"),
        ("SEA LEVEL PRESSURE", "millibars"), ("AMBIENT WIND DIRECTION", "degrees"), ("AMBIENT WIND VELOCITY", "knots"),
        ("AMBIENT TEMPERATURE", "celsius"),
    ];

    /// <summary>LIGHT ON STATES bits and the event that sets each light on an injected aircraft.</summary>
    private static readonly (ushort Bit, string Event)[] LightEvents =
    [
        (0x1, "NAV_LIGHTS_SET"), (0x2, "BEACON_LIGHTS_SET"), (0x4, "LANDING_LIGHTS_SET"), (0x8, "TAXI_LIGHTS_SET"),
        (0x10, "STROBES_SET"), (0x80, "WING_LIGHTS_SET"), (0x100, "LOGO_LIGHTS_SET"),
    ];

    private static readonly string[] WorldEvents =
        ["PAUSE_SET", "SIM_RATE_INCR", "SIM_RATE_DECR", "ZULU_YEAR_SET", "ZULU_DAY_SET", "ZULU_HOURS_SET", "ZULU_MINUTES_SET", "GEAR_SET"];

    private uint _userObjectId;
    private bool _paused;
    private readonly Dictionary<uint, (List<(uint Id, TrafficSample S)> Objects, long Started)> _scanning = [];
    private long _lastScan = long.MinValue / 2;
    private readonly Dictionary<uint, long> _identityAsked = [];
    private readonly Dictionary<uint, (uint ObjectId, TrafficKind Kind)> _identityRequests = [];
    private uint _nextIdentityReq;
    private readonly Dictionary<uint, ulong> _spawnByRequest = [];
    private readonly Dictionary<uint, uint> _spawnRequestBySendId = [];
    private uint _nextSpawnReq;
    private readonly Dictionary<uint, (string Title, string Tail, long FrozenAt)> _injected = [];
    private readonly HashSet<string> _warnedSpawn = [];
    private long _mirrorSince;
    private bool _catalogForMirror;
    private bool _ledgerDirty;
    private readonly Dictionary<uint, (uint Id, string Title, string Tail)> _ghostChecks = [];

    /// <summary>Raised (sim thread) with every complete scan of AI objects, for <c>skylink traffic</c>.</summary>
    public event Action<TrafficKind, IReadOnlyList<(uint Id, TrafficSample Sample)>>? TrafficScanned;
    /// <summary>Raised (sim thread) when an AI object's identity arrives.</summary>
    public event Action<uint, TrafficIdentity>? TrafficIdentified;

    /// <summary>The user aircraft's SimConnect object id (0 until known).</summary>
    public uint UserObjectId => _userObjectId;
    public bool IsPaused => _paused;

    private void OpenWorld()
    {
        Call(SimConnect_SubscribeToSystemEvent(_h, EvtPause, "Pause_EX1"), "subscribe Pause_EX1");
        Call(SimConnect_SubscribeToSystemEvent(_h, EvtObjectRemoved, "ObjectRemoved"), "subscribe ObjectRemoved");
        foreach (var (name, unit) in TrafficReadLayout)
            Call(SimConnect_AddToDataDefinition(_h, DefTrafficRead, name, unit, DataType.Float64, 0, Unused), $"traffic read {name}");
        foreach (var (name, type, _) in TrafficIdentityLayout)
            Call(SimConnect_AddToDataDefinition(_h, DefTrafficIdentity, name, null, type, 0, Unused), $"traffic identity {name}");
        foreach (var (name, unit) in FlightReadLayout[..6])
            Call(SimConnect_AddToDataDefinition(_h, DefTrafficPose, name, unit, DataType.Float64, 0, Unused), $"traffic pose {name}");
        foreach (var (name, unit) in WorldReadLayout)
            Call(SimConnect_AddToDataDefinition(_h, DefWorld, name, unit, DataType.Float64, 0, Unused), $"world read {name}");
        Call(SimConnect_RequestDataOnSimObject(_h, ReqWorld, DefWorld, ObjectIdUser, Period.Second, DataRequestFlag.Default, 0, 0, 0), "request world state");
        foreach (var e in WorldEvents) MapEvent(e);
        foreach (var (_, e) in LightEvents) MapEvent(e);
        CleanUpGhosts();
        if (WorldRole == WorldRole.Mirror) OnWorldRoleChanged(WorldRole.None, WorldRole.Mirror, _now);
    }

    protected override void OnWorldRoleChanged(WorldRole old, WorldRole role, long now)
    {
        _scanning.Clear();
        _identityAsked.Clear();
        _inLastScan.Clear();
        _foreignByKind.Clear();
        _foreignConfirmed.Clear();
        ForeignTraffic = 0;
        if (role == WorldRole.Mirror)
        {
            // Wait (a little) for the installed-aircraft list, so stand-ins can be chosen well.
            _mirrorSince = now;
            _catalogForMirror = RequestAircraftCatalog();
            Mirror.Ready = !_catalogForMirror;
        }
    }

    private void OnMirrorCatalog(List<(string Title, string Livery)> list)
    {
        if (WorldRole != WorldRole.Mirror) return;
        Mirror.SetInstalledModels(list);
        Mirror.Ready = true;
    }

    /// <summary>Scans, then the mirror and the world follower; called every pump.</summary>
    private void PumpWorld(long now)
    {
        if (WorldRole == WorldRole.Mirror && !Mirror.Ready && now - _mirrorSince >= CatalogWaitMs) Mirror.Ready = true;
        int interval = WorldRole switch
        {
            WorldRole.Source => TrafficScanIntervalMs,
            WorldRole.Mirror => ForeignScanIntervalMs,
            _ => int.MaxValue,
        };
        if (interval != int.MaxValue && now - _lastScan >= interval)
        {
            foreach (uint req in new[] { ReqTrafficAirplane, ReqTrafficHelicopter })
            {
                // One request of each kind at a time; a lost answer is retried after 2 s.
                if (_scanning.TryGetValue(req, out var s) && now - s.Started < 2000) continue;
                _scanning[req] = ([], now);
                var type = req == ReqTrafficAirplane ? SimObjectType.Aircraft : SimObjectType.Helicopter;
                Call(SimConnect_RequestDataOnSimObjectType(_h, req, DefTrafficRead, Math.Min(TrafficRadiusMeters, MaxRadiusMeters), type), $"scan AI {type}");
            }
            _lastScan = now;
        }
        StepWorld(now);
        if (_ledgerDirty) SaveLedger();
    }

    private void OnTrafficData(Recv r, uint req)
    {
        if (!_scanning.TryGetValue(req, out var scan)) return;
        uint objectId = r.U32(RecvLayout.DataObjectId);
        uint entry = r.U32(RecvLayout.DataEntryNumber), outOf = r.U32(RecvLayout.DataOutOf);
        if (outOf > 0 && r.Length >= RecvLayout.Data + TrafficReadLayout.Length * 8 && !IsUser(objectId))
        {
            Span<double> f = stackalloc double[TrafficReadLayout.Length];
            for (int i = 0; i < f.Length; i++) f[i] = r.F64(RecvLayout.Data + i * 8);
            var pose = new FlightSample(f[0], f[1], f[2], f[3], f[4], f[5], f[6], f[7], f[8], f[9], f[10], f[11], f[12], f[13], f[14], f[15] != 0);
            scan.Objects.Add((objectId, new TrafficSample(pose, (ushort)Math.Clamp(f[16], 0, ushort.MaxValue), f[17] != 0)));
        }
        if (outOf > 0 && entry < outOf) return;
        _scanning.Remove(req);
        // Until the user aircraft's own id is known it cannot be told apart from AI reliably
        // (it is in every scan): a scan from before then is not used at all.
        if (_userObjectId == 0) return;
        var kind = req == ReqTrafficAirplane ? TrafficKind.Airplane : TrafficKind.Helicopter;
        if (WorldRole == WorldRole.Source) OnSourceScan(kind, scan.Objects);
        else if (WorldRole == WorldRole.Mirror) OnForeignScan(kind, scan.Objects);
        TrafficScanned?.Invoke(kind, scan.Objects);
    }

    private bool IsUser(uint objectId) => objectId == ObjectIdUser || (_userObjectId != 0 && objectId == _userObjectId);

    private int _scanCountAirplane, _scanCountHeli;

    private void OnSourceScan(TrafficKind kind, List<(uint Id, TrafficSample S)> objects)
    {
        OnTrafficScan(kind, objects, _now);
        if (kind == TrafficKind.Airplane) _scanCountAirplane = objects.Count;
        else _scanCountHeli = objects.Count;
        TrafficSeen = _scanCountAirplane + _scanCountHeli;
        // Ask what an aircraft is when its id (re)appears, and again now and then: MSFS reuses ids.
        var inScan = _inLastScan.GetValueOrDefault(kind) ?? [];
        foreach (var (id, _) in objects)
        {
            bool reappeared = !inScan.Contains(id) && !_inLastScan.Any(kv => kv.Key != kind && kv.Value.Contains(id));
            if (!reappeared && _identityAsked.TryGetValue(id, out long at) && _now - at < IdentityRefreshMs) continue;
            _identityAsked[id] = _now;
            uint req = ReqIdentityBase + _nextIdentityReq++ % ReqIdentityRange;
            _identityRequests[req] = (id, kind);
            Call(SimConnect_RequestDataOnSimObject(_h, req, DefTrafficIdentity, id, Period.Once, DataRequestFlag.Default, 0, 0, 0), $"identify AI object {id}");
        }
        _inLastScan[kind] = [.. objects.Select(o => o.Id)];
        foreach (uint id in _identityAsked.Keys.Where(id => !_inLastScan.Values.Any(v => v.Contains(id))).ToList()) _identityAsked.Remove(id);
        // An identity request whose answer never came (object gone meanwhile) is forgotten after a while.
        if (_identityRequests.Count > 2000) _identityRequests.Clear();
    }

    private readonly Dictionary<TrafficKind, HashSet<uint>> _inLastScan = [];

    /// <summary>FO: per kind, AI aircraft here that SkyLink did not create (last scan), and those seen in two scans in a row.</summary>
    private readonly Dictionary<TrafficKind, HashSet<uint>> _foreignByKind = [];
    private readonly Dictionary<TrafficKind, int> _foreignConfirmed = [];

    private void OnForeignScan(TrafficKind kind, List<(uint Id, TrafficSample S)> objects)
    {
        // Two scans in a row, so an aircraft SkyLink is just creating (id not known yet) never counts.
        var now = objects.Select(o => o.Id).Where(id => !Mirror.IsOurs(id) && !_injected.ContainsKey(id)).ToHashSet();
        var before = _foreignByKind.GetValueOrDefault(kind) ?? [];
        _foreignByKind[kind] = now;
        _foreignConfirmed[kind] = now.Count(before.Contains);
        int confirmed = _foreignConfirmed.Values.Sum();
        if (confirmed > 0 && ForeignTraffic == 0)
            Info($"WARNING: {confirmed} AI aircraft in your MSFS were not created by SkyLink. Turn MSFS AI traffic off (the Captain's traffic is mirrored for you).");
        ForeignTraffic = confirmed;
    }

    private void OnIdentityData(Recv r, uint req)
    {
        if (!_identityRequests.Remove(req, out var who)) return;
        int off = RecvLayout.Data;
        var s = new string[TrafficIdentityLayout.Length];
        for (int i = 0; i < s.Length; i++)
        {
            int size = TrafficIdentityLayout[i].Size;
            s[i] = off + size <= r.Length ? r.AnsiString(off, size).Trim() : "";
            off += size;
        }
        var identity = new TrafficIdentity(who.Kind, s[0], s[5], s[1], s[2], s[3], s[4]);
        OnTrafficIdentity(who.ObjectId, identity);
        TrafficIdentified?.Invoke(who.ObjectId, identity);
    }

    private void OnWorldData(Recv r)
    {
        if (r.Length < RecvLayout.Data + WorldReadLayout.Length * 8) return;
        Span<double> f = stackalloc double[WorldReadLayout.Length];
        for (int i = 0; i < f.Length; i++) f[i] = r.F64(RecvLayout.Data + i * 8);
        OnWorldRead(new WorldSample(WorldSample.ToUnix(f[0], f[1], f[2]), f[3], _paused, f[4], f[5], f[6], f[7]), _now);
    }

    private void OnUserObjectId(uint objectId)
    {
        if (objectId == 0 || objectId == _userObjectId) return;
        _userObjectId = objectId;
        Info($"user aircraft is SimConnect object {objectId}");
    }

    private bool OnWorldEvent(uint eventId, uint data)
    {
        switch (eventId)
        {
            case EvtPause:
                _paused = data != 0;
                return true;
            case EvtObjectRemoved:
                if (_injected.Remove(data))
                {
                    _ledgerDirty = true;
                    Mirror.OnLocalObjectGone(data); // MSFS removed one of ours (e.g. a flight reload): respawn it
                }
                return true;
        }
        return false;
    }

    private void OnAssignedObjectId(Recv r)
    {
        uint req = r.U32(RecvLayout.AssignedRequestId), objectId = r.U32(RecvLayout.AssignedObjectId);
        if (!_spawnByRequest.Remove(req, out ulong token)) return;
        foreach (var kv in _spawnRequestBySendId.Where(kv => kv.Value == req).ToList()) _spawnRequestBySendId.Remove(kv.Key);
        _injected[objectId] = (_pendingTitles.GetValueOrDefault(req).Title ?? "", _pendingTitles.GetValueOrDefault(req).Tail ?? "", long.MinValue / 2);
        _pendingTitles.Remove(req);
        _ledgerDirty = true;
        // Ours now: no AI flight model, frozen, moved by SkyLink.
        Call(SimConnect_AIReleaseControl(_h, objectId, ReqTrafficRelease), $"release AI control of {objectId}");
        FreezeInjected(objectId);
        Mirror.OnSpawned(token, objectId);
    }

    private readonly Dictionary<uint, (string Title, string Tail)> _pendingTitles = [];

    /// <summary>An exception for one of our create calls: that model cannot be created here.</summary>
    private bool OnSpawnException(uint code, uint sendId)
    {
        if (!_spawnRequestBySendId.Remove(sendId, out uint req)) return false;
        if (_spawnByRequest.Remove(req, out ulong token))
        {
            var (title, _) = _pendingTitles.GetValueOrDefault(req);
            _pendingTitles.Remove(req);
            if (_warnedSpawn.Add(title ?? ""))
                Info($"cannot create AI aircraft '{title}' here ({ExceptionName(code)}); trying a stand-in");
            Mirror.OnSpawnFailed(token);
        }
        return true;
    }

    protected override void SpawnTrafficObject(SpawnTraffic spawn, long now)
    {
        if (!_open)
        {
            Mirror.OnSpawnFailed(spawn.Token);
            return;
        }
        var p = spawn.Pose;
        double gsKts = Math.Sqrt(p.VWorldEastFps * p.VWorldEastFps + p.VWorldNorthFps * p.VWorldNorthFps) * 0.592484;
        var init = new InitPosition
        {
            Latitude = p.LatDeg, Longitude = p.LonDeg, Altitude = p.AltFt,
            Pitch = p.PitchDeg, Bank = p.BankDeg, Heading = p.HeadingDeg,
            OnGround = p.OnGround ? 1u : 0u, Airspeed = (uint)Math.Round(gsKts),
        };
        uint req = ReqSpawnBase + _nextSpawnReq++ % ReqSpawnRange;
        int hr = SimConnect_AICreateNonATCAircraft_EX1(_h, spawn.Title, spawn.Livery, spawn.TailNumber, init, req);
        if (hr != SOk || SimConnect_GetLastSentPacketID(_h, out uint sendId) != SOk)
        {
            Mirror.OnSpawnFailed(spawn.Token);
            return;
        }
        _sent[sendId] = $"create AI aircraft '{spawn.Title}'";
        _spawnByRequest[req] = spawn.Token;
        _spawnRequestBySendId[sendId] = req;
        _pendingTitles[req] = (spawn.Title, spawn.TailNumber);
        if (spawn.StandIn && _warnedSpawn.Add("standin:" + spawn.Title))
            Info($"showing an AI aircraft as '{spawn.Title}' (the Captain's model is not installed here)");
    }

    private void FreezeInjected(uint objectId)
    {
        foreach (var e in FreezeEvents) TransmitTo(objectId, e, 1);
        if (_injected.TryGetValue(objectId, out var o)) _injected[objectId] = o with { FrozenAt = _now };
    }

    protected override unsafe void MoveTrafficObject(uint localId, FlightSample pose, long now)
    {
        if (!_open || !_injected.TryGetValue(localId, out var o)) return;
        if (now - o.FrozenAt >= InjectedFreezeReassertMs) FreezeInjected(localId);
        double* d = stackalloc double[6];
        d[0] = pose.LatDeg; d[1] = pose.LonDeg; d[2] = pose.AltFt; d[3] = pose.PitchDeg; d[4] = pose.BankDeg; d[5] = pose.HeadingDeg;
        Call(SimConnect_SetDataOnSimObject(_h, DefTrafficPose, localId, 0, 0, 6 * sizeof(double), d), "move mirrored AI aircraft");
    }

    protected override void SetTrafficObjectAppearance(uint localId, ushort lights, bool gearDown)
    {
        if (!_open || !_injected.ContainsKey(localId)) return;
        foreach (var (bit, e) in LightEvents) TransmitTo(localId, e, (lights & bit) != 0 ? 1u : 0u);
        TransmitTo(localId, "GEAR_SET", gearDown ? 1u : 0u);
    }

    protected override void RemoveTrafficObject(uint localId)
    {
        _injected.Remove(localId);
        _ledgerDirty = true;
        if (!_open) return;
        Call(SimConnect_AIRemoveObject(_h, localId, ReqTrafficRemove), $"remove mirrored AI aircraft {localId}");
    }

    protected override void ApplyWorldAction(WorldAction action)
    {
        if (!_open) return;
        switch (action)
        {
            case SetPause p:
                TransmitKEvent("PAUSE_SET", p.Paused ? 1u : 0u);
                break;
            case StepSimRate r:
                TransmitKEvent(r.Direction > 0 ? "SIM_RATE_INCR" : "SIM_RATE_DECR", 0);
                break;
            case SetZuluTime t:
                TransmitKEvent("ZULU_YEAR_SET", (uint)t.Year);
                TransmitKEvent("ZULU_DAY_SET", (uint)t.DayOfYear);
                TransmitKEvent("ZULU_HOURS_SET", (uint)t.Hour);
                TransmitKEvent("ZULU_MINUTES_SET", (uint)t.Minute);
                break;
        }
    }

    private void TransmitTo(uint objectId, string name, uint data)
    {
        if (!_open || !_eventIds.TryGetValue(name, out uint id)) return;
        Call(SimConnect_TransmitClientEvent_EX1(_h, objectId, id, GroupPriorityHighest, EventFlagGroupIdIsPriority, data, 0, 0, 0, 0), $"K:{name} to AI object {objectId}");
    }

    /// <summary>Remove every object SkyLink created, while the connection is still open.</summary>
    private void CloseWorld()
    {
        Mirror.Clear();
        StepTraffic(_now);
        if (_open)
            foreach (uint id in _injected.Keys.ToList())
                Call(SimConnect_AIRemoveObject(_h, id, ReqTrafficRemove), $"remove mirrored AI aircraft {id}");
        // Removal is requested for every object (or MSFS is gone, and they with it): nothing to clean up later.
        DeleteLedger();
        _injected.Clear();
        _spawnByRequest.Clear();
        _spawnRequestBySendId.Clear();
        _pendingTitles.Clear();
        _identityRequests.Clear();
        _identityAsked.Clear();
        _scanning.Clear();
        _inLastScan.Clear();
        _foreignByKind.Clear();
        _foreignConfirmed.Clear();
        _userObjectId = 0;
        _paused = false;
        OnTrafficLost();
    }

    // ------------------------------------------------------------------ ghost ledger

    private sealed record LedgerEntry(uint Id, string Title, string Tail);
    private sealed record Ledger(int Pid, long StartTicks, List<LedgerEntry> Objects);

    public static string LedgerPath => Path.Combine(
        Environment.GetEnvironmentVariable("SKYLINK_DATA") is { Length: > 0 } d ? d : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SkyLink"),
        "injected-traffic.json");

    private static Process? FindMsfs()
    {
        foreach (var name in new[] { "FlightSimulator2024", "FlightSimulator" })
            if (Process.GetProcessesByName(name).FirstOrDefault() is { } p) return p;
        return null;
    }

    private void SaveLedger()
    {
        _ledgerDirty = false;
        try
        {
            if (_injected.Count == 0)
            {
                DeleteLedger();
                return;
            }
            using var msfs = FindMsfs();
            if (msfs is null) return;
            var ledger = new Ledger(msfs.Id, msfs.StartTime.ToUniversalTime().Ticks, [.. _injected.Select(kv => new LedgerEntry(kv.Key, kv.Value.Title, kv.Value.Tail))]);
            Directory.CreateDirectory(Path.GetDirectoryName(LedgerPath)!);
            File.WriteAllText(LedgerPath, JsonSerializer.Serialize(ledger));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            WarnOnce("ledger", "cannot record created AI aircraft for cleanup: " + e.Message);
        }
    }

    private static void DeleteLedger()
    {
        try { File.Delete(LedgerPath); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// A previous SkyLink may have died with objects in this very MSFS session: ask each recorded
    /// id what it is, and remove it only if it is still the aircraft SkyLink created (same title
    /// and tail number), never an object MSFS has since given the same id.
    /// </summary>
    private void CleanUpGhosts()
    {
        Ledger? ledger;
        try
        {
            if (!File.Exists(LedgerPath)) return;
            ledger = JsonSerializer.Deserialize<Ledger>(File.ReadAllText(LedgerPath));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException)
        {
            return;
        }
        using var msfs = FindMsfs();
        bool sameSession = ledger is not null && msfs is not null && msfs.Id == ledger.Pid && msfs.StartTime.ToUniversalTime().Ticks == ledger.StartTicks;
        DeleteLedger();
        if (!sameSession || ledger is null) return;
        uint req = ReqGhostBase;
        foreach (var o in ledger.Objects)
        {
            _ghostChecks[req] = (o.Id, o.Title, o.Tail);
            Call(SimConnect_RequestDataOnSimObject(_h, req++, DefTrafficIdentity, o.Id, Period.Once, DataRequestFlag.Default, 0, 0, 0), $"check leftover AI object {o.Id}");
        }
        Info($"checking {ledger.Objects.Count} AI aircraft a previous SkyLink left in this MSFS session");
    }

    private void OnGhostData(Recv r, uint req)
    {
        if (!_ghostChecks.Remove(req, out var g)) return;
        string title = r.AnsiString(RecvLayout.Data, 256).Trim();
        string tail = r.AnsiString(RecvLayout.Data + 256, 32).Trim();
        if (r.U32(RecvLayout.DataObjectId) != g.Id || title != g.Title || !string.Equals(tail, g.Tail, StringComparison.OrdinalIgnoreCase)) return;
        Call(SimConnect_AIRemoveObject(_h, g.Id, ReqTrafficRemove), $"remove leftover AI aircraft {g.Id}");
        Info($"removed AI aircraft '{title}' ({tail}) left behind by a previous SkyLink");
    }

    /// <summary>Route a SIMOBJECT_DATA(_BYTYPE) message to the world, or return false.</summary>
    private bool OnWorldDataMessage(Recv r, uint req)
    {
        if (req is ReqTrafficAirplane or ReqTrafficHelicopter) OnTrafficData(r, req);
        else if (req == ReqWorld) OnWorldData(r);
        else if (req >= ReqIdentityBase && req < ReqIdentityBase + ReqIdentityRange) OnIdentityData(r, req);
        else if (req >= ReqGhostBase && req < ReqGhostBase + 10_000) OnGhostData(r, req);
        else return false;
        return true;
    }
}
