using System.Globalization;
using System.Text;
using SkyLink.Core;
using YamlDotNet.Core;
using YamlDotNet.Serialization;

namespace SkyLink.Definitions;

public sealed record LintIssue(string File, string Where, string Message, bool IsError)
{
    public override string ToString() => $"{(IsError ? "error" : "warning")}: {File}: {Where}: {Message}";
}

public sealed record MatchResult(AircraftDefinition Definition, string Reason, bool IsFallback);

/// <summary>
/// Loads every <c>*.yaml</c> definition under a folder, resolves <c>extends:</c> inheritance,
/// validates strictly (unknown keys and bad references are errors, not silent skips) and
/// matches the loaded aircraft to a definition.
/// </summary>
public sealed class DefinitionCatalog
{
    private static readonly HashSet<string> DefKeys = ["id", "name", "version", "extends", "abstract", "match", "groups"];
    private static readonly HashSet<string> MatchKeys = ["title_contains", "atc_model", "path_contains", "fallback"];
    private static readonly HashSet<string> GroupKeys = ["id", "order", "vars", "remove"];
    private static readonly HashSet<string> VarKeys = ["id", "class", "read", "write", "capture", "epsilon", "note", "remove"];
    private static readonly HashSet<string> SetterKeys = ["kevent", "bevent", "hevent", "simvar", "inc", "dec", "step", "rpn", "param", "mode"];
    private static readonly HashSet<string> CaptureKeys = ["kevent", "bevent", "lvar"];

    private readonly List<LintIssue> _issues = [];
    private readonly Dictionary<string, RawDef> _raw = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<AircraftDefinition> _aircraft = [];

    public IReadOnlyList<AircraftDefinition> Aircraft => _aircraft;
    public IReadOnlyList<LintIssue> Issues => _issues.Distinct().ToList();
    public bool HasErrors => _issues.Any(i => i.IsError);

    public static DefinitionCatalog Load(string directory)
    {
        var c = new DefinitionCatalog();
        if (!Directory.Exists(directory))
        {
            c._issues.Add(new LintIssue(directory, "-", "definitions folder not found", true));
            return c;
        }
        foreach (var file in Directory.EnumerateFiles(directory, "*.yaml", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
            c.ReadFile(file, Path.GetRelativePath(directory, file));
        c.ResolveAll();
        return c;
    }

    public static DefinitionCatalog FromYaml(params (string Name, string Yaml)[] files)
    {
        var c = new DefinitionCatalog();
        foreach (var (name, yaml) in files) c.ReadText(yaml, name);
        c.ResolveAll();
        return c;
    }

    // ------------------------------------------------------------------ matching

    public MatchResult? Match(string title, string atcModel, string aircraftPath)
    {
        static bool Has(string hay, string needle) => hay.Contains(needle, StringComparison.OrdinalIgnoreCase);

        var byPath = _aircraft
            .Select(d => (d, best: d.Match.PathContains.Where(p => Has(aircraftPath, p)).Select(p => p.Length).DefaultIfEmpty(0).Max()))
            .Where(x => x.best > 0).OrderByDescending(x => x.best).ToList();
        if (byPath.Count > 0) return new MatchResult(byPath[0].d, $"aircraft path contains '{byPath[0].d.Match.PathContains.First(p => Has(aircraftPath, p))}'", false);

        string atc = NormalizeAtcModel(atcModel);
        var byAtc = _aircraft.Where(d => d.Match.AtcModel.Any(m => string.Equals(m, atc, StringComparison.OrdinalIgnoreCase))).ToList();
        var byTitle = _aircraft
            .Select(d => (d, score: d.Match.TitleContains.Count(t => Has(title, t)), all: d.Match.TitleContains.Count > 0 && d.Match.TitleContains.All(t => Has(title, t))))
            .Where(x => x.all).OrderByDescending(x => x.score).ToList();

        if (byAtc.Count == 1) return new MatchResult(byAtc[0], $"ATC model '{atcModel}'", false);
        if (byAtc.Count > 1)
        {
            var refined = byAtc.FirstOrDefault(d => byTitle.Any(t => t.d == d));
            if (refined is not null) return new MatchResult(refined, $"ATC model '{atcModel}' and title '{title}'", false);
        }
        if (byTitle.Count > 0) return new MatchResult(byTitle[0].d, $"title contains {string.Join(" + ", byTitle[0].d.Match.TitleContains.Select(t => $"'{t}'"))}", false);

        var fallback = _aircraft.FirstOrDefault(d => d.IsFallback);
        return fallback is null ? null : new MatchResult(fallback, $"no definition for '{title}' (ATC model '{atcModel}'); using the generic definition", true);
    }

    /// <summary>
    /// MSFS 2024 reports ATC MODEL as a localization key ("ATCCOM.AC_MODEL_A20N.0.text");
    /// reduce it to the model code ("A20N").
    /// </summary>
    public static string NormalizeAtcModel(string atcModel)
    {
        var m = System.Text.RegularExpressions.Regex.Match(atcModel, @"AC_MODEL[_ ]?([A-Za-z0-9]+)(\.\d+)?\.text", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        return (m.Success ? m.Groups[1].Value : atcModel).Trim();
    }

    // ------------------------------------------------------------------ reading

    private void ReadFile(string path, string name)
    {
        try
        {
            ReadText(File.ReadAllText(path), name);
        }
        catch (IOException e)
        {
            Error(name, "-", e.Message);
        }
    }

    private void ReadText(string yaml, string file)
    {
        object? doc;
        try
        {
            doc = new DeserializerBuilder().Build().Deserialize<object>(yaml);
        }
        catch (YamlException e)
        {
            Error(file, $"line {e.Start.Line}", e.InnerException?.Message ?? e.Message);
            return;
        }
        if (doc is not Dictionary<object, object> map)
        {
            Error(file, "-", "a definition file must be a YAML mapping");
            return;
        }
        var d = Map(map);
        CheckKeys(d, DefKeys, file, "top level");
        string? id = Str(d, "id");
        if (string.IsNullOrWhiteSpace(id))
        {
            Error(file, "top level", "missing 'id'");
            return;
        }
        if (_raw.ContainsKey(id))
        {
            Error(file, "top level", $"duplicate definition id '{id}' (also in {_raw[id].File})");
            return;
        }
        var raw = new RawDef(file, id, Str(d, "name") ?? id, Int(d, "version", file, id) ?? 1, Str(d, "extends"),
            Bool(d, "abstract", file, id) ?? false, d.GetValueOrDefault("match"), []);
        if (d.GetValueOrDefault("groups") is { } groups)
        {
            if (groups is not List<object> list)
                Error(file, id, "'groups' must be a list");
            else
                foreach (var (g, gi) in list.Select((g, i) => (g, i)))
                    if (g is Dictionary<object, object> gm) raw.Groups.Add(ReadGroup(Map(gm), file, $"groups[{gi}]"));
                    else Error(file, $"groups[{gi}]", "each group must be a mapping");
        }
        _raw[id] = raw;
    }

    private RawGroup ReadGroup(Dictionary<string, object?> g, string file, string where)
    {
        CheckKeys(g, GroupKeys, file, where);
        string id = Str(g, "id") ?? "";
        if (id.Length == 0) Error(file, where, "group is missing 'id'");
        var group = new RawGroup(id, Int(g, "order", file, where), Bool(g, "remove", file, where) ?? false, []);
        if (g.GetValueOrDefault("vars") is List<object> vars)
            foreach (var (v, vi) in vars.Select((v, i) => (v, i)))
                if (v is Dictionary<object, object> vm)
                {
                    var m = Map(vm);
                    string vid = Str(m, "id") ?? "";
                    if (vid.Length == 0) Error(file, $"{id}.vars[{vi}]", "variable is missing 'id'");
                    else group.Vars.Add((vid, m));
                }
                else Error(file, $"{id}.vars[{vi}]", "each variable must be a mapping");
        else if (g.ContainsKey("vars"))
            Error(file, where, "'vars' must be a list");
        return group;
    }

    // ------------------------------------------------------------------ inheritance + compile

    private void ResolveAll()
    {
        foreach (var raw in _raw.Values.OrderBy(r => r.Id, StringComparer.Ordinal))
        {
            if (raw.Abstract) continue;
            var chain = new List<RawDef>();
            if (!BuildChain(raw, chain)) continue;
            var def = Compile(raw, chain);
            if (def is not null) _aircraft.Add(def);
        }
        foreach (var raw in _raw.Values.Where(r => r.Abstract))
            BuildChain(raw, []); // still report broken extends in abstract bases
    }

    private bool BuildChain(RawDef raw, List<RawDef> chain)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var cur = raw; cur is not null;)
        {
            if (!seen.Add(cur.Id))
            {
                Error(raw.File, raw.Id, $"circular 'extends' through '{cur.Id}'");
                return false;
            }
            chain.Insert(0, cur);
            if (cur.Extends is null) break;
            if (!_raw.TryGetValue(cur.Extends, out var parent))
            {
                Error(cur.File, cur.Id, $"extends unknown definition '{cur.Extends}'");
                return false;
            }
            cur = parent;
        }
        return true;
    }

    private AircraftDefinition? Compile(RawDef leaf, List<RawDef> chain)
    {
        int errorsBefore = _issues.Count(i => i.IsError);

        // Merge groups/vars down the inheritance chain (base first, leaf last).
        var groups = new List<(string Id, int? Order, List<(string Id, Dictionary<string, object?> Map, string File)> Vars)>();
        foreach (var def in chain)
        {
            var seenInThisFile = new HashSet<string>(StringComparer.Ordinal);
            foreach (var g in def.Groups)
                foreach (var (vid, _) in g.Vars)
                    if (!seenInThisFile.Add(vid))
                        Error(def.File, $"{g.Id}.{vid}", $"variable '{vid}' is defined more than once in this file (overrides belong in a definition that 'extends' this one)");

            foreach (var g in def.Groups)
            {
                int gi = groups.FindIndex(x => string.Equals(x.Id, g.Id, StringComparison.Ordinal));
                if (g.Remove)
                {
                    if (gi >= 0) groups.RemoveAt(gi);
                    continue;
                }
                if (gi < 0)
                {
                    groups.Add((g.Id, g.Order, []));
                    gi = groups.Count - 1;
                }
                else if (g.Order is not null)
                {
                    groups[gi] = (groups[gi].Id, g.Order, groups[gi].Vars);
                }
                var vars = groups[gi].Vars;
                foreach (var (vid, map) in g.Vars)
                {
                    // A variable lives in exactly one group: an override anywhere replaces it.
                    foreach (var other in groups) other.Vars.RemoveAll(x => string.Equals(x.Id, vid, StringComparison.Ordinal));
                    if (Bool(map, "remove", def.File, vid) == true) continue;
                    vars.Add((vid, map, def.File));
                }
            }
        }

        var groupSpecs = groups.Select((g, i) => new GroupSpec(g.Id, g.Order ?? (i + 1) * 10)).ToList();
        var bindings = new List<VarBinding>();
        foreach (var g in groups)
            foreach (var (vid, map, file) in g.Vars)
                if (CompileVar(vid, g.Id, map, file) is { } b) bindings.Add(b);

        if (bindings.Count == 0 && leaf.Groups.Count > 0) Error(leaf.File, leaf.Id, "definition has no valid variables");
        var match = CompileMatch(leaf);
        if (match.TitleContains.Count == 0 && match.AtcModel.Count == 0 && match.PathContains.Count == 0 && !match.Fallback)
            Warn(leaf.File, leaf.Id, "no 'match' rules: this definition will never be selected automatically");

        if (_issues.Count(i => i.IsError) > errorsBefore) return null;

        var h = Fnv64.Begin();
        h = Fnv64.Add(h, Encoding.UTF8.GetBytes(leaf.Id));
        h = Fnv64.Add(h, leaf.Version);
        foreach (var b in bindings) h = Fnv64.Add(h, Encoding.UTF8.GetBytes(b.Canonical));

        SyncSchema schema;
        try
        {
            schema = new SyncSchema(groupSpecs, bindings.Select(b => b.Spec), h);
        }
        catch (ArgumentException e)
        {
            Error(leaf.File, leaf.Id, e.Message);
            return null;
        }

        return new AircraftDefinition
        {
            Id = leaf.Id,
            Name = leaf.Name,
            Version = leaf.Version,
            SourceFile = leaf.File,
            Match = match,
            Groups = groupSpecs,
            Vars = bindings,
            Schema = schema,
        };
    }

    private AircraftMatch CompileMatch(RawDef leaf)
    {
        if (leaf.Match is null) return AircraftMatch.None;
        if (leaf.Match is not Dictionary<object, object> mm)
        {
            Error(leaf.File, "match", "'match' must be a mapping");
            return AircraftMatch.None;
        }
        var m = Map(mm);
        CheckKeys(m, MatchKeys, leaf.File, "match");
        return new AircraftMatch(StrList(m, "title_contains", leaf.File), StrList(m, "atc_model", leaf.File),
            StrList(m, "path_contains", leaf.File), Bool(m, "fallback", leaf.File, "match") ?? false);
    }

    private VarBinding? CompileVar(string id, string group, Dictionary<string, object?> m, string file)
    {
        string where = $"{group}.{id}";
        CheckKeys(m, VarKeys, file, where);
        string? clsText = Str(m, "class");
        if (clsText is null || !Enum.TryParse<SyncClass>(clsText, true, out var cls) || !Enum.IsDefined(cls))
        {
            Error(file, where, $"'class' must be one of control, state, analog, event, derived, local (got '{clsText}')");
            return null;
        }

        double eps = 0;
        if (m.ContainsKey("epsilon"))
        {
            if (!double.TryParse(Convert.ToString(m["epsilon"], CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out eps) || eps < 0)
            {
                Error(file, where, "'epsilon' must be a non-negative number");
                return null;
            }
        }
        else if (cls is SyncClass.Analog or SyncClass.Control)
        {
            eps = 0.001;
        }

        SimRef? read = null;
        if (Str(m, "read") is { } readText)
        {
            try { read = SimRef.Parse(readText); }
            catch (FormatException e) { Error(file, where, e.Message); return null; }
        }

        EventCapture? capture = null;
        if (m.GetValueOrDefault("capture") is { } capRaw)
        {
            capture = ParseCapture(capRaw, file, where);
            if (capture is null) return null;
        }

        List<Setter> write;
        try
        {
            write = m.GetValueOrDefault("write") is { } w ? ParseSetters(w, read, file, where) : DefaultSetters(cls, read, capture);
        }
        catch (FormatException e)
        {
            Error(file, where, e.Message);
            return null;
        }

        // Class rules.
        switch (cls)
        {
            case SyncClass.State or SyncClass.Analog or SyncClass.Control:
                if (read is null) return Fail("needs 'read'");
                if (write.Count == 0) return Fail(read.Kind == SimRefKind.A
                    ? "A: variables need an explicit 'write' (simvar, kevent, bevent, inc/dec or rpn)"
                    : "needs 'write'");
                if (capture is not null) return Fail("'capture' is only for class event");
                break;
            case SyncClass.Event:
                if (capture is null) return Fail("events need 'capture' (kevent, bevent or lvar)");
                if (read is not null) return Fail("events are not sampled; remove 'read'");
                break;
            case SyncClass.Derived:
                if (read is null) return Fail("needs 'read'");
                if (m.ContainsKey("write")) return Fail("derived values are never written; remove 'write'");
                if (!m.ContainsKey("epsilon")) Warn(file, where, "derived value without 'epsilon'; tiny float noise will be reported as divergence");
                break;
        }

        foreach (var s in write)
        {
            if (s.Mode == WriteMode.Toggle && (s.Kind != SetterKind.KEvent || read is null))
                return Fail("mode: toggle needs a kevent setter and a 'read' to compare against");
            if (s.Kind == SetterKind.IncDec && read is null) return Fail("inc/dec needs 'read'");
            if (s.Kind == SetterKind.SimVar && s.Var!.Kind == SimRefKind.B) return Fail("simvar setter cannot target a B: event; use bevent");
            if (s.Kind == SetterKind.Rpn && cls != SyncClass.Event && !s.Target.Contains("{value}"))
                Warn(file, where, "rpn setter has no {value} placeholder, so the synced value is never passed to the aircraft");
        }
        if (cls == SyncClass.Event && capture!.Kind == CaptureKind.LVarEdge)
            Warn(file, where, "L-var edge capture is reliable only with the in-sim bridge; SimConnect polling can miss single-frame pulses");

        return new VarBinding(new VarSpec(id, cls, group, eps), read, write, capture, Str(m, "note"));

        VarBinding? Fail(string msg)
        {
            Error(file, where, msg);
            return null;
        }
    }

    private EventCapture? ParseCapture(object raw, string file, string where)
    {
        if (raw is not Dictionary<object, object> cm)
        {
            Error(file, where, "'capture' must be a mapping like { kevent: AP_MASTER }");
            return null;
        }
        var c = Map(cm);
        CheckKeys(c, CaptureKeys, file, where + ".capture");
        if (c.Count != 1)
        {
            Error(file, where, "'capture' needs exactly one of kevent, bevent, lvar");
            return null;
        }
        var (key, value) = c.First();
        string name = Convert.ToString(value, CultureInfo.InvariantCulture)?.Trim() ?? "";
        if (name.Length == 0)
        {
            Error(file, where, "'capture' name is empty");
            return null;
        }
        return new EventCapture(key switch { "kevent" => CaptureKind.KEvent, "bevent" => CaptureKind.BEvent, _ => CaptureKind.LVarEdge }, StripPrefix(name));
    }

    private static List<Setter> DefaultSetters(SyncClass cls, SimRef? read, EventCapture? capture)
    {
        if (cls == SyncClass.Event && capture is not null)
            return [capture.Kind switch
            {
                CaptureKind.KEvent => new Setter(SetterKind.KEvent, capture.Name, ParamExpr.Identity),
                CaptureKind.BEvent => new Setter(SetterKind.BEvent, capture.Name, ParamExpr.Identity),
                _ => new Setter(SetterKind.SimVar, capture.Name, ParamExpr.Identity, Var: new SimRef(SimRefKind.L, capture.Name, "Number")),
            }];
        if (read is null || cls is SyncClass.Derived or SyncClass.Local) return [];
        return read.Kind switch
        {
            SimRefKind.L => [new Setter(SetterKind.SimVar, read.Name, ParamExpr.Identity, Var: read)],
            SimRefKind.B => [new Setter(SetterKind.BEvent, read.Name, ParamExpr.Identity)],
            _ => [],
        };
    }

    private List<Setter> ParseSetters(object raw, SimRef? read, string file, string where)
    {
        if (raw is List<object> list) return list.Select(x => ParseSetter(x, read)).ToList();
        return [ParseSetter(raw, read)];
    }

    private Setter ParseSetter(object raw, SimRef? read)
    {
        if (raw is string s)
        {
            s = s.Trim();
            if (s.Equals("simvar", StringComparison.OrdinalIgnoreCase))
            {
                if (read is null || read.Kind == SimRefKind.B) throw new FormatException("'write: simvar' needs an A: or L: 'read'");
                return new Setter(SetterKind.SimVar, read.Name, ParamExpr.Identity, Var: read);
            }
            if (s.StartsWith("K:", StringComparison.OrdinalIgnoreCase)) return new Setter(SetterKind.KEvent, s[2..].Trim(), ParamExpr.Identity);
            if (s.StartsWith("B:", StringComparison.OrdinalIgnoreCase)) return new Setter(SetterKind.BEvent, s[2..].Trim(), ParamExpr.Identity);
            if (s.StartsWith("H:", StringComparison.OrdinalIgnoreCase)) return new Setter(SetterKind.HEvent, s[2..].Trim(), ParamExpr.Identity);
            throw new FormatException($"unknown write '{s}' (use simvar, K:EVENT, B:EVENT, H:EVENT or a mapping)");
        }
        if (raw is not Dictionary<object, object> dm) throw new FormatException("'write' must be a string, a mapping or a list");
        var d = Map(dm);
        var unknown = d.Keys.Where(k => !SetterKeys.Contains(k)).ToList();
        if (unknown.Count > 0) throw new FormatException($"unknown write key(s): {string.Join(", ", unknown)}");

        var param = d.GetValueOrDefault("param") is { } p ? ParamExpr.Parse(Convert.ToString(p, CultureInfo.InvariantCulture)!) : ParamExpr.Identity;
        var mode = WriteMode.Set;
        if (d.GetValueOrDefault("mode") is { } modeRaw)
        {
            string ms = Convert.ToString(modeRaw, CultureInfo.InvariantCulture)!.Trim();
            mode = ms.ToLowerInvariant() switch
            {
                "set" => WriteMode.Set,
                "toggle" => WriteMode.Toggle,
                _ => throw new FormatException($"unknown mode '{ms}' (set or toggle)"),
            };
        }

        string? S(string key) => d.GetValueOrDefault(key) is { } v ? StripPrefix(Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim()) : null;
        int kinds = new[] { "kevent", "bevent", "hevent", "simvar", "inc", "rpn" }.Count(d.ContainsKey);
        if (kinds != 1) throw new FormatException("a setter needs exactly one of kevent, bevent, hevent, simvar, inc, rpn");

        if (S("kevent") is { } k) return new Setter(SetterKind.KEvent, k, param, mode);
        if (S("bevent") is { } b) return new Setter(SetterKind.BEvent, b, param, mode);
        if (S("hevent") is { } h)
        {
            if (mode == WriteMode.Toggle) throw new FormatException("hevent setters cannot use mode: toggle (H: events carry no state to compare); use rpn with a condition");
            return new Setter(SetterKind.HEvent, h, param, mode);
        }
        if (d.GetValueOrDefault("simvar") is { } sv)
        {
            var target = SimRef.Parse(Convert.ToString(sv, CultureInfo.InvariantCulture)!);
            return new Setter(SetterKind.SimVar, target.Name, param, mode, target);
        }
        if (S("inc") is { } inc)
        {
            string dec = S("dec") ?? throw new FormatException("inc needs a matching 'dec'");
            double step = d.GetValueOrDefault("step") is { } st ? Convert.ToDouble(st, CultureInfo.InvariantCulture) : 1;
            if (step <= 0) throw new FormatException("'step' must be positive");
            return new Setter(SetterKind.IncDec, inc, param, mode, DecEvent: dec, Step: step);
        }
        return new Setter(SetterKind.Rpn, Convert.ToString(d["rpn"], CultureInfo.InvariantCulture)!, param, mode);
    }

    // ------------------------------------------------------------------ helpers

    private static string StripPrefix(string name) =>
        name.Length > 2 && name[1] == ':' && "KkBbLl".Contains(name[0]) ? name[2..].Trim() : name;

    private static Dictionary<string, object?> Map(Dictionary<object, object> m) =>
        m.ToDictionary(kv => Convert.ToString(kv.Key, CultureInfo.InvariantCulture)!.Trim(), kv => (object?)kv.Value, StringComparer.Ordinal);

    private void CheckKeys(Dictionary<string, object?> m, HashSet<string> allowed, string file, string where)
    {
        foreach (var k in m.Keys)
            if (!allowed.Contains(k))
                Error(file, where, $"unknown key '{k}' (allowed: {string.Join(", ", allowed)})");
    }

    private static string? Str(Dictionary<string, object?> m, string key) =>
        m.GetValueOrDefault(key) is { } v ? Convert.ToString(v, CultureInfo.InvariantCulture) : null;

    private int? Int(Dictionary<string, object?> m, string key, string file, string where)
    {
        if (m.GetValueOrDefault(key) is not { } v) return null;
        if (int.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i)) return i;
        Error(file, where, $"'{key}' must be an integer");
        return null;
    }

    private bool? Bool(Dictionary<string, object?> m, string key, string file, string where)
    {
        if (m.GetValueOrDefault(key) is not { } v) return null;
        if (bool.TryParse(Convert.ToString(v, CultureInfo.InvariantCulture), out bool b)) return b;
        Error(file, where, $"'{key}' must be true or false");
        return null;
    }

    private List<string> StrList(Dictionary<string, object?> m, string key, string file)
    {
        var v = m.GetValueOrDefault(key);
        if (v is null) return [];
        if (v is string s) return [s];
        if (v is List<object> l) return l.Select(x => Convert.ToString(x, CultureInfo.InvariantCulture)!).ToList();
        Error(file, "match", $"'{key}' must be a string or a list of strings");
        return [];
    }

    private void Error(string file, string where, string msg) => _issues.Add(new LintIssue(file, where, msg, true));
    private void Warn(string file, string where, string msg) => _issues.Add(new LintIssue(file, where, msg, false));

    private sealed record RawDef(string File, string Id, string Name, int Version, string? Extends, bool Abstract, object? Match, List<RawGroup> Groups);

    private sealed record RawGroup(string Id, int? Order, bool Remove, List<(string Id, Dictionary<string, object?> Map)> Vars);
}
