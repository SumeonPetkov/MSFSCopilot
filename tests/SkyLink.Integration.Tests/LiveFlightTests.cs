using System.Diagnostics;
using System.Net.Sockets;
using SkyLink.Core;
using SkyLink.Definitions;
using SkyLink.Net;
using SkyLink.Session;
using SkyLink.Sim;
using Xunit.Abstractions;

namespace SkyLink.Integration.Tests;

/// <summary>
/// Flight-model tests against the running MSFS 2024.
///   SKYLINK_LIVE=1         read-only: the live aircraft flies, a fake seat follows it.
///   SKYLINK_LIVE_FOLLOW=1  MOVES THE AIRCRAFT: the live aircraft is frozen and follows a fake
///                          pilot flying that sits exactly on it, turns it 5° and back, then is
///                          released where it started. Always unfrozen at the end.
/// </summary>
public sealed class LiveFlightTests(ITestOutputHelper output)
{
    private static (DefinitionCatalog Catalog, AircraftDefinition Def, FlightSample Here) Probe()
    {
        var catalog = DefinitionCatalog.Load(Cli.Program.FindDefinitions());
        using var probe = new SimConnectBridge(catalog);
        var sw = Stopwatch.StartNew();
        while ((probe.Definition is null || probe.LatestFlight is null) && sw.ElapsedMilliseconds < 15_000)
        {
            probe.Pump(sw.ElapsedMilliseconds + 1);
            Thread.Sleep(20);
        }
        Assert.NotNull(probe.Definition);
        Assert.NotNull(probe.LatestFlight);
        return (catalog, probe.Definition!, probe.LatestFlight!.Value);
    }

    private static int FreePort()
    {
        using var u = new UdpClient(0);
        return ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
    }

    private static void RunFor(int ms, params SessionRunner[] runners)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            foreach (var r in runners) r.Step();
            Thread.Sleep(1);
        }
    }

    [Fact]
    public void Fake_seat_follows_the_live_aircraft()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_LIVE") != "1")
        {
            output.WriteLine("skipped: set SKYLINK_LIVE=1 with MSFS 2024 running");
            return;
        }
        var (catalog, def, here) = Probe();
        output.WriteLine($"live aircraft: {here.LatDeg:F6}, {here.LonDeg:F6}, {here.AltFt:F0} ft, hdg {here.HeadingDeg:F1}, pitch {here.PitchDeg:F1}, bank {here.BankDeg:F1}, on ground {here.OnGround}");
        Assert.InRange(here.LatDeg, -90, 90);
        Assert.InRange(here.LonDeg, -180, 180);
        Assert.InRange(here.HeadingDeg, 0, 360);

        int port = FreePort();
        var opts = new LinkOptions { SessionCode = "LIVEF1", Port = port };
        var real = new SimConnectBridge(catalog);
        var fake = new FakeSimBridge(def, 3);
        using var host = new SessionRunner(real, Link.Host(opts));
        using var join = new SessionRunner(fake, Link.Join($"127.0.0.1:{port}", opts));
        RunFor(8000, host, join);

        var live = real.LatestFlight!.Value;
        double speed = Math.Sqrt(live.VWorldEastFps * live.VWorldEastFps + live.VWorldNorthFps * live.VWorldNorthFps + live.VWorldUpFps * live.VWorldUpFps) * 0.3048;
        double dist = FlightFollower.DistanceM(live, fake.Pose);
        long sent = host.Engine!.FlightStatesSent;
        output.WriteLine($"flight states sent {sent} (~{sent / 8.0:F0}/s), applied by the fake seat {join.Engine!.FlightStatesApplied}; live speed {speed:F1} m/s; fake seat {dist:F2} m from the live aircraft");
        Assert.False(real.IsFollowing, "the live aircraft must not be frozen in this test");
        Assert.InRange(sent / 8.0, 20, 40);
        Assert.True(join.Engine.FlightStatesApplied > 100);
        // The follower renders ~100-150 ms behind; allow that much travel plus a few metres.
        Assert.True(dist < speed * 0.3 + 3, $"fake seat {dist:F1} m away at {speed:F1} m/s");
    }

    [Fact]
    public void Live_aircraft_follows_and_is_released_where_it_started()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_LIVE_FOLLOW") != "1")
        {
            output.WriteLine("skipped: set SKYLINK_LIVE_FOLLOW=1 with MSFS 2024 running (freezes the aircraft, turns it 5° and back, releases it)");
            return;
        }
        var (catalog, def, here) = Probe();
        output.WriteLine($"start: {here.LatDeg:F6}, {here.LonDeg:F6}, {here.AltFt:F1} ft, hdg {here.HeadingDeg:F2}");

        int port = FreePort();
        var opts = new LinkOptions { SessionCode = "LIVEF2", Port = port };
        // The fake seat hosts and flies a stationary aircraft exactly where the live one is.
        var stationary = here with { VWorldEastFps = 0, VWorldUpFps = 0, VWorldNorthFps = 0, VBodyXFps = 0, VBodyYFps = 0, VBodyZFps = 0, RotBodyXDps = 0, RotBodyYDps = 0, RotBodyZDps = 0 };
        var fake = new FakeSimBridge(def, 4) { TurnRateDps = 0, Pose = stationary };
        var real = new SimConnectBridge(catalog);
        using var host = new SessionRunner(fake, Link.Host(opts));
        var join = new SessionRunner(real, Link.Join($"127.0.0.1:{port}", opts));
        try
        {
            RunFor(5000, host, join);
            Assert.True(real.IsFollowing, "live aircraft did not start following");
            double held = FlightFollower.DistanceM(here, real.LatestFlight!.Value);
            output.WriteLine($"frozen and following; {held:F2} m from where it started");

            fake.Pose = stationary with { HeadingDeg = (here.HeadingDeg + 5) % 360 };
            RunFor(3000, host, join);
            double turned = FlightFollower.AngleDiff(here.HeadingDeg, real.LatestFlight!.Value.HeadingDeg);
            output.WriteLine($"turned by {turned:F2}° (expected 5)");

            fake.Pose = stationary;
            RunFor(3000, host, join);
            join.TakeControl();
            RunFor(1500, host, join);

            var end = real.LatestFlight!.Value;
            output.WriteLine($"released: {FlightFollower.DistanceM(here, end):F2} m and {FlightFollower.AngleDiff(here.HeadingDeg, end.HeadingDeg):F2}° from the start; following={real.IsFollowing}");
            Assert.InRange(turned, 4.5, 5.5);
            Assert.False(real.IsFollowing, "still frozen after taking the controls");
            Assert.True(FlightFollower.DistanceM(here, end) < 3, "not released where it started");
        }
        finally
        {
            join.Dispose(); // unfreezes the aircraft whatever happened above
        }
    }
}
