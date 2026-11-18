using System.Numerics;
using System.Text.Json;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>
/// The players of a match apart from their races (P1 design §1, slice S1, test T3): how they are numbered, which tables are
/// refused, that the table today's race-keyed callers mean is the one a relayed match records, the one battle test, an
/// actor's owner, a factory's production owners, and players on the wire.
/// </summary>
public class PlayerTableTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static PlayerId P(int value) => new(value);

    private static PlayerSeat Row(int id, Race race, PlayerKind kind = PlayerKind.Human, string name = "Anna") => new(P(id), race, kind, name);

    // ---- numbering ----------------------------------------------------------------------------------

    [Fact]
    public void One_player_per_race_is_numbered_as_the_races_whatever_the_order()
    {
        Assert.Equal([P(0), P(1)], PlayerTable.Number([Race.Red, Race.Blue]));
        Assert.Equal([P(0), P(3), P(1), P(2)], PlayerTable.Number([Race.Red, Race.Yellow, Race.Blue, Race.Green]));
        Assert.Equal([P(2)], PlayerTable.Number([Race.Green]));
    }

    [Fact]
    public void A_race_s_second_player_gets_the_next_number_from_four_on()
    {
        Assert.Equal([P(0), P(4), P(1), P(5)], PlayerTable.Number([Race.Red, Race.Red, Race.Blue, Race.Blue]));
        Assert.Equal([P(0), P(1), P(4), P(5)], PlayerTable.Number([Race.Red, Race.Blue, Race.Red, Race.Blue]));
        // Survival with four heroes of one race: the numbers of the other races' first players stay free.
        Assert.Equal([P(0), P(4), P(5), P(6), P(1)], PlayerTable.Number([Race.Red, Race.Red, Race.Red, Race.Red, Race.Blue]));
    }

    [Fact]
    public void Five_against_five_and_fifteen_against_fifteen_fit_the_numbers()
    {
        var five = PlayerTable.Number([.. Enumerable.Repeat(Race.Red, 5), .. Enumerable.Repeat(Race.Green, 5)]);
        Assert.Equal([P(0), P(4), P(5), P(6), P(7), P(2), P(8), P(9), P(10), P(11)], five);

        var fifteen = PlayerTable.Number([.. Enumerable.Repeat(Race.Red, 15), .. Enumerable.Repeat(Race.Green, 15)]);
        Assert.Equal(30, fifteen.Distinct().Count());
        Assert.Equal(31, fifteen.Max(id => id.Value));
        Assert.Equal((P(0), P(2)), (fifteen[0], fifteen[15]));

        Assert.Throws<ArgumentException>(() => PlayerTable.Number([.. Enumerable.Repeat(Race.Red, 16), .. Enumerable.Repeat(Race.Green, 15)]));
    }

    // ---- refusals -----------------------------------------------------------------------------------

    public static TheoryData<string, PlayerSeat[]> BadTables => new()
    {
        { "an id twice", [Row(0, Race.Red), Row(0, Race.Red, name: "Ben")] },
        { "an id past 31", [Row(0, Race.Red), Row(32, Race.Red)] },
        { "a negative id", [Row(-1, Race.Red)] },
        { "a race whose first player is not its own number", [Row(4, Race.Red)] },
        { "a race's second player with another race's number", [Row(0, Race.Red), Row(1, Race.Red)] },
        { "a race of the computer beside a person", [Row(1, Race.Blue, PlayerKind.Ai, "AI"), Row(4, Race.Blue)] },
        { "a race of the computer twice", [Row(1, Race.Blue, PlayerKind.Ai, "AI"), Row(4, Race.Blue, PlayerKind.Ai, "AI")] },
        { "thirty-one players", [.. Enumerable.Range(0, 31).Select(i => Row(i < 4 ? i : i + 1, (Race)Math.Min(i, 3)))] },
    };

    [Theory]
    [MemberData(nameof(BadTables))]
    public void A_bad_table_is_refused(string what, PlayerSeat[] rows)
    {
        Assert.False(string.IsNullOrEmpty(what));
        Assert.Throws<ArgumentException>(() => new PlayerTable(rows));
    }

    [Fact]
    public void A_bot_outside_a_battle_is_refused()
    {
        var table = new PlayerTable([Row(0, Race.Red), Row(1, Race.Blue, PlayerKind.Bot, "Flux")]);

        Assert.Throws<ArgumentException>(() => MatchSetup.Create(Data, "m_desert", "m_desert", table, 7));
    }

    // ---- what the table says -----------------------------------------------------------------------

    [Fact]
    public void A_table_answers_by_player_and_by_race()
    {
        var table = new PlayerTable(
        [
            Row(4, Race.Red, name: "Ben"), Row(1, Race.Blue, name: "Cleo"), Row(0, Race.Red),
            Row(3, Race.Yellow, PlayerKind.Ai, "AI"),
        ]);

        Assert.Equal([Race.Red, Race.Blue, Race.Yellow], table.Races);
        Assert.Equal([P(0), P(1), P(3), P(4)], table.ById.Select(r => r.Id));
        Assert.Equal([P(0), P(1), P(4)], table.Humans);
        Assert.Equal([P(0), P(4)], table.PlayersOf(Race.Red));
        Assert.Empty(table.PlayersOf(Race.Green));
        Assert.True(table.Shared(Race.Red));
        Assert.False(table.Shared(Race.Blue) || table.Shared(Race.Green));
        Assert.True(table.AnyShared);
        Assert.True(table.HasHuman(Race.Red) && table.HasHuman(Race.Blue) && !table.HasHuman(Race.Yellow));
        Assert.Equal(("Ben", Race.Red), (table[P(4)].Name, table.RaceOf(P(4))));
        Assert.Equal((0, 1), (table.PlaceInRace(P(0)), table.PlaceInRace(P(4))));
        // The waves' race has no row and is still the race of its number.
        Assert.Equal(Race.Green, table.RaceOf(P(2)));
        Assert.Throws<KeyNotFoundException>(() => table.RaceOf(P(9)));
        Assert.False(table.Contains(P(2)));
    }

    [Fact]
    public void A_table_with_two_commanders_of_a_race_builds_a_world_and_deals_them_the_race_s_machines()
    {
        var table = new PlayerTable([Row(0, Race.Red), Row(1, Race.Blue, PlayerKind.Ai, "AI"), Row(4, Race.Red, name: "Ben")]);
        var single = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7);

        var world = MatchSetup.Create(Data, "m_desert", "m_desert", table, 7);

        Assert.Equal([Race.Red, Race.Blue], world.Races.Keys);
        Assert.Equal((Controller.Player, Controller.Ai), (world.Races[Race.Red].Controller, world.Races[Race.Blue].Controller));
        Assert.True(world.Players.Shared(Race.Red) && world.Players.AnyShared);
        Assert.Equal(single.Actors.Select(a => (a.Id, a.Race)), world.Actors.Select(a => (a.Id, a.Race)));
        // The starting machines in turn, in the order they were made; buildings and the computer's machines stay the race's.
        var red = world.Machines.Where(m => m.Race == Race.Red).OrderBy(m => m.Id).ToList();
        Assert.True(red.Count >= 2);
        Assert.Equal(red.Select((_, i) => i % 2 == 0 ? P(0) : P(4)), red.Select(m => m.Owner));
        Assert.All(world.Actors.Where(a => a is Construction || a.Race == Race.Blue), a => Assert.Null(a.OwnerOverride));
        Assert.NotEqual(single.Checksum(), world.Checksum());
        for (var tick = 0; tick < 200; tick++)
            world.Step();
        Assert.Equal(200, world.Tick);
    }

    [Fact]
    public void Presence_is_the_world_s_own_and_starts_with_everyone_there()
    {
        var table = new PlayerTable([Row(0, Race.Red), Row(4, Race.Red, name: "Ben"), Row(1, Race.Blue, PlayerKind.Ai, "AI")]);
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", table, 7);
        var twin = MatchSetup.Create(Data, "m_desert", "m_desert", table, 7);

        world.ChangePresence([new PresenceChange(P(4), Away: true)]);

        Assert.True(world.Players.Away(P(4)));
        Assert.False(twin.Players.Away(P(4)) || world.Players.Away(P(0)));
        Assert.NotEqual(world.Checksum(), twin.Checksum());
        world.HandBackFromAi(P(4));
        Assert.False(world.Players.Away(P(4)));
        Assert.Throws<KeyNotFoundException>(() => world.ChangePresence([new PresenceChange(P(9), Away: true)]));
    }

    [Fact]
    public void Changes_of_presence_naming_a_stranger_change_nothing()
    {
        var table = new PlayerTable([Row(0, Race.Red), Row(1, Race.Blue, name: "Ben")]);
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", table, 7);
        var before = world.Checksum();

        Assert.Throws<KeyNotFoundException>(() => world.ChangePresence(
            [new PresenceChange(P(0), Away: true), new PresenceChange(P(1), Away: true), new PresenceChange(P(9), Away: true)]));

        Assert.Empty(world.Players.AwayPlayers);
        Assert.Equal((Controller.Player, Controller.Player), (world.Races[Race.Red].Controller, world.Races[Race.Blue].Controller));
        Assert.Empty(world.AiControllers);
        Assert.Equal(before, world.Checksum());
    }

    // ---- today's tables -------------------------------------------------------------------------------

    /// <summary>The matches of today, as the relay starts them: the golden cases, and a battle named only by its victory.</summary>
    public static TheoryData<string> TodaysMatches => [.. GoldenCase.All.Select(c => c.Name), VictoryOnly];

    private const string VictoryOnly = "a battle named only by its victory";

    private static MatchInfo Match(string name) => name == VictoryOnly
        ? new MatchInfo("m_desert", "m_desert", new MatchOptions { Victory = VictoryCondition.Battle }, 5, 4,
        [
            new MatchPlayer(1, "Anna", Race.Green, Race.Green, Ai: false),
            new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true),
            new MatchPlayer(0, "AI", Race.Red, Race.Red, Ai: true),
        ])
        : GoldenCase.All.Single(c => c.Name == name).Match;

    [Theory]
    [MemberData(nameof(TodaysMatches))]
    public void The_table_a_relayed_match_records_is_the_one_its_races_mean(string name)
    {
        var match = Match(name);
        var races = match.Players.ToDictionary(p => p.Slot, p => p.Ai ? Controller.Ai : Controller.Player);

        var recorded = match.Table();
        var meant = PlayerTable.OnePerRace(races, match.Options.IsBattle);

        Assert.Equal(meant.Rows.Select(r => (r.Id, r.Race, r.Kind)), recorded.Rows.Select(r => (r.Id, r.Race, r.Kind)));
        Assert.Equal(match.Players.Select(p => PlayerId.Of(p.Slot)), recorded.Rows.Select(r => r.Id));
        Assert.Equal(match.Players.Select(p => p.Name), recorded.Rows.Select(r => r.Name));
        Assert.False(recorded.AnyShared || meant.AnyShared);
        Assert.All(Enum.GetValues<Race>(), race => Assert.False(recorded.Shared(race)));
        if (match.Options.IsBattle)
            Assert.DoesNotContain(recorded.Rows, r => r.Kind == PlayerKind.Ai);
        else
            Assert.DoesNotContain(recorded.Rows, r => r.Kind == PlayerKind.Bot);
    }

    [Fact]
    public void A_co_op_match_numbers_its_rows_by_the_rule()
    {
        var match = new MatchInfo("m_desert", "m_desert", new MatchOptions(), 5, 4,
        [
            new MatchPlayer(1, "Anna", Race.Red, Race.Red, Ai: false),
            new MatchPlayer(2, "Ben", Race.Blue, Race.Blue, Ai: false),
            new MatchPlayer(3, "Cleo", Race.Red, Race.Red, Ai: false),
            new MatchPlayer(0, "AI", Race.Green, Race.Green, Ai: true),
        ]);

        var table = match.Table();

        Assert.Equal([(P(0), "Anna"), (P(1), "Ben"), (P(4), "Cleo"), (P(2), "AI")], table.Rows.Select(r => (r.Id, r.Name)));
        Assert.Equal([PlayerKind.Human, PlayerKind.Human, PlayerKind.Human, PlayerKind.Ai], table.Rows.Select(r => r.Kind));
        Assert.True(table.Shared(Race.Red) && table.AnyShared);
    }

    [Fact]
    public void A_match_is_a_battle_by_its_options_or_its_victory()
    {
        Assert.False(new MatchOptions().IsBattle);
        Assert.False(new MatchOptions { Victory = VictoryCondition.Wired, Wired = new WiredOptions() }.IsBattle);
        Assert.True(new MatchOptions { Battle = new BattleOptions() }.IsBattle);
        Assert.True(new MatchOptions { Victory = VictoryCondition.Battle }.IsBattle);
        Assert.True(new MatchOptions { Victory = VictoryCondition.Battle, Battle = new BattleOptions() }.IsBattle);
    }

    [Fact]
    public void Today_s_callers_get_one_row_per_race_in_their_order()
    {
        var races = new Dictionary<Race, Controller> { [Race.Blue] = Controller.Player, [Race.Red] = Controller.Ai };

        var skirmish = PlayerTable.OnePerRace(races, battle: false);
        var battle = PlayerTable.OnePerRace(races, battle: true);

        Assert.Equal([(P(1), Race.Blue, PlayerKind.Human, "Player"), (P(0), Race.Red, PlayerKind.Ai, "AI")],
            skirmish.Rows.Select(r => (r.Id, r.Race, r.Kind, r.Name)));
        Assert.Equal([PlayerKind.Human, PlayerKind.Bot], battle.Rows.Select(r => r.Kind));
        Assert.Equal([Race.Blue, Race.Red], skirmish.Races);
        Assert.Empty(PlayerTable.Empty.Rows);
        Assert.False(PlayerTable.Empty.AnyShared);
    }

    // ---- owners ---------------------------------------------------------------------------------------

    [Fact]
    public void Changing_sides_gives_an_actor_to_the_new_race_s_first_player()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7);
        var machine = world.Machines.First(m => m.Race == Race.Red);
        var plain = world.Checksum();

        Assert.All(world.Actors, a => Assert.Null(a.OwnerOverride));
        Assert.Equal(P(0), machine.Owner);

        machine.GiveTo(P(4));
        Assert.Equal((P(4), P(4)), (machine.OwnerOverride, machine.Owner));
        Assert.NotEqual(plain, world.Checksum());

        machine.Race = Race.Red;
        Assert.Equal(P(4), machine.Owner);

        machine.Race = Race.Blue;
        Assert.Null(machine.OwnerOverride);
        Assert.Equal(P(1), machine.Owner);

        machine.GiveTo(P(1));
        Assert.Null(machine.OwnerOverride);
        machine.Race = Race.Red;
        Assert.Equal(plain, world.Checksum());
    }

    [Fact]
    public void A_factory_keeps_who_ordered_what_only_while_the_queue_changes_through_it()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7);
        var factory = world.AddConstruction(Race.Red, new Vector2(100, 100), "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var key = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        // A race of one player keeps no owners: everything is its first player's.
        factory.Enqueue(key, null);
        factory.Enqueue(key, null);
        Assert.Empty(factory.ProductionOwners);
        Assert.Equal((P(0), P(0)), (factory.ProductionOwner(0), factory.ProductionOwner(1)));
        factory.Dequeue(0);
        Assert.Single(factory.ProductionQueue);
        factory.Dequeue(0);

        // A race of several keeps them in step, and lets them go once the queue changes another way.
        factory.Enqueue(key, P(4));
        factory.Enqueue(key, P(0));
        Assert.Equal((P(4), P(0)), (factory.ProductionOwner(0), factory.ProductionOwner(1)));
        factory.Dequeue(0);
        Assert.Equal([P(0)], factory.ProductionOwners);
        factory.ProductionQueue.Add(key);
        Assert.Equal((P(0), P(0)), (factory.ProductionOwner(0), factory.ProductionOwner(1)));
        factory.Dequeue(1);
        Assert.Empty(factory.ProductionOwners);
        Assert.Equal(P(0), factory.ProductionOwner(0));
        factory.ProductionQueue.Clear();
        factory.Enqueue(key, P(4));
        Assert.Equal([P(4)], factory.ProductionOwners);
    }

    [Fact]
    public void Owners_let_go_never_fall_back_into_step_by_chance()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert", new PlayerTable([Row(0, Race.Red), Row(4, Race.Red, name: "Ben")]), 7);
        var factory = world.AddConstruction(Race.Red, new Vector2(100, 100), "FACTORY", "CIVILIAN", 1, 0, complete: true);
        var key = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        factory.Enqueue(key, P(4));
        factory.Enqueue(key, P(4));
        // One put at the front another way: from now on every machine on the queue is the race's first player's.
        factory.ProductionQueue.Insert(0, key);
        Assert.Equal([P(0), P(0), P(0)], Enumerable.Range(0, 3).Select(factory.ProductionOwner));

        // Taking one off leaves as many owners as machines again, but they no longer say whose the machines are.
        factory.Dequeue(1);
        Assert.Equal([P(0), P(0)], Enumerable.Range(0, 2).Select(factory.ProductionOwner));
        factory.Enqueue(key, P(4));
        Assert.Equal([P(0), P(0), P(0)], Enumerable.Range(0, 3).Select(factory.ProductionOwner));

        // Once the queue is empty, it keeps its owners again.
        while (factory.ProductionQueue.Count > 0)
            factory.Dequeue(0);
        factory.Enqueue(key, P(4));
        factory.Enqueue(key, P(0));
        Assert.Equal([P(4), P(0)], Enumerable.Range(0, 2).Select(factory.ProductionOwner));
    }

    [Fact]
    public void A_land_mine_is_its_race_s_first_player_s_unless_named()
    {
        Assert.Equal(P(1), new LandMineState(1, Race.Blue, Vector2.Zero).Owner);
        Assert.Equal(P(5), new LandMineState(2, Race.Blue, Vector2.Zero, P(5)).Owner);
    }

    // ---- on the wire ------------------------------------------------------------------------------------

    [Fact]
    public void A_player_goes_over_the_wire_as_its_bare_number()
    {
        Assert.Equal("7", JsonSerializer.Serialize(P(7), NetJson.Options));
        Assert.Equal("{\"4\":\"Ben\"}", JsonSerializer.Serialize(new Dictionary<PlayerId, string> { [P(4)] = "Ben" }, NetJson.Options));
        Assert.Equal("Ben", JsonSerializer.Deserialize<Dictionary<PlayerId, string>>("{\"4\":\"Ben\"}", NetJson.Options)![P(4)]);

        var hero = new BattleHero(Race.Red, "Anvil", "grunt1", [], Bot: true, Player: P(5));
        var back = JsonSerializer.Deserialize<BattleHero>(JsonSerializer.Serialize(hero, NetJson.Options), NetJson.Options)!;
        Assert.Equal((hero.Player, hero.Name), (back.Player, back.Name));
        Assert.Null(JsonSerializer.Deserialize<BattleHero>(JsonSerializer.Serialize(hero with { Player = null }, NetJson.Options), NetJson.Options)!.Player);

        Command give = new GiveCommand(Race.Red, [3, 4], P(4));
        var given = (GiveCommand)JsonSerializer.Deserialize<Command>(JsonSerializer.Serialize(give, NetJson.Options), NetJson.Options)!;
        Assert.Equal((P(4), 2), (given.To, given.Machines.Count));
    }

    [Fact]
    public void Who_gave_a_command_is_never_sent()
    {
        Command stop = new StopCommand(Race.Red, [3]) { Issuer = P(4) };

        var json = JsonSerializer.Serialize(stop, NetJson.Options);
        var back = JsonSerializer.Deserialize<Command>(json, NetJson.Options)!;

        Assert.DoesNotContain("Issuer", json, StringComparison.Ordinal);
        Assert.Null(back.Issuer);
        Assert.Equal(JsonSerializer.Serialize(stop with { Issuer = null }, NetJson.Options), json);
        Assert.DoesNotContain("IsBattle", JsonSerializer.Serialize(new MatchOptions { Battle = new BattleOptions() }, NetJson.Options), StringComparison.Ordinal);
    }

    [Fact]
    public void A_wired_hero_without_an_entry_is_the_run_s_own()
    {
        var options = new WiredOptions
        {
            HeroName = "Solo",
            Chassis = "spy3",
            Heroes = [new WiredHero(P(4), "Duo", "grunt1", [new WiredStartItem("BOLTER")])],
        };

        Assert.Equal(("Duo", "grunt1"), (options.HeroFor(P(4)).Name, options.HeroFor(P(4)).Chassis));
        Assert.Equal((P(0), "Solo", "spy3"), (options.HeroFor(P(0)).Player, options.HeroFor(P(0)).Name, options.HeroFor(P(0)).Chassis));
    }
}
