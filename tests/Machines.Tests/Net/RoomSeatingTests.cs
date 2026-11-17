using System.Net;
using Machines.Game;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>
/// The room's list of seats (<see cref="RoomSeating"/>) over rooms as the real relay sends them, set up as the settings page
/// sets them up (<see cref="RoomOptions"/>): five against five in two teams of five numbered seats, two commanders a side
/// as a line for each race, today's rooms as before; what the host may make of a seat, and which seat a player may take.
/// </summary>
public class RoomSeatingTests
{
    private static readonly Race[] Four = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    /// <summary>The page's battle room on Canyon Attack, at tier II with two lives, of a team size.</summary>
    private static CreateRoom BattleRoom(int teamSize) =>
        new("Five", "m_desert", "m_desert", Four, RoomOptions.Battle(new MatchOptions(), 2, 2, BattleWin.Both, teamSize), FillWithAi: true);

    private static (Race Race, int Number, int? ClientId)[] Seats(SeatGroup group) =>
        [.. group.Seats.Select(s => (s.Race, s.Number, s.Player?.ClientId))];

    [Fact]
    public async Task Five_against_five_lists_two_teams_of_five_numbered_seats_each_of_its_teams_first_race()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        anna.Connection.Send(BattleRoom(5));
        var room = (await anna.Next<RoomState>(s => s.Players.Count == 1)).Id;
        await ben.Join(room);

        var state = await anna.Next<RoomState>(s => s.Players.Count == 2);

        Assert.Equal(10, state.Seats.Count);
        Assert.True(RoomSeating.Numbered(state));
        var groups = RoomSeating.Groups(state);
        Assert.Equal([0, 1], groups.Select(g => g.Team));
        Assert.Equal([[Race.Red], [Race.Green]], groups.Select(g => g.Races.ToArray()));
        // The first seat of each team is taken first: Anna's and Ben's.
        Assert.Equal([(Race.Red, 1, anna.Id), (Race.Red, 2, null), (Race.Red, 3, null), (Race.Red, 4, null), (Race.Red, 5, null)], Seats(groups[0]));
        Assert.Equal([(Race.Green, 1, ben.Id), (Race.Green, 2, null), (Race.Green, 3, null), (Race.Green, 4, null), (Race.Green, 5, null)], Seats(groups[1]));
        // The seats' places in the room are what TakeSeat and SetSeat name.
        Assert.Equal(Enumerable.Range(0, 10), groups.SelectMany(g => g.Seats).Select(s => s.Index));
        // A battle has a hero at every seat: no seat is closed.
        Assert.All(Enumerable.Range(0, 10), i => Assert.Equal([SeatUse.Open, SeatUse.Computer], RoomSeating.Uses(state, i)));
        Assert.False(RoomSeating.MayTake(state, ben.Id, 0));
        Assert.False(RoomSeating.MayTake(state, ben.Id, 5));
        Assert.True(RoomSeating.MayTake(state, ben.Id, 1));
        Assert.False(RoomSeating.MayTake(state, 9999, 1));

        // Ben takes the second seat of Anna's team, and plays beside her.
        ben.Connection.Send(new TakeSeat(groups[0].Seats[1].Index));
        state = await anna.Next<RoomState>(s => s.Seats[1].ClientId == ben.Id);
        groups = RoomSeating.Groups(state);
        Assert.Equal([anna.Id, ben.Id], groups[0].Seats.Where(s => s.Player is not null).Select(s => s.Player!.ClientId));
        Assert.All(groups[1].Seats, s => Assert.True(s.Free));
        Assert.Equal(state.Players.Single(p => p.ClientId == anna.Id).Colour, state.Players.Single(p => p.ClientId == ben.Id).Colour);
    }

    [Fact]
    public async Task Two_commanders_a_side_list_each_race_with_its_two_seats_and_the_computer_only_at_the_first()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        anna.Connection.Send(new CreateRoom("Together", "m_desert", "m_desert", [Race.Blue, Race.Red], new MatchOptions(), FillWithAi: false,
            SeatsPerSlot: RoomOptions.CommandersPerSide[1]));
        var room = (await anna.Next<RoomState>(s => s.Players.Count == 1)).Id;
        await ben.Join(room);

        var state = await anna.Next<RoomState>(s => s.Players.Count == 2);

        Assert.True(RoomSeating.Numbered(state));
        var groups = RoomSeating.Groups(state);
        Assert.All(groups, g => Assert.Equal(-1, g.Team));
        Assert.Equal([(Race.Red, 1, anna.Id), (Race.Red, 2, null)], Seats(groups[0]));
        // Every side's first seat is taken before any second: Ben sits at Blue's.
        Assert.Equal([(Race.Blue, 1, ben.Id), (Race.Blue, 2, null)], Seats(groups[1]));
        Assert.Equal([SeatUse.Open, SeatUse.Closed, SeatUse.Computer], RoomSeating.Uses(state, 0));
        Assert.Equal([SeatUse.Open, SeatUse.Closed], RoomSeating.Uses(state, 1));
        Assert.Equal([SeatUse.Open, SeatUse.Closed, SeatUse.Computer], RoomSeating.Uses(state, 2));
        Assert.Equal([SeatUse.Open, SeatUse.Closed], RoomSeating.Uses(state, 3));
        Assert.Empty(RoomSeating.Uses(state, 4));

        // Ben moves beside Anna: Red has two commanders, Blue none, and Ben shows Red's colour.
        Assert.True(RoomSeating.MayTake(state, ben.Id, 1));
        ben.Connection.Send(new TakeSeat(1));
        state = await anna.Next<RoomState>(s => s.Seats[1].ClientId == ben.Id);
        groups = RoomSeating.Groups(state);
        Assert.Equal([(Race.Red, 1, anna.Id), (Race.Red, 2, ben.Id)], Seats(groups[0]));
        Assert.Equal([(Race.Blue, 1, null), (Race.Blue, 2, null)], Seats(groups[1]));
        Assert.Equal(state.Players.Single(p => p.ClientId == anna.Id).Colour, state.Players.Single(p => p.ClientId == ben.Id).Colour);

        // The host closes Blue's second seat: nobody may take it; the first stays free.
        anna.Connection.Send(new SetSeat(3, SeatUse.Closed));
        state = await anna.Next<RoomState>(s => s.Seats[3].Use == SeatUse.Closed);
        Assert.False(RoomSeating.MayTake(state, ben.Id, 3));
        Assert.True(RoomSeating.MayTake(state, ben.Id, 2));
        Assert.False(RoomSeating.Lines(state)[3].Free);
    }

    [Fact]
    public async Task Todays_rooms_list_their_seats_unnumbered_a_battle_by_its_teams_races()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        anna.Connection.Send(BattleRoom(0));
        var battle = await anna.Next<RoomState>(s => s.Players.Count == 1);
        ben.Connection.Send(new CreateRoom("Game", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: true));
        var normal = await ben.Next<RoomState>(s => s.Players.Count == 1);

        Assert.False(RoomSeating.Numbered(battle));
        var teams = RoomSeating.Groups(battle);
        var map = BattleCatalogue.Map("m_desert")!;
        Assert.Equal([map.TeamA.Order().ToArray(), map.TeamB.Order().ToArray()], teams.Select(g => g.Races.ToArray()));
        Assert.Equal([[(Race.Red, 1, anna.Id), (Race.Blue, 1, null)], [(Race.Green, 1, null), (Race.Yellow, 1, null)]], teams.Select(Seats));

        Assert.False(RoomSeating.Numbered(normal));
        Assert.Equal([[(Race.Red, 1, ben.Id)], [(Race.Blue, 1, null)]], RoomSeating.Groups(normal).Select(Seats));
        Assert.Equal([SeatUse.Open, SeatUse.Closed, SeatUse.Computer], RoomSeating.Uses(normal, 1));
    }

    [Fact]
    public async Task A_side_the_host_gave_the_computer_or_closed_keeps_it_when_the_room_takes_a_second_commander_a_side()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        anna.Connection.Send(new CreateRoom("Room", "m_desert", "m_desert", Four, new MatchOptions(), FillWithAi: false));
        var room = (await anna.Next<RoomState>(s => s.Players.Count == 1)).Id;
        anna.Connection.Send(new SetSeat(1, SeatUse.Computer));
        anna.Connection.Send(new SetSeat(2, SeatUse.Closed));
        await anna.Next<RoomState>(s => s.Seats[2].Use == SeatUse.Closed);

        // The settings page asks for two commanders a side: Blue stays the computer's and Green closed, every second seat
        // waits for a player.
        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", Four, new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2));
        var state = await anna.Next<RoomState>(s => s.Seats.Count == 8);
        Assert.Equal([
                (Race.Red, SeatUse.Open), (Race.Red, SeatUse.Open), (Race.Blue, SeatUse.Computer), (Race.Blue, SeatUse.Open),
                (Race.Green, SeatUse.Closed), (Race.Green, SeatUse.Open), (Race.Yellow, SeatUse.Open), (Race.Yellow, SeatUse.Open)],
            state.Seats.Select(s => (s.Race, s.Use)));
        Assert.Equal(anna.Id, state.Seats[0].ClientId);
        Assert.All(Enumerable.Range(0, 8), i => Assert.Contains(state.Seats[i].Use, RoomSeating.Choices(state, i)));

        // Ben takes the first seat free for a player, Yellow's; the computer plays Blue and nobody Green.
        await ben.Join(room);
        state = await anna.Next<RoomState>(s => s.Players.Count == 2);
        Assert.Equal(ben.Id, state.Seats[6].ClientId);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true));
        ben.Connection.Send(new SetPlayer(Race.Yellow, Ready: true));
        await anna.Next<RoomState>(s => s.Players.All(p => p.Ready));
        anna.Connection.Send(new StartMatch());
        var match = (await anna.Next<MatchStarted>()).Match;
        Assert.Equal([(Race.Red, false), (Race.Yellow, false), (Race.Blue, true)], match.Players.Select(p => (p.Slot, p.Ai)));
    }

    [Fact]
    public async Task A_battle_seat_the_host_gave_the_computer_stays_the_computers_when_the_teams_grow_to_five()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        anna.Connection.Send(BattleRoom(0));
        await anna.Next<RoomState>(s => s.Players.Count == 1);
        // Green's hero, the second team's first, is the computer's: nobody is to take it.
        anna.Connection.Send(new SetSeat(2, SeatUse.Computer));
        await anna.Next<RoomState>(s => s.Seats[2].Use == SeatUse.Computer);

        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", Four, RoomOptions.Battle(new MatchOptions(), 2, 2, BattleWin.Both, 5), FillWithAi: true));
        var state = await anna.Next<RoomState>(s => s.Seats.Count == 10);

        Assert.Equal([(Race.Red, SeatUse.Open), (Race.Green, SeatUse.Computer)], state.Seats.Where((_, i) => i % 5 == 0).Select(s => (s.Race, s.Use)));
        Assert.All(state.Seats.Where((_, i) => i % 5 != 0), s => Assert.Equal(SeatUse.Open, s.Use));
    }

    [Fact]
    public void The_hosts_choice_for_a_seat_shows_what_it_is_for_even_where_that_is_not_offered_there()
    {
        // A computer at a side's second seat (left there by a change from a battle room, say) shows as the computer's, and
        // the host may make it open or closed.
        var state = new RoomState(1, "Room", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), false, 7,
            [new LobbyPlayer(7, "Anna", Race.Red, false)],
            [new RoomSeat(7, SeatUse.Open, Race.Red), new RoomSeat(null, SeatUse.Computer, Race.Red),
             new RoomSeat(null, SeatUse.Open, Race.Blue), new RoomSeat(null, SeatUse.Closed, Race.Blue)], 2);

        Assert.Equal([SeatUse.Open, SeatUse.Closed], RoomSeating.Uses(state, 1));
        Assert.Equal([SeatUse.Open, SeatUse.Closed, SeatUse.Computer], RoomSeating.Choices(state, 1));
        Assert.Equal([SeatUse.Open, SeatUse.Closed, SeatUse.Computer], RoomSeating.Choices(state, 2));
        Assert.Equal([SeatUse.Open, SeatUse.Closed], RoomSeating.Choices(state, 3));
        Assert.Empty(RoomSeating.Choices(state, 4));
    }
}
