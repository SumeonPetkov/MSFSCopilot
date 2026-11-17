using System.Buffers.Binary;
using SkyLink.Sim;

namespace SkyLink.Integration.Tests;

/// <summary>
/// C# model of wasm/SkyLinkBridge/Module.cpp's command handling (same dedup and paging rules),
/// so the app-side client can be tested against a module that loses and repeats frames.
/// </summary>
internal sealed class FakeModule(IReadOnlyList<string> lvars)
{
    private uint _lastSeq;
    private bool _haveLast;
    private byte[]? _lastResponse;

    public List<string> Executed { get; } = [];

    public byte[]? Handle(byte[] command)
    {
        if (!BridgeProtocol.TryDecodeFrame(command, out uint seq, out var records)) return null;
        if (_haveLast && seq == _lastSeq) return _lastResponse; // retransmission: answer again, never re-run
        var replies = new List<BridgeProtocol.Record>();
        foreach (var r in records)
        {
            switch (r.Type)
            {
                case BridgeProtocol.RecordType.Hello:
                    replies.Add(BridgeProtocol.HelloAck("FakeModule 1"));
                    break;
                case BridgeProtocol.RecordType.Rpn:
                    uint id = BinaryPrimitives.ReadUInt32LittleEndian(r.Payload);
                    string code = BridgeProtocol.ParseText(r.Payload, 4);
                    Executed.Add(code);
                    replies.Add(BridgeProtocol.RpnResult(id, !code.Contains("BAD"), Executed.Count));
                    break;
                case BridgeProtocol.RecordType.ListLVars:
                {
                    uint index = BinaryPrimitives.ReadUInt32LittleEndian(r.Payload);
                    int budget = BridgeProtocol.FrameSize - BridgeProtocol.HeaderSize - replies.Sum(x => 4 + x.Payload.Length) - 4 - 5;
                    var page = new List<string>();
                    while (index < lvars.Count && budget >= lvars[(int)index].Length + 1)
                    {
                        budget -= lvars[(int)index].Length + 1;
                        page.Add(lvars[(int)index++]);
                    }
                    replies.Add(BridgeProtocol.LVarList(index, index >= lvars.Count, page));
                    break;
                }
            }
        }
        _lastSeq = seq;
        _haveLast = true;
        return _lastResponse = BridgeProtocol.EncodeFrame(seq, replies);
    }
}

public sealed class BridgeProtocolTests
{
    [Fact]
    public void Frame_bytes_match_the_spec()
    {
        // wasm/PROTOCOL.md: magic, version, count, seq, payload length, then records.
        var frame = BridgeProtocol.EncodeFrame(0x01020304, [BridgeProtocol.Hello()]);
        Assert.Equal(BridgeProtocol.FrameSize, frame.Length);
        byte[] expected =
        [
            0x53, 0x4B, 0x4C, 0x42, // "SKLB"
            0x01, 0x00,             // version 1
            0x01, 0x00,             // 1 record
            0x04, 0x03, 0x02, 0x01, // seq
            0x06, 0x00, 0x00, 0x00, // 6 bytes of records
            0x01, 0x00, 0x02, 0x00, // HELLO, 2 bytes
            0x01, 0x00,             // version 1
        ];
        Assert.Equal(expected, frame[..expected.Length]);
        Assert.All(frame[expected.Length..], b => Assert.Equal(0, b));
    }

    [Fact]
    public void Rpn_record_layout()
    {
        var r = BridgeProtocol.Rpn(7, "(>H:A32NX_EFIS_L_CHRONO_PUSHED)");
        Assert.Equal(7u, BinaryPrimitives.ReadUInt32LittleEndian(r.Payload));
        Assert.Equal("(>H:A32NX_EFIS_L_CHRONO_PUSHED)", BridgeProtocol.ParseText(r.Payload, 4));
    }

    [Fact]
    public void Malformed_frames_are_rejected()
    {
        var good = BridgeProtocol.EncodeFrame(5, [BridgeProtocol.Rpn(1, "1 (>L:X)")]);
        Assert.True(BridgeProtocol.TryDecodeFrame(good, out _, out _));
        var badMagic = (byte[])good.Clone();
        badMagic[0] = 0;
        Assert.False(BridgeProtocol.TryDecodeFrame(badMagic, out _, out _));
        var badLength = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt32LittleEndian(badLength.AsSpan(12), 9000);
        Assert.False(BridgeProtocol.TryDecodeFrame(badLength, out _, out _));
        var badCount = (byte[])good.Clone();
        BinaryPrimitives.WriteUInt16LittleEndian(badCount.AsSpan(6), 3);
        Assert.False(BridgeProtocol.TryDecodeFrame(badCount, out _, out _));
        Assert.False(BridgeProtocol.TryDecodeFrame(good.AsSpan(0, 10), out _, out _));
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Every_rpn_runs_exactly_once_in_order_over_a_lossy_channel(int seed)
    {
        var rng = new Random(seed);
        var module = new FakeModule([]);
        var toModule = new List<(long At, byte[] Frame)>();
        var toApp = new List<(long At, byte[] Frame)>();
        long now = 1;
        var client = new ModuleClient(f =>
        {
            if (rng.NextDouble() < 0.3) return;                       // lost
            int copies = rng.NextDouble() < 0.15 ? 2 : 1;              // duplicated
            for (int i = 0; i < copies; i++) toModule.Add((now + rng.Next(1, 40), f));
        }, (uint)rng.Next(1, int.MaxValue));

        var results = new List<(int N, bool Ok)>();
        for (int i = 0; i < 300; i++)
        {
            int n = i;
            client.ExecuteRpn(i % 50 == 49 ? $"BAD {i}" : $"{i} (>L:SKYLINK_TEST)", (ok, _) => results.Add((n, ok)));
        }

        for (; now < 120_000 && (client.Pending > 0 || !client.IsAvailable); now += 5)
        {
            foreach (var m in toModule.Where(x => x.At <= now).ToList())
            {
                toModule.Remove(m);
                if (module.Handle(m.Frame) is { } resp && rng.NextDouble() >= 0.3)
                    toApp.Add((now + rng.Next(1, 40), resp));
            }
            foreach (var m in toApp.Where(x => x.At <= now).ToList())
            {
                toApp.Remove(m);
                client.OnFrame(m.Frame);
            }
            client.Tick(now);
        }

        Assert.True(client.IsAvailable);
        Assert.Equal(0, client.Pending);
        var expected = Enumerable.Range(0, 300).Select(i => i % 50 == 49 ? $"BAD {i}" : $"{i} (>L:SKYLINK_TEST)");
        Assert.Equal(expected, module.Executed);                           // exactly once, in order
        Assert.Equal(Enumerable.Range(0, 300), results.Select(r => r.N));  // every caller answered once
        Assert.Equal(6, results.Count(r => !r.Ok));
        Assert.True(client.Retransmits > 0, "the channel was supposed to lose frames");
    }

    [Fact]
    public void L_var_listing_pages_through_thousands_of_names()
    {
        var names = Enumerable.Range(0, 5000).Select(i => $"INI_SOME_RATHER_LONG_SWITCH_NAME_{i}").ToList();
        var module = new FakeModule(names);
        ModuleClient? client = null;
        client = new ModuleClient(f =>
        {
            if (module.Handle(f) is { } r) client!.OnFrame(r);
        }, 77);
        List<string>? listed = null;
        client.ListLVars(l => listed = l);
        for (long now = 1; now < 10_000 && listed is null; now += 10) client.Tick(now);
        Assert.Equal(names, listed);
    }

    [Fact]
    public void Missing_module_is_reported_not_waited_for_forever()
    {
        var client = new ModuleClient(_ => { }, 9);
        string? log = null;
        client.Log += m => log = m;
        for (long now = 1; now <= ModuleClient.AbsentAfterMs + 10; now += 10) client.Tick(now);
        Assert.True(client.IsAbsent);
        Assert.False(client.IsAvailable);
        Assert.Contains("skylink-bridge package", log);
    }
}
