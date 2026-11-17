using SkyLink.Core;

namespace SkyLink.Core.Tests;

/// <summary>
/// Two engines wired back to back with instant, lossless delivery, each with a minimal sim:
/// writes land on the next sample unless the test holds or blocks them.
/// </summary>
internal sealed class Pair
{
    private readonly SyncSchema _schema;

    public SyncEngine Host { get; }
    public SyncEngine Join { get; }
    public double[] HostSim { get; }
    public double[] JoinSim { get; }
    /// <summary>Variables whose writes the joiner's sim ignores (held or rejected).</summary>
    public HashSet<int> JoinIgnoresWrites { get; } = [];
    public long Now { get; private set; } = 10_000;
    public List<Message> HostSent { get; } = [];
    public List<Message> JoinSent { get; } = [];
    public List<SimCommand> HostCmds { get; } = [];
    public List<SimCommand> JoinCmds { get; } = [];

    public Pair(SyncSchema schema, EngineOptions? options = null, SyncSchema? joinerSchema = null)
    {
        _schema = schema;
        Host = new SyncEngine(schema, 0, true, 0xAAAA, 0x5E55, options);
        Join = new SyncEngine(joinerSchema ?? schema, 1, false, 0xBBBB, 0, options);
        HostSim = new double[schema.Vars.Count];
        JoinSim = new double[(joinerSchema ?? schema).Vars.Count];
    }

    public void Connect()
    {
        Sample();
        Host.OnConnected(Now);
        Join.OnConnected(Now);
        Pump();
    }

    public void Advance(int ms = 10)
    {
        Now += ms;
        Sample();
        Host.Tick(Now);
        Join.Tick(Now);
        Pump();
    }

    public void Run(int ms)
    {
        for (int t = 0; t < ms; t += 10) Advance();
    }

    private void Sample()
    {
        for (int v = 0; v < _schema.Vars.Count; v++)
        {
            if (_schema.Vars[v].Class == SyncClass.Event) continue;
            Host.OnSimSample(v, HostSim[v], Now);
            if (v < JoinSim.Length) Join.OnSimSample(v, JoinSim[v], Now);
        }
    }

    public void Pump()
    {
        for (int i = 0; i < 100 && (Host.Outbox.Count > 0 || Join.Outbox.Count > 0); i++)
        {
            while (Host.Outbox.TryDequeue(out var o)) { HostSent.Add(o.Message); Join.OnMessage(o.Message, Now); }
            while (Join.Outbox.TryDequeue(out var o)) { JoinSent.Add(o.Message); Host.OnMessage(o.Message, Now); }
        }
        while (Host.SimCommands.TryDequeue(out var c))
        {
            HostCmds.Add(c);
            if (c is WriteVar w) HostSim[w.Var] = w.Value;
        }
        while (Join.SimCommands.TryDequeue(out var c))
        {
            JoinCmds.Add(c);
            if (c is WriteVar w && !JoinIgnoresWrites.Contains(w.Var)) JoinSim[w.Var] = w.Value;
        }
    }
}

public sealed class EngineTests
{
    private static readonly SyncSchema Schema = new(
        [new GroupSpec("elec", 10), new GroupSpec("ap", 80)],
        [
            new VarSpec("battery", SyncClass.State, "elec"),
            new VarSpec("ext_pwr", SyncClass.State, "elec"),
            new VarSpec("alt_push", SyncClass.Event, "ap"),
            new VarSpec("hdg", SyncClass.Analog, "ap", 0.5),
        ]);

    private const int Battery = 0, ExtPwr = 1, AltPush = 2, Hdg = 3;

    private static Pair Connected(EngineOptions? o = null)
    {
        var p = new Pair(Schema, o);
        p.HostSim[Battery] = 1;
        p.HostSim[Hdg] = 90;
        p.JoinSim[Hdg] = 270;
        p.Connect();
        p.Run(3000);
        p.JoinSent.Clear();
        p.HostSent.Clear();
        p.JoinCmds.Clear();
        p.HostCmds.Clear();
        return p;
    }

    [Fact]
    public void Joiner_cold_and_dark_cockpit_never_overrides_the_host()
    {
        var p = new Pair(Schema);
        p.HostSim[Battery] = 1;
        p.HostSim[Hdg] = 90;
        p.JoinSim[Hdg] = 270;
        p.Connect();
        p.Run(3000);

        Assert.Equal(1, p.JoinSim[Battery]);
        Assert.Equal(90, p.JoinSim[Hdg]);
        Assert.Equal(1, p.HostSim[Battery]);
        Assert.Equal(90, p.HostSim[Hdg]);
        Assert.DoesNotContain(p.HostCmds, c => c is WriteVar);
        Assert.False(p.Join.IsInitialSync);
    }

    [Fact]
    public void Write_latency_never_reverts_a_remote_change()
    {
        // The FS Copilot failure mode: after a remote write, the sim still shows the old value
        // for a few frames. That must not be mistaken for the local pilot flipping it back.
        var p = Connected();
        p.JoinIgnoresWrites.Add(ExtPwr); // the joiner's sim is slow to apply

        p.HostSim[ExtPwr] = 1; // captain switches on external power
        p.Run(250);
        Assert.Contains(p.JoinCmds, c => c is WriteVar { Var: ExtPwr, Value: 1 });
        Assert.Equal(0, p.JoinSim[ExtPwr]);

        p.JoinSim[ExtPwr] = 1; // now it lands
        p.Run(100);

        Assert.DoesNotContain(p.JoinSent, m => m is StateDelta d && d.Entries.Any(e => e.Var == ExtPwr));
        Assert.Equal(1, p.HostSim[ExtPwr]);
        Assert.Equal(VarStatus.Ok, p.Join.Health.Status[ExtPwr]);
    }

    [Fact]
    public void Echo_cancels_by_value_and_nothing_stays_armed()
    {
        // After an applied remote value echoes back, a later genuine local change must still go
        // out immediately (FS Copilot's stuck skip counters swallowed these).
        var p = Connected();
        p.HostSim[ExtPwr] = 1;
        p.Run(100);
        Assert.Equal(1, p.JoinSim[ExtPwr]);
        Assert.DoesNotContain(p.JoinSent, m => m is StateDelta d && d.Entries.Any(e => e.Var == ExtPwr));

        p.JoinSim[ExtPwr] = 0; // first officer switches it off
        p.Run(50);

        Assert.Contains(p.JoinSent, m => m is StateDelta d && d.Entries.Any(e => e.Var == ExtPwr && e.Value == 0));
        Assert.Equal(0, p.HostSim[ExtPwr]);
    }

    [Fact]
    public void A_rejected_write_is_reported_and_does_not_ping_pong()
    {
        var p = Connected();
        p.JoinIgnoresWrites.Add(ExtPwr); // e.g. a guarded switch the L-var write cannot move

        p.HostSim[ExtPwr] = 1;
        p.Run(4000);

        Assert.Equal(VarStatus.Rejected, p.Join.Health.Status[ExtPwr]);
        Assert.Equal(1, p.Join.Health.WriteRejections);
        Assert.InRange(p.JoinCmds.Count(c => c is WriteVar { Var: ExtPwr }), 1, 4); // bounded retries
        Assert.DoesNotContain(p.JoinSent, m => m is StateDelta d && d.Entries.Any(e => e.Var == ExtPwr));
        Assert.Equal(1, p.HostSim[ExtPwr]); // the captain's switch is not flipped back

        // A later real change on the joiner still propagates.
        p.JoinSim[ExtPwr] = 2;
        p.Run(50);
        Assert.Equal(2, p.HostSim[ExtPwr]);
    }

    [Fact]
    public void Events_apply_exactly_once_in_order_despite_duplicates_and_reordering()
    {
        var e = new SyncEngine(Schema, 1, false, 0xCCCC);
        e.OnConnected(1);
        e.OnMessage(new Hello(SyncEngine.ProtocolVersion, Schema.Hash, 0xAAAA, 0x5E55, 0, true, new Authority(0, 0)), 1);
        e.SimCommands.Clear();

        EventEntry Ev(ulong seq) => new(seq, AltPush, seq * 10);
        e.OnMessage(new EventBatch(0, [Ev(2), Ev(3)]), 2);
        Assert.Empty(e.SimCommands); // waits for #1
        e.OnMessage(new EventBatch(0, [Ev(1), Ev(2)]), 3);
        e.OnMessage(new EventBatch(0, [Ev(1), Ev(2), Ev(3), Ev(4)]), 4);

        var fired = e.SimCommands.OfType<FireEvent>().Select(f => f.Param).ToArray();
        Assert.Equal([10.0, 20.0, 30.0, 40.0], fired);
        Assert.Equal(4, e.Health.EventDuplicates);
    }

    [Fact]
    public void Events_survive_a_disconnect()
    {
        var p = Connected();
        p.Host.OnDisconnected(p.Now);
        p.Join.OnDisconnected(p.Now);

        p.Join.OnLocalEvent(AltPush, 7, p.Now);
        p.Join.OnLocalEvent(AltPush, 8, p.Now);
        p.Run(1000);
        Assert.Empty(p.HostCmds.OfType<FireEvent>());

        p.Connect();
        p.Run(1000);
        Assert.Equal([7.0, 8.0], p.HostCmds.OfType<FireEvent>().Select(f => f.Param));
        Assert.Equal(0, p.Join.UnackedEventCount);
    }

    [Fact]
    public void Different_aircraft_definitions_refuse_to_sync()
    {
        var other = new SyncSchema([new GroupSpec("elec", 10)], [new VarSpec("battery", SyncClass.State, "elec")]);
        var p = new Pair(Schema, joinerSchema: other);
        p.Connect();
        p.Run(500);

        Assert.False(p.Host.IsEstablished);
        Assert.False(p.Join.IsEstablished);
        Assert.True(p.Host.Health.Incompatible);
        Assert.Contains("definition", p.Join.Health.IncompatibleReason);
    }

    [Fact]
    public void Control_handover_is_a_handshake()
    {
        var p = Connected();
        Assert.True(p.Host.IsPilotFlying);

        p.Join.RequestControl(p.Now);
        p.Run(50);

        Assert.True(p.Join.IsPilotFlying);
        Assert.False(p.Host.IsPilotFlying);
        Assert.Equal(p.Host.Authority, p.Join.Authority);
        Assert.Contains(p.HostCmds, c => c is AuthorityChanged { IsPilotFlying: false });
        Assert.Contains(p.JoinCmds, c => c is AuthorityChanged { IsPilotFlying: true });
    }

    [Fact]
    public void Knob_stream_ends_with_a_reliable_settle_value()
    {
        var p = Connected();
        for (int v = 100; v <= 140; v += 5)
        {
            p.HostSim[Hdg] = v;
            p.Advance(20);
        }
        p.Run(300);

        Assert.Equal(140, p.JoinSim[Hdg]);
        Assert.Contains(p.HostSent, m => m is StateDelta d && d.Entries.Any(e => e.Var == Hdg && e.Value == 140));
    }
}
