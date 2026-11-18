namespace SkyLink.Core;

/// <summary>
/// The session's shared world, next to the cockpit <see cref="SyncEngine"/>. The host (Captain)
/// is the source: its scans of AI traffic and its time/rate/pause/weather go out. The joiner
/// (First Officer) mirrors: messages become <see cref="SimCommand"/>s for its bridge, and its
/// warnings (own AI traffic still on, different weather) go back to the Captain. Lives as long as
/// the session, across aircraft changes. Pure and single-threaded, like the engine.
/// </summary>
public sealed class WorldSync
{
    public const int WorldStateIntervalMs = 1000;

    private readonly TrafficSource? _source;
    private bool _established;
    private bool _wasEstablished;
    private WorldSample? _localWorld;
    private WorldSample? _lastSentWorld;
    private long _lastWorldSent = long.MinValue / 2;
    private ulong _worldSeq;
    private ulong _lastWorldSeqRecv;
    private (int Foreign, bool Weather) _notice;
    private (int Foreign, bool Weather)? _noticeSent;
    private bool _stopped;

    public WorldSync(bool isSource, TrafficOptions? options = null)
    {
        IsSource = isSource;
        if (isSource) _source = new TrafficSource(options);
        SimCommands.Enqueue(new SetWorldRole(isSource ? WorldRole.Source : WorldRole.Mirror));
    }

    public bool IsSource { get; }
    public Queue<Outgoing> Outbox { get; } = new();
    public Queue<SimCommand> SimCommands { get; } = new();
    public TrafficSource? Source => _source;

    /// <summary>What the First Officer reported about their world (seen on the Captain's side).</summary>
    public WorldNotice? PeerNotice { get; private set; }

    public static bool Handles(Message m) => m is TrafficFrame or TrafficAdded or TrafficRemoved or TrafficReset or WorldState or WorldNotice;

    /// <summary>Whether the cockpit engine has a compatible peer (same protocol); nothing flows before that.</summary>
    public void SetEstablished(bool established)
    {
        if (established && !_established) _lastWorldSeqRecv = 0; // a new (or restarted) Captain starts counting again
        _established = established;
    }

    public void OnMessage(Message m, long now)
    {
        if (!_established || _stopped) return;
        if (IsSource)
        {
            if (m is WorldNotice n) PeerNotice = n;
            return;
        }
        switch (m)
        {
            case TrafficFrame or TrafficAdded or TrafficRemoved or TrafficReset:
                SimCommands.Enqueue(new MirrorTraffic(m));
                break;
            case WorldState w when w.Seq > _lastWorldSeqRecv:
                _lastWorldSeqRecv = w.Seq;
                SimCommands.Enqueue(new WorldStateUpdate(w.Time, w.Sample));
                break;
        }
    }

    // ---- Captain: what the local sim reports

    public void OnTrafficScan(TrafficKind kind, IReadOnlyList<(uint Id, TrafficSample Sample)> objects, long now) => _source?.OnScan(kind, objects, now);
    public void OnTrafficIdentity(uint id, TrafficIdentity identity) => _source?.OnIdentity(id, identity);
    public void OnTrafficLost() => _source?.OnSimLost();
    public void OnUserPosition(FlightSample s) => _source?.OnUserPosition(s);

    /// <summary>The local sim's time/rate/pause/weather (both roles; only the Captain sends it).</summary>
    public void OnLocalWorld(WorldSample s) => _localWorld = s;

    // ---- First Officer: what is wrong locally

    public void OnLocalNotice(int foreignTraffic, bool weatherMismatch) => _notice = (foreignTraffic, weatherMismatch);

    public void Tick(long now)
    {
        if (_stopped) return;
        bool justEstablished = _established && !_wasEstablished;
        if (!_established && _wasEstablished) _noticeSent = null;
        _wasEstablished = _established;

        if (IsSource)
        {
            _source!.Tick(now, _established, Send);
            if (!_established || _localWorld is not { } w) return;
            bool changed = _lastSentWorld is not { } last || last.Paused != w.Paused || Math.Abs(last.SimRate - w.SimRate) > 1e-6;
            if (changed || justEstablished || now - _lastWorldSent >= WorldStateIntervalMs)
            {
                _lastWorldSent = now;
                _lastSentWorld = w;
                Send(new Outgoing(Channel.Sequenced, new WorldState(++_worldSeq, now, w)));
            }
            return;
        }

        if (_established && _noticeSent != _notice)
        {
            _noticeSent = _notice;
            Send(new Outgoing(Channel.ReliableOrdered, new WorldNotice(_notice.Foreign, _notice.Weather)));
        }
    }

    /// <summary>Session over: stop scanning/mirroring and remove every mirrored aircraft.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        if (!IsSource) SimCommands.Enqueue(new ClearTraffic());
        SimCommands.Enqueue(new SetWorldRole(WorldRole.None));
    }

    private void Send(Outgoing o) => Outbox.Enqueue(o);
}
