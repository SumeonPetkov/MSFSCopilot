using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text.Json;

namespace SkyLink.Lobby;

public sealed record BeaconParty(string Id, string Name, int Port, int Members, string? Captain, string? FirstOfficer, PartyPhase Phase);

/// <summary>What every SkyLink app announces about itself, every 2 seconds.</summary>
public sealed record Beacon(int V, string Id, string Name, bool SimRunning, string? SimAircraft, BeaconParty? Party)
{
    public const int Version = 1;
}

public sealed record DiscoveredPlayer(Beacon Beacon, IPEndPoint From, string Via, DateTimeOffset LastSeen)
{
    public string Address => From.Address.ToString();
    /// <summary>Only a party host advertises a port; members show their party with port 0.</summary>
    public bool CanJoin => Beacon.Party is { Port: > 0, Members: < PartyAuthority.MaxMembers, Phase: PartyPhase.Lobby };
}

/// <summary>
/// Finds other SkyLink players. Tailscale does not carry broadcasts, so beacons go out three
/// ways: broadcast on every local network (LAN, ZeroTier, Radmin), unicast to every online
/// Tailscale peer (from <c>tailscale status --json</c>), and unicast to manually added
/// addresses. Whoever hears a beacon answers its sender directly, so adding someone by hand on
/// one side is enough for both to see each other.
/// </summary>
public sealed class Discovery : IDisposable
{
    public const int DefaultPort = 47801;
    private const int BeaconEveryMs = 2000;
    private const int ExpireAfterMs = 8000;
    private const int TailscaleRefreshMs = 30_000;
    private const int LearnedPeerTtlMs = 30_000;

    private readonly UdpClient _udp;
    private readonly int _port;
    private readonly List<IPEndPoint> _manual = [];
    private readonly List<IPEndPoint> _extraTargets;
    private readonly Dictionary<IPEndPoint, long> _learned = [];
    private readonly Dictionary<string, DiscoveredPlayer> _players = [];
    private readonly object _tsLock = new();
    private List<(IPAddress Ip, string Host)> _tailscalePeers = [];
    private IPAddress? _tailscaleSelf;
    private long _lastBeacon = long.MinValue / 2;
    private long _lastTailscale = long.MinValue / 2;
    private bool _tailscaleRunning;

    public Func<Beacon>? CurrentBeacon { get; set; }
    public string LocalId { get; }
    public bool TailscaleAvailable { get; private set; }
    public string? TailscaleAddress => _tailscaleSelf?.ToString();
    public event Action? Changed;

    public IReadOnlyList<DiscoveredPlayer> Players =>
        _players.Values.OrderBy(p => p.Beacon.Name, StringComparer.OrdinalIgnoreCase).ToList();

    public Discovery(string localId, int port = DefaultPort, IEnumerable<IPEndPoint>? extraTargets = null)
    {
        LocalId = localId;
        _port = port;
        _extraTargets = extraTargets?.ToList() ?? [];
        _udp = new UdpClient(AddressFamily.InterNetwork) { EnableBroadcast = true };
        _udp.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
        _udp.Client.Bind(new IPEndPoint(IPAddress.Any, port));
        // Ignore ICMP "port unreachable" resets for beacons sent to peers without SkyLink.
        const int SIO_UDP_CONNRESET = -1744830452;
        try { _udp.Client.IOControl(SIO_UDP_CONNRESET, [0, 0, 0, 0], null); } catch (PlatformNotSupportedException) { }
    }

    public void AddManual(string text)
    {
        var (host, port) = SkyLink.Net.Link.ParseEndpoint(text, _port);
        var ip = IPAddress.TryParse(host, out var parsed) ? parsed
            : Dns.GetHostAddresses(host).FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork)
              ?? throw new ArgumentException($"cannot resolve '{host}'");
        var ep = new IPEndPoint(ip, port);
        if (!_manual.Contains(ep)) _manual.Add(ep);
        _lastBeacon = long.MinValue / 2; // announce now
    }

    public void Poll(long now)
    {
        Receive(now);
        if (now - _lastTailscale >= TailscaleRefreshMs && !_tailscaleRunning)
        {
            _lastTailscale = now;
            _tailscaleRunning = true;
            Task.Run(RefreshTailscale);
        }
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
        var seen = new HashSet<IPEndPoint>();
        IEnumerable<IPEndPoint> All()
        {
            yield return new IPEndPoint(IPAddress.Broadcast, _port);
            foreach (var b in BroadcastAddresses()) yield return new IPEndPoint(b, _port);
            lock (_tsLock)
                foreach (var (ip, _) in _tailscalePeers) yield return new IPEndPoint(ip, _port);
            foreach (var m in _manual) yield return m;
            foreach (var x in _extraTargets) yield return x;
            foreach (var (ep, until) in _learned.ToList())
            {
                if (until < now) _learned.Remove(ep);
                else yield return ep;
            }
        }
        foreach (var t in All()) if (seen.Add(t)) yield return t;
    }

    /// <summary>Subnet-directed broadcast address of every IPv4 network we are on (skipping /32 like Tailscale).</summary>
    public static IEnumerable<IPAddress> BroadcastAddresses()
    {
        foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (ni.OperationalStatus != OperationalStatus.Up || ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
            foreach (var ua in ni.GetIPProperties().UnicastAddresses)
            {
                if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask is null) continue;
                byte[] ip = ua.Address.GetAddressBytes(), mask = ua.IPv4Mask.GetAddressBytes();
                if (mask.All(b => b == 255)) continue;
                var bcast = new byte[4];
                for (int i = 0; i < 4; i++) bcast[i] = (byte)(ip[i] | ~mask[i]);
                yield return new IPAddress(bcast);
            }
        }
    }

    private void TrySend(byte[] data, IPEndPoint target)
    {
        try
        {
            _udp.Send(data, data.Length, target);
        }
        catch (SocketException)
        {
            // Unreachable networks are normal (a VPN adapter that is down); skip.
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

            // Answer whoever found us, so a one-sided manual add works both ways.
            bool known = _learned.ContainsKey(from);
            _learned[from] = now + LearnedPeerTtlMs;
            if (!known) _lastBeacon = long.MinValue / 2;

            string via;
            lock (_tsLock) via = _tailscalePeers.Any(p => p.Ip.Equals(from.Address)) ? "Tailscale"
                : _manual.Any(m => m.Address.Equals(from.Address)) ? "manual"
                : IPAddress.IsLoopback(from.Address) ? "this PC" : "LAN";
            var prev = _players.GetValueOrDefault(b.Id);
            // Prefer the Tailscale address if the same player is heard on several networks.
            if (prev is not null && prev.Via == "Tailscale" && via != "Tailscale" && (DateTimeOffset.UtcNow - prev.LastSeen).TotalMilliseconds < ExpireAfterMs)
                (from, via) = (prev.From, prev.Via);
            _players[b.Id] = new DiscoveredPlayer(b, from, via, DateTimeOffset.UtcNow);
            if (prev is null || prev.Beacon != b || prev.Via != via) changed = true;
        }
        if (changed) Changed?.Invoke();
    }

    /// <summary>Online Tailscale peers, from the local CLI. Silent when Tailscale is not installed.</summary>
    private void RefreshTailscale()
    {
        try
        {
            string? exe = FindTailscale();
            if (exe is null) return;
            var psi = new ProcessStartInfo(exe, "status --json") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true };
            using var p = Process.Start(psi);
            if (p is null) return;
            string json = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(4000)) return;
            var (self, peers) = ParseTailscaleStatus(json);
            lock (_tsLock)
            {
                _tailscalePeers = peers;
                _tailscaleSelf = self;
            }
            TailscaleAvailable = true;
        }
        catch (Exception e) when (e is IOException or System.ComponentModel.Win32Exception or JsonException or InvalidOperationException)
        {
            TailscaleAvailable = false;
        }
        finally
        {
            _tailscaleRunning = false;
        }
    }

    private static string? FindTailscale()
    {
        string pf = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        string candidate = Path.Combine(pf, "Tailscale", "tailscale.exe");
        if (File.Exists(candidate)) return candidate;
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            string c = Path.Combine(dir.Trim(), "tailscale.exe");
            if (File.Exists(c)) return c;
        }
        return null;
    }

    /// <summary>Self IPv4 and the online peers' IPv4 addresses from <c>tailscale status --json</c>.</summary>
    public static (IPAddress? Self, List<(IPAddress Ip, string Host)> Peers) ParseTailscaleStatus(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        static IPAddress? V4(JsonElement node) =>
            node.TryGetProperty("TailscaleIPs", out var ips) && ips.ValueKind == JsonValueKind.Array
                ? ips.EnumerateArray().Select(x => IPAddress.TryParse(x.GetString(), out var a) ? a : null)
                    .FirstOrDefault(a => a?.AddressFamily == AddressFamily.InterNetwork)
                : null;
        var self = root.TryGetProperty("Self", out var s) ? V4(s) : null;
        var peers = new List<(IPAddress, string)>();
        if (root.TryGetProperty("Peer", out var peerMap) && peerMap.ValueKind == JsonValueKind.Object)
            foreach (var peer in peerMap.EnumerateObject())
            {
                var v = peer.Value;
                bool online = v.TryGetProperty("Online", out var o) && o.ValueKind == JsonValueKind.True;
                if (online && V4(v) is { } ip)
                    peers.Add((ip, v.TryGetProperty("HostName", out var h) ? h.GetString() ?? "" : ""));
            }
        return (self, peers);
    }

    public void Dispose() => _udp.Dispose();
}
