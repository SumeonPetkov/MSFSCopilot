using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyLink.Definitions;
using SkyLink.Lobby;
using SkyLink.Core;
using SkyLink.Session;
using SkyLink.Sim;

namespace SkyLink.App;

public sealed record AddressItem(string Address, string Adapter);

public sealed record SavedPeerItem(string Text, string Status, bool Found, IPEndPoint Endpoint);

/// <summary>
/// A row of the Flight card's aircraft list: an aircraft (shown by name and developer, keyed by its
/// MSFS title) or, with <see cref="IsHeader"/>, the non-selectable developer heading above a group.
/// </summary>
public sealed record AircraftItem(string Title, string Name, string? Developer, string? Manufacturer, bool IsHeader = false)
{
    public const string UnknownDeveloper = "Other developers";

    public static AircraftItem From(CatalogAircraft a) => new(a.Title, a.Name, a.Developer, a.Manufacturer);

    /// <summary>The list in display order with a heading before each developer's aircraft.</summary>
    public static IEnumerable<AircraftItem> Grouped(IEnumerable<CatalogAircraft> aircraft)
    {
        string? group = null;
        foreach (var a in AircraftCatalog.ForDisplay(aircraft))
        {
            string g = a.Developer ?? UnknownDeveloper;
            if (g != group) yield return new AircraftItem("", group = g, null, null, IsHeader: true);
            yield return From(a);
        }
    }

    /// <summary>What type-to-find matches; headings never match.</summary>
    public string SearchText => IsHeader ? "" : Name;

    public override string ToString() => Developer is null ? Name : $"{Name}, {Developer}";
}

/// <summary>A livery: shown by its readable name, keyed by the name MSFS uses.</summary>
public sealed record LiveryItem(string Key, string Name)
{
    public override string ToString() => Name;
}

public sealed record PlayerItem(string Id, string Name, string Initial, string Status, string? PartyText, bool CanJoin, string Address, DiscoveredPlayer Source);

/// <summary>
/// Everything the window shows. A 50 ms timer pumps discovery, the party and the launch
/// coordinator on the UI thread; the simulator runs on its own thread inside <see cref="SimHost"/>.
/// </summary>
public sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    private readonly AppSettings _settings;
    private readonly SimHost _sim;
    private readonly Discovery _discovery;
    private readonly LaunchCoordinator _launch;
    private readonly DispatcherTimer _timer;
    private readonly int _partyPort;
    private PartyNode? _party;
    private long _shownVersion = -1;
    private long _shownCatalog = -1;
    private bool _applying;
    private bool _catalogRequested;
    private List<CatalogAircraft>? _myCatalog;

    public ObservableCollection<PlayerItem> Players { get; } = [];
    /// <summary>Aircraft rows grouped by developer, with heading rows (see <see cref="AircraftItem.Grouped"/>).</summary>
    public ObservableCollection<AircraftItem> Aircraft { get; } = [];
    public ObservableCollection<LiveryItem> Liveries { get; } = [];
    public ObservableCollection<string> Log { get; } = [];
    public ObservableCollection<AddressItem> MyAddresses { get; } = [];
    public ObservableCollection<SavedPeerItem> SavedPeers { get; } = [];
    private readonly Dictionary<string, IPEndPoint> _saved = [];

    // ---- you
    [ObservableProperty] private string _playerName;
    [ObservableProperty] private string _myInitial = "?";
    [ObservableProperty] private string _simStatusTitle = "MSFS not running";
    [ObservableProperty] private string _simStatusDetail = "";
    [ObservableProperty] private bool _simConnected;
    [ObservableProperty] private string _catalogText = "Aircraft list not read yet";
    [ObservableProperty] private string _manualAddress = "";

    // ---- party
    [ObservableProperty] private bool _inParty;
    [ObservableProperty] private bool _notInParty = true;
    [ObservableProperty] private string _partyName = "";
    [ObservableProperty] private string _partyRole = "";
    [ObservableProperty] private bool _isPartyHost;
    [ObservableProperty] private string _captainInitial = "";
    [ObservableProperty] private string _firstOfficerInitial = "";
    [ObservableProperty] private string _captainName = "empty";
    [ObservableProperty] private string _captainStatus = "";
    [ObservableProperty] private string _firstOfficerName = "empty";
    [ObservableProperty] private string _firstOfficerStatus = "";
    [ObservableProperty] private bool _iAmCaptain;
    [ObservableProperty] private bool _iAmFirstOfficer;
    [ObservableProperty] private bool _canTakeCaptain;
    [ObservableProperty] private bool _canTakeFirstOfficer;
    [ObservableProperty] private bool _canKick;

    // ---- setup
    [ObservableProperty] private string? _selectedAircraft;
    [ObservableProperty] private string? _selectedLivery;
    [ObservableProperty] private string _callsign = "";
    [ObservableProperty] private string _airlineCallsign = "";
    [ObservableProperty] private string _flightNumber = "";
    [ObservableProperty] private string _simBriefUser = "";
    [ObservableProperty] private bool _hasPlan;
    [ObservableProperty] private bool _noPlan = true;
    [ObservableProperty] private string _planOrigin = "";
    [ObservableProperty] private string _planDestination = "";
    [ObservableProperty] private string _planOriginName = "";
    [ObservableProperty] private string _planDestinationName = "";
    [ObservableProperty] private string _planFacts = "";
    [ObservableProperty] private string _planRoute = "";
    [ObservableProperty] private string _planBy = "";
    [ObservableProperty] private string _editedBy = "";
    [ObservableProperty] private bool _setupEditable;
    [ObservableProperty] private string _catalogHint = "";
    [ObservableProperty] private bool _importing;

    // ---- start / flight
    [ObservableProperty] private bool _canStart;
    [ObservableProperty] private string _startHint = "";
    [ObservableProperty] private bool _inLobbyPhase = true;
    [ObservableProperty] private bool _inFlightPhase;
    [ObservableProperty] private string _flightHeadline = "";
    [ObservableProperty] private string _myStage = "";
    [ObservableProperty] private string _sessionText = "";
    [ObservableProperty] private string _controlsText = "";
    [ObservableProperty] private string _lastAtc = "";
    [ObservableProperty] private string _worldText = "";
    [ObservableProperty] private string _worldDetail = "";
    private bool _warnedForeign, _warnedWeather;
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _playerName = _settings.PlayerName;
        _myInitial = Initial(_playerName);
        _simBriefUser = _settings.SimBriefUser ?? "";
        if (_settings.SimConnectDll is { Length: > 0 } dll) Environment.SetEnvironmentVariable("SKYLINK_SIMCONNECT_DLL", dll);

        _partyPort = AppSettings.Port("SKYLINK_PARTY_PORT", PartyNode.DefaultPort);
        int beaconPort = AppSettings.Port("SKYLINK_BEACON_PORT", Discovery.DefaultPort);
        int flightPort = AppSettings.Port("SKYLINK_FLIGHT_PORT", Net.LinkOptions.DefaultPort);
        var devPeers = (Environment.GetEnvironmentVariable("SKYLINK_DEV_PEERS") ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => IPEndPoint.Parse(p)).ToList();

        var catalog = DefinitionCatalog.Load(DefinitionCatalog.FindFolder());
        IFlightSim sim = Environment.GetEnvironmentVariable("SKYLINK_FAKE_SIM") == "1"
            ? new FakeSimBridge(catalog.Aircraft.First(d => d.Id == "asobo-c172sp-g1000"), _settings.PlayerId.GetHashCode())
            : new SimConnectBridge(catalog);
        _sim = new SimHost(sim);
        _sim.Log += m => Ui(() => AddLog(m));
        sim.AircraftCatalog += list => Ui(() => OnCatalog(list));
        sim.EventCaptured += (b, _) => Ui(() => OnAtc(b, mine: true));
        sim.EventReplayed += (b, _) => Ui(() => OnAtc(b, mine: false));
        _sim.Start();

        _discovery = new Discovery(_settings.PlayerId, beaconPort, devPeers) { CurrentBeacon = MakeBeacon };
        _discovery.Changed += RefreshPlayers;
        foreach (var peer in _settings.ManualPeers) TryAddPeer(peer, save: false);

        _launch = new LaunchCoordinator(_sim, flightPort);
        _launch.Log += AddLog;

        _myCatalog = AircraftCatalog.LoadCache();
        if (_myCatalog is not null)
        {
            CatalogText = $"{_myCatalog.Count} aircraft installed";
            if (_myCatalog.Any(a => a.Developer is null)) ResolveNames(_myCatalog); // cache from an older version
        }

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
        AddLog($"SkyLink ready. Looking for other pilots (UDP {beaconPort}).");
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    // ------------------------------------------------------------------ loop

    private long _lastSlow;
    private long _lastAddresses = long.MinValue / 2;

    /// <summary>Hyper-V/WSL, VMware and VirtualBox host adapters: never reachable by a co-pilot.</summary>
    private static bool IsVirtualAdapter(string name) =>
        new[] { "vEthernet", "VMware", "VirtualBox", "WSL", "Loopback" }.Any(v => name.Contains(v, StringComparison.OrdinalIgnoreCase));

    /// <summary>This PC's IPv4 addresses, for telling the co-pilot what to type.</summary>
    private void RefreshMyAddresses()
    {
        var list = Net.Link.LocalAddresses()
            .Select(a =>
            {
                int paren = a.IndexOf(" (", StringComparison.Ordinal);
                return paren < 0 ? new AddressItem(a, "") : new AddressItem(a[..paren], a[(paren + 2)..].TrimEnd(')'));
            })
            .Where(a => !a.Address.StartsWith("169.254.", StringComparison.Ordinal) && !IsVirtualAdapter(a.Adapter))
            .ToList();
        if (list.SequenceEqual(MyAddresses)) return;
        MyAddresses.Clear();
        foreach (var a in list) MyAddresses.Add(a);
    }

    private void Pump()
    {
        long now = _sim.Now;
        _discovery.Poll(now);
        _party?.Poll(now);
        if (_party is not null) _launch.Tick(_party);

        if (now - _lastAddresses >= 10_000)
        {
            _lastAddresses = now;
            RefreshMyAddresses();
        }
        if (now - _lastSlow >= 250)
        {
            _lastSlow = now;
            RefreshSim();
            RefreshFlight();
            // Confirmations fade away by themselves; errors stay until dismissed.
            if (Message is not null && !MessageIsError && DateTime.UtcNow - _messageAt > TimeSpan.FromSeconds(6)) Message = null;
        }
        if (_party?.State is { } s && (s.Version != _shownVersion || s.CatalogVersion != _shownCatalog)) RefreshParty(s);
    }

    private Beacon MakeBeacon()
    {
        var snap = _sim.Current;
        BeaconParty? bp = _party?.State is { } s
            ? new BeaconParty(s.PartyId, s.Name, _party.IsHost ? _party.Port : 0, s.Members.Count, s.Captain?.Name, s.FirstOfficer?.Name, s.Phase)
            : null;
        return new Beacon(Beacon.Version, _settings.PlayerId, PlayerName, snap.Connected, snap.Aircraft, bp);
    }

    private void RefreshSim()
    {
        var s = _sim.Current;
        SimConnected = s.Connected;
        (SimStatusTitle, SimStatusDetail) = !s.Connected
            ? ("MSFS not running", s.Status.StartsWith("waiting", StringComparison.OrdinalIgnoreCase) ? "" : s.Status)
            : s.InFlight ? ("In flight", s.Aircraft ?? "")
            : ("MSFS in menus", "");
        if (s.Connected && !_catalogRequested)
        {
            _catalogRequested = true;
            _sim.Post(() => _sim.Sim.RequestAircraftCatalog());
        }
        if (!s.Connected) _catalogRequested = false;
    }

    private void RefreshPlayers()
    {
        var items = _discovery.Players.Select(p =>
        {
            var b = p.Beacon;
            string status = b.SimRunning ? (b.SimAircraft is { } a ? $"Flying the {a}" : "MSFS open") : "MSFS closed";
            string? party = b.Party is { } bp
                ? bp.Phase switch
                {
                    PartyPhase.Lobby => bp.Members < 2 ? $"Hosting {bp.Name} · looking for a co-pilot" : $"In {bp.Name} · full",
                    _ => $"In {bp.Name} · flying",
                }
                : null;
            bool joinable = p.CanJoin && _party is null;
            return new PlayerItem(b.Id, b.Name, Initial(b.Name), status, party, joinable, p.Address, p);
        }).ToList();
        Players.Clear();
        foreach (var i in items) Players.Add(i);

        var saved = _saved.Select(kv =>
        {
            var who = _discovery.Players.FirstOrDefault(p => p.From.Address.Equals(kv.Value.Address));
            return new SavedPeerItem(kv.Key, who is null ? "No answer yet" : who.Beacon.Name, who is not null, kv.Value);
        }).ToList();
        SavedPeers.Clear();
        foreach (var sp in saved) SavedPeers.Add(sp);
    }

    private void RefreshParty(PartyState s)
    {
        _applying = true;
        try
        {
            _shownVersion = s.Version;
            string me = _settings.PlayerId;
            InParty = true;
            NotInParty = false;
            PartyName = s.Name;
            IsPartyHost = _party!.IsHost;
            PartyRole = _party.IsHost ? "you host this party" : _party.IsConnected ? $"hosted by {s.Member(s.HostId)?.Name}" : "reconnecting to the host...";

            CaptainInitial = s.Captain is { } ci ? Initial(ci.Name) : "";
            FirstOfficerInitial = s.FirstOfficer is { } fi ? Initial(fi.Name) : "";
            CaptainName = s.Captain is { } c ? Label(c, me) : "Empty seat";
            CaptainStatus = s.Captain is { } c2 ? MemberStatus(c2) : "waiting for a pilot";
            FirstOfficerName = s.FirstOfficer is { } f ? Label(f, me) : "Empty seat";
            FirstOfficerStatus = s.FirstOfficer is { } f2 ? MemberStatus(f2) : "waiting for a pilot";
            IAmCaptain = s.CaptainId == me;
            IAmFirstOfficer = s.FirstOfficerId == me;
            CanTakeCaptain = !IAmCaptain;
            CanTakeFirstOfficer = !IAmFirstOfficer;
            CanKick = _party.IsHost && s.Members.Count > 1;

            if (s.CatalogVersion != _shownCatalog || AircraftCount != _party.SharedCatalog.Count)
            {
                _shownCatalog = s.CatalogVersion;
                Aircraft.Clear();
                foreach (var a in AircraftItem.Grouped(_party.SharedCatalog)) Aircraft.Add(a);
            }
            CatalogHint = s.Members.Count < 2 ? "Showing your aircraft. Once your co-pilot joins, only what you both have installed is listed."
                : AircraftCount == 0 ? (s.Members.Any(m => m.CatalogCount == 0) ? "Waiting for your co-pilot's aircraft list. It's read when MSFS is open." : "You don't have any aircraft in common.")
                : $"{AircraftCount} aircraft you both have";

            var setup = s.Setup;
            SelectedAircraft = setup.AircraftTitle;
            FillLiveries(setup.AircraftTitle);
            SelectedLivery = setup.Livery;
            Callsign = setup.Callsign ?? "";
            AirlineCallsign = setup.AirlineCallsign ?? "";
            FlightNumber = setup.FlightNumber ?? "";
            HasPlan = setup.Plan is not null;
            NoPlan = !HasPlan;
            if (setup.Plan is { } p)
            {
                PlanOrigin = p.Origin;
                PlanDestination = p.Destination;
                PlanOriginName = Title(p.OriginName);
                PlanDestinationName = Title(p.DestinationName);
                var facts = new List<string>();
                if (p.CruiseAltitudeFt is { } alt) facts.Add($"FL{alt / 100:000}");
                if (p.DepartureRunway is { } rwy) facts.Add($"Runway {rwy}");
                if (p.BlockFuel is { } fuel) facts.Add($"{fuel:N0} {p.WeightUnit ?? ""} fuel".Replace("  ", " "));
                if (p.Passengers is { } pax) facts.Add($"{pax} passengers");
                if (p.AircraftIcao is { } icao) facts.Add(p.Registration is { } reg ? $"{icao} · {reg}" : icao);
                PlanFacts = string.Join("   ·   ", facts);
                PlanRoute = p.Route ?? "";
                PlanBy = $"Imported by {p.FetchedBy}";
            }
            EditedBy = setup.EditedBy is { } by ? $"Last change by {s.Member(by)?.Name ?? by}" : "";
            SetupEditable = s.Phase == PartyPhase.Lobby && s.SeatOf(me) != Seat.None;

            InLobbyPhase = s.Phase == PartyPhase.Lobby;
            InFlightPhase = !InLobbyPhase;
            FlightHeadline = s.Phase switch
            {
                PartyPhase.Launching => $"Starting {setup.AircraftTitle}{(setup.Plan is { } fp ? $" · {fp.Origin} → {fp.Destination}" : "")}...",
                PartyPhase.InFlight => $"In flight · {setup.Callsign ?? setup.AircraftTitle}{(setup.Plan is { } fp2 ? $" · {fp2.Origin} → {fp2.Destination}" : "")}",
                _ => "",
            };
            string? why = _party.CanStart();
            CanStart = why is null && IAmCaptain;
            StartHint = why ?? (IAmCaptain ? "Everything is ready." : "Waiting for the Captain to start the flight.");
        }
        finally
        {
            _applying = false;
        }
    }

    private static string Label(MemberInfo m, string me) => m.Id == me ? $"{m.Name} (you)" : m.Name;

    private static string Initial(string? name) =>
        string.IsNullOrWhiteSpace(name) ? "?" : char.ToUpperInvariant(name.Trim()[0]).ToString();

    /// <summary>"FRANKFURT/MAIN" reads better as "Frankfurt/Main".</summary>
    private static string Title(string? s) =>
        string.IsNullOrWhiteSpace(s) ? "" : System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(s.ToLowerInvariant());

    private static string MemberStatus(MemberInfo m)
    {
        if (!m.Connected) return "Offline · reconnecting";
        if (m.LaunchStage is { } st) return char.ToUpperInvariant(st[0]) + st[1..];
        string sim = m.SimRunning ? (m.SimAircraft is { } a ? $"In the {a}" : "MSFS open") : "MSFS closed";
        return m.CatalogCount > 0 ? $"{sim} · {m.CatalogCount} aircraft" : sim;
    }

    private void RefreshFlight()
    {
        string stage = LaunchCoordinator.Describe(_launch.Stage);
        MyStage = _launch.Stage == LaunchStage.Failed ? $"Launch failed: {_launch.Error}" : char.ToUpperInvariant(stage[0]) + stage[1..];
        if (_sim.Session is null) LastAtc = "";
        if (_sim.Session is { } st)
        {
            string sync = st.Incompatible is { } inc ? "Not compatible: " + inc : !st.Established ? "Connecting" : st.InitialSync ? "Syncing the cockpits" : "Cockpits in sync";
            SessionText = $"{sync}   ·   {st.LinkStats.RttMs} ms{(st.Rejections > 0 ? $"   ·   {st.Rejections} switch(es) the sim refused" : "")}";
            ControlsText = st.PilotFlying ? "You have the controls" : "Your co-pilot has the controls";
            (WorldText, WorldDetail) = DescribeWorld(st.World, st.PeerWorld, CoPilotName());
            WarnAboutWorld(st.World, st.PeerWorld);
        }
        else
        {
            SessionText = "";
            ControlsText = "";
            WorldText = WorldDetail = "";
            _warnedForeign = _warnedWeather = false;
        }
    }

    // ------------------------------------------------------------------ shared world

    /// <summary>The "shared world" row: what is mirrored, and what is wrong (own MSFS traffic still on, other weather).</summary>
    public static (string Headline, string Detail) DescribeWorld(WorldStatus w, WorldNotice? peer, string coPilot)
    {
        var parts = new List<string>();
        if ((w.Role == WorldRole.Mirror ? w.Remote : w.Local) is { } ws)
            parts.Add($"{ws.Zulu:HH:mm}Z · {(ws.Paused ? "paused" : $"{ws.SimRate:0.##}× sim rate")}");
        switch (w.Role)
        {
            case WorldRole.Source:
                if (peer is { ForeignTraffic: > 0 } p1)
                    parts.Add($"⚠ {coPilot} still has MSFS AI traffic on ({p1.ForeignTraffic} aircraft): ask them to turn it off");
                if (peer is { WeatherMismatch: true })
                    parts.Add($"⚠ {coPilot}'s weather differs from yours: both should fly live weather");
                return ($"Sharing your world: {w.TrafficSeen} AI aircraft, time and pause", string.Join("   ·   ", parts));
            case WorldRole.Mirror:
                string shown = w.TrafficSeen == 0 ? "no AI traffic"
                    : $"{w.TrafficMirrored} of {w.TrafficSeen} AI aircraft shown{(w.StandIns > 0 ? $" ({w.StandIns} with a stand-in model)" : "")}";
                if (w.ForeignTraffic > 0)
                    parts.Add($"⚠ Your MSFS still shows its own AI traffic ({w.ForeignTraffic} aircraft): turn AI traffic off in MSFS's traffic options");
                if (w.WeatherMismatch)
                    parts.Add($"⚠ Your weather differs from the Captain's ({w.WeatherDetail}): use live weather");
                return ($"The Captain's world: {shown}", string.Join("   ·   ", parts));
            default:
                return ("", "");
        }
    }

    private void WarnAboutWorld(WorldStatus w, WorldNotice? peer)
    {
        bool foreign = w.ForeignTraffic > 0 || peer is { ForeignTraffic: > 0 };
        bool weather = w.WeatherMismatch || peer is { WeatherMismatch: true };
        if (foreign && !_warnedForeign)
            Show(w.Role == WorldRole.Mirror
                ? "Your MSFS still has its own AI traffic. Turn AI traffic off in MSFS so you only see the Captain's."
                : $"{CoPilotName()}'s MSFS still has its own AI traffic. Ask them to turn it off.", error: true);
        if (weather && !_warnedWeather && !foreign)
            Show("The two sims have different weather. Both pilots should use live weather.", error: true);
        _warnedForeign = foreign;
        _warnedWeather = weather;
    }

    // ------------------------------------------------------------------ ATC

    /// <summary>"answered ATC with reply 2", "opened or closed the ATC window"... or null for non-ATC events.</summary>
    public static string? DescribeAtc(string id) => id switch
    {
        "atc_window" => "opened or closed the ATC window",
        _ when id.StartsWith("atc_reply_", StringComparison.Ordinal) => $"answered ATC with reply {id["atc_reply_".Length..]}",
        _ => null,
    };

    /// <summary>Both pilots' ATC answers are mirrored; tell this pilot when the other one answered.</summary>
    private void OnAtc(VarBinding b, bool mine)
    {
        if (DescribeAtc(b.Spec.Id) is not { } what) return;
        string who = mine ? "You" : CoPilotName();
        bool reply = b.Spec.Id.StartsWith("atc_reply_", StringComparison.Ordinal);
        if (reply) LastAtc = $"{who} {what} at {DateTime.Now:HH:mm}";
        if (!mine && reply) Show($"{who} {what}. Your ATC got the same answer.", error: false);
        else AddLog($"{who} {what}");
    }

    private string CoPilotName() =>
        _party?.State?.Members.FirstOrDefault(m => m.Id != _settings.PlayerId)?.Name ?? "Your co-pilot";

    // ------------------------------------------------------------------ catalog

    private void OnCatalog(List<(string Title, string Livery)> list)
    {
        _myCatalog = AircraftCatalog.Group(list);
        AircraftCatalog.SaveCache(_myCatalog);
        CatalogText = $"{_myCatalog.Count} aircraft installed";
        PublishCatalog();
        ResolveNames(_myCatalog);
    }

    private int _resolveRun;

    /// <summary>
    /// Look up names and developers in the installed MSFS packages (off the UI thread; reads files
    /// only), then cache and publish the named list. Until then the plain titles are shown.
    /// </summary>
    private void ResolveNames(List<CatalogAircraft> catalog)
    {
        int run = ++_resolveRun;
        Task.Run(() =>
        {
            AircraftNames names;
            List<CatalogAircraft> named;
            try
            {
                names = AircraftNames.FromThisPc();
                named = names.Apply(catalog);
            }
            catch (Exception e)
            {
                Ui(() => AddLog($"Could not read aircraft names from the MSFS packages: {e.Message}"));
                return;
            }
            Ui(() =>
            {
                if (run != _resolveRun) return; // a newer list arrived meanwhile
                _myCatalog = named;
                AircraftCatalog.SaveCache(named);
                int known = named.Count(a => a.Developer is not null);
                AddLog($"Aircraft names: {known} of {named.Count} matched to a developer ({names.PackageCount} packages, {names.ExactCount} exact titles read)");
                PublishCatalog();
            });
        });
    }

    private void PublishCatalog()
    {
        if (_party is { IsConnected: true } && _myCatalog is not null) _party.Send(new PublishCatalog(_myCatalog));
    }

    private void FillLiveries(string? title)
    {
        var keys = _party?.SharedCatalog.FirstOrDefault(a => a.Title == title)?.Liveries ?? [];
        var names = keys.Select(AircraftNames.LiveryDisplay).ToList();
        // Two liveries that read the same keep their MSFS names apart.
        var items = keys.Select((k, i) => new LiveryItem(k, names.Count(n => n.Equals(names[i], StringComparison.OrdinalIgnoreCase)) > 1 && names[i] != k ? $"{names[i]} ({k})" : names[i]))
            .OrderBy(l => l.Name != AircraftCatalog.DefaultLivery).ThenBy(l => l.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (Liveries.SequenceEqual(items)) return;
        Liveries.Clear();
        foreach (var l in items) Liveries.Add(l);
    }

    private int AircraftCount => Aircraft.Count(a => !a.IsHeader);

    partial void OnSelectedAircraftChanged(string? value)
    {
        if (_applying || string.IsNullOrEmpty(value) || _party is null) return;
        FillLiveries(value);
        string livery = Liveries.Any(l => l.Key == SelectedLivery) ? SelectedLivery! : Liveries.FirstOrDefault()?.Key ?? "";
        _party.Send(new SetAircraft(value, livery));
    }

    partial void OnSelectedLiveryChanged(string? value)
    {
        if (_applying || value is null || SelectedAircraft is null || _party is null) return;
        _party.Send(new SetAircraft(SelectedAircraft, value));
    }

    partial void OnPlayerNameChanged(string value)
    {
        _settings.PlayerName = value.Trim();
        _settings.Save();
        MyInitial = Initial(value);
    }

    // Callsign fields save themselves when you leave the box (no Apply button).
    partial void OnCallsignChanged(string value) => SendCallsign();
    partial void OnAirlineCallsignChanged(string value) => SendCallsign();
    partial void OnFlightNumberChanged(string value) => SendCallsign();

    private void SendCallsign()
    {
        if (_applying || _party?.State is not { } s) return;
        var cur = s.Setup;
        if ((cur.Callsign ?? "") == Callsign.Trim() && (cur.AirlineCallsign ?? "") == AirlineCallsign.Trim() && (cur.FlightNumber ?? "") == FlightNumber.Trim()) return;
        _party.Send(new SetCallsign(Callsign, AirlineCallsign, FlightNumber));
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void CreateParty()
    {
        try
        {
            SetParty(PartyNode.Host(new LocalPlayer(_settings.PlayerId, PlayerName), $"{PlayerName}'s flight", _partyPort));
            AddLog($"Party created. Your co-pilot can now join it from their list.");
        }
        catch (InvalidOperationException e)
        {
            Show(e.Message, error: true);
        }
    }

    [RelayCommand]
    private void Join(PlayerItem? item)
    {
        if (item?.Source.Beacon.Party is not { } bp || _party is not null) return;
        SetParty(PartyNode.Join(new LocalPlayer(_settings.PlayerId, PlayerName), item.Source.Address, bp.Port, bp.Id));
        AddLog($"joining {item.Name}'s party at {item.Source.Address}");
    }

    private void SetParty(PartyNode node)
    {
        _party = node;
        _shownVersion = _shownCatalog = -1;
        node.Log += AddLog;
        node.Error += m => Show(m, error: true);
        node.Ended += reason =>
        {
            Show($"Left the party: {reason}", error: false);
            LeaveParty();
        };
        node.Changed += () =>
        {
            if (node.IsConnected && !_publishedTo.Contains(node)) { _publishedTo.Add(node); PublishCatalog(); }
        };
        if (node.IsHost) { _publishedTo.Add(node); PublishCatalog(); }
        RefreshPlayers();
    }

    private readonly HashSet<PartyNode> _publishedTo = [];

    [RelayCommand]
    private void LeaveParty()
    {
        var p = _party;
        _party = null;
        p?.Dispose();
        _sim.StopSession();
        InParty = false;
        NotInParty = true;
        Aircraft.Clear();
        Liveries.Clear();
        RefreshPlayers();
    }

    [RelayCommand] private void TakeCaptain() => _party?.Send(new TakeSeat(Seat.Captain));
    [RelayCommand] private void TakeFirstOfficer() => _party?.Send(new TakeSeat(Seat.FirstOfficer));
    [RelayCommand] private void SwapSeats() => _party?.Send(new SwapSeats());

    [RelayCommand]
    private void KickOther()
    {
        if (_party?.State is not { } s) return;
        var other = s.Members.FirstOrDefault(m => m.Id != _settings.PlayerId);
        if (other is not null) _party.KickMember(other.Id);
    }

    [RelayCommand]
    private async Task ImportSimBrief()
    {
        if (_party is null) return;
        Importing = true;
        try
        {
            // Offline testing: SKYLINK_DEV_SIMBRIEF_FILE points at a saved OFP (JSON).
            var plan = Environment.GetEnvironmentVariable("SKYLINK_DEV_SIMBRIEF_FILE") is { Length: > 0 } devFile
                ? SimBrief.Parse(await System.IO.File.ReadAllTextAsync(devFile), PlayerName, DateTimeOffset.UtcNow).Plan
                : await SimBrief.FetchLatestAsync(Http, SimBriefUser, PlayerName);
            _settings.SimBriefUser = SimBriefUser.Trim();
            _settings.Save();
            _party.Send(new SetPlan(plan));
            Show($"Imported {plan.Origin} → {plan.Destination} from SimBrief.", error: false);
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException or InvalidOperationException or ArgumentException or System.Text.Json.JsonException)
        {
            Show("SimBrief: " + e.Message, error: true);
        }
        finally
        {
            Importing = false;
        }
    }

    [RelayCommand] private void ClearPlan() => _party?.Send(new SetPlan(null));

    [RelayCommand]
    private void StartFlight()
    {
        if (_party?.State is not { } s) return;
        var snap = _sim.Current;
        StartPosition? here = snap is { InFlight: true, Position: { } pos }
            ? new StartPosition(pos.LatDeg, pos.LonDeg, pos.AltFt, pos.HeadingDeg, pos.OnGround, "where the Captain's aircraft is")
            : null;
        var start = FlightFiles.ChooseStart(s.Setup, here);
        if (start is null)
        {
            Show("Import a SimBrief plan, or be in a flight in MSFS: without a plan the flight starts where your aircraft is.", error: true);
            return;
        }
        _party.Send(new StartFlight(start));
    }

    [RelayCommand] private void EndFlight() => _party?.Send(new EndFlight());
    [RelayCommand] private void RequestControls() => _sim.RequestControl();
    [RelayCommand] private void TakeControls() => _sim.TakeControl();
    [RelayCommand] private void GiveControls() => _sim.GiveControl();

    [RelayCommand]
    private void AddPeer()
    {
        if (TryAddPeer(ManualAddress, save: true)) ManualAddress = "";
    }

    [RelayCommand] private void RefreshAircraft() => _sim.Post(() => _sim.Sim.RequestAircraftCatalog());

    [RelayCommand] private void DismissMessage() => Message = null;

    [RelayCommand]
    private void CopyAddress(AddressItem? item)
    {
        if (item is null) return;
        try
        {
            Clipboard.SetText(item.Address);
            Show($"Copied {item.Address}. Send it to your co-pilot so they can add you.", error: false);
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Show("Couldn't use the clipboard right now; try again.", error: true);
        }
    }

    [RelayCommand]
    private void RemovePeer(SavedPeerItem? item)
    {
        if (item is null) return;
        _discovery.RemoveManual(item.Endpoint);
        _saved.Remove(item.Text);
        _settings.ManualPeers.Remove(item.Text);
        _settings.Save();
        RefreshPlayers();
    }

    private bool TryAddPeer(string text, bool save)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            _saved[text.Trim()] = _discovery.AddManual(text.Trim());
            RefreshPlayers();
            if (save && !_settings.ManualPeers.Contains(text.Trim()))
            {
                _settings.ManualPeers.Add(text.Trim());
                _settings.Save();
            }
            AddLog($"Looking for a pilot at {text.Trim()}");
            return true;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or System.Net.Sockets.SocketException)
        {
            Show($"Cannot use '{text}': {e.Message}", error: true);
            return false;
        }
    }

    private DateTime _messageAt;

    private void Show(string message, bool error)
    {
        _messageAt = DateTime.UtcNow;
        Message = message;
        MessageIsError = error;
        AddLog(message);
    }

    private void AddLog(string line)
    {
        Log.Insert(0, $"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 300) Log.RemoveAt(Log.Count - 1);
    }

    public void Dispose()
    {
        _timer.Stop();
        _party?.Dispose();
        _discovery.Dispose();
        _sim.Dispose(); // unfreezes the aircraft if it was following
    }
}
