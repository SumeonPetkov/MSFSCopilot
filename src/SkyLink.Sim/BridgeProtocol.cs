using System.Buffers.Binary;
using System.Text;

namespace SkyLink.Sim;

/// <summary>
/// The wire format between skylink.exe and the SkyLinkBridge WASM module inside MSFS, carried in
/// two 8 KB SimConnect client data areas. Spec: wasm/PROTOCOL.md. The C++ side
/// (wasm/SkyLinkBridge/Protocol.h) must stay byte-identical; BridgeProtocolTests pins golden
/// bytes for both.
/// </summary>
public static class BridgeProtocol
{
    public const string CommandArea = "SkyLink.Bridge.Command";
    public const string ResponseArea = "SkyLink.Bridge.Response";
    public const uint Magic = 0x424C4B53; // "SKLB" little-endian
    public const ushort Version = 1;
    public const int FrameSize = 8192;
    public const int HeaderSize = 16;
    public const int RecordHeaderSize = 4;
    public const int MaxRecordPayload = FrameSize - HeaderSize - RecordHeaderSize;

    public enum RecordType : ushort
    {
        Hello = 0x01,
        Rpn = 0x02,
        ListLVars = 0x03,
        HelloAck = 0x81,
        RpnResult = 0x82,
        LVarList = 0x83,
        Error = 0x84,
    }

    public readonly record struct Record(RecordType Type, byte[] Payload);

    /// <summary>Frame layout: magic u32, version u16, count u16, seq u32, payload length u32, records.</summary>
    public static byte[] EncodeFrame(uint seq, IReadOnlyList<Record> records)
    {
        var frame = new byte[FrameSize];
        int off = HeaderSize;
        foreach (var r in records)
        {
            if (r.Payload.Length > MaxRecordPayload) throw new ArgumentException($"record {r.Type} too large ({r.Payload.Length} bytes)");
            if (off + RecordHeaderSize + r.Payload.Length > FrameSize) throw new ArgumentException("records do not fit in one frame");
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(off), (ushort)r.Type);
            BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(off + 2), (ushort)r.Payload.Length);
            r.Payload.CopyTo(frame, off + RecordHeaderSize);
            off += RecordHeaderSize + r.Payload.Length;
        }
        BinaryPrimitives.WriteUInt32LittleEndian(frame, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(frame.AsSpan(6), (ushort)records.Count);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(8), seq);
        BinaryPrimitives.WriteUInt32LittleEndian(frame.AsSpan(12), (uint)(off - HeaderSize));
        return frame;
    }

    public static bool TryDecodeFrame(ReadOnlySpan<byte> frame, out uint seq, out List<Record> records)
    {
        seq = 0;
        records = [];
        if (frame.Length < HeaderSize) return false;
        if (BinaryPrimitives.ReadUInt32LittleEndian(frame) != Magic) return false;
        if (BinaryPrimitives.ReadUInt16LittleEndian(frame[4..]) != Version) return false;
        int count = BinaryPrimitives.ReadUInt16LittleEndian(frame[6..]);
        seq = BinaryPrimitives.ReadUInt32LittleEndian(frame[8..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(frame[12..]);
        if (length > frame.Length - HeaderSize) return false;
        var body = frame.Slice(HeaderSize, (int)length);
        int off = 0;
        for (int i = 0; i < count; i++)
        {
            if (off + RecordHeaderSize > body.Length) return false;
            var type = (RecordType)BinaryPrimitives.ReadUInt16LittleEndian(body[off..]);
            int len = BinaryPrimitives.ReadUInt16LittleEndian(body[(off + 2)..]);
            if (off + RecordHeaderSize + len > body.Length) return false;
            records.Add(new Record(type, body.Slice(off + RecordHeaderSize, len).ToArray()));
            off += RecordHeaderSize + len;
        }
        return off == body.Length;
    }

    // ---- command payloads

    public static Record Hello() => new(RecordType.Hello, U16(Version));

    /// <summary>RPN: u32 request id, then UTF-8 calculator code.</summary>
    public static Record Rpn(uint id, string code)
    {
        var text = Encoding.UTF8.GetBytes(code);
        var p = new byte[4 + text.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(p, id);
        text.CopyTo(p, 4);
        return new Record(RecordType.Rpn, p);
    }

    public static Record ListLVars(uint startIndex) => new(RecordType.ListLVars, U32(startIndex));

    // ---- response payloads (also used by test fakes to build responses)

    public static Record HelloAck(string build)
    {
        var text = Encoding.UTF8.GetBytes(build);
        var p = new byte[2 + text.Length];
        BinaryPrimitives.WriteUInt16LittleEndian(p, Version);
        text.CopyTo(p, 2);
        return new Record(RecordType.HelloAck, p);
    }

    public static Record RpnResult(uint id, bool ok, double value)
    {
        var p = new byte[13];
        BinaryPrimitives.WriteUInt32LittleEndian(p, id);
        p[4] = ok ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteDoubleLittleEndian(p.AsSpan(5), value);
        return new Record(RecordType.RpnResult, p);
    }

    /// <summary>L-var page: u32 next index, u8 done, then NUL-terminated names.</summary>
    public static Record LVarList(uint nextIndex, bool done, IEnumerable<string> names)
    {
        var ms = new MemoryStream();
        ms.Write(U32(nextIndex));
        ms.WriteByte(done ? (byte)1 : (byte)0);
        foreach (var n in names)
        {
            ms.Write(Encoding.UTF8.GetBytes(n));
            ms.WriteByte(0);
        }
        return new Record(RecordType.LVarList, ms.ToArray());
    }

    public static (uint Id, bool Ok, double Value) ParseRpnResult(byte[] p) =>
        p.Length < 13 ? throw new InvalidDataException("short RPN result")
            : (BinaryPrimitives.ReadUInt32LittleEndian(p), p[4] != 0, BinaryPrimitives.ReadDoubleLittleEndian(p.AsSpan(5)));

    public static (uint Next, bool Done, List<string> Names) ParseLVarList(byte[] p)
    {
        if (p.Length < 5) throw new InvalidDataException("short L-var list");
        var names = new List<string>();
        int start = 5;
        for (int i = 5; i < p.Length; i++)
            if (p[i] == 0)
            {
                names.Add(Encoding.UTF8.GetString(p, start, i - start));
                start = i + 1;
            }
        return (BinaryPrimitives.ReadUInt32LittleEndian(p), p[4] != 0, names);
    }

    public static string ParseText(byte[] p, int skip) => Encoding.UTF8.GetString(p, skip, p.Length - skip);

    private static byte[] U16(ushort v)
    {
        var b = new byte[2];
        BinaryPrimitives.WriteUInt16LittleEndian(b, v);
        return b;
    }

    private static byte[] U32(uint v)
    {
        var b = new byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(b, v);
        return b;
    }
}

/// <summary>
/// App-side protocol state machine: one frame in flight (stop-and-wait), retransmit until the
/// module answers that sequence number, as many queued commands per frame as fit. Pure: the
/// caller supplies the clock and the transport, so tests can lose and duplicate frames.
/// </summary>
public sealed class ModuleClient
{
    public const int RetransmitMs = 400;
    public const int HelloRetryMs = 1000;
    public const int AbsentAfterMs = 6000;

    private readonly Action<byte[]> _send;
    private readonly Queue<(BridgeProtocol.Record Record, Action<BridgeProtocol.Record>? OnReply)> _queue = new();
    private readonly Dictionary<uint, Action<bool, double>> _rpnCallbacks = [];
    private (uint Seq, byte[] Frame, List<Action<BridgeProtocol.Record>?> Replies)? _inFlight;
    private uint _seq;
    private uint _nextRpnId = 1;
    private long _lastSentAt;
    private long _startedAt = -1;
    private long _lastHelloAt = long.MinValue / 2;
    private bool _helloQueued;

    public bool IsAvailable { get; private set; }
    public bool IsAbsent { get; private set; }
    public string? ModuleBuild { get; private set; }
    public long FramesSent { get; private set; }
    public long Retransmits { get; private set; }
    public event Action<string>? Log;

    public ModuleClient(Action<byte[]> send, uint initialSeq)
    {
        _send = send;
        _seq = initialSeq == 0 ? 1 : initialSeq;
    }

    /// <summary>Forget everything (the sim connection was re-opened).</summary>
    public void Reset()
    {
        _queue.Clear();
        _rpnCallbacks.Clear();
        _inFlight = null;
        IsAvailable = false;
        IsAbsent = false;
        _startedAt = -1;
        _helloQueued = false;
    }

    public int Pending => _queue.Count + (_inFlight is null ? 0 : 1);

    public void ExecuteRpn(string code, Action<bool, double>? done = null)
    {
        uint id = _nextRpnId++;
        if (done is not null) _rpnCallbacks[id] = done;
        Enqueue(BridgeProtocol.Rpn(id, code), null);
    }

    /// <summary>Every L-var currently defined in the sim (paged by the module).</summary>
    public void ListLVars(Action<List<string>> done)
    {
        var all = new List<string>();
        void Page(uint start) => Enqueue(BridgeProtocol.ListLVars(start), r =>
        {
            if (r.Type != BridgeProtocol.RecordType.LVarList) return;
            var (next, finished, names) = BridgeProtocol.ParseLVarList(r.Payload);
            all.AddRange(names);
            if (finished) done(all);
            else Page(next);
        });
        Page(0);
    }

    private void Enqueue(BridgeProtocol.Record r, Action<BridgeProtocol.Record>? onReply)
    {
        if (r.Payload.Length > BridgeProtocol.MaxRecordPayload) throw new ArgumentException("command too large for one frame");
        _queue.Enqueue((r, onReply));
    }

    public void Tick(long now)
    {
        if (_startedAt < 0) _startedAt = now;
        if (!IsAvailable)
        {
            if (!IsAbsent && now - _startedAt >= AbsentAfterMs)
            {
                IsAbsent = true;
                Log?.Invoke("SkyLink bridge module did not answer: is the skylink-bridge package installed (and MSFS restarted since)?");
            }
            if (!_helloQueued && now - _lastHelloAt >= HelloRetryMs && _inFlight is null)
            {
                _lastHelloAt = now;
                _helloQueued = true;
                SendFrame([(BridgeProtocol.Hello(), null)], now);
                return;
            }
        }

        if (_inFlight is { } f)
        {
            if (now - _lastSentAt >= RetransmitMs)
            {
                _lastSentAt = now;
                Retransmits++;
                _send(f.Frame);
                FramesSent++;
            }
            return;
        }
        if (!IsAvailable || _queue.Count == 0) return;

        var batch = new List<(BridgeProtocol.Record, Action<BridgeProtocol.Record>?)>();
        int size = BridgeProtocol.HeaderSize;
        while (_queue.TryPeek(out var next) && size + BridgeProtocol.RecordHeaderSize + next.Record.Payload.Length <= BridgeProtocol.FrameSize)
        {
            size += BridgeProtocol.RecordHeaderSize + next.Record.Payload.Length;
            batch.Add(_queue.Dequeue());
        }
        SendFrame(batch, now);
    }

    private void SendFrame(List<(BridgeProtocol.Record Record, Action<BridgeProtocol.Record>? OnReply)> batch, long now)
    {
        uint seq = _seq++;
        if (_seq == 0) _seq = 1;
        var frame = BridgeProtocol.EncodeFrame(seq, batch.Select(b => b.Record).ToList());
        _inFlight = (seq, frame, batch.Select(b => b.OnReply).ToList());
        _lastSentAt = now;
        _send(frame);
        FramesSent++;
    }

    /// <summary>A response frame from the module (client data area update).</summary>
    public void OnFrame(ReadOnlySpan<byte> data)
    {
        if (!BridgeProtocol.TryDecodeFrame(data, out uint seq, out var records)) return;
        if (_inFlight is not { } f || seq != f.Seq) return; // stale or duplicate answer
        _inFlight = null;

        // Records answer the commands in order; unanswered slots get nothing.
        for (int i = 0; i < records.Count; i++)
        {
            var r = records[i];
            switch (r.Type)
            {
                case BridgeProtocol.RecordType.HelloAck:
                    _helloQueued = false;
                    if (!IsAvailable)
                    {
                        IsAvailable = true;
                        IsAbsent = false;
                        ModuleBuild = BridgeProtocol.ParseText(r.Payload, 2);
                        Log?.Invoke($"SkyLink bridge module connected ({ModuleBuild})");
                    }
                    break;
                case BridgeProtocol.RecordType.RpnResult:
                {
                    var (id, ok, value) = BridgeProtocol.ParseRpnResult(r.Payload);
                    if (_rpnCallbacks.Remove(id, out var cb)) cb(ok, value);
                    break;
                }
                case BridgeProtocol.RecordType.Error:
                    Log?.Invoke("bridge module error: " + BridgeProtocol.ParseText(r.Payload, 2));
                    break;
            }
            if (i < f.Replies.Count) f.Replies[i]?.Invoke(r);
        }
        if (records.Count == 0) _helloQueued = false;
    }
}
