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
/// the game runs one (P1 slice S4b): they connect, open or join a room, take seats, say who they are and that they are
/// ready, begin the match the relay starts, and from then on play it frame by frame, a world and a lockstep session each,
/// until the test has them leave or come back.
/// </summary>
internal sealed class RelayClients : IAsyncDisposable
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    /// <summary>How long a match may take to reach a tick before the test gives up and says who was waiting for whom.</summary>
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(90);

    private readonly List<RelayClient> _clients = [];

    private RelayClients(RelayServer relay, RelayHttp web) => (Relay, Web) = (relay, web);

    public static GameData Data => DataLoader.Value;

    public RelayServer Relay { get; }
    public RelayHttp Web { get; }

    /// <summary>Every client connected so far, whether it still plays or not.</summary>
    public IReadOnlyList<RelayClient> Clients => _clients;

    /// <summary>The relay's TCP listener and its web side, on free ports of this computer.</summary>
    public static async Task<RelayClients> Start()
    {
        var relay = RelayServer.Start(0, IPAddress.Loopback);
        return new RelayClients(relay, await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback));
    }

    /// <summary>
    /// A client says hello under <paramref name="name"/>, asking to keep its seat over a dropped line as the game does, over
    /// a WebSocket (<paramref name="web"/>, the public relay's way) or over TCP (the LAN's).
    /// </summary>
    public async Task<RelayClient> Connect(string name, bool web)
    {
        var address = web ? $"ws://127.0.0.1:{Web.Port}" : $"127.0.0.1:{Relay.Port}";
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
    /// Everyone in the room says, one after the other, that they are ready, in the colour the room shows them and in a battle
    /// with the hero <paramref name="hero"/> gives them; the host starts and each client begins the match the relay sent
    /// (with <paramref name="play"/> false only takes it, for a test to play it elsewhere).
    /// </summary>
    public static async Task<MatchInfo> Begin(RelayClient host, IReadOnlyList<RelayClient> everyone, Func<RelayClient, BattleHero?>? hero = null, bool play = true)
    {
        // The room as it stands: the relay shows it again to a member who asks for the list of rooms.
        host.Connection.Send(new ListRooms());
        var state = await host.Next<RoomState>(s => s.Players.Count == everyone.Count);
        foreach (var client in everyone)
        {
            client.Connection.Send(new SetPlayer(state.Players.Single(p => p.ClientId == client.Id).Colour, Ready: true, Hero: hero?.Invoke(client)));
            state = await host.Next<RoomState>(s => s.Players.Single(p => p.ClientId == client.Id).Ready);
        }
        host.Connection.Send(new StartMatch());
        MatchInfo? match = null;
        foreach (var client in everyone)
        {
            match = (await client.Next<MatchStarted>()).Match;
            if (play)
                client.Begin(match);
        }
        return match!;
    }

    /// <summary>
    /// A client who dropped out of a running match connects again under <paramref name="name"/> and asks for their seat back
    /// (<see cref="RejoinMatch"/>, naming <paramref name="player"/> when given, as the game does when it still knows it), then
    /// rebuilds the world from everything the relay forwarded and catches up as it plays.
    /// </summary>
    public async Task<RelayClient> Rejoin(string name, bool web, int room, PlayerId? player = null)
    {
        var client = await Connect(name, web);
        client.Connection.Send(new RejoinMatch(room, player));
        var resumed = await client.Next<MatchResumed>();
        client.Begin(resumed.Match, resumed.Player, resumed.FromTick);
        return client;
    }

    /// <summary>
    /// Plays every client still in the match until each world stands at <paramref name="tick"/>: frames of all of them in turn,
    /// waiting for the relay only when no world could step.
    /// </summary>
    public static async Task PlayTo(long tick, IEnumerable<RelayClient> clients)
    {
        var playing = clients.Where(c => !c.Gone).ToList();
        var watch = Stopwatch.StartNew();
        while (playing.Any(c => c.World.Tick < tick))
        {
            if (watch.Elapsed > Patience)
                Assert.Fail($"the match did not reach tick {tick} in {Patience.TotalSeconds:0} s: "
                            + string.Join("; ", playing.Select(c => $"{c.Name} (player {c.Me}) at {c.World.Tick} waits for [{string.Join(", ", c.Session.Waiting())}]")));
            var stepped = false;
            foreach (var client in playing)
                stepped |= client.Frame(tick);
            if (!stepped)
                await Arrival(playing);
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

    /// <summary>
    /// The clients' worlds agree: those still playing stand at the same tick with the same checksum, nobody heard of a desync,
    /// and every checksum any of them reported (those who left until they left, those who came back from the start again)
    /// is the one every other reported for that tick. Returns how many ticks were compared.
    /// </summary>
    public static int AssertAgree(IEnumerable<RelayClient> clients)
    {
        var all = clients.ToList();
        var playing = all.Where(c => !c.Gone).ToList();
        Assert.Single(playing.Select(c => c.World.Tick).Distinct());
        Assert.Single(playing.Select(c => c.World.Checksum()).Distinct());
        foreach (var client in all)
            Assert.True(client.Session.DesyncTick is null, $"{client.Name} heard of a desync at tick {client.Session.DesyncTick}");
        var sums = new Dictionary<long, (ulong Value, string Who)>();
        foreach (var client in all)
        {
            foreach (var (tick, value) in client.Sums)
            {
                if (!sums.TryGetValue(tick, out var seen))
                    sums[tick] = (value, client.Name);
                else
                    Assert.True(seen.Value == value, $"at tick {tick} {client.Name} reported {value:X16}, {seen.Who} {seen.Value:X16}");
            }
        }
        // Everyone still playing reported every checksum of the match, a returning player those of before too.
        foreach (var client in playing)
        {
            var expected = Enumerable.Range(1, (int)(client.World.Tick / client.Session.ChecksumInterval)).Select(i => (long)i * client.Session.ChecksumInterval);
            Assert.Equal(expected, client.Sums.Keys);
        }
        return sums.Count;
    }

    /// <summary>
    /// A client's recording of the match (as the game saves it at the end: the match, the turns carried out, the comings and
    /// goings) written to a file, read back and played: it passes every checksum the client reported and ends in its world.
    /// Returns the world played back.
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

    public async ValueTask DisposeAsync()
    {
        foreach (var client in _clients)
            await client.DisposeAsync();
        await Web.DisposeAsync();
        await Relay.DisposeAsync();
    }
}

/// <summary>
/// One client as the game runs it: its connection, and once the match began its world, its lockstep session and who it is
/// there; besides, what a test reads off it (every checksum it reported, every order of its refused) and the script of what
/// its person does.
/// </summary>
internal sealed class RelayClient(Connection connection, string name, bool web) : IAsyncDisposable
{
    public Connection Connection { get; } = connection;

    /// <summary>The name the client said hello with, by which it rejoins.</summary>
    public string Name { get; } = name;

    /// <summary>Whether it talks to the relay over a WebSocket, else over TCP.</summary>
    public bool Web { get; } = web;

    public int Id { get; set; }

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

    /// <summary>The tick of the client's first turn: later than the start when it came back.</summary>
    public long FirstTurn { get; private set; }

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
    /// The match begins for this client, as the game begins it: the world built from the match's table, the session for the
    /// player the client is (the row of its client, or the one it came back as) sending from the tick it plays from.
    /// </summary>
    public void Begin(MatchInfo match, PlayerId? me = null, long? from = null)
    {
        Match = match;
        var table = match.Table();
        Me = me ?? table.Rows[match.Players.ToList().FindIndex(p => p.ClientId == Id && !p.Ai)].Id;
        Race = table.RaceOf(Me);
        FirstTurn = from ?? match.InputDelay;
        Session = new LockstepSession(Replay.BuildWorld(RelayClients.Data, match), Me, match.InputDelay, firstLocalTurn: from, echoed: true);
        Session.Outgoing += Connection.Send;
        Session.Outgoing += message =>
        {
            if (message is ChecksumReport report)
                Sums[report.Tick] = report.Value;
        };
    }

    /// <summary>
    /// One frame: the client's next turns out (its person's orders in them), what arrived in, and the ticks that can run, up
    /// to 40 as the game catches up and never past <paramref name="limit"/>; returns whether the world ran a tick.
    /// </summary>
    public bool Frame(long limit)
    {
        if (Gone)
            return false;
        Act();
        Read();
        var stepped = false;
        for (var steps = 0; steps < 40 && World.Tick < limit && Session.TryStep(); steps++)
        {
            stepped = true;
            foreach (var refusal in World.Rejected)
                Refused.Add((World.Tick, refusal));
            AfterStep?.Invoke(World);
            Act();
            Read();
        }
        return stepped;
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

    private void Read()
    {
        while (Connection.Inbox.TryRead(out var message))
            Session.Receive(message);
    }

    /// <summary>The client closes on purpose (the game quits the match): the relay tells the others at once.</summary>
    public Task Leave() => DisposeAsync().AsTask();

    public async ValueTask DisposeAsync()
    {
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
