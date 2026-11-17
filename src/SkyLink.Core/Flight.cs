namespace SkyLink.Core;

/// <summary>
/// The aircraft's position and motion, as read from the pilot flying's sim every frame.
/// Units are SimConnect's: degrees, feet, feet per second, degrees per second.
/// World velocity: X east, Y up, Z north. Body velocity/rotation: the aircraft's own axes.
/// </summary>
public readonly record struct FlightSample(
    double LatDeg,
    double LonDeg,
    double AltFt,
    double PitchDeg,
    double BankDeg,
    double HeadingDeg,
    double VWorldEastFps,
    double VWorldUpFps,
    double VWorldNorthFps,
    double VBodyXFps,
    double VBodyYFps,
    double VBodyZFps,
    double RotBodyXDps,
    double RotBodyYDps,
    double RotBodyZDps,
    bool OnGround);

public sealed class FollowerOptions
{
    /// <summary>How far behind the newest sample the monitoring aircraft renders (absorbs network jitter).</summary>
    public int BufferMs { get; init; } = 100;
    /// <summary>After the last sample, keep moving along the last velocity for at most this long, then hold.</summary>
    public int MaxExtrapolationMs { get; init; } = 500;
    /// <summary>Samples older than this behind the render time are dropped.</summary>
    public int HistoryMs { get; init; } = 2000;
    /// <summary>Window for the minimum one-way-delay estimate (absorbs slow clock drift).</summary>
    public int OffsetWindow { get; init; } = 90;
}

/// <summary>
/// Turns a jittery, lossy, possibly reordered stream of <see cref="FlightSample"/>s (stamped
/// with the pilot flying's clock) into a smooth pose for every local frame: Hermite
/// interpolation of position using the world velocities, wrap-aware interpolation of attitude,
/// bounded extrapolation when samples stop. Pure; the caller supplies both clocks.
/// </summary>
public sealed class FlightFollower
{
    private const double EarthRadiusM = 6_371_008.8;
    private const double FtToM = 0.3048;

    private readonly FollowerOptions _o;
    private readonly List<(long RemoteMs, FlightSample S)> _samples = [];
    private readonly Queue<long> _delays = new();
    private long _minDelay = long.MaxValue;
    // The delay actually used for rendering. It follows _minDelay but is slewed so the rendered
    // timeline never runs more than ±10% off real time (a step would show as a lurch).
    private double _appliedDelay = double.NaN;
    private long _lastRenderAt;
    private const double MaxSlew = 0.10;
    private const double SnapIfOffMs = 1000;

    public FlightFollower(FollowerOptions? options = null) => _o = options ?? new FollowerOptions();

    public int Count => _samples.Count;
    public bool HasData => _samples.Count > 0;
    public long SamplesReceived { get; private set; }
    public long SamplesOutOfOrder { get; private set; }

    public void Reset()
    {
        _samples.Clear();
        _delays.Clear();
        _minDelay = long.MaxValue;
        _appliedDelay = double.NaN;
        _lastOut = null;
        _lastRaw = null;
        _blend = null;
        _rawHeld = _lastHeld = _lastRawHeld = false;
    }

    /// <summary>A sample stamped <paramref name="remoteMs"/> by the sender, received at local <paramref name="localNow"/>.</summary>
    public void Add(long remoteMs, FlightSample s, long localNow)
    {
        SamplesReceived++;
        // Remote-to-local clock mapping: the smallest observed (receive - send) over a window is
        // the clock offset plus the fastest one-way delay; jitter only ever makes it larger.
        long delay = localNow - remoteMs;
        _delays.Enqueue(delay);
        if (_delays.Count > _o.OffsetWindow) _delays.Dequeue();
        _minDelay = _delays.Min();

        int i = _samples.Count;
        while (i > 0 && _samples[i - 1].RemoteMs > remoteMs) i--;
        if (i > 0 && _samples[i - 1].RemoteMs == remoteMs) return; // duplicate
        if (i < _samples.Count) SamplesOutOfOrder++;
        _samples.Insert(i, (remoteMs, s));
    }

    /// <summary>
    /// The pose to show at local time <paramref name="localNow"/>, or null before anything is known.
    /// Discontinuities (a handover, a reconnect after a gap) are eased out over
    /// <see cref="BlendMs"/> instead of snapping; only a jump beyond <see cref="SnapBeyondM"/>
    /// (joining a session somewhere else entirely) is applied at once.
    /// </summary>
    public FlightSample? Sample(long localNow)
    {
        var raw = Raw(localNow);
        if (raw is null) return _lastOut; // seeded but no samples yet: hold where we are

        // A discontinuity is a jump in the incoming stream itself (a handover seed, resuming after
        // a gap, a late correction), never in our eased output: while easing, the output
        // legitimately moves faster than the aircraft does.
        bool jumped = false;
        if (!_rawHeld && _lastOut is not null)
        {
            if (_lastRaw is { } lr)
            {
                var predicted = _lastRawHeld ? lr : Extrapolate(lr, Math.Max(0, localNow - _lastRawAt) / 1000.0);
                jumped = DistanceM(predicted, raw.Value) > BlendThresholdM;
            }
            else
            {
                jumped = true; // first stream pose after SeedOutput
            }
        }
        if (jumped && _lastOut is { } last)
        {
            // A held/seeded pose is stationary even though it may carry a velocity.
            var expected = _lastHeld ? last : Extrapolate(last, Math.Max(0, localNow - _lastOutAt) / 1000.0);
            double dev = DistanceM(expected, raw.Value);
            if (dev >= SnapBeyondM)
            {
                _blend = null; // somewhere else entirely (joining): go there at once
            }
            else if (dev > BlendThresholdM)
            {
                _blend = Offset(expected, raw.Value);
                _blendStart = localNow;
                BlendsStarted++;
            }
        }

        var current = ApplyBlend(raw.Value, localNow);
        _lastRaw = raw;
        _lastRawAt = localNow;
        _lastRawHeld = _rawHeld;
        _lastOut = current;
        _lastOutAt = localNow;
        _lastHeld = _rawHeld && _blend is null;
        return current;
    }

    private FlightSample? _lastRaw;
    private long _lastRawAt;
    private bool _lastRawHeld;

    /// <summary>True while the stream has stopped for longer than the extrapolation limit.</summary>
    public bool IsHolding => _rawHeld;

    private bool _rawHeld;
    private bool _lastHeld;

    public const int BlendMs = 1000;
    public const double BlendThresholdM = 0.5;
    public const double SnapBeyondM = 2000;
    public long BlendsStarted { get; private set; }

    /// <summary>
    /// Start following from where the local aircraft is now (e.g. just after handing over the
    /// controls), so the switch to the remote stream is eased instead of snapped.
    /// </summary>
    public void SeedOutput(FlightSample localPose, long localNow)
    {
        _lastOut = localPose;
        _lastOutAt = localNow;
        _lastRaw = null;
        _blend = null;
        _lastHeld = true; // the local aircraft is frozen at this pose until the stream arrives
    }

    private FlightSample? _lastOut;
    private long _lastOutAt;
    private (double N, double E, double U, double Hdg, double Pitch, double Bank)? _blend;
    private long _blendStart;

    /// <summary>a relative to b: metres north/east/up and degrees.</summary>
    private static (double, double, double, double, double, double) Offset(in FlightSample a, in FlightSample b)
    {
        double cosLat = Math.Cos(b.LatDeg * Math.PI / 180);
        return ((a.LatDeg - b.LatDeg) * Math.PI / 180 * EarthRadiusM,
                WrapDeg(a.LonDeg - b.LonDeg) * Math.PI / 180 * EarthRadiusM * cosLat,
                (a.AltFt - b.AltFt) * FtToM,
                AngleDiff(b.HeadingDeg, a.HeadingDeg),
                a.PitchDeg - b.PitchDeg,
                a.BankDeg - b.BankDeg);
    }

    private FlightSample ApplyBlend(FlightSample raw, long localNow)
    {
        if (_blend is not { } o) return raw;
        double k = 1 - (localNow - _blendStart) / (double)BlendMs;
        if (k <= 0)
        {
            _blend = null;
            return raw;
        }
        k = k * k * (3 - 2 * k); // smoothstep: eases in and out
        double cosLat = Math.Cos(raw.LatDeg * Math.PI / 180);
        return raw with
        {
            LatDeg = raw.LatDeg + o.N * k / EarthRadiusM * 180 / Math.PI,
            LonDeg = WrapLon(raw.LonDeg + o.E * k / (EarthRadiusM * cosLat) * 180 / Math.PI),
            AltFt = raw.AltFt + o.U * k / FtToM,
            HeadingDeg = Wrap360(raw.HeadingDeg + o.Hdg * k),
            PitchDeg = raw.PitchDeg + o.Pitch * k,
            BankDeg = raw.BankDeg + o.Bank * k,
        };
    }

    private FlightSample? Raw(long localNow)
    {
        _rawHeld = false;
        if (_samples.Count == 0) return null;
        UpdateAppliedDelay(localNow);
        double renderRemote = RenderRemoteTime(localNow);

        // Drop history we can no longer need (keep one sample before the render time).
        while (_samples.Count > 2 && _samples[1].RemoteMs < renderRemote - _o.HistoryMs) _samples.RemoveAt(0);

        var first = _samples[0];
        if (renderRemote <= first.RemoteMs) return first.S;

        for (int i = 1; i < _samples.Count; i++)
        {
            var b = _samples[i];
            if (b.RemoteMs < renderRemote) continue;
            var a = _samples[i - 1];
            return Interpolate(a.S, b.S, (b.RemoteMs - a.RemoteMs) / 1000.0, (renderRemote - a.RemoteMs) / (b.RemoteMs - a.RemoteMs));
        }

        var last = _samples[^1];
        _rawHeld = renderRemote - last.RemoteMs > _o.MaxExtrapolationMs;
        double ahead = Math.Min(renderRemote - last.RemoteMs, _o.MaxExtrapolationMs) / 1000.0;
        var extrapolated = Extrapolate(last.S, ahead);
        if (_samples.Count < 2) return extrapolated;

        // Keep turning/rolling/pitching at the recent rate, so a gap in a turn doesn't straighten out.
        var prev = _samples[^2];
        double span = (last.RemoteMs - prev.RemoteMs) / 1000.0;
        if (span <= 0 || span > 0.5) return extrapolated;
        double R(double from, double to) => AngleDiff(from, to) / span * ahead;
        return extrapolated with
        {
            HeadingDeg = Wrap360(last.S.HeadingDeg + R(prev.S.HeadingDeg, last.S.HeadingDeg)),
            BankDeg = last.S.BankDeg + R(prev.S.BankDeg, last.S.BankDeg),
            PitchDeg = last.S.PitchDeg + R(prev.S.PitchDeg, last.S.PitchDeg),
        };
    }

    private void UpdateAppliedDelay(long localNow)
    {
        if (double.IsNaN(_appliedDelay) || Math.Abs(_appliedDelay - _minDelay) > SnapIfOffMs)
        {
            _appliedDelay = _minDelay; // first sample, or the sender restarted: jump once
        }
        else
        {
            double dt = Math.Max(0, localNow - _lastRenderAt);
            _appliedDelay += Math.Clamp(_minDelay - _appliedDelay, -MaxSlew * dt, MaxSlew * dt);
        }
        _lastRenderAt = localNow;
    }

    /// <summary>The sender-clock time that <see cref="Sample"/> renders at local time <paramref name="localNow"/>.</summary>
    public double RenderRemoteTime(long localNow) =>
        localNow - (double.IsNaN(_appliedDelay) ? _minDelay : _appliedDelay) - _o.BufferMs;

    /// <summary>How far behind the newest received sample the current render point is (ms); for diagnostics.</summary>
    public double LagMs(long localNow) => _samples.Count == 0 ? 0 : _samples[^1].RemoteMs - RenderRemoteTime(localNow);

    public static FlightSample Interpolate(in FlightSample a, in FlightSample b, double dtSec, double s)
    {
        s = Math.Clamp(s, 0, 1);
        // Positions in a local east/north/up frame anchored at a, metres.
        double cosLat = Math.Cos(a.LatDeg * Math.PI / 180);
        double bn = (b.LatDeg - a.LatDeg) * Math.PI / 180 * EarthRadiusM;
        double be = WrapDeg(b.LonDeg - a.LonDeg) * Math.PI / 180 * EarthRadiusM * cosLat;
        double bu = (b.AltFt - a.AltFt) * FtToM;

        double h00 = 2 * s * s * s - 3 * s * s + 1, h10 = s * s * s - 2 * s * s + s;
        double h01 = -2 * s * s * s + 3 * s * s, h11 = s * s * s - s * s;
        double e = h10 * dtSec * a.VWorldEastFps * FtToM + h01 * be + h11 * dtSec * b.VWorldEastFps * FtToM;
        double n = h10 * dtSec * a.VWorldNorthFps * FtToM + h01 * bn + h11 * dtSec * b.VWorldNorthFps * FtToM;
        double u = h10 * dtSec * a.VWorldUpFps * FtToM + h01 * bu + h11 * dtSec * b.VWorldUpFps * FtToM;
        _ = h00; // a is the origin

        return new FlightSample(
            a.LatDeg + n / EarthRadiusM * 180 / Math.PI,
            WrapLon(a.LonDeg + e / (EarthRadiusM * cosLat) * 180 / Math.PI),
            a.AltFt + u / FtToM,
            LerpAngle(a.PitchDeg, b.PitchDeg, s),
            LerpAngle(a.BankDeg, b.BankDeg, s),
            Wrap360(LerpAngle(a.HeadingDeg, b.HeadingDeg, s)),
            Lerp(a.VWorldEastFps, b.VWorldEastFps, s),
            Lerp(a.VWorldUpFps, b.VWorldUpFps, s),
            Lerp(a.VWorldNorthFps, b.VWorldNorthFps, s),
            Lerp(a.VBodyXFps, b.VBodyXFps, s),
            Lerp(a.VBodyYFps, b.VBodyYFps, s),
            Lerp(a.VBodyZFps, b.VBodyZFps, s),
            Lerp(a.RotBodyXDps, b.RotBodyXDps, s),
            Lerp(a.RotBodyYDps, b.RotBodyYDps, s),
            Lerp(a.RotBodyZDps, b.RotBodyZDps, s),
            s < 0.5 ? a.OnGround : b.OnGround);
    }

    /// <summary>Move along the world velocity (and heading along the yaw rate) for <paramref name="sec"/> seconds.</summary>
    public static FlightSample Extrapolate(in FlightSample a, double sec)
    {
        if (sec <= 0) return a;
        double cosLat = Math.Cos(a.LatDeg * Math.PI / 180);
        double n = a.VWorldNorthFps * FtToM * sec, e = a.VWorldEastFps * FtToM * sec, u = a.VWorldUpFps * FtToM * sec;
        return a with
        {
            LatDeg = a.LatDeg + n / EarthRadiusM * 180 / Math.PI,
            LonDeg = WrapLon(a.LonDeg + e / (EarthRadiusM * cosLat) * 180 / Math.PI),
            AltFt = a.AltFt + u / FtToM,
        };
    }

    /// <summary>Great-circle-ish distance in metres (equirectangular; exact enough below a few km).</summary>
    public static double DistanceM(in FlightSample a, in FlightSample b)
    {
        double cosLat = Math.Cos(a.LatDeg * Math.PI / 180);
        double n = (b.LatDeg - a.LatDeg) * Math.PI / 180 * EarthRadiusM;
        double e = WrapDeg(b.LonDeg - a.LonDeg) * Math.PI / 180 * EarthRadiusM * cosLat;
        double u = (b.AltFt - a.AltFt) * FtToM;
        return Math.Sqrt(n * n + e * e + u * u);
    }

    public static double AngleDiff(double a, double b) => WrapDeg(b - a);

    private static double Lerp(double a, double b, double s) => a + (b - a) * s;
    private static double LerpAngle(double a, double b, double s) => a + WrapDeg(b - a) * s;

    /// <summary>Into (-180, 180].</summary>
    private static double WrapDeg(double d)
    {
        d %= 360;
        if (d > 180) d -= 360;
        if (d <= -180) d += 360;
        return d;
    }

    private static double Wrap360(double d)
    {
        d %= 360;
        return d < 0 ? d + 360 : d;
    }

    private static double WrapLon(double d) => WrapDeg(d) == -180 ? 180 : WrapDeg(d);
}
