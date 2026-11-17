using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// A relay as it runs on the internet, TCP and WebSocket into the same rooms, and any number of clients of it, each run as
/// the game runs one (P1 slice S4b, P2 slice S2a): they connect, open or join a room, take seats, say who they are and that
/// they are ready, begin the match the relay starts, say once their world is built, and from then on play it on the relay's
/// bundles, a world and a lockstep session each, until the test has them leave or come back. Either the test plays them
/// frame by frame as fast as they go (<see cref="PlayTo"/>, for a relay with <see cref="BeatTimings.Scripted"/>), or each
/// runs on a thread of its own on the real clock, as the game paces its frames (<see cref="Run"/>).
/// </summary>
internal sealed class RelayClients : IAsyncDisposable
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    /// <summary>How long a match may take to reach a tick before the test gives up and says who was where.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    /// <summary>How far ahead of the slowest client in the match a client may step when the test plays them (the pace rule).</summary>
    public const int Lead = 40;

    private readonly List<RelayClient> _clients = [];

    private RelayClients(RelayServer relay, RelayHttp web) => (Relay, Web) = (relay, web);

    public static GameData Data => DataLoader.Value;

    public RelayServer Relay { get; }
    public RelayHttp Web { get; }

    /// <summary>Every line the relay wrote to its log, in order.</summary>
    public ConcurrentQueue<string> Lines { get; } = new();

    /// <summary>Every client connected so far, whether it still plays or not.</summary>
    public IReadOnlyList<RelayClient> Clients => _clients;

    /// <summary>
    /// The relay's TCP listener and its web side, on free ports of this computer, keeping the beat by <paramref name="beat"/>:
    /// without, <see cref="BeatTimings.Scripted"/>, a bundle closing as soon as everyone's turn for it is in.
    /// </summary>
    public static async Task<RelayClients> Start(BeatTimings? beat = null, LinkTimings? link = null)
    {
        var relay = RelayServer.Start(0, IPAddress.Loopback, link, beat ?? BeatTimings.Scripted);
        var clients = new RelayClients(relay, await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback));
        relay.Log += clients.Lines.Enqueue;
        return clients;
    }

    /// <summary>
    /// A client says hello under <paramref name="name"/>, asking to keep its seat over a dropped line as the game does, over
    /// a WebSocket (<paramref name="web"/>, the public relay's way) or over TCP (the LAN's), with <paramref name="via"/> to that
    /// port of this computer (a <see cref="LineProxy"/> to the relay) instead of the relay's own.
    /// </summary>
    public async Task<RelayClient> Connect(string name, bool web, int? via = null)
    {
        var address = web ? $"ws://127.0.0.1:{via ?? Web.Port}" : $"127.0.0.1:{via ?? Relay.Port}";
        var connection = await Connection.ConnectAsync(address, CancellationToken.None);
        connection.Send(new Hello(name, NetJson.Protocol, Resume: true));
        var client = new RelayClient(connection, name, web);
        client.Id = (await client.Next<Welcome>()).ClientId;
        _clients.Add(client);
        return client;
    }

    /// <summary>
    /// The host opens a room and the guests join it one after the other, each once the one before is in; returns the room
    /// as everyone then sees it.
    /// </summary>
    public static async Task<RoomState> Open(RelayClient host, CreateRoom create, params RelayClient[] guests)
    {
        host.Connection.Send(create);
        var room = (await host.Next<RoomState>(s => s.Players.Any(p => p.ClientId == host.Id))).Id;
        foreach (var guest in guests)
            await guest.Join(room);
        return await host.Next<RoomState>(s => s.Players.Count == guests.Length + 1);
    }

    /// <summary>
    /// Everyone in the room says, one after the other, that they are ready, in the colour the room shows them, in a battle
    /// with the hero <paramref name="hero"/> gives them, and with the ground fingerprint <paramref name="terrain"/> (which the
    /// match then has, <see cref="MatchInfo.Terrain"/>); the host starts and each client begins the match the relay sent
    /// (with <paramref name="play"/> false only takes it, for a test to play it elsewhere).
    /// </summary>
    public static async Task<MatchInfo> Begin(RelayClient host, IReadOnlyList<RelayClient> everyone, Func<RelayClient, BattleHero?>? hero = null, bool play = true,
        long? terrain = null)
    {
        // The room as it stands: the relay shows it again to a member who asks for the list of rooms.
        host.Connection.Send(new ListRooms());
        var state = await host.Next<RoomState>(s => s.Players.Count == everyone.Count);
        foreach (var client in everyone)
        {
            client.Connection.Send(new SetPlayer(state.Players.Single(p => p.ClientId == client.Id).Colour, Ready: true, terrain, hero?.Invoke(client)));
            state = await host.Next<RoomState>(s => s.Players.Single(p => p.ClientId == client.Id).Ready);
        }
        host.Connection.Send(new StartMatch());
        MatchInfo? match = null;
        foreach (var client in everyone)
        {
            match = (await client.Next<MatchStarted>()).Match;
            client.RoomId = state.Id;
            if (play)
                client.Begin(match);
        }
        return match!;
    }

    /// <summary>
    /// A client who dropped out of a running match connects again under <paramref name="name"/> and asks for their seat back
    /// by name (<see cref="RejoinMatch"/>, naming <paramref name="player"/> when given, as the game does when it still knows
    /// it), then rebuilds the world from the relay's log and catches up as it plays.
    /// </summary>
    public async Task<RelayClient> Rejoin(string name, bool web, int room, PlayerId? player = null, int? via = null)
    {
        var client = await Connect(name, web, via);
        client.RoomId = room;
        // The old connection's goodbye closes its line at once, but the relay lets the player go only when it is done with
        // that connection, which may come after this one asked: until then nobody of the name has left, and it asks again.
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        client.Connection.Send(new RejoinMatch(room, player));
        while (true)
        {
            switch (await client.Connection.Inbox.ReadAsync(patience.Token))
            {
                case MatchResumed resumed:
                    client.Take(resumed);
                    return client;
                case Refused { Reason: var why } when why.StartsWith("no player called", StringComparison.Ordinal) && !patience.IsCancellationRequested:
                    await Task.Delay(20, CancellationToken.None);
                    client.Connection.Send(new RejoinMatch(room, player));
                    break;
                case Refused refused:
                    throw new InvalidOperationException($"{name}: {refused.Reason}");
            }
        }
    }

    /// <summary>
    /// The person of <paramref name="old"/> comes back over a new connection with their seat key (P2 design §6.1), whether or
    /// not the old one still holds the seat, and rebuilds the world from the relay's log.
    /// </summary>
    public async Task<RelayClient> RejoinWithKey(RelayClient old, bool web, int? via = null)
    {
        var client = await Connect(old.Name, web, via);
        (client.RoomId, client.Key) = (old.RoomId, old.Key);
        client.Connection.Send(new RejoinMatch(old.RoomId, old.Me, old.Key));
        client.Take(await client.Next<MatchResumed>());
        return client;
    }

    /// <summary>
    /// A player joins the running battle of <paramref name="room"/> late under <paramref name="name"/> (P2 design §6.2), in
    /// the bot's seat the relay picks or the one <paramref name="seat"/> names, with the ground fingerprint
    /// <paramref name="terrain"/>, and rebuilds the world from the relay's log while the bot plays on; a refusal throws
    /// (<see cref="InvalidOperationException"/>, "name: reason").
    /// </summary>
    public async Task<RelayClient> LateJoin(string name, bool web, int room, int? seat = null, long? terrain = null)
    {
        var client = await Connect(name, web);
        client.RoomId = room;
        client.Connection.Send(new JoinRoom(room, seat, terrain));
        client.Take(await client.Next<MatchResumed>());
        return client;
    }

    /// <summary>
    /// Plays every client still in the match until each world stands at <paramref name="tick"/>: frames of all of them in turn,
    /// none stepping past the slowest by more than <see cref="Lead"/> (so one rebuilding catches up), waiting for the relay
    /// only when no world could step.
    /// </summary>
    public static async Task PlayTo(long tick, IEnumerable<RelayClient> clients)
    {
        var all = clients.Where(c => !c.Gone).ToList();
        var watch = Stopwatch.StartNew();
        while (all.Where(c => c.InMatch).Any(c => c.World.Tick < tick))
        {
            if (watch.Elapsed > Patience)
                Assert.Fail($"the match did not reach tick {tick} in {Patience.TotalSeconds:0} s: "
                            + string.Join("; ", all.Select(c => $"{c.Name} (player {c.Me}) at {c.World.Tick}, bundles to {c.Session.NewestBundle}{(c.InMatch ? "" : ", out")}")));
            var playing = all.Where(c => c.InMatch).ToList();
            var limit = playing.Count == 0 ? tick : Math.Min(tick, playing.Min(c => c.World.Tick) + Lead);
            var stepped = false;
            foreach (var client in all)
                stepped |= client.Frame(limit);
            if (!stepped)
                await Arrival(all);
        }
    }

    /// <summary>Until something arrives for one of the clients, at most a frame's time.</summary>
    private static async Task Arrival(IReadOnlyList<RelayClient> clients)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        var waits = clients.Where(c => !c.Connection.Closed).Select(c => c.Connection.Inbox.WaitToReadAsync(stop.Token).AsTask()).ToList();
        if (waits.Count == 0)
        {
            await Task.Delay(1);
            return;
        }
        await Task.WhenAny(waits);
        stop.Cancel();
        foreach (var wait in waits)
        {
            try
            {
                await wait;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    /// <summary>Each client runs on a thread of its own, on the real clock as the game paces its frames (<see cref="RelayClient.Run"/>).</summary>
    public static void Run(IEnumerable<RelayClient> clients, Func<long?>? clock = null)
    {
        foreach (var client in clients)
            client.Run(clock);
    }

    /// <summary>The clients' threads stop; their worlds stand where they are.</summary>
    public static void Halt(IEnumerable<RelayClient> clients)
    {
        foreach (var client in clients)
            client.Halt();
    }

    /// <summary>Waits until <paramref name="done"/>, at most <paramref name="within"/>, else fails the test saying what did not happen.</summary>
    public static async Task Until(Func<bool> done, TimeSpan within, string what)
    {
        var watch = Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > within)
                Assert.Fail($"{what}: not within {within.TotalSeconds:0.#} s");
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// The clients' worlds agree: those still playing stand at the same tick with the same checksum, nobody was resynced
    /// (unless <paramref name="resyncs"/>), and every checksum any of them reported is the one every other reported for that
    /// tick (<see cref="AssertSumsAgree"/>). Returns how many ticks were compared.
    /// </summary>
    public static int AssertAgree(IEnumerable<RelayClient> clients, bool resyncs = false)
    {
        var all = clients.ToList();
        var playing = all.Where(c => c.InMatch).ToList();
        Assert.Single(playing.Select(c => c.World.Tick).Distinct());
        Assert.Single(playing.Select(c => c.World.Checksum()).Distinct());
        var compared = AssertSumsAgree(all, resyncs);
        // Everyone still playing reported every checksum of the match, a returning player those of before too.
        foreach (var client in playing)
        {
            var expected = Enumerable.Range(1, (int)(client.World.Tick / client.Session.ChecksumInterval)).Select(i => (long)i * client.Session.ChecksumInterval);
            Assert.Equal(expected, client.Sums.Keys);
        }
        return compared;
    }

    /// <summary>
    /// Every checksum any of the clients reported (those who left until they left, those who came back from the start again;
    /// of a world rebuilt the newest one's) is the one every other reported for that tick, and nobody's world was found out of
    /// step and rebuilt (unless <paramref name="resyncs"/>); wherever their worlds stand. Returns how many ticks were compared.
    /// </summary>
    public static int AssertSumsAgree(IEnumerable<RelayClient> clients, bool resyncs = false)
    {
        var sums = new Dictionary<long, (ulong Value, string Who)>();
        foreach (var client in clients)
        {
            Assert.True(resyncs || client.Resyncs == 0, $"{client.Name}'s world was found out of step and rebuilt {client.Resyncs} times");
            Assert.True(client.Failure is null, $"{client.Name}'s thread failed: {client.Failure}");
            foreach (var (tick, value) in client.Sums)
            {
                if (!sums.TryGetValue(tick, out var seen))
                    sums[tick] = (value, client.Name);
                else
                    Assert.True(seen.Value == value, $"at tick {tick} {client.Name} reported {value:X16}, {seen.Who} {seen.Value:X16}");
            }
        }
        return sums.Count;
    }

    /// <summary>
    /// The clients' worlds recovered (P2 design §5): every checksum a world of theirs reported is the one the relay agreed for
    /// that tick, where it agreed one, except those a world spoiled by <see cref="RelayClient.Corrupt"/> reported after its
    /// spoiling, which may differ once the relay rebuilt that world (or took the client out of the match). Returns how many
    /// reports were found equal to an agreed value.
    /// </summary>
    public static int AssertRecovered(RelayServer relay, int room, IEnumerable<RelayClient> clients)
    {
        var equal = 0;
        foreach (var client in clients)
        {
            Assert.True(client.Failure is null, $"{client.Name}'s thread failed: {client.Failure}");
            foreach (var world in client.History.Select(h => h.World).Distinct().Order())
            {
                // A world was replaced when the relay rebuilt it; the newest one stands, unless the client is out of the match.
                var replaced = world < client.Rebuilds || client.Out is not null;
                var spoiled = client.Corruptions.Where(c => c.World == world).Select(c => c.Tick).DefaultIfEmpty(long.MaxValue).Min();
                foreach (var (_, tick, value) in client.History.Where(h => h.World == world))
                {
                    if (relay.Agreed(room, tick) is not { } agreed)
                        continue;
                    if (value == agreed)
                        equal++;
                    else
                        Assert.True(replaced && tick > spoiled,
                            $"{client.Name}'s world {world} reported {value:X16} at tick {tick}, the agreed {agreed:X16}"
                            + (tick > spoiled ? $"; spoiled at {spoiled} and never rebuilt" : "; not spoiled"));
                }
            }
        }
        return equal;
    }

    /// <summary>
    /// A client's recording of the match (as the game saves it at the end: the match, the turns carried out, the comings and
    /// goings) written to a file, read back and played in turn mode: it passes every checksum the client reported and ends in
    /// its world. Returns the world played back.
    /// </summary>
    public static GameWorld AssertPlaysBack(RelayClient client)
    {
        var path = Path.Combine(Path.GetTempPath(), $"relay-{Guid.NewGuid():N}{Replay.Extension}");
        new Replay(client.Match, client.Session.Played, client.World.Tick, client.Session.Presence).Save(path);
        Replay loaded;
        try
        {
            loaded = Replay.Load(path);
        }
        finally
        {
            File.Delete(path);
        }
        Assert.Equal(NetJson.Protocol, loaded.Format);
        Assert.Equal(client.Match.Players, loaded.Match.Players);
        Assert.Equal(client.Session.Presence, loaded.Presence);
        var playback = loaded.Playback(Replay.BuildWorld(Data, loaded.Match));
        Assert.Equal(SessionMode.Turns, playback.Mode);
        var compared = 0;
        while (playback.World.Tick < loaded.Ticks)
        {
            Assert.True(playback.TryStep(), $"the recording stuck at tick {playback.World.Tick}");
            if (client.Sums.TryGetValue(playback.World.Tick, out var sum))
            {
                Assert.True(sum == playback.World.Checksum(), $"{client.Name}'s recording differs at tick {playback.World.Tick}");
                compared++;
            }
        }
        Assert.Equal(client.Sums.Count(s => s.Key <= loaded.Ticks), compared);
        Assert.Equal(client.World.Checksum(), playback.World.Checksum());
        return playback.World;
    }

    /// <summary>
    /// Whether a player is away in a world standing at <paramref name="tick"/> by a client's log of comings and goings: the
    /// newest leave or return from <paramref name="tick"/> − 1 or before decides (a change from a tick on runs in that tick's step).
    /// </summary>
    public static bool AwayAt(IEnumerable<NetMessage> presence, PlayerId player, long tick)
    {
        var away = false;
        foreach (var change in presence)
        {
            if (change is PlayerLeft left && left.Player == player && left.FromTick < tick)
                away = true;
            else if (change is PlayerReturned back && back.Player == player && back.FromTick < tick)
                away = false;
        }
        return away;
    }

    public async ValueTask DisposeAsync()
    {
        Halt(_clients);
        foreach (var client in _clients)
            await client.DisposeAsync();
        await Web.DisposeAsync();
        await Relay.DisposeAsync();
    }
}

/// <summary>What a client's thread saw of its pace: when (ms since it started), its world's tick, the newest bundle in hand, and the relay's clock tick.</summary>
internal readonly record struct PaceSample(double Ms, long Tick, long Head, long? Clock);

/// <summary>
/// One client as the game runs it: its connection, and once the match began its world, its lockstep session on the relay's
/// bundles and who it is there; besides, what a test reads off it (every checksum it reported, every order of its refused,
/// the comings and goings it heard, its seat key, whether the relay took it out) and the script of what its person does.
/// </summary>
internal sealed class RelayClient(Connection connection, string name, bool web) : IAsyncDisposable
{
    private Thread? _pacer;
    private volatile bool _pacing;
    private volatile bool _frozen;
    private long _tick;

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
        var session = new LockstepSession(Replay.BuildWorld(RelayClients.Data, match), Me, match.InputDelay, mode: SessionMode.Bundles);
        session.Outgoing += Send;
        session.Gap += (expected, got) => Gaps.Enqueue((expected, got));
        Session = session;
        Interlocked.Exchange(ref _tick, 0);
        Out = null;
        if (!HoldLoad)
            Load();
    }

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
        }
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
                Key = resumed.Key ?? Key;
                Begin(resumed.Match, resumed.Player);
                if (Rescript is { } script)
                    Script = script(this);
                break;
            case LeftMatch left:
                Out = left;
                break;
            case MatchEnded ended:
                Ended = ended;
                break;
            case Refused refused:
                Refusals.Enqueue(refused.Reason);
                break;
            case Bundle bundle when InMatch:
                foreach (var change in bundle.Presence ?? [])
                    Heard.Enqueue(change);
                Session.Receive(bundle);
                break;
            default:
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
        AfterStep?.Invoke(World);
    }

    /// <summary>Sends the turn for the tick ahead once the world has come to it, with what the script orders now.</summary>
    private void Act()
    {
        if (Session.NextLocalTurn > World.Tick + Session.InputDelay)
            return;
        if (Script is { } script)
        {
            foreach (var command in script.Next(World))
                Session.Queue(command);
        }
        Session.Pump();
    }

    /// <summary>
    /// The client runs on a thread of its own as the game paces its frames (P2 design §4.3), a frame every 16 ms: what arrived
    /// in; then with a backlog of <see cref="BundlePacer.CatchUpLead"/> bundles or more it catches up for the frame's budget and
    /// sends one turn, else every 50 ms slot owed (at most a quarter second's) sends a turn and runs the ticks
    /// <see cref="BundlePacer.TicksForSlot"/> gives. Every 100 ms it notes where it stands against <paramref name="clock"/>.
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
                    var budget = BundlePacer.CatchUpBudget(Session.Ready(BundlePacer.LongCatchUp));
                    if (budget > TimeSpan.Zero)
                    {
                        var until = now + budget;
                        while (InMatch && watch.Elapsed < until && Session.TryStep())
                            Stepped();
                        if (InMatch)
                            Act();
                        owed = TimeSpan.Zero;
                    }
                    else
                    {
                        owed += delta;
                        while (InMatch && owed >= slot)
                        {
                            owed -= slot;
                            Act();
                            for (var ticks = BundlePacer.TicksForSlot(Session.Ready(BundlePacer.CatchUpLead)); ticks > 0 && Session.TryStep(); ticks--)
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
        }
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
/// One of several commanders of a skirmish race: about once a second, on a tick of their own, they order machines they may
/// order (their own, and those they keep for an away teammate) about, send fighters to attack, queue a machine at one of
/// the race's factories, place a building with their idle constructors, form a squadron of their own or order it, and, with
/// <paramref name="gives"/>, now and then give a machine of theirs to a teammate who is there.
/// </summary>
internal sealed class CommanderScript(PlayerId player, int seed, bool gives = true) : RelayScript(player, seed)
{
    public override IReadOnlyList<Command> Next(GameWorld world)
    {
        if (world.Tick % GameWorld.TicksPerSecond != 1 + Player.Value % 6 * 3)
            return [];
        var race = world.Players.RaceOf(Player);
        var mine = world.Machines.Where(m => m.Race == race && !m.IsDead && m.Inside is null && world.MayOrder(Player, m)).OrderBy(m => m.Id).ToList();
        var movers = mine.Where(m => m.Spec.Type is not ("CONSTRUCTOR" or "TECHNICIAN")).ToList();
        var fighters = mine.Where(m => m.CanFight).ToList();
        var buildings = world.Constructions.Where(c => c.Race == race && !c.IsDead).OrderBy(c => c.Id).ToList();
        var enemies = world.Actors.Where(a => !a.IsDead && a.Race != race && world.AreEnemies(race, a.Race)).OrderBy(a => a.Id).ToList();
        switch (Random.Next(10))
        {
            case 0 or 1 when movers.Count > 0:
            {
                var machine = Pick(movers);
                return [new MoveCommand(race, [machine.Id], Near(world, machine.Position, 200))];
            }
            case 2 when fighters.Count > 0:
                return [new AttackMoveCommand(race, Some(fighters, 4).Select(m => m.Id).ToList(),
                    enemies.Count > 0 ? Near(world, Pick(enemies).Position, 30) : Near(world, Pick(fighters).Position, 300))];
            case 3 when fighters.Count > 0 && enemies.Count > 0:
                return [new AttackCommand(race, Some(fighters, 3).Select(m => m.Id).ToList(), Pick(enemies).Id)];
            case 4:
                return Build(world, race, buildings);
            case 5:
                return Place(world, race, mine, buildings);
            case 6 when fighters.Count > 0:
                return [new FormSquadronCommand(race, Some(fighters, 4).Select(m => m.Id).ToList(), 1 + Random.Next(2))];
            case 7 when fighters.Count > 0:
                return [new SquadronCommand(race, 1 + Random.Next(2), new AttackMoveCommand(race, [], Near(world, Pick(fighters).Position, 300)))];
            case 8 when gives && Random.Next(3) == 0 && movers.Where(m => m.Owner == Player).ToList() is { Count: > 0 } own
                        && world.Players.PlayersOf(race).Where(p => p != Player && world.Players[p].Kind == PlayerKind.Human && !world.Players.Away(p)).ToList() is { Count: > 0 } mates:
                return [new GiveCommand(race, [Pick(own).Id], Pick(mates))];
            default:
                return [];
        }
    }

    /// <summary>A machine one of the race's factories offers and the race has researched.</summary>
    private List<Command> Build(GameWorld world, Race race, List<Construction> buildings)
    {
        var factories = buildings.Where(c => c.Complete && c.IsType("FACTORY") && c.ProductionQueue.Count < 3).ToList();
        if (factories.Count == 0)
            return [];
        var factory = Pick(factories);
        var offer = world.Data.FactoryOffer(factory.Spec.SubType!, factory.Stats.HardwareLevel).Where(world.Races[race].Researched.Contains).ToList();
        return offer.Count > 0 ? [new BuildMachineCommand(race, factory.Id, Pick(offer))] : [];
    }

    /// <summary>A building the race can raise, other than a mine, placed near its pod by the commander's idle constructors.</summary>
    private List<Command> Place(GameWorld world, Race race, List<Machine> mine, List<Construction> buildings)
    {
        var constructors = mine.Where(m => m.Spec.Type == "CONSTRUCTOR" && m.Order is IdleOrder).ToList();
        var entries = world.Buildable(race).Where(e => e.Type != "MINE").ToList();
        if (constructors.Count == 0 || entries.Count == 0)
            return [];
        var entry = Pick(entries);
        var anchor = buildings.FirstOrDefault(c => c.IsType("POD"))?.Position ?? constructors[0].Position;
        for (var tries = 0; tries < 8; tries++)
        {
            var at = Near(world, anchor, 250);
            var orientation = 90 * Random.Next(4);
            if (world.WhyCannotPlace(race, entry.Type, entry.SubType, entry.HardwareLevel, at, orientation) is null)
                return [new PlaceConstructionCommand(race, constructors.Select(m => m.Id).ToList(), entry.Type, entry.SubType, entry.HardwareLevel, at, orientation)];
        }
        return [];
    }
}

/// <summary>
/// A battle hero's person: for twenty seconds they drive their own hero in first person, mostly forward and turning towards
/// the nearest enemy machine, aiming at it and firing; then they let go and for twenty seconds send it at that enemy, onto
/// the other team's pod or on an attack move.
/// </summary>
internal sealed class HeroScript(PlayerId player, int seed) : RelayScript(player, seed)
{
    private bool _driving;

    public override IReadOnlyList<Command> Next(GameWorld world)
    {
        var race = world.Players.RaceOf(Player);
        if (world.HeroOf(Player) is not { } hero)
        {
            _driving = false;
            return [];
        }
        var enemy = world.Machines.Where(m => !m.IsDead && world.AreEnemies(race, m.Race))
            .OrderBy(m => Vector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).FirstOrDefault();
        var pod = world.Constructions.Where(c => c.IsType("POD") && world.AreEnemies(race, c.Race)).OrderBy(c => c.Id).FirstOrDefault();
        if (world.Tick / 400 % 2 == 0)
        {
            if (Random.Next(4) != 0)
                return [];
            _driving = true;
            var goal = enemy?.Position ?? pod?.Position ?? Near(world, hero.Position, 200);
            var way = goal - hero.Position;
            var off = MathF.IEEERemainder(DetMath.Atan2(way.Y, way.X) - hero.Facing, 2 * MathF.PI);
            var turn = Random.Next(5) == 0 ? Random.Next(3) - 1 : off > 0.15f ? 1 : off < -0.15f ? -1 : 0;
            return [new DriveCommand(race, hero.Id, true, Random.Next(5) == 0 ? -1 : 1, turn, Near(world, goal, 10), Weapons: Random.Next(4),
                Buttons: Random.Next(2) == 0, Yaw: Random.Next(2) == 0 ? (float)(Random.NextDouble() * 8 - 4) : null)];
        }
        if (_driving)
        {
            _driving = false;
            return [new DriveCommand(race, hero.Id, false)];
        }
        // An order now and then, each person on a tick of their own: a hero's way across the map is long to find.
        if ((world.Tick + 13 * Player.Value) % 160 != 0)
            return [];
        return Random.Next(3) switch
        {
            0 when enemy is not null => [new AttackCommand(race, [hero.Id], enemy.Id)],
            1 when pod is not null => [new MoveCommand(race, [hero.Id], Near(world, pod.Position, 20))],
            _ => [new AttackMoveCommand(race, [hero.Id], enemy is not null ? Near(world, enemy.Position, 20) : Near(world, hero.Position, 200))],
        };
    }
}
