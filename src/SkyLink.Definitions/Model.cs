using SkyLink.Core;

namespace SkyLink.Definitions;

public enum SimRefKind { A, L, B }

/// <summary>A reference to something readable in the sim: <c>A:NAME:1, Unit</c>, <c>L:NAME</c> or <c>B:NAME</c>.</summary>
public sealed record SimRef(SimRefKind Kind, string Name, string Unit)
{
    public static SimRef Parse(string text)
    {
        string t = text.Trim();
        if (t.Length < 3 || t[1] != ':') throw new FormatException($"'{text}' must start with A:, L: or B:.");
        var kind = char.ToUpperInvariant(t[0]) switch
        {
            'A' => SimRefKind.A,
            'L' => SimRefKind.L,
            'B' => SimRefKind.B,
            _ => throw new FormatException($"'{text}': unknown prefix '{t[0]}:' (expected A:, L: or B:)."),
        };
        string rest = t[2..];
        string name, unit;
        int comma = rest.IndexOf(',');
        if (comma >= 0)
        {
            name = rest[..comma].Trim();
            unit = rest[(comma + 1)..].Trim();
        }
        else
        {
            name = rest.Trim();
            unit = "";
        }
        if (name.Length == 0) throw new FormatException($"'{text}': empty name.");
        switch (kind)
        {
            case SimRefKind.A when unit.Length == 0:
                throw new FormatException($"'{text}': A: variables need a unit, e.g. 'A:{name}, Bool'.");
            case SimRefKind.L when unit.Length == 0:
                unit = "Number";
                break;
            case SimRefKind.B when unit.Length > 0:
                throw new FormatException($"'{text}': B: input events take no unit.");
        }
        return new SimRef(kind, name, unit);
    }

    public override string ToString() => Kind == SimRefKind.B ? $"B:{Name}" : $"{Kind}:{Name}, {Unit}";
}

public enum SetterKind
{
    /// <summary>Write the value into a sim variable (settable A:var or L:var).</summary>
    SimVar,
    /// <summary>Transmit a K: event with a parameter computed from the value.</summary>
    KEvent,
    /// <summary>Set a B: input event to the value.</summary>
    BEvent,
    /// <summary>Fire INC/DEC K: events until the value matches.</summary>
    IncDec,
    /// <summary>Calculator (RPN) code, executed by the in-sim WASM bridge.</summary>
    Rpn,
}

public enum WriteMode
{
    Set,
    /// <summary>The setter flips the switch: fire only if current != target, never twice in flight.</summary>
    Toggle,
}

public sealed record Setter(
    SetterKind Kind,
    string Target,
    ParamExpr Param,
    WriteMode Mode = WriteMode.Set,
    SimRef? Var = null,
    string? DecEvent = null,
    double Step = 1)
{
    public string Canonical => $"{Kind}|{Target}|{Param.Source}|{Mode}|{Var}|{DecEvent}|{Step}";
}

public enum CaptureKind { KEvent, BEvent, LVarEdge }

public sealed record EventCapture(CaptureKind Kind, string Name)
{
    public string Canonical => $"{Kind}|{Name}";
}

/// <summary>Everything the adapter needs to read, write and capture one synced variable.</summary>
public sealed record VarBinding(
    VarSpec Spec,
    SimRef? Read,
    IReadOnlyList<Setter> Write,
    EventCapture? Capture,
    string? Note)
{
    public string Canonical =>
        $"{Spec.Id}|{Spec.Class}|{Spec.Group}|{Spec.Epsilon}|{Read}|{string.Join(";", Write.Select(w => w.Canonical))}|{Capture?.Canonical}";
}

public sealed record AircraftMatch(
    IReadOnlyList<string> TitleContains,
    IReadOnlyList<string> AtcModel,
    IReadOnlyList<string> PathContains,
    bool Fallback)
{
    public static readonly AircraftMatch None = new([], [], [], false);
}

/// <summary>A fully resolved (inheritance applied) and validated aircraft definition.</summary>
public sealed class AircraftDefinition
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required int Version { get; init; }
    public required string SourceFile { get; init; }
    public required AircraftMatch Match { get; init; }
    public required IReadOnlyList<GroupSpec> Groups { get; init; }
    public required IReadOnlyList<VarBinding> Vars { get; init; }
    public required SyncSchema Schema { get; init; }
    public bool IsFallback => Match.Fallback;
}
