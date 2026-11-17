using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// Out of step and back, through the real relay (P2 design §5, slice S3): the relay settles each checksum tick on a value
/// more than half of the playing persons reported, as soon as one is; a world that reported another is rebuilt from the
/// relay's log (a resync) while the others play on, its seat held in a skirmish and its hero driven by a bot in a battle, and
/// is back once on time; a straggler's report, an extra one of a returning world and a rebuilt world's catch-up are checked
/// against what was agreed, an old world's reports once its rebuild began are not; a world out of step after three resyncs
/// is taken out of the match for good; and nothing is resynced once the end is agreed.
/// </summary>
public class ResyncTests
{
    private const string TakenOut = "Your game kept going out of step with the others, so it has left the match.";

    private static PlayerId P(int value) => new(value);

    [Fact]
    public async Task Of_ten_heroes_only_the_one_whose_world_goes_its_own_way_is_rebuilt_and_recovers_while_a_bot_drives_its_hero()
    {
        await using var relay = await RelayClients.Start();
        string[] names = ["Anna", "Ben", "Cleo", "Dan", "Eve", "Finn", "Gus", "Hal", "Ivy", "Jo"];
        var clients = new List<RelayClient>();
        for (var i = 0; i < names.Length; i++)
            clients.Add(await relay.Connect(names[i], web: i % 2 == 1));
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 1, Lives = 3, TeamSize = 5 }, FogOfWar = false };
        var room = await RelayClients.Open(clients[0], new CreateRoom("Ten", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false), [.. clients.Skip(1)]);
        await RelayClients.Begin(clients[0], clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 1, line: c.Id % 3));
        foreach (var client in clients)
        {
            client.Script = new HeroScript(client.Me, 1000 + client.Me.Value);
            // A world rebuilt from the log plays on as a game started afresh would: its person's script begins anew.
            client.Rescript = rebuilt => new HeroScript(rebuilt.Me, 1100 + rebuilt.Me.Value);
        }
        var eve = clients[4];
        Assert.Equal(P(5), eve.Me);

        // Eve's hero loses a hit point in her world alone after tick 130: her report of 140 is out of step with the others'.
        eve.Corrupt(130);
        await RelayClients.PlayTo(800, clients);

        var resumed = Assert.Single(eve.Rebuilt);
        Assert.Equal((P(5), ResumeReason.Resync, 1, (string?)null), (resumed.Player, resumed.Reason, resumed.Resyncs, resumed.Key));
        Assert.All(clients.Where(c => c != eve), c => Assert.Empty(c.Rebuilt));
        Assert.NotEqual(relay.Relay.Agreed(room.Id, 140), eve.History.Single(h => h is { World: 0, Tick: 140 }).Value);
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: Eve (player 5) out of step at tick 140: ", StringComparison.Ordinal));
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: resync 1/3 of Eve (player 5) from tick {resumed.FromTick}: log ", StringComparison.Ordinal));
        Assert.Equal(1, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(5))!.Resyncs));

        // In every world a bot drove her hero from the rebuild until she was back.
        foreach (var client in clients)
        {
            var news = client.Session.Presence;
            Assert.Equal(2, news.Count);
            Assert.Equal(new PlayerLeft(P(5), resumed.FromTick), news[0]);
            Assert.True(news[1] is PlayerReturned back && back.Player == P(5) && back.FromTick > resumed.FromTick, $"{news[1]}");
            Assert.DoesNotContain(P(5), client.World.HeroBots.Keys);
        }

        Assert.True(RelayClients.AssertRecovered(relay.Relay, room.Id, clients) >= 10 * 800 / 20);
        Assert.True(RelayClients.AssertAgree(clients, resyncs: true) >= 800 / 20);
        foreach (var client in new[] { clients[0], eve })
            RelayClients.AssertPlaysBack(client);
    }

    [Fact]
    public async Task A_skirmish_commanders_world_rebuilt_holds_the_race_without_the_computer_and_is_back_with_the_lag_mark_off()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients) = await Skirmish(relay);
        var cleo = clients[2];
        var greenAi = false;
        foreach (var client in clients)
            client.AfterStep = world => greenAi |= world.Races[Race.Green].Controller == Controller.Ai || world.AiControllers.Any(ai => ai.Race == Race.Green);

        cleo.Corrupt(90);
        await RelayClients.PlayTo(500, clients);

        var resumed = Assert.Single(cleo.Rebuilt);
        Assert.Equal((P(2), ResumeReason.Resync, 1), (resumed.Player, resumed.Reason, resumed.Resyncs));
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: Cleo (player 2) out of step at tick 100: ", StringComparison.Ordinal));
        // Her race was held, its machines let go of but hers, from the rebuild until her world was on time again: no
        // computer stood in for it, and she plays it again.
        foreach (var client in clients)
        {
            Assert.Equal(new PlayerLagging(P(2), resumed.FromTick, true), client.Session.Presence[0]);
            Assert.True(client.Session.Presence is [_, PlayerLagging { Lagging: false } off] && off.Player == P(2) && off.FromTick > resumed.FromTick,
                string.Join(", ", client.Session.Presence));
            Assert.False(client.Session.Lagging(P(2)));
        }
        Assert.False(greenAi, "the computer stood in for Green");
        Assert.True(RelayClients.AssertRecovered(relay.Relay, room.Id, clients) > 0);
        Assert.True(RelayClients.AssertAgree(clients, resyncs: true) >= 500 / 20);
        RelayClients.AssertPlaysBack(cleo);
    }

    [Fact]
    public async Task A_commander_whose_world_goes_its_own_way_after_every_rebuild_is_taken_out_after_three_resyncs_and_may_not_come_back()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients) = await Skirmish(relay);
        var (anna, ben, cleo) = (clients[0], clients[1], clients[2]);
        var green = new List<(long Tick, Controller Controller)>();
        anna.AfterStep = world => green.Add((world.Tick, world.Races[Race.Green].Controller));

        // Cleo's game spoils every world it builds after tick 50: each rebuilt world's catch-up report of 60 is out of step with
        // the one agreed long ago, which starts the next resync at once.
        cleo.Corrupt(50, every: true);
        await RelayClients.PlayTo(600, clients);

        Assert.Equal([1, 2, 3], cleo.Rebuilt.Select(r => r.Resyncs));
        Assert.All(cleo.Rebuilt, r => Assert.Equal((ResumeReason.Resync, (string?)null), (r.Reason, r.Key)));
        Assert.Equal(new LeftMatch(TakenOut), cleo.Out);
        for (var i = 1; i <= 3; i++)
            Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: resync {i}/3 of Cleo (player 2) from tick ", StringComparison.Ordinal));
        // Four times out of step at 60, once for each world her game built: the first as the connections happened to deliver the
        // three reports, each rebuilt one's against the value agreed long before.
        var outOfStep = relay.Lines.Where(l => l.StartsWith($"room {room.Id}: Cleo (player 2) out of step at tick 60: ", StringComparison.Ordinal)).ToList();
        Assert.Equal(4, outOfStep.Count);
        Assert.All(outOfStep.Skip(1), l => Assert.Contains(" against the agreed ", l, StringComparison.Ordinal));
        Assert.Equal(SeatState.Barred, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(2))!.State));

        // Her race was held from the first rebuild, with no computer for it, until she was taken out; then the computer plays it.
        var held = Assert.Single(anna.Session.Presence.OfType<PlayerLagging>());
        Assert.Equal(new PlayerLagging(P(2), cleo.Rebuilt[0].FromTick, true), held);
        var left = Assert.Single(anna.Session.Presence.OfType<PlayerLeft>());
        Assert.Equal(P(2), left.Player);
        Assert.Contains($"room {room.Id}: Cleo (player 2) out of step 4 times: taken out of the match at tick {left.FromTick}", relay.Lines);
        Assert.All(green.Where(g => g.Tick <= left.FromTick), g => Assert.Equal(Controller.Player, g.Controller));
        Assert.Contains(green, g => g.Tick > left.FromTick);
        Assert.All(green.Where(g => g.Tick > left.FromTick), g => Assert.Equal(Controller.Ai, g.Controller));

        // Neither with her key nor by her name may she come back.
        var again = await relay.Connect("Cleo", web: false);
        again.Connection.Send(new RejoinMatch(room.Id, cleo.Me, cleo.Key));
        Assert.Equal("you were taken out of that match", await Refusal(again));
        again.Connection.Send(new RejoinMatch(room.Id));
        Assert.Equal("you were taken out of that match", await Refusal(again));

        Assert.True(RelayClients.AssertRecovered(relay.Relay, room.Id, clients) > 0);
        Assert.True(RelayClients.AssertAgree([anna, ben]) >= 600 / 20);
        RelayClients.AssertPlaysBack(anna);
    }

    /// <summary>
    /// The relay's side alone, with checksums sent by hand and no worlds (P2 design §5.2): a value more than half of the
    /// playing persons reported is agreed without waiting for the rest; a straggler, an extra report of a returning world and
    /// a rebuilt world's catch-up are checked against it; an old world's reports after its rebuild began count for nothing;
    /// the fourth time out of step takes the player out.
    /// </summary>
    [Fact]
    public async Task The_relay_settles_on_a_majority_passes_over_an_old_worlds_reports_and_checks_a_rebuilt_ones_against_the_agreed()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, players) = await Waiting(relay);
        var (anna, ben, cleo) = (clients[0], clients[1], clients[2]);
        Task<List<NetMessage>> Report(RelayClient client, long tick, ulong value) => Said(client, new ChecksumReport(tick, players[client], value));
        int Resyncs() => relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(2))!.Resyncs);

        foreach (var client in clients)
            await Report(client, 20, 7);
        Assert.Equal(7ul, relay.Relay.Agreed(room.Id, 20));

        // Two of three is more than half: agreed without Cleo's; hers, when it comes, is out of step.
        await Report(anna, 40, 3);
        Assert.Null(relay.Relay.Agreed(room.Id, 40));
        await Report(ben, 40, 3);
        Assert.Equal(3ul, relay.Relay.Agreed(room.Id, 40));
        Assert.Null(relay.Relay.Pending(room.Id, 40));
        var resumed = Assert.Single((await Report(cleo, 40, 4)).OfType<MatchResumed>());
        Assert.Equal((P(2), ResumeReason.Resync, 1), (resumed.Player, resumed.Reason, resumed.Resyncs));
        Assert.Contains($"room {room.Id}: Cleo (player 2) out of step at tick 40: 0000000000000004 against the agreed 0000000000000003", relay.Lines);

        // Until her rebuilt world is built, what her old one reports counts for nothing: it is no report, nor out of step.
        Assert.DoesNotContain(await Report(cleo, 60, 5), m => m is MatchResumed or LeftMatch);
        Assert.DoesNotContain(await Report(cleo, 40, 4), m => m is MatchResumed or LeftMatch);
        Assert.Null(relay.Relay.Pending(room.Id, 60));
        await Report(anna, 60, 6);
        await Report(ben, 60, 6);
        Assert.Equal(6ul, relay.Relay.Agreed(room.Id, 60));
        Assert.Equal(1, Resyncs());

        // Built, her world catches up: what it reports of agreed ticks is checked at once, and agrees.
        cleo.Load();
        foreach (var (tick, value) in new (long, ulong)[] { (20, 7), (40, 3), (60, 6) })
            Assert.DoesNotContain(await Report(cleo, tick, value), m => m is MatchResumed or LeftMatch);
        Assert.Equal(1, Resyncs());
        // While returning she does not count towards a majority (two play), but her report waits with the tick's.
        await Report(cleo, 80, 8);
        Assert.Equal(new Dictionary<PlayerId, ulong> { [P(2)] = 8 }, relay.Relay.Pending(room.Id, 80));
        await Report(anna, 80, 8);
        Assert.Null(relay.Relay.Agreed(room.Id, 80));
        await Report(ben, 80, 8);
        Assert.Equal(8ul, relay.Relay.Agreed(room.Id, 80));
        // One of hers that differs is out of step once the tick is agreed.
        await Report(cleo, 100, 1);
        await Report(anna, 100, 2);
        await Report(ben, 100, 2);
        Assert.Equal(2, (await cleo.Next<MatchResumed>()).Resyncs);
        Assert.Contains($"room {room.Id}: Cleo (player 2) out of step at tick 100: 0000000000000001 against 0000000000000002, agreed by 2 of 2 playing", relay.Lines);

        // A rebuilt world whose catch-up differs from the agreed is out of step again at once, the fourth time for good.
        cleo.Load();
        Assert.Equal(3, Assert.Single((await Report(cleo, 60, 9)).OfType<MatchResumed>()).Resyncs);
        cleo.Load();
        Assert.Equal(new LeftMatch(TakenOut), Assert.Single((await Report(cleo, 60, 9)).OfType<LeftMatch>()));
        Assert.Equal((SeatState.Barred, 4), relay.Relay.WithBeat(room.Id, beat => (beat.Seat(P(2))!.State, beat.Seat(P(2))!.Resyncs)));
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: Cleo (player 2) out of step 4 times: taken out of the match at tick ", StringComparison.Ordinal));
        cleo.Connection.Send(new RejoinMatch(room.Id));
        Assert.Equal("you were taken out of that match", await Refusal(cleo));
    }

    /// <summary>
    /// When every playing person reported a tick and no value is more than half's, every reporter is out of step (P2 design
    /// §5.2); and once the end is agreed a world out of step is only noted, its battle not counted, and nobody is resynced.
    /// </summary>
    [Fact]
    public async Task Without_a_majority_every_reporter_is_rebuilt_and_once_the_end_is_agreed_nobody_is()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, players) = await Waiting(relay);
        var (anna, ben, cleo) = (clients[0], clients[1], clients[2]);

        // Three values, one each: nothing is agreed, and all three are rebuilt.
        await Said(anna, new ChecksumReport(20, players[anna], 1));
        await Said(ben, new ChecksumReport(20, players[ben], 2));
        Assert.Null(relay.Relay.Agreed(room.Id, 20));
        var heard = await Said(cleo, new ChecksumReport(20, players[cleo], 3));
        Assert.Null(relay.Relay.Agreed(room.Id, 20));
        foreach (var resumed in new[] { await anna.Next<MatchResumed>(), await ben.Next<MatchResumed>(), Assert.Single(heard.OfType<MatchResumed>()) })
            Assert.Equal((ResumeReason.Resync, 1), (resumed.Reason, resumed.Resyncs));
        Assert.Contains($"room {room.Id}: Ben (player 1) out of step at tick 20: 0000000000000002, no value agreed by more than half of 3 playing", relay.Lines);

        // A second match: two of three agree on the end; Finn's world, out of step after it, is only noted.
        var (other, people, ids) = await Waiting(relay, "Dan", "Eve", "Finn");
        foreach (var client in people.Take(2))
            await Said(client, new MatchDecided(5));
        Assert.Equal(5, relay.Relay.WithBeat(other.Id, beat => beat.DecidedAt));
        foreach (var (client, value) in people.Zip(new ulong[] { 1, 1, 2 }))
            Assert.DoesNotContain(await Said(client, new ChecksumReport(20, ids[client], value)), m => m is MatchResumed or LeftMatch);
        Assert.Equal(1ul, relay.Relay.Agreed(other.Id, 20));
        Assert.Contains($"room {other.Id}: Finn (player 2) out of step at tick 20: 0000000000000002 against the agreed 0000000000000001; the match is decided at tick 5: nothing done",
            relay.Lines);
        Assert.Equal((SeatState.Playing, 0), relay.Relay.WithBeat(other.Id, beat => (beat.Seat(P(2))!.State, beat.Seat(P(2))!.Resyncs)));
        Assert.DoesNotContain(relay.Lines, l => l.StartsWith($"room {other.Id}: resync ", StringComparison.Ordinal));
    }

    /// <summary>A skirmish of three commanders, Anna Red (player 0), Ben Blue (1) and Cleo Green (2), each with a script.</summary>
    private static async Task<(RoomState Room, List<RelayClient> Clients)> Skirmish(RelayClients relay)
    {
        List<RelayClient> clients = [await relay.Connect("Anna", web: false), await relay.Connect("Ben", web: true), await relay.Connect("Cleo", web: false)];
        var room = await RelayClients.Open(clients[0],
            new CreateRoom("Three", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], new MatchOptions(), FillWithAi: false), [.. clients.Skip(1)]);
        await RelayClients.Begin(clients[0], clients);
        Assert.Equal([P(0), P(1), P(2)], clients.Select(c => c.Me));
        foreach (var client in clients)
            client.Script = new CommanderScript(client.Me, 1200 + client.Me.Value, gives: false);
        return (room, clients);
    }

    /// <summary>
    /// A skirmish of three whose worlds nobody runs: the clients take the match, say their worlds are built, and send what a
    /// test has them send. Returns each client's player.
    /// </summary>
    private static async Task<(RoomState Room, List<RelayClient> Clients, Dictionary<RelayClient, PlayerId> Players)> Waiting(RelayClients relay,
        string first = "Anna", string second = "Ben", string third = "Cleo")
    {
        List<RelayClient> clients = [await relay.Connect(first, web: false), await relay.Connect(second, web: true), await relay.Connect(third, web: false)];
        var room = await RelayClients.Open(clients[0],
            new CreateRoom("Three", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], new MatchOptions(), FillWithAi: false), [.. clients.Skip(1)]);
        var match = await RelayClients.Begin(clients[0], clients, play: false);
        foreach (var client in clients)
            client.Load();
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Anchor) is not null, TimeSpan.FromSeconds(15), "the beat begun");
        return (room, clients, clients.ToDictionary(c => c, c => match.Players.Single(p => p.ClientId == c.Id).Player!.Value));
    }

    /// <summary>
    /// A client sends a message and waits until the relay handled it (it answers a list of rooms in the order it was asked);
    /// returns what the client was sent until then.
    /// </summary>
    private static async Task<List<NetMessage>> Said(RelayClient client, NetMessage message)
    {
        client.Connection.Send(message);
        client.Connection.Send(new ListRooms());
        var heard = new List<NetMessage>();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (await client.Connection.Inbox.ReadAsync(timeout.Token) is var next && next is not RoomList)
            heard.Add(next);
        return heard;
    }

    /// <summary>The next refusal a client is sent.</summary>
    private static async Task<string> Refusal(RelayClient client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            if (await client.Connection.Inbox.ReadAsync(timeout.Token) is Refused refused)
                return refused.Reason;
        }
    }
}

/// <summary>
/// Out of step on the real clock (P2 design §5.2): a player whose game hangs is marked and stays playing, yet the others'
/// checksums are agreed without theirs; when the hung game runs again, its report is checked against what was agreed.
/// The test measures time, so it runs with the other tests of the beat on the clock, one at a time.
/// </summary>
[Collection(nameof(BeatRelayTests))]
public class ResyncClockTests
{
    /// <summary><see cref="BeatTimings.Quick"/> (one resync, then out), a held seat handed over after ten seconds rather than one.</summary>
    private static readonly BeatTimings Holding = BeatTimings.Quick with { LoadTimeout = TimeSpan.FromSeconds(30), LagLeave = 200 };

    [Fact]
    public async Task A_hung_player_holds_no_checksum_back_and_its_report_when_it_runs_again_is_checked_against_the_agreed()
    {
        await using var relay = await RelayClients.Start(Holding);
        List<RelayClient> clients = [await relay.Connect("Anna", web: false), await relay.Connect("Ben", web: true), await relay.Connect("Cleo", web: false),
            await relay.Connect("Dan", web: true)];
        var room = await RelayClients.Open(clients[0],
            new CreateRoom("Four", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green, Race.Yellow], new MatchOptions(), FillWithAi: false), [.. clients.Skip(1)]);
        await RelayClients.Begin(clients[0], clients);
        var (anna, cleo) = (clients[0], clients[2]);
        foreach (var client in clients)
            client.Script = new CommanderScript(client.Me, 1300 + client.Me.Value, gives: false);
        var clock = relay.Relay.Clock(room.Id);
        RelayClients.Run(clients, clock);
        await Until(relay, () => cleo.Tick >= 60, TimeSpan.FromSeconds(30), "Cleo at tick 60");

        // Cleo's game hangs. The next checksum tick her world has still to run past is agreed by the three others: more than
        // half of the four playing, while she is marked but still plays.
        cleo.Frozen = true;
        await Task.Delay(100);
        var froze = cleo.Tick;
        var next = (froze + 1) / 20 * 20 + 20;
        await Until(relay, () => relay.Relay.Agreed(room.Id, next) is not null && relay.Relay.WithBeat(room.Id, beat => beat.Seat(cleo.Me)!.Marked),
            TimeSpan.FromSeconds(10), $"tick {next} agreed and Cleo marked");
        Assert.Equal(SeatState.Playing, relay.Relay.WithBeat(room.Id, beat => beat.Seat(cleo.Me)!.State));
        Assert.Equal(froze, cleo.Tick);
        Assert.DoesNotContain(cleo.History, h => h.Tick >= next);

        // Her world goes its own way as it runs again: her report of that tick, when it comes, is out of step with the agreed one.
        cleo.Corrupt(froze + 1);
        cleo.Frozen = false;
        await Until(relay, () => cleo.Rebuilds == 1 && relay.Relay.WithBeat(room.Id, beat => beat.Seat(cleo.Me)!.State) == SeatState.Playing,
            TimeSpan.FromSeconds(20), "Cleo rebuilt and back");
        await Until(relay, () => cleo.Tick > anna.Tick - 20 && anna.Tick > next + 100, TimeSpan.FromSeconds(20), "Cleo caught up");
        RelayClients.Halt(clients);

        var resumed = Assert.Single(cleo.Rebuilt);
        Assert.Equal((ResumeReason.Resync, 1), (resumed.Reason, resumed.Resyncs));
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: Cleo (player {cleo.Me}) out of step at tick {next}: ", StringComparison.Ordinal)
                                          && l.Contains(" against the agreed ", StringComparison.Ordinal));
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: resync 1/1 of Cleo (player {cleo.Me}) from tick ", StringComparison.Ordinal));
        // Her race was let go of from the mark, and is hers again.
        Assert.False(anna.Session.Lagging(cleo.Me));
        Assert.True(RelayClients.AssertRecovered(relay.Relay, room.Id, clients) > 0);
        Assert.True(RelayClients.AssertSumsAgree(clients, resyncs: true) > 0);
    }

    /// <summary>Waits as <see cref="RelayClients.Until"/> does; a failure shows the relay's last lines.</summary>
    private static async Task Until(RelayClients relay, Func<bool> done, TimeSpan within, string what)
    {
        var watch = System.Diagnostics.Stopwatch.StartNew();
        while (!done())
        {
            if (watch.Elapsed > within)
                Assert.Fail($"{what}: not within {within.TotalSeconds:0.#} s; the relay's last lines:{Environment.NewLine}{string.Join(Environment.NewLine, relay.Lines.TakeLast(40))}");
            await Task.Delay(20);
        }
    }
}
