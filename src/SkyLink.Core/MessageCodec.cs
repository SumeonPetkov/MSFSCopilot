using System.Buffers.Binary;

namespace SkyLink.Core;

/// <summary>
/// Compact little-endian binary encoding. Decoding is strict: any truncated, oversized or
/// unknown payload throws <see cref="InvalidDataException"/> instead of yielding garbage.
/// </summary>
public static class MessageCodec
{
    private const int MaxArray = 4096;

    private enum Tag : byte
    {
        Hello = 1,
        StateDelta = 2,
        Digest = 3,
        EventBatch = 4,
        EventAck = 5,
        ControlFrame = 6,
        AuthorityMsg = 7,
    }

    public static byte[] Encode(Message message)
    {
        var w = new Writer();
        switch (message)
        {
            case Hello m:
                w.U8((byte)Tag.Hello);
                w.I32(m.ProtocolVersion);
                w.U64(m.SchemaHash);
                w.U64(m.InstanceId);
                w.U64(m.SessionId);
                w.U8(m.Seat);
                w.U8(m.IsHost ? (byte)1 : (byte)0);
                w.Authority(m.Authority);
                break;
            case StateDelta m:
                w.U8((byte)Tag.StateDelta);
                w.Count(m.Entries.Length);
                foreach (var e in m.Entries)
                {
                    w.U16(e.Var);
                    w.F64(e.Value);
                    w.Hlc(e.Stamp);
                }
                break;
            case Digest m:
                w.U8((byte)Tag.Digest);
                w.U64(m.SessionId);
                w.Authority(m.Authority);
                w.U8(m.InitialSync ? (byte)1 : (byte)0);
                w.U64Array(m.GroupHashes);
                w.U64Array(m.DerivedHashes);
                break;
            case EventBatch m:
                w.U8((byte)Tag.EventBatch);
                w.U64(m.BaseSeq);
                w.Count(m.Events.Length);
                foreach (var e in m.Events)
                {
                    w.U64(e.Seq);
                    w.U16(e.Var);
                    w.F64(e.Param);
                }
                break;
            case EventAck m:
                w.U8((byte)Tag.EventAck);
                w.U64(m.AckerInstance);
                w.U64(m.UpToSeq);
                break;
            case ControlFrame m:
                w.U8((byte)Tag.ControlFrame);
                w.Authority(m.Authority);
                w.U64(m.Seq);
                w.Count(m.Values.Length);
                foreach (var v in m.Values)
                {
                    w.U16(v.Var);
                    w.F64(v.Value);
                }
                break;
            case AuthorityMsg m:
                w.U8((byte)Tag.AuthorityMsg);
                w.U8((byte)m.Kind);
                w.Authority(m.Authority);
                break;
            default:
                throw new ArgumentException($"Unknown message type {message.GetType().Name}.");
        }
        return w.ToArray();
    }

    public static Message Decode(ReadOnlySpan<byte> data)
    {
        var r = new Reader(data);
        Message m = (Tag)r.U8() switch
        {
            Tag.Hello => new Hello(r.I32(), r.U64(), r.U64(), r.U64(), r.U8(), r.Bool(), r.Authority()),
            Tag.StateDelta => new StateDelta(r.Array(static (ref Reader x) => new StateEntry(x.U16(), x.F64(), x.Hlc()))),
            Tag.Digest => new Digest(r.U64(), r.Authority(), r.Bool(), r.U64Array(), r.U64Array()),
            Tag.EventBatch => new EventBatch(r.U64(), r.Array(static (ref Reader x) => new EventEntry(x.U64(), x.U16(), x.F64()))),
            Tag.EventAck => new EventAck(r.U64(), r.U64()),
            Tag.ControlFrame => new ControlFrame(r.Authority(), r.U64(), r.Array(static (ref Reader x) => new ControlValue(x.U16(), x.F64()))),
            Tag.AuthorityMsg => new AuthorityMsg(r.Kind(), r.Authority()),
            var t => throw new InvalidDataException($"Unknown message tag {(byte)t}."),
        };
        if (!r.AtEnd) throw new InvalidDataException("Trailing bytes after message.");
        return m;
    }

    private sealed class Writer
    {
        private byte[] _buf = new byte[256];
        private int _len;

        private Span<byte> Take(int n)
        {
            if (_len + n > _buf.Length) Array.Resize(ref _buf, Math.Max(_buf.Length * 2, _len + n));
            var s = _buf.AsSpan(_len, n);
            _len += n;
            return s;
        }

        public void U8(byte v) => Take(1)[0] = v;
        public void U16(int v) => BinaryPrimitives.WriteUInt16LittleEndian(Take(2), checked((ushort)v));
        public void I32(int v) => BinaryPrimitives.WriteInt32LittleEndian(Take(4), v);
        public void I64(long v) => BinaryPrimitives.WriteInt64LittleEndian(Take(8), v);
        public void U64(ulong v) => BinaryPrimitives.WriteUInt64LittleEndian(Take(8), v);
        public void F64(double v) => BinaryPrimitives.WriteDoubleLittleEndian(Take(8), v);

        public void Count(int n)
        {
            if (n > MaxArray) throw new ArgumentException($"Array too long ({n}).");
            U16(n);
        }

        public void Hlc(Hlc h)
        {
            I64(h.Wall);
            I32(h.Logical);
            U8(h.Node);
        }

        public void Authority(Authority a)
        {
            I32(a.Epoch);
            U8(a.PfSeat);
        }

        public void U64Array(ulong[] a)
        {
            Count(a.Length);
            foreach (var v in a) U64(v);
        }

        public byte[] ToArray() => _buf.AsSpan(0, _len).ToArray();
    }

    private ref struct Reader
    {
        private readonly ReadOnlySpan<byte> _data;
        private int _pos;

        public Reader(ReadOnlySpan<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public readonly bool AtEnd => _pos == _data.Length;

        private ReadOnlySpan<byte> Take(int n)
        {
            if (_pos + n > _data.Length) throw new InvalidDataException("Truncated message.");
            var s = _data.Slice(_pos, n);
            _pos += n;
            return s;
        }

        public byte U8() => Take(1)[0];
        public bool Bool() => U8() switch { 0 => false, 1 => true, _ => throw new InvalidDataException("Bad bool.") };
        public int U16() => BinaryPrimitives.ReadUInt16LittleEndian(Take(2));
        public int I32() => BinaryPrimitives.ReadInt32LittleEndian(Take(4));
        public long I64() => BinaryPrimitives.ReadInt64LittleEndian(Take(8));
        public ulong U64() => BinaryPrimitives.ReadUInt64LittleEndian(Take(8));
        public double F64() => BinaryPrimitives.ReadDoubleLittleEndian(Take(8));
        public Hlc Hlc() => new(I64(), I32(), U8());
        public Authority Authority() => new(I32(), U8());

        public AuthorityKind Kind()
        {
            byte k = U8();
            if (k > (byte)AuthorityKind.Takeover) throw new InvalidDataException("Bad authority kind.");
            return (AuthorityKind)k;
        }

        public int Count()
        {
            int n = U16();
            if (n > MaxArray) throw new InvalidDataException("Array too long.");
            return n;
        }

        public delegate T ItemReader<T>(ref Reader r);

        public T[] Array<T>(ItemReader<T> read)
        {
            int n = Count();
            var a = new T[n];
            for (int i = 0; i < n; i++) a[i] = read(ref this);
            return a;
        }

        public ulong[] U64Array()
        {
            int n = Count();
            var a = new ulong[n];
            for (int i = 0; i < n; i++) a[i] = U64();
            return a;
        }
    }
}
