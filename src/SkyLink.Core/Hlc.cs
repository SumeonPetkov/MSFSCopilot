namespace SkyLink.Core;

/// <summary>
/// Hybrid logical clock timestamp. Totally ordered by (Wall, Logical, Node), so two seats
/// always agree on which of two concurrent writes wins.
/// </summary>
public readonly record struct Hlc(long Wall, int Logical, byte Node) : IComparable<Hlc>
{
    /// <summary>
    /// A "baseline" stamp marks a value we observed but have no authority over (e.g. the joiner's
    /// cold-and-dark state before the first sync). Baseline entries are never transmitted and
    /// never win against a real stamp.
    /// </summary>
    public static Hlc Baseline(byte node) => new(0, 0, node);

    public bool IsBaseline => Wall == 0 && Logical == 0;

    public int CompareTo(Hlc other)
    {
        int c = Wall.CompareTo(other.Wall);
        if (c != 0) return c;
        c = Logical.CompareTo(other.Logical);
        return c != 0 ? c : Node.CompareTo(other.Node);
    }

    public static bool operator >(Hlc a, Hlc b) => a.CompareTo(b) > 0;
    public static bool operator <(Hlc a, Hlc b) => a.CompareTo(b) < 0;
    public static bool operator >=(Hlc a, Hlc b) => a.CompareTo(b) >= 0;
    public static bool operator <=(Hlc a, Hlc b) => a.CompareTo(b) <= 0;

    public override string ToString() => $"{Wall}.{Logical}@{Node}";
}

/// <summary>Issues monotonically increasing <see cref="Hlc"/> stamps for one seat.</summary>
public sealed class HlcClock
{
    private readonly byte _node;
    private long _wall;
    private int _logical;

    public HlcClock(byte node) => _node = node;

    public Hlc Last => new(_wall, _logical, _node);

    /// <summary>Stamp a local event. <paramref name="nowMs"/> must be &gt; 0.</summary>
    public Hlc Now(long nowMs)
    {
        if (nowMs > _wall)
        {
            _wall = nowMs;
            _logical = 0;
        }
        else
        {
            _logical++;
        }
        return new Hlc(_wall, _logical, _node);
    }

    /// <summary>Merge a remote stamp so that every later local stamp is causally after it.</summary>
    public void Observe(Hlc remote, long nowMs)
    {
        long wall = Math.Max(Math.Max(_wall, remote.Wall), nowMs);
        if (wall == _wall && wall == remote.Wall) _logical = Math.Max(_logical, remote.Logical);
        else if (wall == remote.Wall) _logical = remote.Logical;
        else if (wall != _wall) _logical = 0;
        _wall = wall;
    }
}
