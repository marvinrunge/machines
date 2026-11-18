using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// The relay on the internet: WebSockets beside TCP into the same rooms, /health, and connections that outlive their
/// sockets, whether a line drops, the host ends a socket (Cloud Run after 60 minutes) or the client moves on its own.
/// </summary>
public class OnlineRelayTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    // Short clocks, so that a test sees a whole minute's worth of the connection's life in a second or two.
    private static readonly LinkTimings Quick = new()
    {
        Ack = TimeSpan.FromMilliseconds(100),
        Heartbeat = TimeSpan.FromMilliseconds(500),
        Silence = TimeSpan.FromSeconds(5),
        Grace = TimeSpan.FromSeconds(10),
        Dial = TimeSpan.FromSeconds(2),
        Tick = TimeSpan.FromMilliseconds(25),
    };

    [Fact]
    public void An_address_picks_its_link_by_what_was_typed()
    {
        Assert.True(RelayAddress.IsWebSocket("wss://machines-relay-abc-ey.a.run.app"));
        Assert.True(RelayAddress.IsWebSocket(" HTTPS://relay.example.com "));
        Assert.True(RelayAddress.IsWebSocket("ws://127.0.0.1:8080/ws"));
        Assert.False(RelayAddress.IsWebSocket("192.168.1.20:7777"));
        Assert.False(RelayAddress.IsWebSocket("[2001:db8::1]:7777"));

        Assert.Equal("wss://machines-relay-abc-ey.a.run.app/ws", RelayAddress.WebSocketUri("https://machines-relay-abc-ey.a.run.app").ToString());
        Assert.Equal("wss://relay.example.com/ws", RelayAddress.WebSocketUri("wss://relay.example.com/").ToString());
        Assert.Equal("ws://127.0.0.1:8080/ws", RelayAddress.WebSocketUri("http://127.0.0.1:8080").ToString());
        Assert.Equal("ws://localhost:9000/other", RelayAddress.WebSocketUri("ws://localhost:9000/other").ToString());

        Assert.Equal(("192.168.1.20", 7777), RelayAddress.Split("192.168.1.20"));
        Assert.Equal(("example.org", 7000), RelayAddress.Split(" example.org:7000 "));
        Assert.Equal(("2001:db8::1", 7777), RelayAddress.Split("2001:db8::1"));
        Assert.Equal(("2001:db8::1", 7001), RelayAddress.Split("[2001:db8::1]:7001"));
    }

    [Fact]
    public void A_hello_and_welcome_without_the_resume_fields_still_read_as_before()
    {
        // A client from before asks for nothing, and a relay from before gives no session: both carry on as they did,
        // so the protocol number stays.
        var hello = (Hello)NetJson.Decode("""{"$type":"hello","Name":"Anna","Protocol":12}"""u8);
        Assert.False(hello.Resume);
        var welcome = (Welcome)NetJson.Decode("""{"$type":"welcome","ClientId":3}"""u8);
        Assert.Null(welcome.Session);
        Assert.Null(welcome.Relay);
        // And what a client from before does not know, it passes over.
        var fields = System.Text.Json.JsonDocument.Parse(NetJson.Encode(new Welcome(3, "ABC", RelayServer.Version))).RootElement;
        Assert.Equal(3, fields.GetProperty("ClientId").GetInt32());
    }

    [Fact]
    public async Task A_client_talks_to_the_relay_over_a_WebSocket_and_health_counts_it()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Client.Connect($"ws://127.0.0.1:{http.Port}", "Anna");

        Assert.NotNull(anna.Welcome.Session);
        Assert.Equal(RelayServer.Version, anna.Welcome.Relay);
        anna.Connection.Send(new CreateRoom("Online", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false));
        await anna.Next(m => m is RoomState { Players.Count: 1 });
        anna.Connection.Send(new ListRooms());
        var list = (RoomList)await anna.Next(m => m is RoomList);
        Assert.Equal("Online", Assert.Single(list.Rooms).Name);

        using var web = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{http.Port}") };
        var health = await web.GetFromJsonAsync<RelayHealth>("/health");
        Assert.NotNull(health);
        Assert.Equal("ok", health.Status);
        Assert.Equal(RelayServer.Version, health.Version);
        Assert.Equal(NetJson.Protocol, health.Protocol);
        Assert.Equal(1, health.Rooms);
        Assert.Equal(1, health.Players);
        // A plain request to the WebSocket's place is turned away.
        Assert.Equal(HttpStatusCode.BadRequest, (await web.GetAsync(RelayAddress.WebSocketPath)).StatusCode);
    }

    [Fact]
    public async Task A_room_opened_over_WebSocket_plays_with_a_TCP_client()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, beat: BeatTimings.Scripted);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true);
        await using var ben = await Player.Join($"127.0.0.1:{relay.Port}", "Ben", host: false);
        await Player.Start(anna, ben);

        await Player.Play(300, new Random(3), anna, ben);
        await Player.CatchUp(anna, ben);

        Player.AssertSame(anna, ben, minimumTick: 150);
    }

    [Fact]
    public async Task A_cut_line_comes_back_without_a_message_lost_or_doubled()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, Quick, BeatTimings.Scripted);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true, Quick);
        await using var ben = await Player.Join($"127.0.0.1:{relay.Port}", "Ben", host: false, Quick);
        await Player.Start(anna, ben);
        var rng = new Random(7);

        await Player.Play(80, rng, anna, ben);
        // Anna's line breaks under her, mid-match.
        anna.Connection.Cut();
        await Player.Play(80, rng, anna, ben);
        // The relay's host ends every socket, as Cloud Run does after its request timeout.
        relay.CutLines();
        await Player.Play(80, rng, anna, ben);
        // And Ben's breaks while Anna's, on its new socket, carries on.
        ben.Connection.Cut();
        await Player.Play(120, rng, anna, ben);
        await Player.CatchUp(anna, ben);

        Assert.True(anna.Connection.Links >= 3, $"Anna used {anna.Connection.Links} sockets");
        Assert.True(ben.Connection.Links >= 3, $"Ben used {ben.Connection.Links} sockets");
        Player.AssertSame(anna, ben, minimumTick: 150);
        // Nobody left: the match never noticed.
        Assert.Empty(anna.Session.Presence);
        Assert.Empty(ben.Session.Presence);
        Assert.Equal(Controller.Player, anna.Session.World.Races[ben.Race].Controller);
    }

    [Fact]
    public async Task Clients_move_to_a_new_socket_on_their_own_and_the_match_goes_on()
    {
        var moving = Quick with { Rotation = TimeSpan.FromMilliseconds(300), RotationSpread = TimeSpan.FromMilliseconds(100) };
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, moving, BeatTimings.Scripted);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true, moving);
        await using var ben = await Player.Join($"127.0.0.1:{relay.Port}", "Ben", host: false, moving);
        await Player.Start(anna, ben);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var rng = new Random(11);
        // Two and a half seconds of sockets moving, and the ticks and moves the test asks for even on a computer busy with
        // other tests (where a dial can take its whole two seconds and the move is tried again later).
        while ((watch.Elapsed < TimeSpan.FromSeconds(2.5) || anna.Session.World.Tick < 30 || ben.Session.World.Tick < 30
                || anna.Connection.Links < 4 || ben.Connection.Links < 4)
               && watch.Elapsed < TimeSpan.FromSeconds(30))
            await Player.Play(20, rng, anna, ben);
        await Player.CatchUp(anna, ben);

        Assert.True(anna.Connection.Links >= 4, $"Anna used {anna.Connection.Links} sockets");
        Assert.True(ben.Connection.Links >= 4, $"Ben used {ben.Connection.Links} sockets");
        Player.AssertSame(anna, ben, minimumTick: 30);
        Assert.Empty(anna.Session.Presence);
    }

    [Fact]
    public async Task A_line_that_stays_down_gives_the_race_to_the_AI_after_the_grace_period()
    {
        var brief = Quick with { Grace = TimeSpan.FromSeconds(1.5), Dial = TimeSpan.FromMilliseconds(300) };
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, brief, BeatTimings.Scripted);
        await using var line = new LineProxy(relay.Port);
        await using var anna = await Player.Join($"127.0.0.1:{relay.Port}", "Anna", host: true, brief);
        await using var ben = await Player.Join($"127.0.0.1:{line.Port}", "Ben", host: false, brief);
        await Player.Start(anna, ben);
        var rng = new Random(5);
        await Player.Play(60, rng, anna, ben);

        // Ben's line goes down and stays down.
        line.Down();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!ben.Connection.Closed && watch.Elapsed < TimeSpan.FromSeconds(10))
            await Player.Play(5, rng, anna, ben);
        Assert.True(ben.Connection.Closed, "Ben's connection never gave up");
        Assert.True(watch.Elapsed >= TimeSpan.FromSeconds(1), $"Ben gave up after only {watch.Elapsed.TotalSeconds:0.0} s");

        // The relay gave up on him too: Anna plays on, his race the AI's.
        var before = anna.Session.World.Tick;
        for (var frame = 0; frame < 400 && anna.Session.World.Tick < before + 40; frame++)
            await Player.Frame(anna);
        Assert.True(anna.Session.World.Tick >= before + 40, $"stuck at {anna.Session.World.Tick}");
        Assert.Contains(anna.Session.Presence, m => m is PlayerLeft left && left.Player == PlayerId.Of(ben.Race));
        Assert.Equal(Controller.Ai, anna.Session.World.Races[ben.Race].Controller);
    }

    [Fact]
    public async Task A_player_who_closes_on_purpose_is_not_waited_for()
    {
        // The default grace of a minute: closing says goodbye, so nobody waits it out.
        await using var relay = RelayServer.Start(0, IPAddress.Loopback, beat: BeatTimings.Scripted);
        await using var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Player.Join($"ws://127.0.0.1:{http.Port}", "Anna", host: true);
        var ben = await Player.Join($"ws://127.0.0.1:{http.Port}", "Ben", host: false);
        await Player.Start(anna, ben);
        await Player.Play(30, new Random(2), anna, ben);

        await ben.DisposeAsync();
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!anna.Session.Presence.Any(m => m is PlayerLeft) && watch.Elapsed < TimeSpan.FromSeconds(10))
            await Player.Frame(anna);

        Assert.Contains(anna.Session.Presence, m => m is PlayerLeft left && left.Player == PlayerId.Of(ben.Race));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Anna waited {watch.Elapsed.TotalSeconds:0.0} s");
    }

    [Fact]
    public async Task The_relay_answers_while_the_thread_that_started_it_is_busy()
    {
        // A game hosting the relay starts it on its main thread, the tests on the test runner's few threads; neither may
        // hold it up: a frame of loading, or every runner thread at a long simulation, kept new players from their welcome.
        var busy = new HeldContext();
        var before = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(busy);
        RelayServer relay;
        try
        {
            relay = RelayServer.Start(0, IPAddress.Loopback);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(before);
        }
        try
        {
            var anna = Task.Run(() => ChatTests.Client.Connect(relay, "Anna"));
            var answered = await Task.WhenAny(anna, Task.Delay(TimeSpan.FromSeconds(8))) == anna;

            Assert.True(answered && anna.IsCompletedSuccessfully, $"no welcome while the starting thread was busy ({busy.Held} callbacks waited for it)");
            await (await anna).DisposeAsync();
        }
        finally
        {
            busy.Release();
            await relay.DisposeAsync();
        }
    }

    /// <summary>A thread that is busy: what is posted to it waits until <see cref="Release"/>, then runs on the pool.</summary>
    private sealed class HeldContext : SynchronizationContext
    {
        private readonly List<(SendOrPostCallback, object?)> _held = [];
        private bool _released;

        public int Held
        {
            get { lock (_held) return _held.Count; }
        }

        public override void Post(SendOrPostCallback d, object? state)
        {
            lock (_held)
                if (!_released)
                {
                    _held.Add((d, state));
                    return;
                }
            ThreadPool.QueueUserWorkItem(_ => d(state));
        }

        public void Release()
        {
            lock (_held)
            {
                _released = true;
                foreach (var (d, state) in _held)
                    ThreadPool.QueueUserWorkItem(_ => d(state));
                _held.Clear();
            }
        }
    }

    [Fact]
    public async Task A_relay_that_stops_says_goodbye_and_nobody_waits_to_come_back()
    {
        var relay = RelayServer.Start(0, IPAddress.Loopback);
        var http = await RelayHttp.StartAsync(relay, 0, IPAddress.Loopback);
        await using var anna = await Client.Connect($"ws://127.0.0.1:{http.Port}", "Anna");
        await using var ben = await Client.Connect($"127.0.0.1:{relay.Port}", "Ben");

        // As the relay's program stops on SIGTERM: the relay with its players, then the web side.
        var watch = System.Diagnostics.Stopwatch.StartNew();
        await relay.DisposeAsync();
        await http.DisposeAsync();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(3), $"stopping took {watch.Elapsed.TotalSeconds:0.0} s");

        // Both were told goodbye: their connections end rather than trying for a minute to come back.
        foreach (var (client, name) in new[] { (anna, "Anna"), (ben, "Ben") })
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            try
            {
                while (await client.Connection.Inbox.WaitToReadAsync(timeout.Token))
                    client.Connection.Inbox.TryRead(out _);
            }
            catch (OperationCanceledException)
            {
                Assert.Fail($"{name} still open after {watch.Elapsed.TotalSeconds:0.0} s, reconnecting {client.Connection.Reconnecting}, {client.Connection.Links} sockets");
            }
            Assert.True(client.Connection.Closed);
        }
    }

    /// <summary>A connected client before any room.</summary>
    private sealed class Client : IAsyncDisposable
    {
        public required Connection Connection { get; init; }
        public required Welcome Welcome { get; init; }

        public static async Task<Client> Connect(string address, string name, LinkTimings? timings = null)
        {
            var connection = await Connection.ConnectAsync(address, CancellationToken.None, timings);
            connection.Send(new Hello(name, NetJson.Protocol, Resume: true));
            return new Client { Connection = connection, Welcome = (Welcome)await Next(connection, m => m is Welcome) };
        }

        public Task<NetMessage> Next(Func<NetMessage, bool> wanted) => Next(Connection, wanted);

        private static async Task<NetMessage> Next(Connection connection, Func<NetMessage, bool> wanted)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (true)
            {
                var message = await connection.Inbox.ReadAsync(timeout.Token);
                if (message is Refused refused)
                    throw new InvalidOperationException(refused.Reason);
                if (wanted(message))
                    return message;
            }
        }

        public ValueTask DisposeAsync() => Connection.DisposeAsync();
    }

    /// <summary>A client as the game runs it, which also notes every bundle the relay hands it and every turn in them.</summary>
    private sealed class Player : IAsyncDisposable
    {
        public required Client Client { get; init; }
        public Connection Connection => Client.Connection;
        public int Id => Client.Welcome.ClientId;
        public MatchInfo Match { get; private set; } = null!;
        public LockstepSession Session { get; private set; } = null!;
        public Race Race { get; private set; }

        /// <summary>The tick of every bundle received, in the order it came.</summary>
        public List<long> Bundles { get; } = [];

        /// <summary>Every turn received in a bundle, by its tick and race, in the order it came.</summary>
        public List<(long Tick, Race Race)> Turns { get; } = [];

        /// <summary>Every gap the session met in the relay's numbering.</summary>
        public List<(long Expected, long Got)> Gaps { get; } = [];

        /// <summary>Every rebuild of the world the relay sent (a resync), in order.</summary>
        public List<MatchResumed> Resumed { get; } = [];

        /// <summary>The tick of every turn with commands the player sent, in order.</summary>
        public List<long> Ordered { get; } = [];

        public static async Task<Player> Join(string address, string name, bool host, LinkTimings? timings = null)
        {
            var client = await Client.Connect(address, name, timings);
            if (host)
                client.Connection.Send(new CreateRoom("test", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false));
            else
            {
                client.Connection.Send(new ListRooms());
                var rooms = (RoomList)await client.Next(m => m is RoomList { Rooms.Count: > 0 });
                client.Connection.Send(new JoinRoom(rooms.Rooms.Single().Id));
            }
            await client.Next(m => m is RoomState state && state.Players.Any(p => p.ClientId == client.Welcome.ClientId));
            return new Player { Client = client };
        }

        public static async Task Start(Player host, Player guest)
        {
            await host.Client.Next(m => m is RoomState { Players.Count: 2 });
            guest.Connection.Send(new SetPlayer(Race.Blue, Ready: true));
            await host.Client.Next(m => m is RoomState state && state.Players.Any(p => p.ClientId == guest.Id && p.Ready));
            host.Connection.Send(new SetPlayer(Race.Red, Ready: true));
            await host.Client.Next(m => m is RoomState state && state.Players.All(p => p.Ready));
            host.Connection.Send(new StartMatch());
            foreach (var player in new[] { host, guest })
            {
                var started = (MatchStarted)await player.Client.Next(m => m is MatchStarted);
                player.Match = started.Match;
                var table = started.Match.Table();
                var me = table.Rows[started.Match.Players.ToList().FindIndex(p => p.ClientId == player.Id && !p.Ai)].Id;
                player.Race = table.RaceOf(me);
                player.Session = new LockstepSession(Replay.BuildWorld(DataLoader.Value, started.Match), me, started.Match.InputDelay, mode: SessionMode.Bundles);
                player.Session.Outgoing += player.Connection.Send;
                player.Session.Outgoing += message =>
                {
                    if (message is Turn { Commands.Count: > 0 } ordered)
                        player.Ordered.Add(ordered.Tick);
                };
                player.Session.Gap += (expected, got) => player.Gaps.Add((expected, got));
                player.Connection.Send(new MatchLoaded());
            }
        }

        private void Read()
        {
            while (Connection.Inbox.TryRead(out var message))
            {
                if (message is Bundle bundle)
                {
                    Bundles.Add(bundle.Tick);
                    Turns.AddRange(bundle.Turns.Select(turn => (bundle.Tick, Session.World.Players.RaceOf(turn.Player))));
                }
                if (message is MatchResumed resumed)
                    Resumed.Add(resumed);
                Session.Receive(message);
            }
        }

        /// <summary>One frame of each client: send a turn, read what arrived, step what can be stepped, a turn after each tick.</summary>
        public static async Task Frame(params Player[] players)
        {
            foreach (var player in players)
            {
                player.Session.Pump();
                player.Read();
                for (var steps = 0; steps < 40 && player.Session.TryStep(); steps++)
                    player.Session.Pump();
            }
            await Task.Delay(1);
        }

        /// <summary>Frames in which each player now and then orders one of its machines somewhere.</summary>
        public static async Task Play(int frames, Random rng, params Player[] players)
        {
            for (var frame = 0; frame < frames; frame++)
            {
                foreach (var player in players)
                {
                    if (rng.Next(20) == 0 && player.Session.World.Machines.Where(m => m.Race == player.Race).ToList() is { Count: > 0 } own)
                        player.Session.Queue(new MoveCommand(player.Race, [own[rng.Next(own.Count)].Id],
                            new Vector2(200 + (float)rng.NextDouble() * 1200, 200 + (float)rng.NextDouble() * 1200)));
                }
                await Frame(players);
            }
        }

        /// <summary>Lets both worlds reach the same tick.</summary>
        public static async Task CatchUp(Player a, Player b)
        {
            for (var i = 0; i < 1000 && a.Session.World.Tick != b.Session.World.Tick; i++)
            {
                foreach (var player in new[] { a, b })
                {
                    player.Read();
                    var other = player == a ? b : a;
                    if (player.Session.World.Tick < other.Session.World.Tick)
                        player.Session.TryStep();
                }
                await Task.Delay(1);
            }
        }

        /// <summary>
        /// Both worlds are alike, and each client was handed every bundle exactly once, from the first on without a gap, and in
        /// them each player's every turn with commands, once, on the tick it asked for (a scripted relay moves none): nothing
        /// was lost on the way, nothing came twice. Only turns with commands go into bundles, so a player who happened to order
        /// nothing has none there.
        /// </summary>
        public static void AssertSame(Player a, Player b, long minimumTick)
        {
            Assert.True(a.Session.World.Tick >= minimumTick, $"only {a.Session.World.Tick} ticks ran");
            Assert.Equal(a.Session.World.Tick, b.Session.World.Tick);
            Assert.Equal(a.Session.World.Checksum(), b.Session.World.Checksum());
            Assert.Empty(a.Resumed);
            Assert.Empty(b.Resumed);
            foreach (var player in new[] { a, b })
            {
                Assert.Empty(player.Gaps);
                Assert.True(player.Bundles.Count >= minimumTick);
                Assert.Equal(Enumerable.Range(0, player.Bundles.Count).Select(t => (long)t), player.Bundles);
                foreach (var sender in new[] { a, b })
                {
                    var ticks = player.Turns.Where(t => t.Race == sender.Race).Select(t => t.Tick).ToList();
                    Assert.Equal(sender.Ordered.Where(t => t < player.Bundles.Count), ticks);
                }
            }
        }

        public ValueTask DisposeAsync() => Client.DisposeAsync();
    }
}
