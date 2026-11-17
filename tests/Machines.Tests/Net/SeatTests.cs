using System.Net;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// Rooms whose players are apart from races, through the real relay and without worlds: seats a race shares, the order
/// people take them, names and colours, moving seats, what a room cannot be, and the players a match numbers from them,
/// whose turns and checksums are each their own.
/// </summary>
public class SeatTests
{
    private static async Task<Refused> Refusal(ChatTests.Client client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
            if (await client.Connection.Inbox.ReadAsync(timeout.Token) is Refused refused)
                return refused;
    }

    /// <summary>Opens a room on Canyon Attack for two commanders a side, Red and Blue.</summary>
    private static async Task<int> OpenTogether(ChatTests.Client host)
    {
        host.Connection.Send(new CreateRoom("Together", "m_desert", "m_desert", [Race.Blue, Race.Red], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2));
        return (await host.Next<RoomState>(s => s.Players.Any(p => p.ClientId == host.Id))).Id;
    }

    private static Race Colour(RoomState state, ChatTests.Client client) => state.Players.Single(p => p.ClientId == client.Id).Colour;

    /// <summary>Everyone in the room says they are ready in the colour they show, and the host starts.</summary>
    private static async Task<MatchInfo> StartAll(ChatTests.Client host, params ChatTests.Client[] guests)
    {
        var state = await host.Next<RoomState>(s => s.Players.Count == guests.Length + 1);
        foreach (var client in guests.Prepend(host))
        {
            client.Connection.Send(new SetPlayer(Colour(state, client), Ready: true));
            state = await host.Next<RoomState>(s => s.Players.Single(p => p.ClientId == client.Id).Ready);
        }
        host.Connection.Send(new StartMatch());
        return (await host.Next<MatchStarted>()).Match;
    }

    /// <summary>The relay has handled everything this client sent so far: it answers in the order it was asked.</summary>
    private static async Task Handled(ChatTests.Client client)
    {
        client.Connection.Send(new ListRooms());
        await client.Next<RoomList>();
    }

    [Fact]
    public async Task A_room_for_two_commanders_a_side_fills_every_race_once_before_any_twice()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        await using var cleo = await ChatTests.Client.Connect(relay, "Cleo");
        await using var dan = await ChatTests.Client.Connect(relay, "Dan");
        var room = await OpenTogether(anna);

        foreach (var client in new[] { ben, cleo, dan })
            await client.Join(room);

        var state = await anna.Next<RoomState>(s => s.Players.Count == 4);
        Assert.Equal(2, state.SeatsPerSlot);
        Assert.Equal([Race.Red, Race.Red, Race.Blue, Race.Blue], state.Seats.Select(s => s.Race));
        Assert.Equal([anna.Id, cleo.Id, ben.Id, dan.Id], state.Seats.Select(s => s.ClientId!.Value));
        // A teammate shows the race's colour; the races show two.
        Assert.Equal(Colour(state, anna), Colour(state, cleo));
        Assert.Equal(Colour(state, ben), Colour(state, dan));
        Assert.NotEqual(Colour(state, anna), Colour(state, ben));
        ben.Connection.Send(new ListRooms());
        Assert.Equal(4, (await ben.Next<RoomList>()).Rooms.Single().MaxPlayers);
    }

    [Fact]
    public async Task Players_of_one_name_are_told_apart_in_the_room_and_in_what_they_say()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var first = await ChatTests.Client.Connect(relay, "Anna");
        await using var second = await ChatTests.Client.Connect(relay, "Anna");
        await using var third = await ChatTests.Client.Connect(relay, "Anna");
        var room = await first.Open("Annas", slots: 4);
        await second.Join(room);
        await third.Join(room);

        var state = await first.Next<RoomState>(s => s.Players.Count == 3);
        Assert.Equal(["Anna", "Anna 2", "Anna 3"], state.Players.Select(p => p.Name));
        second.Connection.Send(new Chat("Anna", "Hello"));
        Assert.Equal(new Chat("Anna 2", "Hello"), await first.Next<Chat>());

        // A name given up is free again.
        second.Connection.Send(new LeaveRoom());
        await first.Next<RoomState>(s => s.Players.Count == 2);
        await using var fourth = await ChatTests.Client.Connect(relay, "Anna");
        await fourth.Join(room);
        state = await first.Next<RoomState>(s => s.Players.Count == 3);
        Assert.Equal("Anna 2", state.Players.Single(p => p.ClientId == fourth.Id).Name);
    }

    [Fact]
    public async Task A_colour_is_the_whole_races_and_no_other_race_may_show_it()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        await using var cleo = await ChatTests.Client.Connect(relay, "Cleo");
        var room = await OpenTogether(anna);
        await ben.Join(room);
        await cleo.Join(room);
        await anna.Next<RoomState>(s => s.Players.Count == 3);

        cleo.Connection.Send(new SetPlayer(Race.Green, Ready: false));
        var state = await anna.Next<RoomState>(s => Colour(s, cleo) == Race.Green);
        Assert.Equal(Race.Green, Colour(state, anna));

        ben.Connection.Send(new SetPlayer(Race.Green, Ready: true));
        Assert.Equal("Green is taken", (await Refusal(ben)).Reason);
        ben.Connection.Send(new SetPlayer(Race.Yellow, Ready: true));
        state = await anna.Next<RoomState>(s => Colour(s, ben) == Race.Yellow);
        Assert.Equal(Race.Green, Colour(state, cleo));
    }

    [Fact]
    public async Task A_player_moves_to_a_free_open_seat_only_and_takes_its_races_colour()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await OpenTogether(anna);
        await ben.Join(room);
        var state = await anna.Next<RoomState>(s => s.Players.Count == 2);
        Assert.Equal(ben.Id, state.Seats[2].ClientId);

        ben.Connection.Send(new TakeSeat(1));
        state = await anna.Next<RoomState>(s => s.Seats[1].ClientId == ben.Id);
        Assert.Null(state.Seats[2].ClientId);
        Assert.Equal(Colour(state, anna), Colour(state, ben));

        ben.Connection.Send(new TakeSeat(0));
        Assert.Equal("that seat is not free", (await Refusal(ben)).Reason);
        ben.Connection.Send(new TakeSeat(4));
        Assert.Equal("no such seat", (await Refusal(ben)).Reason);
        anna.Connection.Send(new SetSeat(3, SeatUse.Closed));
        await anna.Next<RoomState>(s => s.Seats[3].Use == SeatUse.Closed);
        ben.Connection.Send(new TakeSeat(3));
        Assert.Equal("that seat is not free", (await Refusal(ben)).Reason);

        // Back at Blue alone, Ben shows a colour Red does not.
        ben.Connection.Send(new TakeSeat(2));
        state = await anna.Next<RoomState>(s => s.Seats[2].ClientId == ben.Id);
        Assert.NotEqual(Colour(state, anna), Colour(state, ben));
    }

    [Fact]
    public async Task The_relay_refuses_a_room_it_cannot_seat()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        List<Race> two = [Race.Red, Race.Blue];
        MatchOptions Sized(int size) => new() { Battle = new BattleOptions { Tier = 3, TeamSize = size } };

        foreach (var (create, why) in new[]
                 {
                     (new CreateRoom("r", "m_desert", "m_desert", two, new MatchOptions(), false, SeatsPerSlot: 3), "two commanders per side at most"),
                     (new CreateRoom("r", "m_desert", "m_desert", two, new MatchOptions(), false, SeatsPerSlot: 0), "every side needs a seat"),
                     (new CreateRoom("r", "m_desert", "m_desert", [Race.Red, (Race)7], new MatchOptions(), false), "no such start position"),
                     (new CreateRoom("r", "m_desert", "m_desert", two, Sized(BattleBalance.MostTeamSize + 1), false), "a team has at most 5 heroes"),
                     (new CreateRoom("r", "m_desert", "m_desert", two, Sized(-1), false), "no such team size"),
                 })
        {
            anna.Connection.Send(create);
            Assert.Equal(why, (await Refusal(anna)).Reason);
        }

        // Nor may a room become one.
        var room = await anna.Open("Room", slots: 2);
        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", two, new MatchOptions(), false, SeatsPerSlot: 3));
        Assert.Equal("two commanders per side at most", (await Refusal(anna)).Reason);
        anna.Connection.Send(new ListRooms());
        Assert.Equal(room, (await anna.Next<RoomList>()).Rooms.Single().Id);
        Assert.Equal(1, (await anna.Next<RoomState>()).SeatsPerSlot);
    }

    [Fact]
    public async Task Two_commanders_a_side_play_as_players_of_their_own_and_send_only_their_own_turns()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        await using var cleo = await ChatTests.Client.Connect(relay, "Cleo");
        await using var dan = await ChatTests.Client.Connect(relay, "Dan");
        var room = await OpenTogether(anna);
        foreach (var client in new[] { ben, cleo, dan })
            await client.Join(room);

        var match = await StartAll(anna, ben, cleo, dan);

        // A row for each person in seat order, numbered race by race: each race's first its own number, the second from 4 on.
        Assert.Equal(
            [(anna.Id, Race.Red, 0), (cleo.Id, Race.Red, 4), (ben.Id, Race.Blue, 1), (dan.Id, Race.Blue, 5)],
            match.Players.Select(p => (p.ClientId, p.Slot, p.Player!.Value.Value)));
        Assert.All(match.Players, p => Assert.False(p.Ai));
        Assert.Equal(match.Players[0].Colour, match.Players[1].Colour);
        var table = match.Table();
        Assert.Equal([new PlayerId(0), new PlayerId(1), new PlayerId(4), new PlayerId(5)], table.Humans);
        Assert.Equal(Race.Red, table.RaceOf(new PlayerId(4)));

        // Cleo may not send Anna's turn; her own reaches everyone, her first.
        cleo.Connection.Send(new Turn(match.InputDelay, new PlayerId(0), []));
        cleo.Connection.Send(new Turn(match.InputDelay, new PlayerId(4), []));
        Assert.Equal(new PlayerId(4), (await anna.Next<Turn>()).Player);
        Assert.Equal(new PlayerId(4), (await dan.Next<Turn>()).Player);

        // Every person reports their own checksum, and all four must agree: a report in another's name counts for nothing,
        // and a teammate whose world went its own way is a desync.
        var everyone = new[] { (anna, 0), (ben, 1), (cleo, 4), (dan, 5) };
        foreach (var (client, player) in everyone)
            client.Connection.Send(new ChecksumReport(20, new PlayerId(player), 7));
        foreach (var (client, player) in everyone.Where(e => e.Item1 != dan))
            client.Connection.Send(new ChecksumReport(40, new PlayerId(player), 3));
        cleo.Connection.Send(new ChecksumReport(40, new PlayerId(5), 99));
        foreach (var (client, _) in everyone)
            await Handled(client);
        foreach (var (client, player) in everyone)
            client.Connection.Send(new ChecksumReport(60, new PlayerId(player), client == cleo ? 8u : 9u));

        Assert.Equal(60, (await ben.Next<Desync>()).Tick);
    }

    [Fact]
    public async Task A_player_who_left_comes_back_as_the_one_they_name_else_the_first_of_their_name()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        var first = await ChatTests.Client.Connect(relay, "Kim");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var second = await ChatTests.Client.Connect(relay, "Kim");
        var room = await OpenTogether(first);
        await ben.Join(room);
        await second.Join(room);
        var match = await StartAll(first, ben, second);
        Assert.Equal([("Kim", 0), ("Kim 2", 4), ("Ben", 1)], match.Players.Select(p => (p.Name, p.Player!.Value.Value)));

        await first.DisposeAsync();
        await second.DisposeAsync();
        var left = new[] { await ben.Next<PlayerLeft>(), await ben.Next<PlayerLeft>() };
        Assert.Equal([new PlayerId(0), new PlayerId(4)], left.Select(l => l.Player).Order());

        // Ben is still there, and not Kim: nobody of that name left as him.
        await using var named = await ChatTests.Client.Connect(relay, "Kim");
        named.Connection.Send(new RejoinMatch(room, new PlayerId(1)));
        Assert.Equal("no player called Kim left that match", (await Refusal(named)).Reason);
        named.Connection.Send(new RejoinMatch(room, new PlayerId(4)));
        var resumed = await named.Next<MatchResumed>();
        Assert.Equal(new PlayerId(4), resumed.Player);
        Assert.Equal(new PlayerId(4), (await ben.Next<PlayerReturned>()).Player);

        await using var unnamed = await ChatTests.Client.Connect(relay, "Kim");
        unnamed.Connection.Send(new RejoinMatch(room));
        Assert.Equal(new PlayerId(0), (await unnamed.Next<MatchResumed>()).Player);
        Assert.Equal(new PlayerId(0), (await ben.Next<PlayerReturned>()).Player);

        // Back, each sends their own turns again.
        named.Connection.Send(new Turn(resumed.FromTick, new PlayerId(4), []));
        Assert.Equal(new PlayerId(4), (await ben.Next<Turn>(t => t.Tick >= resumed.FromTick)).Player);
    }

    [Fact]
    public async Task Five_a_side_seats_ten_numbered_players_and_the_relay_names_the_player_of_each_hero()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 3, TeamSize = 5 }, FogOfWar = false };
        anna.Connection.Send(new CreateRoom("Ten", "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green, Race.Yellow], options, FillWithAi: false));
        var room = (await anna.Next<RoomState>(s => s.Players.Any(p => p.ClientId == anna.Id))).Id;
        await ben.Join(room);

        var state = await anna.Next<RoomState>(s => s.Players.Count == 2);
        // The map's two anchors, five seats each; the second person sits at the other team's first seat.
        Assert.Equal([Race.Red, Race.Green], state.Slots);
        Assert.Equal([.. Enumerable.Repeat(Race.Red, 5), .. Enumerable.Repeat(Race.Green, 5)], state.Seats.Select(s => s.Race));
        Assert.Equal((anna.Id, ben.Id), (state.Seats[0].ClientId, state.Seats[5].ClientId));

        // Ben's hero says it is Red's first player; the seat says otherwise.
        var bens = BattleNetTests.Brought("Ben's hero", 3, line: 1) with { Race = Race.Red, Player = new PlayerId(0) };
        ben.Connection.Send(new SetPlayer(Colour(state, ben), Ready: true, Hero: bens));
        await anna.Next<RoomState>(s => s.Players.Single(p => p.ClientId == ben.Id).Ready);
        anna.Connection.Send(new SetPlayer(Colour(state, anna), Ready: true, Hero: BattleNetTests.Brought("Anna's hero", 3)));
        await anna.Next<RoomState>(s => s.Players.All(p => p.Ready));
        anna.Connection.Send(new StartMatch());
        var match = (await ben.Next<MatchStarted>()).Match;

        Assert.Equal(
            [(Race.Red, 0, false), (Race.Green, 2, false), (Race.Red, 4, true), (Race.Red, 5, true), (Race.Red, 6, true), (Race.Red, 7, true),
             (Race.Green, 8, true), (Race.Green, 9, true), (Race.Green, 10, true), (Race.Green, 11, true)],
            match.Players.Select(p => (p.Slot, p.Player!.Value.Value, p.Ai)));
        var battle = match.Options.Battle!;
        Assert.Equal(5, battle.TeamSize);
        Assert.Equal(match.Players.Select(p => p.Player).Order(), battle.Heroes.Select(h => h.Player));
        BattleHero Hero(int player) => battle.Heroes.Single(h => h.Player == new PlayerId(player));
        Assert.Equal(("Anna's hero", Race.Red, false), (Hero(0).Name, Hero(0).Race, Hero(0).Bot));
        Assert.Equal(("Ben's hero", Race.Green, false), (Hero(2).Name, Hero(2).Race, Hero(2).Bot));
        foreach (var bot in match.Players.Where(p => p.Ai))
            Assert.Equal((bot.Name, bot.Slot, true), (Hero(bot.Player!.Value.Value).Name, Hero(bot.Player!.Value.Value).Race, Hero(bot.Player!.Value.Value).Bot));
        var table = match.Table();
        Assert.Equal([new PlayerId(0), new PlayerId(2)], table.Humans);
        Assert.Equal(8, table.Rows.Count(r => r.Kind == PlayerKind.Bot));
    }
}
