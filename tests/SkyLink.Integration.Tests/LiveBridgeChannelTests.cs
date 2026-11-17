using System.Diagnostics;
using SkyLink.Definitions;
using SkyLink.Sim;
using Xunit.Abstractions;
using static SkyLink.Sim.SimConnectNative;

namespace SkyLink.Integration.Tests;

/// <summary>
/// Opt-in (SKYLINK_LIVE=1, MSFS running). Exercises the real client-data-area channel inside
/// MSFS without the WASM module: a C# stand-in (<see cref="FakeModule"/>) connects through its
/// own SimConnect handle and answers on the same areas. Nothing in the sim changes: the stand-in
/// records RPN code instead of running it.
/// </summary>
public sealed class LiveBridgeChannelTests(ITestOutputHelper output)
{
    private const uint CdaCommand = 0x534B0001, CdaResponse = 0x534B0002;

    private sealed unsafe class ExternalModule : IDisposable
    {
        private readonly IntPtr _h;
        public FakeModule Logic { get; }
        public int Frames { get; private set; }

        public ExternalModule(FakeModule logic)
        {
            Logic = logic;
            InstallResolver();
            Assert.Equal(SOk, SimConnect_Open(out _h, "SkyLinkFakeModule", IntPtr.Zero, 0, IntPtr.Zero, 0));
            SimConnect_MapClientDataNameToID(_h, BridgeProtocol.CommandArea, CdaCommand);
            SimConnect_MapClientDataNameToID(_h, BridgeProtocol.ResponseArea, CdaResponse);
            SimConnect_CreateClientData(_h, CdaCommand, BridgeProtocol.FrameSize, 0);
            SimConnect_CreateClientData(_h, CdaResponse, BridgeProtocol.FrameSize, 0);
            SimConnect_AddToClientDataDefinition(_h, 1, 0, BridgeProtocol.FrameSize, 0, Unused);
            SimConnect_AddToClientDataDefinition(_h, 2, 0, BridgeProtocol.FrameSize, 0, Unused);
            SimConnect_RequestClientData(_h, CdaCommand, 1, 1, ClientDataPeriod.OnSet, 0, 0, 0, 0);
        }

        public void Pump()
        {
            while (SimConnect_GetNextDispatch(_h, out var ptr, out uint size) == SOk && ptr != IntPtr.Zero)
            {
                var r = new Recv(ptr, size);
                if (r.Id != RecvId.ClientData || r.U32(12) != 1 || r.Length < 40 + BridgeProtocol.FrameSize) continue;
                Frames++;
                if (Logic.Handle(r.Bytes(40, BridgeProtocol.FrameSize).ToArray()) is { } resp)
                    fixed (byte* p = resp)
                        SimConnect_SetClientData(_h, CdaResponse, 2, 0, 0, (uint)resp.Length, p);
            }
        }

        public void Dispose() => SimConnect_Close(_h);
    }

    [Fact]
    public void Client_data_channel_works_inside_msfs()
    {
        if (Environment.GetEnvironmentVariable("SKYLINK_LIVE") != "1")
        {
            output.WriteLine("skipped: set SKYLINK_LIVE=1 with MSFS 2024 running");
            return;
        }
        string yaml = """
            id: channeltest
            match: { fallback: true }
            groups:
              - id: g
                vars:
                  - { id: taxi_sw,  class: state, read: "L:INI_TAXI_LIGHT_SWITCH" }
                  - { id: beacon_sw, class: state, read: "L:INI_BEACON_LIGHT_SWITCH" }
                  - { id: typo,     class: state, read: "L:SKYLINK_DEFINITELY_MISSPELLED" }
                  - { id: rpn_var,  class: state, read: "A:LIGHT TAXI, Bool", write: { rpn: "{value} (>L:SKYLINK_CHANNEL_TEST)" } }
            """;
        var catalog = DefinitionCatalog.FromYaml(("channeltest.yaml", yaml));
        Assert.Empty(catalog.Issues.Where(i => i.IsError));

        // The stand-in reports which L-vars "exist": the real two, not the typo.
        using var module = new ExternalModule(new FakeModule(["INI_TAXI_LIGHT_SWITCH", "INI_BEACON_LIGHT_SWITCH", "SOMETHING_ELSE"]));
        using var bridge = new SimConnectBridge(catalog);
        var logs = new List<string>();
        bridge.Log += logs.Add;

        var sw = Stopwatch.StartNew();
        bool rpnSent = false;
        while (sw.ElapsedMilliseconds < 10_000)
        {
            bridge.Pump(sw.ElapsedMilliseconds + 1);
            module.Pump();
            if (!rpnSent && bridge.ModuleStatus.StartsWith("connected") && bridge.Definition is not null)
            {
                rpnSent = true;
                bridge.Execute(new SkyLink.Core.WriteVar(bridge.Definition.Schema.IndexOf("rpn_var"), 1, 0), sw.ElapsedMilliseconds + 1);
            }
            Thread.Sleep(10);
        }
        foreach (var l in logs) output.WriteLine(l);
        output.WriteLine($"module frames handled: {module.Frames}; rpn recorded: {string.Join(" | ", module.Logic.Executed)}");

        var def = bridge.Definition!;
        Assert.StartsWith("connected", bridge.ModuleStatus);
        Assert.Contains("1 (>L:SKYLINK_CHANNEL_TEST)", module.Logic.Executed);
        Assert.True(bridge.TryGetValue(def.Schema.IndexOf("taxi_sw"), out _), "confirmed L-var was not subscribed");
        Assert.True(bridge.TryGetValue(def.Schema.IndexOf("beacon_sw"), out _), "confirmed L-var was not subscribed");
        Assert.False(bridge.TryGetValue(def.Schema.IndexOf("typo"), out _), "misspelled L-var was subscribed (and silently created)");
        Assert.Equal(1, bridge.UnconfirmedLVars);
        Assert.Empty(bridge.Problems); // no SimConnect call failed or was rejected
    }
}
