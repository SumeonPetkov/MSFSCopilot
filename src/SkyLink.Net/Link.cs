using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using LiteNetLib;
using SkyLink.Core;

namespace SkyLink.Net;

public sealed class LinkOptions
{
    public const int DefaultPort = 47800;

    public required string SessionCode { get; init; }
    public int Port { get; init; } = DefaultPort;
    public int DisconnectTimeoutMs { get; init; } = 10_000;
    public int PingIntervalMs { get; init; } = 500;
    public int ReconnectMinMs { get; init; } = 1000;
    public int ReconnectMaxMs { get; init; } = 10_000;

    /// <summary>Test hooks: LiteNetLib's built-in network impairment.</summary>
    public int SimulateLossPercent { get; init; }
    public int SimulateLatencyMinMs { get; init; }
    public int SimulateLatencyMaxMs { get; init; }
}

public enum LinkState { Stopped, Listening, Connecting, Connected }

public readonly record struct LinkStats(int RttMs, float LossPercent, long BytesSent, long BytesReceived, long BadPackets, string? Remote);

/// <summary>
/// One shared-cockpit connection over UDP (LiteNetLib). The host listens; the joiner dials and
/// keeps redialling with backoff. Only someone who knows the session code can connect, and
/// every packet carries an HMAC tag derived from it. All callbacks are raised from
/// <see cref="Poll"/> on the caller's thread.
/// </summary>
public sealed class Link : IDisposable
{
    private const byte ReliableChannel = 0;
    private const byte SequencedChannel = 1;
    private const int TagLength = 8;

    private readonly LinkOptions _o;
    private readonly bool _isHost;
    private readonly string? _remoteHost;
    private readonly int _remotePort;
    private readonly NetManager _net;
    private readonly EventBasedNetListener _listener = new();
    private readonly string _connectKey;
    private readonly byte[] _hmacKey;
    private NetPeer? _peer;
    private NetPeer? _dialPeer;
    private long _nextDialAt;
    private int _backoff;
    private long _badPackets;
    private long _bytesSent;
    private long _bytesReceived;

    public event Action<string>? Connected;
    public event Action<string>? Disconnected;
    public event Action<Message>? Received;
    public event Action<string>? Log;

    public LinkState State { get; private set; } = LinkState.Stopped;
    public bool IsHost => _isHost;

    private Link(LinkOptions options, bool isHost, string? remoteHost, int remotePort)
    {
        if (string.IsNullOrWhiteSpace(options.SessionCode)) throw new ArgumentException("A session code is required.");
        _o = options;
        _isHost = isHost;
        _remoteHost = remoteHost;
        _remotePort = remotePort;
        string code = NormalizeCode(options.SessionCode);
        _connectKey = $"SkyLink/{SyncEngine.ProtocolVersion}/{Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes("skylink-session:" + code)))[..24]}";
        _hmacKey = SHA256.HashData(Encoding.UTF8.GetBytes("skylink-hmac:" + code));

        _net = new NetManager(_listener)
        {
            ChannelsCount = 2,
            DisconnectTimeout = options.DisconnectTimeoutMs,
            PingInterval = options.PingIntervalMs,
            UpdateTime = 5,
            EnableStatistics = true,
            AutoRecycle = true,
            SimulatePacketLoss = options.SimulateLossPercent > 0,
            SimulationPacketLossChance = options.SimulateLossPercent,
            SimulateLatency = options.SimulateLatencyMaxMs > 0,
            SimulationMinLatency = options.SimulateLatencyMinMs,
            SimulationMaxLatency = options.SimulateLatencyMaxMs,
        };
        _listener.ConnectionRequestEvent += OnConnectionRequest;
        _listener.PeerConnectedEvent += OnPeerConnected;
        _listener.PeerDisconnectedEvent += OnPeerDisconnected;
        _listener.NetworkReceiveEvent += OnReceive;
        _listener.NetworkErrorEvent += (ep, err) => Log?.Invoke($"network error {err} ({ep})");
    }

    public static Link Host(LinkOptions options)
    {
        var link = new Link(options, true, null, 0);
        if (!link._net.Start(options.Port))
            throw new SocketException((int)SocketError.AddressAlreadyInUse);
        link.State = LinkState.Listening;
        link.Log?.Invoke($"listening on UDP {options.Port}");
        return link;
    }

    public static Link Join(string host, LinkOptions options)
    {
        var (h, port) = ParseEndpoint(host, options.Port);
        var link = new Link(options, false, h, port);
        if (!link._net.Start()) throw new SocketException((int)SocketError.SocketError);
        link.State = LinkState.Connecting;
        return link;
    }

    /// <summary>A random, easy-to-read 6-character code (no 0/O/1/I).</summary>
    public static string NewSessionCode()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        return string.Create(6, 0, (span, _) =>
        {
            for (int i = 0; i < span.Length; i++) span[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        });
    }

    public static string NormalizeCode(string code) => code.Trim().ToUpperInvariant().Replace("-", "").Replace(" ", "");

    public static (string Host, int Port) ParseEndpoint(string text, int defaultPort)
    {
        text = text.Trim();
        if (text.StartsWith('[')) // [ipv6]:port
        {
            int end = text.IndexOf(']');
            string addr = text[1..end];
            return (addr, end + 2 < text.Length ? int.Parse(text[(end + 2)..]) : defaultPort);
        }
        int colon = text.LastIndexOf(':');
        if (colon > 0 && text.IndexOf(':') == colon && int.TryParse(text[(colon + 1)..], out int p)) return (text[..colon], p);
        return (text, defaultPort);
    }

    public void Poll(long now)
    {
        _net.PollEvents();
        if (!_isHost && _peer is null && _dialPeer is null && now >= _nextDialAt)
        {
            State = LinkState.Connecting;
            _nextDialAt = now + Math.Min(_o.ReconnectMaxMs, _o.ReconnectMinMs << Math.Min(_backoff, 4));
            _backoff++;
            try
            {
                _dialPeer = _net.Connect(_remoteHost!, _remotePort, _connectKey);
            }
            catch (Exception e) when (e is SocketException or ArgumentException)
            {
                Log?.Invoke($"cannot reach {_remoteHost}:{_remotePort}: {e.Message}");
            }
        }
    }

    public void Send(Outgoing o)
    {
        var peer = _peer;
        if (peer is null || peer.ConnectionState != ConnectionState.Connected) return;
        byte[] payload = MessageCodec.Encode(o.Message);
        byte[] packet = new byte[payload.Length + TagLength];
        payload.CopyTo(packet, 0);
        Tag(payload, packet.AsSpan(payload.Length));
        _bytesSent += packet.Length;
        if (o.Channel == Channel.Sequenced) peer.Send(packet, SequencedChannel, DeliveryMethod.Sequenced);
        else peer.Send(packet, ReliableChannel, DeliveryMethod.ReliableOrdered);
    }

    public LinkStats Stats
    {
        get
        {
            var p = _peer;
            return new LinkStats(p?.RoundTripTime ?? 0, p is null ? 0 : p.Statistics.PacketLossPercent,
                _bytesSent, _bytesReceived, _badPackets, p?.ToString());
        }
    }

    private void OnConnectionRequest(ConnectionRequest request)
    {
        if (!_isHost)
        {
            request.Reject();
            return;
        }
        var peer = request.AcceptIfKey(_connectKey);
        if (peer is null)
        {
            Log?.Invoke($"refused {request.RemoteEndPoint}: wrong session code or SkyLink version");
            return;
        }
        // Two seats only. A newer connection from someone who knows the code replaces a stale
        // one (the joiner restarted or its old connection hasn't timed out on our side yet).
        if (_peer is { } old && old != peer)
        {
            Log?.Invoke($"replacing connection {old} with {peer}");
            old.Disconnect();
        }
    }

    private void OnPeerConnected(NetPeer peer)
    {
        if (peer == _dialPeer) _dialPeer = null;
        if (_peer is not null && _peer != peer) Disconnected?.Invoke("replaced by a new connection");
        _peer = peer;
        _backoff = 0;
        State = LinkState.Connected;
        Connected?.Invoke(peer.ToString());
    }

    private void OnPeerDisconnected(NetPeer peer, DisconnectInfo info)
    {
        if (peer == _dialPeer)
        {
            _dialPeer = null;
            Log?.Invoke($"could not connect to {_remoteHost}:{_remotePort}: {Describe(info)}; retrying");
            return;
        }
        if (peer != _peer) return;
        _peer = null;
        State = _isHost ? LinkState.Listening : LinkState.Connecting;
        Disconnected?.Invoke(Describe(info));
    }

    private static string Describe(DisconnectInfo info) =>
        info.Reason switch
        {
            DisconnectReason.ConnectionRejected => "connection refused (wrong session code or SkyLink version?)",
            DisconnectReason.ConnectionFailed => "no answer from the host (address, port forwarding or firewall?)",
            DisconnectReason.Timeout => "connection timed out",
            DisconnectReason.RemoteConnectionClose => "the other pilot closed the connection",
            var r => r.ToString(),
        };

    private void OnReceive(NetPeer peer, NetPacketReader reader, byte channel, DeliveryMethod method)
    {
        if (peer != _peer) return;
        byte[] data = reader.GetRemainingBytes();
        _bytesReceived += data.Length;
        if (data.Length <= TagLength || !CheckTag(data))
        {
            _badPackets++;
            return;
        }
        Message m;
        try
        {
            m = MessageCodec.Decode(data.AsSpan(0, data.Length - TagLength));
        }
        catch (InvalidDataException)
        {
            _badPackets++;
            return;
        }
        Received?.Invoke(m);
    }

    private void Tag(ReadOnlySpan<byte> payload, Span<byte> tag)
    {
        Span<byte> full = stackalloc byte[32];
        HMACSHA256.HashData(_hmacKey, payload, full);
        full[..TagLength].CopyTo(tag);
    }

    private bool CheckTag(byte[] data)
    {
        var payload = data.AsSpan(0, data.Length - TagLength);
        Span<byte> expected = stackalloc byte[TagLength];
        Tag(payload, expected);
        return CryptographicOperations.FixedTimeEquals(expected, data.AsSpan(data.Length - TagLength));
    }

    /// <summary>Local addresses worth telling the other pilot (LAN, Tailscale 100.x, ZeroTier, Radmin 26.x).</summary>
    public static IEnumerable<string> LocalAddresses()
    {
        foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(ua.Address))
                    yield return $"{ua.Address} ({ni.Name})";
        }
    }

    public void Dispose()
    {
        _peer?.Disconnect();
        _net.Stop();
        State = LinkState.Stopped;
    }
}
