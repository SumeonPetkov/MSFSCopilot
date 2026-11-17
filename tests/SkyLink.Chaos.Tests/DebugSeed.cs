using SkyLink.Core;
using Xunit.Abstractions;
namespace SkyLink.Chaos.Tests;
public sealed class DebugSeed(ITestOutputHelper o)
{
    [Fact]
    public void Trace()
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SKYLINK_DEBUG_SEED"), out int seed)) return;
        var r = ConvergenceTests.RunScenario(seed, assert: false, output: o);
        foreach (var f in r.Failures) o.WriteLine("FAIL " + f);
        foreach (var l in r.World.Log.TakeLast(40)) o.WriteLine(l);
        var w = r.World;
        foreach (var p in w.Peers)
        {
            var e = p.Engine;
            var (g, _) = e.ComputeHashes();
            o.WriteLine($"seat {e.Seat}: est={e.IsEstablished} init={e.IsInitialSync} pending={e.PendingWriteCount} hashes={string.Join(",", g.Select(x => x.ToString("X")[..4]))} mismatches={e.Health.DigestMismatches}");
        }
        w.Run(3000);
        foreach (var p in w.Peers) o.WriteLine($"after +3s seat {p.Engine.Seat}: init={p.Engine.IsInitialSync} est={p.Engine.IsEstablished}");
    }
}
