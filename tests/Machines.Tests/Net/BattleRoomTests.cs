using System.Net;
using Machines.Import.Model;
using Machines.Net;
using Machines.Relay;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Net;

/// <summary>A battle room through the real relay: the heroes players bring, its seats and how the relay starts it.</summary>
public class BattleRoomTests
{
    private static readonly List<Race> Four = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    private static MatchOptions Options(int tier = 3, int lives = 1) => new() { Battle = new BattleOptions { Tier = tier, Lives = lives }, FogOfWar = false };

    /// <summary>Opens a battle room on Canyon Attack.</summary>
    private static async Task<int> OpenBattle(ChatTests.Client host, int tier = 3)
    {
        host.Connection.Send(new CreateRoom("Battle", "m_desert", "m_desert", Four, Options(tier), FillWithAi: false));
        return (await host.Next<RoomState>(s => s.Players.Any(p => p.ClientId == host.Id))).Id;
    }

    private static async Task<Refused> Refusal(ChatTests.Client client)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
            if (await client.Connection.Inbox.ReadAsync(timeout.Token) is Refused refused)
                return refused;
    }

    [Fact]
    public async Task A_battle_room_shows_its_mode_and_bracket_in_the_list_and_a_normal_one_none()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        await using var cleo = await ChatTests.Client.Connect(relay, "Cleo");
        await OpenBattle(anna, tier: 5);
        await ben.Open("Normal", slots: 2);

        cleo.Connection.Send(new ListRooms());
        var rooms = (await cleo.Next<RoomList>()).Rooms;

        var battle = rooms.Single(r => r.Name == "Battle");
        Assert.Equal(("battle", 5, 4), (battle.Mode, battle.Tier, battle.MaxPlayers));
        var normal = rooms.Single(r => r.Name == "Normal");
        Assert.Equal((null, 0), (normal.Mode, normal.Tier));
    }

    [Fact]
    public async Task The_relay_refuses_a_hero_out_of_the_bracket_or_one_that_does_not_fit_together()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await OpenBattle(anna, tier: 3);

        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: BattleNetTests.Brought("Anna", 1)));
        Assert.Equal("that hero is not in the room's bracket", (await Refusal(anna)).Reason);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: BattleNetTests.Brought("Anna", 4)));
        Assert.Equal("that hero is not in the room's bracket", (await Refusal(anna)).Reason);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: BattleNetTests.Brought("Anna", 3) with { Chassis = "nothing" }));
        Assert.Equal("that hero is not in the room's bracket", (await Refusal(anna)).Reason);

        // In the bracket, but with what its chassis cannot carry: a weapon that does not exist, one at a Mk the tier does
        // not allow, an ability a battle does not offer, and a hand-made message that leaves the items out.
        var good = BattleNetTests.Brought("Anna", 3);
        var weapon = good.Items.First(i => i.Slot is { Kind: WiredSlotKind.Weapon });
        foreach (var bad in new[]
                 {
                     good with { Items = [.. good.Items.Where(i => i != weapon), weapon with { Catalogue = "NO_SUCH_GUN" }] },
                     good with { Items = [.. good.Items.Where(i => i != weapon), weapon with { Mk = 3 }] },
                     good with { Items = [.. good.Items.Where(i => i.Slot is not { Kind: WiredSlotKind.Ability }), new BattleItem("treachery", WiredSlot.Ability(0))] },
                     good with { Items = null! },
                 })
        {
            anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: bad));
            Assert.Equal("that hero does not fit together", (await Refusal(anna)).Reason);
        }

        // Nobody is ready without a hero; with a good one they are, and the room shows it.
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true));
        Assert.Equal("choose a hero in your pod first", (await Refusal(anna)).Reason);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: good));
        var state = await anna.Next<RoomState>(s => s.Players.Single().Ready);
        BattleNetTests.SameHero(good, state.Players.Single().Hero!);
    }

    [Fact]
    public async Task A_hero_bringing_more_than_its_chassis_wears_is_refused_and_its_name_is_kept_short()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna, whose name runs on far longer than any hero's may");
        await OpenBattle(anna, tier: 3);
        var good = BattleNetTests.Brought("Anna", 3);

        // Things without a slot would stay in the inventory: thousands of them would be echoed to every member of the room.
        var heap = good with { Items = [.. good.Items, .. Enumerable.Repeat(new BattleItem("plates", null), 5000)] };
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: heap));
        Assert.Equal("that hero does not fit together", (await Refusal(anna)).Reason);

        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: good with { Name = new string('x', 300) }));
        var state = await anna.Next<RoomState>(s => s.Players.Single().Ready);
        Assert.Equal(new string('x', BattleHero.MostName), state.Players.Single().Hero!.Name);
        // With no name of its own the hero takes the player's, as short.
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: false, Hero: good with { Name = " " }));
        state = await anna.Next<RoomState>(s => !s.Players.Single().Ready);
        Assert.Equal("Anna, whose name runs on", state.Players.Single().Hero!.Name);
        Assert.Equal(BattleHero.MostName, state.Players.Single().Hero!.Name.Length);
    }

    [Fact]
    public async Task A_room_asking_for_the_battle_victory_is_a_battle_even_without_its_settings()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var victory = new MatchOptions { Victory = VictoryCondition.Battle };

        // Off the battle maps it is refused (the sim could not set it up there) ...
        anna.Connection.Send(new CreateRoom("Sneak", "m_moon", "m_moon", Four, victory, FillWithAi: false));
        Assert.Equal("battles are fought only on the battle maps", (await Refusal(anna)).Reason);
        await anna.Open("Normal", slots: 2);
        anna.Connection.Send(new ChangeRoom("m_moon", "m_moon", Four, victory, FillWithAi: false));
        Assert.Equal("battles are fought only on the battle maps", (await Refusal(anna)).Reason);

        // ... and on one it is a battle with the default settings: listed as one, a hero needed to be ready.
        ben.Connection.Send(new CreateRoom("Battle", "fortmult", "fortmult", [Race.Red, Race.Blue], victory, FillWithAi: false));
        var state = await ben.Next<RoomState>(s => s.Players.Any(p => p.ClientId == ben.Id));
        Assert.Equal(1, state.Options.Battle!.Tier);
        Assert.Equal(Four, state.Slots);
        ben.Connection.Send(new ListRooms());
        var listed = (await ben.Next<RoomList>()).Rooms.Single(r => r.Name == "Battle");
        Assert.Equal(("battle", 1), (listed.Mode, listed.Tier));
        ben.Connection.Send(new SetPlayer(Race.Red, Ready: true));
        Assert.Equal("choose a hero in your pod first", (await Refusal(ben)).Reason);
    }

    [Fact]
    public async Task A_battle_room_shows_its_maps_planet_and_the_settings_the_sim_plays()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        var odd = new MatchOptions
        {
            Victory = VictoryCondition.Annihilation,
            Wired = new WiredOptions(),
            Battle = new BattleOptions
            {
                Tier = 3, Lives = -7, RespawnWait = -3, TimeLimit = -1, Win = (BattleWin)42,
                TeamA = [Race.Red], TeamB = [Race.Red, Race.Green],
                Heroes = [BattleNetTests.Brought("Fake", 3), BattleNetTests.Brought("Fake", 3)],
            },
        };
        anna.Connection.Send(new CreateRoom("Battle", "m_desert", "micey", [Race.Red, Race.Blue], odd, FillWithAi: false));
        var state = await anna.Next<RoomState>(s => s.Players.Any(p => p.ClientId == anna.Id));

        var map = BattleCatalogue.Map("m_desert")!;
        Assert.Equal(("m_desert", "m_desert"), (state.Scenario, state.Planet));
        Assert.Equal(VictoryCondition.Battle, state.Options.Victory);
        Assert.Null(state.Options.Wired);
        var battle = state.Options.Battle!;
        Assert.Equal((3, 1, 0f, 1, BattleWin.Both), (battle.Tier, battle.Lives, battle.RespawnWait, battle.TimeLimit, battle.Win));
        Assert.Equal(map.TeamA, battle.TeamA);
        Assert.Equal(map.TeamB, battle.TeamB);
        Assert.Empty(battle.Heroes);

        // A change is held to the same: more lives than five are five.
        anna.Connection.Send(new ChangeRoom("micey", "m_desert", Four, odd with { Battle = odd.Battle! with { Lives = 9 } }, FillWithAi: false));
        state = await anna.Next<RoomState>(s => s.Scenario == "micey");
        Assert.Equal("micey", state.Planet);
        Assert.Equal((5, BattleWin.Both), (state.Options.Battle!.Lives, state.Options.Battle.Win));
        Assert.Empty(state.Options.Battle.Heroes);
    }

    [Fact]
    public async Task A_normal_room_ignores_a_hero_and_a_battle_only_goes_on_the_battle_maps()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        await anna.Open("Normal", slots: 2);

        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: BattleNetTests.Brought("Anna", 1)));
        var state = await anna.Next<RoomState>(s => s.Players.Single().Ready);
        Assert.Null(state.Players.Single().Hero);

        anna.Connection.Send(new ChangeRoom("m_moon", "m_moon", Four, Options(), FillWithAi: false));
        Assert.Equal("battles are fought only on the battle maps", (await Refusal(anna)).Reason);
        ben.Connection.Send(new CreateRoom("Battle", "m_desert", "m_desert", Four, Options(tier: 9), FillWithAi: false));
        Assert.Equal("no such tier", (await Refusal(ben)).Reason);
    }

    [Fact]
    public async Task A_battle_room_has_no_closed_seats()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await OpenBattle(anna);

        anna.Connection.Send(new SetSeat(1, SeatUse.Closed));
        var state = await anna.Next<RoomState>(s => s.Seats[1].Use != SeatUse.Open);
        Assert.Equal(SeatUse.Computer, state.Seats[1].Use);

        // A normal room's closed seat becomes the computer's when the room turns into a battle; its start positions are the map's teams.
        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], new MatchOptions(), FillWithAi: false));
        await anna.Next<RoomState>(s => s.Seats.Count == 3 && s.Options.Battle is null);
        anna.Connection.Send(new SetSeat(2, SeatUse.Closed));
        await anna.Next<RoomState>(s => s.Seats[2].Use == SeatUse.Closed);
        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green], Options() with { DisableFirstPerson = true }, FillWithAi: false));
        state = await anna.Next<RoomState>(s => s.Options.Battle is not null);
        Assert.Equal(Four, state.Slots);
        Assert.DoesNotContain(state.Seats, s => s.Use == SeatUse.Closed);
        Assert.Equal(SeatUse.Computer, state.Seats[1].Use);
        Assert.False(state.Options.DisableFirstPerson);
    }

    [Fact]
    public async Task Heroes_are_chosen_again_when_the_bracket_or_the_map_changes()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await OpenBattle(anna, tier: 3);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: BattleNetTests.Brought("Anna", 3)));
        await anna.Next<RoomState>(s => s.Players.Single().Ready);

        // More lives: the hero stays (everyone gets ready again).
        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", Four, Options(3, lives: 3), FillWithAi: false));
        var state = await anna.Next<RoomState>(s => s.Options.Battle!.Lives == 3);
        Assert.NotNull(state.Players.Single().Hero);
        Assert.False(state.Players.Single().Ready);

        anna.Connection.Send(new ChangeRoom("m_desert", "m_desert", Four, Options(4, lives: 3), FillWithAi: false));
        state = await anna.Next<RoomState>(s => s.Options.Battle!.Tier == 4);
        Assert.Null(state.Players.Single().Hero);

        anna.Connection.Send(new SetPlayer(Race.Red, Ready: false, Hero: BattleNetTests.Brought("Anna", 4)));
        await anna.Next<RoomState>(s => s.Players.Single().Hero is not null);
        anna.Connection.Send(new ChangeRoom("m_green", "m_green", Four, Options(4, lives: 3), FillWithAi: false));
        state = await anna.Next<RoomState>(s => s.Scenario == "m_green");
        Assert.Null(state.Players.Single().Hero);
    }

    [Fact]
    public async Task The_start_gives_every_race_a_hero_the_players_theirs_and_bots_by_the_mirror_rule()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await using var ben = await ChatTests.Client.Connect(relay, "Ben");
        var room = await OpenBattle(anna, tier: 3);
        await ben.Join(room);
        // Anna at Red brings a tier II hero, Ben at Blue a tier III one; Green and Yellow are the computer's.
        var annas = BattleNetTests.Brought("Anna's hero", 2);
        var bens = BattleNetTests.Brought("Ben's hero", 3, line: 1);
        ben.Connection.Send(new SetPlayer(Race.Blue, Ready: true, Hero: bens));
        await anna.Next<RoomState>(s => s.Players.Any(p => p.ClientId == ben.Id && p.Ready));
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: false, Hero: annas));
        await anna.Next<RoomState>(s => s.Players.Any(p => p.ClientId == anna.Id && p.Hero is not null));

        anna.Connection.Send(new StartMatch());
        Assert.Equal("not everyone is ready", (await Refusal(anna)).Reason);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: annas));
        await anna.Next<RoomState>(s => s.Players.All(p => p.Ready));
        anna.Connection.Send(new StartMatch());

        var match = (await ben.Next<MatchStarted>()).Match;
        var map = BattleCatalogue.Map("m_desert")!;
        var battle = match.Options.Battle!;
        Assert.Equal(map.TeamA, battle.TeamA);
        Assert.Equal(map.TeamB, battle.TeamB);
        Assert.Equal(Four, match.Players.Select(p => p.Slot).Order());
        Assert.Equal(anna.Id, match.Players.Single(p => p.Slot == Race.Red).ClientId);
        Assert.Equal(ben.Id, match.Players.Single(p => p.Slot == Race.Blue).ClientId);
        Assert.True(match.Players.Single(p => p.Slot == Race.Green).Ai);
        Assert.True(match.Players.Single(p => p.Slot == Race.Yellow).Ai);

        Assert.Equal(Four, battle.Heroes.Select(h => h.Race));
        BattleNetTests.SameHero(annas with { Race = Race.Red }, battle.Heroes[0]);
        BattleNetTests.SameHero(bens with { Race = Race.Blue }, battle.Heroes[1]);
        // Green is the first of team B and mirrors Anna's tier, Yellow the second and mirrors Ben's.
        Assert.Equal((true, 2), (battle.Heroes[2].Bot, BattleCatalogue.Tier(battle.Heroes[2].Chassis)));
        Assert.Equal((true, 3), (battle.Heroes[3].Bot, BattleCatalogue.Tier(battle.Heroes[3].Chassis)));
        BattleNetTests.SameHero(BattleBots.Bot(3, Race.Yellow, 1), battle.Heroes[3]);
        Assert.Null(BattleBots.WhyNot(battle.Heroes[2], 3));
    }

    [Fact]
    public async Task At_the_start_every_seat_nobody_sits_at_is_a_bot_whatever_it_was_set_to()
    {
        await using var relay = RelayServer.Start(0, IPAddress.Loopback);
        await using var anna = await ChatTests.Client.Connect(relay, "Anna");
        await OpenBattle(anna, tier: 1);
        anna.Connection.Send(new SetSeat(1, SeatUse.Closed));
        anna.Connection.Send(new SetSeat(2, SeatUse.Computer));
        await anna.Next<RoomState>(s => s.Seats[2].Use == SeatUse.Computer);
        anna.Connection.Send(new SetPlayer(Race.Red, Ready: true, Hero: BattleNetTests.Brought("Anna", 1)));
        await anna.Next<RoomState>(s => s.Players.All(p => p.Ready));

        anna.Connection.Send(new StartMatch());

        var match = (await anna.Next<MatchStarted>()).Match;
        Assert.Equal(4, match.Players.Count);
        Assert.Equal(3, match.Players.Count(p => p.Ai));
        Assert.Equal(4, match.Options.Battle!.Heroes.Count);
        Assert.Equal(3, match.Options.Battle.Heroes.Count(h => h.Bot));
        Assert.Equal(4, match.Players.Select(p => p.Colour).Distinct().Count());
    }
}
