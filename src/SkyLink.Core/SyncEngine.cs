namespace SkyLink.Core;

public sealed class EngineOptions
{
    /// <summary>Time between write attempts while waiting for a remote value to show up in the sim.</summary>
    public int RetryIntervalMs { get; init; } = 300;
    public int MaxWriteAttempts { get; init; } = 4;
    /// <summary>After this long from the first write, an unverified write is declared rejected.</summary>
    public int VerifyWindowMs { get; init; } = 1500;

    public int DigestIntervalMs { get; init; } = 1000;
    /// <summary>A group must mismatch in this many consecutive digests before a snapshot is sent.</summary>
    public int MismatchStreakForRepair { get; init; } = 2;
    public int SnapshotChunk { get; init; } = 48;

    public int ControlFrameIntervalMs { get; init; } = 33;
    public int ControlHeartbeatMs { get; init; } = 250;

    public int AnalogFlushIntervalMs { get; init; } = 50;
    public int AnalogSettleMs { get; init; } = 200;

    public int EventRetransmitMs { get; init; } = 500;
    public int EventBatchMax { get; init; } = 128;
    /// <summary>Unacknowledged events older than this are discarded instead of delivered late.</summary>
    public int MaxEventAgeMs { get; init; } = 60_000;

    public int HelloRetransmitMs { get; init; } = 1000;

    /// <summary>How often the pilot flying sends position and motion (default ~30 Hz).</summary>
    public int FlightStateIntervalMs { get; init; } = 33;

    public bool AutoGrantControl { get; init; } = true;
    public int GrantRetransmitMs { get; init; } = 500;
    public int GrantTimeoutMs { get; init; } = 5000;
}

public enum VarStatus : byte { Unknown, Ok, Pending, Rejected }

public sealed class SyncHealth
{
    public long LocalChanges;
    public long RemoteApplied;
    public long WritesIssued;
    public long WritesVerified;
    public long WriteRejections;
    public long EventsSent;
    public long EventsApplied;
    public long EventDuplicates;
    public long EventsExpired;
    public long DigestMismatches;
    public long SnapshotsSent;
    public long StaleIgnored;
    public int DerivedMismatchGroups;
    public bool Incompatible;
    public string? IncompatibleReason;
    public VarStatus[] Status = [];
}

/// <summary>
/// The per-seat replication engine. Pure and single-threaded: the host calls the On*/Tick
/// methods with a monotonic clock and drains <see cref="Outbox"/> (to the network) and
/// <see cref="SimCommands"/> (to the simulator). No I/O happens here, which is what makes it
/// testable under simulated packet loss.
/// </summary>
public sealed class SyncEngine
{
    public const int ProtocolVersion = 3; // 2: FlightState, 3: shared world (traffic, time, rate, pause)

    private readonly SyncSchema _schema;
    private readonly EngineOptions _o;
    private readonly byte _seat;
    private readonly bool _isHost;
    private readonly ulong _instanceId;
    private readonly HlcClock _clock;
    private readonly int _n;

    // Replicated store (State / Analog).
    private readonly double[] _value;
    private readonly Hlc[] _stamp;
    private readonly bool[] _has;

    // Last value seen in the local sim.
    private readonly double[] _sim;
    private readonly bool[] _hasSim;

    // Remote values being written into the local sim, awaiting read-back.
    private readonly PendingWrite[] _pending;
    private readonly HashSet<int> _pendingSet = [];

    // Values the sim refused to take. Samples equal to these are known divergence, not changes.
    private readonly bool[] _rejected;
    private readonly double[] _rejectedValue;

    private readonly HashSet<int> _stateDirty = [];
    private readonly Dictionary<int, long> _analogActive = [];
    private long _lastAnalogFlush = long.MinValue / 2;
    private bool _analogDirty;

    // Session
    private ulong _sessionId;
    private bool _connected;
    private bool _established;
    private bool _peerConfirmed;
    private long _lastHelloSent = long.MinValue / 2;
    private byte _peerSeat;
    private ulong _peerInstance;
    private bool _initialSync;

    // Digest
    private long _lastDigest = long.MinValue / 2;
    private readonly int[] _mismatchStreak;
    private readonly bool[] _groupMatchedOnce;
    private readonly long[] _lastSnapshot;

    // Events out
    private readonly List<OutEvent> _eventOutbox = [];
    private ulong _nextEventSeq;
    // Events in
    private ulong _lastRecvSeq;
    private readonly SortedDictionary<ulong, EventEntry> _eventBuffer = [];

    // Authority / controls
    private Authority _auth;
    private bool _controlDirty;
    private long _lastControlFrame = long.MinValue / 2;
    private ulong _controlSeq;
    private ulong _lastControlSeqRecv;
    private FlightSample? _flightLatest;
    private long _lastFlightSend = long.MinValue / 2;
    private ulong _flightSeq;
    private ulong _lastFlightSeqRecv;
    private Authority? _grant;
    private long _grantSentAt;
    private long _grantDeadline;

    public Queue<Outgoing> Outbox { get; } = new();
    public Queue<SimCommand> SimCommands { get; } = new();
    public SyncHealth Health { get; }
    public Action<string>? Log { get; set; }

    public SyncEngine(SyncSchema schema, byte seat, bool isHost, ulong instanceId, ulong sessionId = 0, EngineOptions? options = null)
    {
        if (instanceId == 0) throw new ArgumentException("instanceId must be non-zero.");
        if (isHost && sessionId == 0) throw new ArgumentException("The host must create a non-zero sessionId.");
        _schema = schema;
        _o = options ?? new EngineOptions();
        _seat = seat;
        _isHost = isHost;
        _instanceId = instanceId;
        _sessionId = isHost ? sessionId : 0;
        _clock = new HlcClock(seat);
        _n = schema.Vars.Count;

        _value = new double[_n];
        _stamp = new Hlc[_n];
        _has = new bool[_n];
        _sim = new double[_n];
        _hasSim = new bool[_n];
        _pending = new PendingWrite[_n];
        _rejected = new bool[_n];
        _rejectedValue = new double[_n];
        _mismatchStreak = new int[schema.Groups.Count];
        _groupMatchedOnce = new bool[schema.Groups.Count];
        _lastSnapshot = new long[schema.Groups.Count];
        Array.Fill(_lastSnapshot, long.MinValue / 2);

        Health = new SyncHealth { Status = new VarStatus[_n] };
        _auth = new Authority(0, isHost ? seat : (byte)0);
    }

    public byte Seat => _seat;
    public bool IsHost => _isHost;
    public ulong SessionId => _sessionId;
    public bool IsEstablished => _established;
    public bool IsInitialSync => _initialSync;
    public Authority Authority => _auth;
    public bool IsPilotFlying => _auth.PfSeat == _seat;
    public bool ControlRequestPending { get; private set; }
    public int PendingWriteCount => _pendingSet.Count;
    public int UnackedEventCount => _eventOutbox.Count;

    public bool TryGetEntry(int v, out StateEntry entry)
    {
        entry = new StateEntry(v, _value[v], _stamp[v]);
        return _has[v];
    }

    /// <summary>Announce the initial authority to the sim adapter (freeze if not flying).</summary>
    public void Start() => SimCommands.Enqueue(new AuthorityChanged(IsPilotFlying, _auth.Epoch));

    // ------------------------------------------------------------------ connection

    public void OnConnected(long now)
    {
        _connected = true;
        _established = false;
        _peerConfirmed = false;
        SendHello(now);
    }

    public void OnDisconnected(long now)
    {
        _connected = false;
        _established = false;
        _peerConfirmed = false;
        Array.Clear(_mismatchStreak);
    }

    private void SendHello(long now)
    {
        _lastHelloSent = now;
        Send(Channel.ReliableOrdered, new Hello(ProtocolVersion, _schema.Hash, _instanceId, _sessionId, _seat, _isHost, _auth));
    }

    // ------------------------------------------------------------------ local input

    /// <summary>Report the current simulator value of a variable. Call for every non-event var each sample tick.</summary>
    public void OnSimSample(int v, double s, long now)
    {
        var cls = _schema.ClassOf[v];
        bool first = !_hasSim[v];
        bool changedFromSim = first || !Eq(v, s, _sim[v]);
        _sim[v] = s;
        _hasSim[v] = true;

        switch (cls)
        {
            case SyncClass.Control:
                if (IsPilotFlying && changedFromSim) _controlDirty = true;
                return;
            case SyncClass.Event:
                throw new InvalidOperationException($"'{_schema.Vars[v].Id}' is an event; use OnLocalEvent.");
            case SyncClass.Derived:
            case SyncClass.Local:
                return;
        }

        ref var p = ref _pending[v];
        if (p.Active)
        {
            if (Eq(v, s, p.Target))
            {
                ClearPending(v);
                Health.WritesVerified++;
                Health.Status[v] = VarStatus.Ok;
                return;
            }
            if (!p.HasPrev || Eq(v, s, p.Prev)) return; // write not visible yet
            ClearPending(v); // the sim moved somewhere else: a genuine local change
        }

        if (_rejected[v])
        {
            if (Eq(v, s, _rejectedValue[v])) return; // known divergence, already reported
            _rejected[v] = false;
            if (_has[v] && Eq(v, s, _value[v]))
            {
                Health.Status[v] = VarStatus.Ok; // sim caught up on its own
                return;
            }
        }

        if (_has[v] && Eq(v, s, _value[v])) return; // echo of a value we already hold

        LocalChange(v, s, now);
    }

    private void LocalChange(int v, double s, long now)
    {
        // The joiner's first observation is a baseline: it never overrides the host's cockpit.
        Hlc stamp = !_has[v] && !_isHost ? Hlc.Baseline(_seat) : _clock.Now(now);
        _value[v] = s;
        _stamp[v] = stamp;
        _has[v] = true;
        Health.Status[v] = VarStatus.Ok;
        if (stamp.IsBaseline) return;

        Health.LocalChanges++;
        if (_schema.ClassOf[v] == SyncClass.Analog)
        {
            _analogActive[v] = now;
            _analogDirty = true;
        }
        else
        {
            _stateDirty.Add(v);
        }
    }

    /// <summary>A cockpit command the local pilot just triggered (button press, FMS key, ...).</summary>
    public void OnLocalEvent(int v, double param, long now)
    {
        if (_schema.ClassOf[v] != SyncClass.Event)
            throw new InvalidOperationException($"'{_schema.Vars[v].Id}' is not an event.");
        _eventOutbox.Add(new OutEvent(++_nextEventSeq, v, param, now));
        Health.EventsSent++;
    }

    /// <summary>The local aircraft's position and motion (only used while this seat is flying).</summary>
    public void OnFlightSample(FlightSample sample, long now)
    {
        if (IsPilotFlying) _flightLatest = sample;
    }

    public long FlightStatesSent { get; private set; }
    public long FlightStatesApplied { get; private set; }

    // ------------------------------------------------------------------ control authority

    public void RequestControl(long now)
    {
        if (IsPilotFlying || !_established) return;
        Send(Channel.ReliableOrdered, new AuthorityMsg(AuthorityKind.Request, _auth));
    }

    public void GrantControl(long now)
    {
        ControlRequestPending = false;
        if (!IsPilotFlying || !_established) return;
        _grant = new Authority(_auth.Epoch + 1, _peerSeat);
        _grantSentAt = now;
        _grantDeadline = now + _o.GrantTimeoutMs;
        Send(Channel.ReliableOrdered, new AuthorityMsg(AuthorityKind.Grant, _grant.Value));
    }

    /// <summary>Take the controls without waiting for the other pilot (priority takeover).</summary>
    public void TakeControl(long now)
    {
        if (IsPilotFlying) return;
        SetAuthority(new Authority(_auth.Epoch + 1, _seat));
        if (_established) Send(Channel.ReliableOrdered, new AuthorityMsg(AuthorityKind.Takeover, _auth));
    }

    private void ObserveAuthority(Authority a)
    {
        if (a > _auth) SetAuthority(a);
    }

    private void SetAuthority(Authority a)
    {
        bool wasPf = IsPilotFlying;
        _auth = a;
        _lastControlSeqRecv = 0;
        _lastFlightSeqRecv = 0;
        _flightLatest = null;
        if (_grant is { } g && a >= g) _grant = null;
        if (wasPf != IsPilotFlying)
        {
            ControlRequestPending = false;
            if (IsPilotFlying) _controlDirty = true;
            SimCommands.Enqueue(new AuthorityChanged(IsPilotFlying, a.Epoch));
            Log?.Invoke($"seat {_seat}: authority {a.Epoch}/{a.PfSeat} -> {(IsPilotFlying ? "PILOT FLYING" : "pilot monitoring")}");
        }
    }

    // ------------------------------------------------------------------ network input

    public void OnMessage(Message m, long now)
    {
        if (!_connected) return;
        if (m is Hello h)
        {
            HandleHello(h, now);
            return;
        }
        if (!_established) return;
        _peerConfirmed = true;

        switch (m)
        {
            case StateDelta d:
                foreach (var e in d.Entries) ApplyRemote(e, now);
                break;
            case Digest d:
                HandleDigest(d, now);
                break;
            case EventBatch b:
                HandleEvents(b);
                break;
            case EventAck a:
                if (a.AckerInstance == _peerInstance) _eventOutbox.RemoveAll(e => e.Seq <= a.UpToSeq);
                break;
            case ControlFrame f:
                HandleControlFrame(f);
                break;
            case AuthorityMsg a:
                HandleAuthority(a, now);
                break;
            case FlightState f:
                HandleFlightState(f);
                break;
        }
    }

    private void HandleHello(Hello h, long now)
    {
        if (h.ProtocolVersion != ProtocolVersion || h.SchemaHash != _schema.Hash)
        {
            Health.Incompatible = true;
            Health.IncompatibleReason = h.ProtocolVersion != ProtocolVersion
                ? $"Protocol version mismatch (peer {h.ProtocolVersion}, local {ProtocolVersion})."
                : "Aircraft definition mismatch: both pilots must load the same aircraft and definition version.";
            return;
        }
        if (h.IsHost == _isHost || h.Seat == _seat)
        {
            Health.Incompatible = true;
            Health.IncompatibleReason = "Both sides are configured as the same seat/role.";
            return;
        }
        Health.Incompatible = false;
        Health.IncompatibleReason = null;

        bool wasEstablished = _established;
        _peerSeat = h.Seat;

        if (h.InstanceId != _peerInstance)
        {
            // A new peer process: nothing we queued for the old one is meaningful any more.
            _peerInstance = h.InstanceId;
            _lastRecvSeq = 0;
            _eventBuffer.Clear();
            if (_eventOutbox.Count > 0) Health.EventsExpired += _eventOutbox.Count;
            _eventOutbox.Clear();
            _lastControlSeqRecv = 0;
            _lastFlightSeqRecv = 0;
        }

        if (!_isHost && h.SessionId != _sessionId)
        {
            // Joining a (new) session: the host's cockpit is the truth.
            _sessionId = h.SessionId;
            DemoteToBaseline();
            _initialSync = true;
            Array.Clear(_groupMatchedOnce);
            var forced = h.Authority;
            if (forced != _auth)
            {
                _auth = new Authority(int.MinValue, 0); // let SetAuthority emit the change
                SetAuthority(forced);
            }
            Log?.Invoke($"seat {_seat}: joined session {_sessionId:X}, initial sync started");
        }
        else if (h.SessionId == _sessionId)
        {
            ObserveAuthority(h.Authority);
        }

        _established = true;
        if (!wasEstablished)
        {
            // Make sure the peer gets our Hello too (it may have been lost), then compare state.
            SendHello(now);
            SendDigest(now);
        }
    }

    private void DemoteToBaseline()
    {
        for (int v = 0; v < _n; v++)
        {
            if (_has[v]) _stamp[v] = Hlc.Baseline(_seat);
            if (_pending[v].Active) ClearPending(v);
            _rejected[v] = false;
            Health.Status[v] = _has[v] ? VarStatus.Ok : VarStatus.Unknown;
        }
        _stateDirty.Clear();
        _analogActive.Clear();
        _eventOutbox.Clear();
    }

    private void ApplyRemote(StateEntry e, long now)
    {
        int v = e.Var;
        if ((uint)v >= (uint)_n) return;
        var cls = _schema.ClassOf[v];
        if (cls != SyncClass.State && cls != SyncClass.Analog) return;
        if (e.Stamp.IsBaseline) return;

        _clock.Observe(e.Stamp, now);
        if (_has[v] && e.Stamp <= _stamp[v])
        {
            Health.StaleIgnored++;
            return;
        }

        _value[v] = e.Value;
        _stamp[v] = e.Stamp;
        _has[v] = true;
        _stateDirty.Remove(v);
        _analogActive.Remove(v);
        Health.RemoteApplied++;

        if (_hasSim[v] && Eq(v, _sim[v], e.Value))
        {
            if (_pending[v].Active) ClearPending(v);
            _rejected[v] = false;
            Health.Status[v] = VarStatus.Ok;
            return;
        }

        ref var p = ref _pending[v];
        if (p.Active)
        {
            p.Target = e.Value; // retarget; keep the original "before" value
            p.Started = false;
        }
        else
        {
            p = new PendingWrite { Active = true, Target = e.Value, Prev = _sim[v], HasPrev = _hasSim[v] };
            _pendingSet.Add(v);
        }
        _rejected[v] = false;
        Health.Status[v] = VarStatus.Pending;
    }

    private void HandleDigest(Digest d, long now)
    {
        if (d.SessionId != _sessionId) return;
        ObserveAuthority(d.Authority);
        if (d.GroupHashes.Length != _schema.Groups.Count) return;

        var (mine, derived) = ComputeHashes();
        bool allMatch = true;
        for (int g = 0; g < mine.Length; g++)
        {
            if (mine[g] == d.GroupHashes[g])
            {
                _mismatchStreak[g] = 0;
                _groupMatchedOnce[g] = true;
                continue;
            }
            allMatch = false;
            _mismatchStreak[g]++;
            bool urgent = d.InitialSync || _initialSync;
            if ((urgent || _mismatchStreak[g] >= _o.MismatchStreakForRepair) && now - _lastSnapshot[g] >= _o.DigestIntervalMs)
            {
                _lastSnapshot[g] = now;
                SendGroupSnapshot(g);
            }
        }

        int derivedMismatch = 0;
        if (d.DerivedHashes.Length == derived.Length)
            for (int g = 0; g < derived.Length; g++)
                if (derived[g] != d.DerivedHashes[g]) derivedMismatch++;
        Health.DerivedMismatchGroups = derivedMismatch;

        if (!allMatch) Health.DigestMismatches++;
        // Initial sync is over once every group has been identical at least once (pilots may
        // already be changing things, so "all groups at the same moment" may never happen)
        // and every write from the host's snapshot has landed.
        if (_initialSync && _pendingSet.Count == 0 && Array.TrueForAll(_groupMatchedOnce, m => m))
        {
            _initialSync = false;
            Log?.Invoke($"seat {_seat}: initial sync complete");
        }
    }

    private void SendGroupSnapshot(int g)
    {
        var chunk = new List<StateEntry>(_o.SnapshotChunk);
        for (int v = 0; v < _n; v++)
        {
            if (_schema.GroupOf[v] != g || !IsStored(v) || !_has[v] || _stamp[v].IsBaseline) continue;
            chunk.Add(new StateEntry(v, _value[v], _stamp[v]));
            if (chunk.Count == _o.SnapshotChunk)
            {
                Send(Channel.ReliableOrdered, new StateDelta(chunk.ToArray()));
                chunk.Clear();
            }
        }
        if (chunk.Count > 0) Send(Channel.ReliableOrdered, new StateDelta(chunk.ToArray()));
        Health.SnapshotsSent++;
    }

    private void HandleEvents(EventBatch b)
    {
        if (b.BaseSeq > _lastRecvSeq)
        {
            // The sender guarantees nothing at or below BaseSeq will ever arrive (acked or expired).
            _lastRecvSeq = b.BaseSeq;
            foreach (var k in _eventBuffer.Keys.Where(k => k <= b.BaseSeq).ToList()) _eventBuffer.Remove(k);
        }
        foreach (var e in b.Events)
        {
            if ((uint)e.Var >= (uint)_n || _schema.ClassOf[e.Var] != SyncClass.Event) continue;
            if (e.Seq <= _lastRecvSeq || !_eventBuffer.TryAdd(e.Seq, e)) Health.EventDuplicates++;
        }
        while (_eventBuffer.Remove(_lastRecvSeq + 1, out var next))
        {
            _lastRecvSeq++;
            SimCommands.Enqueue(new FireEvent(next.Var, next.Param));
            Health.EventsApplied++;
        }
        Send(Channel.ReliableOrdered, new EventAck(_instanceId, _lastRecvSeq));
    }

    private void HandleControlFrame(ControlFrame f)
    {
        if (f.Authority.PfSeat == _peerSeat) ObserveAuthority(f.Authority);
        if (IsPilotFlying || f.Authority != _auth || f.Seq <= _lastControlSeqRecv) return;
        _lastControlSeqRecv = f.Seq;
        foreach (var c in f.Values)
            if ((uint)c.Var < (uint)_n && _schema.ClassOf[c.Var] == SyncClass.Control)
                SimCommands.Enqueue(new WriteControl(c.Var, c.Value));
    }

    private void HandleFlightState(FlightState f)
    {
        if (f.Authority.PfSeat == _peerSeat) ObserveAuthority(f.Authority);
        if (IsPilotFlying || f.Authority != _auth || f.Seq <= _lastFlightSeqRecv) return;
        _lastFlightSeqRecv = f.Seq;
        FlightStatesApplied++;
        SimCommands.Enqueue(new FlightStateUpdate(f.Time, f.Sample));
    }

    private void HandleAuthority(AuthorityMsg m, long now)
    {
        switch (m.Kind)
        {
            case AuthorityKind.Request:
                if (IsPilotFlying && m.Authority == _auth)
                {
                    ControlRequestPending = true;
                    if (_o.AutoGrantControl) GrantControl(now);
                }
                break;
            case AuthorityKind.Grant:
                ObserveAuthority(m.Authority);
                if (m.Authority == _auth && IsPilotFlying)
                    Send(Channel.ReliableOrdered, new AuthorityMsg(AuthorityKind.Commit, _auth));
                break;
            case AuthorityKind.Commit:
            case AuthorityKind.Takeover:
                ObserveAuthority(m.Authority);
                break;
        }
    }

    // ------------------------------------------------------------------ tick

    public void Tick(long now)
    {
        ProcessPendingWrites(now);
        PruneExpiredEvents(now);

        if (!_connected) return;
        if (!_established || !_peerConfirmed)
        {
            if (now - _lastHelloSent >= _o.HelloRetransmitMs) SendHello(now);
            if (!_established) return;
        }

        FlushState();
        FlushAnalog(now);
        FlushEvents(now);
        FlushControls(now);
        FlushFlightState(now);

        if (_grant is { } g)
        {
            if (_auth >= g || now >= _grantDeadline) _grant = null;
            else if (now - _grantSentAt >= _o.GrantRetransmitMs)
            {
                _grantSentAt = now;
                Send(Channel.ReliableOrdered, new AuthorityMsg(AuthorityKind.Grant, g));
            }
        }

        if (now - _lastDigest >= _o.DigestIntervalMs) SendDigest(now);
    }

    private void ProcessPendingWrites(long now)
    {
        if (_pendingSet.Count == 0) return;
        int gate = int.MaxValue;
        if (_initialSync)
            foreach (int v in _pendingSet) gate = Math.Min(gate, _schema.GroupOrderOf[v]);

        List<int>? done = null;
        foreach (int v in _pendingSet)
        {
            if (_initialSync && _schema.GroupOrderOf[v] > gate) continue; // power before avionics
            ref var p = ref _pending[v];
            if (!p.Started)
            {
                p.Started = true;
                p.Attempts = 1;
                p.NextAttemptAt = now + _o.RetryIntervalMs;
                p.Deadline = now + _o.VerifyWindowMs;
                SimCommands.Enqueue(new WriteVar(v, p.Target, 0));
                Health.WritesIssued++;
            }
            else if (now >= p.Deadline)
            {
                (done ??= []).Add(v);
            }
            else if (now >= p.NextAttemptAt && p.Attempts < _o.MaxWriteAttempts)
            {
                p.NextAttemptAt = now + _o.RetryIntervalMs;
                SimCommands.Enqueue(new WriteVar(v, p.Target, p.Attempts));
                p.Attempts++;
                Health.WritesIssued++;
            }
        }

        if (done is null) return;
        foreach (int v in done)
        {
            double target = _pending[v].Target;
            ClearPending(v);
            Health.WriteRejections++;
            Health.Status[v] = VarStatus.Rejected;
            if (_hasSim[v])
            {
                _rejected[v] = true;
                _rejectedValue[v] = _sim[v];
            }
            Log?.Invoke($"seat {_seat}: sim rejected '{_schema.Vars[v].Id}' = {target} (still {(_hasSim[v] ? _sim[v] : double.NaN)})");
        }
    }

    private void ClearPending(int v)
    {
        _pending[v] = default;
        _pendingSet.Remove(v);
    }

    private void PruneExpiredEvents(long now)
    {
        int expired = 0;
        while (expired < _eventOutbox.Count && now - _eventOutbox[expired].CreatedAt > _o.MaxEventAgeMs) expired++;
        if (expired == 0) return;
        _eventOutbox.RemoveRange(0, expired);
        Health.EventsExpired += expired;
    }

    private void FlushState()
    {
        if (_stateDirty.Count == 0) return;
        SendEntries(Channel.ReliableOrdered, _stateDirty.Order());
        _stateDirty.Clear();
    }

    private void FlushAnalog(long now)
    {
        if (_analogActive.Count == 0) return;
        if (_analogDirty && now - _lastAnalogFlush >= _o.AnalogFlushIntervalMs)
        {
            // Every active knob goes in every packet, so a dropped or superseded packet loses nothing.
            _lastAnalogFlush = now;
            _analogDirty = false;
            SendEntries(Channel.Sequenced, _analogActive.Keys.Order());
        }
        List<int>? settled = null;
        foreach (var (v, changedAt) in _analogActive)
            if (now - changedAt >= _o.AnalogSettleMs) (settled ??= []).Add(v);
        if (settled is null) return;
        foreach (int v in settled) _analogActive.Remove(v);
        SendEntries(Channel.ReliableOrdered, settled); // final value, guaranteed
    }

    private void SendEntries(Channel channel, IEnumerable<int> vars)
    {
        var chunk = new List<StateEntry>(_o.SnapshotChunk);
        foreach (int v in vars)
        {
            chunk.Add(new StateEntry(v, _value[v], _stamp[v]));
            if (chunk.Count == _o.SnapshotChunk)
            {
                Send(channel, new StateDelta(chunk.ToArray()));
                chunk.Clear();
            }
        }
        if (chunk.Count > 0) Send(channel, new StateDelta(chunk.ToArray()));
    }

    private void FlushEvents(long now)
    {
        if (_eventOutbox.Count == 0) return;
        var batch = new List<EventEntry>();
        for (int i = 0; i < _eventOutbox.Count && batch.Count < _o.EventBatchMax; i++)
        {
            var e = _eventOutbox[i];
            if (e.LastSentAt is { } t && now - t < _o.EventRetransmitMs) continue;
            batch.Add(new EventEntry(e.Seq, e.Var, e.Param));
            _eventOutbox[i] = e with { LastSentAt = now };
        }
        if (batch.Count == 0) return;
        ulong baseSeq = _eventOutbox[0].Seq - 1;
        Send(Channel.ReliableOrdered, new EventBatch(baseSeq, batch.ToArray()));
    }

    private void FlushControls(long now)
    {
        if (!IsPilotFlying) return;
        long since = now - _lastControlFrame;
        if (!(_controlDirty && since >= _o.ControlFrameIntervalMs) && since < _o.ControlHeartbeatMs) return;
        var values = new List<ControlValue>();
        for (int v = 0; v < _n; v++)
            if (_schema.ClassOf[v] == SyncClass.Control && _hasSim[v]) values.Add(new ControlValue(v, _sim[v]));
        _lastControlFrame = now;
        _controlDirty = false;
        if (values.Count > 0) Send(Channel.Sequenced, new ControlFrame(_auth, ++_controlSeq, values.ToArray()));
    }

    private void FlushFlightState(long now)
    {
        if (!IsPilotFlying || _flightLatest is not { } s || now - _lastFlightSend < _o.FlightStateIntervalMs) return;
        _lastFlightSend = now;
        FlightStatesSent++;
        Send(Channel.Sequenced, new FlightState(_auth, ++_flightSeq, now, s));
    }

    private void SendDigest(long now)
    {
        _lastDigest = now;
        var (groups, derived) = ComputeHashes();
        Send(Channel.ReliableOrdered, new Digest(_sessionId, _auth, _initialSync, groups, derived));
    }

    /// <summary>Per-group hashes of the replicated store, and of derived sim values.</summary>
    public (ulong[] Groups, ulong[] Derived) ComputeHashes()
    {
        int gc = _schema.Groups.Count;
        var groups = new ulong[gc];
        var derived = new ulong[gc];
        Array.Fill(groups, Fnv64.Begin());
        Array.Fill(derived, Fnv64.Begin());
        for (int v = 0; v < _n; v++)
        {
            int g = _schema.GroupOf[v];
            if (IsStored(v))
            {
                if (!_has[v] || _stamp[v].IsBaseline) continue;
                ulong h = Fnv64.Add(groups[g], v);
                h = Fnv64.Add(h, _value[v]);
                h = Fnv64.Add(h, _stamp[v].Wall);
                h = Fnv64.Add(h, _stamp[v].Logical);
                groups[g] = Fnv64.Add(h, (int)_stamp[v].Node);
            }
            else if (_schema.ClassOf[v] == SyncClass.Derived && _hasSim[v])
            {
                double q = Math.Round(_sim[v] / _schema.EpsilonOf[v]);
                derived[g] = Fnv64.Add(Fnv64.Add(derived[g], v), q);
            }
        }
        return (groups, derived);
    }

    // ------------------------------------------------------------------ helpers

    private bool IsStored(int v) => _schema.ClassOf[v] is SyncClass.State or SyncClass.Analog;

    private bool Eq(int v, double a, double b)
    {
        if (double.IsNaN(a) || double.IsNaN(b)) return double.IsNaN(a) && double.IsNaN(b);
        return Math.Abs(a - b) <= _schema.EpsilonOf[v];
    }

    private void Send(Channel channel, Message m)
    {
        if (_connected) Outbox.Enqueue(new Outgoing(channel, m));
    }

    private struct PendingWrite
    {
        public bool Active;
        public double Target;
        public double Prev;
        public bool HasPrev;
        public bool Started;
        public int Attempts;
        public long NextAttemptAt;
        public long Deadline;
    }

    private readonly record struct OutEvent(ulong Seq, int Var, double Param, long CreatedAt, long? LastSentAt = null);
}
