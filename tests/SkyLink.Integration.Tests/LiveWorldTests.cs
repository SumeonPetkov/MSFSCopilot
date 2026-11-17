using System.Diagnostics;
using SkyLink.Core;
using SkyLink.Definitions;
using SkyLink.Sim;
using Xunit.Abstractions;

namespace SkyLink.Integration.Tests;

/// <summary>
/// Shared-world tests against the running MSFS 2024.
///   SKYLINK_LIVE=1          read-only: scan the AI traffic (as the Captain does), read time/rate/pause/weather.
///   SKYLINK_LIVE_TRAFFIC=1  CHANGES THE SIM: creates ONE AI aircraft (a copy of the loaded aircraft)
///                           300 m ahead of the user aircraft, moves it for 10 s as a mirrored
///                           aircraft, checks it is not counted as foreign traffic, then removes it
///                           and checks it is gone. The user aircraft is never touched.
/// </summary>
public sealed class LiveWorldTests(ITestOutputHelper output)
{
    private static void PumpFor(SimConnectBridge b, Stopwatch clock, int ms, Func<bool>? until = null)
    {
        long end = clock.ElapsedMilliseconds + ms;
        while (clock.ElapsedMilliseconds < end && until?.Invoke() != true)
        {
            b.Pump(clock.ElapsedMilliseconds + 1);
            b.DataAvailable.WaitOne(10);
        }
    }

    [Fact]
    public void Captain_scan_reads_traffic_and_world_state()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_LIVE") != "1")
        {
            output.WriteLine("skipped: set SKYLINK_LIVE=1 with MSFS 2024 running");
            return;
        }
        using var b = new SimConnectBridge(DefinitionCatalog.Load(Cli.Program.FindDefinitions()));
        b.Log += m => output.WriteLine("  " + m);
        int scans = 0, objects = 0;
        var identities = new Dictionary<uint, TrafficIdentity>();
        b.TrafficScanned += (_, list) => { scans++; objects = Math.Max(objects, list.Count); };
        b.TrafficIdentified += (id, i) => identities[id] = i;
        var clock = Stopwatch.StartNew();
        b.Execute(new SetWorldRole(WorldRole.Source), 1);
        PumpFor(b, clock, 12_000);
        Assert.True(b.IsConnected, "MSFS is not running");
        Assert.NotEqual(0u, b.UserObjectId);
        Assert.True(scans >= 20, $"only {scans} scans completed in 12 s");
        Assert.NotNull(b.World.Local);
        Assert.DoesNotContain(identities.Keys, id => id == b.UserObjectId);
        output.WriteLine($"{scans} scans, up to {objects} AI aircraft, {identities.Count} identified; world {b.World.Local}");
        foreach (var (id, i) in identities.Take(20)) output.WriteLine($"  {id}: '{i.Title}' livery '{i.Livery}' {i.AtcId} {i.Airline} {i.FlightNumber}");
    }

    [Fact]
    public void One_mirrored_aircraft_is_created_moved_and_removed()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_LIVE_TRAFFIC") != "1")
        {
            output.WriteLine("skipped: set SKYLINK_LIVE_TRAFFIC=1 with MSFS 2024 running and the user in flight (creates and removes one AI aircraft)");
            return;
        }
        using var b = new SimConnectBridge(DefinitionCatalog.Load(Cli.Program.FindDefinitions()));
        b.Log += m => output.WriteLine("  " + m);
        var clock = Stopwatch.StartNew();
        PumpFor(b, clock, 15_000, () => b.LatestFlight is not null && b.AircraftTitle is not null && b.UserObjectId != 0);
        Assert.NotNull(b.LatestFlight);
        var me = b.LatestFlight!.Value;
        string title = b.AircraftTitle!;

        // Pretend to be the FO: the "Captain" has one aircraft 300 m ahead, flying the user's heading at 20 kt.
        b.Execute(new SetWorldRole(WorldRole.Mirror), clock.ElapsedMilliseconds);
        var identity = new TrafficIdentity(TrafficKind.Airplane, title, "", "SKYTEST", "", "", "");
        b.Execute(new MirrorTraffic(new TrafficAdded([new TrafficInfo(1, identity)])), clock.ElapsedMilliseconds);
        double hdg = me.HeadingDeg * Math.PI / 180, fps = 20 / 0.592484;
        var start = FlightFollower.Extrapolate(me with { VWorldEastFps = Math.Sin(hdg) * 300 / 0.3048, VWorldNorthFps = Math.Cos(hdg) * 300 / 0.3048, VWorldUpFps = 0 }, 1)
            with { VWorldEastFps = Math.Sin(hdg) * fps, VWorldNorthFps = Math.Cos(hdg) * fps, VWorldUpFps = 0 };
        ulong seq = 0;
        var clockStart = clock.ElapsedMilliseconds;
        var seenObjects = new List<uint>();
        b.TrafficScanned += (_, list) => seenObjects = [.. list.Select(o => o.Id)];
        try
        {
            long until = clock.ElapsedMilliseconds + 12_000;
            while (clock.ElapsedMilliseconds < until)
            {
                long now = clock.ElapsedMilliseconds;
                var pose = FlightFollower.Extrapolate(start, (now - clockStart) / 1000.0);
                b.Execute(new MirrorTraffic(new TrafficFrame(++seq, now, [new TrafficEntry(1, 0, new TrafficSample(pose, 0x3, true))])), now);
                PumpFor(b, clock, 200);
            }
            Assert.Equal(1, b.World.TrafficMirrored);
            Assert.Equal(0, b.World.ForeignTraffic);
            output.WriteLine($"created and moved '{title}' as a mirrored aircraft; foreign traffic here: {b.World.ForeignTraffic} (scan saw {seenObjects.Count} AI objects)");
        }
        finally
        {
            b.Execute(new ClearTraffic(), clock.ElapsedMilliseconds);
            b.Execute(new SetWorldRole(WorldRole.None), clock.ElapsedMilliseconds);
            PumpFor(b, clock, 1500);
        }
        Assert.Equal(0, b.World.TrafficMirrored);

        // Really gone from MSFS: scan as the Captain would and look for its tail number.
        var tails = new List<string>();
        b.TrafficIdentified += (_, i) => tails.Add(i.AtcId);
        b.Execute(new SetWorldRole(WorldRole.Source), clock.ElapsedMilliseconds);
        PumpFor(b, clock, 4000);
        b.Execute(new SetWorldRole(WorldRole.None), clock.ElapsedMilliseconds);
        Assert.DoesNotContain("SKYTEST", tails);
        Assert.False(File.Exists(SimConnectBridge.LedgerPath), "the cleanup ledger must be gone once everything was removed");
    }
}
