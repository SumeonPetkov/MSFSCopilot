using SkyLink.Core;
using SkyLink.Definitions;
using SkyLink.Net;
using SkyLink.Session;
using SkyLink.Sim;

namespace SkyLink.Cli;

public static class Program
{
    private const string Usage = """
        SkyLink — shared cockpit for MSFS 2024

        usage:
          skylink code                                  print a new session code
          skylink host [--port N] --code CODE [options] host a session (you start as pilot flying)
          skylink join HOST[:PORT] --code CODE [options] join a session
          skylink probe [--seconds N] [--defs DIR]      check what the loaded aircraft exposes
          skylink lint [DIR]                            validate aircraft definitions
          skylink install-bridge [--dry-run]            install the in-sim bridge module into MSFS
          skylink watch [--seconds N]                   print every cockpit event SkyLink catches (e.g. ATC replies)
          skylink traffic [--seconds N] [--radius KM]   list the AI traffic, time and weather SkyLink would share (read-only)

        options:
          --defs DIR         definitions folder (default: the one shipped with SkyLink)
          --fake-sim         use a simulated cockpit instead of MSFS (for testing the link)
          --aircraft ID      definition to use with --fake-sim (default asobo-c172sp-g1000)
          --fake-pilot       with --fake-sim: randomly flip switches and press buttons
          --loss PCT         simulate packet loss (testing)
          --latency MS       simulate extra latency (testing)
        """;

    public static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help")
        {
            Console.WriteLine(Usage);
            return 0;
        }
        try
        {
            var a = Args.Parse(args.Skip(1));
            return args[0] switch
            {
                "code" => PrintCode(),
                "lint" => Lint(a.Positional.FirstOrDefault() ?? a.Get("defs") ?? FindDefinitions()),
                "probe" => Probe(a),
                "install-bridge" => InstallBridge(a),
                "watch" => Watch(a),
                "traffic" => Traffic(a),
                "host" => RunSession(a, host: true),
                "join" => RunSession(a, host: false),
                _ => Fail($"unknown command '{args[0]}'\n\n{Usage}"),
            };
        }
        catch (ArgumentException e)
        {
            return Fail(e.Message);
        }
    }

    private static int PrintCode()
    {
        Console.WriteLine(Link.NewSessionCode());
        return 0;
    }

    private static int Lint(string dir)
    {
        var catalog = DefinitionCatalog.Load(dir);
        foreach (var issue in catalog.Issues) Console.WriteLine(issue);
        foreach (var d in catalog.Aircraft)
            Console.WriteLine($"ok: {d.Id} v{d.Version} ({d.Vars.Count} variables in {d.Groups.Count} groups, hash {d.Schema.Hash:X16}){(d.IsFallback ? " [fallback]" : "")}");
        int errors = catalog.Issues.Count(i => i.IsError);
        Console.WriteLine($"{catalog.Aircraft.Count} definitions, {errors} errors, {catalog.Issues.Count - errors} warnings");
        return errors == 0 ? 0 : 1;
    }

    private static int Probe(Args a)
    {
        var catalog = LoadCatalog(a);
        int seconds = a.GetInt("seconds") ?? 10;
        using var bridge = new SimConnectBridge(catalog);
        bridge.Log += m => Console.WriteLine("  " + m);
        Console.WriteLine($"SimConnect.dll: {bridge.SimConnectDll ?? "NOT FOUND (searched: " + string.Join("; ", SimConnectNativeCandidates()) + ")"}");
        var end = DateTime.UtcNow.AddSeconds(seconds);
        long start = Environment.TickCount64;
        while (DateTime.UtcNow < end)
        {
            bridge.Pump(Environment.TickCount64 - start + 1);
            bridge.DataAvailable.WaitOne(20);
        }
        Console.WriteLine();
        Console.WriteLine($"status:     {bridge.Status}");
        Console.WriteLine($"title:      {bridge.AircraftTitle}");
        Console.WriteLine($"ATC model:  {bridge.AircraftAtcModel}");
        Console.WriteLine($"path:       {bridge.AircraftPath}");
        if (bridge.Definition is not { } def) return Fail("no aircraft definition active");
        Console.WriteLine($"definition: {def.Id} v{def.Version} ({bridge.Match!.Reason})");
        Console.WriteLine($"bridge:     {bridge.ModuleStatus}{InstalledBridgeNote()}");
        if (bridge.UnconfirmedLVars > 0)
            Console.WriteLine($"            {bridge.UnconfirmedLVars} L-var(s) not found in the sim yet (still checking; run probe longer, e.g. --seconds 70)");
        Console.WriteLine();
        int missing = 0;
        for (int v = 0; v < def.Vars.Count; v++)
        {
            var b = def.Vars[v];
            if (b.Spec.Class == SyncClass.Event)
            {
                Console.WriteLine($"  {b.Spec.Id,-24} event    capture {b.Capture!.Kind}:{b.Capture.Name}");
                continue;
            }
            bool ok = bridge.TryGetValue(v, out double value);
            if (!ok) missing++;
            Console.WriteLine($"  {b.Spec.Id,-24} {b.Spec.Class.ToString().ToLowerInvariant(),-8} {(ok ? value.ToString("G10") : "NO VALUE"),-14} {b.Read}");
        }
        Console.WriteLine();
        foreach (var p in bridge.Problems) Console.WriteLine("PROBLEM: " + p);
        Console.WriteLine($"{def.Vars.Count} variables, {missing} without a value, {bridge.Problems.Count} problems");
        return bridge.Problems.Count == 0 && missing == 0 ? 0 : 2;
    }

    private static IEnumerable<string> SimConnectNativeCandidates() =>
        typeof(SimConnectBridge).Assembly.GetType("SkyLink.Sim.SimConnectNative")!
            .GetMethod("Candidates")!.Invoke(null, null) as IEnumerable<string> ?? [];

    private static int RunSession(Args a, bool host)
    {
        string code = a.Get("code") ?? throw new ArgumentException("--code is required (make one with: skylink code)");
        var options = new LinkOptions
        {
            SessionCode = code,
            Port = a.GetInt("port") ?? LinkOptions.DefaultPort,
            SimulateLossPercent = a.GetInt("loss") ?? 0,
            SimulateLatencyMinMs = a.GetInt("latency") ?? 0,
            SimulateLatencyMaxMs = a.GetInt("latency") ?? 0,
        };
        var catalog = LoadCatalog(a);
        ISimBridge sim;
        if (a.Has("fake-sim"))
        {
            string id = a.Get("aircraft") ?? "asobo-c172sp-g1000";
            var def = catalog.Aircraft.FirstOrDefault(d => d.Id == id) ?? throw new ArgumentException($"no definition '{id}'");
            sim = new FakeSimBridge(def, Environment.TickCount) { FakePilot = a.Has("fake-pilot") };
        }
        else
        {
            sim = new SimConnectBridge(catalog);
        }

        Link link;
        if (host)
        {
            link = Link.Host(options);
            Console.WriteLine($"Hosting on UDP port {options.Port}. Session code: {Link.NormalizeCode(code)}");
            Console.WriteLine("Give the other pilot one of these addresses (or your public IP with the port forwarded):");
            foreach (var addr in Link.LocalAddresses()) Console.WriteLine("  " + addr);
        }
        else
        {
            string target = a.Positional.FirstOrDefault() ?? throw new ArgumentException("join needs the host address, e.g. skylink join 100.64.1.2 --code ABC123");
            link = Link.Join(target, options);
            Console.WriteLine($"Joining {target}...");
        }

        using var runner = new SessionRunner(sim, link);
        runner.Log += m => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {m}");
        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        var loop = new Thread(() => runner.Run(cts.Token)) { IsBackground = true, Name = "SkyLink session" };
        loop.Start();

        Console.WriteLine("Keys: [r] request controls  [t] take controls  [g] give controls  [s] status  [q] quit");
        string last = "";
        while (!cts.IsCancellationRequested)
        {
            if (!Console.IsInputRedirected && Console.KeyAvailable)
            {
                switch (char.ToLowerInvariant(Console.ReadKey(true).KeyChar))
                {
                    case 'r': runner.RequestControl(); break;
                    case 't': runner.TakeControl(); break;
                    case 'g': runner.GrantControl(); break;
                    case 's': last = ""; break;
                    case 'q': cts.Cancel(); break;
                }
            }
            string line = FormatStatus(runner.Status);
            if (line != last)
            {
                Console.WriteLine($"{DateTime.Now:HH:mm:ss} [status] {line}");
                last = line;
            }
            Thread.Sleep(500);
        }
        loop.Join(2000);
        return 0;
    }

    private static string FormatStatus(SessionStatus s)
    {
        string sync = s.Incompatible is not null ? "INCOMPATIBLE: " + s.Incompatible
            : !s.Established ? "not synced"
            : s.InitialSync ? "initial sync..."
            : "in sync";
        string link = s.Link == LinkState.Connected ? $"link up (rtt {s.LinkStats.RttMs} ms)" : s.Link.ToString().ToLowerInvariant();
        string problems = (s.Rejections > 0 ? $" | {s.Rejections} REJECTED" : "") + (s.DerivedMismatchGroups > 0 ? $" | {s.DerivedMismatchGroups} groups diverging" : "")
                          + (s.LinkStats.BadPackets > 0 ? $" | {s.LinkStats.BadPackets} bad packets" : "");
        return $"{link} | {s.Aircraft ?? s.Sim}{(s.GenericFallback ? " (GENERIC)" : "")} | {(s.PilotFlying ? "PILOT FLYING" : "monitoring")} | {sync} | pending {s.PendingWrites} unacked {s.UnackedEvents}{problems}";
    }

    private static DefinitionCatalog LoadCatalog(Args a)
    {
        string dir = a.Get("defs") ?? FindDefinitions();
        var catalog = DefinitionCatalog.Load(dir);
        foreach (var i in catalog.Issues.Where(i => i.IsError)) Console.WriteLine(i);
        if (catalog.Aircraft.Count == 0) throw new ArgumentException($"no usable aircraft definitions in {dir}");
        return catalog;
    }

    /// <summary>
    /// Live check: prints each synced event as MSFS reports it, e.g. to confirm that clicking an
    /// ATC reply (not just pressing its number key) is seen and would be mirrored.
    /// </summary>
    private static int Watch(Args a)
    {
        var catalog = LoadCatalog(a);
        int seconds = a.GetInt("seconds") ?? 120;
        using var bridge = new SimConnectBridge(catalog);
        int seen = 0;
        bridge.Log += m => Console.WriteLine("  " + m);
        bridge.EventCaptured += (b, p) =>
        {
            seen++;
            Console.WriteLine($"{DateTime.Now:HH:mm:ss}  {b.Spec.Id,-18} {b.Capture!.Kind}:{b.Capture.Name}{(p != 0 ? $" ({p})" : "")}");
        };
        Console.WriteLine($"Watching for {seconds} s. Press buttons in the cockpit, or open the ATC window and pick replies (click them, then try the number keys).");
        var end = DateTime.UtcNow.AddSeconds(seconds);
        long start = Environment.TickCount64;
        while (DateTime.UtcNow < end)
        {
            bridge.Pump(Environment.TickCount64 - start + 1);
            bridge.DataAvailable.WaitOne(20);
        }
        Console.WriteLine($"{seen} event(s) caught.");
        foreach (var p in bridge.Problems) Console.WriteLine("PROBLEM: " + p);
        return 0;
    }

    /// <summary>Read-only: scan the AI traffic as the Captain's SkyLink does, and print it.</summary>
    private static int Traffic(Args a)
    {
        var catalog = LoadCatalog(a);
        int seconds = a.GetInt("seconds") ?? 10;
        using var bridge = new SimConnectBridge(catalog);
        if (a.GetInt("radius") is { } km) bridge.TrafficRadiusMeters = (uint)Math.Max(1, km) * 1000;
        bridge.Log += m => Console.WriteLine("  " + m);
        var identities = new Dictionary<uint, TrafficIdentity>();
        var latest = new Dictionary<TrafficKind, IReadOnlyList<(uint Id, TrafficSample Sample)>>();
        int scans = 0;
        bridge.TrafficScanned += (k, list) =>
        {
            latest[k] = list;
            scans++;
        };
        bridge.TrafficIdentified += (id, identity) => identities[id] = identity;
        long start = Environment.TickCount64;
        bridge.Execute(new SetWorldRole(WorldRole.Source), 1); // scanning only: nothing is written to the sim
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            bridge.Pump(Environment.TickCount64 - start + 1);
            bridge.DataAvailable.WaitOne(20);
        }
        var me = bridge.LatestFlight;
        var all = latest.Values.SelectMany(l => l).ToList();
        Console.WriteLine();
        Console.WriteLine($"user aircraft: object {bridge.UserObjectId}, {(me is { } u ? $"{u.LatDeg:F5} {u.LonDeg:F5} {u.AltFt:F0} ft" : "no position")}");
        Console.WriteLine($"{scans} scans, {all.Count} AI aircraft within {bridge.TrafficRadiusMeters / 1000} km, {identities.Count} identified");
        foreach (var (id, s) in all.OrderBy(o => me is { } m ? FlightFollower.DistanceM(m, o.Sample.Pose) : o.Id))
        {
            var p = s.Pose;
            identities.TryGetValue(id, out var who);
            string dist = me is { } m ? $"{FlightFollower.DistanceM(m, p) / 1852:F1} nm" : "";
            double gs = Math.Sqrt(p.VWorldEastFps * p.VWorldEastFps + p.VWorldNorthFps * p.VWorldNorthFps) * 0.592484;
            Console.WriteLine($"  {id,6} {dist,9} {p.AltFt,7:F0} ft {gs,4:F0} kt {(p.OnGround ? "ground" : "air   ")} lights {s.Lights:X3} gear {(s.GearDown ? "down" : "up  ")}  " +
                              $"{who?.AtcId,-8} {who?.Airline} {who?.FlightNumber}  '{who?.Title}' livery '{who?.Livery}' model '{who?.AtcModel}'");
        }
        if (bridge.World.Local is { } w)
            Console.WriteLine($"world: {w.Zulu:yyyy-MM-dd HH:mm:ss}Z, rate {w.SimRate}, {(bridge.IsPaused ? "PAUSED" : "running")}, {w.SeaLevelPressureMb:F1} hPa, wind {w.WindDirDeg:F0}°/{w.WindKts:F0} kt, {w.TempC:F1} °C");
        foreach (var p in bridge.Problems) Console.WriteLine("PROBLEM: " + p);
        return 0;
    }

    private static int InstallBridge(Args a)
    {
        string source = a.Get("package") ?? FindUp(Path.Combine("package", BridgeInstaller.PackageFolderName), "manifest.json")
                        ?? throw new ArgumentException("package/skylink-bridge not found (use --package DIR)");
        string? community = a.Get("community");
        if (community is null)
        {
            community = BridgeInstaller.FindCommunityFolder(out var cfg)
                        ?? throw new ArgumentException("could not find MSFS 2024's UserCfg.opt; pass --community DIR");
            Console.WriteLine($"Community folder: {community} (from {cfg})");
        }
        try
        {
            Console.WriteLine(BridgeInstaller.Install(source, community, a.Has("dry-run")));
            return 0;
        }
        catch (Exception e) when (e is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            return Fail(e.Message);
        }
    }

    private static string InstalledBridgeNote()
    {
        if (BridgeInstaller.FindCommunityFolder(out _) is not { } community) return "";
        var installed = BridgeInstaller.ReadPackage(Path.Combine(community, BridgeInstaller.PackageFolderName));
        return installed is null ? " (package not installed: skylink install-bridge)" : $" (package {installed.Version} installed)";
    }

    /// <summary>A folder (containing <paramref name="marker"/>) next to the executable or above the current directory.</summary>
    private static string? FindUp(string relative, string marker)
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, relative);
                if (File.Exists(Path.Combine(candidate, marker))) return candidate;
            }
        return null;
    }

    /// <summary>The definitions folder next to the executable, or the nearest one above the current directory.</summary>
    public static string FindDefinitions()
    {
        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            for (var d = new DirectoryInfo(start); d is not null; d = d.Parent)
            {
                string candidate = Path.Combine(d.FullName, "definitions");
                if (Directory.Exists(Path.Combine(candidate, "_base"))) return candidate;
            }
        return Path.Combine(AppContext.BaseDirectory, "definitions");
    }

    private static int Fail(string message)
    {
        Console.Error.WriteLine(message);
        return 1;
    }

    private sealed class Args
    {
        private readonly Dictionary<string, string?> _named = new(StringComparer.OrdinalIgnoreCase);
        public List<string> Positional { get; } = [];

        public static Args Parse(IEnumerable<string> args)
        {
            var a = new Args();
            var list = args.ToList();
            for (int i = 0; i < list.Count; i++)
            {
                if (!list[i].StartsWith("--"))
                {
                    a.Positional.Add(list[i]);
                    continue;
                }
                string key = list[i][2..];
                bool hasValue = i + 1 < list.Count && !list[i + 1].StartsWith("--");
                a._named[key] = hasValue && key is not ("fake-sim" or "fake-pilot" or "dry-run") ? list[++i] : null;
            }
            return a;
        }

        public bool Has(string key) => _named.ContainsKey(key);
        public string? Get(string key) => _named.GetValueOrDefault(key);

        public int? GetInt(string key) => Get(key) is { } v
            ? int.TryParse(v, out int i) ? i : throw new ArgumentException($"--{key} must be a number")
            : null;
    }
}
