using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// Joining a running battle late, through the real relay (P2 design §6.2, §6.3, slice S3): a player asks to join a battle whose
/// beat runs and takes a seat a bot has played since the start, on the team with fewer people or the one they name; their
/// world is rebuilt from the relay's log while the bot plays on, and once it is on time every world turns the bot's row into
/// theirs at one tick, and they take its hero over as it stands. A battle with ground takes only joiners with that ground; a
/// skirmish, a battle not begun or decided, and a seat that is no bot's take nobody; a joiner lost before joining leaves the
/// seat the bot's; one resynced or taken over by key while catching up still joins; a joiner who joined is a person of the
/// match from then on, who leaves and comes back by key; and the lobby counts the seats to join.
/// </summary>
public class LateJoinTests
{
    private static PlayerId P(int value) => new(value);

    [Fact]
    public async Task A_player_joins_a_running_five_against_five_in_a_bots_seat_takes_its_hero_over_and_every_world_agrees()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        var anna = clients[0];
        // Red's seats are players 0 and 4 to 7, Green's 2 and 8 to 11: Anna and Cleo play Red, Ben Green, bots the other seven.
        Assert.Equal([P(0), P(2), P(4)], clients.Select(c => c.Me));
        await RelayClients.PlayTo(300, clients);
        Assert.Contains(P(8), anna.World.HeroBots.Keys);

        // Zoe joins: Green has fewer people, and the lowest numbered of its bots is player 8. Her world is the bot's as yet.
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        Assert.Equal((P(8), Race.Green, ResumeReason.LateJoin), (zoe.Me, zoe.Race, zoe.Resumed!.Reason));
        Assert.NotNull(zoe.Resumed.Key);
        Assert.Equal(zoe.Resumed.Key, zoe.Key);
        Assert.Equal(PlayerKind.Bot, zoe.World.Players[P(8)].Kind);
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: late join of Zoe (player 8) from tick ", StringComparison.Ordinal));
        zoe.Script = new HeroScript(zoe.Me, 908);
        clients.Add(zoe);
        await RelayClients.PlayTo(800, clients);

        // Every world turned the bot's row into Zoe's at one tick, after she asked, once her rebuilt world was on time.
        var joined = Assert.Single(anna.Session.Presence.OfType<PlayerJoined>());
        Assert.Equal((P(8), "Zoe"), (joined.Player, joined.Name));
        Assert.True(joined.FromTick > zoe.Resumed.FromTick, $"joined at {joined.FromTick}, rebuilt from {zoe.Resumed.FromTick}");
        foreach (var client in clients)
        {
            Assert.Equal(anna.Session.Presence, client.Session.Presence);
            Assert.Equal(new PlayerSeat(P(8), Race.Green, PlayerKind.Human, "Zoe"), client.World.Players[P(8)]);
            Assert.Equal([P(8)], client.World.Players.Joined);
            // She drives the bot's hero now: no bot does, and the six other bots still drive theirs.
            Assert.DoesNotContain(P(8), client.World.HeroBots.Keys);
            Assert.Equal(6, client.World.HeroBots.Count);
        }
        // Her orders reach every world from the join on.
        Assert.Contains(anna.Session.Played, t => t.Player == P(8) && t.Tick >= joined.FromTick && t.Commands.Count > 0);
        Assert.Equal(SeatState.Playing, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(8))!.State));
        Assert.Contains($"room {room.Id}: player 8 back at tick {joined.FromTick} (joined)", relay.Lines);

        Assert.True(RelayClients.AssertAgree(clients) >= 800 / 20);
        // Every recording plays back in turn mode, the joiner's too, with the join at its tick.
        foreach (var client in clients)
            Assert.Equal([P(8)], RelayClients.AssertPlaysBack(client).Players.Joined);
    }

    [Fact]
    public async Task A_late_joiner_takes_a_bots_seat_on_the_team_with_fewer_people_or_the_one_named_and_never_another()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"], play: false);

        // The beat has not begun before every world is built: nobody may join yet.
        Assert.Equal("Una: no seat to join in that battle", (await Refused(() => relay.LateJoin("Una", web: false, room.Id))).Message);
        foreach (var client in clients)
            client.Load();
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Anchor) is not null, TimeSpan.FromSeconds(15), "the beat begun");

        // Red has Anna and Cleo, Green Ben: Zoe goes to Green's lowest numbered bot. Two a side then, a tie: Yan to Red's.
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        var yan = await relay.LateJoin("Yan", web: false, room.Id);
        // The eighth seat is Green's third place, player 9.
        var xia = await relay.LateJoin("Xia", web: true, room.Id, seat: 7);
        // Three a side: Red again, under a name the room does not show yet.
        var zoe2 = await relay.LateJoin("Zoe", web: false, room.Id);
        Assert.Equal([P(8), P(5), P(9), P(6)], new[] { zoe, yan, xia, zoe2 }.Select(c => c.Me));
        Assert.All(new[] { zoe, yan, xia, zoe2 }, c => Assert.Equal(ResumeReason.LateJoin, c.Resumed!.Reason));
        var seats = relay.Relay.WithBeat(room.Id, beat => new[] { 8, 5, 9, 6 }.Select(p => beat.Seat(P(p))!).Select(s => (s.State, s.Returning, s.Name)).ToList());
        Assert.Equal([(SeatState.Returning, ReturnKind.Joining, "Zoe"), (SeatState.Returning, ReturnKind.Joining, "Yan"),
            (SeatState.Returning, ReturnKind.Joining, "Xia"), (SeatState.Returning, ReturnKind.Joining, "Zoe 2")], seats);

        // Anna's seat, Zoe's now, and a seat the room does not have are refused.
        foreach (var (seat, why) in new[] { (0, "that seat is not free"), (6, "that seat is not free"), (10, "no such seat"), (-1, "no such seat") })
            Assert.Equal($"Una: {why}", (await Refused(() => relay.LateJoin("Una", web: false, room.Id, seat))).Message);
        // Nor may a player of the match join it again as another.
        clients[1].Connection.Send(new JoinRoom(room.Id));
        Assert.Equal("that match has already started", await Refusal(clients[1]));
        Assert.Equal(SeatState.Playing, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(2))!.State));
    }

    [Fact]
    public async Task A_battle_with_ground_takes_only_joiners_with_that_ground_and_one_without_takes_any()
    {
        await using var relay = await RelayClients.Start();
        var (grounded, people, match) = await Battle(relay, ["Anna", "Ben"], play: false, terrain: 777);
        var (plain, others, without) = await Battle(relay, ["Dan", "Eve"], play: false);
        Assert.Equal((777L, (long?)null), (match.Terrain!.Value, without.Terrain));
        foreach (var client in people.Concat(others))
            client.Load();
        foreach (var room in new[] { grounded, plain })
            await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Anchor) is not null, TimeSpan.FromSeconds(15), "the beat begun");

        // Without the battle's ground the joiner's shots would go through hills its world does not have.
        foreach (var terrain in new long?[] { 778, null })
            Assert.Equal("Zoe: your game's ground differs from that battle's", (await Refused(() => relay.LateJoin("Zoe", web: true, grounded.Id, terrain: terrain))).Message);
        // One a side, a tie: Red's lowest numbered bot.
        Assert.Equal(P(4), (await relay.LateJoin("Zoe", web: true, grounded.Id, terrain: 777)).Me);

        // A battle without ground takes a joiner with or without.
        Assert.Equal(P(4), (await relay.LateJoin("Yan", web: false, plain.Id, terrain: 5)).Me);
        Assert.Equal(P(8), (await relay.LateJoin("Xia", web: false, plain.Id)).Me);
    }

    [Fact]
    public async Task The_lobby_counts_the_seats_to_join_while_the_beat_runs_and_a_skirmish_takes_no_late_joiner()
    {
        await using var relay = await RelayClients.Start();
        var lou = await relay.Connect("Lou", web: false);
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"], play: false);

        // Started, but before every world is built there is nothing to join; once the beat runs, the seven bots' seats.
        Assert.Equal((true, 0), Seen(await Listed(lou, room.Id)));
        foreach (var client in clients)
            client.Load();
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Anchor) is not null, TimeSpan.FromSeconds(15), "the beat begun");
        Assert.Equal((true, 7), Seen(await Listed(lou, room.Id)));
        await relay.LateJoin("Zoe", web: true, room.Id);
        Assert.Equal((true, 6), Seen(await Listed(lou, room.Id)));

        // A running skirmish counts none and refuses a joiner as before.
        var dan = await relay.Connect("Dan", web: false);
        var eve = await relay.Connect("Eve", web: true);
        var skirmish = await RelayClients.Open(dan, new CreateRoom("Skirmish", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false), eve);
        await RelayClients.Begin(dan, [dan, eve], play: false);
        dan.Load();
        eve.Load();
        await RelayClients.Until(() => relay.Relay.WithBeat(skirmish.Id, beat => beat.Anchor) is not null, TimeSpan.FromSeconds(15), "the skirmish's beat begun");
        Assert.Equal((true, 0), Seen(await Listed(lou, skirmish.Id)));
        Assert.Equal("Una: that match has already started", (await Refused(() => relay.LateJoin("Una", web: false, skirmish.Id))).Message);

        // Once the battle's end is agreed (two of its three people say so), nothing is to join.
        foreach (var client in clients.Take(2))
            client.Connection.Send(new MatchDecided(3));
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.DecidedAt) is not null, TimeSpan.FromSeconds(15), "the end agreed");
        Assert.Equal((true, 0), Seen(await Listed(lou, room.Id)));
        Assert.Equal("Una: no seat to join in that battle", (await Refused(() => relay.LateJoin("Una", web: false, room.Id))).Message);
    }

    [Fact]
    public async Task A_joiner_lost_before_joining_leaves_the_seat_a_bots_and_nothing_happened_in_any_world()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        var anna = clients[0];
        await RelayClients.PlayTo(200, clients);

        // Zoe's game asks to join, and is gone before its world caught up.
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        Assert.Equal(P(8), zoe.Me);
        await zoe.Leave();
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(8))!.State) == SeatState.Bot, TimeSpan.FromSeconds(15), "the seat a bot's again");
        Assert.Contains($"room {room.Id}: player 8 lost before joining: the seat is a bot's again", relay.Lines);
        await RelayClients.PlayTo(400, clients);
        Assert.All(clients, c => Assert.Empty(c.Session.Presence));
        Assert.All(clients, c => Assert.Contains(P(8), c.World.HeroBots.Keys));

        // The seat is free to join again: Yan takes it, and joins.
        var yan = await relay.LateJoin("Yan", web: false, room.Id);
        Assert.Equal(P(8), yan.Me);
        clients.Add(yan);
        await RelayClients.PlayTo(700, clients);
        Assert.Equal(new PlayerJoined(P(8), anna.Session.Presence.OfType<PlayerJoined>().Single().FromTick, "Yan"), Assert.Single(anna.Session.Presence));
        Assert.True(RelayClients.AssertAgree(clients) >= 700 / 20);
    }

    /// <summary>
    /// While a joiner catches up its seat stays a joining one whatever rebuilds it again (P2 design §2.5): a resync of its world,
    /// or its key taken to another connection; either way it ends with <see cref="PlayerJoined"/>, never a return of a bot's row.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_joiner_resynced_or_taken_over_by_key_while_catching_up_still_joins(bool byKey)
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        var anna = clients[0];
        await RelayClients.PlayTo(300, clients);

        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        RelayClient joiner;
        if (byKey)
        {
            // Zoe's game hangs before it caught up; she starts it again, and it asks for her seat with its key.
            joiner = await relay.RejoinWithKey(zoe, web: false);
            Assert.Equal(new LeftMatch("you came back to this match from another connection"), await zoe.Next<LeftMatch>());
            Assert.Equal((P(8), ResumeReason.Rejoin, (string?)null), (joiner.Me, joiner.Resumed!.Reason, joiner.Resumed.Key));
        }
        else
        {
            // Zoe's world goes its own way while it catches up: her report of 60 is not the one agreed long ago.
            zoe.Corrupt(50);
            joiner = zoe;
        }
        Assert.Equal(ReturnKind.Joining, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(8))!.Returning));
        clients.Add(joiner);
        await RelayClients.PlayTo(800, clients);

        if (!byKey)
        {
            Assert.Equal([ResumeReason.LateJoin, ResumeReason.Resync], zoe.Rebuilt.Select(r => r.Reason));
            Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: Zoe (player 8) out of step at tick 60: ", StringComparison.Ordinal)
                                              && l.Contains(" against the agreed ", StringComparison.Ordinal));
            Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: resync 1/3 of Zoe (player 8) from tick ", StringComparison.Ordinal));
            Assert.True(RelayClients.AssertRecovered(relay.Relay, room.Id, clients) > 0);
        }
        // She joined, under her name, once: no world ever heard of her leaving or returning.
        foreach (var client in clients.Where(c => c.InMatch))
        {
            Assert.Equal([(P(8), "Zoe")], client.Session.Presence.Select(m => m is PlayerJoined j ? (j.Player, j.Name) : (default(PlayerId), "")));
            Assert.Equal(PlayerKind.Human, client.World.Players[P(8)].Kind);
        }
        Assert.Equal(SeatState.Playing, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(8))!.State));
        Assert.True(RelayClients.AssertAgree(clients, resyncs: !byKey) >= 800 / 20);
        RelayClients.AssertPlaysBack(joiner);
    }

    /// <summary>
    /// A world rebuilt after the join, from the join in a sealed segment of the relay's log or in its tail (P2 design §2.10):
    /// the join is applied at its tick again, and the rebuilt world agrees with everyone's from its first checksum on.
    /// </summary>
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_world_rebuilt_after_the_join_has_it_from_the_log_and_agrees_from_tick_20_on(bool joinSealed)
    {
        await using var relay = await RelayClients.Start(BeatTimings.Scripted with { SegmentTicks = 200 });
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        var anna = clients[0];
        await RelayClients.PlayTo(100, clients);
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        zoe.Script = new HeroScript(zoe.Me, 908);
        clients.Add(zoe);
        // Until every world ran the join's tick.
        for (var to = 120L; anna.Session.Presence.OfType<PlayerJoined>().FirstOrDefault() is not { } join || anna.World.Tick <= join.FromTick; to += 10)
        {
            Assert.True(to < 600, "Zoe never joined");
            await RelayClients.PlayTo(to, clients);
        }
        var joined = anna.Session.Presence.OfType<PlayerJoined>().Single().FromTick;
        if (joinSealed)
        {
            // Past the end of the segment the join lies in, and until that segment is sealed.
            var end = (joined / 200 + 1) * 200;
            await RelayClients.PlayTo(end + 20, clients);
            await RelayClients.Until(() => relay.Relay.LogShape(room.Id).Segments.Any(s => s.From <= joined && joined <= s.To), TimeSpan.FromSeconds(15), "the join sealed");
        }
        else
            Assert.True(joined >= relay.Relay.LogShape(room.Id).TailFrom, $"joined at {joined}, the tail from {relay.Relay.LogShape(room.Id).TailFrom}");

        // Her game asks for its seat again over the line it plays on, as after a gap: her world is rebuilt from the log.
        zoe.RejoinHere();
        await RelayClients.PlayTo(anna.World.Tick + 300, clients);
        Assert.Equal([ResumeReason.LateJoin, ResumeReason.Rejoin], zoe.Rebuilt.Select(r => r.Reason));
        var rebuilt = zoe.History.Where(h => h.World == 2).ToList();
        Assert.Equal(20, rebuilt[0].Tick);
        Assert.All(rebuilt, h => Assert.Equal(relay.Relay.Agreed(room.Id, h.Tick) ?? h.Value, h.Value));
        // In the rebuilt world she joined at the same tick; she left for the rebuild and is back.
        Assert.Equal(anna.Session.Presence, zoe.Session.Presence);
        Assert.Equal([typeof(PlayerJoined), typeof(PlayerLeft), typeof(PlayerReturned)], anna.Session.Presence.Select(m => m.GetType()));
        Assert.True(RelayClients.AssertAgree(clients) >= anna.World.Tick / 20);
        RelayClients.AssertPlaysBack(zoe);
    }

    [Fact]
    public async Task A_joiner_who_joined_is_a_person_of_the_match_who_leaves_and_comes_back_by_key()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        var anna = clients[0];
        await RelayClients.PlayTo(200, clients);
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        zoe.Script = new HeroScript(zoe.Me, 908);
        clients.Add(zoe);
        await RelayClients.PlayTo(500, clients);
        Assert.Single(anna.Session.Presence.OfType<PlayerJoined>());

        // She quits: a bot drives her hero, and her seat is hers to take back, never a bot's to join.
        await zoe.Leave();
        await RelayClients.PlayTo(600, clients);
        Assert.Equal(SeatState.Gone, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(8))!.State));
        Assert.All(clients.Where(c => c.InMatch), c => Assert.Contains(P(8), c.World.HeroBots.Keys));
        Assert.Equal(6, relay.Relay.WithBeat(room.Id, beat => beat.Seats.Count(s => s.State == SeatState.Bot)));
        Assert.Equal("Yan: that seat is not free", (await Refused(() => relay.LateJoin("Yan", web: false, room.Id, seat: 6))).Message);

        // With her key she is back as herself.
        var back = await relay.RejoinWithKey(zoe, web: false);
        Assert.Equal((P(8), ResumeReason.Rejoin), (back.Me, back.Resumed!.Reason));
        back.Script = new HeroScript(back.Me, 909);
        clients.Add(back);
        await RelayClients.PlayTo(900, clients);
        Assert.Equal([typeof(PlayerJoined), typeof(PlayerLeft), typeof(PlayerReturned)], anna.Session.Presence.Select(m => m.GetType()));
        Assert.All(anna.Session.Presence, m => Assert.Equal(P(8), m switch { PlayerJoined j => j.Player, PlayerLeft l => l.Player, _ => ((PlayerReturned)m).Player }));
        foreach (var client in clients.Where(c => c.InMatch))
        {
            Assert.Equal(new PlayerSeat(P(8), Race.Green, PlayerKind.Human, "Zoe"), client.World.Players[P(8)]);
            Assert.DoesNotContain(P(8), client.World.HeroBots.Keys);
        }
        Assert.True(RelayClients.AssertAgree(clients) >= 900 / 20);
        RelayClients.AssertPlaysBack(back);
    }

    /// <summary>
    /// In a two against two of today's layout each seat is a race of its own (P2 design §6.2): a joiner takes a bot's race
    /// back from the computer, and a world of theirs rebuilt after the join hands the race's hero to a bot and back again, as
    /// every world does at the same ticks.
    /// </summary>
    [Fact]
    public async Task A_joiner_takes_a_bots_race_of_one_in_a_two_against_two_and_a_resync_after_the_join_hands_it_to_a_bot_and_back()
    {
        await using var relay = await RelayClients.Start();
        List<RelayClient> clients = [await relay.Connect("Anna", web: false), await relay.Connect("Ben", web: true)];
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 1, Lives = 3 }, FogOfWar = false };
        var room = await RelayClients.Open(clients[0], new CreateRoom("Pair", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false), clients[1]);
        await RelayClients.Begin(clients[0], clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 1, line: c.Id % 2));
        foreach (var client in clients)
            client.Script = new HeroScript(client.Me, 900 + client.Me.Value);
        // Anna plays Red and Ben Blue, team A; Green and Yellow are the bots' races of one.
        Assert.Equal([P(0), P(1)], clients.Select(c => c.Me));
        await RelayClients.PlayTo(200, clients);

        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        Assert.Equal((P(2), Race.Green), (zoe.Me, zoe.Race));
        zoe.Script = new HeroScript(zoe.Me, 902);
        zoe.Rescript = rebuilt => new HeroScript(rebuilt.Me, 912);
        clients.Add(zoe);
        var anna = clients[0];
        for (var to = 220L; anna.Session.Presence.OfType<PlayerJoined>().FirstOrDefault() is not { } join || zoe.World.Tick <= join.FromTick + 5; to += 10)
        {
            Assert.True(to < 800, "Zoe never joined");
            await RelayClients.PlayTo(to, clients);
        }
        var joined = anna.Session.Presence.OfType<PlayerJoined>().Single().FromTick;
        Assert.All(clients, c => Assert.Equal(Controller.Player, c.World.Races[Race.Green].Controller));

        // Her world goes its own way after the join: rebuilt, a bot drives the hero meanwhile, and she is back.
        zoe.Corrupt(zoe.World.Tick + 1);
        await RelayClients.PlayTo(anna.World.Tick + 400, clients);
        Assert.Equal([ResumeReason.LateJoin, ResumeReason.Resync], zoe.Rebuilt.Select(r => r.Reason));
        Assert.Equal([typeof(PlayerJoined), typeof(PlayerLeft), typeof(PlayerReturned)], anna.Session.Presence.Select(m => m.GetType()));
        Assert.Equal(joined, anna.Session.Presence.OfType<PlayerJoined>().Single().FromTick);
        foreach (var client in clients)
        {
            Assert.Equal(anna.Session.Presence, client.Session.Presence);
            Assert.Equal(new PlayerSeat(P(2), Race.Green, PlayerKind.Human, "Zoe"), client.World.Players[P(2)]);
            Assert.Equal(Controller.Player, client.World.Races[Race.Green].Controller);
            Assert.DoesNotContain(P(2), client.World.HeroBots.Keys);
        }
        Assert.True(RelayClients.AssertRecovered(relay.Relay, room.Id, clients) > 0);
        Assert.True(RelayClients.AssertAgree(clients, resyncs: true) >= anna.World.Tick / 20);
        foreach (var client in clients)
            RelayClients.AssertPlaysBack(client);
    }

    /// <summary>
    /// A seat's resyncs are its person's (P2 design §5.5): a joiner lost before joining leaves the seat a bot's, and the next
    /// one to join it starts with none of the lost one's, rather than taken out on their first resync.
    /// </summary>
    [Fact]
    public async Task A_joiner_starts_with_none_of_the_resyncs_of_one_lost_before_joining_the_same_seat()
    {
        await using var relay = await RelayClients.Start();
        // Begun, with no world run: the checksums are sent by hand.
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Anchor) is not null, TimeSpan.FromSeconds(15), "the beat begun");
        foreach (var client in clients)
            await Said(client, new ChecksumReport(20, client.Me, 7));
        Assert.Equal(7ul, relay.Relay.Agreed(room.Id, 20));

        // Xia's world goes its own way three times while it catches up, and her game is gone before it joined.
        var xia = await relay.LateJoin("Xia", web: false, room.Id);
        Assert.Equal(P(8), xia.Me);
        for (var i = 1; i <= 3; i++)
        {
            var resumed = Assert.Single((await Said(xia, new ChecksumReport(20, xia.Me, 9))).OfType<MatchResumed>());
            Assert.Equal((ResumeReason.Resync, i), (resumed.Reason, resumed.Resyncs));
            xia.Take(resumed);
        }
        await xia.Leave();
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(8))!.State) == SeatState.Bot, TimeSpan.FromSeconds(15), "the seat a bot's again");

        // Yan takes the seat: none of Xia's resyncs are his, and his world going its own way is his first.
        var yan = await relay.LateJoin("Yan", web: true, room.Id);
        Assert.Equal((P(8), 0), (yan.Me, yan.Resumed!.Resyncs));
        Assert.Equal(0, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(8))!.Resyncs));
        var heard = await Said(yan, new ChecksumReport(20, yan.Me, 9));
        Assert.DoesNotContain(heard, m => m is LeftMatch);
        Assert.Equal((ResumeReason.Resync, 1), Assert.Single(heard.OfType<MatchResumed>()) is var again ? (again.Reason, again.Resyncs) : default);
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: resync 1/3 of Yan (player 8) from tick ", StringComparison.Ordinal));
        // The match's summary still counts every time the seat's worlds were out of step.
        Assert.EndsWith("; out of step by player: 8 4", relay.Relay.WithBeat(room.Id, beat => beat.Summary()));
    }

    /// <summary>
    /// A player taken out of a battle for going out of step too often is out of it for good (P2 design §5.5): neither the
    /// game that was taken out nor one connecting again under that name may join the battle late in a bot's seat, which the
    /// lobby offers it; nor may a joiner taken out before it joined.
    /// </summary>
    [Fact]
    public async Task A_player_taken_out_for_going_out_of_step_may_not_join_the_battle_again_in_a_bots_seat()
    {
        await using var relay = await RelayClients.Start();
        // Begun, with no world run: the checksums are sent by hand.
        var (room, clients, _) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        await RelayClients.Until(() => relay.Relay.WithBeat(room.Id, beat => beat.Anchor) is not null, TimeSpan.FromSeconds(15), "the beat begun");
        foreach (var client in clients)
            await Said(client, new ChecksumReport(20, client.Me, 7));
        var cleo = clients[2];

        // Cleo's world goes its own way after every rebuild: three resyncs, then out.
        for (var i = 1; i <= 3; i++)
            cleo.Take(Assert.Single((await Said(cleo, new ChecksumReport(20, cleo.Me, 9))).OfType<MatchResumed>()));
        Assert.Contains(new LeftMatch("Your game kept going out of step with the others, so it has left the match."), await Said(cleo, new ChecksumReport(20, cleo.Me, 9)));
        Assert.Equal(SeatState.Barred, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(4))!.State));
        var free = relay.Relay.WithBeat(room.Id, beat => beat.Seats.Count(s => s.State == SeatState.Bot));

        // Back in the lobby, her game sees seats to join, and is refused; so is a game of hers connecting anew.
        cleo.Connection.Send(new JoinRoom(room.Id));
        Assert.Equal("you were taken out of that match", await Refusal(cleo));
        Assert.Equal("Cleo: you were taken out of that match", (await Refused(() => relay.LateJoin("Cleo", web: true, room.Id))).Message);

        // A joiner taken out before it joined: the same.
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        for (var i = 1; i <= 3; i++)
            zoe.Take(Assert.Single((await Said(zoe, new ChecksumReport(20, zoe.Me, 9))).OfType<MatchResumed>()));
        Assert.Contains(await Said(zoe, new ChecksumReport(20, zoe.Me, 9)), m => m is LeftMatch);
        zoe.Connection.Send(new JoinRoom(room.Id));
        Assert.Equal("you were taken out of that match", await Refusal(zoe));
        Assert.Equal("Zoe: you were taken out of that match", (await Refused(() => relay.LateJoin("Zoe", web: false, room.Id))).Message);
        Assert.Equal(free - 1, relay.Relay.WithBeat(room.Id, beat => beat.Seats.Count(s => s.State == SeatState.Bot)));
        // Anyone else may still join.
        Assert.Equal(ResumeReason.LateJoin, (await relay.LateJoin("Una", web: false, room.Id)).Resumed!.Reason);
    }

    /// <summary>
    /// A joiner takes a name no player of the battle shows (P2 design §6.2): not a member's, nor that of a person who left,
    /// whose row shows it in every world still; the name of the bot whose seat they take is theirs to keep.
    /// </summary>
    [Fact]
    public async Task A_joiner_takes_a_name_no_other_player_of_the_battle_shows_not_even_one_who_left()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, match) = await Battle(relay, ["Anna", "Ben", "Cleo"]);
        await RelayClients.PlayTo(100, clients);
        // Ben (Green, player 2) quits: a bot drives his hero, and his row is still called Ben.
        await clients[1].Leave();
        await RelayClients.PlayTo(200, clients);

        // Another Ben joins Green's lowest bot seat, player 8, as Ben 2; one named after the bot of seat 7 (player 9) keeps it.
        var again = await relay.LateJoin("Ben", web: false, room.Id);
        var bot = match.Players.Single(p => p.Player == P(9)).Name;
        var named = await relay.LateJoin(bot, web: true, room.Id, seat: 7);
        Assert.Equal((P(8), P(9)), (again.Me, named.Me));
        Assert.Equal(("Ben 2", bot), relay.Relay.WithBeat(room.Id, beat => (beat.Seat(P(8))!.Name, beat.Seat(P(9))!.Name)));
        again.Script = new HeroScript(again.Me, 908);
        named.Script = new HeroScript(named.Me, 909);
        clients.AddRange([again, named]);
        await RelayClients.PlayTo(700, clients);
        foreach (var client in clients.Where(c => c.InMatch))
        {
            Assert.Equal(("Ben", "Ben 2", bot), (client.World.Players[P(2)].Name, client.World.Players[P(8)].Name, client.World.Players[P(9)].Name));
            Assert.Equal([P(8), P(9)], client.World.Players.Joined);
        }
        Assert.True(RelayClients.AssertAgree(clients) >= 700 / 20);
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

    /// <summary>
    /// A five against five on Canyon Attack at tier I whose people are <paramref name="names"/> (every second over a
    /// WebSocket), bots in the other seats, each with the hero they bring and with <paramref name="terrain"/> as their ground;
    /// begun and played by the clients (each with a hero's script), or with <paramref name="play"/> false only taken.
    /// </summary>
    private static async Task<(RoomState Room, List<RelayClient> Clients, MatchInfo Match)> Battle(RelayClients relay, string[] names, bool play = true, long? terrain = null)
    {
        var clients = new List<RelayClient>();
        for (var i = 0; i < names.Length; i++)
            clients.Add(await relay.Connect(names[i], web: i % 2 == 0));
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 1, Lives = 3, TeamSize = 5 }, FogOfWar = false };
        var room = await RelayClients.Open(clients[0], new CreateRoom("Late", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false), [.. clients.Skip(1)]);
        var match = await RelayClients.Begin(clients[0], clients, c => BattleNetTests.Brought($"{c.Name}'s hero", 1, line: c.Id % 2), play, terrain);
        if (play)
        {
            foreach (var client in clients)
                client.Script = new HeroScript(client.Me, 900 + client.Me.Value);
        }
        return (room, clients, match);
    }

    /// <summary>What the lobby shows of a room: whether its match runs, and how many seats of it a player may join.</summary>
    private static (bool Started, int Joinable) Seen(RoomSummary room) => (room.Started, room.Joinable);

    /// <summary>The room as a client in the lobby finds it in the list of rooms.</summary>
    private static async Task<RoomSummary> Listed(RelayClient lobby, int room)
    {
        lobby.Connection.Send(new ListRooms());
        return (await lobby.Next<RoomList>()).Rooms.Single(r => r.Id == room);
    }

    /// <summary>A late join the relay refuses: the refusal, as the client's exception says it.</summary>
    private static Task<InvalidOperationException> Refused(Func<Task> join) => Assert.ThrowsAsync<InvalidOperationException>(join);

    /// <summary>The next refusal a client in a match is sent.</summary>
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
