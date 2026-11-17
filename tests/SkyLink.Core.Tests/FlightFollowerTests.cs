using SkyLink.Core;
using Xunit.Abstractions;

namespace SkyLink.Core.Tests;

public sealed class FlightFollowerTests(ITestOutputHelper output)
{
    private const double FtToM = 0.3048;

    /// <summary>
    /// A rate-one-ish climbing turn: 150 kt, 25° bank, +1000 ft/min, starting heading 350° so
    /// the heading crosses 360/0 during the test.
    /// </summary>
    private static FlightSample Truth(double t)
    {
        const double v = 77.2, bankDeg = 25, startHdg = 350;
        double radius = v * v / (9.80665 * Math.Tan(bankDeg * Math.PI / 180));
        double omega = v / radius;
        double hdg0 = startHdg * Math.PI / 180;
        double hdg = hdg0 + omega * t;
        // Integrate the circle exactly: position relative to the start point.
        double north = radius * (Math.Sin(hdg) - Math.Sin(hdg0));
        double east = radius * (Math.Cos(hdg0) - Math.Cos(hdg));
        const double lat0 = 47.45, lon0 = -122.31;
        double lat = lat0 + north / 6_371_008.8 * 180 / Math.PI;
        double lon = lon0 + east / (6_371_008.8 * Math.Cos(lat0 * Math.PI / 180)) * 180 / Math.PI;
        double climbFps = 1000.0 / 60;
        return new FlightSample(lat, lon, 5000 + climbFps * t, -3, bankDeg, (hdg * 180 / Math.PI) % 360,
            v * Math.Sin(hdg) / FtToM, climbFps, v * Math.Cos(hdg) / FtToM,
            0, 0, v / FtToM, 0, omega * 180 / Math.PI, 0, false);
    }

    [Theory]
    [InlineData(1, 0.0, 30, 30)]      // perfect network
    [InlineData(2, 0.2, 40, 160)]     // 20% loss, 40-160 ms latency (reordering)
    [InlineData(3, 0.35, 20, 220)]    // very bad network
    public void Tracks_a_climbing_turn_smoothly(int seed, double loss, int latMin, int latMax)
    {
        var rng = new Random(seed);
        var f = new FlightFollower(new FollowerOptions { BufferMs = 120 });
        const long remoteClock0 = 3_600_000; // the pilot flying's clock is an hour ahead
        var inflight = new List<(long ArriveLocal, long RemoteMs, FlightSample S)>();
        double maxPosErr = 0, maxHdgErr = 0, maxJumpM = 0;
        FlightSample? previous = null;

        for (long local = 0; local <= 20_000; local++)
        {
            if (local % 33 == 0 && rng.NextDouble() >= loss)
                inflight.Add((local + rng.Next(latMin, latMax + 1), remoteClock0 + local, Truth(local / 1000.0)));
            foreach (var p in inflight.Where(p => p.ArriveLocal == local).ToList())
            {
                inflight.Remove(p);
                f.Add(p.RemoteMs, p.S, local);
            }
            if (local % 16 != 0 || local < 2000) continue; // render at ~60 fps after warm-up

            var pose = f.Sample(local)!.Value;
            if (previous is { } prev) maxJumpM = Math.Max(maxJumpM, FlightFollower.DistanceM(prev, pose));
            previous = pose;

            // Where the follower should be: the truth at its render time on the sender's clock.
            var truth = Truth((f.RenderRemoteTime(local) - remoteClock0) / 1000.0);
            maxPosErr = Math.Max(maxPosErr, FlightFollower.DistanceM(truth, pose));
            maxHdgErr = Math.Max(maxHdgErr, Math.Abs(FlightFollower.AngleDiff(truth.HeadingDeg, pose.HeadingDeg)));
        }

        output.WriteLine($"max position error {maxPosErr:F2} m, max heading error {maxHdgErr:F3}°, largest frame-to-frame move {maxJumpM:F2} m (77 m/s ≈ 1.24 m per 16 ms)");
        Assert.True(maxPosErr < 1.5, $"position error {maxPosErr:F2} m");
        Assert.True(maxHdgErr < 0.5, $"heading error {maxHdgErr:F3}°");
        Assert.True(maxJumpM < 2.0, $"a frame jumped {maxJumpM:F2} m (stutter)");
    }

    [Fact]
    public void A_handover_discontinuity_is_eased_not_snapped()
    {
        // The old pilot flying was ~30 m ahead of the stream it now follows.
        var f = new FlightFollower(new FollowerOptions { BufferMs = 100 });
        var truth0 = Truth(0);
        var ahead = FlightFollower.Extrapolate(truth0, 30 / 77.2);
        f.SeedOutput(ahead, 0);

        double maxMove = 0;
        FlightSample? prev = ahead;
        for (long t = 0; t <= 3000; t++)
        {
            if (t % 33 == 0) f.Add(t, Truth(t / 1000.0), t + 40);
            if (t % 16 != 0) continue;
            var pose = f.Sample(t)!.Value;
            if (prev is { } p) maxMove = Math.Max(maxMove, FlightFollower.DistanceM(p, pose));
            prev = pose;
        }
        var settled = f.Sample(3000)!.Value;
        var expect = Truth((f.RenderRemoteTime(3000)) / 1000.0);
        output.WriteLine($"largest frame move during the blend {maxMove:F2} m; error after settling {FlightFollower.DistanceM(expect, settled):F3} m");
        Assert.True(maxMove < 2.6, $"handover snapped: {maxMove:F2} m in one frame");
        Assert.True(FlightFollower.DistanceM(expect, settled) < 0.1);
        Assert.True(f.BlendsStarted >= 1);
    }

    [Fact]
    public void Joining_far_away_snaps_once_instead_of_flying_across_the_map()
    {
        var f = new FlightFollower();
        f.SeedOutput(Truth(0) with { LatDeg = 10, LonDeg = 10 }, 0); // the joiner spawned elsewhere
        for (long t = 0; t <= 500; t += 33) f.Add(t, Truth(t / 1000.0), t + 30);
        var pose = f.Sample(500)!.Value;
        Assert.True(FlightFollower.DistanceM(Truth(0.4), pose) < 100);
        Assert.Equal(0, f.BlendsStarted);
    }

    [Fact]
    public void Heading_interpolates_through_north_not_the_long_way_round()
    {
        var a = Truth(0) with { HeadingDeg = 359 };
        var b = Truth(0) with { HeadingDeg = 1 };
        var mid = FlightFollower.Interpolate(a, b, 0.033, 0.5);
        Assert.True(mid.HeadingDeg is < 0.01 or > 359.99, $"got {mid.HeadingDeg}");
    }

    [Fact]
    public void Extrapolates_briefly_then_holds_when_samples_stop()
    {
        var f = new FlightFollower(new FollowerOptions { BufferMs = 100, MaxExtrapolationMs = 500 });
        for (long t = 0; t <= 1000; t += 33) f.Add(t, Truth(t / 1000.0), t + 50);

        var at600 = f.Sample(1050 + 150 + 300)!.Value;  // 300 ms past the last sample
        var at2000 = f.Sample(1050 + 150 + 2000)!.Value; // far past: held at +500 ms
        var at4000 = f.Sample(1050 + 150 + 4000)!.Value;
        Assert.True(FlightFollower.DistanceM(at2000, at4000) < 0.01, "kept moving after the extrapolation limit");
        Assert.True(FlightFollower.DistanceM(Truth(0.99), at600) > 10, "did not extrapolate at all");
    }

    [Fact]
    public void Duplicates_and_out_of_order_samples_are_handled()
    {
        var f = new FlightFollower();
        f.Add(100, Truth(0.1), 150);
        f.Add(66, Truth(0.066), 151);
        f.Add(100, Truth(0.1), 152);
        f.Add(133, Truth(0.133), 183);
        Assert.Equal(3, f.Count);
        Assert.Equal(1, f.SamplesOutOfOrder);
    }

    [Fact]
    public void Engine_streams_flight_state_only_from_the_pilot_flying()
    {
        var schema = new SyncSchema([new GroupSpec("g", 1)], [new VarSpec("x", SyncClass.State, "g")]);
        var p = new Pair(schema);
        p.Connect();
        p.Run(1500);

        for (int i = 0; i < 30; i++)
        {
            p.Host.OnFlightSample(Truth(i * 0.033), p.Now);
            p.Join.OnFlightSample(Truth(99), p.Now); // monitoring seat: must be ignored
            p.Advance(33);
        }
        var received = p.JoinCmds.OfType<FlightStateUpdate>().ToList();
        Assert.InRange(received.Count, 25, 31);
        Assert.Empty(p.HostCmds.OfType<FlightStateUpdate>());

        // After a handover the stream reverses.
        p.Join.TakeControl(p.Now);
        p.Run(100);
        int before = p.HostCmds.OfType<FlightStateUpdate>().Count();
        for (int i = 0; i < 10; i++)
        {
            p.Join.OnFlightSample(Truth(i * 0.033), p.Now);
            p.Host.OnFlightSample(Truth(99), p.Now);
            p.Advance(33);
        }
        Assert.True(p.HostCmds.OfType<FlightStateUpdate>().Count() - before >= 8);
        Assert.Equal(received.Count, p.JoinCmds.OfType<FlightStateUpdate>().Count(c => c.Sample.LatDeg != Truth(99).LatDeg));
    }

    [Fact]
    public void Flight_state_round_trips_through_the_codec()
    {
        var m = new FlightState(new Authority(3, 1), 42, 1_700_000_000_123, Truth(1.5) with { OnGround = true });
        var back = (FlightState)MessageCodec.Decode(MessageCodec.Encode(m));
        Assert.Equal(m, back);
    }
}
