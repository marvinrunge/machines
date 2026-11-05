using System.Net;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>A room through the real relay: what its host says of each start position, and of the room's map and settings.</summary>
public class RoomTests
{
    private static readonly List<Race> Three = [Race.Red, Race.Green, Race.Blue];

    [Fact]
    public async Task A_room_has_a_seat_for_each_start_position_and_players_take_them_in_turn()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await anna.Open("Room", slots: 3);

        await ben.Join(room);

        var state = await anna.Next<RoomState>(s => s.Players.Count == 2);
        Assert.Equal([new RoomSeat(anna.Id, SeatUse.Open), new RoomSeat(ben.Id, SeatUse.Open), new RoomSeat(null, SeatUse.Open)], state.Seats);
    }

    [Fact]
    public async Task A_closed_seat_lets_nobody_in()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await anna.Open("Room", slots: 2);

        anna.Connection.Send(new SetSeat(1, SeatUse.Closed));
        await anna.Next<RoomState>(s => s.Seats[1].Use == SeatUse.Closed);
        ben.Connection.Send(new ListRooms());
        var list = await ben.Next<RoomList>();
        ben.Connection.Send(new JoinRoom(room));

        Assert.Equal(1, list.Rooms.Single().MaxPlayers);
        Assert.Equal("that room is full", (await Refusal(ben)).Reason);
    }

    [Fact]
    public async Task The_host_takes_a_player_out_and_the_seat_is_what_the_host_said()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await anna.Open("Room", slots: 2);
        await ben.Join(room);

        anna.Connection.Send(new SetSeat(1, SeatUse.Computer));

        var state = await anna.Next<RoomState>(s => s.Players.Count == 1);
        Assert.Equal(new RoomSeat(null, SeatUse.Computer), state.Seats[1]);
        await ben.Next<RoomState>(s => s.Players.All(p => p.ClientId != ben.Id));
        Assert.Equal("The host took you out of the room.", (await Refusal(ben)).Reason);
    }

    [Fact]
    public async Task Nobody_else_changes_a_seat_or_the_room_and_the_host_cannot_take_themself_out()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await anna.Open("Room", slots: 3);
        await ben.Join(room);

        ben.Connection.Send(new SetSeat(0, SeatUse.Closed));
        Assert.Equal("only the host changes the seats", (await Refusal(ben)).Reason);
        ben.Connection.Send(new ChangeRoom("m_desert", "m_desert", Three, new MatchOptions(), FillWithAi: false));
        Assert.Equal("only the host changes the room", (await Refusal(ben)).Reason);

        anna.Connection.Send(new SetSeat(0, SeatUse.Closed));
        anna.Connection.Send(new SetSeat(2, SeatUse.Closed));
        var state = await anna.Next<RoomState>(s => s.Seats[2].Use == SeatUse.Closed);
        Assert.Equal(new RoomSeat(anna.Id, SeatUse.Open), state.Seats[0]);
    }

    [Fact]
    public async Task A_new_map_or_settings_make_everyone_get_ready_again()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await anna.Open("Room", slots: 2);
        await ben.Join(room);
        ben.Connection.Send(new SetPlayer(Race.Blue, Ready: true));
        await anna.Next<RoomState>(s => s.Players.Any(p => p.Ready));

        var options = new MatchOptions { FogOfWar = false, BroadcastAlliances = true, DisableFirstPerson = true };
        anna.Connection.Send(new ChangeRoom("m_other", "m_world", Three, options, FillWithAi: true));

        var state = await ben.Next<RoomState>(s => s.Scenario == "m_other");
        Assert.Equal("m_world", state.Planet);
        Assert.Equal(options, state.Options);
        Assert.True(state.FillWithAi);
        Assert.All(state.Players, p => Assert.False(p.Ready));
        Assert.Equal([new RoomSeat(anna.Id, SeatUse.Open), new RoomSeat(ben.Id, SeatUse.Open), new RoomSeat(null, SeatUse.Open)], state.Seats);
    }

    [Fact]
    public async Task A_smaller_map_takes_out_those_it_has_no_seat_for_and_keeps_the_host()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        await using var cleo = await ChatTests.Client.Connect(relay, "Cleo");
        var room = await anna.Open("Room", slots: 3);
        await ben.Join(room);
        await cleo.Join(room);

        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false));

        var state = await anna.Next<RoomState>(s => s.Seats.Count == 2 && s.Players.Count == 2);
        Assert.Equal([anna.Id, ben.Id], state.Seats.Select(s => s.ClientId!.Value));
        Assert.Equal("The host took you out of the room.", (await Refusal(cleo)).Reason);
    }

    [Fact]
    public async Task At_the_start_a_computer_seat_plays_the_ai_and_a_closed_one_nobody()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var slots = new List<Race> { Race.Red, Race.Green, Race.Blue, Race.Yellow }.Order().ToList();
        anna.Connection.Send(new CreateRoom("Room", "m_desert", "m_desert", slots, new MatchOptions(), FillWithAi: false));
        var room = (await anna.Next<RoomState>(s => s.Players.Any(p => p.ClientId == anna.Id))).Id;
        anna.Connection.Send(new SetSeat(1, SeatUse.Computer));
        anna.Connection.Send(new SetSeat(2, SeatUse.Closed));
        await anna.Next<RoomState>(s => s.Seats[2].Use == SeatUse.Closed);
        await ben.Join(room);
        ben.Connection.Send(new SetPlayer(Race.Blue, Ready: true));
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true));
        await anna.Next<RoomState>(s => s.Players.All(p => p.Ready));

        anna.Connection.Send(new StartMatch());

        var match = (await anna.Next<MatchStarted>()).Match;
        Assert.Equal(3, match.Players.Count);
        Assert.Equal(anna.Id, match.Players.Single(p => p.Slot == slots[0]).ClientId);
        Assert.True(match.Players.Single(p => p.Slot == slots[1]).Ai);
        Assert.DoesNotContain(match.Players, p => p.Slot == slots[2]);
        Assert.Equal(ben.Id, match.Players.Single(p => p.Slot == slots[3]).ClientId);
    }

    [Fact]
    public async Task Open_seats_play_the_ai_at_the_start_only_when_the_room_says_so()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        anna.Connection.Send(new CreateRoom("Room", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: true));
        await anna.Next<RoomState>(s => s.Players.Count == 1);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true));
        await anna.Next<RoomState>(s => s.Players.All(p => p.Ready));

        anna.Connection.Send(new StartMatch());

        var match = (await anna.Next<MatchStarted>()).Match;
        Assert.Contains(match.Players, p => p.Ai);
    }

    [Fact]
    public async Task Who_comes_back_to_the_rooms_while_in_one_finds_it_as_it_is()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        var room = await anna.Open("Room", slots: 2);

        anna.Connection.Send(new ListRooms());

        await anna.Next<RoomList>();
        Assert.Equal(room, (await anna.Next<RoomState>()).Id);
    }

    private static async Task<Refused> Refusal(ChatTests.Client client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
            if (await client.Connection.Inbox.ReadAsync(timeout.Token) is Refused refused)
                return refused;
    }
}
