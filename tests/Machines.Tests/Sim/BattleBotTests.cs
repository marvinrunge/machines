using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Ai;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The computer's heroes in a battle: their loadouts and chassis, the mirror rule, how they fight it out, and taking over a leaver's hero.</summary>
public class BattleBotTests
{
    private static readonly Race[] All = Enum.GetValues<Race>();

    [Fact]
    public void Every_chassis_gets_a_loadout_that_fits_at_its_tier()
    {
        var batteries = 0;
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var tier = BattleCatalogue.Tier(chassis.Id);
            Assert.InRange(tier, 1, BattleCatalogue.MostTier);
            var hero = new BattleHero(Race.Red, "Bot", chassis.Id, BattleBots.Loadout(chassis.Id, tier), Bot: true);
            Assert.Null(BattleBots.WhyNot(hero, tier));
            var state = WiredState.ForBattle(Battles.Data, Race.Red, hero, 1);
            var build = HeroBuild.Resolve(Battles.Data, state.Loadout);
            Assert.True(build.Weapons.Count > 0, chassis.Id);
            Assert.True(build.Load <= build.LoadCapacity, $"{chassis.Id} carries {build.Load} of {build.LoadCapacity}");
            Assert.True(build.PowerDraw <= build.PowerSupply, $"{chassis.Id} draws {build.PowerDraw} of {build.PowerSupply}");
            Assert.All(hero.Items, i => Assert.True(i.Mk <= BattleCatalogue.MostMk(tier)));
            Assert.DoesNotContain(hero.Items, i => i.Catalogue == "treachery");
            Assert.Equal(tier >= BattleCatalogue.AbilitiesFrom, hero.Items.Any(i => WiredCatalogue.FindAbility(i.Catalogue) is not null));
            // Every copy of a battery is worn: one entry, its count of weapons in the slot.
            var slotted = hero.Items.Where(i => i.Slot is not null).ToList();
            Assert.Equal(slotted.Sum(i => i.Count), state.Loadout.Worn.Count() - 1);
            var weapons = slotted.Where(i => i.Slot!.Kind == WiredSlotKind.Weapon).Select(i => (Item: i, Weapon: WiredCatalogue.FindWeapon(i.Catalogue)!)).ToList();
            if (tier <= BattleBalance.StartKitTier)
            {
                Assert.All(weapons, w => Assert.Equal(1, w.Item.Count));
                continue;
            }
            // No slot could take one more copy of its weapon on the chassis' own load and power, less the power kept for
            // an ability, where the weapon forms batteries and the slot has room for it.
            var load = weapons.Sum(w => w.Weapon.Load * w.Item.Count);
            var draw = weapons.Sum(w => w.Weapon.Power * w.Item.Count);
            var supply = chassis.Power - BattleBots.AbilityReserve(tier);
            foreach (var (item, weapon) in weapons)
            {
                Assert.True(item.Count <= WiredCatalogue.MostCopies(chassis, (Mounting)item.Slot!.Index), $"{chassis.Id}: {item.Count} of {item.Catalogue}");
                Assert.True(item.Count == 1 || !weapon.Single, $"{chassis.Id}: {item.Count} of {item.Catalogue}, which forms no battery");
                batteries += item.Count > 1 ? 1 : 0;
                if (weapon.Single || item.Count >= WiredCatalogue.MostCopies(chassis, (Mounting)item.Slot.Index))
                    continue;
                Assert.True(load + weapon.Load > chassis.Load || draw + weapon.Power > supply,
                    $"{chassis.Id}: {item.Count} of {item.Catalogue} at {load} of {chassis.Load} load and {draw} of {supply} power take another");
            }
        }
        Assert.True(batteries > 0, "no bot carries a battery");
    }

    [Fact]
    public void Every_chassis_of_a_tier_but_the_constructors_comes_up_as_a_bot_and_the_kth_of_either_team_are_alike()
    {
        for (var tier = 1; tier <= BattleCatalogue.MostTier; tier++)
        {
            var line = BattleCatalogue.OfTier(tier).Where(c => (c.Special & ChassisSpecial.Towers) == 0).Select(c => c.Id).Order(StringComparer.Ordinal).ToList();
            var seen = new SortedSet<string>(StringComparer.Ordinal);
            foreach (var map in BattleCatalogue.Maps)
            {
                var heroes = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = map.TeamA, TeamB = map.TeamB }, [], map.Scenario).Heroes;
                List<string> Line(IReadOnlyList<Race> team) => team.Order().Select(r => heroes.Single(h => h.Race == r).Chassis).ToList();
                var (a, b) = (Line(map.TeamA), Line(map.TeamB));
                // A mirror: the k-th of either team alike, each the k-th of the map's line, and the two of a team differ.
                Assert.Equal(a, b);
                Assert.Equal([BattleBots.Pick(map.Scenario, tier, 0), BattleBots.Pick(map.Scenario, tier, 1)], a);
                if (line.Count > 1)
                    Assert.NotEqual(a[0], a[1]);
                Assert.All(a, c => Assert.Contains(c, line));
                seen.UnionWith(a);
            }
            // Over the maps every chassis of the tier comes up, the constructors never.
            Assert.Equal(line, seen.ToList());
        }
        Assert.NotEqual(BattleBots.Name(Race.Red, 0), BattleBots.Name(Race.Red, 1));
    }

    [Fact]
    public void Bots_of_the_first_and_second_tier_take_both_locators_on_every_map_mirrored()
    {
        foreach (var map in BattleCatalogue.Maps)
        {
            foreach (var (tier, locator, grunt) in new[] { (1, "geo1", "grunt1"), (2, "geo3", "grunt2") })
            {
                var heroes = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = map.TeamA, TeamB = map.TeamB }, [], map.Scenario).Heroes;
                List<string> Line(IReadOnlyList<Race> team) => team.Order().Select(r => heroes.Single(h => h.Race == r).Chassis).ToList();
                Assert.Equal(Line(map.TeamA), Line(map.TeamB));
                Assert.Equal(new[] { locator, grunt }.Order(StringComparer.Ordinal), Line(map.TeamA).Order(StringComparer.Ordinal));
                var geo = heroes.First(h => h.Chassis == locator);
                Assert.Null(BattleBots.WhyNot(geo, tier));
                if (tier <= BattleBalance.StartKitTier)
                    Assert.Equal([new BattleItem(WiredBalance.StartWeapon, WiredSlot.Weapon(Mounting.Top))], geo.Items);
            }
        }
    }

    [Theory]
    [InlineData("grunt5")]
    [InlineData("boss3")]
    public void A_bot_on_a_chassis_with_a_dash_of_its_own_wears_no_thrusters(string chassis)
    {
        Assert.NotNull(WiredCatalogue.FindChassis(chassis)!.Dash);
        for (var tier = BattleCatalogue.Tier(chassis); tier <= BattleCatalogue.MostTier; tier++)
            Assert.DoesNotContain(BattleBots.Loadout(chassis, tier), i => i.Catalogue == "thrusters");
    }

    [Fact]
    public void A_chassis_that_takes_thrusters_takes_none_once_it_has_a_dash_of_its_own()
    {
        // Every chassis without a dash of its own that a bot fits with thrusters at a tier, given a dash, is fitted without them.
        var takers = WiredCatalogue.Chassis.Where(c => c.Dash is null)
            .SelectMany(c => Enumerable.Range(BattleCatalogue.Tier(c.Id), BattleCatalogue.MostTier - BattleCatalogue.Tier(c.Id) + 1).Select(tier => (Chassis: c, Tier: tier)))
            .Where(e => BattleBots.Loadout(e.Chassis, e.Tier).Any(i => i.Catalogue == "thrusters"))
            .ToList();
        Assert.NotEmpty(takers);
        foreach (var (chassis, tier) in takers)
        {
            Assert.Equal(BattleBots.Loadout(chassis.Id, tier), BattleBots.Loadout(chassis, tier));
            Assert.DoesNotContain(BattleBots.Loadout(chassis with { Dash = 10 }, tier), i => i.Catalogue == "thrusters");
        }
    }

    [Fact]
    public void Bots_mirror_the_tier_of_their_counterpart_and_take_the_top_against_a_bot()
    {
        var player = new BattleHero(Race.Green, "Ace", "reaper3", BattleBots.Loadout("reaper3", 4));
        var options = BattleBots.Compose(new BattleOptions { Tier = 5, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, [player]);
        Assert.Equal(All, options.Heroes.Select(h => h.Race));
        Assert.Same(player, options.Heroes.Single(h => h.Race == Race.Green));
        // Red is the first of team A, Green's counterpart: tier IV like Green's. Blue and Yellow face bots: the top, V.
        Assert.Equal(4, BattleCatalogue.Tier(options.Heroes.Single(h => h.Race == Race.Red).Chassis));
        Assert.Equal(5, BattleCatalogue.Tier(options.Heroes.Single(h => h.Race == Race.Blue).Chassis));
        Assert.Equal(5, BattleCatalogue.Tier(options.Heroes.Single(h => h.Race == Race.Yellow).Chassis));
        Assert.All(options.Heroes.Where(h => h.Race != Race.Green), h => Assert.True(h.Bot));
        Assert.All(options.Heroes, h => Assert.Null(BattleBots.WhyNot(h, 5)));
    }

    public static readonly TheoryData<string> Maps = new() { "fortmult", "mgrack", "m_desert", "micey", "m_green" };

    [Theory]
    [MemberData(nameof(Maps))]
    public void Bots_fight_a_battle_out_well_within_the_limit(string map)
    {
        var options = new BattleOptions { Tier = 3 };
        var world = Battles.New(options, map, seed: 5, bots: All);
        var kills = 0;
        while (world.Battle!.Result is null && world.Time < BattleBalance.Opening + options.TimeLimit + 1)
        {
            world.Step();
            kills += world.Events.Count(e => e.Kind == GameEventKind.BattleHeroKilled);
        }
        var result = world.Battle.Result;
        Assert.NotNull(result);
        Assert.NotEqual(BattleEnd.Time, result.Reason);
        Assert.True(result.EndedAt - BattleBalance.Opening < options.TimeLimit * 0.75f, $"{map}: {result.Reason} after {result.EndedAt - BattleBalance.Opening} s");
        Assert.True(kills > 0 || result.Reason == BattleEnd.Capture);
        // They meet and fight: no race of the pods past each other.
        Assert.True(world.Battle.Tally.Values.Sum(t => t.Damage) > 0, $"{map}: nobody was hurt");
    }

    [Theory]
    [InlineData(BattleWin.Both)]
    [InlineData(BattleWin.Wipe)]
    public void With_a_pod_to_take_the_second_of_a_team_guards_its_own_while_the_first_attacks(BattleWin win)
    {
        var world = Battles.New(new BattleOptions { Tier = 3, Win = win }, "micey", seed: 5, bots: All);
        Battles.Run(world, BattleBalance.Opening + 40);
        var run = world.Battle!;
        Assert.Null(run.Result);
        for (var team = 0; team < 2; team++)
        {
            var (attacker, defender) = (Battles.Hero(world, run.Teams[team].Order().First()), Battles.Hero(world, run.Teams[team].Order().Last()));
            var home = world.BattlePod(team)!.Position;
            Assert.True(Vector2.Distance(attacker.Position, home) > 150, $"team {team}'s attacker is {Vector2.Distance(attacker.Position, home)} m from home");
            var guards = Vector2.Distance(defender.Position, home);
            if (win == BattleWin.Wipe)
                Assert.True(guards > 150, $"team {team}'s second stays {guards} m from home with nothing to take");
            else
                Assert.True(guards <= BattleBalance.CaptureRadius + 10, $"team {team}'s second is {guards} m from the pod it guards");
        }
    }

    [Fact]
    public void Twin_battles_of_bots_keep_the_same_checksum()
    {
        GameWorld New() => Battles.New(new BattleOptions { Tier = 5, Lives = 2 }, "fortmult", seed: 11, bots: All);
        var (a, b) = (New(), New());
        Assert.Equal(a.Checksum(), b.Checksum());
        for (var i = 0; i < 4000 && a.Battle!.Result is null; i++)
        {
            a.Step();
            b.Step();
            if (i % 200 == 0)
                Assert.Equal(a.Checksum(), b.Checksum());
        }
        Assert.Equal(a.Checksum(), b.Checksum());
        Assert.True(a.Battle!.Tally.Values.Sum(t => t.Damage) > 0);

        // The battle's own state shows in the checksum.
        a.Battle.Tally[Race.Red].Damage++;
        Assert.NotEqual(a.Checksum(), b.Checksum());
    }

    [Fact]
    public void A_bot_hero_goes_for_the_enemy_and_its_orders_can_go_elsewhere()
    {
        var world = Battles.New(bots: [Race.Red]);
        var red = Battles.Hero(world, Race.Red);
        var start = red.Position;
        Battles.Run(world, BattleBalance.Opening + 20);
        Assert.True(Vector2.Distance(red.Position, start) > 40);
        Assert.True(Vector2.Distance(red.Position, world.BattlePod(1)!.Position) < Vector2.Distance(start, world.BattlePod(1)!.Position));

        // With a hook for its orders it gives them there and not to the world.
        var orders = new List<Command>();
        var bot = new BattleBot(world, Race.Blue, orders.Add);
        bot.Think();
        Assert.NotEmpty(orders);
        Assert.All(orders, o => Assert.Equal(Race.Blue, o.Race));
    }

    [Fact]
    public void A_leaver_s_hero_is_driven_by_a_bot_until_the_player_is_back()
    {
        var world = Battles.Begun();
        var green = Battles.Hero(world, Race.Green);
        Battles.Do(world, new DriveCommand(Race.Green, green.Id, Driving: true, Forward: 1));
        Assert.True(green.DrivenByPlayer);
        world.HandOverToAi(Race.Green);
        Assert.Equal(Controller.Ai, world.Races[Race.Green].Controller);
        Assert.Contains(PlayerId.Of(Race.Green), world.HeroBots.Keys);
        Assert.Contains(PlayerId.Of(Race.Green), world.Battle!.Bots);
        Assert.False(green.DrivenByPlayer);
        Assert.Equal(DefCon.High, green.DefCon);
        var start = green.Position;
        Battles.Run(world, 20);
        Assert.True(Vector2.Distance(green.Position, start) > 30);

        world.HandBackFromAi(Race.Green);
        Assert.Equal(Controller.Player, world.Races[Race.Green].Controller);
        Assert.DoesNotContain(PlayerId.Of(Race.Green), world.HeroBots.Keys);
        Assert.DoesNotContain(PlayerId.Of(Race.Green), world.Battle.Bots);
        Assert.Equal(DefCon.Normal, green.DefCon);
        Assert.IsType<IdleOrder>(green.Order);
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void A_mate_set_down_beside_a_player_who_holds_the_door_gets_out_to_its_post(string map)
    {
        // The player at the door stands still all the while; its mate, a bot, guards the pod from out of the way of the door.
        var entry = BattleCatalogue.Map(map)!;
        var player = entry.TeamA.Order().First();
        var world = Battles.New(new BattleOptions { Tier = 8 }, map, seed: 5, bots: All.Where(r => r != player).ToArray());
        var run = world.Battle!;
        var mate = Battles.Hero(world, run.Teams[0].Order().Last());
        var (start, door) = (mate.Position, world.BattlePod(0)!.EntranceOutside!.Value);
        // A mate set down in the way of the door moves out of it; one already clear of it (a narrow machine) may stay.
        var inTheWay = Vector2.Distance(start, door) < 2 * mate.Radius;
        Battles.Run(world, BattleBalance.Opening + 30);
        Assert.True(!inTheWay || Vector2.Distance(mate.Position, start) > 5, $"{map}: the mate moved {Vector2.Distance(mate.Position, start)} m");
        Assert.IsType<StandGroundOrder>(mate.Order);
        Assert.True(Vector2.Distance(mate.Position, door) >= 2 * mate.Radius, $"{map}: the guard stands {Vector2.Distance(mate.Position, door)} m from the door");
        Assert.True(Vector2.Distance(mate.Position, world.BattlePod(0)!.Position) <= BattleBalance.CaptureRadius + 10);
    }

    [Fact]
    public void A_lone_guard_gone_over_to_the_attack_does_not_walk_back_from_the_other_pod()
    {
        // Only Blue, Red's mate and its team's guard, is a bot; the others stand where they are put.
        var world = Battles.New(new BattleOptions { Tier = 3 }, "micey", seed: 5, bots: [Race.Blue]);
        Battles.Run(world, BattleBalance.Opening + GameWorld.Dt);
        var run = world.Battle!;
        Assert.Equal([Race.Red, Race.Blue], run.Teams[0].Order());
        Battles.Kill(world, Race.Red, Race.Green);
        var (ownPod, enemyPod) = (world.BattlePod(0)!, world.BattlePod(1)!);
        var middle = (ownPod.Position + enemyPod.Position) / 2;
        Battles.Away(world, Race.Green, middle + new Vector2(0, 400));
        Battles.Away(world, Race.Yellow, middle - new Vector2(0, 400));
        // A minute of quiet at its pod with its mate gone, and it goes over to the attack.
        Battles.Run(world, 65);
        var blue = Battles.Hero(world, Race.Blue);
        Assert.True(Vector2.Distance(blue.Position, ownPod.Position) > 60, $"the guard is {Vector2.Distance(blue.Position, ownPod.Position)} m from its pod");
        // Near the other pod by now, while an enemy steps into its own circle far behind it.
        blue.Position = world.Nav.NearestFreePoint(enemyPod.Position + Vector2.Normalize(ownPod.Position - enemyPod.Position) * 90, crossesWater: false)!.Value;
        blue.Path.Clear();
        Battles.IntoCircle(world, Race.Green);
        Battles.Run(world, 5);
        Assert.False(blue.Order is AttackOrder attack && attack.Target == Battles.Hero(world, Race.Green).Id, "it turned back for the intruder");
        Assert.True(Vector2.Distance(blue.Position, enemyPod.Position) < 90, $"it is {Vector2.Distance(blue.Position, enemyPod.Position)} m from the pod it goes for");
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void Heroes_are_set_down_with_their_whole_breadth_on_open_ground(string map)
    {
        void AssertOpen(GameWorld world, Machine hero, string when)
        {
            for (var k = 0; k < 8; k++)
            {
                var edge = hero.Position + new Vector2(DetMath.Cos(k * MathF.Tau / 8), DetMath.Sin(k * MathF.Tau / 8)) * hero.Radius * 0.9f;
                Assert.True(world.Nav.IsFree(edge, crossesWater: false), $"{map} {when}: {hero.Race}'s edge {edge} is not open ground");
            }
        }
        foreach (var tier in new[] { 1, 8 })
        {
            var world = Battles.New(new BattleOptions { Tier = tier, Lives = 3 }, map, seed: 5);
            var run = world.Battle!;
            foreach (var race in run.Teams.SelectMany(t => t))
                AssertOpen(world, Battles.Hero(world, race), $"tier {tier} start");
            Battles.Run(world, BattleBalance.Opening + GameWorld.Dt);
            // Both of a team fall while both enemies stand at and beside their door: they come back round them.
            for (var team = 0; team < 2; team++)
            {
                var door = world.BattlePod(team)!.EntranceOutside!.Value;
                var enemies = run.Teams[1 - team].Order().ToList();
                Battles.Hero(world, enemies[0]).Position = door;
                Battles.Away(world, enemies[1], door + new Vector2(0, 14));
                foreach (var race in run.Teams[team].Order())
                    Battles.Kill(world, race, enemies[0]);
                Battles.Run(world, run.Options.RespawnWait + 1);
                foreach (var race in run.Teams[team])
                    AssertOpen(world, Battles.Hero(world, race), $"tier {tier} respawn of team {team}");
            }
        }
    }

    [Theory]
    [MemberData(nameof(Maps))]
    public void A_hero_coming_back_at_its_pod_looks_out_of_the_door_with_no_mate_before_it(string map)
    {
        foreach (var tier in new[] { 1, 8 })
        {
            var world = Battles.New(new BattleOptions { Tier = tier, Lives = 3 }, map, seed: 5);
            var run = world.Battle!;
            Battles.Run(world, BattleBalance.Opening + GameWorld.Dt);
            var (player, mate) = (run.Teams[0].Order().First(), run.Teams[0].Order().Last());
            var pod = world.BattlePod(0)!;
            var door = pod.EntranceOutside!.Value;
            var outward = Vector2.Normalize(door - pod.Position);
            // The mate guards 15 m straight out of the door, where the player would come back looking at its back.
            var guard = Battles.Hero(world, mate);
            guard.Position = world.Nav.NearestFreePoint(door + outward * 15, crossesWater: false)!.Value;
            guard.Path.Clear();
            Battles.Kill(world, player, run.Teams[1].Order().First());
            Battles.Run(world, run.Options.RespawnWait + 1);
            var back = Battles.Hero(world, player);
            var ahead = new Vector2(DetMath.Cos(back.Facing), DetMath.Sin(back.Facing));
            Assert.True(Vector2.Dot(ahead, outward) > 0.99f, $"{map} tier {tier}: it looks {back.Facing}, the door out {DetMath.Atan2(outward.Y, outward.X)}");
            var way = guard.Position - back.Position;
            var along = Vector2.Dot(way, ahead);
            var aside = MathF.Abs(way.X * ahead.Y - way.Y * ahead.X);
            Assert.True(along <= 0 || aside >= back.Radius + guard.Radius, $"{map} tier {tier}: the mate stands {along} m ahead and {aside} m aside");
        }
    }

    [Fact]
    public void A_team_mate_the_computer_drives_makes_way_for_a_player_at_the_wheel_who_runs_into_it()
    {
        // Blue stands its ground on Red's way out (a guard at its post); Red drives straight at it.
        var world = Battles.New(new BattleOptions { Tier = 4 }, "m_desert", seed: 5);
        Battles.Run(world, BattleBalance.Opening + GameWorld.Dt);
        var (red, blue) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Blue));
        blue.DrivenByPlayer = false;
        var pod = world.BattlePod(0)!;
        var north = new Vector2(0, 1);
        Battles.Away(world, Race.Red, pod.Position + north * 80);
        Battles.Away(world, Race.Blue, red.Position + north * (red.Radius + blue.Radius + 3));
        world.Issue(new StandGroundCommand(Race.Blue, [blue.Id]));
        red.Facing = MathF.PI / 2;
        var start = red.Position;
        for (var tick = 0; tick < 8 / GameWorld.Dt; tick++)
        {
            world.Issue(new DriveCommand(Race.Red, red.Id, Driving: true, Forward: 1));
            world.Step();
        }
        Assert.True(Vector2.Distance(red.Position, start) > 2 * (red.Radius + blue.Radius) + 3, $"Red got {Vector2.Distance(red.Position, start)} m");

        // An enemy in the way does not make way.
        var other = Battles.New(new BattleOptions { Tier = 4 }, "m_desert", seed: 5);
        Battles.Run(other, BattleBalance.Opening + GameWorld.Dt);
        var (driver, enemy) = (Battles.Hero(other, Race.Red), Battles.Hero(other, Race.Green));
        enemy.DrivenByPlayer = false;
        Battles.Away(other, Race.Red, pod.Position + north * 80);
        Battles.Away(other, Race.Green, driver.Position + north * (driver.Radius + enemy.Radius + 3));
        var stands = enemy.Position;
        driver.Facing = MathF.PI / 2;
        for (var tick = 0; tick < 4 / GameWorld.Dt; tick++)
        {
            other.Issue(new DriveCommand(Race.Red, driver.Id, Driving: true, Forward: 1));
            other.Step();
        }
        Assert.True(Vector2.Distance(enemy.Position, stands) < 1);
    }

    // ---- what the chassis can do ------------------------------------------------------------------

    /// <summary>
    /// A battle on m_desert past its opening at the tier of <paramref name="chassis"/>, Red's hero on it, and Green's on
    /// <paramref name="green"/> if given; every hero stands still where it is put. Red stands in the middle of the field and
    /// Yellow far off, out of every reach that counts here.
    /// </summary>
    private static GameWorld BattleWith(string chassis, string? green = null)
    {
        var tier = BattleCatalogue.Tier(chassis);
        List<BattleHero> given = [new BattleHero(Race.Red, "Ace", chassis, BattleBots.Loadout(chassis, tier))];
        if (green is not null)
            given.Add(new BattleHero(Race.Green, "Foe", green, BattleBots.Loadout(green, BattleCatalogue.Tier(green))));
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, given);
        var world = Battles.Begun(options);
        var (home, away) = (world.BattlePod(0)!.Position, world.BattlePod(1)!.Position);
        Battles.Away(world, Race.Red, (home + away) / 2);
        var red = Battles.Hero(world, Race.Red);
        red.Path.Clear();
        Battles.Away(world, Race.Yellow, away + Vector2.Normalize(away - home) * 60);
        Assert.True(Vector2.Distance(Battles.Hero(world, Race.Yellow).Position, red.Position) > 200);
        return world;
    }

    /// <summary>The first free place that far from <paramref name="from"/>, by 32 headings round from the first, with <paramref name="sight"/> in sight of it, and its heading.</summary>
    private static (Vector2 Place, float Heading) Beside(GameWorld world, Vector2 from, float metres, bool sight = true)
    {
        for (var k = 0; k < 32; k++)
        {
            var heading = k * MathF.Tau / 32;
            var place = from + new Vector2(DetMath.Cos(heading), DetMath.Sin(heading)) * metres;
            if (world.Nav.IsFree(place, crossesWater: false) && (!sight || world.Nav.LineOfSight(from, place, false)))
                return (place, heading);
        }
        throw new InvalidOperationException($"nothing free {metres} m from {from}");
    }

    /// <summary>What a bot new to the battle would order for <paramref name="race"/> now.</summary>
    private static List<Command> Think(GameWorld world, Race race)
    {
        var commands = new List<Command>();
        new BattleBot(world, race, commands.Add).Think();
        return commands;
    }

    private static void Put(Machine machine, Vector2 place)
    {
        machine.Position = place;
        machine.Path.Clear();
    }

    [Fact]
    public void An_old_iron_bot_rams_an_enemy_close_in_front_and_nothing_further_off_or_behind()
    {
        var world = BattleWith("grunt5");
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        var (far, farHeading) = Beside(world, red.Position, 30);
        Put(green, far);
        red.Facing = farHeading;
        var commands = Think(world, Race.Red);
        Assert.DoesNotContain(commands, c => c is WiredDashCommand);
        Assert.Contains(commands, c => c is AttackCommand attack && attack.Target == green.Id);

        var (near, heading) = Beside(world, red.Position, 12);
        Put(green, near);
        red.Facing = heading + MathF.PI;
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredDashCommand);

        red.Facing = heading;
        var ram = Assert.Single(Think(world, Race.Red).OfType<WiredDashCommand>());
        Assert.True(Vector2.Dot(ram.Direction, Vector2.Normalize(near - red.Position)) > 0.999f);
        var events = new List<GameEvent>();
        world.Issue(ram);
        events.AddRange(Battles.Run(world, 1));
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredRammed && e.Actor == green.Id && e.Other == red.Id);

        // Its dash is spent: no second ram until it is ready again.
        Put(green, red.Position + new Vector2(DetMath.Cos(red.Facing), DetMath.Sin(red.Facing)) * 12);
        Assert.NotNull(world.WhyNoDash(world.Wired!.Heroes[Race.Red]));
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredDashCommand);
    }

    [Fact]
    public void A_colossus_bot_anchors_where_it_holds_and_lets_go_of_its_tracks_before_it_moves_on()
    {
        var world = BattleWith("ballista5");
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        var (home, away) = (world.BattlePod(0)!, world.BattlePod(1)!);
        // Red, the first of its team, holds the enemy pod's circle; the enemies are off at Red's own pod.
        Battles.Away(world, Race.Green, home.Position + Vector2.Normalize(home.Position - away.Position) * 40);
        Battles.Away(world, Race.Yellow, home.Position + Vector2.Normalize(home.Position - away.Position) * 60);
        Battles.IntoCircle(world, Race.Red);
        red.Path.Clear();
        Assert.True(Vector2.Distance(green.Position, red.Position) > 150);

        var commands = Think(world, Race.Red);
        Assert.Contains(commands, c => c is StandGroundCommand);
        var anchor = Assert.Single(commands.OfType<WiredSpecialCommand>());
        Assert.True(anchor.On);
        Assert.Null(Battles.Do(world, anchor));
        Battles.Run(world, WiredBalance.AnchorTime + 0.2f);
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(red));
        // Anchored where it holds, it asks for nothing more of its tracks.
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand or MoveCommand);

        // Set down away from its place, it lets go first and only then sets off.
        Put(red, world.Nav.NearestFreePoint(away.Position + Vector2.Normalize(home.Position - away.Position) * 70, crossesWater: false)!.Value);
        commands = Think(world, Race.Red);
        Assert.DoesNotContain(commands, c => c is MoveCommand);
        var release = Assert.Single(commands.OfType<WiredSpecialCommand>());
        Assert.False(release.On);
        Assert.Null(Battles.Do(world, release));
        Assert.Equal(AnchorPhase.Releasing, world.AnchorPhaseOf(red));
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand or MoveCommand);
        Battles.Run(world, WiredBalance.AnchorTime + 0.2f);
        Assert.Equal(AnchorPhase.Free, world.AnchorPhaseOf(red));
        commands = Think(world, Race.Red);
        Assert.Contains(commands, c => c is MoveCommand);
        Assert.DoesNotContain(commands, c => c is WiredSpecialCommand);
    }

    [Fact]
    public void A_spy_bot_lays_a_mine_under_itself_once_an_enemy_hero_on_the_ground_is_within_15_m()
    {
        var world = BattleWith("spy3");
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        Put(green, Beside(world, red.Position, 25).Place);
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand);

        Put(green, Beside(world, red.Position, 10).Place);
        var mine = Assert.Single(Think(world, Race.Red).OfType<WiredSpecialCommand>());
        world.Issue(mine);
        world.Step();
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.MineLaid && e.Actor == red.Id);
        Assert.True(Vector2.Distance(Assert.Single(world.LandMines).Position, red.Position) < 2);
    }

    [Fact]
    public void A_spy_bot_guarding_its_pod_lays_a_mine_on_the_approach_every_10_s()
    {
        // Blue, the second of team A, guards the pod while Red, its mate, stands at the wheel; the enemies are at theirs.
        var tier = BattleCatalogue.Tier("spy5");
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Blue, "Ace", "spy5", BattleBots.Loadout("spy5", tier))]);
        var world = Battles.Begun(options, "m_desert", Race.Blue);
        var (pod, enemyPod) = (world.BattlePod(0)!, world.BattlePod(1)!);
        var blue = Battles.Hero(world, Race.Blue);
        Assert.True(Vector2.Distance(pod.Position, enemyPod.Position) > 200);
        var laid = new List<(float Time, Vector2 Place)>();
        for (var tick = 0; tick < 36 / GameWorld.Dt; tick++)
        {
            world.Step();
            laid.AddRange(world.Events.Where(e => e.Kind == GameEventKind.MineLaid && e.Actor == blue.Id).Select(e => (world.Time, e.Position)));
        }
        Assert.InRange(laid.Count, 3, 4);
        for (var i = 1; i < laid.Count; i++)
        {
            Assert.True(laid[i].Time - laid[i - 1].Time >= 10 - GameWorld.Dt, $"mine {i} came {laid[i].Time - laid[i - 1].Time} s after the one before");
            Assert.All(laid.Take(i), before => Assert.True(Vector2.Distance(before.Place, laid[i].Place) >= 4, $"mine {i} lies on another"));
        }
        // On the pod's approach, the side the enemy comes from.
        var towards = Vector2.Normalize(enemyPod.Position - pod.Position);
        Assert.All(laid, mine =>
        {
            Assert.InRange(Vector2.Distance(mine.Place, pod.Position), 24, 32);
            Assert.True(Vector2.Dot(Vector2.Normalize(mine.Place - pod.Position), towards) > 0.5f, $"a mine at {mine.Place} is not on the approach");
        });
        // Between its mines it guards the pod.
        Assert.True(Vector2.Distance(blue.Position, pod.Position) <= BattleBalance.CaptureRadius + 10);
    }

    [Fact]
    public void A_spy_bot_lays_no_mine_on_another_nor_where_its_own_fire_would_set_it_off()
    {
        var world = BattleWith("spy5", green: "spy5");
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        Assert.NotEqual(Locomotion.Glider, green.Stats.Locomotion);
        var state = world.Wired!.Heroes[Race.Red];
        // Its abilities kept back for now: what it does with its mines alone.
        var abilities = state.Loadout.Abilities.OfType<WiredItem>().ToList();
        Assert.Contains(abilities, a => a.Catalogue == "vortex");
        foreach (var ability in abilities)
            state.AbilityReadyAt[ability.Id] = float.MaxValue;
        Put(green, Beside(world, red.Position, 12).Place);
        // Standing where it is with an enemy close by, it lays one mine and then no more there.
        for (var round = 0; round < 6; round++)
        {
            foreach (var command in Think(world, Race.Red).OfType<WiredSpecialCommand>())
                world.Issue(command);
            Battles.Run(world, WiredBalance.MineLayGap + 0.2f);
        }
        Assert.Single(world.LandMines);
        Assert.Null(world.WhyNoMine(state));
        // Moved on past it, it lays the next.
        Put(red, Beside(world, red.Position, 8).Place);
        Put(green, Beside(world, red.Position, 12).Place);
        Assert.Single(Think(world, Race.Red).OfType<WiredSpecialCommand>());

        // An enemy within twice the burst of its rounds: their bursts on it would set a mine here off.
        Put(green, Beside(world, red.Position, 5).Place);
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand);

        // Nor where a blast of its own reaches, running or set off in the same look.
        Put(green, Beside(world, red.Position, 12).Place);
        world.Blasts.Add(new Blast("VORTEX", green.Position, Race.Red, world.Time, Blast.WavesFor(world.Data.Parm.Weapon("VORTEX"))));
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand);
        world.Blasts.Clear();
        Assert.Single(Think(world, Race.Red).OfType<WiredSpecialCommand>());
        foreach (var ability in abilities)
            state.AbilityReadyAt[ability.Id] = world.Time;
        var commands = Think(world, Race.Red);
        Assert.Contains(commands, c => c is WiredAbilityCommand use && state.Loadout.Abilities[use.Slot]!.Catalogue == "vortex");
        Assert.DoesNotContain(commands, c => c is WiredSpecialCommand);
    }

    [Fact]
    public void A_phantom_bot_whose_launchers_burst_wide_lays_no_mine_under_a_close_fight()
    {
        // Its Multi Launchers' bursts set off a mine 20 m from where they hit.
        var world = BattleWith("phantom6", green: "phantom6");
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        Assert.All(red.Weapons, w => Assert.StartsWith("MULTI_LAUNCHER", w.Spec.TypeName));
        Assert.NotEqual(Locomotion.Glider, green.Stats.Locomotion);
        var state = world.Wired!.Heroes[Race.Red];
        Assert.Null(world.WhyNoMine(state));
        foreach (var ability in state.Loadout.Abilities.OfType<WiredItem>())
            state.AbilityReadyAt[ability.Id] = float.MaxValue;
        Put(green, Beside(world, red.Position, 12).Place);
        var commands = Think(world, Race.Red);
        Assert.DoesNotContain(commands, c => c is WiredSpecialCommand);
        Assert.Contains(commands, c => c is AttackCommand attack && attack.Target == green.Id);
    }

    [Fact]
    public void A_marshal_bot_guarding_its_pod_stays_at_it_while_its_mate_is_out_in_the_field()
    {
        // Blue, the second of team A, guards; Red, its mate, stands in the middle of the field, the enemies at their pod.
        var tier = BattleCatalogue.Tier("commander6");
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Blue, "Ace", "commander6", BattleBots.Loadout("commander6", tier))]);
        var world = Battles.Begun(options, "m_desert", Race.Blue);
        var (pod, enemyPod) = (world.BattlePod(0)!, world.BattlePod(1)!);
        Battles.Away(world, Race.Red, (pod.Position + enemyPod.Position) / 2);
        Assert.True(Vector2.Distance(Battles.Hero(world, Race.Red).Position, pod.Position) > 125);
        Battles.Run(world, 30);
        var blue = Battles.Hero(world, Race.Blue);
        Assert.True(Vector2.Distance(blue.Position, pod.Position) <= BattleBalance.CaptureRadius + 10, $"the Marshal is {Vector2.Distance(blue.Position, pod.Position)} m from the pod it guards");
    }

    [Fact]
    public void A_marshal_bot_on_the_attack_keeps_by_a_mate_out_in_the_field_and_not_by_its_guard()
    {
        // Red, the first of team A, attacks on a Marshal; Blue, its guard, stands where it is put.
        var tier = BattleCatalogue.Tier("commander6");
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Ace", "commander6", BattleBots.Loadout("commander6", tier))]);
        var world = Battles.Begun(options);
        var (pod, enemyPod) = (world.BattlePod(0)!, world.BattlePod(1)!);
        var towards = Vector2.Normalize(enemyPod.Position - pod.Position);
        var aside = new Vector2(-towards.Y, towards.X);
        var blue = Battles.Hero(world, Race.Blue);
        blue.Path.Clear();
        bool GoesTo(List<Command> commands, Func<Vector2, bool> where) => commands.Exists(c => c is MoveCommand move && where(move.Target));

        // Its guard 70 m out of the pod, after an intruder: it goes on for the enemy pod.
        Battles.Away(world, Race.Blue, pod.Position + aside * 70);
        var commands = Think(world, Race.Red);
        Assert.False(GoesTo(commands, t => Vector2.Distance(t, blue.Position) < 1));
        Assert.True(GoesTo(commands, t => Vector2.Distance(t, enemyPod.Position) <= BattleBalance.CaptureRadius));

        // Its mate out in the field, past where a guard goes: it goes to it.
        Battles.Away(world, Race.Blue, pod.Position + towards * 150);
        Assert.True(Vector2.Distance(blue.Position, pod.Position) > 125 && Vector2.Distance(blue.Position, enemyPod.Position) > BattleBalance.CaptureRadius + 20);
        Assert.True(GoesTo(Think(world, Race.Red), t => Vector2.Distance(t, blue.Position) < 1));
    }

    [Fact]
    public void A_geo_bot_sends_its_pulse_at_an_enemy_hero_in_its_scanner_s_reach_and_not_at_one_beyond_it()
    {
        var world = BattleWith("geo1");
        var (red, green) = (Battles.Hero(world, Race.Red), Battles.Hero(world, Race.Green));
        var reach = world.ScannerReach(red);
        Put(green, world.Nav.NearestFreePoint(red.Position + Vector2.Normalize(green.Position - red.Position) * (reach + 30), crossesWater: false)!.Value);
        Assert.True(Vector2.Distance(green.Position, red.Position) > reach);
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand);

        Put(green, Beside(world, red.Position, reach - 30, sight: false).Place);
        // Located by a team-mate's pulse for longer than 2 s yet, it asks for none; with 2 s or less left, it does.
        world.Wired!.Located[green.Id] = world.Time + 5;
        Assert.Null(world.WhyNoLocate(world.Wired.Heroes[Race.Red]));
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand);
        world.Wired.Located[green.Id] = world.Time + 1.5f;
        var pulse = Assert.Single(Think(world, Race.Red).OfType<WiredSpecialCommand>());
        Assert.Null(Battles.Do(world, pulse));
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.WiredLocated && e.Actor == red.Id);
        Assert.True(world.LocatedFor(green.Id) > WiredBalance.LocateTime - 1);
        // Its pulse spent, it asks for none.
        Assert.DoesNotContain(Think(world, Race.Red), c => c is WiredSpecialCommand);
    }
}
