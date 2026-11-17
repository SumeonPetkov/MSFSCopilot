using System.Xml.Linq;
using SkyLink.Lobby;

namespace SkyLink.Integration.Tests;

public sealed class LobbyPiecesTests
{
    [Fact]
    public void SimBrief_ofp_is_parsed_and_the_msfs_plan_link_found()
    {
        var (plan, pln) = SimBrief.Parse(SimBriefSample.Json, "Alex", DateTimeOffset.UnixEpoch);
        Assert.Equal(("EDDF", "LFPG", "DLH4AB", "25C"), (plan.Origin, plan.Destination, plan.Callsign, plan.DepartureRunway));
        Assert.Equal(36000, plan.CruiseAltitudeFt);
        Assert.Equal(50.033333, plan.OriginLat!.Value, 6);
        Assert.Equal(142, plan.Passengers);
        Assert.Equal("https://www.simbrief.com/ofp/flightplans/EDDFLFPG_MFS_1.pln", pln!.ToString());
    }

    [Fact]
    public void SimBrief_errors_are_reported_not_swallowed()
    {
        var e = Assert.Throws<InvalidOperationException>(() => SimBrief.Parse("""{ "fetch": { "status": "Error: Unknown UserID" } }""", "x", DateTimeOffset.UnixEpoch));
        Assert.Contains("Unknown UserID", e.Message);
        Assert.Contains("username=simeon%20p", SimBrief.LatestOfpUri("simeon p").AbsoluteUri);
        Assert.Contains("userid=123456", SimBrief.LatestOfpUri("123456").ToString());
    }

    [Theory]
    [InlineData(50.033333, 'N', 'S', "N50° 2' 0.00\"")] // 1' 59.9988" rounds up cleanly
    [InlineData(50.5, 'N', 'S', "N50° 30' 0.00\"")]
    [InlineData(-33.946111, 'N', 'S', "S33° 56' 46.00\"")]
    [InlineData(-0.461389, 'E', 'W', "W0° 27' 41.00\"")]
    public void Coordinates_use_msfs_degree_minute_second_format(double deg, char pos, char neg, string expected)
        => Assert.Equal(expected, FlightFiles.Dms(deg, pos, neg));

    [Fact]
    public void Flight_file_carries_aircraft_livery_position_callsign_and_plan()
    {
        var plan = SimBrief.Parse(SimBriefSample.Json, "Alex", DateTimeOffset.UnixEpoch).Plan;
        var setup = FlightSetup.Empty with { AircraftTitle = "A320neo V2", Livery = "Lufthansa", Callsign = "DLH4AB", Plan = plan };
        var start = FlightFiles.ChooseStart(setup, null)!;
        Assert.Equal(250, start.HeadingDeg);
        string dir = Path.Combine(Path.GetTempPath(), "skylink-flt-" + Guid.NewGuid().ToString("N"));
        try
        {
            var written = FlightFiles.Write("PARTY1", setup, start, dir);
            string flt = File.ReadAllText(written.FltPath);
            Assert.Contains("Sim=A320neo V2", flt);
            Assert.Contains("Livery=Lufthansa", flt);
            Assert.Contains("Latitude=N50° 2' 0.00\"", flt);
            Assert.Contains("TailNumber=DLH4AB", flt);
            Assert.Contains($"path={written.PlnPath}", flt);
            var pln = XDocument.Load(written.PlnPath!); // SimBrief's .pln is absent here: SkyLink writes a direct one
            Assert.Equal("EDDF", pln.Descendants("DepartureID").Single().Value);
            Assert.Equal("LFPG", pln.Descendants("DestinationID").Single().Value);
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void Without_a_plan_the_flight_starts_where_the_captain_is()
    {
        var here = new StartPosition(47.45, -122.31, 433, 160, true, "Captain's position");
        Assert.Equal(here, FlightFiles.ChooseStart(FlightSetup.Empty with { AircraftTitle = "x" }, here));
        Assert.Null(FlightFiles.ChooseStart(FlightSetup.Empty, null));
    }

    [Fact]
    public void Catalog_groups_liveries_and_names_the_unnamed_one_default()
    {
        var g = AircraftCatalog.Group([("A320neo V2", "Lufthansa"), ("A320neo V2", ""), ("A320neo V2", "Lufthansa"), ("Cessna 172", "White")]);
        Assert.Equal(["A320neo V2", "Cessna 172"], g.Select(a => a.Title));
        Assert.Equal(["Default", "Lufthansa"], g[0].Liveries);
    }
}
