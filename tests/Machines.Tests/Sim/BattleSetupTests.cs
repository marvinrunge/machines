using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>What the battle tests share: a battle on a map, stepping it, and its heroes.</summary>
internal static class Battles
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    public static GameData Data => DataLoader.Value;

    /// <summary>A battle on a map of the catalogue; the races not named in <paramref name="bots"/> are players, who stand still.</summary>
    public static GameWorld New(BattleOptions? options = null, string map = "m_desert", int seed = 7, params Race[] bots)
    {
        var entry = BattleCatalogue.Map(map)!;
        options ??= new BattleOptions();
        options = options with
        {
            TeamA = options.TeamA.Count > 0 ? options.TeamA : entry.TeamA,
            TeamB = options.TeamB.Count > 0 ? options.TeamB : entry.TeamB,
        };
        if (options.Heroes.Count == 0)
            options = BattleBots.Compose(options, [], entry.Scenario);
        var world = MatchSetup.Create(Data, entry.Scenario, entry.Planet,
            Enum.GetValues<Race>().ToDictionary(r => r, r => bots.Contains(r) ? Controller.Ai : Controller.Player), seed,
            new MatchOptions { Battle = options, FogOfWar = false });
        // The players' heroes are at the wheel and stand still, so they do not shoot of their own accord: the tests say who hits whom.
        foreach (var hero in Enum.GetValues<Race>().Where(r => !bots.Contains(r)).Select(world.HeroOf).OfType<Machine>())
            hero.DrivenByPlayer = true;
        return world;
    }

    /// <summary>A battle past its opening.</summary>
    public static GameWorld Begun(BattleOptions? options = null, string map = "m_desert", params Race[] bots)
    {
        var world = New(options, map, 7, bots);
        Run(world, BattleBalance.Opening + GameWorld.Dt);
        Assert.False(world.Battle!.InOpening(world.Time));
        return world;
    }

    /// <returns>Why the command was refused, or null.</returns>
    public static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    /// <returns>All that happened in that time.</returns>
    public static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < seconds / GameWorld.Dt; i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    public static Machine Hero(GameWorld world, Race race) => world.HeroOf(race)!;

    /// <returns>What the kill raised: a step clears the events, so they are taken at once.</returns>
    public static List<GameEvent> Kill(GameWorld world, Race race, Race by)
    {
        var before = world.Events.Count;
        world.Damage(Hero(world, race), 1_000_000, by);
        return world.Events.Skip(before).ToList();
    }

    /// <summary>Puts a hero in the middle of the other team's capture circle, beside the pod.</summary>
    public static void IntoCircle(GameWorld world, Race race, float offset = 0)
    {
        var run = world.Battle!;
        var pod = world.BattlePod(1 - run.TeamOf(race))!;
        var hero = Hero(world, race);
        var place = Enumerable.Range(0, 32).Select(i => pod.Position + new Vector2(DetMath.Cos(i * MathF.Tau / 32 + offset), DetMath.Sin(i * MathF.Tau / 32 + offset)) * 22)
            .First(p => world.Nav.IsFree(p, crossesWater: false));
        hero.Position = place;
        hero.Path.Clear();
    }

    /// <summary>Puts a hero far from every pod's circle.</summary>
    public static void Away(GameWorld world, Race race, Vector2 at) => Hero(world, race).Position = world.Nav.NearestFreePoint(at, crossesWater: false) ?? at;
}

/// <summary>A battle is set up: two teams of heroes allied within, one pod each, bots for the computer's races, nothing of Wired's waves.</summary>
public class BattleSetupTests
{
    private static GameData Data => Battles.Data;

    [Fact]
    public void Teams_are_allied_both_ways_and_enemies_across()
    {
        var world = Battles.New();
        Assert.Equal(VictoryCondition.Battle, world.Options.Victory);
        Assert.Null(world.Options.Wired);
        foreach (var (a, b) in new[] { (Race.Red, Race.Blue), (Race.Green, Race.Yellow) })
        {
            Assert.False(world.AreEnemies(a, b));
            Assert.False(world.AreEnemies(b, a));
        }
        foreach (var a in new[] { Race.Red, Race.Blue })
        {
            foreach (var b in new[] { Race.Green, Race.Yellow })
                Assert.True(world.AreEnemies(a, b) && world.AreEnemies(b, a));
        }
        Assert.All(world.Races.Values, r => Assert.DoesNotContain(r.Race, r.Allies));
    }

    [Fact]
    public void Each_team_has_one_pod_that_of_its_anchor_and_nothing_else_stands()
    {
        var world = Battles.New();
        var buildings = world.Constructions.ToList();
        Assert.Equal(2, buildings.Count);
        Assert.All(buildings, b => Assert.True(b.IsType("POD")));
        Assert.Equal(Race.Red, world.BattlePod(0)!.Race);
        Assert.Equal(Race.Green, world.BattlePod(1)!.Race);
        Assert.Equal([world.BattlePod(0)!.Id, world.BattlePod(1)!.Id], world.Battle!.Pods.Select(p => p!.Value));
    }

    [Fact]
    public void Every_race_has_its_hero_at_its_team_pod_and_the_computers_races_a_bot()
    {
        var world = Battles.New(bots: [Race.Blue, Race.Yellow]);
        Assert.Equal(4, world.Machines.Count());
        foreach (var race in Enum.GetValues<Race>())
        {
            var hero = Battles.Hero(world, race);
            var team = world.Battle!.TeamOf(race);
            Assert.True(Vector2.Distance(hero.Position, world.BattlePod(team)!.Position) < 45, $"{race} stands at {hero.Position}");
            Assert.Equal(world.BattlePod(team)!.Id, world.Wired!.Heroes[race].Pod);
            // The first of a team on Grunt 1, the second on the crab: the first tier's line, taken in turn.
            Assert.Equal(race is Race.Red or Race.Green ? "AGGRESSOR GRUNT" : "GEO_LOCATOR", hero.Spec.TypeName);
        }
        Assert.Equal([PlayerId.Of(Race.Blue), PlayerId.Of(Race.Yellow)], world.HeroBots.Keys);
        Assert.Equal([PlayerId.Of(Race.Blue), PlayerId.Of(Race.Yellow)], world.Battle!.Bots);
        Assert.Equal(DefCon.High, Battles.Hero(world, Race.Blue).DefCon);
        // Team mates do not stand on each other.
        Assert.True(Vector2.Distance(Battles.Hero(world, Race.Red).Position, Battles.Hero(world, Race.Blue).Position) >= 4);
    }

    [Fact]
    public void A_battle_has_no_waves_no_shop_and_no_computer_scripts()
    {
        var world = Battles.New(bots: [Race.Blue, Race.Green, Race.Yellow]);
        Assert.Empty(world.AiControllers);
        Assert.Empty(world.StandIns);
        Assert.All(world.Races.Values, r => Assert.Empty(r.Researched));
        var events = Battles.Run(world, 60);
        Assert.Equal(WiredPhase.Battle, world.Wired!.Phase);
        Assert.Equal(0, world.Wired.Wave);
        Assert.Empty(world.Wired.Foes);
        Assert.Empty(world.Wired.SpawnPoints);
        Assert.DoesNotContain(events, e => e.Kind is GameEventKind.WiredWaveStarted or GameEventKind.WiredWreckCollected);
        Assert.All(world.Wired.Heroes.Values, s => Assert.Empty(s.Offer));
        Assert.Equal("there is no hangar in a battle", Battles.Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Equal("there is no hangar in a battle", Battles.Do(world, new WiredReadyCommand(Race.Red)));
        Assert.Equal("there is no hangar in a battle", world.WhyNoHangar(world.Wired.Heroes[Race.Red]));
    }

    [Fact]
    public void Other_matches_have_no_battle()
    {
        var normal = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai });
        var wired = MatchSetup.Create(Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai },
            options: new MatchOptions { Wired = new WiredOptions() });
        Assert.Null(new MatchOptions().Battle);
        Assert.Null(normal.Battle);
        Assert.Null(wired.Battle);
        Assert.Null(wired.Options.Battle);
        Assert.Empty(wired.HeroBots);
        Assert.NotEqual(WiredPhase.Battle, wired.Wired!.Phase);
    }

    [Fact]
    public void Teams_come_from_the_map_table_when_the_options_name_none()
    {
        var world = MatchSetup.Create(Data, "fortmult", "fortmult", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player }, 3,
            new MatchOptions { Battle = new BattleOptions() });
        Assert.Equal([Race.Red, Race.Yellow], world.Battle!.Teams[0]);
        Assert.Equal([Race.Blue, Race.Green], world.Battle.Teams[1]);
        // Races not listed are the computer's.
        Assert.Equal(Controller.Ai, world.Races[Race.Green].Controller);
        Assert.Equal([PlayerId.Of(Race.Blue), PlayerId.Of(Race.Green), PlayerId.Of(Race.Yellow)], world.HeroBots.Keys);
    }

    [Fact]
    public void A_race_the_options_name_no_hero_for_gets_the_bot_the_relay_would_compose_for_the_map()
    {
        var map = BattleCatalogue.Map("fortmult")!;
        var player = BattleBots.Bot(map.Scenario, 4, Race.Red, 0) with { Name = "Ace", Bot = false };
        foreach (var given in new List<BattleHero>[] { [], [player] })
        {
            var options = new BattleOptions { Tier = 5, TeamA = map.TeamA, TeamB = map.TeamB, Heroes = given };
            var world = MatchSetup.Create(Data, map.Scenario, map.Planet, new Dictionary<Race, Controller> { [Race.Red] = Controller.Player }, 3,
                new MatchOptions { Battle = options });
            var composed = BattleBots.Compose(options, given, map.Scenario).Heroes;
            var played = world.Battle!.Options.Heroes;
            Assert.Equal(composed.Count, played.Count);
            foreach (var (expected, actual) in composed.Zip(played))
                Machines.Tests.Net.BattleNetTests.SameHero(expected, actual);
            // Two bots facing each other take the top of the bracket.
            Assert.Contains(played, h => h.Bot && BattleCatalogue.Tier(h.Chassis) == 5);
        }
    }

    [Fact]
    public void Every_map_of_the_catalogue_has_a_pod_for_each_anchor_and_room_for_four_heroes()
    {
        foreach (var map in BattleCatalogue.Maps)
        {
            var world = Battles.New(map: map.Scenario);
            Assert.NotNull(world.BattlePod(0));
            Assert.NotNull(world.BattlePod(1));
            Assert.Equal(4, Enum.GetValues<Race>().Count(r => world.HeroOf(r) is not null));
            var apart = Vector2.Distance(world.BattlePod(0)!.Position, world.BattlePod(1)!.Position);
            Assert.InRange(apart, 600, 1700);
        }
    }

    [Fact]
    public void A_hero_that_does_not_resolve_is_clamped_to_a_bots_loadout()
    {
        var tier = 3;
        BattleHero Hero(Race race, string chassis, params BattleItem[] items) => new(race, race.ToString(), chassis, items);
        var heroes = new[]
        {
            // Out of the bracket: a knight of tier V.
            Hero(Race.Red, "knight3"),
            // A heavy weapon on a light slot.
            Hero(Race.Blue, "grunt3", new BattleItem("PLASMA_CANNON2", WiredSlot.Weapon(Mounting.Left))),
            // A Mk III weapon on tier III, and an unknown chassis.
            Hero(Race.Green, "reaper2", new BattleItem("AUTO_CANNON", WiredSlot.Weapon(Mounting.Right), 3)),
            Hero(Race.Yellow, "nonsense"),
        };
        Assert.All(heroes, h => Assert.NotNull(BattleBots.WhyNot(h, tier)));
        var world = Battles.New(new BattleOptions { Tier = tier, Heroes = heroes });
        var played = world.Battle!.Options.Heroes;
        Assert.Equal(4, played.Count);
        Assert.All(played, h => Assert.Null(BattleBots.WhyNot(h, tier)));
        Assert.All(played, h => Assert.Equal(h.Race.ToString(), h.Name));
        // On its own line where the bottom of the bracket has it, else the chassis a bot of its place takes on the map.
        Assert.Equal("grunt2", played.Single(h => h.Race == Race.Blue).Chassis);
        Assert.Equal(BattleBots.Pick("m_desert", BattleCatalogue.BracketBottom(tier), 0), played.Single(h => h.Race == Race.Green).Chassis);
        Assert.Contains(played.Single(h => h.Race == Race.Green).Chassis, BattleCatalogue.OfTier(BattleCatalogue.BracketBottom(tier)).Select(c => c.Id));
        Assert.All(Enum.GetValues<Race>(), r => Assert.NotNull(world.HeroOf(r)));

        // A hero that may play stays as it is.
        var fine = Hero(Race.Red, "reaper2", new BattleItem("AUTO_CANNON", WiredSlot.Weapon(Mounting.Left)), new BattleItem("plates", WiredSlot.Module(0)),
            new BattleItem("discharge", WiredSlot.Ability(0)));
        Assert.Null(BattleBots.WhyNot(fine, tier));
        Assert.Same(fine, BattleBots.Resolved(fine, tier, 0, "m_desert"));
        Assert.NotNull(BattleBots.WhyNot(fine with { Items = [new BattleItem("treachery", WiredSlot.Ability(0))] }, tier));
        Assert.NotNull(BattleBots.WhyNot(Hero(Race.Red, "grunt2", new BattleItem("discharge", WiredSlot.Ability(0))), tier));
    }

    [Fact]
    public void A_hand_made_hero_with_no_items_or_more_than_its_chassis_wears_is_clamped()
    {
        var tier = 3;
        var fine = BattleBots.Bot("m_desert", tier, Race.Red, 0);
        Assert.Null(BattleBots.WhyNot(fine, tier));
        // Things without a slot stay in the inventory: a few may come, but never more than the chassis has places for.
        var chassis = WiredCatalogue.FindChassis(fine.Chassis)!;
        var room = chassis.Slots.Count + chassis.Modules + WiredBalance.AbilitySlots - fine.Items.Count;
        Assert.Null(BattleBots.WhyNot(fine with { Items = [.. fine.Items, .. Enumerable.Repeat(new BattleItem("plates", null), room)] }, tier));
        var heap = fine with { Race = Race.Blue, Items = [.. fine.Items, .. Enumerable.Repeat(new BattleItem("plates", null), room + 1)] };
        var bare = fine with { Race = Race.Green, Items = null! };
        var holed = fine with { Race = Race.Yellow, Items = [.. fine.Items, null!] };
        Assert.All(new[] { heap, bare, holed }, h => Assert.NotNull(BattleBots.WhyNot(h, tier)));

        var world = Battles.New(new BattleOptions { Tier = tier, Heroes = [fine, heap, bare, holed] });
        var played = world.Battle!.Options.Heroes;
        Assert.All(played, h => Assert.Null(BattleBots.WhyNot(h, tier)));
        Assert.All(played.Skip(1), h => Assert.Equal(BattleCatalogue.BracketBottom(tier), BattleCatalogue.Tier(h.Chassis)));
        Assert.All(Enum.GetValues<Race>(), r => Assert.NotNull(world.HeroOf(r)));
    }

    [Fact]
    public void A_hero_wears_its_items_at_their_mk()
    {
        var hero = new BattleHero(Race.Red, "Ace", "reaper3",
            [new BattleItem("HEAVY_BOLTER1", WiredSlot.Weapon(Mounting.Left), 2), new BattleItem("PLASMA_RIFLE", WiredSlot.Weapon(Mounting.Right)),
             new BattleItem("plates", WiredSlot.Module(0), 2), new BattleItem("thrusters", WiredSlot.Module(1)), new BattleItem("vortex", WiredSlot.Ability(1))]);
        var world = Battles.New(new BattleOptions { Tier = 4, Heroes = BattleBots.Compose(new BattleOptions { Tier = 4, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, [hero]).Heroes });
        var state = world.Wired!.Heroes[Race.Red];
        Assert.Equal("reaper3", state.Loadout.Chassis.Catalogue);
        Assert.Equal(("HEAVY_BOLTER1", 2), (state.Loadout.Weapon(Mounting.Left)!.Catalogue, state.Loadout.Weapon(Mounting.Left)!.Mk));
        Assert.Equal(("plates", 2), (state.Loadout.Modules[0]!.Catalogue, state.Loadout.Modules[0]!.Mk));
        Assert.Equal("vortex", state.Loadout.Abilities[1]!.Catalogue);
        Assert.Equal(0, state.SpareBodies);
        Assert.Equal(2, Battles.Hero(world, Race.Red).Weapons.Count);
        Assert.NotNull(state.Build(Data).DashCooldown);
    }

    [Fact]
    public void A_battery_is_one_entry_of_a_heros_items_and_the_hero_plays_with_every_copy()
    {
        var tier = BattleCatalogue.Tier("reaper3");
        var hero = new BattleHero(Race.Red, "Ace", "reaper3", [new BattleItem("HEAVY_BOLTER1", WiredSlot.Weapon(Mounting.Left), 1, 2)]);
        Assert.Null(BattleBots.WhyNot(hero, tier));
        // The cap on items counts entries, not copies.
        var chassis = WiredCatalogue.FindChassis("reaper3")!;
        var room = chassis.Slots.Count + chassis.Modules + WiredBalance.AbilitySlots - hero.Items.Count;
        Assert.Null(BattleBots.WhyNot(hero with { Items = [.. hero.Items, .. Enumerable.Repeat(new BattleItem("plates", null), room)] }, tier));
        Assert.Equal("more items than the chassis wears",
            BattleBots.WhyNot(hero with { Items = [.. hero.Items, .. Enumerable.Repeat(new BattleItem("plates", null), room + 1)] }, tier));

        var world = Battles.Begun(new BattleOptions
        {
            Tier = tier,
            Heroes = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, [hero]).Heroes,
        });
        var state = world.Wired!.Heroes[Race.Red];
        Assert.Equal(["HEAVY_BOLTER1", "HEAVY_BOLTER1"], state.Loadout.Battery(Mounting.Left).Select(i => i.Catalogue));
        var red = Battles.Hero(world, Race.Red);
        Assert.Equal([0, 1], red.Weapons.Select(w => w.Copy));

        Assert.Null(Battles.Do(world, new DriveCommand(Race.Red, red.Id, Driving: true, Aim: red.Position + new Vector2(30, 0), Weapons: -1, Buttons: true)));
        var fired = Battles.Run(world, 2).Where(e => e.Kind == GameEventKind.Fired && e.Actor == red.Id).Select(e => e.Gun).ToHashSet();
        Assert.Equal([0, 1], fired.Order());
    }

    [Fact]
    public void Alliances_cannot_change_in_a_battle()
    {
        var world = Battles.Begun();
        Assert.Equal("teams are fixed in a battle", Battles.Do(world, new AllianceCommand(Race.Red, Race.Blue, Ally: false)));
        Assert.Equal("teams are fixed in a battle", Battles.Do(world, new AllianceCommand(Race.Red, Race.Green, Ally: true)));
        Assert.False(world.AreEnemies(Race.Red, Race.Blue));
        Assert.True(world.AreEnemies(Race.Red, Race.Green));
    }

    [Fact]
    public void In_the_opening_no_hero_moves_fires_dashes_or_uses_an_ability()
    {
        var hero = new BattleHero(Race.Red, "Ace", "grunt3",
            [new BattleItem("BOLTER", WiredSlot.Weapon(Mounting.Left)), new BattleItem("thrusters", WiredSlot.Module(0)), new BattleItem("discharge", WiredSlot.Ability(0))]);
        var options = BattleBots.Compose(new BattleOptions { Tier = 3, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, [hero]);
        var world = Battles.New(options);
        var red = Battles.Hero(world, Race.Red);
        var start = red.Position;
        Assert.True(world.Battle!.InOpening(world.Time));
        Assert.Equal(BattleBalance.Opening, world.Battle.OpeningLeft(world.Time));
        Assert.Equal("the battle has not begun yet", Battles.Do(world, new MoveCommand(Race.Red, [red.Id], start + new Vector2(40, 0))));
        Assert.Equal("the battle has not begun yet", Battles.Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
        Assert.Equal("the battle has not begun yet", world.WhyNoAbility(world.Wired!.Heroes[Race.Red], 0, red.Position));
        Assert.Null(Battles.Do(world, new DriveCommand(Race.Red, red.Id, Driving: true, Forward: 1, Turn: 1, Aim: start + new Vector2(30, 0), Weapons: -1, Buttons: true)));
        var events = Battles.Run(world, 3);
        Assert.Equal(start, red.Position);
        Assert.DoesNotContain(events, e => e.Kind == GameEventKind.Fired);

        // Once it is over the same input drives and fires.
        Battles.Run(world, BattleBalance.Opening);
        Assert.False(world.Battle.InOpening(world.Time));
        Battles.Do(world, new DriveCommand(Race.Red, red.Id, Driving: true, Forward: 1, Aim: start + new Vector2(30, 0), Weapons: -1, Buttons: true));
        events = Battles.Run(world, 2);
        Assert.NotEqual(start, red.Position);
        Assert.Contains(events, e => e.Kind == GameEventKind.Fired && e.Actor == red.Id);
    }

    [Fact]
    public void Friendly_fire_is_off_and_blasts_spare_allies()
    {
        var world = Battles.Begun();
        var blue = Battles.Hero(world, Race.Blue);
        var green = Battles.Hero(world, Race.Green);
        var (blueBefore, greenBefore) = (blue.Durability, green.Durability);
        world.Damage(blue, 30, Race.Red);
        world.Damage(blue, 30, Race.Blue);
        Assert.Equal(blueBefore, blue.Durability);
        world.Damage(green, 30, Race.Red);
        Assert.NotEqual(greenBefore, green.Durability);

        // A vortex at the team mate's feet leaves it be.
        var red = Battles.Hero(world, Race.Red);
        world.StartBlast(Data.Parm.Weapon("VORTEX"), blue.Position, Race.Red);
        Battles.Run(world, 3);
        Assert.False(blue.IsDead);
        Assert.False(red.IsDead);
        Assert.Equal(blueBefore, blue.Durability);
    }

    [Fact]
    public void Trees_and_rocks_fall_to_either_team()
    {
        // Artefacts are red, as is team A's anchor: they are nobody's ally all the same.
        var world = Battles.Begun();
        var artefact = world.Artefacts.Where(a => !a.IsDead).OrderBy(a => a.Id).First();
        Assert.Equal(Race.Red, artefact.Race);
        foreach (var race in new[] { Race.Red, Race.Blue, Race.Green, Race.Yellow })
        {
            var before = artefact.HitPoints;
            world.Damage(artefact, 10, race);
            Assert.True(artefact.HitPoints < before, $"{race} did not hurt the artefact");
        }
    }

    [Fact]
    public void Nothing_self_destructs_in_a_battle()
    {
        var world = Battles.Begun();
        var pod = world.BattlePod(0)!;
        var red = Battles.Hero(world, Race.Red);
        Assert.Equal("nothing self-destructs in a battle", Battles.Do(world, new SelfDestructCommand(Race.Red, [pod.Id])));
        Assert.Equal("nothing self-destructs in a battle", Battles.Do(world, new SelfDestructCommand(Race.Red, [red.Id])));
        Battles.Run(world, GameWorld.SelfDestructDelay + 1);
        Assert.Same(pod, world.BattlePod(0));
        Assert.Same(red, world.HeroOf(Race.Red));
    }

    [Fact]
    public void Large_team_mates_are_set_down_clear_of_each_other_at_the_start_and_on_respawn()
    {
        foreach (var map in BattleCatalogue.Maps)
        {
            var world = Battles.New(new BattleOptions { Tier = 8, Lives = 2, RespawnWait = 1 }, map.Scenario);
            foreach (var team in world.Battle!.Teams)
            {
                var (a, b) = (Battles.Hero(world, team[0]), Battles.Hero(world, team[1]));
                Assert.True(Vector2.Distance(a.Position, b.Position) >= a.Radius + b.Radius,
                    $"{map.Scenario}: {a.Spec.TypeName} and {b.Spec.TypeName} stand {Vector2.Distance(a.Position, b.Position)} m apart");
            }
        }

        // A body coming back to the door while its mate stands on it.
        var battle = Battles.Begun(new BattleOptions { Tier = 8, Lives = 2, RespawnWait = 1 });
        var pod = battle.BattlePod(0)!;
        var blue = Battles.Hero(battle, Race.Blue);
        blue.Position = pod.EntranceOutside ?? blue.Position;
        blue.Path.Clear();
        Battles.Away(battle, Race.Red, (pod.Position + battle.BattlePod(1)!.Position) / 2);
        Battles.Kill(battle, Race.Red, Race.Green);
        Battles.Run(battle, 2);
        var red = Battles.Hero(battle, Race.Red);
        Assert.True(Vector2.Distance(red.Position, blue.Position) >= red.Radius + blue.Radius,
            $"the new body stands {Vector2.Distance(red.Position, blue.Position)} m from its mate");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void Team_mates_start_side_by_side_looking_out_of_the_door_and_never_one_in_front_of_the_other(int tier)
    {
        foreach (var map in BattleCatalogue.Maps)
        {
            var world = Battles.New(new BattleOptions { Tier = tier }, map.Scenario);
            for (var team = 0; team < 2; team++)
            {
                var pod = world.BattlePod(team)!;
                var heroes = world.Battle!.Teams[team].Select(r => Battles.Hero(world, r)).ToList();
                foreach (var hero in heroes)
                {
                    var look = new Vector2(DetMath.Cos(hero.Facing), DetMath.Sin(hero.Facing));
                    Assert.True(Vector2.Dot(look, hero.Position - pod.Position) > 0, $"{map.Scenario}: {hero.Race} looks at its own pod");
                }
                AssertBeside(map.Scenario, heroes[0], heroes[1]);
                AssertBeside(map.Scenario, heroes[1], heroes[0]);
            }
        }

        // A body coming back looks out of the door too, beside its mate standing there.
        var battle = Battles.Begun(new BattleOptions { Tier = tier, Lives = 2, RespawnWait = 1 });
        var home = battle.BattlePod(0)!;
        var blue = Battles.Hero(battle, Race.Blue);
        Battles.Away(battle, Race.Red, (home.Position + battle.BattlePod(1)!.Position) / 2);
        Battles.Hero(battle, Race.Red).Facing = 1;
        Battles.Kill(battle, Race.Red, Race.Green);
        Battles.Run(battle, 2);
        var red = Battles.Hero(battle, Race.Red);
        Assert.Equal(blue.Facing, red.Facing, 3);
        AssertBeside("respawn", blue, red);
    }

    /// <summary>Asserts that <paramref name="mate"/> stands no further ahead of <paramref name="hero"/>, along the way it looks, than to its side.</summary>
    private static void AssertBeside(string where, Machine hero, Machine mate)
    {
        var look = new Vector2(DetMath.Cos(hero.Facing), DetMath.Sin(hero.Facing));
        var side = new Vector2(-look.Y, look.X);
        var way = mate.Position - hero.Position;
        Assert.True(Vector2.Dot(way, look) <= MathF.Abs(Vector2.Dot(way, side)) + 0.01f,
            $"{where}: {mate.Race} stands {Vector2.Dot(way, look):0.0} m ahead of {hero.Race} and {Vector2.Dot(way, side):0.0} m aside");
    }

    [Fact]
    public void Pods_take_no_damage_cannot_be_attacked_and_nobody_drives_in()
    {
        var world = Battles.Begun();
        var enemyPod = world.BattlePod(1)!;
        var hitPoints = enemyPod.HitPoints;
        world.Damage(enemyPod, 500, Race.Red);
        world.StartBlast(Data.Parm.Weapon("ION_ORBITAL_CANNON"), enemyPod.Position, Race.Red);
        Battles.Run(world, 3);
        Assert.Equal(hitPoints, enemyPod.HitPoints);
        Assert.False(world.CanAttack(Race.Red, enemyPod));
        var red = Battles.Hero(world, Race.Red);
        Assert.Equal("no enemy there", Battles.Do(world, new AttackCommand(Race.Red, [red.Id], enemyPod.Id)));
        var ownPod = world.BattlePod(0)!;
        Assert.False(world.CanEnter(red, ownPod));
        Battles.Do(world, new EnterGarrisonCommand(Race.Red, [red.Id], ownPod.Id));
        Battles.Run(world, 10);
        Assert.Null(red.Inside);
    }

    [Fact]
    public void Treachery_is_refused_and_the_ion_call_waits_ninety_seconds()
    {
        var hero = new BattleHero(Race.Red, "Ace", "knight5",
            [new BattleItem("MULTI_LAUNCHER4", WiredSlot.Weapon(Mounting.Left)), new BattleItem("ionstrike", WiredSlot.Ability(0)), new BattleItem("discharge", WiredSlot.Ability(1))]);
        var options = BattleBots.Compose(new BattleOptions { Tier = 7, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, [hero]);
        var world = Battles.Begun(options);
        var state = world.Wired!.Heroes[Race.Red];
        // Far from every hero: the strike is not what is tested.
        var target = (world.BattlePod(0)!.Position + world.BattlePod(1)!.Position) / 2;
        Assert.Null(Battles.Do(world, new WiredAbilityCommand(Race.Red, 0, target)));
        Assert.Equal("that is not ready yet", world.WhyNoAbility(state, 0, target));
        Assert.Equal(BattleBalance.IonCooldown, state.AbilitySecondsLeft(0, world.Time), 0.1f);
        Battles.Run(world, BattleBalance.IonCooldown);
        Assert.Null(world.WhyNoAbility(state, 0, target));

        // An orb of treachery cannot turn a hero: worn by hand, it is refused.
        state.Loadout = state.Loadout.With(WiredSlot.Ability(1), state.Add(WiredKind.Ability, "treachery", 0));
        Assert.Equal("that cannot turn a hero", world.WhyNoAbility(state, 1, target));
    }
}
