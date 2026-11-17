using System.Text.Json;
using System.Text.Json.Serialization;
using LiteNetLib;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("SkyLink.Integration.Tests")]

namespace SkyLink.Lobby;

[JsonPolymorphic(TypeDiscriminatorPropertyName = "t")]
[JsonDerivedType(typeof(JoinRequest), "join")]
[JsonDerivedType(typeof(JoinReply), "joined")]
[JsonDerivedType(typeof(StateUpdate), "state")]
[JsonDerivedType(typeof(CatalogUpdate), "catalog")]
[JsonDerivedType(typeof(Request), "req")]
[JsonDerivedType(typeof(RequestResult), "res")]
[JsonDerivedType(typeof(Bye), "bye")]
public abstract record LobbyMessage;

public sealed record JoinRequest(string MemberId, string Name, int Protocol) : LobbyMessage;
public sealed record JoinReply(bool Accepted, string? Reason) : LobbyMessage;
public sealed record StateUpdate(PartyState State) : LobbyMessage;
public sealed record CatalogUpdate(long Version, List<CatalogAircraft> Aircraft) : LobbyMessage;
public sealed record Request(long Id, PartyIntent Intent) : LobbyMessage;
public sealed record RequestResult(long Id, string? Error) : LobbyMessage;
public sealed record Bye(string Reason) : LobbyMessage;

public sealed record LocalPlayer(string Id, string Name);

/// <summary>
/// One player's end of a party. The host owns the <see cref="PartyAuthority"/> and pushes
/// versioned snapshots (on every change and every 2 s, so nothing can stay stale); a member
/// sends requests and shows the newest snapshot it has. A member whose connection drops
/// reconnects by itself and gets its seat back. Call <see cref="Poll"/> regularly from one
/// thread (the UI timer); all events fire from inside it.
/// </summary>
public sealed class PartyNode : IDisposable
{
    public const int Protocol = 1;
    public const int DefaultPort = 47802;
    private const int SnapshotEveryMs = 2000;

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly LocalPlayer _me;
    private readonly NetManager _net;
    private readonly EventBasedNetListener _listener = new();
    private readonly PartyAuthority? _authority;
    private readonly string _partyId;
    private readonly string? _hostAddress;
    private readonly int _port;
    private readonly Dictionary<NetPeer, string> _members = [];
    private readonly Dictionary<long, string> _pending = [];
    private NetPeer? _hostPeer;
    private NetPeer? _dialing;
    private bool _joined;
    private bool _leaving;
    private long _lastSnapshot;
    private long _sentVersion = -1;
    private long _sentCatalog = -1;
    private long _nextDial;
    private long _requestIds;

    public PartyState? State { get; private set; }
    public IReadOnlyList<CatalogAircraft> SharedCatalog { get; private set; } = [];
    public bool IsHost => _authority is not null;
    public bool IsConnected => IsHost || _joined;
    public string PartyId => _partyId;
    /// <summary>For a member: the address it reached the party host on.</summary>
    public string? HostAddress => _hostAddress;
    public int Port => _port;
    public LocalPlayer Me => _me;

    public event Action? Changed;
    public event Action<string>? Log;
    public event Action<string>? Error;
    /// <summary>The host refused us, removed us, or closed the party; the node is finished.</summary>
    public event Action<string>? Ended;

    private PartyNode(LocalPlayer me, PartyAuthority? authority, string partyId, string? hostAddress, int port)
    {
        _me = me;
        _authority = authority;
        _partyId = partyId;
        _hostAddress = hostAddress;
        _port = port;
        _net = new NetManager(_listener) { DisconnectTimeout = 8000, PingInterval = 1000, AutoRecycle = true, UpdateTime = 10 };
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnConnected;
        _listener.PeerDisconnectedEvent += OnDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
    }

    private string Key => $"SkyLinkParty/{Protocol}/{_partyId}";

    public static PartyNode Host(LocalPlayer me, string partyName, int port = DefaultPort)
    {
        var auth = new PartyAuthority(me.Id, me.Name, partyName);
        var node = new PartyNode(me, auth, auth.State.PartyId, null, port);
        if (!node._net.Start(port)) throw new InvalidOperationException($"UDP port {port} is in use (is another SkyLink running?)");
        node.State = auth.State;
        node.SharedCatalog = auth.SharedCatalog;
        return node;
    }

    public static PartyNode Join(LocalPlayer me, string hostAddress, int port, string partyId)
    {
        var node = new PartyNode(me, null, partyId, hostAddress, port);
        if (!node._net.Start()) throw new InvalidOperationException("could not open a UDP socket");
        return node;
    }

    /// <summary>Ask for a change. On the host it applies at once; members send it to the host.</summary>
    public void Send(PartyIntent intent)
    {
        if (_authority is not null)
        {
            if (_authority.Apply(_me.Id, intent) is { } err) Error?.Invoke(err);
            Publish(force: false);
            return;
        }
        if (_hostPeer is null || !_joined)
        {
            Error?.Invoke("not connected to the party host");
            return;
        }
        long id = ++_requestIds;
        _pending[id] = intent.GetType().Name;
        SendTo(_hostPeer, new Request(id, intent));
    }

    /// <summary>The host's view of why Start is (not) possible; members compute it from state.</summary>
    public string? CanStart() => _authority?.CanStart() ?? LocalCanStart();

    public void Poll(long now)
    {
        _net.PollEvents();
        if (_authority is not null)
        {
            Publish(force: now - _lastSnapshot >= SnapshotEveryMs, now);
            return;
        }
        if (!_leaving && _hostPeer is null && _dialing is null && now >= _nextDial)
        {
            _nextDial = now + 2000;
            _dialing = _net.Connect(_hostAddress!, _port, Key);
        }
    }

    public void Leave()
    {
        _leaving = true;
        foreach (var p in _members.Keys.ToList()) Close(p, "the host closed the party");
        if (_hostPeer is not null) Close(_hostPeer, "left");
        _net.PollEvents();
    }

    /// <summary>
    /// Disconnect with the reason inside the disconnect packet itself: a message queued just
    /// before disconnecting can be lost, the disconnect reason cannot.
    /// </summary>
    private static void Close(NetPeer peer, string reason) =>
        peer.Disconnect(System.Text.Encoding.UTF8.GetBytes("bye:" + reason));

    private static string? ReasonOf(DisconnectInfo info)
    {
        if (info.AdditionalData is not { AvailableBytes: > 0 } data) return null;
        string text = System.Text.Encoding.UTF8.GetString(data.GetRemainingBytes());
        return text.StartsWith("bye:", StringComparison.Ordinal) ? text[4..] : null;
    }

    // ------------------------------------------------------------------ host

    private void OnConnectionRequest(ConnectionRequest r)
    {
        if (_authority is null) r.Reject();
        else if (r.AcceptIfKey(Key) is null) Log?.Invoke($"refused {r.RemoteEndPoint}: not for this party");
    }

    private void Publish(bool force, long now = 0)
    {
        if (_authority is null) return;
        var s = _authority.State;
        bool changed = s.Version != _sentVersion;
        if (changed)
        {
            State = s;
            SharedCatalog = _authority.SharedCatalog;
        }
        if (s.CatalogVersion != _sentCatalog)
        {
            _sentCatalog = s.CatalogVersion;
            foreach (var p in _members.Keys) SendTo(p, new CatalogUpdate(s.CatalogVersion, _authority.SharedCatalog.ToList()));
        }
        if (changed || force)
        {
            _sentVersion = s.Version;
            if (force) _lastSnapshot = now;
            foreach (var p in _members.Keys) SendTo(p, new StateUpdate(s));
        }
        if (changed) Changed?.Invoke();
    }

    private void HandleAsHost(NetPeer peer, LobbyMessage m)
    {
        var auth = _authority!;
        switch (m)
        {
            case JoinRequest j:
                if (j.Protocol != Protocol)
                {
                    Refuse(peer, $"different SkyLink version (party protocol {Protocol}, theirs {j.Protocol})");
                    return;
                }
                var outcome = auth.Join(j.MemberId, j.Name, peer.Address.ToString());
                if (outcome is JoinOutcome.Full or JoinOutcome.InFlightLocked)
                {
                    Refuse(peer, outcome == JoinOutcome.Full ? "the party is full (Captain and First Officer are taken)" : "a flight is in progress");
                    return;
                }
                // The same player on a new connection replaces the old one.
                foreach (var (oldPeer, id) in _members.Where(kv => kv.Value == j.MemberId && kv.Key != peer).ToList())
                {
                    _members.Remove(oldPeer);
                    oldPeer.Disconnect();
                }
                _members[peer] = j.MemberId;
                SendTo(peer, new JoinReply(true, null));
                SendTo(peer, new CatalogUpdate(auth.State.CatalogVersion, auth.SharedCatalog.ToList()));
                Log?.Invoke($"{j.Name} {(outcome == JoinOutcome.Rejoined ? "rejoined" : "joined")} the party");
                _sentVersion = -1; // push the new state to everyone, including the newcomer
                Publish(force: false);
                break;
            case Request req when _members.TryGetValue(peer, out var from):
                string? err = auth.Apply(from, req.Intent);
                SendTo(peer, new RequestResult(req.Id, err));
                if (req.Intent is Kick k) DropMember(k.MemberId, "you were removed from the party");
                Publish(force: false);
                break;
            case Bye when _members.Remove(peer, out var who):
                auth.Remove(who);
                Publish(force: false);
                break;
        }
    }

    public void KickMember(string memberId)
    {
        Send(new Kick(memberId));
        DropMember(memberId, "you were removed from the party");
    }

    private void DropMember(string memberId, string reason)
    {
        foreach (var p in _members.Where(kv => kv.Value == memberId).Select(kv => kv.Key).ToList())
        {
            _members.Remove(p);
            Close(p, reason);
        }
    }

    private static void Refuse(NetPeer peer, string reason) => Close(peer, reason);

    // ------------------------------------------------------------------ member

    private void HandleAsMember(LobbyMessage m)
    {
        switch (m)
        {
            case JoinReply { Accepted: true }:
                _joined = true;
                Log?.Invoke("joined the party");
                Changed?.Invoke();
                break;
            case JoinReply r:
                End(r.Reason ?? "the host refused to let us join");
                break;
            case StateUpdate u when State is null || u.State.Version > State.Version:
                State = u.State;
                Changed?.Invoke();
                break;
            case CatalogUpdate c:
                SharedCatalog = c.Aircraft;
                Changed?.Invoke();
                break;
            case RequestResult r:
                _pending.Remove(r.Id);
                if (r.Error is { } e) Error?.Invoke(e);
                break;
            case Bye b:
                End(b.Reason);
                break;
        }
    }

    private void End(string reason)
    {
        _leaving = true;
        _joined = false;
        _net.DisconnectAll();
        Ended?.Invoke(reason);
    }

    private string? LocalCanStart()
    {
        if (State is not { } s) return "not connected";
        if (s.Captain is null || s.FirstOfficer is null) return "waiting for a First Officer";
        if (s.Members.FirstOrDefault(m => !m.Connected) is { } off) return $"{off.Name} is offline";
        if (s.Setup.AircraftTitle is null) return "choose an aircraft and livery";
        return s.Phase == PartyPhase.Lobby ? null : "a flight is already running";
    }

    // ------------------------------------------------------------------ transport

    private void OnConnected(NetPeer peer)
    {
        if (_authority is not null) return; // members identify themselves with JoinRequest
        if (peer != _dialing) return;
        _dialing = null;
        _hostPeer = peer;
        SendTo(peer, new JoinRequest(_me.Id, _me.Name, Protocol));
    }

    private void OnDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (_authority is not null)
        {
            if (_members.Remove(peer, out var id))
            {
                if (ReasonOf(info) == "left")
                {
                    _authority.Remove(id);
                    Log?.Invoke($"{id} left the party");
                    Publish(force: false);
                    return;
                }
                _authority.Disconnected(id);
                Log?.Invoke($"{_authority.State.Member(id)?.Name ?? id} lost connection ({info.Reason})");
                Publish(force: false);
            }
            return;
        }
        if (peer == _dialing) _dialing = null;
        // Refused, removed, or the host closed the party: stop for good, with the host's reason.
        if (ReasonOf(info) is { } reason && !_leaving)
        {
            _hostPeer = null;
            End(reason);
            return;
        }
        if (peer != _hostPeer) return;
        _hostPeer = null;
        bool wasJoined = _joined;
        _joined = false;
        if (!_leaving)
        {
            if (wasJoined) Log?.Invoke($"lost the party host ({info.Reason}); reconnecting");
            Changed?.Invoke();
        }
    }

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        LobbyMessage? m;
        try
        {
            m = JsonSerializer.Deserialize<LobbyMessage>(reader.GetRemainingBytes(), Json);
        }
        catch (JsonException e)
        {
            Log?.Invoke($"ignored a malformed party message: {e.Message}");
            return;
        }
        if (m is null) return;
        if (_authority is not null) HandleAsHost(peer, m);
        else if (peer == _hostPeer) HandleAsMember(m);
    }

    private static void SendTo(NetPeer peer, LobbyMessage m) =>
        peer.Send(JsonSerializer.SerializeToUtf8Bytes(m, Json), DeliveryMethod.ReliableOrdered);

    /// <summary>Tests: vanish like a crashed app or a pulled cable (no goodbye).</summary>
    internal void DropWithoutGoodbye()
    {
        _leaving = true;
        _net.Stop(false);
    }

    public void Dispose()
    {
        if (!_leaving) Leave();
        _net.Stop();
    }
}
