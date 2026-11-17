using System.Collections.ObjectModel;
using System.Net;
using System.Net.Http;
using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using SkyLink.Definitions;
using SkyLink.Lobby;
using SkyLink.Session;
using SkyLink.Sim;

namespace SkyLink.App;

public sealed record PlayerItem(string Id, string Name, string Detail, string PartyText, bool CanJoin, DiscoveredPlayer Source);

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
    public ObservableCollection<string> Aircraft { get; } = [];
    public ObservableCollection<string> Liveries { get; } = [];
    public ObservableCollection<string> Log { get; } = [];

    // ---- you
    [ObservableProperty] private string _playerName;
    [ObservableProperty] private string _networkText = "";
    [ObservableProperty] private string _simText = "MSFS: not running";
    [ObservableProperty] private bool _simConnected;
    [ObservableProperty] private string _catalogText = "aircraft list: not read yet";
    [ObservableProperty] private string _manualAddress = "";

    // ---- party
    [ObservableProperty] private bool _inParty;
    [ObservableProperty] private bool _notInParty = true;
    [ObservableProperty] private string _partyName = "";
    [ObservableProperty] private string _partyRole = "";
    [ObservableProperty] private bool _isPartyHost;
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
    [ObservableProperty] private string _planSummary = "No flight plan. Import one from SimBrief, or the flight starts where the Captain's aircraft is.";
    [ObservableProperty] private bool _hasPlan;
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
    [ObservableProperty] private string? _message;
    [ObservableProperty] private bool _messageIsError;

    public MainViewModel()
    {
        _settings = AppSettings.Load();
        _playerName = _settings.PlayerName;
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
        _sim.Start();

        _discovery = new Discovery(_settings.PlayerId, beaconPort, devPeers) { CurrentBeacon = MakeBeacon };
        _discovery.Changed += RefreshPlayers;
        foreach (var peer in _settings.ManualPeers) TryAddPeer(peer, save: false);

        _launch = new LaunchCoordinator(_sim, flightPort);
        _launch.Log += AddLog;

        _myCatalog = AircraftCatalog.LoadCache();
        if (_myCatalog is not null) CatalogText = $"aircraft list: {_myCatalog.Count} aircraft (saved)";

        _timer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _timer.Tick += (_, _) => Pump();
        _timer.Start();
        AddLog($"SkyLink ready. You are '{PlayerName}'. Looking for players on UDP {beaconPort}.");
    }

    private static void Ui(Action a) => Application.Current?.Dispatcher.BeginInvoke(a);

    // ------------------------------------------------------------------ loop

    private long _lastSlow;

    private void Pump()
    {
        long now = _sim.Now;
        _discovery.Poll(now);
        _party?.Poll(now);
        if (_party is not null) _launch.Tick(_party);

        if (now - _lastSlow >= 250)
        {
            _lastSlow = now;
            RefreshSim();
            RefreshFlight();
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
        SimText = !s.Connected ? $"MSFS: {s.Status}"
            : s.InFlight ? $"MSFS: in flight, {s.Aircraft ?? "unknown aircraft"}"
            : "MSFS: running (menus)";
        NetworkText = _discovery.TailscaleAddress is { } ts ? $"Tailscale {ts}" : "Tailscale not found (LAN and manual addresses still work)";
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
            string sim = b.SimRunning ? (b.SimAircraft ?? "MSFS running") : "MSFS closed";
            string party = b.Party is { } bp ? $"{bp.Name} · {bp.Members}/2 · {bp.Phase}" : "not in a party";
            bool joinable = p.CanJoin && _party is null;
            return new PlayerItem(b.Id, b.Name, $"{p.Address} via {p.Via} · {sim}", party, joinable, p);
        }).ToList();
        Players.Clear();
        foreach (var i in items) Players.Add(i);
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

            CaptainName = s.Captain is { } c ? Label(c, me) : "empty";
            CaptainStatus = s.Captain is { } c2 ? MemberStatus(c2) : "waiting for a pilot";
            FirstOfficerName = s.FirstOfficer is { } f ? Label(f, me) : "empty";
            FirstOfficerStatus = s.FirstOfficer is { } f2 ? MemberStatus(f2) : "waiting for a pilot";
            IAmCaptain = s.CaptainId == me;
            IAmFirstOfficer = s.FirstOfficerId == me;
            CanTakeCaptain = !IAmCaptain;
            CanTakeFirstOfficer = !IAmFirstOfficer;
            CanKick = _party.IsHost && s.Members.Count > 1;

            if (s.CatalogVersion != _shownCatalog || Aircraft.Count != _party.SharedCatalog.Count)
            {
                _shownCatalog = s.CatalogVersion;
                Aircraft.Clear();
                foreach (var a in _party.SharedCatalog) Aircraft.Add(a.Title);
            }
            CatalogHint = s.Members.Count < 2 ? "Showing your aircraft. Once a First Officer joins, only aircraft and liveries installed on both PCs are listed."
                : Aircraft.Count == 0 ? (s.Members.Any(m => m.CatalogCount == 0) ? "Waiting for the other PC's aircraft list (it is read when MSFS runs)." : "No aircraft is installed on both PCs.")
                : $"{Aircraft.Count} aircraft installed on both PCs";

            var setup = s.Setup;
            SelectedAircraft = setup.AircraftTitle;
            FillLiveries(setup.AircraftTitle);
            SelectedLivery = setup.Livery;
            Callsign = setup.Callsign ?? "";
            AirlineCallsign = setup.AirlineCallsign ?? "";
            FlightNumber = setup.FlightNumber ?? "";
            HasPlan = setup.Plan is not null;
            PlanSummary = setup.Plan is { } p
                ? $"{p.Origin} → {p.Destination}   ·   FL{(p.CruiseAltitudeFt ?? 0) / 100:000}   ·   rwy {p.DepartureRunway ?? "?"}\n{p.Route}\n" +
                  $"{p.AircraftIcao} {p.Registration}   ·   fuel {p.BlockFuel:0} {p.WeightUnit}   ·   {p.Passengers} pax   ·   imported by {p.FetchedBy}"
                : "No flight plan. Import one from SimBrief, or the flight starts where the Captain's aircraft is.";
            EditedBy = setup.EditedBy is { } by ? $"last change by {s.Member(by)?.Name ?? by}" : "";
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

    private static string MemberStatus(MemberInfo m)
    {
        if (!m.Connected) return "offline";
        string sim = m.SimRunning ? (m.SimAircraft is { } a ? $"MSFS: {a}" : "MSFS running") : "MSFS closed";
        string list = m.CatalogCount > 0 ? $"{m.CatalogCount} aircraft" : "aircraft list pending";
        return m.LaunchStage is { } st ? $"{st} · {sim}" : $"{sim} · {list}";
    }

    private void RefreshFlight()
    {
        MyStage = _launch.Stage == LaunchStage.Failed ? $"Launch failed: {_launch.Error}" : LaunchCoordinator.Describe(_launch.Stage);
        if (_sim.Session is { } st)
        {
            string sync = st.Incompatible is { } inc ? "INCOMPATIBLE: " + inc : !st.Established ? "connecting" : st.InitialSync ? "initial sync" : "in sync";
            SessionText = $"Cockpit link: {st.Link} · rtt {st.LinkStats.RttMs} ms · {sync} · {st.PendingWrites} pending · {st.Rejections} rejected";
            ControlsText = st.PilotFlying ? "You are PILOT FLYING" : "You are PILOT MONITORING (the other pilot flies; your aircraft follows)";
        }
        else
        {
            SessionText = "";
            ControlsText = "";
        }
    }

    // ------------------------------------------------------------------ catalog

    private void OnCatalog(List<(string Title, string Livery)> list)
    {
        _myCatalog = AircraftCatalog.Group(list);
        AircraftCatalog.SaveCache(_myCatalog);
        CatalogText = $"aircraft list: {_myCatalog.Count} aircraft ({list.Count} liveries)";
        PublishCatalog();
    }

    private void PublishCatalog()
    {
        if (_party is { IsConnected: true } && _myCatalog is not null) _party.Send(new PublishCatalog(_myCatalog));
    }

    private void FillLiveries(string? title)
    {
        var liveries = _party?.SharedCatalog.FirstOrDefault(a => a.Title == title)?.Liveries ?? [];
        if (Liveries.SequenceEqual(liveries)) return;
        Liveries.Clear();
        foreach (var l in liveries) Liveries.Add(l);
    }

    partial void OnSelectedAircraftChanged(string? value)
    {
        if (_applying || value is null || _party is null) return;
        FillLiveries(value);
        string livery = Liveries.Contains(SelectedLivery ?? "") ? SelectedLivery! : Liveries.FirstOrDefault() ?? "";
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
    }

    // ------------------------------------------------------------------ commands

    [RelayCommand]
    private void CreateParty()
    {
        try
        {
            SetParty(PartyNode.Host(new LocalPlayer(_settings.PlayerId, PlayerName), $"{PlayerName}'s flight", _partyPort));
            AddLog($"party created; others on your network or tailnet can now join (UDP {_partyPort})");
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
    private void ApplyCallsign() => _party?.Send(new SetCallsign(Callsign, AirlineCallsign, FlightNumber));

    [RelayCommand]
    private async Task ImportSimBrief()
    {
        if (_party is null) return;
        Importing = true;
        try
        {
            var plan = await SimBrief.FetchLatestAsync(Http, SimBriefUser, PlayerName);
            _settings.SimBriefUser = SimBriefUser.Trim();
            _settings.Save();
            _party.Send(new SetPlan(plan));
            Show($"Imported {plan.Origin} → {plan.Destination} from SimBrief{(plan.PlnXml is null ? " (no MSFS .pln offered; SkyLink writes a direct route)" : "")}", error: false);
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

    private bool TryAddPeer(string text, bool save)
    {
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            _discovery.AddManual(text.Trim());
            if (save && !_settings.ManualPeers.Contains(text.Trim()))
            {
                _settings.ManualPeers.Add(text.Trim());
                _settings.Save();
            }
            AddLog($"looking for a SkyLink player at {text.Trim()}");
            return true;
        }
        catch (Exception e) when (e is ArgumentException or FormatException or System.Net.Sockets.SocketException)
        {
            Show($"Cannot use '{text}': {e.Message}", error: true);
            return false;
        }
    }

    private void Show(string message, bool error)
    {
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
