using SkyLink.Core;

namespace SkyLink.Core.Tests;

public sealed class CodecAndClockTests
{
    private static readonly Message[] Samples =
    [
        new Hello(1, 0xDEADBEEF, 42, 7, 1, false, new Authority(3, 1)),
        new StateDelta([new StateEntry(1, 2.5, new Hlc(1000, 2, 1)), new StateEntry(65535, double.NaN, new Hlc(5, 0, 0))]),
        new Digest(7, new Authority(1, 0), true, [1, 2, ulong.MaxValue], [9]),
        new EventBatch(4, [new EventEntry(5, 3, -1.25)]),
        new EventAck(42, 99),
        new ControlFrame(new Authority(2, 1), 123, [new ControlValue(0, 0.5), new ControlValue(1, -0.25)]),
        new AuthorityMsg(AuthorityKind.Takeover, new Authority(9, 1)),
    ];

    [Fact]
    public void Every_message_round_trips()
    {
        foreach (var m in Samples)
        {
            var back = MessageCodec.Decode(MessageCodec.Encode(m));
            Assert.Equal(MessageCodec.Encode(m), MessageCodec.Encode(back));
            Assert.Equal(m.GetType(), back.GetType());
        }
    }

    [Fact]
    public void Corrupt_packets_throw_instead_of_producing_garbage()
    {
        foreach (var m in Samples)
        {
            var bytes = MessageCodec.Encode(m);
            for (int len = 0; len < bytes.Length; len++)
                Assert.Throws<InvalidDataException>(() => MessageCodec.Decode(bytes.AsSpan(0, len)));
            Assert.Throws<InvalidDataException>(() => MessageCodec.Decode([.. bytes, 0]));
        }
        Assert.Throws<InvalidDataException>(() => MessageCodec.Decode([0xEE]));
    }

    [Fact]
    public void Clock_is_monotonic_and_respects_causality()
    {
        var a = new HlcClock(0);
        var b = new HlcClock(1);
        var t1 = a.Now(5000);
        var t2 = a.Now(4000); // wall clock went backwards
        Assert.True(t2 > t1);

        b.Observe(t2, 1000); // b's wall clock is far behind
        var t3 = b.Now(1000);
        Assert.True(t3 > t2);
    }

    [Fact]
    public void Schema_hash_changes_when_the_definition_changes()
    {
        var a = new SyncSchema([new GroupSpec("g", 1)], [new VarSpec("x", SyncClass.State, "g")]);
        var b = new SyncSchema([new GroupSpec("g", 1)], [new VarSpec("x", SyncClass.Analog, "g")]);
        var c = new SyncSchema([new GroupSpec("g", 1)], [new VarSpec("x", SyncClass.State, "g")]);
        Assert.NotEqual(a.Hash, b.Hash);
        Assert.Equal(a.Hash, c.Hash);
    }
}
