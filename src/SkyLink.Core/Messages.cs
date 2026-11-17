namespace SkyLink.Core;

/// <summary>Transport channel a message must travel on.</summary>
public enum Channel : byte
{
    /// <summary>Delivered once, in order, while the connection lives.</summary>
    ReliableOrdered = 0,
    /// <summary>May be lost; an older packet arriving after a newer one is dropped.</summary>
    Sequenced = 1,
}

/// <summary>Who is flying. Ordered by (Epoch, PfSeat); the higher value always wins.</summary>
public readonly record struct Authority(int Epoch, byte PfSeat) : IComparable<Authority>
{
    public int CompareTo(Authority other)
    {
        int c = Epoch.CompareTo(other.Epoch);
        return c != 0 ? c : PfSeat.CompareTo(other.PfSeat);
    }

    public static bool operator >(Authority a, Authority b) => a.CompareTo(b) > 0;
    public static bool operator <(Authority a, Authority b) => a.CompareTo(b) < 0;
    public static bool operator >=(Authority a, Authority b) => a.CompareTo(b) >= 0;
    public static bool operator <=(Authority a, Authority b) => a.CompareTo(b) <= 0;
}

public readonly record struct StateEntry(int Var, double Value, Hlc Stamp);

public readonly record struct EventEntry(ulong Seq, int Var, double Param);

public readonly record struct ControlValue(int Var, double Value);

public enum AuthorityKind : byte { Request = 0, Grant = 1, Commit = 2, Takeover = 3 }

public abstract record Message;

/// <summary>First message on every connection. Re-sent until the peer proves it received it.</summary>
public sealed record Hello(
    int ProtocolVersion,
    ulong SchemaHash,
    ulong InstanceId,
    ulong SessionId,
    byte Seat,
    bool IsHost,
    Authority Authority) : Message;

/// <summary>State/analog values. Also used for group snapshots during repair.</summary>
public sealed record StateDelta(StateEntry[] Entries) : Message;

/// <summary>Anti-entropy summary, sent every second.</summary>
public sealed record Digest(
    ulong SessionId,
    Authority Authority,
    bool InitialSync,
    ulong[] GroupHashes,
    ulong[] DerivedHashes) : Message;

/// <summary>
/// Discrete events. <see cref="BaseSeq"/> is the sender's highest sequence that will never be
/// (re)sent again, so the receiver may advance past it.
/// </summary>
public sealed record EventBatch(ulong BaseSeq, EventEntry[] Events) : Message;

public sealed record EventAck(ulong AckerInstance, ulong UpToSeq) : Message;

public sealed record ControlFrame(Authority Authority, ulong Seq, ControlValue[] Values) : Message;

public sealed record AuthorityMsg(AuthorityKind Kind, Authority Authority) : Message;

/// <summary>Position and motion from the pilot flying, ~30 Hz, sequenced. <see cref="Time"/> is the sender's clock.</summary>
public sealed record FlightState(Authority Authority, ulong Seq, long Time, FlightSample Sample) : Message;

public readonly record struct Outgoing(Channel Channel, Message Message);

/// <summary>Instructions from the engine to the simulator adapter.</summary>
public abstract record SimCommand;

/// <summary>Write a state/analog value. <see cref="Attempt"/> 0 is the first try.</summary>
public sealed record WriteVar(int Var, double Value, int Attempt) : SimCommand;

public sealed record WriteControl(int Var, double Value) : SimCommand;

public sealed record FireEvent(int Var, double Param) : SimCommand;

/// <summary>A flight-state sample from the pilot flying, for the monitoring seat's follower.</summary>
public sealed record FlightStateUpdate(long RemoteTime, FlightSample Sample) : SimCommand;

/// <summary>This seat became (or stopped being) the pilot flying: freeze/unfreeze physics.</summary>
public sealed record AuthorityChanged(bool IsPilotFlying, int Epoch) : SimCommand;
