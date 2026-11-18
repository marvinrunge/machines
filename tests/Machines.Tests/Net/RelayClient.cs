using System.Collections.Concurrent;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Ai;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>What a client's thread saw of its pace: when (ms since it started), its world's tick, the newest bundle in hand, and the relay's clock tick.</summary>
internal readonly record struct PaceSample(double Ms, long Tick, long Head, long? Clock);

/// <summary>
/// One client as the game runs it: its connection, and once the match began its world, its lockstep session on the relay's
/// bundles and who it is there; besides, what a test reads off it (every checksum it reported, every order of its refused,
/// the comings and goings it heard, its seat key, whether the relay took it out) and the script of what its person does.
/// </summary>
/// <remarks>
/// The harness's core, free of the test framework (P2 design §9, S6), so that tools/net-bots compiles it too and fills seats
/// of a room with clients like these against any relay.
/// </remarks>
internal sealed class RelayClient(Connection connection, string name, bool web, GameData data) : IAsyncDisposable
{
    private Thread? _pacer;
    private volatile bool _pacing;
    private volatile bool _frozen;
    private long _tick;

    /// <summary>
    /// A client says hello to the relay at <paramref name="address"/> ("ws://host:port" over a WebSocket, else "host:port"
    /// over TCP) under <paramref name="name"/>, asking to keep its seat over a dropped line as the game does, and is welcomed;
    /// its worlds are built from <paramref name="data"/>.
    /// </summary>
    public static async Task<RelayClient> Connect(string address, string name, GameData data)
    {
        var connection = await Connection.ConnectAsync(address, CancellationToken.None);
        connection.Send(new Hello(name, NetJson.Protocol, Resume: true));
        var client = new RelayClient(connection, name, address.StartsWith("ws", StringComparison.OrdinalIgnoreCase), data);
        client.Id = (await client.Next<Welcome>()).ClientId;
        return client;
    }

    /// <summary>The game's data the client's worlds are built from.</summary>
    public GameData Data { get; } = data;

    public Connection Connection { get; } = connection;

    /// <summary>The name the client said hello with, by which it rejoins.</summary>
    public string Name { get; } = name;

    /// <summary>Whether it talks to the relay over a WebSocket, else over TCP.</summary>
    public bool Web { get; } = web;

    public int Id { get; set; }

    /// <summary>The room of its match.</summary>
    public int RoomId { get; set; }

    public MatchInfo Match { get; private set; } = null!;
    public LockstepSession Session { get; private set; } = null!;
    public GameWorld World => Session.World;

    /// <summary>The player the client is in the match.</summary>
    public PlayerId Me { get; private set; }

    /// <summary>The race (team) the client plays.</summary>
    public Race Race { get; private set; }

    /// <summary>What the client's person does each tick a turn of theirs goes out; nothing without one.</summary>
    public RelayScript? Script { get; set; }

    /// <summary>Called after each tick the client's world ran, for a test to look at the world just then.</summary>
    public Action<GameWorld>? AfterStep { get; set; }

    /// <summary>The slowest tick the client's thread ran and the slowest look its script took at the world (ms, and the tick).</summary>
    public (double Ms, long Tick) SlowestTick { get; private set; }

    /// <inheritdoc cref="SlowestTick"/>
    public (double Ms, long Tick) SlowestThink { get; private set; }

    /// <summary>
    /// With the world's <see cref="GameWorld.Profile"/> set: each tick over 100 ms, with the parts that took over 5 ms of it,
    /// the time outside the world's parts (the orders given before its step) among them.
    /// </summary>
    public ConcurrentQueue<string> SlowTicks { get; } = new();

    // A tick on the client's thread, timed.
    private bool Step()
    {
        var before = World.Profile is { } profile ? new Dictionary<string, double>(profile) : null;
        var start = Stopwatch.GetTimestamp();
        if (!Session.TryStep())
            return false;
        var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        if (ms > SlowestTick.Ms)
            SlowestTick = (ms, World.Tick);
        if (ms > 100 && before is not null && World.Profile is { } after)
        {
            var parts = after.Select(p => (p.Key, Ms: p.Value - before.GetValueOrDefault(p.Key))).ToList();
            // The world times the parts of its step (an order's own part indented under "machines"); the rest of the tick is
            // the session's, mostly the turns' orders given before the step (a path search for each move).
            var rest = ms - parts.Where(p => !p.Key.StartsWith(' ')).Sum(p => p.Ms);
            SlowTicks.Enqueue($"tick {World.Tick} {ms:F0} ms: " + string.Join(", ", parts.Append((Key: "orders given and the rest", Ms: rest))
                .Where(p => p.Ms > 5).OrderByDescending(p => p.Ms).Select(p => $"{p.Key} {p.Ms:F0}")));
        }
        return true;
    }

    /// <summary>Every checksum the client reported to the relay, by tick.</summary>
    public SortedDictionary<long, ulong> Sums { get; } = [];

    /// <summary>Every order refused in the client's world, with the tick it ran in.</summary>
    public List<(long Tick, CommandRejected Refusal)> Refused { get; } = [];

    /// <summary>Whether the client left (closed its connection); it plays no more.</summary>
    public bool Gone { get; private set; }

    /// <summary>The key the relay gave the client's seat (<see cref="SeatKey"/>, or a new one with <see cref="MatchResumed"/>).</summary>
    public string? Key { get; set; }

    /// <summary>Why the relay took the client out of its match (<see cref="LeftMatch"/>), until it comes back; null while it plays.</summary>
    public LeftMatch? Out { get; private set; }

    /// <summary>The end the relay agreed for the match, once it did.</summary>
    public MatchEnded? Ended { get; private set; }

    /// <summary>The newest rebuild the relay sent the client, and how many it took.</summary>
    public MatchResumed? Resumed { get; private set; }

    public int Rebuilds { get; private set; }

    /// <summary>Every rebuild the relay sent the client, in order: the one that replaced its world <c>i</c> at <c>[i]</c>.</summary>
    public List<MatchResumed> Rebuilt { get; } = [];

    /// <summary>How often the relay rebuilt the client's world for being out of step (a resync).</summary>
    public int Resyncs => Rebuilt.Count(r => r.Reason == ResumeReason.Resync);

    /// <summary>
    /// Every checksum the client reported, with the world it came from: 0 for the one the match began with, then one more
    /// for each rebuild (<see cref="Rebuilds"/> when it was reported).
    /// </summary>
    public List<(int World, long Tick, ulong Value)> History { get; } = [];

    /// <summary>Every time <see cref="Corrupt"/> spoiled one of the client's worlds: which, and after which tick.</summary>
    public List<(int World, long Tick)> Corruptions { get; } = [];

    /// <summary>The script the client's person follows in a world rebuilt from the relay's log; without, the old one goes on.</summary>
    public Func<RelayClient, RelayScript?>? Rescript { get; set; }

    private (long Tick, bool Every)? _spoil;

    /// <summary>Whether the client keeps its <see cref="MatchLoaded"/> back when its world is built, until <see cref="Load"/>.</summary>
    public bool HoldLoad { get; set; }

    /// <summary>The comings and goings of every bundle the client took, as they came (for a test on another thread).</summary>
    public ConcurrentQueue<NetMessage> Heard { get; } = new();

    /// <summary>Every 100 ms of its thread, where the client stood (<see cref="Run"/>).</summary>
    public ConcurrentQueue<PaceSample> Samples { get; } = new();

    /// <summary>Every gap in the relay's numbering the session met: the tick expected, the tick that came.</summary>
    public ConcurrentQueue<(long Expected, long Got)> Gaps { get; } = new();

    /// <summary>Every refusal the relay sent during the match.</summary>
    public ConcurrentQueue<string> Refusals { get; } = new();

    /// <summary>What went wrong on the client's thread, if anything did.</summary>
    public Exception? Failure { get; private set; }

    /// <summary>The tick its world stands at, for a test on another thread.</summary>
    public long Tick => Interlocked.Read(ref _tick);

    /// <summary>Whether the client plays a match: begun, not taken out, not gone.</summary>
    public bool InMatch => Session is not null && Out is null && !Gone;

    /// <summary>A frozen client reads, sends and steps nothing, as a hung game; its clock runs on.</summary>
    public bool Frozen
    {
        get => _frozen;
        set => _frozen = value;
    }

    /// <summary>The next message of a kind the client was sent, before its match; a refusal fails the test.</summary>
    public async Task<T> Next<T>(Func<T, bool>? wanted = null) where T : NetMessage
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        while (true)
        {
            var message = await Connection.Inbox.ReadAsync(timeout.Token);
            if (message is Refused refused)
                throw new InvalidOperationException($"{Name}: {refused.Reason}");
            if (message is T typed && (wanted is null || wanted(typed)))
                return typed;
        }
    }

    public async Task Join(int room)
    {
        Connection.Send(new JoinRoom(room));
        await Next<RoomState>(s => s.Players.Any(p => p.ClientId == Id));
    }

    /// <summary>Moves to a free open seat (<see cref="TakeSeat"/>) and waits until the room shows it there.</summary>
    public async Task TakeSeat(int seat)
    {
        Connection.Send(new TakeSeat(seat));
        await Next<RoomState>(s => s.Seats[seat].ClientId == Id);
    }

    /// <summary>
    /// The match begins for this client, as the game begins it: the world built from the match's table, the session on the
    /// relay's bundles for the player the client is (the row of its client, or the one it comes back as), and once built,
    /// unless held back, <see cref="MatchLoaded"/>.
    /// </summary>
    public void Begin(MatchInfo match, PlayerId? me = null)
    {
        Match = match;
        var table = match.Table();
        Me = me ?? table.Rows[match.Players.ToList().FindIndex(p => p.ClientId == Id && !p.Ai)].Id;
        Race = table.RaceOf(Me);
        var session = new LockstepSession(Replay.BuildWorld(Data, match), Me, match.InputDelay, mode: SessionMode.Bundles);
        session.Outgoing += Send;
        session.Gap += (expected, got) => Gaps.Enqueue((expected, got));
        Session = session;
        Interlocked.Exchange(ref _tick, 0);
        Out = null;
        _decided = false;
        if (!HoldLoad)
            Load();
    }

    /// <summary>
    /// Where the client says what happens to it as the game's log does, for a tool comparing it with the games' logs: its
    /// checksums ("checksum at tick N: x"), every 100 ticks its pace ("pace: tick T head H utc ms lag l away a"), its
    /// catch-ups, rebuilds and the comings and goings it ran; nothing without.
    /// </summary>
    public Action<string>? Log { get; set; }

    /// <summary>Every coming or going of a bundle the client took, with when it came (Unix ms, UTC).</summary>
    public ConcurrentQueue<(NetMessage Change, long Utc)> Arrivals { get; } = new();

    /// <summary>Every turn the client sent, the tick it asked for and when (Unix ms, UTC).</summary>
    public ConcurrentQueue<(long Stamp, long Utc)> Stamps { get; } = new();

    /// <summary>When each rebuild of <see cref="Rebuilt"/> came (Unix ms, UTC).</summary>
    public ConcurrentQueue<long> RebuiltAt { get; } = new();

    /// <summary>The bytes of the relay's bundles and log parts the client took, as JSON (the frames' payload).</summary>
    public long Received => Interlocked.Read(ref _received);

    private long _received;

    /// <summary>Whether the client tells the relay when its world decides the match (<see cref="MatchDecided"/>), as the game does.</summary>
    public bool SaysDecided { get; set; }

    // Whether the client's world decided the match and said so, once a world.
    private bool _decided;

    /// <summary>Unix milliseconds (UTC) now.</summary>
    public static long Utc => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();

    /// <summary>
    /// The client's world runs at most <paramref name="rate"/> ticks a second (catching up included) for
    /// <paramref name="length"/> from when the relay's clock passes <paramref name="from"/> (or, without a clock, its world
    /// does), as a computer that cannot keep up would (the game's MACHINES_THROTTLE, P2 design §9 S4/S6).
    /// </summary>
    public void Throttle(long from, TimeSpan length, double rate) => _throttle = (from, length, rate);

    /// <summary>When the throttle began and ended (Unix ms, UTC), once it did.</summary>
    public long? ThrottledAt { get; private set; }

    public long? ThrottleOverAt { get; private set; }

    private (long From, TimeSpan Length, double Rate)? _throttle;
    private double _throttleTokens;

    /// <summary>The client says its world is built.</summary>
    public void Load() => Connection.Send(new MatchLoaded());

    /// <summary>
    /// Something only this client's world has, a cheat or a bug (P2 design §9 S3): just after the world ran
    /// <paramref name="tick"/>, the client's hero loses a hit point in a battle (while it lives with more than one), else its
    /// race gains a BMU; once, or with <paramref name="every"/> in each world rebuilt from the log as well.
    /// </summary>
    public void Corrupt(long tick, bool every = false) => _spoil = (tick, every);

    /// <summary>The client asks for its seat back with its key over the connection it plays on, as the game does after a gap.</summary>
    public void RejoinHere() => Connection.Send(new RejoinMatch(RoomId, Me, Key));

    private void Send(NetMessage message)
    {
        Connection.Send(message);
        if (message is ChecksumReport report)
        {
            Sums[report.Tick] = report.Value;
            History.Add((Rebuilds, report.Tick, report.Value));
            Log?.Invoke($"checksum at tick {report.Tick}: {report.Value:x16}");
        }
        else if (message is Turn turn)
            Stamps.Enqueue((turn.Tick, Utc));
    }

    /// <summary>Everything that arrived.</summary>
    private void Read()
    {
        while (Connection.Inbox.TryRead(out var message))
            Take(message);
    }

    /// <summary>A message of the relay: the match's to the session, the rest noted (a rebuild begins the match afresh).</summary>
    internal void Take(NetMessage message)
    {
        switch (message)
        {
            case SeatKey key:
                (RoomId, Key) = (key.RoomId, key.Key);
                break;
            case MatchResumed resumed:
                (Resumed, Rebuilds) = (resumed, Rebuilds + 1);
                Rebuilt.Add(resumed);
                RebuiltAt.Enqueue(Utc);
                Log?.Invoke($"net: rebuilt from tick {resumed.FromTick} ({resumed.Reason}), resync {resumed.Resyncs}");
                Key = resumed.Key ?? Key;
                Begin(resumed.Match, resumed.Player);
                if (Rescript is { } script)
                    Script = script(this);
                break;
            case LeftMatch left:
                Out = left;
                Log?.Invoke($"net: taken out of the match: {left.Reason}");
                break;
            case MatchEnded ended:
                Ended = ended;
                Log?.Invoke($"net: the match was decided at tick {ended.Tick}");
                break;
            case Refused refused:
                Refusals.Enqueue(refused.Reason);
                break;
            case Bundle bundle when InMatch:
                Interlocked.Add(ref _received, NetJson.Encode(bundle).Length);
                var at = Utc;
                foreach (var change in bundle.Presence ?? [])
                {
                    Heard.Enqueue(change);
                    Arrivals.Enqueue((change, at));
                    Log?.Invoke($"presence: {change}");
                }
                Session.Receive(bundle);
                break;
            default:
                if (message is LogPart part)
                    Interlocked.Add(ref _received, NetJson.Encode(part).Length);
                if (InMatch)
                    Session.Receive(message);
                break;
        }
    }

    /// <summary>
    /// One frame as fast as it goes: what arrived in, the client's next turn out (its person's orders in it), and the ticks that
    /// can run, up to 40 as the game catches up and never past <paramref name="limit"/>, a turn after each as the game sends
    /// one each slot; returns whether the world ran a tick.
    /// </summary>
    public bool Frame(long limit)
    {
        if (Gone || Frozen)
            return false;
        Read();
        if (!InMatch)
            return false;
        Act();
        var stepped = false;
        for (var steps = 0; steps < 40 && InMatch && World.Tick < limit && Session.TryStep(); steps++)
        {
            stepped = true;
            Stepped();
            Read();
            if (InMatch)
                Act();
        }
        return stepped;
    }

    private void Stepped()
    {
        if (_spoil is { } spoil && World.Tick == spoil.Tick)
        {
            if (World.Battle is not null && World.HeroOf(Me) is { IsDead: false, HitPoints: > 1 } hero)
                hero.HitPoints -= 1;
            else
                World.Races[Race].Bmu += 1;
            Corruptions.Add((Rebuilds, World.Tick));
            if (!spoil.Every)
                _spoil = null;
        }
        Interlocked.Exchange(ref _tick, World.Tick);
        foreach (var refusal in World.Rejected)
            Refused.Add((World.Tick, refusal));
        // The first time the world decides the match for everyone (a winner, a battle over, a run ended), as the game says it
        // (P2 design §2.8); a race's own defeat decides nothing.
        if (SaysDecided && !_decided && (World.Winner is not null || World.Battle is { Over: true } || World.Wired is { Phase: WiredPhase.Ended }))
        {
            _decided = true;
            Connection.Send(new MatchDecided(World.Tick));
            Log?.Invoke($"net: decided at tick {World.Tick}");
        }
        if (Log is { } log && World.Tick % 100 == 0)
            log($"pace: tick {World.Tick} head {Session.NewestBundle} utc {Utc} lag {(Session.Lagging(Me) ? 1 : 0)} away {(Session.Away(Me, World.Tick) ? 1 : 0)}");
        AfterStep?.Invoke(World);
    }

    /// <summary>Sends the turn for the tick ahead once the world has come to it, with what the script orders now.</summary>
    private void Act()
    {
        if (Session.NextLocalTurn > World.Tick + Session.InputDelay)
            return;
        if (Script is { } script)
        {
            var start = Stopwatch.GetTimestamp();
            foreach (var command in script.Next(World))
                Session.Queue(command);
            if (Stopwatch.GetElapsedTime(start).TotalMilliseconds is var ms && ms > SlowestThink.Ms)
                SlowestThink = (ms, World.Tick);
        }
        Session.Pump();
    }

    /// <summary>
    /// The client runs on a thread of its own as the game paces its frames (P2 design §4.3), a frame every 16 ms: what arrived
    /// in; then with a backlog of <see cref="BundlePacer.CatchUpLead"/> bundles or more it catches up for the frame's budget and
    /// sends one turn, else every 50 ms slot owed (at most a quarter second's) takes what arrived meanwhile, sends a turn and
    /// runs the ticks <see cref="BundlePacer.TicksForSlot"/> gives. Every 100 ms it notes where it stands against
    /// <paramref name="clock"/>.
    /// </summary>
    public void Run(Func<long?>? clock = null)
    {
        if (_pacer is not null)
            return;
        _pacing = true;
        _pacer = new Thread(() => Pace(clock)) { IsBackground = true, Name = $"client {Name}" };
        _pacer.Start();
    }

    /// <summary>The client's thread stops.</summary>
    public void Halt()
    {
        _pacing = false;
        _pacer?.Join();
        _pacer = null;
    }

    private void Pace(Func<long?>? clock)
    {
        var slot = TimeSpan.FromSeconds(1.0 / GameWorld.TicksPerSecond);
        var watch = Stopwatch.StartNew();
        var last = watch.Elapsed;
        var owed = TimeSpan.Zero;
        var sampled = TimeSpan.Zero;
        TimeSpan? catchingUp = null;
        try
        {
            while (_pacing)
            {
                if (Frozen)
                {
                    Thread.Sleep(5);
                    continue;
                }
                var now = watch.Elapsed;
                var delta = now - last < TimeSpan.FromSeconds(0.25) ? now - last : TimeSpan.FromSeconds(0.25);
                last = now;
                Read();
                if (InMatch)
                {
                    if (_throttle is { } throttle)
                        _throttleTokens = Math.Min(_throttleTokens + delta.TotalSeconds * throttle.Rate, 2);
                    var budget = BundlePacer.CatchUpBudget(Session.Ready(BundlePacer.LongCatchUp));
                    if (budget > TimeSpan.Zero)
                    {
                        if (catchingUp is null)
                        {
                            catchingUp = now;
                            Log?.Invoke($"net: catching up from tick {World.Tick}, {Session.NewestBundle - World.Tick + 1} bundles in hand");
                        }
                        var until = now + budget;
                        while (InMatch && watch.Elapsed < until && MayStep(clock) && Step())
                            Stepped();
                        if (InMatch)
                            Act();
                        owed = TimeSpan.Zero;
                    }
                    else
                    {
                        if (catchingUp is { } since)
                        {
                            Log?.Invoke($"net: caught up at tick {World.Tick} after {(now - since).TotalSeconds:0.0} s");
                            catchingUp = null;
                        }
                        owed += delta;
                        while (InMatch && owed >= slot)
                        {
                            owed -= slot;
                            // What came in while the slots before ran, as the game reads it: the turn's Held must count the
                            // bundles that arrived during a slow tick, or it is sent as if made without a backlog (§2.7).
                            Read();
                            if (!InMatch)
                                break;
                            Act();
                            for (var ticks = BundlePacer.TicksForSlot(Session.Ready(BundlePacer.CatchUpLead)); ticks > 0 && MayStep(clock) && Step(); ticks--)
                                Stepped();
                        }
                    }
                    if (watch.Elapsed - sampled >= TimeSpan.FromMilliseconds(100) && InMatch)
                    {
                        sampled = watch.Elapsed;
                        Samples.Enqueue(new PaceSample(sampled.TotalMilliseconds, World.Tick, Session.NewestBundle, clock?.Invoke()));
                    }
                }
                Thread.Sleep(16);
            }
        }
        catch (Exception exception)
        {
            Failure = exception;
            Log?.Invoke($"failed: {exception}");
        }
    }

    /// <summary>Whether the world may run its next tick now: always, but while <see cref="Throttle"/> holds it back (the game's MayStep).</summary>
    private bool MayStep(Func<long?>? clock)
    {
        if (_throttle is not { } throttle || ThrottleOverAt is not null)
            return true;
        if (ThrottledAt is null)
        {
            if ((clock?.Invoke() ?? World.Tick) < throttle.From)
                return true;
            ThrottledAt = Utc;
            Log?.Invoke($"net: throttled to {throttle.Rate:0.#} ticks a second for {throttle.Length.TotalSeconds:0.#} s from tick {World.Tick}");
        }
        if (Utc - ThrottledAt >= throttle.Length.TotalMilliseconds)
        {
            ThrottleOverAt = Utc;
            Log?.Invoke($"net: throttle over at tick {World.Tick} ({Session.NewestBundle + 1} bundles in hand)");
            return true;
        }
        if (_throttleTokens < 1)
            return false;
        _throttleTokens -= 1;
        return true;
    }

    /// <summary>The client closes on purpose (the game quits the match): the relay tells the others at once.</summary>
    public Task Leave() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
        Halt();
        if (Gone)
            return;
        Gone = true;
        await Connection.DisposeAsync();
    }
}

/// <summary>
/// What a person at a client does, from the world as their client sees it and a <see cref="System.Random"/> of their own: a
/// script only reads the world, and what it makes goes into the person's turn.
/// </summary>
internal abstract class RelayScript(PlayerId player, int seed)
{
    public PlayerId Player { get; } = player;

    protected Random Random { get; } = new(seed);

    /// <summary>The commands of the turn the person sends at the world's present tick.</summary>
    public abstract IReadOnlyList<Command> Next(GameWorld world);

    protected T Pick<T>(IReadOnlyList<T> list) => list[Random.Next(list.Count)];

    protected List<T> Some<T>(IReadOnlyList<T> list, int most) =>
        list.OrderBy(_ => Random.Next()).Take(Math.Max(1, Random.Next(most) + 1)).ToList();

    /// <summary>A point within <paramref name="reach"/> of another, kept on the map.</summary>
    protected Vector2 Near(GameWorld world, Vector2 from, int reach)
    {
        var at = from + new Vector2(Random.Next(-reach, reach + 1), Random.Next(-reach, reach + 1));
        return Vector2.Clamp(at, new Vector2(10, 10), new Vector2(world.Planet.Surface.Width - 10, world.Planet.Surface.Depth - 10));
    }
}

/// <summary>
/// A battle hero's person who plays as the computer's hero does (P2 design §9, S6): a sim <see cref="BattleBot"/> for their
/// player looks at the client's world every <see cref="BattleBot.ThinkEvery"/> ticks, on a tick of their own (staggered by
/// player, so that not every person's orders fall on one tick), and what it orders goes into the person's turn; a world
/// rebuilt from the relay's log gets a bot of its own.
/// </summary>
internal sealed class BattleBotScript(PlayerId player) : RelayScript(player, 0)
{
    private readonly List<Command> _orders = [];
    private GameWorld? _world;
    private BattleBot? _bot;
    private long _next;

    public override IReadOnlyList<Command> Next(GameWorld world)
    {
        if (!ReferenceEquals(world, _world))
            (_world, _bot, _next) = (world, new BattleBot(world, Player, _orders.Add), 0);
        if (world.Tick < _next)
            return [];
        // The next look on this person's tick of the ten: the client sends a turn once a slot, and may run a few ticks a slot.
        var phase = Player.Value % BattleBot.ThinkEvery;
        _next = world.Tick - world.Tick % BattleBot.ThinkEvery + phase;
        if (_next <= world.Tick)
            _next += BattleBot.ThinkEvery;
        _orders.Clear();
        _bot!.Think();
        return [.. _orders];
    }
}

/// <summary>
/// The finer timer of Windows for this process (<c>timeBeginPeriod(1)</c>), as the game and the relay take it, so that a client's
/// thread sleeps the 16 ms of a frame rather than the 31 of two of the default's ticks; nothing elsewhere.
/// </summary>
internal sealed class FineTimer : IDisposable
{
    private readonly bool _taken = OperatingSystem.IsWindows() && TimeBeginPeriod(1) == 0;

    public void Dispose()
    {
        if (_taken)
            TimeEndPeriod(1);
    }

    [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
    private static extern uint TimeBeginPeriod(uint period);

    [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
    private static extern uint TimeEndPeriod(uint period);
}
