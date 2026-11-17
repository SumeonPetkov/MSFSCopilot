using System.Net;
using System.Net.Sockets;
using System.Text.Json;

namespace SkyLink.Lobby;

public sealed record BeaconParty(string Id, string Name, int Port, int Members, string? Captain, string? FirstOfficer, PartyPhase Phase);

/// <summary>What every SkyLink app announces about itself, every 2 seconds.</summary>
public sealed record Beacon(int V, string Id, string Name, bool SimRunning, string? SimAircraft, BeaconParty? Party)
{
    public const int Version = 1;
}

public sealed record DiscoveredPlayer(Beacon Beacon, IPEndPoint From, DateTimeOffset LastSeen)
{
    public string Address => From.Address.ToString();

    /// <summary>Only a party host advertises a port; members show their party with port 0.</summary>
    public bool CanJoin => Beacon.Party is { Port: > 0, Members: < PartyAuthority.MaxMembers, Phase: PartyPhase.Lobby };
}

/// <summary>
/// Finds other SkyLink pilots by address: beacons go only to the addresses you add (and to
/// anyone who added you, so adding someone on one side is enough for both to see each other).
/// Nothing is broadcast or scanned.
/// </summary>
public sealed class Discovery : IDisposable
{
    public const int DefaultPort = 47801;
    private const int BeaconEveryMs = 2000;
    private const int ExpireAfterMs = 8000;
    private const int AnswerBackTtlMs = 30_000;

    private readonly UdpClient _udp;
    private readonly int _port;
    private readonly List<IPEndPoint> _manual = [];
    private readonly List<IPEndPoint> _extraTargets;
    private readonly Dictionary<IPEndPoint, long> _answerBack = [];
    private readonly Dictionary<string, DiscoveredPlayer> _players = [];
    private long _lastBeacon = long.MinValue / 2;

    public Func<Beacon>? CurrentBeacon { get; set; }
    public string LocalId { get; }
    public event Action? Changed;

    public IReadOnlyList<DiscoveredPlayer> Players =>
        _players.Values.OrderBy(p => p.Beacon.Name, StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>The addresses added by hand (resolved to IP:port).</summary>
    public IReadOnlyList<IPEndPoint> Manual => _manual;

    public Discovery(string localId, int port = DefaultPort, IEnumerable<IPEndPoint>? extraTargets = null)
    {
        LocalId = localId;
        _port = port;
        _extraTargets = extraTargets?.ToList() ?? [];
        _udp = new UdpClient(AddressFamily.InterNetwork);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        // Ignore ICMP "port unreachable" resets for beacons sent to addresses without SkyLink.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { _udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (PlatformNotSupportedException) { }
    }

    /// <summary>Add a pilot by IP (or host name), optionally with :port.</summary>
    public IPEndPoint AddManual(string text)
    {
        var (host, port) = SkyLink.Net.Link.ParseEndpoint(text, _port);
        var ip = IPAddress.TryParse(host, out var parsed) ? parsed
            : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
              ?? throw new ArgumentException($"cannot resolve '{host}'");
        var ep = new IPEndPoint(ip, port);
        if (!_manual.Contains(ep)) _manual.Add(ep);
        _lastBeacon = long.MinValue / 2; // announce now
        return ep;
    }

    public void RemoveManual(IPEndPoint ep)
    {
        _manual.Remove(ep);
        _answerBack.Remove(ep);
        foreach (var (id, _) in _players.Where(kv => kv.Value.From.Address.Equals(ep.Address)).ToList()) _players.Remove(id);
        Changed?.Invoke();
    }

    public void Poll(long now)
    {
        Receive(now);
        if (now - _lastBeacon >= BeaconEveryMs && CurrentBeacon is { } make)
        {
            _lastBeacon = now;
            var data = JsonSerializer.SerializeToUtf8Bytes(make(), PartyNode.Json);
            foreach (var target in Targets(now)) TrySend(data, target);
        }
        bool expired = false;
        foreach (var (id, p) in _players.ToList())
            if ((DateTimeOffset.UtcNow - p.LastSeen).TotalMilliseconds > ExpireAfterMs)
            {
                _players.Remove(id);
                expired = true;
            }
        if (expired) Changed?.Invoke();
    }

    private IEnumerable<IPEndPoint> Targets(long now)
    {
        foreach (var (ep, _) in _answerBack.Where(kv => kv.Value < now).ToList()) _answerBack.Remove(ep);
        return _manual.Concat(_extraTargets).Concat(_answerBack.Keys).Distinct().ToList();
    }

    private void TrySend(byte[] data, IPEndPoint target)
    {
        try
        {
            _udp.Send(data, data.Length, target);
        }
        catch (SocketException)
        {
            // Unreachable right now (adapter down, no route); try again next round.
        }
    }

    private void Receive(long now)
    {
        bool changed = false;
        while (_udp.Available > 0)
        {
            IPEndPoint from = new(IPAddress.Any, 0);
            byte[] data;
            try
            {
                data = _udp.Receive(ref from);
            }
            catch (SocketException)
            {
                continue;
            }
            Beacon? b;
            try
            {
                b = JsonSerializer.Deserialize<Beacon>(data, PartyNode.Json);
            }
            catch (JsonException)
            {
                continue;
            }
            if (b is null || b.V != Beacon.Version || b.Id == LocalId || string.IsNullOrEmpty(b.Id)) continue;

            // Answer whoever found us, so adding someone on one side is enough.
            bool known = _answerBack.ContainsKey(from);
            _answerBack[from] = now + AnswerBackTtlMs;
            if (!known) _lastBeacon = long.MinValue / 2;

            var prev = _players.GetValueOrDefault(b.Id);
            _players[b.Id] = new DiscoveredPlayer(b, from, DateTimeOffset.UtcNow);
            if (prev is null || prev.Beacon != b || !prev.From.Equals(from)) changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    public void Dispose() => _udp.Dispose();
}
