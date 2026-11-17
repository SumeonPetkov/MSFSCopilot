using System.Diagnostics;
using System.Security.Cryptography;
using SkyLink.Core;
using SkyLink.Net;
using SkyLink.Sim;

namespace SkyLink.Session;

/// <summary>Thread-safe copy of what the UI needs to show.</summary>
public sealed record SessionStatus(
    string Sim,
    string? Aircraft,
    bool GenericFallback,
    LinkState Link,
    LinkStats LinkStats,
    bool Established,
    bool InitialSync,
    bool PilotFlying,
    string? Incompatible,
    int PendingWrites,
    int UnackedEvents,
    long Rejections,
    int DerivedMismatchGroups,
    long Repairs,
    long LocalChanges,
    long RemoteApplied,
    long EventsSent,
    long EventsApplied);

/// <summary>
/// The session loop. Everything (sim pump, network poll, engine) runs on one thread, so the
/// engine needs no locks. When the loaded aircraft changes the engine is rebuilt with the new
/// definition and re-handshakes over the existing connection.
/// </summary>
public sealed class SessionRunner : IDisposable
{
    private readonly ISimBridge _sim;
    private readonly Link _link;
    private readonly EngineOptions? _options;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _epochMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly object _statusLock = new();
    private readonly Queue<Action<SyncEngine, long>> _requests = new();
    private SyncEngine? _engine;
    private int _generation = -1;
    private bool _linkUp;
    private SessionStatus _status;
    private long _lastStatus;

    public event Action<string>? Log;

    private readonly bool _ownsSim;
    private readonly Action<string> _simLog;

    /// <param name="ownsSim">False when the sim bridge outlives this session (the desktop app keeps it between flights).</param>
    public SessionRunner(ISimBridge sim, Link link, EngineOptions? options = null, bool ownsSim = true)
    {
        _sim = sim;
        _link = link;
        _options = options;
        _ownsSim = ownsSim;
        _status = BuildStatus();
        _simLog = m => Log?.Invoke("[sim] " + m);
        _sim.Log += _simLog;
        _link.Log += m => Log?.Invoke("[net] " + m);
        _link.Connected += remote =>
        {
            _linkUp = true;
            Log?.Invoke($"[net] connected to {remote}");
            _engine?.OnConnected(Now);
        };
        _link.Disconnected += reason =>
        {
            _linkUp = false;
            Log?.Invoke($"[net] disconnected: {reason}");
            _engine?.OnDisconnected(Now);
        };
        _link.Received += m => _engine?.OnMessage(m, Now);
    }

    /// <summary>Monotonic milliseconds, aligned to Unix time at start (HLC wall clock).</summary>
    public long Now => _epochMs + _clock.ElapsedMilliseconds;

    public SyncEngine? Engine => _engine;

    public SessionStatus Status
    {
        get { lock (_statusLock) return _status; }
    }

    /// <summary>Queue an action on the session thread (e.g. a UI button).</summary>
    public void Post(Action<SyncEngine, long> action)
    {
        lock (_requests) _requests.Enqueue(action);
    }

    public void RequestControl() => Post((e, now) => e.RequestControl(now));
    public void TakeControl() => Post((e, now) => e.TakeControl(now));
    public void GrantControl() => Post((e, now) => e.GrantControl(now));

    public void Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            if (_sim.DataAvailable is { } h) h.WaitOne(5);
            else Thread.Sleep(5);
            Step();
        }
    }

    /// <summary>One iteration of the loop (public for tests that drive it directly).</summary>
    public void Step()
    {
        long now = Now;
        _link.Poll(now);
        _sim.Pump(now);

        if (_sim.DefinitionGeneration != _generation) RebuildEngine(now);

        var e = _engine;
        if (e is not null)
        {
            lock (_requests)
                while (_requests.TryDequeue(out var a)) a(e, now);
            _sim.Feed(e, now);
            e.Tick(now);
            while (e.SimCommands.TryDequeue(out var c)) _sim.Execute(c, now);
            while (e.Outbox.TryDequeue(out var o)) _link.Send(o);
        }

        if (now - _lastStatus >= 250)
        {
            _lastStatus = now;
            var s = BuildStatus();
            lock (_statusLock) _status = s;
        }
    }

    private void RebuildEngine(long now)
    {
        _generation = _sim.DefinitionGeneration;
        _engine?.OnDisconnected(now);
        _engine = null;
        var def = _sim.Definition;
        if (def is null)
        {
            Log?.Invoke("[sync] no aircraft definition active; sync paused");
            return;
        }
        ulong instance = RandomNonZero();
        _engine = _link.IsHost
            ? new SyncEngine(def.Schema, 0, true, instance, RandomNonZero(), _options)
            : new SyncEngine(def.Schema, 1, false, instance, 0, _options);
        _engine.Log = m => Log?.Invoke("[sync] " + m);
        _engine.Start();
        Log?.Invoke($"[sync] engine ready for {def.Id} v{def.Version} ({def.Vars.Count} variables, definition hash {def.Schema.Hash:X16})");
        if (_linkUp) _engine.OnConnected(now);
    }

    private static ulong RandomNonZero()
    {
        Span<byte> b = stackalloc byte[8];
        ulong v;
        do
        {
            RandomNumberGenerator.Fill(b);
            v = BitConverter.ToUInt64(b);
        } while (v == 0);
        return v;
    }

    private SessionStatus BuildStatus()
    {
        var e = _engine;
        var h = e?.Health;
        return new SessionStatus(
            _sim.Status,
            _sim.Definition?.Name,
            _sim.Match?.IsFallback ?? false,
            _link.State,
            _link.Stats,
            e?.IsEstablished ?? false,
            e?.IsInitialSync ?? false,
            e?.IsPilotFlying ?? _link.IsHost,
            h is { Incompatible: true } ? h.IncompatibleReason : null,
            e?.PendingWriteCount ?? 0,
            e?.UnackedEventCount ?? 0,
            h?.WriteRejections ?? 0,
            h?.DerivedMismatchGroups ?? 0,
            h?.SnapshotsSent ?? 0,
            h?.LocalChanges ?? 0,
            h?.RemoteApplied ?? 0,
            h?.EventsSent ?? 0,
            h?.EventsApplied ?? 0);
    }

    public void Dispose()
    {
        _engine?.OnDisconnected(Now);
        _link.Dispose();
        _sim.Log -= _simLog;
        if (_ownsSim) _sim.Dispose();
    }
}
