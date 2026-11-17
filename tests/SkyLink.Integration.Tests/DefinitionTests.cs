using SkyLink.Core;
using SkyLink.Definitions;

namespace SkyLink.Integration.Tests;

public sealed class DefinitionTests
{
    private const string Base = """
        id: base
        abstract: true
        groups:
          - id: electrical
            order: 10
            vars:
              - { id: battery, class: state, read: "A:ELECTRICAL MASTER BATTERY:1, Bool", write: { kevent: TOGGLE_MASTER_BATTERY, mode: toggle } }
              - { id: beacon, class: state, read: "A:LIGHT BEACON, Bool", write: "K:BEACON_LIGHTS_SET" }
          - id: ap
            order: 80
            vars:
              - { id: hdg, class: analog, read: "A:AUTOPILOT HEADING LOCK DIR, Degrees", epsilon: 0.5, write: { kevent: HEADING_BUG_SET, param: "round(value)" } }
              - { id: ap_press, class: event, capture: { kevent: AP_MASTER } }
        """;

    private static string Plane(string extra = "") => $$"""
        id: plane
        extends: base
        match: { atc_model: ["C172"], title_contains: ["172"] }
        groups:
          - id: electrical
            vars:
              - { id: beacon, remove: true }
              - { id: fuel_pump, class: state, read: "L:FUEL_PUMP" }
        {{extra}}
        """;

    [Fact]
    public void Shipped_definitions_are_valid()
    {
        var catalog = DefinitionCatalog.Load(Cli.Program.FindDefinitions());
        Assert.Empty(catalog.Issues.Where(i => i.IsError));
        Assert.Contains(catalog.Aircraft, d => d.Id == "asobo-c172sp-g1000");
        Assert.Contains(catalog.Aircraft, d => d.IsFallback);
    }

    [Fact]
    public void Inheritance_adds_overrides_and_removes()
    {
        var c = DefinitionCatalog.FromYaml(("base.yaml", Base), ("plane.yaml", Plane()));
        Assert.Empty(c.Issues.Where(i => i.IsError));
        var d = Assert.Single(c.Aircraft);
        var ids = d.Vars.Select(v => v.Spec.Id).ToList();
        Assert.Equal(["battery", "fuel_pump", "hdg", "ap_press"], ids);
        var pump = d.Vars.Single(v => v.Spec.Id == "fuel_pump");
        Assert.Equal(SetterKind.SimVar, pump.Write.Single().Kind); // L: vars default to writing themselves
        var press = d.Vars.Single(v => v.Spec.Id == "ap_press");
        Assert.Equal("AP_MASTER", press.Write.Single().Target); // events replay what they capture
    }

    [Theory]
    [InlineData("  - { id: x, class: state, read: \"A:LIGHT NAV, Bool\" }", "explicit 'write'")]
    [InlineData("  - { id: x, class: state, read: \"A:LIGHT NAV, Bool\", write: \"K:X\", colour: red }", "unknown key 'colour'")]
    [InlineData("  - { id: x, class: switch, read: \"L:X\" }", "'class' must be one of")]
    [InlineData("  - { id: x, class: state, read: \"A:LIGHT NAV\" }", "need a unit")]
    [InlineData("  - { id: x, class: state, read: \"L:X\", write: { kevent: T, mode: flip } }", "unknown mode")]
    [InlineData("  - { id: x, class: event, read: \"L:X\", capture: { kevent: Y } }", "not sampled")]
    [InlineData("  - { id: x, class: derived, read: \"L:X\", write: simvar }", "never written")]
    [InlineData("  - { id: x, class: analog, read: \"L:X\", write: { kevent: T, param: \"value **\" } }", "Expression")]
    public void Mistakes_are_errors_not_silent_skips(string varYaml, string expected)
    {
        string yaml = $"""
            id: bad
            match: {"{"} fallback: true {"}"}
            groups:
              - id: g
                vars:
                  {varYaml}
            """;
        var c = DefinitionCatalog.FromYaml(("bad.yaml", yaml));
        Assert.Empty(c.Aircraft); // a definition with any error never loads
        Assert.Contains(c.Issues, i => i.IsError && i.Message.Contains(expected, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Same_id_twice_in_one_file_is_an_error_not_an_override()
    {
        const string yaml = """
            id: dup
            match: { fallback: true }
            groups:
              - id: a
                vars:
                  - { id: battery, class: state, read: "L:ONE" }
              - id: b
                vars:
                  - { id: battery, class: state, read: "L:TWO" }
            """;
        var c = DefinitionCatalog.FromYaml(("dup.yaml", yaml));
        Assert.Empty(c.Aircraft);
        Assert.Contains(c.Issues, i => i.IsError && i.Message.Contains("defined more than once"));
    }

    [Fact]
    public void Unknown_parent_is_an_error()
    {
        var c = DefinitionCatalog.FromYaml(("p.yaml", Plane()));
        Assert.Contains(c.Issues, i => i.IsError && i.Message.Contains("extends unknown definition 'base'"));
    }

    [Fact]
    public void Matching_prefers_path_then_atc_model_then_title_then_fallback()
    {
        const string generic = "id: generic\nmatch: { fallback: true }\ngroups: [ { id: g, vars: [ { id: v, class: state, read: \"L:V\" } ] } ]";
        const string byPath = "id: bypath\nmatch: { path_contains: [\"special_172\"] }\ngroups: [ { id: g, vars: [ { id: v, class: state, read: \"L:V\" } ] } ]";
        var c = DefinitionCatalog.FromYaml(("base.yaml", Base), ("plane.yaml", Plane()), ("generic.yaml", generic), ("bypath.yaml", byPath));
        Assert.Empty(c.Issues.Where(i => i.IsError));

        Assert.Equal("bypath", c.Match("Cessna 172", "C172", @"SimObjects\Airplanes\special_172\aircraft.cfg")!.Definition.Id);
        Assert.Equal("plane", c.Match("Whatever", "C172", "x")!.Definition.Id);
        Assert.Equal("plane", c.Match("Whatever", "ATCCOM.AC_MODEL_C172.0.text", "x")!.Definition.Id); // MSFS 2024 form
        Assert.Equal("plane", c.Match("Cessna 172 Skyhawk", "", "x")!.Definition.Id);
        var fb = c.Match("Airbus A321", "A321", "x")!;
        Assert.Equal("generic", fb.Definition.Id);
        Assert.True(fb.IsFallback);
    }

    [Fact]
    public void Any_binding_change_changes_the_hash_so_mismatched_pilots_are_refused()
    {
        var a = DefinitionCatalog.FromYaml(("base.yaml", Base), ("plane.yaml", Plane())).Aircraft.Single();
        var b = DefinitionCatalog.FromYaml(("base.yaml", Base.Replace("round(value)", "round(value) + 0")), ("plane.yaml", Plane())).Aircraft.Single();
        var c = DefinitionCatalog.FromYaml(("base.yaml", Base), ("plane.yaml", Plane())).Aircraft.Single();
        Assert.NotEqual(a.Schema.Hash, b.Schema.Hash);
        Assert.Equal(a.Schema.Hash, c.Schema.Hash);
    }

    [Theory]
    [InlineData("ATCCOM.AC_MODEL_A20N.0.text", "A20N")]
    [InlineData("ATCCOM.AC_MODEL C172.0.text", "C172")]
    [InlineData("C172", "C172")]
    public void Atc_model_localization_keys_are_reduced_to_the_model_code(string raw, string expected) =>
        Assert.Equal(expected, DefinitionCatalog.NormalizeAtcModel(raw));

    [Theory]
    [InlineData("value", 5, 5)]
    [InlineData("round(value * 16)", 1013.25, 16212)]
    [InlineData("bcd16(value)", 1200, 0x1200)]
    [InlineData("bcd16(value)", 7700, 0x7700)]
    [InlineData("round(value * 16383 / 3)", 3, 16383)]
    [InlineData("-value * 2 + (1 - 1)", 4, -8)]
    [InlineData("clamp16k(value * 100000)", 1, 16384)]
    public void Param_expressions(string expr, double input, double expected) =>
        Assert.Equal(expected, ParamExpr.Parse(expr).Evaluate(input), 6);

    [Fact]
    public void Negative_event_data_travels_as_twos_complement()
    {
        Assert.Equal(0xFFFFFFFFu, Sim.SimBridgeBase.ToData(-1));
        Assert.Equal(118_000_000u, Sim.SimBridgeBase.ToData(118_000_000));
        Assert.Equal(3_000_000_000u, Sim.SimBridgeBase.ToData(3e9));
    }
}
