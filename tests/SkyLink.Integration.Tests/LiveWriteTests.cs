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
/// Opt-in (SKYLINK_LIVE_WRITE=1, MSFS running). WRITES INTO THE SIM: the real sim hosts, a fake
/// cockpit joins; the fake pilot flips the taxi light and moves the heading bug, which must
/// travel over UDP and be written into MSFS and verified; then both are put back. If the
/// sync path fails to restore them, they are restored by a direct write.
/// </summary>
public sealed class LiveWriteTests(ITestOutputHelper output)
{
    [Fact]
    public void Remote_changes_are_written_into_the_live_sim_and_restored()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_LIVE_WRITE") != "1")
        {
            output.WriteLine("skipped: set SKYLINK_LIVE_WRITE=1 with MSFS 2024 running (changes the taxi light and heading bug, then restores them)");
            return;
        }
        var catalog = DefinitionCatalog.Load(Cli.Program.FindDefinitions());
        var def = WaitForDefinition(catalog);

        int port;
        using (var u = new UdpClient(0)) port = ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
        var opts = new LinkOptions { SessionCode = "LIVE02", Port = port };
        var real = new SimConnectBridge(catalog);
        var fake = new FakeSimBridge(def, 9);
        using var host = new SessionRunner(real, Link.Host(opts));
        using var join = new SessionRunner(fake, Link.Join($"127.0.0.1:{port}", opts));
        var logs = new List<string>();
        host.Log += m => logs.Add("H " + m);
        join.Log += m => logs.Add("J " + m);

        int taxi = def.Schema.IndexOf("taxi");
        int hdg = def.Schema.IndexOf("heading_bug");
        var sw = Stopwatch.StartNew();
        void Step()
        {
            host.Step();
            join.Step();
            Thread.Sleep(2);
        }
        bool RunUntil(Func<bool> done, int ms)
        {
            var t = Stopwatch.StartNew();
            while (t.ElapsedMilliseconds < ms)
            {
                Step();
                if (done()) return true;
            }
            return false;
        }
        double Live(int v) => real.TryGetValue(v, out double x) ? x : double.NaN;

        Assert.True(RunUntil(() => join.Status.Established && !join.Status.InitialSync && real.TryGetValue(taxi, out _) && real.TryGetValue(hdg, out _), 15_000),
            "session did not reach sync:\n" + string.Join("\n", logs));
        double origTaxi = Live(taxi), origHdg = Live(hdg);
        double newTaxi = origTaxi == 0 ? 1 : 0, newHdg = (origHdg + 10) % 360;
        output.WriteLine($"original: taxi={origTaxi} heading bug={origHdg}");

        bool restored = false;
        try
        {
            // 1) The other pilot changes them.
            var t = Stopwatch.StartNew();
            fake.UserSet(taxi, newTaxi);
            bool taxiOk = RunUntil(() => Live(taxi) == newTaxi, 4000);
            output.WriteLine($"taxi light -> {newTaxi}: {(taxiOk ? $"written and verified in {t.ElapsedMilliseconds} ms" : $"NOT applied (sim shows {Live(taxi)})")}");
            t.Restart();
            fake.UserSet(hdg, newHdg);
            bool hdgOk = RunUntil(() => Math.Abs(Live(hdg) - newHdg) <= 0.5, 4000);
            output.WriteLine($"heading bug -> {newHdg}: {(hdgOk ? $"written and verified in {t.ElapsedMilliseconds} ms" : $"NOT applied (sim shows {Live(hdg)})")}");
            RunUntil(() => false, 2500); // let verification / rejection logic settle
            var status = host.Engine!.Health.Status;
            output.WriteLine($"host engine status: taxi={status[taxi]} heading_bug={status[hdg]}, rejections={host.Engine.Health.WriteRejections}");

            // 2) The other pilot puts them back.
            fake.UserSet(taxi, origTaxi);
            fake.UserSet(hdg, origHdg);
            restored = RunUntil(() => Live(taxi) == origTaxi && Math.Abs(Live(hdg) - origHdg) <= 0.5, 5000);
            output.WriteLine($"restore through sync: {(restored ? "ok" : "FAILED")}");

            Assert.True(taxiOk, "taxi light was not written into the sim");
            Assert.True(hdgOk, "heading bug was not written into the sim");
            Assert.True(restored, "originals not restored through sync");
            Assert.Equal(0, host.Engine.Health.WriteRejections);
        }
        finally
        {
            if (!restored)
            {
                long now = host.Now;
                real.Execute(new WriteVar(taxi, origTaxi, 0), now);
                real.Execute(new WriteVar(hdg, origHdg, 0), now);
                var t = Stopwatch.StartNew();
                while (t.ElapsedMilliseconds < 3000 && !(Live(taxi) == origTaxi && Math.Abs(Live(hdg) - origHdg) <= 0.5))
                {
                    real.Pump(host.Now);
                    Thread.Sleep(20);
                }
                output.WriteLine($"direct restore: taxi={Live(taxi)} (was {origTaxi}) heading bug={Live(hdg)} (was {origHdg})");
            }
            foreach (var l in logs.Where(l => l.Contains("PROBLEM") || l.Contains("rejected") || l.Contains("[net]"))) output.WriteLine(l);
        }
    }

    private static AircraftDefinition WaitForDefinition(DefinitionCatalog catalog)
    {
        using var probe = new SimConnectBridge(catalog);
        var sw = Stopwatch.StartNew();
        while (probe.Definition is null && sw.ElapsedMilliseconds < 15_000)
        {
            probe.Pump(sw.ElapsedMilliseconds + 1);
            Thread.Sleep(20);
        }
        Assert.NotNull(probe.Definition);
        return probe.Definition!;
    }
}
