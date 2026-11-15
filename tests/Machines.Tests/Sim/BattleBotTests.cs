using System.Numerics;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Ai;
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
            Assert.Equal(hero.Items.Count(i => i.Slot is not null), state.Loadout.Worn.Count() - 1);
        }
    }

    [Fact]
    public void Two_bots_of_a_team_differ_and_the_kth_of_either_team_are_alike()
    {
        for (var tier = 1; tier <= BattleCatalogue.MostTier; tier++)
        {
            Assert.Equal(tier, BattleCatalogue.Tier(BattleBots.Pick(tier, Race.Red, 0)));
            Assert.Equal(BattleBots.Pick(tier, Race.Red, 1), BattleBots.Pick(tier, Race.Yellow, 1));
            if (BattleCatalogue.OfTier(tier).Count > 1)
                Assert.NotEqual(BattleBots.Pick(tier, Race.Red, 0), BattleBots.Pick(tier, Race.Blue, 1));
        }
        Assert.NotEqual(BattleBots.Name(Race.Red, 0), BattleBots.Name(Race.Red, 1));
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
        Assert.Contains(Race.Green, world.HeroBots.Keys);
        Assert.Contains(Race.Green, world.Battle!.Bots);
        Assert.False(green.DrivenByPlayer);
        Assert.Equal(DefCon.High, green.DefCon);
        var start = green.Position;
        Battles.Run(world, 20);
        Assert.True(Vector2.Distance(green.Position, start) > 30);

        world.HandBackFromAi(Race.Green);
        Assert.Equal(Controller.Player, world.Races[Race.Green].Controller);
        Assert.DoesNotContain(Race.Green, world.HeroBots.Keys);
        Assert.DoesNotContain(Race.Green, world.Battle.Bots);
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
        Battles.Run(world, BattleBalance.Opening + 30);
        Assert.True(Vector2.Distance(mate.Position, start) > 5, $"{map}: the mate moved {Vector2.Distance(mate.Position, start)} m");
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
}
