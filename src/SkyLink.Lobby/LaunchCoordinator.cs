using SkyLink.Net;
using SkyLink.Session;
using SkyLink.Sim;

namespace SkyLink.Lobby;

public enum LaunchStage { Idle, Preparing, StartingSim, LoadingFlight, SettingUp, Connecting, InCockpit, Failed }

/// <summary>
/// Turns "the Captain pressed Start" into both pilots sitting in the same aircraft, in sync, on
/// their own PCs: write the flight files, start MSFS if needed, load the flight, set the
/// callsign and route, then start the cockpit session (the Captain hosts it, the First Officer
/// joins). Each stage is reported to the party so both screens show both pilots' progress.
/// Driven by <see cref="Tick"/> from the UI timer; never blocks.
/// </summary>
public sealed class LaunchCoordinator
{
    public static readonly TimeSpan SimStartTimeout = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan LoadTimeout = TimeSpan.FromMinutes(6);

    /// <summary>
    /// SimConnect answers while MSFS is still booting, long before the main menu can load a flight,
    /// and a load sent too early is dropped without an error. So after SkyLink starts MSFS it waits
    /// this long, and it repeats a load request that MSFS showed no sign of starting.
    /// </summary>
    public TimeSpan LoadSettle { get; init; } = TimeSpan.FromSeconds(20);
    public TimeSpan LoadRetryAfter { get; init; } = TimeSpan.FromSeconds(20);
    public const int MaxLoadAttempts = 8;
    public static readonly TimeSpan ConnectTimeout = TimeSpan.FromMinutes(2);

    private readonly SimHost _host;
    private readonly Func<bool> _isSimRunning;
    private readonly Func<string> _launchSim;
    private readonly int _flightPort;
    private string? _session;
    private WrittenFlight? _flight;
    private DateTimeOffset _stageSince;
    private DateTimeOffset _loadRequestedAt;
    private bool _loadRequested;
    private int _loadAttempts;
    private int _activityAtRequest;
    private bool _launchRequested;
    private (bool, string?, bool, string?) _lastReported;
    private DateTimeOffset _lastReportAt;

    public LaunchStage Stage { get; private set; }
    public string? Error { get; private set; }
    public string? FlightFolder => _flight is null ? null : Path.GetDirectoryName(_flight.FltPath);
    public event Action<string>? Log;

    public LaunchCoordinator(SimHost host, int flightPort = LinkOptions.DefaultPort, Func<bool>? isSimRunning = null, Func<string>? launchSim = null)
    {
        _host = host;
        _flightPort = flightPort;
        _isSimRunning = isSimRunning ?? MsfsLauncher.IsRunning;
        _launchSim = launchSim ?? MsfsLauncher.Launch;
    }

    public static string Describe(LaunchStage s) => s switch
    {
        LaunchStage.Idle => "in the lobby",
        LaunchStage.Preparing => "preparing the flight",
        LaunchStage.StartingSim => "starting MSFS",
        LaunchStage.LoadingFlight => "loading the flight",
        LaunchStage.SettingUp => "setting callsign and route",
        LaunchStage.Connecting => "connecting the cockpits",
        LaunchStage.InCockpit => "in the cockpit",
        _ => "failed",
    };

    public void Tick(PartyNode party)
    {
        var s = party.State;
        var me = party.Me;
        if (s is null) return;
        var sim = _host.Current;

        if (s.Phase == PartyPhase.Lobby || s.SessionCode is null)
        {
            if (Stage != LaunchStage.Idle) Reset("the flight ended");
            Report(party, sim, null);
            return;
        }
        var seat = s.SeatOf(me.Id);
        if (seat == Seat.None) return;

        if (_session != s.SessionCode)
        {
            // A new launch.
            Reset(null);
            _session = s.SessionCode;
            Enter(LaunchStage.Preparing);
        }

        try
        {
            Advance(party, s, seat, sim);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            Fail(e.Message);
        }
        Report(party, sim, Stage == LaunchStage.Failed ? $"failed: {Error}" : Describe(Stage));
    }

    private void Advance(PartyNode party, PartyState s, Seat seat, SimSnapshot sim)
    {
        var now = DateTimeOffset.UtcNow;
        switch (Stage)
        {
            case LaunchStage.Preparing:
            {
                var start = s.LaunchStart ?? throw new InvalidOperationException("the Captain sent no start position");
                _flight = FlightFiles.Write(s.PartyId, s.Setup, start);
                Log?.Invoke($"flight files written to {Path.GetDirectoryName(_flight.FltPath)}");
                Enter(LaunchStage.StartingSim);
                break;
            }
            case LaunchStage.StartingSim:
                if (sim.Connected)
                {
                    Enter(LaunchStage.LoadingFlight);
                    break;
                }
                if (!_launchRequested && !_isSimRunning())
                {
                    _launchRequested = true;
                    Log?.Invoke(_launchSim());
                }
                if (now - _stageSince > SimStartTimeout) Fail("MSFS did not start (or SimConnect is not reachable)");
                break;

            case LaunchStage.LoadingFlight:
            {
                string flt = _flight!.FltPath;
                bool ours = IsSameFile(sim.LoadedFlight, flt);
                bool loadStarted = sim.LoadActivity != _activityAtRequest;
                if (_loadRequested && sim.InFlight && now - _loadRequestedAt > TimeSpan.FromSeconds(1)
                    && (ours || (loadStarted && sim.Aircraft == s.Setup.AircraftTitle)))
                {
                    Enter(LaunchStage.SettingUp);
                    break;
                }
                bool settled = !_launchRequested || now - _stageSince >= LoadSettle;
                bool retry = _loadRequested && !loadStarted && !ours && now - _loadRequestedAt >= LoadRetryAfter && _loadAttempts < MaxLoadAttempts;
                if (settled && (!_loadRequested || retry))
                {
                    _loadRequested = true;
                    _loadRequestedAt = now;
                    _activityAtRequest = sim.LoadActivity;
                    _loadAttempts++;
                    _host.Post(() => _host.Sim.LoadFlight(flt));
                    Log?.Invoke(_loadAttempts == 1
                        ? $"loading {s.Setup.AircraftTitle} ({s.Setup.Livery})"
                        : $"MSFS has not started loading yet: asking again (attempt {_loadAttempts})");
                    break;
                }
                if (now - _stageSince > LoadTimeout)
                    Fail(sim.LoadActivity == _activityAtRequest
                        ? "MSFS never started loading the flight (is it at the main menu?)"
                        : $"MSFS did not finish loading the flight (in flight: {sim.InFlight}, aircraft: {sim.Aircraft ?? "none"})");
                break;
            }
            case LaunchStage.SettingUp:
            {
                var setup = s.Setup;
                string? pln = _flight!.PlnPath;
                _host.Post(() =>
                {
                    _host.Sim.SetCallsign(setup.Callsign, setup.AirlineCallsign, setup.FlightNumber);
                    if (pln is not null) _host.Sim.LoadFlightPlan(pln);
                });
                StartCockpitSession(party, s, seat);
                Enter(LaunchStage.Connecting);
                break;
            }
            case LaunchStage.Connecting:
                if (_host.Session is { Established: true, InitialSync: false })
                    Enter(LaunchStage.InCockpit);
                else if (now - _stageSince > ConnectTimeout)
                    Fail(_host.Session?.Incompatible ?? $"the cockpits did not connect ({_host.Session?.Link.ToString() ?? "no link"})");
                break;
        }
    }

    private void StartCockpitSession(PartyNode party, PartyState s, Seat seat)
    {
        var options = new LinkOptions { SessionCode = s.SessionCode!, Port = _flightPort };
        Link link;
        if (seat == Seat.Captain)
        {
            link = Link.Host(options);
            Log?.Invoke($"hosting the cockpit session on UDP {_flightPort}");
        }
        else
        {
            string address = CaptainAddress(party, s) ?? throw new InvalidOperationException("don't know the Captain's address");
            link = Link.Join($"{address}:{_flightPort}", options);
            Log?.Invoke($"joining the Captain's cockpit at {address}:{_flightPort}");
        }
        _host.StartSession(link);
    }

    /// <summary>The Captain's network address as seen from this PC.</summary>
    public static string? CaptainAddress(PartyNode party, PartyState s)
    {
        var captain = s.Captain;
        if (captain is null) return null;
        if (captain.Id == s.HostId) return party.HostAddress; // we joined the Captain's party
        return string.IsNullOrEmpty(captain.Address) ? null : captain.Address;
    }

    private void Report(PartyNode party, SimSnapshot sim, string? stage)
    {
        var current = (sim.Connected, sim.Aircraft, Stage == LaunchStage.InCockpit, stage);
        var now = DateTimeOffset.UtcNow;
        if (current == _lastReported && now - _lastReportAt < TimeSpan.FromSeconds(5)) return;
        _lastReported = current;
        _lastReportAt = now;
        if (party.IsConnected) party.Send(new UpdateStatus(sim.Connected, sim.Aircraft, Stage == LaunchStage.InCockpit, stage));
    }

    private static bool IsSameFile(string? reported, string path)
    {
        if (string.IsNullOrWhiteSpace(reported)) return false;
        try { return string.Equals(Path.GetFullPath(reported), Path.GetFullPath(path), StringComparison.OrdinalIgnoreCase); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return false; }
    }

    private void Enter(LaunchStage stage)
    {
        Stage = stage;
        _stageSince = DateTimeOffset.UtcNow;
        if (stage != LaunchStage.Failed) Log?.Invoke(Describe(stage));
    }

    private void Fail(string why)
    {
        Error = why;
        Enter(LaunchStage.Failed);
        Log?.Invoke("launch failed: " + why);
    }

    private void Reset(string? why)
    {
        if (Stage is not LaunchStage.Idle) _host.StopSession();
        if (why is not null && Stage != LaunchStage.Idle) Log?.Invoke(why);
        Stage = LaunchStage.Idle;
        Error = null;
        _session = null;
        _flight = null;
        _loadRequested = false;
        _loadAttempts = 0;
        _launchRequested = false;
    }
}
