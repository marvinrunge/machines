using Machines.Game;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// The story played together online and on the LAN, through the real relay (plan WP-M, decision 5): a story room has the
/// story's three seats, the host the Locator at the first; the others take Ike's and Bob's, and the seats nobody takes are
/// the computer's bots at the start. A player joins a running story late into a bot's Grunt and takes its hero over as it
/// stands, one who leaves hands it back to the computer in every world and comes back to it by key, every world agrees, and
/// the recordings play back. The lobby tells story rooms apart and counts the Grunts to join.
/// </summary>
public class StoryRoomTests
{
    private static PlayerId P(int value) => new(value);

    // From the repair station: Ike and Bob awake (the director keeps them asleep before the first checkpoint) and no hunter near.
    private static readonly StoryOptions Named = new() { IkeName = "Rust", BobName = "Bolt", Checkpoint = 5 };

    private static CreateRoom Room(StoryOptions? story = null) => RoomOptions.StoryRoom("Story", story ?? Named);

    /// <summary>A story room the first of <paramref name="names"/> opens and the others join, in that order.</summary>
    private static async Task<(RoomState Room, List<RelayClient> Clients)> Opened(RelayClients relay, string[] names)
    {
        var clients = new List<RelayClient>();
        for (var i = 0; i < names.Length; i++)
            clients.Add(await relay.Connect(names[i], web: i % 2 == 0));
        var room = await RelayClients.Open(clients[0], Room(), [.. clients.Skip(1)]);
        return (room, clients);
    }

    /// <summary>A story started from <see cref="Opened"/>, each client playing its seat's hero by a <see cref="HeroScript"/>.</summary>
    private static async Task<(RoomState Room, List<RelayClient> Clients, MatchInfo Match)> Started(RelayClients relay, string[] names)
    {
        var (room, clients) = await Opened(relay, names);
        var match = await RelayClients.Begin(clients[0], clients);
        foreach (var client in clients)
            client.Script = new HeroScript(client.Me, 700 + client.Me.Value);
        return (room, clients, match);
    }

    [Fact]
    public async Task A_story_room_seats_the_host_as_the_locator_and_the_others_as_the_grunts()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients) = await Opened(relay, ["Host", "Anna", "Ben"]);
        var (host, anna, ben) = (clients[0], clients[1], clients[2]);

        // The chapter is the scenario and the planet, the trio's race the one start position, its three seats in the room.
        Assert.Equal((StoryOptions.Prologue, StoryOptions.Prologue), (room.Scenario, room.Planet));
        Assert.Equal([Race.Red], room.Slots);
        Assert.Equal([Race.Red, Race.Red, Race.Red], room.Seats.Select(s => s.Race));
        Assert.Equal([host.Id, anna.Id, ben.Id], room.Seats.Select(s => s.ClientId));
        Assert.True(room.FillWithAi);
        Assert.Equal((VictoryCondition.Story, false, Named), (room.Options.Victory, room.Options.FogOfWar, room.Options.Story));
        Assert.Null(room.Options.Battle);
        Assert.Equal(["Locator", "Rust", "Bolt"], Enumerable.Range(0, 3).Select(i => RoomSeating.StoryRole(room, i)!));

        // The lobby shows it as a story room of three.
        var lou = await relay.Connect("Lou", web: false);
        lou.Connection.Send(new ListRooms());
        var listed = (await lou.Next<RoomList>()).Rooms.Single(r => r.Id == room.Id);
        Assert.Equal((RoomSummary.Story, 3, 3, 0), (listed.Mode, listed.Players, listed.MaxPlayers, listed.Joinable));
        lou.Connection.Send(new JoinRoom(room.Id));
        Assert.Equal("that room is full", await Refusal(lou));

        // The Locator's seat is the host's: nobody takes it, and the host takes no other. The Grunts' players may swap.
        ben.Connection.Send(new LeaveRoom());
        await host.Next<RoomState>(s => s.Players.Count == 2);
        anna.Connection.Send(new TakeSeat(0));
        Assert.Equal("the Locator's seat is the host's", await Refusal(anna));
        host.Connection.Send(new TakeSeat(2));
        Assert.Equal("the Locator's seat is the host's", await Refusal(host));
        await anna.TakeSeat(2);
        // A Grunt's seat the host closes is the computer's.
        host.Connection.Send(new SetSeat(1, SeatUse.Closed));
        var state = await host.Next<RoomState>(s => s.Seats[1].Use != SeatUse.Open);
        Assert.Equal(SeatUse.Computer, state.Seats[1].Use);
        Assert.Equal([SeatUse.Open, SeatUse.Computer], RoomSeating.Uses(state, 1));
        Assert.Empty(RoomSeating.Uses(state, 0));
        Assert.False(RoomSeating.MayTake(state, host.Id, 1));
        Assert.False(RoomSeating.MayTake(state, anna.Id, 0));

        // The host leaves before the start: the next one is host, and the Locator.
        host.Connection.Send(new LeaveRoom());
        state = await anna.Next<RoomState>(s => s.Players.Count == 1);
        Assert.Equal((anna.Id, anna.Id), (state.HostId, state.Seats[0].ClientId));
    }

    [Fact]
    public async Task A_story_room_plays_a_chapter_there_is_and_nothing_else()
    {
        await using var relay = await RelayClients.Start();
        var host = await relay.Connect("Host", web: false);

        host.Connection.Send(Room(Named with { Chapter = "akt9" }));
        Assert.Equal("no such chapter", await Refusal(host));
        host.Connection.Send(Room(Named with { Ship = Race.Red }));
        Assert.Equal("no such side", await Refusal(host));
        host.Connection.Send(Room() with { Options = Room().Options with { Battle = new() } });
        Assert.Equal("a story is no battle", await Refusal(host));
        // Whatever a room names for its map and seats, a story room is its chapter's, with blank names the Grunts' own.
        host.Connection.Send(Room(Named with { IkeName = " " }) with { Scenario = "m_desert", Planet = "m_desert", Slots = [Race.Blue, Race.Green] });
        var room = await host.Next<RoomState>();
        Assert.Equal((StoryOptions.Prologue, 3, "Ike"), (room.Scenario, room.Seats.Count, room.Options.Story!.IkeName));
    }

    [Fact]
    public async Task Two_people_play_the_prologue_with_bob_the_computers_and_every_world_agrees()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, match) = await Started(relay, ["Host", "Anna"]);
        var (host, anna) = (clients[0], clients[1]);

        // The host is the Locator, Anna Ike, Bob the computer's bot under the name the host gave him; the ship the computer's.
        Assert.Equal([(P(0), false, "Host"), (P(4), false, "Anna"), (P(5), true, "Bolt"), (P(1), true, "Ship")],
            match.Players.Select(p => (p.Player!.Value, p.Ai, p.Name)));
        Assert.Equal((P(0), P(4)), (host.Me, anna.Me));
        Assert.Equal([PlayerKind.Human, PlayerKind.Human, PlayerKind.Bot, PlayerKind.Ai], match.Table().Rows.Select(r => r.Kind));
        Assert.Equal(SeatState.Bot, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(5))!.State));
        Assert.Contains($"room {room.Id} started the story's prolog at checkpoint 5 with 2 players", relay.Lines);

        await RelayClients.PlayTo(400, clients);
        foreach (var client in clients)
        {
            Assert.Equal([P(5)], client.World.Story!.Bots);
            // Bob follows the Locator (stepping out of its way where it drives at him: a move aside, then the follow again).
            Assert.Equal(CompanionOrder.Follow, client.World.Story.Crew.Of(P(5)).Order);
            // At checkpoint 5 (the Wache) it went to its wait place by the valve the sneaking way and waits there (CompanionBot.Stealth).
            var waits = client.World.Story.Stealth.Bots.TryGetValue(5, out var sneak) && sneak.Goal is not null && client.World.HeroOf(P(5))!.Order is StandGroundOrder;
            Assert.True(waits || (client.World.HeroOf(P(5))!.Order is FollowOrder { Target: var target } ? target == client.World.HeroOf(P(0))!.Id : client.World.HeroOf(P(5))!.Order is MoveOrder),
                $"Bob's order {client.World.HeroOf(P(5))!.Order}");
        }
        // Only the host's game keeps the progress.
        Assert.Equal([true, false], clients.Select(c => StoryCoop.KeepsProgress(c.World.Players, c.World.Story!.Options, c.Me)));
        Assert.True(RelayClients.AssertAgree(clients) >= 400 / 20);
        RelayClients.AssertPlaysBack(anna);
    }

    [Fact]
    public async Task A_late_joiner_takes_bobs_hero_over_and_a_leaver_hands_it_back_to_the_computer_and_comes_back_by_key()
    {
        await using var relay = await RelayClients.Start();
        var (room, clients, _) = await Started(relay, ["Host", "Anna"]);
        var host = clients[0];
        await RelayClients.PlayTo(200, clients);
        var bob = host.World.HeroOf(P(5))!.Id;

        // The lobby counts Bob's seat to join; Zoe takes it, and her world is the bot's as yet.
        var lou = await relay.Connect("Lou", web: false);
        lou.Connection.Send(new ListRooms());
        var listed = (await lou.Next<RoomList>()).Rooms.Single(r => r.Id == room.Id);
        Assert.Equal((RoomSummary.Story, true, 1), (listed.Mode, listed.Started, listed.Joinable));
        var zoe = await relay.LateJoin("Zoe", web: true, room.Id);
        Assert.Equal((P(5), Race.Red, ResumeReason.LateJoin), (zoe.Me, zoe.Race, zoe.Resumed!.Reason));
        Assert.Contains(relay.Lines, l => l.StartsWith($"room {room.Id}: late join of Zoe (player 5) from tick ", StringComparison.Ordinal));
        zoe.Script = new HeroScript(zoe.Me, 905);
        clients.Add(zoe);
        await RelayClients.PlayTo(600, clients);

        // Every world turned Bob's row into Zoe's at one tick, and she drives his hero: the same actor, no companion now.
        var joined = Assert.Single(host.Session.Presence.OfType<PlayerJoined>());
        Assert.Equal((P(5), "Zoe"), (joined.Player, joined.Name));
        foreach (var client in clients)
        {
            Assert.Equal(new PlayerSeat(P(5), Race.Red, PlayerKind.Human, "Zoe"), client.World.Players[P(5)]);
            Assert.Equal(bob, client.World.HeroOf(P(5))!.Id);
            Assert.Empty(client.World.Story!.Bots);
            Assert.Equal("Zoe", client.World.StorySeatName(P(5)));
        }
        Assert.Contains(host.Session.Played, t => t.Player == P(5) && t.Tick >= joined.FromTick && t.Commands.Count > 0);
        Assert.Equal("Una: no seat to join in that story", (await Assert.ThrowsAsync<InvalidOperationException>(() => relay.LateJoin("Una", web: false, room.Id))).Message);

        // Anna quits: the computer takes Ike over in every world, under the host's name for him; her seat stays hers.
        await clients[1].Leave();
        clients.RemoveAt(1);
        await RelayClients.PlayTo(800, clients);
        Assert.Equal(SeatState.Gone, relay.Relay.WithBeat(room.Id, beat => beat.Seat(P(4))!.State));
        foreach (var client in clients)
        {
            Assert.Equal([P(4)], client.World.Story!.Bots);
            // The host's scripted Locator drives at whatever is near and may be gone by now (it failed the 0.8.0 tag's first CI
            // run); while it stands, Ike follows it.
            if (client.World.HeroOf(P(0)) is { } locator)
                Assert.Equal(new FollowOrder(locator.Id), client.World.HeroOf(P(4))!.Order);
            Assert.Equal("Rust", client.World.StorySeatName(P(4)));
        }

        // By name she is back as herself, and drives Ike again.
        var back = await relay.Rejoin("Anna", web: false, room.Id);
        Assert.Equal((P(4), ResumeReason.Rejoin), (back.Me, back.Resumed!.Reason));
        back.Script = new HeroScript(back.Me, 904);
        clients.Add(back);
        await RelayClients.PlayTo(1100, clients);
        foreach (var client in clients.Where(c => c.InMatch))
        {
            Assert.Empty(client.World.Story!.Bots);
            Assert.Equal("Anna", client.World.StorySeatName(P(4)));
        }
        Assert.Equal([typeof(PlayerJoined), typeof(PlayerLeft), typeof(PlayerReturned)], host.Session.Presence.Select(m => m.GetType()));
        Assert.True(RelayClients.AssertAgree(clients) >= 1100 / 20);
        RelayClients.AssertPlaysBack(zoe);
        RelayClients.AssertPlaysBack(back);
    }

    /// <summary>The next refusal a client is sent, past whatever else comes first.</summary>
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
