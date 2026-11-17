using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SkyLink.Sim;

namespace SkyLink.Lobby;

/// <summary>One installed MSFS package that contains aircraft, as read from its manifest.json.</summary>
public sealed record AircraftPackage(string Name, string Title, string? Creator, string? Manufacturer, string? ContentType = null);

/// <summary>What an <c>aircraft.cfg</c> says about one aircraft title (exact, not guessed).</summary>
public sealed record AircraftCfgEntry(string Title, string? UiType, string? UiVariation, string? Manufacturer, string? CreatedBy, AircraftPackage? Package);

/// <summary>
/// Friendly names and developers for the aircraft MSFS lists. SimConnect only gives the aircraft
/// title and livery name, so the rest comes from the installed packages (read only, never changed):
/// an unpacked <c>aircraft.cfg</c> (Community add-ons) maps a title exactly; official/streamed
/// aircraft are packed, so their title is matched against the package manifests MSFS keeps readable
/// (<c>minimalcache</c> next to the game, Official package folders). The title stays the key.
/// </summary>
public sealed class AircraftNames
{
    private readonly Dictionary<string, AircraftCfgEntry> _exact = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(AircraftPackage Package, string[] Keys, Dictionary<string, int> Tokens, Dictionary<string, int> MakerTokens)> _packages = [];

    public int PackageCount => _packages.Count;
    public int ExactCount => _exact.Count;

    public AircraftNames(IEnumerable<AircraftPackage> packages, IEnumerable<AircraftCfgEntry> cfgEntries)
    {
        foreach (var e in cfgEntries) _exact.TryAdd(e.Title.Trim(), e);
        foreach (var p in packages.DistinctBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            // AI-only models and livery packs are not aircraft anyone picks.
            if (IsPassive(p.Name) || IsPassive(p.Title) || p.Name.Contains("-livery", StringComparison.OrdinalIgnoreCase)) continue;
            string shortName = ShortPackageName(p.Name);
            var keys = new[] { Squash(StripNoise(p.Title)), Squash(shortName) }.Where(k => k.Length >= 3).Distinct().ToArray();
            _packages.Add((p, keys, Tokens(p.Title + " " + shortName), Tokens(p.Manufacturer ?? "")));
        }
    }

    public static AircraftNames Empty { get; } = new([], []);

    /// <summary>Scan this PC's MSFS 2024 install and package folders (best effort, read only).</summary>
    public static AircraftNames FromThisPc()
    {
        var roots = PackageRoots().ToList();
        return Scan(roots);
    }

    /// <summary>Read every package under each root (a folder whose subfolders are packages).</summary>
    public static AircraftNames Scan(IEnumerable<string> roots)
    {
        var packages = new List<AircraftPackage>();
        var cfgs = new List<AircraftCfgEntry>();
        foreach (var root in roots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string[] dirs;
            try { dirs = Directory.Exists(root) ? Directory.GetDirectories(root) : []; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { continue; }
            foreach (var dir in dirs)
            {
                var pkg = ReadManifest(dir);
                if (pkg is not null && IsAircraftPackage(pkg)) packages.Add(pkg);
                cfgs.AddRange(ReadAircraftCfgs(dir, pkg));
            }
        }
        return new AircraftNames(packages, cfgs);
    }

    /// <summary>Fill in display name / developer / manufacturer for each aircraft that lacks them.</summary>
    public List<CatalogAircraft> Apply(IEnumerable<CatalogAircraft> catalog) =>
        catalog.Select(a =>
        {
            var info = Resolve(a.Title);
            return a with
            {
                DisplayName = a.DisplayName ?? info.DisplayName,
                Developer = a.Developer ?? info.Developer,
                Manufacturer = a.Manufacturer ?? info.Manufacturer,
            };
        }).ToList();

    public (string DisplayName, string? Developer, string? Manufacturer) Resolve(string title)
    {
        title = title.Trim();
        if (_exact.TryGetValue(title, out var e))
        {
            string name = LooksLikeCode(title) && e.UiType is { Length: > 0 } t
                ? (e.UiVariation is { Length: > 0 } v && !t.Contains(v, StringComparison.OrdinalIgnoreCase) ? $"{t} {v}" : t)
                : CleanTitle(title);
            return (name, NormalizeDeveloper(e.CreatedBy ?? e.Package?.Creator), NormalizeManufacturer(e.Manufacturer ?? e.Package?.Manufacturer));
        }
        var best = BestPackage(title);
        return (CleanTitle(title), NormalizeDeveloper(best?.Creator), NormalizeManufacturer(best?.Manufacturer));
    }

    /// <summary>
    /// The package a title most likely belongs to: a package's own name contained in the title
    /// scores its length, shared words score their length (the maker's words half). Too weak = none.
    /// </summary>
    public AircraftPackage? BestPackage(string title)
    {
        string squashed = Squash(CleanTitle(title));
        var titleTokens = Tokens(CleanTitle(title));
        AircraftPackage? best = null;
        double bestScore = 0;
        bool tie = false;
        foreach (var (p, keys, tokens, maker) in _packages)
        {
            double score = keys.Where(k => squashed.Contains(k, StringComparison.Ordinal)).Select(k => (double)k.Length).DefaultIfEmpty(0).Max();
            foreach (var (tok, weight) in titleTokens)
            {
                if (tokens.ContainsKey(tok)) score += weight;
                else if (maker.ContainsKey(tok)) score += weight / 2.0;
            }
            if (score > bestScore + 1e-9) { best = p; bestScore = score; tie = false; }
            else if (Math.Abs(score - bestScore) < 1e-9 && best is not null && !SameDeveloper(best, p)) tie = true;
        }
        return bestScore >= 3 && !tie ? best : null;
    }

    private static bool SameDeveloper(AircraftPackage a, AircraftPackage b) =>
        string.Equals(NormalizeDeveloper(a.Creator), NormalizeDeveloper(b.Creator), StringComparison.OrdinalIgnoreCase);

    // ------------------------------------------------------------------ presentation helpers

    /// <summary>AI-only ("passive") aircraft MSFS lists but nobody can fly.</summary>
    public static bool IsPassive(string title) => title.Contains("passive", StringComparison.OrdinalIgnoreCase);

    /// <summary>"Jetson One [Preset Default]" → "Jetson One"; "INI_A20N_DEFAULT" → "INI A20N DEFAULT".</summary>
    public static string CleanTitle(string title)
    {
        string t = Regex.Replace(title.Trim(), @"\s*\[[^\]]*\]\s*$", "");
        if (LooksLikeCode(t)) t = t.Replace('_', ' ');
        t = Regex.Replace(t, @"\s+", " ").Trim();
        return t.Length > 0 ? t : title.Trim();
    }

    /// <summary>
    /// Livery names as shown: "" → "Default", "A320_OFFICIAL" → "A320 Official", "WIZZAIR" → "Wizzair",
    /// "TT:AIRCRAFT.LIVERY.KENMORE" (an untranslated text key) → "Kenmore".
    /// </summary>
    public static string LiveryDisplay(string livery)
    {
        string l = livery.Trim();
        if (l.StartsWith("TT:", StringComparison.OrdinalIgnoreCase) && l.LastIndexOf('.') is var dot and > 2 && dot < l.Length - 1) l = l[(dot + 1)..];
        if (l.Length == 0 || l == AircraftCatalog.DefaultLivery) return AircraftCatalog.DefaultLivery;
        if (l.Contains('_')) l = Regex.Replace(l.Replace('_', ' '), @"\s+", " ").Trim();
        if (l.Length == 0) return livery;
        // Shout-case words (longer than an acronym, no digits) read better in title case.
        return string.Join(' ', l.Split(' ').Select(w =>
            w.Length > 3 && w.All(c => char.IsLetter(c) && char.IsUpper(c)) ? w[0] + w[1..].ToLowerInvariant() : w));
    }

    private static readonly Dictionary<string, string> KnownDevelopers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["inibuilds"] = "iniBuilds",
        ["bluemesh"] = "BlueMesh",
        ["microsoft"] = "Microsoft",
        ["asobo"] = "Asobo",
        ["asobo studio"] = "Asobo",
        ["working title"] = "Working Title",
        ["working title simulations"] = "Working Title",
        ["flightfx"] = "FlightFX",
        ["pmdg"] = "PMDG",
        ["fenix"] = "Fenix",
        ["fenix simulations"] = "Fenix",
        ["fbw"] = "FlyByWire",
        ["flybywire"] = "FlyByWire",
        ["flybywire simulations"] = "FlyByWire",
        ["headwind"] = "Headwind",
        ["got friends"] = "Got Friends",
    };

    /// <summary>"Microsoft / iniBuilds" → "iniBuilds", "Asobo Studio" → "Asobo", "microsoft" → "Microsoft".</summary>
    public static string? NormalizeDeveloper(string? creator)
    {
        if (string.IsNullOrWhiteSpace(creator)) return null;
        var parts = creator.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => Regex.Replace(p, @"\s+", " "))
            .Where(p => p.Length > 0)
            .Select(p => KnownDevelopers.TryGetValue(p, out var known) ? known
                : p.All(c => !char.IsLetter(c) || char.IsLower(c)) ? char.ToUpperInvariant(p[0]) + p[1..] : p)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        // "Microsoft / X" is published by Microsoft but built by X: X is the developer.
        if (parts.Count > 1) parts.RemoveAll(p => p == "Microsoft");
        return parts.Count == 0 ? null : string.Join(" / ", parts);
    }

    /// <summary>"jetson one" → "Jetson One"; names with any capitals are kept as written.</summary>
    public static string? NormalizeManufacturer(string? manufacturer)
    {
        if (NonEmpty(manufacturer) is not { } m) return null;
        m = Regex.Replace(m, @"\s+", " ");
        if (m.Equals("generic", StringComparison.OrdinalIgnoreCase)) return null;
        return m.Any(char.IsUpper) ? m : string.Join(' ', m.Split(' ').Select(w => w.Length > 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w));
    }

    private static string? NonEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static bool LooksLikeCode(string t) => !t.Contains(' ') && t.Contains('_');

    // ------------------------------------------------------------------ matching helpers

    private static readonly HashSet<string> NoiseWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "modular", "common", "msfs", "fs20", "fs24", "fs2020", "fs2024", "aircraft", "microsoft", "asobo",
        "passengers", "passenger", "cargo", "floats", "skis", "ski", "wheels", "the", "of", "and", "default", "preset",
    };

    /// <summary>"fs24-microsoft-aircraft-a320neo" → "a320neo"; "inibuilds-aircraft-a310" → "a310".</summary>
    private static string ShortPackageName(string name)
    {
        int i = name.IndexOf("aircraft-", StringComparison.OrdinalIgnoreCase);
        return i >= 0 ? name[(i + "aircraft-".Length)..] : name;
    }

    private static string StripNoise(string s) =>
        string.Join(' ', Regex.Split(s, @"\s+").Where(w => !NoiseWords.Contains(w)));

    private static string Squash(string s)
    {
        var sb = new StringBuilder(s.Length);
        foreach (char c in s) if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
        return sb.ToString();
    }

    /// <summary>Lower-case words, split at punctuation and letter/digit boundaries, with weights.</summary>
    private static Dictionary<string, int> Tokens(string s)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (Match m in Regex.Matches(s.ToLowerInvariant(), @"[a-z]+|[0-9]+"))
        {
            string t = m.Value;
            bool digits = char.IsDigit(t[0]);
            if (!digits && (t.Length < 2 || NoiseWords.Contains(t))) continue;
            result[t] = t.Length;
        }
        return result;
    }

    // ------------------------------------------------------------------ reading packages

    private static bool IsAircraftPackage(AircraftPackage pkg) =>
        pkg.ContentType is { } type ? string.Equals(type, "AIRCRAFT", StringComparison.OrdinalIgnoreCase)
            : pkg.Name.Contains("aircraft", StringComparison.OrdinalIgnoreCase);

    public static AircraftPackage? ReadManifest(string dir)
    {
        string path = Path.Combine(dir, "manifest.json");
        try
        {
            if (!File.Exists(path)) return null;
            using var doc = JsonDocument.Parse(File.ReadAllText(path), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            string? S(string key) => root.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? NonEmpty(v.GetString()) : null;
            return new AircraftPackage(Path.GetFileName(dir), S("title") ?? Path.GetFileName(dir), S("creator"), S("manufacturer"), S("content_type"));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Every readable aircraft.cfg in a package. MSFS 2024 modular aircraft keep the ui_* keys in
    /// <c>common/config/aircraft.cfg</c> and only the title in <c>presets/*/*/config/aircraft.cfg</c>.
    /// </summary>
    public static List<AircraftCfgEntry> ReadAircraftCfgs(string packageDir, AircraftPackage? pkg)
    {
        var result = new List<AircraftCfgEntry>();
        string simObjects = Path.Combine(packageDir, "SimObjects");
        if (!Directory.Exists(simObjects)) return result;
        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(simObjects, "aircraft.cfg", new EnumerationOptions
            {
                RecurseSubdirectories = true, MatchCasing = MatchCasing.CaseInsensitive, IgnoreInaccessible = true, MaxRecursionDepth = 8,
            }).ToList();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return result;
        }
        foreach (var file in files)
        {
            var cfg = ReadIni(file);
            if (cfg is null) continue;
            var inherited = CommonCfgFor(file) is { } common && !string.Equals(common, file, StringComparison.OrdinalIgnoreCase) ? ReadIni(common) : null;
            string? general(string key) => Get(cfg, "GENERAL", key) ?? (inherited is null ? null : Get(inherited, "GENERAL", key) ?? FirstFltsim(inherited, key));
            foreach (var (section, keys) in cfg)
            {
                if (!section.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase)) continue;
                if (keys.GetValueOrDefault("title") is not { Length: > 0 } title || title.Contains('$')) continue;
                string? K(string key) => keys.GetValueOrDefault(key) ?? general(key);
                result.Add(new AircraftCfgEntry(title, K("ui_type"), K("ui_variation"),
                    K("ui_manufacturer") ?? general("icao_manufacturer"), K("ui_createdby"), pkg));
            }
        }
        return result;
    }

    /// <summary>For <c>X/presets/dev/name/config/aircraft.cfg</c>: <c>X/common/config/aircraft.cfg</c>.</summary>
    private static string? CommonCfgFor(string cfgFile)
    {
        var dir = new DirectoryInfo(Path.GetDirectoryName(cfgFile)!);
        for (var d = dir; d is not null; d = d.Parent)
        {
            if (!string.Equals(d.Name, "presets", StringComparison.OrdinalIgnoreCase) || d.Parent is null) continue;
            string common = Path.Combine(d.Parent.FullName, "common", "config", "aircraft.cfg");
            return File.Exists(common) ? common : null;
        }
        return null;
    }

    private static string? Get(Dictionary<string, Dictionary<string, string>> ini, string section, string key) =>
        ini.TryGetValue(section, out var s) ? s.GetValueOrDefault(key) : null;

    private static string? FirstFltsim(Dictionary<string, Dictionary<string, string>> ini, string key) =>
        ini.Where(s => s.Key.StartsWith("FLTSIM.", StringComparison.OrdinalIgnoreCase))
            .Select(s => s.Value.GetValueOrDefault(key)).FirstOrDefault(v => v is not null);

    /// <summary>A tolerant INI reader: sections, key = value, ; or // comments, optional quotes.</summary>
    public static Dictionary<string, Dictionary<string, string>>? ReadIni(string path)
    {
        string[] lines;
        try { lines = File.ReadAllLines(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
        var ini = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        Dictionary<string, string>? current = null;
        foreach (var raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] is ';' or '#' || line.StartsWith("//")) continue;
            if (line[0] == '[')
            {
                int end = line.IndexOf(']');
                if (end < 0) continue;
                string name = line[1..end].Trim();
                if (!ini.TryGetValue(name, out current)) ini[name] = current = new(StringComparer.OrdinalIgnoreCase);
                continue;
            }
            int eq = line.IndexOf('=');
            if (current is null || eq <= 0) continue;
            string key = line[..eq].Trim();
            string value = line[(eq + 1)..].Trim();
            if (value.StartsWith('"'))
            {
                int close = value.IndexOf('"', 1);
                value = close > 0 ? value[1..close] : value[1..];
            }
            else
            {
                int c = value.IndexOf(';');
                if (c >= 0) value = value[..c];
                int s = value.IndexOf("//", StringComparison.Ordinal);
                if (s >= 0) value = value[..s];
            }
            value = value.Trim();
            if (value.Length > 0) current.TryAdd(key, value);
        }
        return ini;
    }

    // ------------------------------------------------------------------ where MSFS keeps packages

    /// <summary>
    /// Folders that hold MSFS 2024 packages on this PC: Community and Official under
    /// InstalledPackagesPath (UserCfg.opt), and the game's own <c>minimalcache</c> (manifests of the
    /// streamed official aircraft). <c>SKYLINK_MSFS_PACKAGES</c> (';'-separated) replaces them all.
    /// </summary>
    public static IEnumerable<string> PackageRoots()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_MSFS_PACKAGES") is { Length: > 0 } over)
        {
            foreach (var r in over.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)) yield return r;
            yield break;
        }
        foreach (var cfg in BridgeInstaller.UserCfgCandidates())
        {
            string? packages = null;
            try { if (File.Exists(cfg)) packages = BridgeInstaller.ParseInstalledPackagesPath(File.ReadAllText(cfg)); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
            if (packages is null) continue;
            yield return Path.Combine(packages, "Community");
            foreach (var official in new[] { "Official2024", "Official2020", "Official" })
            {
                string dir = Path.Combine(packages, official);
                string[] subs = [];
                try { if (Directory.Exists(dir)) subs = Directory.GetDirectories(dir); }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
                foreach (var s in subs) yield return s; // Official2024\Steam, Official2024\OneStore
            }
        }
        foreach (var install in InstallFolders()) yield return Path.Combine(install, "minimalcache");
    }

    /// <summary>Where FlightSimulator2024.exe lives: SKYLINK_MSFS_DIR, the running game, Steam libraries.</summary>
    public static IEnumerable<string> InstallFolders()
    {
        var found = new List<string>();
        if (Environment.GetEnvironmentVariable("SKYLINK_MSFS_DIR") is { Length: > 0 } env) found.Add(env);
        try
        {
            foreach (var p in Process.GetProcessesByName(MsfsLauncher.ProcessName))
            {
                try { if (p.MainModule?.FileName is { } exe) found.Add(Path.GetDirectoryName(exe)!); }
                catch (Exception) { /* access denied / exited */ }
                finally { p.Dispose(); }
            }
        }
        catch (Exception) { /* process list unavailable */ }
        foreach (var lib in SteamLibraries()) found.Add(Path.Combine(lib, "steamapps", "common", "MSFS2024"));
        return found.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> SteamLibraries()
    {
        var steamRoots = new List<string>();
        if (OperatingSystem.IsWindows())
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
                if (key?.GetValue("SteamPath") is string sp) steamRoots.Add(sp.Replace('/', '\\'));
            }
            catch (Exception) { /* no registry access */ }
        }
        steamRoots.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
        var libs = new List<string>();
        foreach (var root in steamRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            libs.Add(root);
            string vdf = Path.Combine(root, "steamapps", "libraryfolders.vdf");
            try
            {
                if (!File.Exists(vdf)) continue;
                foreach (Match m in Regex.Matches(File.ReadAllText(vdf), "\"path\"\\s+\"(?<p>[^\"]+)\""))
                    libs.Add(m.Groups["p"].Value.Replace(@"\\", @"\"));
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
        return libs.Distinct(StringComparer.OrdinalIgnoreCase);
    }
}
