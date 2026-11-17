using System.Collections.Concurrent;
using System.Diagnostics;
using SkyLink.Core;
using SkyLink.Net;
using SkyLink.Sim;

namespace SkyLink.Session;

public sealed record SimSnapshot(
    bool Connected,
    bool InFlight,
    string? Aircraft,
    string Status,
    string? Definition,
    bool GenericFallback,
    bool PilotFlying,
    bool Following,
    string Module,
    FlightSample? Position);

/// <summary>
/// Owns the simulator connection for the whole app lifetime, on its own thread. In the lobby it
/// just keeps the bridge pumped; during a flight it runs a <see cref="SessionRunner"/> over the
/// same bridge. Everything else talks to the sim through <see cref="Post"/>.
/// </summary>
public sealed class SimHost : IDisposable
{
    private readonly ConcurrentQueue<Action> _work = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly long _epoch = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
    private readonly object _lock = new();
    private Thread? _thread;
    private SessionRunner? _runner;
    private SimSnapshot _snapshot;
    private SessionStatus? _session;
    private long _lastSnapshot;

    public IFlightSim Sim { get; }
    public event Action<string>? Log;

    public SimHost(IFlightSim sim)
    {
        Sim = sim;
        Sim.Log += m => Log?.Invoke("[sim] " + m);
        _snapshot = Snapshot();
    }

    public long Now => _epoch + _clock.ElapsedMilliseconds;

    public SimSnapshot Current
    {
        get { lock (_lock) return _snapshot; }
    }

    public SessionStatus? Session
    {
        get { lock (_lock) return _session; }
    }

    public bool SessionRunning
    {
        get { lock (_lock) return _runner is not null; }
    }

    public void Start()
    {
        _thread = new Thread(Loop) { IsBackground = true, Name = "SkyLink sim" };
        _thread.Start();
    }

    /// <summary>Run <paramref name="action"/> on the sim thread.</summary>
    public void Post(Action action) => _work.Enqueue(action);

    public void StartSession(Link link, EngineOptions? options = null) => Post(() =>
    {
        _runner?.Dispose();
        var runner = new SessionRunner(Sim, link, options, ownsSim: false);
        runner.Log += m => Log?.Invoke(m);
        lock (_lock) _runner = runner;
    });

    public void StopSession() => Post(() =>
    {
        SessionRunner? r;
        lock (_lock)
        {
            r = _runner;
            _runner = null;
            _session = null;
        }
        r?.Dispose();
    });

    public void RequestControl() => Post(() => _runner?.RequestControl());
    public void TakeControl() => Post(() => _runner?.TakeControl());
    public void GiveControl() => Post(() => _runner?.GrantControl());

    private void Loop()
    {
        while (!_cts.IsCancellationRequested)
        {
            if (Sim.DataAvailable is { } h) h.WaitOne(5);
            else Thread.Sleep(5);
            StepOnce();
        }
    }

    /// <summary>One iteration of the loop (tests drive it directly instead of <see cref="Start"/>).</summary>
    public void StepOnce()
    {
        while (_work.TryDequeue(out var a))
        {
            try { a(); }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.Net.Sockets.SocketException or ArgumentException)
            {
                Log?.Invoke("error: " + e.Message);
            }
        }
        var runner = _runner;
        if (runner is not null) runner.Step();
        else Sim.Pump(Now);

        long now = Now;
        if (now - _lastSnapshot < 100) return;
        _lastSnapshot = now;
        var snap = Snapshot();
        var session = runner?.Status;
        lock (_lock)
        {
            _snapshot = snap;
            _session = session;
        }
    }

    private SimSnapshot Snapshot()
    {
        bool pf = Sim is SimBridgeBase b ? b.IsPilotFlying : true;
        return new SimSnapshot(Sim.IsConnected, Sim.IsInFlight, Sim.AircraftTitle, Sim.Status, Sim.Definition?.Name,
            Sim.Match?.IsFallback ?? false, pf, Sim.IsFollowing, Sim.ModuleStatus, Sim.LatestFlight);
    }

    public void Dispose()
    {
        _cts.Cancel();
        _thread?.Join(2000);
        _runner?.Dispose();
        Sim.Dispose();
    }
}
