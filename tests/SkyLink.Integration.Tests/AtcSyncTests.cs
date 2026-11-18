using System.Diagnostics;
using System.Net.Sockets;
using SkyLink.Definitions;
using SkyLink.Net;
using SkyLink.Session;
using SkyLink.Sim;

namespace SkyLink.Integration.Tests;

/// <summary>
/// ATC answers are mirrored: whichever pilot opens the ATC menu and picks a reply, the other
/// pilot's sim gets the same menu actions, exactly once and in order, and is told who answered.
/// </summary>
public sealed class AtcSyncTests
{
    private static readonly AircraftDefinition C172 =
        DefinitionCatalog.Load(Cli.Program.FindDefinitions()).Aircraft.Single(d => d.Id == "asobo-c172sp-g1000");

    [Fact]
    public void Either_pilots_atc_reply_reaches_the_other_sim_exactly_once_and_in_order()
    {
        int port;
        using (var u = new UdpClient(0)) port = ((System.Net.IPEndPoint)u.Client.LocalEndPoint!).Port;
        var opts = new LinkOptions { SessionCode = "ATC001", Port = port, SimulateLossPercent = 15, SimulateLatencyMinMs = 10, SimulateLatencyMaxMs = 60 };
        var captainSim = new FakeSimBridge(C172, 1);
        var foSim = new FakeSimBridge(C172, 2);
        using var captain = new SessionRunner(captainSim, Link.Host(opts));
        using var fo = new SessionRunner(foSim, Link.Join($"127.0.0.1:{port}", opts));

        var captainNotices = new List<string>();
        var foNotices = new List<string>();
        captainSim.EventReplayed += (b, _) => captainNotices.Add(b.Spec.Id);
        foSim.EventReplayed += (b, _) => foNotices.Add(b.Spec.Id);
        int V(string id) => C172.Schema.IndexOf(id);

        bool RunUntil(Func<bool> done, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms)
            {
                captain.Step();
                fo.Step();
                if (done()) return true;
                Thread.Sleep(2);
            }
            return false;
        }

        Assert.True(RunUntil(() => fo.Status.Established && !fo.Status.InitialSync, 8000), "session did not connect");

        // The First Officer answers ATC (over a link losing 15% of packets).
        foSim.UserPress(V("atc_window"), 0);
        foSim.UserPress(V("atc_reply_2"), 0);
        Assert.True(RunUntil(() => captainSim.AppliedEvents.Count >= 2, 5000), "the Captain's ATC never got the answer");
        RunUntil(() => false, 1500); // anything extra (a duplicate or an echo) would show up now

        Assert.Equal([(V("atc_window"), 0.0), (V("atc_reply_2"), 0.0)], captainSim.AppliedEvents);
        Assert.Equal(["atc_window", "atc_reply_2"], captainNotices);
        Assert.Empty(foSim.AppliedEvents);  // the FO's own answer is not bounced back to them
        Assert.Empty(foNotices);

        // Then the Captain answers the next ATC call.
        captainSim.UserPress(V("atc_reply_1"), 0);
        Assert.True(RunUntil(() => foSim.AppliedEvents.Count >= 1, 5000), "the First Officer's ATC never got the answer");
        RunUntil(() => false, 1000);
        Assert.Equal([(V("atc_reply_1"), 0.0)], foSim.AppliedEvents);
        Assert.Equal(["atc_reply_1"], foNotices);
        Assert.Equal(2, captainSim.AppliedEvents.Count); // still nothing echoed back to the Captain
    }

    [Fact]
    public void Every_aircraft_definition_mirrors_the_atc_window_and_all_ten_replies()
    {
        var catalog = DefinitionCatalog.Load(Cli.Program.FindDefinitions());
        foreach (var def in catalog.Aircraft)
        {
            var atc = def.Vars.Where(v => v.Spec.Group == "atc").ToList();
            Assert.All(atc, v => Assert.Equal(Core.SyncClass.Event, v.Spec.Class));
            Assert.Equal(
                ["ATC", "ATC_MENU_1", "ATC_MENU_2", "ATC_MENU_3", "ATC_MENU_4", "ATC_MENU_5",
                 "ATC_MENU_6", "ATC_MENU_7", "ATC_MENU_8", "ATC_MENU_9", "ATC_MENU_0"],
                atc.Select(v => v.Capture!.Name));
        }
    }
}
