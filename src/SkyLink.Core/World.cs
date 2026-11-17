namespace SkyLink.Core;

// One shared world: the Captain's MSFS is the single source of truth for AI traffic, time,
// simulation rate and pause; the First Officer's sim mirrors it. Everything here is pure (no I/O,
// caller-supplied clocks) so it can be tested under loss, reordering and restarts.

public enum TrafficKind : byte { Airplane = 0, Helicopter = 1 }

/// <summary>What an AI aircraft is: enough to spawn a look-alike in the other sim.</summary>
public sealed record TrafficIdentity(
    TrafficKind Kind,
    string Title,
    string Livery,
    string AtcId,
    string AtcModel,
    string Airline,
    string FlightNumber);

/// <summary>One AI aircraft's pose and visible state as read from the Captain's sim.</summary>
public readonly record struct TrafficSample(FlightSample Pose, ushort Lights, bool GearDown);

/// <summary>A traffic pose in a <see cref="TrafficFrame"/>; <see cref="AgeMs"/> is how old it was when the frame was sent.</summary>
public readonly record struct TrafficEntry(uint Id, ushort AgeMs, TrafficSample Sample);

public readonly record struct TrafficInfo(uint Id, TrafficIdentity Identity);

/// <summary>Time, rate, pause and the weather at the aircraft, read from one sim.</summary>
public readonly record struct WorldSample(
    double ZuluUnixSec,
    double SimRate,
    bool Paused,
    double SeaLevelPressureMb,
    double WindDirDeg,
    double WindKts,
    double TempC)
{
    /// <summary>MSFS reports zulu time as seconds since midnight, day of year (1-based) and year.</summary>
    public static double ToUnix(double zuluTimeSec, double dayOfYear, double year)
    {
        int y = (int)Math.Clamp(Math.Round(year), 1, 9998);
        var midnight = new DateTime(y, 1, 1, 0, 0, 0, DateTimeKind.Utc).AddDays(Math.Max(0, Math.Round(dayOfYear) - 1));
        return (midnight - DateTime.UnixEpoch).TotalSeconds + zuluTimeSec;
    }

    public DateTime Zulu => DateTime.UnixEpoch.AddSeconds(ZuluUnixSec);
}

public sealed class TrafficOptions
{
    /// <summary>The Captain mirrors at most this many AI aircraft, nearest first.</summary>
    public int MaxObjects { get; init; } = 120;
    /// <summary>Objects per <see cref="TrafficFrame"/> (keeps every frame well inside one UDP packet).</summary>
    public int FrameBatch { get; init; } = 16;
    public int AnnounceBatch { get; init; } = 6;
    /// <summary>An object absent from this many consecutive complete scans is gone.</summary>
    public int MissingScansForRemoval { get; init; } = 2;
    /// <summary>An (empty) frame is sent at least this often, so the mirror knows the source is alive.</summary>
    public int HeartbeatMs { get; init; } = 1000;

    /// <summary>Render delay of each mirrored object; larger than the ~200 ms scan interval.</summary>
    public int BufferMs { get; init; } = 350;
    public int MaxExtrapolationMs { get; init; } = 1500;
    /// <summary>With no frame at all for this long the source is gone: remove everything.</summary>
    public int StaleClearMs { get; init; } = 15_000;
    public int MaxSpawnsInFlight { get; init; } = 4;
    public int SpawnTimeoutMs { get; init; } = 10_000;
    /// <summary>An aircraft whose every model failed is tried again from scratch after this long.</summary>
    public int FailedRetryMs { get; init; } = 30_000;
    /// <summary>Pose writes per object: every step within NearM, every MidIntervalMs within MidM, else FarIntervalMs.</summary>
    public double NearM { get; init; } = 5_000;
    public double MidM { get; init; } = 20_000;
    public int MidIntervalMs { get; init; } = 100;
    public int FarIntervalMs { get; init; } = 250;
}

/// <summary>
/// Captain side: turns complete scans of the sim's AI aircraft into a stream for the mirror:
/// reliable announcements (identity), reliable removals, lossy pose frames.
/// </summary>
public sealed class TrafficSource
{
    private sealed class Obj
    {
        public TrafficKind Kind;
        public TrafficIdentity? Identity;
        public bool Announced;
        public TrafficSample Sample;
        public long SeenAt;
        public int Missing;
        public bool Fresh;
    }

    private readonly TrafficOptions _o;
    private readonly Dictionary<uint, Obj> _objs = [];
    private readonly List<uint> _removals = [];
    private FlightSample? _user;
    private bool _wasEstablished;
    private bool _frameDue;
    private long _lastFrame = long.MinValue / 2;
    private ulong _seq;

    public TrafficSource(TrafficOptions? options = null) => _o = options ?? new TrafficOptions();

    /// <summary>AI aircraft currently in the Captain's sim (within the scan radius).</summary>
    public int Count => _objs.Count;
    /// <summary>AI aircraft the other pilot has been told about.</summary>
    public int Announced => _objs.Values.Count(o => o.Announced);
    public long FramesSent { get; private set; }

    public bool Has(uint id) => _objs.ContainsKey(id);


    /// <summary>For diagnostics: the source's view of one aircraft.</summary>
    public string Describe(uint id) => _objs.TryGetValue(id, out var o)
        ? $"{o.Kind} announced {o.Announced} fresh {o.Fresh} missing {o.Missing} seen {o.SeenAt} '{o.Identity?.Title}'"
        : "unknown";

    public void OnUserPosition(FlightSample user) => _user = user;

    /// <summary>A complete scan of one kind of AI object (every object of that kind within the radius).</summary>
    public void OnScan(TrafficKind kind, IReadOnlyList<(uint Id, TrafficSample Sample)> objects, long now)
    {
        var seen = new HashSet<uint>();
        foreach (var (id, s) in objects)
        {
            if (!seen.Add(id)) continue;
            if (!_objs.TryGetValue(id, out var o)) _objs[id] = o = new Obj();
            o.Kind = kind; // an id MSFS reused for another kind of aircraft belongs to the scan that sees it
            o.Sample = s;
            o.SeenAt = now;
            o.Missing = 0;
            o.Fresh = true;
        }
        foreach (var (id, o) in _objs.ToList())
        {
            if (o.Kind != kind || seen.Contains(id)) continue;
            if (++o.Missing >= _o.MissingScansForRemoval) Remove(id);
        }
        _frameDue = true;
    }

    /// <summary>What the object is (TITLE, livery, ATC data). A different identity for a known id means MSFS reused the id.</summary>
    public void OnIdentity(uint id, TrafficIdentity identity)
    {
        if (!_objs.TryGetValue(id, out var o) || o.Identity == identity) return;
        if (o.Announced)
        {
            _removals.Add(id); // a different aircraft now: the mirror must replace it
            o.Announced = false;
        }
        o.Identity = identity;
    }

    /// <summary>The sim went away (closed, reconnecting): every object is gone.</summary>
    public void OnSimLost()
    {
        foreach (var id in _objs.Keys.ToList()) Remove(id);
    }

    private void Remove(uint id)
    {
        if (_objs.Remove(id, out var o) && o.Announced) _removals.Add(id);
    }

    public void Tick(long now, bool established, Action<Outgoing> send)
    {
        if (!established)
        {
            _wasEstablished = false;
            return;
        }
        var selected = Select();
        if (!_wasEstablished)
        {
            // (Re)connected: the mirror may hold anything. Tell it what still exists, then
            // announce everything again (it ignores announcements it already has).
            _wasEstablished = true;
            _removals.Clear();
            foreach (var o in _objs.Values) o.Announced = false;
            send(new Outgoing(Channel.ReliableOrdered, new TrafficReset([.. selected.Where(id => _objs[id].Identity is not null).Order()])));
        }

        // Objects that fell out of the nearest-N selection are removed on the other side too.
        foreach (var (id, o) in _objs)
            if (o.Announced && !selected.Contains(id))
            {
                o.Announced = false;
                _removals.Add(id);
            }

        // Removals first: an id reused by MSFS must disappear before its new aircraft is announced.
        for (int i = 0; i < _removals.Count; i += 256)
            send(new Outgoing(Channel.ReliableOrdered, new TrafficRemoved([.. _removals.Skip(i).Take(256)])));
        _removals.Clear();

        var announce = new List<TrafficInfo>();
        foreach (uint id in selected)
        {
            var o = _objs[id];
            if (o.Announced || o.Identity is null) continue;
            o.Announced = true;
            announce.Add(new TrafficInfo(id, o.Identity));
            if (announce.Count == _o.AnnounceBatch)
            {
                send(new Outgoing(Channel.ReliableOrdered, new TrafficAdded([.. announce])));
                announce.Clear();
            }
        }
        if (announce.Count > 0) send(new Outgoing(Channel.ReliableOrdered, new TrafficAdded([.. announce])));

        if (!_frameDue && now - _lastFrame < _o.HeartbeatMs) return;
        _frameDue = false;
        _lastFrame = now;
        var batch = new List<TrafficEntry>(_o.FrameBatch);
        bool sentAny = false;
        foreach (uint id in selected)
        {
            var o = _objs[id];
            if (!o.Announced || !o.Fresh) continue;
            o.Fresh = false;
            batch.Add(new TrafficEntry(id, (ushort)Math.Clamp(now - o.SeenAt, 0, ushort.MaxValue), o.Sample));
            if (batch.Count == _o.FrameBatch)
            {
                SendFrame(now, batch, send);
                sentAny = true;
            }
        }
        if (batch.Count > 0 || !sentAny) SendFrame(now, batch, send);
    }

    private void SendFrame(long now, List<TrafficEntry> batch, Action<Outgoing> send)
    {
        send(new Outgoing(Channel.Sequenced, new TrafficFrame(++_seq, now, [.. batch])));
        FramesSent++;
        batch.Clear();
    }

    /// <summary>The nearest <see cref="TrafficOptions.MaxObjects"/> objects (by id without a user position).</summary>
    private HashSet<uint> Select()
    {
        if (_objs.Count <= _o.MaxObjects) return [.. _objs.Keys];
        IEnumerable<KeyValuePair<uint, Obj>> ordered = _user is { } u
            ? _objs.OrderBy(kv => FlightFollower.DistanceM(u, kv.Value.Sample.Pose)).ThenBy(kv => kv.Key)
            : _objs.OrderBy(kv => kv.Key);
        return [.. ordered.Take(_o.MaxObjects).Select(kv => kv.Key)];
    }
}

/// <summary>What the mirror wants the sim to do with injected objects.</summary>
public abstract record TrafficAction;

/// <summary>Create an aircraft. The sim answers with <see cref="TrafficMirror.OnSpawned"/> or <see cref="TrafficMirror.OnSpawnFailed"/> for <see cref="Token"/>.</summary>
public sealed record SpawnTraffic(ulong Token, uint RemoteId, string Title, string Livery, string TailNumber, FlightSample Pose, bool StandIn) : TrafficAction;

public sealed record MoveTraffic(uint LocalId, FlightSample Pose) : TrafficAction;

public sealed record SetTrafficAppearance(uint LocalId, ushort Lights, bool GearDown) : TrafficAction;

public sealed record RemoveTraffic(uint LocalId) : TrafficAction;

/// <summary>Picks which installed model stands in for a remote aircraft, best match first.</summary>
public static class TrafficModels
{
    public static List<(string Title, string Livery, bool StandIn)> Candidates(TrafficIdentity id, IReadOnlyCollection<(string Title, string Livery)>? installed)
    {
        var result = new List<(string, string, bool)>();
        void Add(string title, string livery, bool standIn)
        {
            if (title.Length > 0 && !result.Any(r => r.Item1 == title && r.Item2 == livery)) result.Add((title, livery, standIn));
        }
        if (installed is null || installed.Count == 0)
        {
            // Nothing known about the local install: try the exact model, then its default livery.
            Add(id.Title, id.Livery, false);
            Add(id.Title, "", false);
            return result;
        }
        var exact = installed.Where(m => string.Equals(m.Title, id.Title, StringComparison.OrdinalIgnoreCase)).ToList();
        foreach (var m in exact.Where(m => string.Equals(m.Livery, id.Livery, StringComparison.OrdinalIgnoreCase))) Add(m.Title, m.Livery, false);
        if (exact.Count > 0)
        {
            Add(exact[0].Title, exact.FirstOrDefault(m => m.Livery.Length > 0).Livery ?? "", false);
            Add(exact[0].Title, "", false);
        }

        // Not installed here: the closest installed model by shared words in the title and ATC model.
        var words = Words(id.Title + " " + id.AtcModel);
        var best = installed
            .Select(m => (m, Score: Words(m.Title).Count(w => words.Any(x => x.StartsWith(w) || w.StartsWith(x)))))
            .Where(x => x.Score > 0)
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.m.Title, StringComparer.Ordinal)
            .Take(3);
        foreach (var (m, _) in best) Add(m.Title, m.Livery, true);

        // Last resort: a few installed aircraft of the right kind (helicopters look for a heli).
        bool heli = id.Kind == TrafficKind.Helicopter;
        foreach (var m in installed.Where(m => LooksLikeHeli(m.Title) == heli).DistinctBy(m => m.Title).Take(3))
            Add(m.Title, m.Livery, true);
        return result;
    }

    private static bool LooksLikeHeli(string title) =>
        title.Contains("heli", StringComparison.OrdinalIgnoreCase) || title.Contains("H125", StringComparison.OrdinalIgnoreCase)
        || title.Contains("H135", StringComparison.OrdinalIgnoreCase) || title.Contains("Bell", StringComparison.OrdinalIgnoreCase)
        || title.Contains("Cabri", StringComparison.OrdinalIgnoreCase) || title.Contains("Robinson", StringComparison.OrdinalIgnoreCase);

    private static HashSet<string> Words(string s) =>
        [.. s.Split([' ', '-', '_', '(', ')', '/', '.', ','], StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.ToUpperInvariant())
            .Where(w => w.Length >= 3 && w.Any(char.IsDigit) || w.Length >= 4)
            .Where(w => w is not ("AIRCRAFT" or "LIVERY" or "DEFAULT" or "GENERIC" or "ASOBO" or "MICROSOFT"))];
}

/// <summary>
/// First Officer side: the registry of mirrored AI aircraft. Remote id → identity, a per-object
/// <see cref="FlightFollower"/>, and the local object id once the sim has created it. Each
/// <see cref="Step"/> yields spawns, pose writes and removals. Removal is guaranteed: an object
/// whose creation completes after it was removed (or after <see cref="Clear"/>) is removed as
/// soon as its local id is known.
/// </summary>
public sealed class TrafficMirror
{
    private enum State { Waiting, Spawning, Spawned, Failed }

    private sealed class Entry
    {
        public required uint RemoteId;
        public required TrafficIdentity Identity;
        public required FlightFollower Follower;
        public State State;
        public ulong Token;
        public long SpawnStartedAt;
        public int Attempt;
        public int Timeouts;
        public long FailedAt;
        public List<(string Title, string Livery, bool StandIn)>? Candidates;
        public uint LocalId;
        public bool StandIn;
        public long LastWrite = long.MinValue / 2;
        public ushort Lights;
        public bool GearDown;
        public bool AppearanceDirty = true;
    }

    private readonly TrafficOptions _o;
    private readonly Dictionary<uint, Entry> _byRemote = [];
    private readonly Dictionary<ulong, Entry?> _tokens = [];   // null: the entry is gone, remove on arrival
    private readonly List<uint> _orphans = [];                  // local objects to remove at the next step
    private readonly HashSet<uint> _local = [];
    private IReadOnlyCollection<(string Title, string Livery)>? _installed;
    private ulong _nextToken;
    private long _lastFrameAt = long.MinValue;

    public TrafficMirror(TrafficOptions? options = null) => _o = options ?? new TrafficOptions();

    public int Known => _byRemote.Count;
    public int Spawned => _byRemote.Values.Count(e => e.State == State.Spawned);
    public int StandIns => _byRemote.Values.Count(e => e.State == State.Spawned && e.StandIn);
    public int Failed => _byRemote.Values.Count(e => e.State == State.Failed);
    public int SpawnsInFlight => _tokens.Count;
    /// <summary>Every local object id SkyLink created and has not removed yet (including orphans awaiting removal).</summary>
    public IReadOnlyCollection<uint> LocalObjects => _local;
    public bool IsOurs(uint localId) => _local.Contains(localId);
    public bool HasPendingWork => _tokens.Count > 0 || _orphans.Count > 0;
    /// <summary>False holds back new spawns (e.g. until the installed-aircraft list is known).</summary>
    public bool Ready { get; set; } = true;

    /// <summary>Installed aircraft and liveries (from the sim's catalog), for choosing stand-ins.</summary>
    public void SetInstalledModels(IReadOnlyCollection<(string Title, string Livery)>? installed) => _installed = installed;

    public void OnMessage(Message m, long now)
    {
        switch (m)
        {
            case TrafficAdded a:
                foreach (var info in a.Objects) Add(info, now);
                break;
            case TrafficRemoved r:
                foreach (uint id in r.Ids) Remove(id);
                break;
            case TrafficReset r:
            {
                var live = r.LiveIds.ToHashSet();
                foreach (uint id in _byRemote.Keys.Where(id => !live.Contains(id)).ToList()) Remove(id);
                _lastFrameAt = now;
                break;
            }
            case TrafficFrame f:
                _lastFrameAt = now;
                foreach (var e in f.Objects)
                {
                    if (!_byRemote.TryGetValue(e.Id, out var x)) continue; // not announced (yet): its identity is on the way
                    x.Follower.Add(f.Time - e.AgeMs, e.Sample.Pose, now);
                    if (x.Lights != e.Sample.Lights || x.GearDown != e.Sample.GearDown)
                    {
                        x.Lights = e.Sample.Lights;
                        x.GearDown = e.Sample.GearDown;
                        x.AppearanceDirty = true;
                    }
                }
                break;
        }
    }

    private void Add(TrafficInfo info, long now)
    {
        if (_byRemote.TryGetValue(info.Id, out var existing))
        {
            if (existing.Identity == info.Identity) return; // re-announced after a reconnect
            Remove(info.Id);
        }
        _byRemote[info.Id] = new Entry
        {
            RemoteId = info.Id,
            Identity = info.Identity,
            Follower = new FlightFollower(new FollowerOptions { BufferMs = _o.BufferMs, MaxExtrapolationMs = _o.MaxExtrapolationMs }),
        };
        if (_lastFrameAt == long.MinValue) _lastFrameAt = now;
    }

    private void Remove(uint remoteId)
    {
        if (!_byRemote.Remove(remoteId, out var e)) return;
        Drop(e);
    }

    private void Drop(Entry e)
    {
        switch (e.State)
        {
            case State.Spawned:
                _orphans.Add(e.LocalId);
                break;
            case State.Spawning:
                _tokens[e.Token] = null; // remove it when (if) it arrives
                break;
        }
        e.State = State.Failed;
    }

    /// <summary>The sim created the object for <paramref name="token"/>.</summary>
    public void OnSpawned(ulong token, uint localId)
    {
        _local.Add(localId);
        if (!_tokens.Remove(token, out var e) || e is null || e.State != State.Spawning || e.Token != token)
        {
            _orphans.Add(localId); // removed (or timed out) meanwhile: never leave it behind
            return;
        }
        e.State = State.Spawned;
        e.LocalId = localId;
        e.LastWrite = long.MinValue / 2;
        e.AppearanceDirty = true;
    }

    /// <summary>The sim could not create the object for <paramref name="token"/> (model not installed, ...).</summary>
    public void OnSpawnFailed(ulong token)
    {
        if (!_tokens.Remove(token, out var e) || e is null || e.State != State.Spawning || e.Token != token) return;
        e.State = State.Waiting;
        e.Attempt++;
    }

    /// <summary>The sim removed one of our objects by itself (or confirmed a removal).</summary>
    public void OnLocalObjectGone(uint localId)
    {
        _local.Remove(localId);
        _orphans.Remove(localId); // already gone: nothing left to remove
        foreach (var e in _byRemote.Values)
            if (e.State == State.Spawned && e.LocalId == localId)
            {
                e.State = State.Waiting; // respawn it
                e.Attempt = 0;
            }
    }

    /// <summary>Remove every mirrored object (session over). Spawns still in flight are removed when they land.</summary>
    public void Clear()
    {
        foreach (var e in _byRemote.Values) Drop(e);
        _byRemote.Clear();
        _lastFrameAt = long.MinValue;
    }

    public void Step(long now, FlightSample? user, List<TrafficAction> actions)
    {
        if (_byRemote.Count > 0 && _lastFrameAt != long.MinValue && now - _lastFrameAt > _o.StaleClearMs) Clear();

        foreach (uint id in _orphans)
        {
            actions.Add(new RemoveTraffic(id));
            _local.Remove(id);
        }
        _orphans.Clear();

        foreach (var (token, e) in _tokens.ToList())
        {
            if (e is null || e.State != State.Spawning || now - e.SpawnStartedAt < _o.SpawnTimeoutMs) continue;
            _tokens[token] = null; // no answer: try again, but remove this one should it ever arrive
            e.State = State.Waiting;
            if (++e.Timeouts >= 3) e.Attempt++; // the sim never answers for this model: next candidate
        }

        int inFlight = _tokens.Values.Count(e => e is not null);
        foreach (var e in _byRemote.Values)
        {
            if (e.State == State.Failed && now - e.FailedAt >= _o.FailedRetryMs)
            {
                e.State = State.Waiting; // models may have been installed, or the sim was just busy
                e.Attempt = e.Timeouts = 0;
            }
            switch (e.State)
            {
                case State.Waiting when Ready && inFlight < _o.MaxSpawnsInFlight && e.Follower.HasData:
                {
                    e.Candidates ??= TrafficModels.Candidates(e.Identity, _installed);
                    if (e.Attempt >= e.Candidates.Count)
                    {
                        e.State = State.Failed;
                        e.FailedAt = now;
                        break;
                    }
                    if (e.Follower.Sample(now) is not { } pose) break;
                    var (title, livery, standIn) = e.Candidates[e.Attempt];
                    e.State = State.Spawning;
                    e.Token = ++_nextToken;
                    e.SpawnStartedAt = now;
                    e.StandIn = standIn;
                    _tokens[e.Token] = e;
                    inFlight++;
                    actions.Add(new SpawnTraffic(e.Token, e.RemoteId, title, livery, TailNumber(e.Identity), pose, standIn));
                    break;
                }
                case State.Spawned:
                {
                    double dist = user is { } u && e.Follower.HasData ? DistanceToLast(e, u, now) : 0;
                    int interval = dist < _o.NearM ? 0 : dist < _o.MidM ? _o.MidIntervalMs : _o.FarIntervalMs;
                    if (now - e.LastWrite >= interval && e.Follower.Sample(now) is { } pose)
                    {
                        actions.Add(new MoveTraffic(e.LocalId, pose));
                        e.LastWrite = now;
                    }
                    if (e.AppearanceDirty)
                    {
                        e.AppearanceDirty = false;
                        actions.Add(new SetTrafficAppearance(e.LocalId, e.Lights, e.GearDown));
                    }
                    break;
                }
            }
        }
    }

    private static double DistanceToLast(Entry e, FlightSample user, long now) =>
        e.Follower.Sample(now) is { } p ? FlightFollower.DistanceM(user, p) : 0;

    /// <summary>MSFS limits tail numbers; keep the ATC id (callsign) the Captain sees.</summary>
    private static string TailNumber(TrafficIdentity id)
    {
        string t = id.AtcId.Trim();
        if (t.Length == 0) t = "SKYLINK";
        return t.Length > 12 ? t[..12] : t;
    }

    /// <summary>For tests and diagnostics: where a mirrored object is being rendered, by remote id.</summary>
    public bool TryGetLocal(uint remoteId, out uint localId)
    {
        if (_byRemote.TryGetValue(remoteId, out var e) && e.State == State.Spawned)
        {
            localId = e.LocalId;
            return true;
        }
        localId = 0;
        return false;
    }

    public IEnumerable<uint> RemoteIds => _byRemote.Keys;

    /// <summary>For diagnostics: the registry's view of one remote aircraft.</summary>
    public string Describe(uint remoteId) => _byRemote.TryGetValue(remoteId, out var e)
        ? $"{e.State} attempt {e.Attempt}/{e.Candidates?.Count} timeouts {e.Timeouts} local {e.LocalId} samples {e.Follower.Count} '{e.Identity.Title}'"
        : "unknown";
}

public sealed class WorldOptions
{
    public bool SyncTime { get; init; } = true;
    public bool SyncRate { get; init; } = true;
    public bool SyncPause { get; init; } = true;
    /// <summary>Clock differences below this are left alone (time can only be set to the minute).</summary>
    public double TimeToleranceSec { get; init; } = 120;
    public int TimeRetryMs { get; init; } = 20_000;
    public int MaxTimeAttempts { get; init; } = 3;
    public int RateStepGuardMs { get; init; } = 700;
    public int MaxRateSteps { get; init; } = 16;
    public int PauseRetryMs { get; init; } = 1500;
    public int MaxPauseAttempts { get; init; } = 3;
    /// <summary>Weather must differ (or agree again) for this long before the warning changes.</summary>
    public int WeatherSettleMs { get; init; } = 20_000;
    public double PressureToleranceMb { get; init; } = 3;
    public double WindToleranceKts { get; init; } = 8;
    public double WindDirToleranceDeg { get; init; } = 35;
    public double TempToleranceC { get; init; } = 5;
    /// <summary>Samples older than this are not compared.</summary>
    public int FreshMs { get; init; } = 5000;
}

public abstract record WorldAction;
public sealed record SetPause(bool Paused) : WorldAction;
/// <summary>One step of the sim rate (MSFS rates are powers of two): +1 faster, -1 slower.</summary>
public sealed record StepSimRate(int Direction) : WorldAction;
/// <summary>Set the zulu date and time. <see cref="DayOfYear"/> is 1-based, as MSFS reports it.</summary>
public sealed record SetZuluTime(int Year, int DayOfYear, int Hour, int Minute) : WorldAction;

/// <summary>
/// First Officer side: brings the local sim's time, rate and pause to the Captain's and notices
/// when the weather at the (shared) aircraft position differs. The Captain's changes are applied
/// with bounded retries, so a sim that refuses a change is never hammered.
/// </summary>
public sealed class WorldFollower
{
    private readonly WorldOptions _o;
    private (WorldSample S, long At)? _remote;
    private (WorldSample S, long At)? _local;

    private bool? _remotePauseApplied;
    private int _pauseAttempts;
    private long _lastPauseAttempt = long.MinValue / 2;

    private double _rateTarget = double.NaN;
    private int _rateSteps;
    private long _lastRateStep = long.MinValue / 2;

    private int _timeAttempts;
    private long _lastTimeAttempt = long.MinValue / 2;
    private double _timeTargetBase = double.NaN; // remote zulu minus remote monotonic, to see when the Captain jumps

    private bool _weatherDiffers;
    private long _weatherSince = long.MinValue;

    public WorldFollower(WorldOptions? options = null) => _o = options ?? new WorldOptions();

    public bool WeatherMismatch { get; private set; }
    public string? WeatherDetail { get; private set; }
    public WorldSample? Remote => _remote?.S;
    public WorldSample? Local => _local?.S;

    public void OnRemote(WorldSample s, long now) => _remote = (s, now);
    public void OnLocal(WorldSample s, long now) => _local = (s, now);

    public void Reset()
    {
        _remote = null;
        _remotePauseApplied = null;
        _pauseAttempts = 0;
        _rateTarget = double.NaN;
        _rateSteps = 0;
        _timeAttempts = 0;
        _timeTargetBase = double.NaN;
        WeatherMismatch = false;
        WeatherDetail = null;
        _weatherSince = long.MinValue;
    }

    public void Step(long now, List<WorldAction> actions)
    {
        if (_remote is not { } r || _local is not { } l) return;
        var rs = r.S;
        var ls = l.S;

        // Pause: follow each change the Captain makes, a few tries, then leave it to the pilot.
        if (_o.SyncPause)
        {
            if (_remotePauseApplied != rs.Paused)
            {
                _remotePauseApplied = rs.Paused;
                _pauseAttempts = 0;
            }
            if (ls.Paused != rs.Paused && _pauseAttempts < _o.MaxPauseAttempts && now - _lastPauseAttempt >= _o.PauseRetryMs)
            {
                _pauseAttempts++;
                _lastPauseAttempt = now;
                actions.Add(new SetPause(rs.Paused));
            }
        }

        // Rate: step toward the Captain's, one step at a time, waiting to see each one land.
        if (_o.SyncRate && rs.SimRate > 0 && ls.SimRate > 0 && !ls.Paused)
        {
            if (Math.Abs(rs.SimRate - _rateTarget) > 1e-6)
            {
                _rateTarget = rs.SimRate;
                _rateSteps = 0;
            }
            double ratio = Math.Log2(rs.SimRate / ls.SimRate);
            if (Math.Abs(ratio) > 0.25 && _rateSteps < _o.MaxRateSteps && now - _lastRateStep >= _o.RateStepGuardMs && l.At > _lastRateStep)
            {
                _rateSteps++;
                _lastRateStep = now;
                actions.Add(new StepSimRate(ratio > 0 ? 1 : -1));
            }
        }

        // Time: only a real difference (different time of day / date), and only a few tries per Captain jump.
        if (_o.SyncTime && now - r.At <= _o.FreshMs && now - l.At <= _o.FreshMs)
        {
            double remoteNow = rs.ZuluUnixSec + (rs.Paused ? 0 : (now - r.At) / 1000.0 * rs.SimRate);
            double localNow = ls.ZuluUnixSec + (ls.Paused ? 0 : (now - l.At) / 1000.0 * ls.SimRate);
            double remoteBase = remoteNow - now / 1000.0;
            if (double.IsNaN(_timeTargetBase) || Math.Abs(remoteBase - _timeTargetBase) > _o.TimeToleranceSec)
            {
                _timeTargetBase = remoteBase;
                _timeAttempts = 0;
            }
            if (Math.Abs(remoteNow - localNow) > _o.TimeToleranceSec && _timeAttempts < _o.MaxTimeAttempts && now - _lastTimeAttempt >= _o.TimeRetryMs)
            {
                _timeAttempts++;
                _lastTimeAttempt = now;
                var t = DateTime.UnixEpoch.AddSeconds(remoteNow + 30); // round to the nearest minute
                actions.Add(new SetZuluTime(t.Year, t.DayOfYear, t.Hour, t.Minute));
            }
        }

        // Weather: both should fly live weather; at the same position it must then agree.
        if (now - r.At <= _o.FreshMs && now - l.At <= _o.FreshMs)
        {
            string? why = WeatherDifference(rs, ls);
            bool differs = why is not null;
            if (differs != _weatherDiffers || _weatherSince == long.MinValue)
            {
                _weatherDiffers = differs;
                _weatherSince = now;
            }
            if (differs) WeatherDetail = why;
            if (WeatherMismatch != differs && now - _weatherSince >= _o.WeatherSettleMs) WeatherMismatch = differs;
        }
    }

    private string? WeatherDifference(WorldSample a, WorldSample b)
    {
        if (Math.Abs(a.SeaLevelPressureMb - b.SeaLevelPressureMb) > _o.PressureToleranceMb)
            return $"pressure {a.SeaLevelPressureMb:F0} vs {b.SeaLevelPressureMb:F0} hPa";
        if (Math.Abs(a.WindKts - b.WindKts) > _o.WindToleranceKts)
            return $"wind {a.WindKts:F0} vs {b.WindKts:F0} kt";
        if (Math.Max(a.WindKts, b.WindKts) > _o.WindToleranceKts && Math.Abs(FlightFollower.AngleDiff(a.WindDirDeg, b.WindDirDeg)) > _o.WindDirToleranceDeg)
            return $"wind from {a.WindDirDeg:F0}° vs {b.WindDirDeg:F0}°";
        if (Math.Abs(a.TempC - b.TempC) > _o.TempToleranceC)
            return $"temperature {a.TempC:F0} vs {b.TempC:F0} °C";
        return null;
    }
}
