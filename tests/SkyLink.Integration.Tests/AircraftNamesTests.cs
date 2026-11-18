using System.Text;
using SkyLink.Lobby;
using SkyLink.Sim;

namespace SkyLink.Integration.Tests;

/// <summary>
/// Friendly names and developers for the aircraft list: read from sample package folders laid out
/// like MSFS 2024's (Community add-ons unpacked, official aircraft only as manifests).
/// </summary>
public sealed class AircraftNamesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "skylink-names-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }

    private string Write(string relative, string text)
    {
        string path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, text);
        return path;
    }

    private void Manifest(string folder, string title, string creator, string manufacturer, string type = "AIRCRAFT") =>
        Write(Path.Combine(folder, "manifest.json"),
            $$"""{ "dependencies": [], "content_type": "{{type}}", "title": "{{title}}", "manufacturer": "{{manufacturer}}", "creator": "{{creator}}", "package_version": "1.0.0" }""");

    /// <summary>Official aircraft as MSFS's minimalcache shows them: a manifest, nothing unpacked.</summary>
    private string MinimalCache()
    {
        string mc = Path.Combine(_root, "minimalcache");
        Manifest(Path.Combine(mc, "fs24-microsoft-aircraft-a320neo"), "Airbus A320neo", "Microsoft/iniBuilds", "Airbus");
        Manifest(Path.Combine(mc, "fs24-microsoft-aircraft-a321"), "Airbus A321", "Microsoft/iniBuilds", "Airbus");
        Manifest(Path.Combine(mc, "fs24-asobo-aircraft-c172sp-as1000"), "C172SP G1000", "Asobo Studio", "Cessna");
        Manifest(Path.Combine(mc, "fs24-asobo-aircraft-c152"), "Cessna 152", "Asobo Studio", "Cessna");
        Manifest(Path.Combine(mc, "fs24-asobo-aircraft-b7478i"), "B747", "Working Title Simulations", "Boeing");
        Manifest(Path.Combine(mc, "fs24-asobo-aircraft-kingair350"), "Beechcraft King Air 350i", "Asobo Studio", "Textron Aviation");
        Manifest(Path.Combine(mc, "fs24-asobo-aircraft-bonanza-g36"), "Beechcraft Bonanza G36", "Asobo Studio", "Textron Aviation");
        Manifest(Path.Combine(mc, "fs24-microsoft-aircraft-dc3"), "DC-3", "Microsoft/Aeroplane Heaven", "Douglas");
        Manifest(Path.Combine(mc, "fs24-microsoft-aircraft-archer"), "Midnight", "Microsoft / FlightFX", "archer");
        // Must not win matches: livery packs, AI-only models, non-aircraft content.
        Manifest(Path.Combine(mc, "fs20-asobo-aircraft-pitts-s1-reno-livery-aviators"), "Pitts S1 Aviators Club livery", "Asobo Studio", "Aviat");
        Manifest(Path.Combine(mc, "fs24-asobo-passiveaircraft-a320family"), "Passive A320 family", "Asobo Studio", "Airbus");
        Manifest(Path.Combine(mc, "fs24-asobo-trainings-basic-aircraft"), "Asobo Trainings - Aircraft Essentials", "Asobo Studio", "", "MISSION");
        return mc;
    }

    [Theory]
    [InlineData("A320neo V2", "A320neo V2", "iniBuilds", "Airbus")]
    [InlineData("A320neo V2 VIP", "A320neo V2 VIP", "iniBuilds", "Airbus")]
    [InlineData("A321", "A321", "iniBuilds", "Airbus")]
    [InlineData("C172SP G1000 Passengers", "C172SP G1000 Passengers", "Asobo", "Cessna")]
    [InlineData("Cessna C152 Aerial Advertising", "Cessna C152 Aerial Advertising", "Asobo", "Cessna")]
    [InlineData("747-8i", "747-8i", "Working Title", "Boeing")]
    [InlineData("Beechcraft King Air", "Beechcraft King Air", "Asobo", "Textron Aviation")]
    [InlineData("Douglas DC-3 Aviators Club modern", "Douglas DC-3 Aviators Club modern", "Aeroplane Heaven", "Douglas")]
    [InlineData("Midnight", "Midnight", "FlightFX", "Archer")]
    [InlineData("Jetson One [Preset Default]", "Jetson One", null, null)]
    [InlineData("Some Unknown Glider", "Some Unknown Glider", null, null)]
    public void Official_aircraft_are_matched_to_their_package_manifest(string title, string name, string? developer, string? manufacturer)
    {
        var names = AircraftNames.Scan([MinimalCache()]);
        Assert.Equal((name, developer, manufacturer), names.Resolve(title));
    }

    [Fact]
    public void Community_aircraft_cfg_titles_map_exactly_including_2024_presets()
    {
        string community = Path.Combine(_root, "Community");
        // MSFS 2020 style: one aircraft.cfg with a [FLTSIM.n] per variant.
        string fenix = Path.Combine(community, "fnx-aircraft-320");
        Manifest(fenix, "Fenix A320", "Fenix Simulations", "Airbus");
        Write(Path.Combine(fenix, @"SimObjects\Airplanes\FNX_320_CFM\aircraft.cfg"), """
            [VERSION]
            major = 1
            [GENERAL]
            icao_manufacturer = "AIRBUS"
            [FLTSIM.0]
            title = "FNX_320_CFM_HOUSE" ; Variation name
            ui_manufacturer = "Airbus"
            ui_type = "A320"
            ui_variation = "CFM House"
            ui_createdby = "Fenix Simulations"
            [FLTSIM.1]
            title = "FNX_320_CFM_EZY"
            ui_type = "A320"
            ui_variation = "easyJet"
            """);
        // MSFS 2024 modular: presets carry only the title, common/config the ui_* keys.
        string pmdg = Path.Combine(community, "pmdg-aircraft-77w");
        Manifest(pmdg, "PMDG 777-300ER", "PMDG", "Boeing");
        Write(Path.Combine(pmdg, @"SimObjects\Airplanes\pmdg-77w\common\config\aircraft.cfg"), """
            [FLTSIM.0]
            title = "$Title Common"
            ui_manufacturer = "Boeing"
            ui_type = "777-300ER"
            ui_createdby = "PMDG Simulations"
            """);
        Write(Path.Combine(pmdg, @"SimObjects\Airplanes\pmdg-77w\presets\pmdg\ge90\config\AIRCRAFT.CFG"), """
            [FLTSIM.0]
            title = "PMDG 777-300ER GE90" ; Variation name
            """);

        var names = AircraftNames.Scan([community]);
        Assert.Equal(3, names.ExactCount); // "$Title Common" is a template, not a title
        Assert.Equal(("A320 CFM House", "Fenix", "Airbus"), names.Resolve("FNX_320_CFM_HOUSE"));
        Assert.Equal(("A320 easyJet", "Fenix", "AIRBUS"), names.Resolve("FNX_320_CFM_EZY"));
        Assert.Equal(("PMDG 777-300ER GE90", "PMDG Simulations", "Boeing"), names.Resolve("PMDG 777-300ER GE90"));
    }

    [Fact]
    public void Apply_keeps_the_title_and_liveries_as_keys_and_fills_only_missing_names()
    {
        var names = AircraftNames.Scan([MinimalCache()]);
        var catalog = AircraftCatalog.Group([("A320neo V2", ""), ("A320neo V2", "Lufthansa"), ("Mystery Plane", "Red")]);
        catalog.Add(new CatalogAircraft("C152 renamed", ["Default"], "My C152", "Me", null));

        var named = names.Apply(catalog);

        Assert.Equal(catalog.Select(a => a.Title), named.Select(a => a.Title));
        Assert.Equal(catalog.Select(a => a.Liveries), named.Select(a => a.Liveries));
        Assert.Equal("iniBuilds", named[0].Developer);
        Assert.Null(named[1].Developer);
        Assert.Equal("Mystery Plane", named[1].Name);
        Assert.Equal(("My C152", "Me"), (named[2].DisplayName, named[2].Developer));
    }

    [Fact]
    public void Missing_or_unreadable_folders_give_no_names_instead_of_failing()
    {
        Write(@"broken\pkg\manifest.json", "{ this is not json");
        var names = AircraftNames.Scan([Path.Combine(_root, "does-not-exist"), Path.Combine(_root, "broken")]);
        Assert.Equal(0, names.PackageCount);
        Assert.Equal(("A320neo V2", null, null), names.Resolve("A320neo V2"));
    }

    [Theory]
    [InlineData("Microsoft/iniBuilds", "iniBuilds")]
    [InlineData("Microsoft / inibuilds", "iniBuilds")]
    [InlineData("iniBuilds/Microsoft", "iniBuilds")]
    [InlineData("Asobo Studio", "Asobo")]
    [InlineData("Working Title Simulations", "Working Title")]
    [InlineData("microsoft", "Microsoft")]
    [InlineData("Microsoft / S&H Software", "S&H Software")]
    [InlineData("taog", "Taog")]
    [InlineData("  ", null)]
    public void Developers_are_normalized(string creator, string? expected) =>
        Assert.Equal(expected, AircraftNames.NormalizeDeveloper(creator));

    [Theory]
    [InlineData("", "Default")]
    [InlineData("Default", "Default")]
    [InlineData("Lufthansa", "Lufthansa")]
    [InlineData("WIZZAIR", "Wizzair")]
    [InlineData("KLM", "KLM")]
    [InlineData("A320_OFFICIAL", "A320 Official")]
    [InlineData("TT:AIRCRAFT.LIVERY.KENMORE", "Kenmore")]
    [InlineData("Volocity Microsoft (ADAC Livery)", "Volocity Microsoft (ADAC Livery)")]
    public void Livery_names_read_well(string livery, string expected) =>
        Assert.Equal(expected, AircraftNames.LiveryDisplay(livery));

    [Fact]
    public void Grouping_drops_ai_only_aircraft_and_the_duplicate_default_livery()
    {
        var g = AircraftCatalog.Group([
            ("Asobo PassiveAircraft C140", ""), ("North American T-6 Texan Passive Asobo Reno", "Livery 01"),
            ("NXCub", ""), ("NXCub", "DEFAULT"),
            ("Diamond DA40NG", ""), ("Diamond DA40NG", "TT:AIRCRAFT.LIVERY.DEFAULT"), ("Diamond DA40NG", "TT:AIRCRAFT.LIVERY.KENMORE"),
            ("A320neo V2", ""), ("A320neo V2", "Lufthansa"),
        ]);
        Assert.Equal(["A320neo V2", "Diamond DA40NG", "NXCub"], g.Select(a => a.Title));
        Assert.Equal(["Default", "Lufthansa"], g[0].Liveries);
        Assert.Equal(["TT:AIRCRAFT.LIVERY.DEFAULT", "TT:AIRCRAFT.LIVERY.KENMORE"], g[1].Liveries);
        Assert.Equal(["DEFAULT"], g[2].Liveries);
    }

    [Fact]
    public void Display_order_is_by_developer_with_unknown_last()
    {
        List<CatalogAircraft> list =
        [
            new("Zeta", ["Default"]),
            new("C172SP G1000", ["Default"], Developer: "Asobo"),
            new("A320neo V2", ["Default"], Developer: "iniBuilds"),
            new("A10", ["Default"], Developer: "Asobo"),
        ];
        Assert.Equal(["A10", "C172SP G1000", "A320neo V2", "Zeta"], AircraftCatalog.ForDisplay(list).Select(a => a.Title));
    }

    [Fact]
    public void Old_cache_files_still_load_and_new_ones_round_trip()
    {
        string old = Write("old-aircraft.json", """[{"title":"A320neo V2","liveries":["Default","Lufthansa"]},{"title":"Asobo PassiveAircraft C140","liveries":["Default"]}]""");
        var loaded = AircraftCatalog.LoadCache(old)!;
        Assert.Equal("A320neo V2", Assert.Single(loaded).Title); // AI-only entries from old caches are dropped
        Assert.Null(loaded[0].Developer);
        Assert.Equal("A320neo V2", loaded[0].Name);

        string path = Path.Combine(_root, "new-aircraft.json");
        var named = new List<CatalogAircraft> { new("A320neo V2", ["Default"], "A320neo V2", "iniBuilds", "Airbus") };
        AircraftCatalog.SaveCache(named, path);
        Assert.DoesNotContain("\"name\"", File.ReadAllText(path)); // Name is computed, not stored
        var back = AircraftCatalog.LoadCache(path)!;
        Assert.Equal(("A320neo V2", "iniBuilds", "Airbus"), (back[0].DisplayName, back[0].Developer, back[0].Manufacturer));
    }

    [Fact]
    public void Shared_catalog_matches_by_title_and_takes_names_from_whichever_pc_knew_them()
    {
        var p = new PartyAuthority("host", "Simeon", "Saturday flight");
        Assert.Equal(JoinOutcome.Accepted, p.Join("guest", "Alex", "100.64.0.2"));
        p.Apply("host", new PublishCatalog([
            new("A320neo V2", ["Default", "Lufthansa"]),                         // host could not resolve it
            new("C172SP G1000 Passengers", ["Default"], "C172SP G1000 Passengers", "Asobo", "Cessna"),
        ]));
        long before = p.State.CatalogVersion;
        p.Apply("guest", new PublishCatalog([
            new("A320neo V2", ["Lufthansa"], "A320neo V2", "iniBuilds", "Airbus"),
            new("C172SP G1000 Passengers", ["Default"], "Something else", new string('x', 500), null),
        ]));
        Assert.True(p.State.CatalogVersion > before);
        Assert.Equal(["A320neo V2", "C172SP G1000 Passengers"], p.SharedCatalog.Select(a => a.Title));
        Assert.Equal(["Lufthansa"], p.SharedCatalog[0].Liveries);
        Assert.Equal("iniBuilds", p.SharedCatalog[0].Developer);
        Assert.Equal(("C172SP G1000 Passengers", "Asobo"), (p.SharedCatalog[1].DisplayName, p.SharedCatalog[1].Developer)); // host's own first
        Assert.Null(p.Apply("host", new SetAircraft("A320neo V2", "Lufthansa"))); // still keyed by the MSFS title

        // Names arriving later (the other PC finished reading its packages) update the shared list.
        long v = p.State.CatalogVersion;
        p.Apply("host", new PublishCatalog([
            new("A320neo V2", ["Default", "Lufthansa"], "A320neo V2", "iniBuilds", "Airbus"),
            new("C172SP G1000 Passengers", ["Default"], "C172SP G1000 Passengers", "Asobo", "Cessna"),
        ]));
        Assert.Equal(v, p.State.CatalogVersion); // same result: no new version
    }

    [Fact]
    public void Guest_supplied_names_are_length_limited()
    {
        var p = new PartyAuthority("host", "Simeon", "x");
        p.Apply("host", new PublishCatalog([new("A", ["Default"], new string('n', 1000), new string('d', 1000), null)]));
        Assert.Equal(128, p.SharedCatalog[0].DisplayName!.Length);
        Assert.Equal(64, p.SharedCatalog[0].Developer!.Length);
    }

    /// <summary>
    /// MSFS 2024 pads each SIMCONNECT_RECV_ENUMERATE_SIMOBJECT_AND_LIVERY_LIST page past its last
    /// entry (seen live: 79 entries in 40 988 bytes). The entries are 512 bytes apart regardless.
    /// </summary>
    [Fact]
    public unsafe void SimConnect_aircraft_pages_are_read_with_a_fixed_stride_despite_padding()
    {
        (string, string)[] entries = [("A320neo V2", ""), ("A320neo V2", "Lufthansa"), ("C172SP G1000 Passengers", "Flight Seeing 01")];
        var page = new byte[28 + entries.Length * 512 + 512]; // trailing padding, as MSFS sends it
        void U32(int off, uint v) => BitConverter.GetBytes(v).CopyTo(page, off);
        U32(0, (uint)page.Length); U32(4, 6); U32(8, 38); U32(12, 5); U32(16, (uint)entries.Length); U32(20, 0); U32(24, 1);
        for (int i = 0; i < entries.Length; i++)
        {
            Encoding.ASCII.GetBytes(entries[i].Item1).CopyTo(page, 28 + i * 512);
            Encoding.ASCII.GetBytes(entries[i].Item2).CopyTo(page, 28 + i * 512 + 256);
        }
        var got = new List<(string Title, string Livery)>();
        fixed (byte* p = page) SimConnectBridge.ReadCatalogEntries(new Recv((IntPtr)p, (uint)page.Length), got);
        Assert.Equal(entries, got.Select(g => (g.Title, g.Livery)));

        // A page claiming more entries than it holds is rejected, not read past its end.
        U32(16, 50);
        fixed (byte* p = page)
        {
            var r = new Recv((IntPtr)p, (uint)page.Length);
            bool threw = false;
            try { SimConnectBridge.ReadCatalogEntries(r, got); } catch (InvalidDataException) { threw = true; }
            Assert.True(threw);
        }
    }
}
