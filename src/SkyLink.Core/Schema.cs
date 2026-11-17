using System.Text;

namespace SkyLink.Core;

/// <summary>How a variable is synchronized. See the design doc for the full rules.</summary>
public enum SyncClass : byte
{
    /// <summary>Flight controls: owned by the pilot flying, streamed at high rate, never stored.</summary>
    Control = 0,
    /// <summary>Discrete cockpit state: last-writer-wins, reliable, digest-checked.</summary>
    State = 1,
    /// <summary>Continuous knobs: rate-limited stream plus a reliable settle value.</summary>
    Analog = 2,
    /// <summary>Momentary commands: exactly-once, in order, never sampled.</summary>
    Event = 3,
    /// <summary>Computed by aircraft logic: never sent, only hashed to detect divergence.</summary>
    Derived = 4,
    /// <summary>Per-seat (views, volumes): never synced.</summary>
    Local = 5,
}

public sealed record GroupSpec(string Id, int Order);

public sealed record VarSpec(string Id, SyncClass Class, string Group, double Epsilon = 0);

/// <summary>
/// The compiled, ordered list of synced variables. Both seats must hold a schema with an
/// identical <see cref="Hash"/>; variables are addressed by index on the wire.
/// </summary>
public sealed class SyncSchema
{
    private const double MinEpsilon = 1e-9;

    public IReadOnlyList<VarSpec> Vars { get; }
    public IReadOnlyList<GroupSpec> Groups { get; }
    public ulong Hash { get; }

    internal int[] GroupOf { get; }
    internal int[] GroupOrderOf { get; }
    internal double[] EpsilonOf { get; }
    internal SyncClass[] ClassOf { get; }

    /// <param name="contentHash">
    /// Hash of everything else both seats must agree on (e.g. the definition's read/write
    /// bindings). Mixed into <see cref="Hash"/>.
    /// </param>
    public SyncSchema(IEnumerable<GroupSpec> groups, IEnumerable<VarSpec> vars, ulong contentHash = 0)
    {
        Groups = groups.ToArray();
        Vars = vars.ToArray();
        if (Vars.Count > ushort.MaxValue) throw new ArgumentException("Too many variables.");

        var groupIndex = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int g = 0; g < Groups.Count; g++)
            if (!groupIndex.TryAdd(Groups[g].Id, g))
                throw new ArgumentException($"Duplicate group '{Groups[g].Id}'.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        GroupOf = new int[Vars.Count];
        GroupOrderOf = new int[Vars.Count];
        EpsilonOf = new double[Vars.Count];
        ClassOf = new SyncClass[Vars.Count];
        for (int i = 0; i < Vars.Count; i++)
        {
            var v = Vars[i];
            if (!seen.Add(v.Id)) throw new ArgumentException($"Duplicate variable '{v.Id}'.");
            if (!groupIndex.TryGetValue(v.Group, out int g))
                throw new ArgumentException($"Variable '{v.Id}' references unknown group '{v.Group}'.");
            if (v.Epsilon < 0 || double.IsNaN(v.Epsilon))
                throw new ArgumentException($"Variable '{v.Id}' has an invalid epsilon.");
            GroupOf[i] = g;
            GroupOrderOf[i] = Groups[g].Order;
            EpsilonOf[i] = Math.Max(v.Epsilon, MinEpsilon);
            ClassOf[i] = v.Class;
        }

        Hash = Fnv64.Add(ComputeHash(), (long)contentHash);
    }

    public int IndexOf(string id)
    {
        for (int i = 0; i < Vars.Count; i++)
            if (Vars[i].Id == id) return i;
        throw new KeyNotFoundException(id);
    }

    private ulong ComputeHash()
    {
        var h = Fnv64.Begin();
        foreach (var g in Groups)
        {
            h = Fnv64.Add(h, Encoding.UTF8.GetBytes(g.Id));
            h = Fnv64.Add(h, g.Order);
        }
        foreach (var v in Vars)
        {
            h = Fnv64.Add(h, Encoding.UTF8.GetBytes(v.Id));
            h = Fnv64.Add(h, (int)v.Class);
            h = Fnv64.Add(h, Encoding.UTF8.GetBytes(v.Group));
            h = Fnv64.Add(h, BitConverter.DoubleToInt64Bits(v.Epsilon));
        }
        return h;
    }
}

/// <summary>FNV-1a 64-bit. Stable across processes and platforms (unlike string.GetHashCode).</summary>
public static class Fnv64
{
    private const ulong Offset = 14695981039346656037UL;
    private const ulong Prime = 1099511628211UL;

    public static ulong Begin() => Offset;

    public static ulong Add(ulong h, ReadOnlySpan<byte> data)
    {
        foreach (byte b in data) h = (h ^ b) * Prime;
        return (h ^ 0xFF) * Prime; // length separator so "ab"+"c" != "a"+"bc"
    }

    public static ulong Add(ulong h, long value)
    {
        for (int i = 0; i < 8; i++)
        {
            h = (h ^ (byte)value) * Prime;
            value >>= 8;
        }
        return h;
    }

    public static ulong Add(ulong h, int value) => Add(h, (long)value);

    /// <summary>Hash a double so that -0.0 and 0.0, and all NaNs, hash identically.</summary>
    public static ulong Add(ulong h, double value)
    {
        if (value == 0) value = 0;
        long bits = double.IsNaN(value) ? 0x7FF8000000000000L : BitConverter.DoubleToInt64Bits(value);
        return Add(h, bits);
    }
}
