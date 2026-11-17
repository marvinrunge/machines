using System.Net;
using System.Text.Json;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.Tests.Sim;

namespace Machines.Tests.Net;

/// <summary>
/// Matches as the relay starts them since protocol 13 (P1 slice S4a), played: what it adds to a match of one player a race
/// (players' numbers, the computer's rows named after their heroes, every person's hero naming the player of its seat)
/// moves no sum; a person at a later seat of a team plays that seat; two commanders of a race build one world; and a
/// change of the room's settings alone leaves every seat as it was.
/// </summary>
public class RelayShapeTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    /// <summary>
    /// A golden battle (<see cref="GoldenCase"/>, shaped as the relay started one before) as the relay starts it now
    /// (<c>RelayServer.StartBattle</c>): every row names its seat's player, the computer's rows carry their heroes' names, and
    /// the heroes are composed again from the people's, each naming the player of its seat.
    /// </summary>
    private static MatchInfo AsRelayedNow(MatchInfo golden)
    {
        var battle = golden.Options.Battle!;
        var brought = battle.Heroes.Where(h => !h.Bot).Select(h => h with { Player = PlayerId.Of(h.Race) }).ToList();
        var composed = BattleBots.Compose(battle with { Heroes = [] }, brought, golden.Scenario);
        var rows = golden.Players.Select(p => p with
        {
            Player = PlayerId.Of(p.Slot),
            Name = p.Ai ? composed.Heroes.Single(h => h.Race == p.Slot).Name : p.Name,
        }).ToList();
        return golden with { Options = golden.Options with { Battle = composed }, Players = rows };
    }

    public static TheoryData<string> CommandedBattles => [GoldenCase.Battle.Name, GoldenCase.ReturningBattle.Name];

    [Theory]
    [MemberData(nameof(CommandedBattles))]
    public void A_battle_of_today_s_layout_as_the_relay_starts_it_now_plays_as_before(string name)
    {
        var golden = GoldenCase.All.Single(c => c.Name == name);
        var now = AsRelayedNow(golden.Match);
        // What is new is only who the players are: the heroes are the same, the people's naming their seats' players.
        string Json(BattleHero hero) => JsonSerializer.Serialize(hero with { Player = null }, NetJson.Options);
        Assert.Equal(golden.Match.Options.Battle!.Heroes.Select(Json), now.Options.Battle!.Heroes.Select(Json));
        Assert.Equal(now.Options.Battle.Heroes.Where(h => !h.Bot).Select(h => (PlayerId?)PlayerId.Of(h.Race)), now.Options.Battle.Heroes.Where(h => !h.Bot).Select(h => h.Player));
        Assert.DoesNotContain(now.Players, p => p.Ai && p.Name == "AI");

        var before = new GoldenMatch(golden, Data);
        var after = new GoldenMatch(golden with { Match = now }, Data);
        for (var tick = 200; tick <= 4000; tick += 200)
            Assert.Equal(before.PlayTo(tick), after.PlayTo(tick));
    }

    private static Race Colour(RoomState state, ChatTests.Client client) => state.Players.Single(p => p.ClientId == client.Id).Colour;

    [Fact]
    public async Task A_person_at_a_later_seat_of_five_a_side_plays_that_seat_and_a_bot_their_team_s_first()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var options = new MatchOptions { Battle = new BattleOptions { Tier = 3, TeamSize = 5 }, FogOfWar = false };
        anna.Connection.Send(new CreateRoom("Ten", "m_desert", "m_desert", [Race.Red, Race.Green], options, FillWithAi: false));
        var room = (await anna.Next<RoomState>(s => s.Players.Any(p => p.ClientId == anna.Id))).Id;
        await ben.Join(room);
        await anna.Next<RoomState>(s => s.Players.Count == 2);

        // Anna moves from Red's first seat to its third, which is player 5.
        anna.Connection.Send(new TakeSeat(2));
        var state = await anna.Next<RoomState>(s => s.Seats[2].ClientId == anna.Id);
        Assert.Null(state.Seats[0].ClientId);
        foreach (var (client, hero) in new[] { (anna, "Anna's hero"), (ben, "Ben's hero") })
        {
            client.Connection.Send(new SetPlayer(Colour(state, client), Ready: true, Hero: BattleNetTests.Brought(hero, 3)));
            await anna.Next<RoomState>(s => s.Players.Single(p => p.ClientId == client.Id).Ready);
        }
        anna.Connection.Send(new StartMatch());
        var match = (await ben.Next<MatchStarted>()).Match;

        Assert.Equal([(anna.Id, Race.Red, 5), (ben.Id, Race.Green, 2)],
            match.Players.Where(p => !p.Ai).Select(p => (p.ClientId, p.Slot, p.Player!.Value.Value)));
        Assert.Contains(match.Players, p => p.Ai && p.Player == PlayerId.Of(Race.Red));
        Assert.Equal("Anna's hero", match.Options.Battle!.Heroes.Single(h => h.Player == new PlayerId(5)).Name);

        // The world Anna's game and every other builds: her hero at her seat, a bot at Red's first.
        var world = Replay.BuildWorld(Data, match);
        Assert.Equal([new PlayerId(2), new PlayerId(5)], world.Players.Humans);
        Assert.Equal(new PlayerId(5), world.HeroOf(new PlayerId(5))!.Owner);
        Assert.Contains(PlayerId.Of(Race.Red), world.HeroBots.Keys);
        Assert.DoesNotContain(new PlayerId(5), world.HeroBots.Keys);

        // Her turns are hers; once she has left, a bot drives her hero and Red's first stays the computer's.
        var session = new LockstepSession(world, new PlayerId(5), match.InputDelay);
        session.Receive(new PlayerLeft(new PlayerId(5), 40));
        for (var tick = 0; tick < 60; tick++)
        {
            session.Pump();
            session.Receive(new Turn(world.Tick, new PlayerId(2), []));
            Assert.True(session.TryStep(), $"stuck at tick {world.Tick}");
        }
        Assert.Contains(new PlayerId(5), world.HeroBots.Keys);
        Assert.Contains(PlayerId.Of(Race.Red), world.HeroBots.Keys);
        session.Pump();
        Assert.Equal([new PlayerId(2)], session.Waiting());
    }

    [Fact]
    public async Task Two_commanders_of_a_race_seated_by_the_relay_share_its_machines_in_one_world()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        await using var cleo = await ChatTests.Client.Connect(relay, "Cleo");
        anna.Connection.Send(new CreateRoom("Together", "m_desert", "m_desert", [Race.Red, Race.Blue], new MatchOptions(), FillWithAi: false, SeatsPerSlot: 2));
        var room = (await anna.Next<RoomState>(s => s.Players.Any(p => p.ClientId == anna.Id))).Id;
        await ben.Join(room);
        await cleo.Join(room);
        var state = await anna.Next<RoomState>(s => s.Players.Count == 3);
        foreach (var client in new[] { anna, ben, cleo })
        {
            client.Connection.Send(new SetPlayer(Colour(state, client), Ready: true));
            state = await anna.Next<RoomState>(s => s.Players.Single(p => p.ClientId == client.Id).Ready);
        }
        anna.Connection.Send(new StartMatch());
        var match = (await cleo.Next<MatchStarted>()).Match;

        // Anna and Cleo are Red (0 and 4), Ben Blue; Blue's second seat stays empty and adds nobody.
        Assert.Equal([(anna.Id, 0), (cleo.Id, 4), (ben.Id, 1)], match.Players.Select(p => (p.ClientId, p.Player!.Value.Value)));
        var world = Replay.BuildWorld(Data, match);
        Assert.Equal([new PlayerId(0), new PlayerId(1), new PlayerId(4)], world.Players.Humans);
        Assert.True(world.Players.Shared(Race.Red));
        Assert.False(world.Players.Shared(Race.Blue));
        // Red's starting machines are dealt between its two commanders; Blue's all Ben's.
        var red = world.Machines.Where(m => m.Race == Race.Red).ToList();
        Assert.Contains(red, m => m.Owner == new PlayerId(0));
        Assert.Contains(red, m => m.Owner == new PlayerId(4));
        Assert.All(world.Machines.Where(m => m.Race == Race.Blue), m => Assert.Equal(new PlayerId(1), m.Owner));

        // Cleo's session waits for both others and carries out her orders as hers: she cannot give herself what is hers
        // (as the race's first player, the one an order without a player stands for, she could), and of two machines she
        // gives Anna, Anna's own stays as it is and Cleo's becomes Anna's.
        var (annas, cleos) = (red.First(m => m.Owner == new PlayerId(0)), red.First(m => m.Owner == new PlayerId(4)));
        var session = new LockstepSession(world, new PlayerId(4), match.InputDelay);
        Assert.Equal([new PlayerId(0), new PlayerId(1), new PlayerId(4)], session.Humans);
        session.Queue(new GiveCommand(Race.Red, [cleos.Id], To: new PlayerId(4)));
        session.Queue(new GiveCommand(Race.Red, [annas.Id, cleos.Id], To: new PlayerId(0)));
        var refused = new List<CommandRejected>();
        for (var tick = 0; tick < match.InputDelay + 2; tick++)
        {
            session.Pump();
            foreach (var other in new[] { new PlayerId(0), new PlayerId(1) })
                session.Receive(new Turn(world.Tick, other, []));
            Assert.True(session.TryStep(), $"stuck at tick {world.Tick}");
            refused.AddRange(world.Rejected);
        }
        var refusal = Assert.Single(refused);
        Assert.Equal(((PlayerId?)new PlayerId(4), "they are yours already"), (refusal.Command.Issuer, refusal.Reason));
        Assert.Equal(new PlayerId(0), cleos.Owner);
        Assert.Equal(new PlayerId(0), annas.Owner);
    }

    [Fact]
    public async Task A_change_of_the_settings_alone_leaves_everyone_where_they_sat_and_every_seat_as_it_was_set()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await anna.Open("Room", slots: 3);
        await ben.Join(room);
        await anna.Next<RoomState>(s => s.Players.Count == 2);

        // Ben moves from Blue to Green, and the host lets the computer play Blue.
        ben.Connection.Send(new TakeSeat(2));
        await anna.Next<RoomState>(s => s.Seats[2].ClientId == ben.Id);
        anna.Connection.Send(new SetSeat(1, SeatUse.Computer));
        await anna.Next<RoomState>(s => s.Seats[1].Use == SeatUse.Computer);

        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], new MatchOptions { FogOfWar = false }, FillWithAi: false));
        var state = await anna.Next<RoomState>(s => !s.Options.FogOfWar);
        Assert.Equal([anna.Id, null, ben.Id], state.Seats.Select(s => s.ClientId));
        Assert.Equal([SeatUse.Open, SeatUse.Computer, SeatUse.Open], state.Seats.Select(s => s.Use));
        Assert.All(state.Players, p => Assert.False(p.Ready));

        // Another map (here one more start position) seats everyone anew, the host first.
        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green, Race.Yellow], new MatchOptions { FogOfWar = false }, FillWithAi: false));
        state = await anna.Next<RoomState>(s => s.Seats.Count == 4);
        Assert.Equal([anna.Id, ben.Id, null, null], state.Seats.Select(s => s.ClientId));
    }
}
