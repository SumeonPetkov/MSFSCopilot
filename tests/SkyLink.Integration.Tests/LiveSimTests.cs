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
/// Opt-in (SKYLINK_LIVE=1, MSFS running): the real sim hosts, a fake cockpit joins over real
/// UDP. The host's cockpit is the truth, so nothing is written into MSFS; the test checks
/// that the joiner ends up mirroring the live aircraft exactly.
/// </summary>
public sealed class LiveSimTests(ITestOutputHelper output)
{
    [Fact]
    public void Fake_joiner_mirrors_the_live_sim()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_LIVE") != "1")
        {
            output.WriteLine("skipped: set SKYLINK_LIVE=1 with MSFS 2024 running");
            return;
        }
        var catalog = DefinitionCatalog.Load(Cli.Program.FindDefinitions());
        using var probe = new SimConnectBridge(catalog);
        var sw = Stopwatch.StartNew();
        while (probe.Definition is null && sw.ElapsedMilliseconds < 15_000)
        {
            probe.Pump(sw.ElapsedMilliseconds + 1);
            Thread.Sleep(20);
        }
        Assert.NotNull(probe.Definition);
        var def = probe.Definition!;
        probe.Dispose();

        int port;
        using (var u = new UdpClient(0)) port = ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
        var opts = new LinkOptions { SessionCode = "LIVE01", Port = port };
        var real = new SimConnectBridge(catalog);
        var fake = new FakeSimBridge(def, 7);
        var logs = new List<string>();
        using var host = new SessionRunner(real, Link.Host(opts));
        using var join = new SessionRunner(fake, Link.Join($"127.0.0.1:{port}", opts));
        host.Log += m => logs.Add("H " + m);
        join.Log += m => logs.Add("J " + m);

        sw.Restart();
        while (sw.ElapsedMilliseconds < 10_000)
        {
            host.Step();
            join.Step();
            Thread.Sleep(2);
        }
        foreach (var l in logs) output.WriteLine(l);

        Assert.True(join.Status.Established, "joiner never established");
        Assert.False(join.Status.InitialSync, "initial sync did not finish");
        int compared = 0;
        for (int v = 0; v < def.Vars.Count; v++)
        {
            var b = def.Vars[v];
            if (b.Spec.Class is not (SyncClass.State or SyncClass.Analog)) continue;
            Assert.True(real.TryGetValue(v, out double live), $"no live value for {b.Spec.Id}");
            output.WriteLine($"{b.Spec.Id,-20} live={live,-14} joiner={fake.Get(v)}");
            Assert.True(Math.Abs(live - fake.Get(v)) <= b.Spec.Epsilon + 1e-9, $"{b.Spec.Id}: live={live} joiner={fake.Get(v)}");
            compared++;
        }
        Assert.Equal(0, host.Status.PendingWrites); // nothing was written into MSFS
        output.WriteLine($"{compared} variables mirrored from the live sim");
    }
}
