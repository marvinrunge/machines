using Machines.Game;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>What the HUD of a battle reads off the world: the clock and the opening, both teams, the capture, lives and the end.</summary>
public class BattleReadoutTests
{
    private static BattleReadout Of(GameWorld world, Race race) => BattleReadout.Of(world, PlayerId.Of(race))!;

    [Fact]
    public void A_hero_with_an_empty_name_is_called_by_its_race()
    {
        var composed = BattleBots.Compose(new BattleOptions { TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, []);
        var options = composed with
        {
            Heroes = composed.Heroes.Select(h => h.Race == Race.Red ? h with { Name = "" } : h).ToList(),
        };
        var world = Battles.New(options);

        Assert.Equal("Red", BattleReadout.HeroName(world.Battle!, PlayerId.Of(Race.Red)));
        Assert.Equal("Red", Of(world, Race.Red).You.Name);
        var blue = world.Battle!.Options.Heroes.Single(h => h.Race == Race.Blue).Name;
        Assert.NotEmpty(blue);
        Assert.Equal(blue, BattleReadout.HeroName(world.Battle, PlayerId.Of(Race.Blue)));
    }

    [Theory]
    [InlineData("phantom6", ChassisSpecial.Mines | ChassisSpecial.Shadow)]
    [InlineData("geo1", ChassisSpecial.Locate)]
    [InlineData("ballista5", ChassisSpecial.Anchor)]
    [InlineData("commander6", ChassisSpecial.Field)]
    public void The_special_of_the_heros_chassis_is_shown_as_the_world_has_it(string chassis, ChassisSpecial special)
    {
        var tier = BattleCatalogue.Tier(chassis);
        var options = BattleBots.Compose(new BattleOptions { Tier = tier, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Ace", chassis, BattleBots.Loadout(chassis, tier))]);
        var world = Battles.New(options);
        Assert.Equal(chassis, world.Wired!.Heroes[Race.Red].Loadout.Chassis.Catalogue);

        var shown = Of(world, Race.Red).SpecialView;

        Assert.NotNull(shown);
        Assert.Equal(world.SpecialView(Race.Red), shown);
        Assert.Equal(special, shown!.Special);
        Battles.Run(world, BattleBalance.Opening + 1);
        Assert.Equal(world.SpecialView(Race.Red), Of(world, Race.Red).SpecialView);
    }

    [Fact]
    public void A_hero_on_a_chassis_without_a_special_shows_none()
    {
        var options = BattleBots.Compose(new BattleOptions { Tier = 1, TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] },
            [new BattleHero(Race.Red, "Ace", "grunt1", BattleBots.Loadout("grunt1", 1))]);
        var world = Battles.New(options);

        Assert.Null(Of(world, Race.Red).SpecialView);
    }

    [Fact]
    public void There_is_none_outside_a_battle_or_for_a_race_in_no_team()
    {
        var players = Enum.GetValues<Race>().ToDictionary(r => r, _ => Controller.Player);
        var normal = MatchSetup.Create(Battles.Data, "m_desert", "m_desert", players, 7, new MatchOptions());
        var wired = MatchSetup.Create(Battles.Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai },
            7, new MatchOptions { Wired = new WiredOptions { Arena = "m_desert" } });
        Assert.Null(BattleReadout.Of(normal, PlayerId.Of(Race.Red)));
        Assert.Null(BattleReadout.Of(wired, PlayerId.Of(Race.Red)));
        var pair = Battles.New(new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Green] });
        Assert.Null(BattleReadout.Of(pair, PlayerId.Of(Race.Blue)));
        Assert.NotNull(BattleReadout.Of(pair, PlayerId.Of(Race.Green)));
    }

    [Fact]
    public void The_opening_counts_down_and_the_clock_runs_after_it()
    {
        var world = Battles.New(new BattleOptions { TimeLimit = 300 });
        Assert.Equal((int)BattleBalance.Opening, Of(world, Race.Red).Opening);
        Assert.Equal(300, Of(world, Race.Red).Seconds);
        Battles.Run(world, 3.5f);
        Assert.Equal(7, Of(world, Race.Red).Opening);
        Assert.Equal(300, Of(world, Race.Red).Seconds);
        Battles.Run(world, BattleBalance.Opening - 3.5f + 20.5f);
        var now = Of(world, Race.Red);
        Assert.Null(now.Opening);
        Assert.Equal(280, now.Seconds);
        Assert.Equal(300, now.TimeLimit);
        Assert.Null(now.Outcome);
    }

    [Fact]
    public void Each_race_sees_its_own_team_first_with_names_chassis_and_tiers()
    {
        var world = Battles.New(new BattleOptions { Tier = 3 });
        var red = Of(world, Race.Red);
        Assert.True(red.Ours.Ours);
        Assert.False(red.Theirs.Ours);
        Assert.Equal([Race.Red, Race.Blue], red.Ours.Heroes.Select(h => h.Race));
        Assert.Equal([Race.Green, Race.Yellow], red.Theirs.Heroes.Select(h => h.Race));
        Assert.Equal(Race.Red, red.You.Race);
        Assert.True(red.You.You);
        Assert.False(red.Ours.Heroes[1].You);
        foreach (var hero in red.Ours.Heroes.Concat(red.Theirs.Heroes))
        {
            var sheet = world.Battle!.Options.Heroes.Single(h => h.Race == hero.Race);
            Assert.Equal(sheet.Name, hero.Name);
            Assert.Equal(sheet.Chassis, hero.Chassis);
            Assert.Equal(BattleCatalogue.Tier(sheet.Chassis), hero.Tier);
            Assert.InRange(hero.Tier, 2, 3);
            Assert.Equal(1, hero.Health);
            Assert.True(hero.Standing);
        }
        var green = Of(world, Race.Green);
        Assert.Equal(1, green.Ours.Team);
        Assert.Equal([Race.Green, Race.Yellow], green.Ours.Heroes.Select(h => h.Race));
        Assert.True(red.Capture);
        Assert.False(Of(Battles.New(new BattleOptions { Win = BattleWin.Wipe }), Race.Red).Capture);
    }

    [Fact]
    public void The_capture_of_both_pods_shows_from_either_side()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        Battles.Run(world, 10);
        var red = Of(world, Race.Red);
        Assert.Equal(10, red.Ours.Progress, 0.2f);
        Assert.True(red.Ours.Capturing);
        Assert.Equal(0, red.Theirs.Progress);
        Assert.False(red.Theirs.Capturing);
        var green = Of(world, Race.Green);
        Assert.Equal(10, green.Theirs.Progress, 0.2f);
        Assert.True(green.Theirs.Capturing);
        Assert.True(red.Ours.PodStands && red.Theirs.PodStands);
        Assert.True(red.EnemyPodDistance <= BattleBalance.CaptureRadius);
        Assert.True(red.PodDistance > red.EnemyPodDistance);
    }

    [Fact]
    public void A_hero_down_with_lives_left_waits_for_its_body_and_its_life_still_counts()
    {
        var world = Battles.Begun(new BattleOptions { Lives = 3, RespawnWait = 10 });
        var start = Of(world, Race.Red).You;
        Assert.Equal(3, start.Lives);
        Assert.Equal(3, start.LivesLeft);
        Battles.Kill(world, Race.Red, Race.Green);
        Battles.Run(world, 2);
        var down = Of(world, Race.Red);
        Assert.False(down.You.Standing);
        Assert.False(down.Out);
        Assert.Equal(8, down.DownSeconds!.Value, 0.2f);
        // The body it comes back in is one of its lives: it counts until it is gone.
        Assert.Equal(2, down.You.LivesLeft);
        Assert.Equal(1, Of(world, Race.Green).You.Kills);
        Battles.Run(world, 8.5f);
        var back = Of(world, Race.Red);
        Assert.True(back.You.Standing);
        Assert.Null(back.DownSeconds);
        Assert.Equal(2, back.You.LivesLeft);
    }

    [Fact]
    public void A_hero_that_falls_for_the_last_time_is_out_and_looks_on_through_its_team_mate()
    {
        var world = Battles.Begun();
        Assert.Equal([PlayerId.Of(Race.Blue)], Of(world, Race.Red).Watchable);
        Battles.Kill(world, Race.Red, Race.Green);
        Battles.Run(world, 1);
        var red = Of(world, Race.Red);
        Assert.True(red.Out);
        Assert.Null(red.DownSeconds);
        Assert.Equal(0, red.You.LivesLeft);
        Assert.Equal(0, red.You.Health);
        Assert.True(red.Ours.InTheFight);
        Assert.Equal([PlayerId.Of(Race.Blue)], red.Watchable);
        Assert.Null(red.Outcome);
        Assert.Null(red.PodDistance);
        Assert.Empty(Of(world, Race.Blue).Watchable);
    }

    [Fact]
    public void The_end_says_who_won_lost_or_drew_and_why()
    {
        var wiped = Battles.Begun();
        Battles.Kill(wiped, Race.Green, Race.Red);
        Battles.Kill(wiped, Race.Yellow, Race.Blue);
        Battles.Run(wiped, 1);
        Assert.Equal(BattleOutcome.Won, Of(wiped, Race.Red).Outcome);
        Assert.Equal(BattleOutcome.Won, Of(wiped, Race.Blue).Outcome);
        Assert.Equal(BattleOutcome.Lost, Of(wiped, Race.Green).Outcome);
        Assert.Equal(BattleEnd.Wipe, Of(wiped, Race.Yellow).Reason);
        Assert.False(Of(wiped, Race.Green).Ours.InTheFight);

        var timed = Battles.Begun(new BattleOptions { TimeLimit = 2 });
        Battles.Run(timed, 2.5f);
        Assert.Equal(BattleOutcome.Draw, Of(timed, Race.Red).Outcome);
        Assert.Equal(BattleOutcome.Draw, Of(timed, Race.Green).Outcome);
        Assert.Equal(BattleEnd.Time, Of(timed, Race.Red).Reason);
        Assert.Equal(0, Of(timed, Race.Red).Seconds);
    }

    [Fact]
    public void The_ion_call_counts_its_cooldown_in_seconds_and_the_others_are_ready_at_the_start()
    {
        var world = Battles.Begun(new BattleOptions { Tier = 7 });
        var (race, slot) = Enum.GetValues<Race>()
            .SelectMany(r => Of(world, r).Abilities.Select(a => (Race: r, Ability: a)))
            .Where(p => p.Ability.Catalogue == "ionstrike").Select(p => (p.Race, p.Ability.Slot)).First();
        Assert.All(Of(world, race).Abilities, a => Assert.True(a.Ready && a.Share == 1));
        var enemy = world.HeroOf(Of(world, race).Theirs.Heroes[0].Race)!;
        Assert.Null(Battles.Do(world, new WiredAbilityCommand(race, slot, enemy.Position)));
        var ion = Of(world, race).Abilities.Single(a => a.Slot == slot);
        Assert.False(ion.Ready);
        Assert.Equal(0, ion.WavesLeft);
        Assert.Equal(BattleBalance.IonCooldown, ion.SecondsLeft, 0.2f);
        Assert.InRange(ion.Share, 0, 0.01f);
        Battles.Run(world, BattleBalance.IonCooldown / 2);
        Assert.InRange(Of(world, race).Abilities.Single(a => a.Slot == slot).Share, 0.45f, 0.55f);
    }

    // ---- five against five (P1 design §6.4) -----------------------------------------------------------

    /// <summary>The players of the Desert's two teams in a five against five, in their places.</summary>
    private static readonly PlayerId[] Reds = [new(0), new(4), new(5), new(6), new(7)], Greens = [new(2), new(8), new(9), new(10), new(11)];

    /// <summary>A five against five on the Desert, <paramref name="humans"/> people (named "Seat n") and the computer's bots in the other seats.</summary>
    private static GameWorld FiveAgainstFive(params PlayerId[] humans)
    {
        var entry = BattleCatalogue.Map("m_desert")!;
        var options = new BattleOptions { Tier = 3, TeamSize = 5, TeamA = entry.TeamA, TeamB = entry.TeamB };
        var rows = BattleSeats.Of(options).Select(seat => new PlayerSeat(seat.Player, seat.Race,
            humans.Contains(seat.Player) ? PlayerKind.Human : PlayerKind.Bot, $"Seat {seat.Player.Value}")).ToList();
        var world = MatchSetup.Create(Battles.Data, entry.Scenario, entry.Planet, new PlayerTable(rows), 7, new MatchOptions { Battle = options, FogOfWar = false });
        foreach (var human in humans)
            world.HeroOf(human)!.DrivenByPlayer = true;
        return world;
    }

    [Fact]
    public void Five_against_five_lists_every_player_in_their_place_with_number_and_names()
    {
        var world = FiveAgainstFive(Reds[2], Greens[0]);
        var run = world.Battle!;

        var mine = BattleReadout.Of(world, Reds[2])!;

        Assert.Equal(Reds, mine.Ours.Heroes.Select(h => h.Player));
        Assert.Equal(Greens, mine.Theirs.Heroes.Select(h => h.Player));
        Assert.Equal([1, 2, 3, 4, 5], mine.Ours.Heroes.Select(h => h.Number));
        Assert.Equal([1, 2, 3, 4, 5], mine.Theirs.Heroes.Select(h => h.Number));
        Assert.All(mine.Ours.Heroes, h => Assert.Equal(Race.Red, h.Race));
        Assert.All(mine.Theirs.Heroes, h => Assert.Equal(Race.Green, h.Race));
        Assert.Equal((Reds[2], 3, true), (mine.You.Player, mine.You.Number, mine.You.You));
        Assert.Single(mine.Ours.Heroes.Concat(mine.Theirs.Heroes), h => h.You);
        foreach (var hero in mine.Ours.Heroes.Concat(mine.Theirs.Heroes))
        {
            var sheet = run.Options.Heroes.Single(h => h.Player == hero.Player);
            Assert.Equal(sheet.Name, hero.Name);
            Assert.Equal(sheet.Name, BattleReadout.HeroName(run, hero.Player));
            Assert.Equal($"{hero.Number} · {sheet.Name}", hero.Label);
            Assert.Equal(sheet.Chassis, hero.Chassis);
            Assert.True(hero.Standing);
        }
        // People are named by their row; the computer's heroes are bots and have no person.
        Assert.Equal(("Seat 5", false), (mine.You.PlayerName, mine.You.Bot));
        Assert.Equal(("Seat 2", false), (mine.Theirs.Heroes[0].PlayerName, mine.Theirs.Heroes[0].Bot));
        Assert.Equal((null, true), (mine.Ours.Heroes[0].PlayerName, mine.Ours.Heroes[0].Bot));
        Assert.All(mine.Ours.Heroes.Concat(mine.Theirs.Heroes), h => Assert.False(h.Away));
        // The team-mates standing, in their places, without the player.
        Assert.Equal([Reds[0], Reds[1], Reds[3], Reds[4]], mine.Watchable);
        // The other side sees its own team first.
        var theirs = BattleReadout.Of(world, Greens[0])!;
        Assert.Equal(Greens, theirs.Ours.Heroes.Select(h => h.Player));
        Assert.Equal((1, 1), (theirs.Ours.Team, theirs.You.Number));

        // A person who leaves is away, and a bot drives their hero.
        world.HandOverToAi(Greens[0]);
        var left = BattleReadout.Of(world, Reds[2])!.Theirs.Heroes[0];
        Assert.Equal((true, true, "Seat 2"), (left.Away, left.Bot, left.PlayerName));
    }

    [Fact]
    public void Today_s_layout_numbers_no_hero()
    {
        var red = Of(Battles.New(), Race.Red);
        Assert.All(red.Ours.Heroes.Concat(red.Theirs.Heroes), h =>
        {
            Assert.Equal(0, h.Number);
            Assert.Equal(h.Name, h.Label);
            Assert.Equal(PlayerId.Of(h.Race), h.Player);
        });
    }

    [Fact]
    public void The_capture_bar_counts_against_the_points_that_take_the_pod()
    {
        // Five against five takes 250 points: a team's shares add up against those.
        var world = FiveAgainstFive(Reds[0]);
        var run = world.Battle!;
        Assert.Equal(250, run.CaptureTarget);
        run.Shares[Reds[1]] = 40;
        run.Shares[Reds[3]] = 22.5f;
        var red = BattleReadout.Of(world, Reds[0])!;
        Assert.Equal(250, red.CaptureTarget);
        Assert.Equal((62.5f, 250f, true), (red.Ours.Progress, red.Ours.Target, red.Ours.Capturing));
        Assert.Equal(0.25f, red.Ours.Taken, 1e-4f);
        Assert.Equal(25, red.Ours.Percent);
        Assert.Equal((0f, 0, false), (red.Theirs.Taken, red.Theirs.Percent, red.Theirs.Capturing));
        // Seen from the other side, the same capture is of its own pod.
        Assert.Equal(25, BattleReadout.Of(world, Greens[2])!.Theirs.Percent);
        run.Shares[Reds[4]] = 250;
        Assert.Equal((1f, 100), (BattleReadout.Of(world, Reds[0])!.Ours.Taken, BattleReadout.Of(world, Reds[0])!.Ours.Percent));

        // Today's layout takes 100: points and per cent are one.
        var classic = Battles.New();
        classic.Battle!.Shares[PlayerId.Of(Race.Blue)] = 37;
        var ours = Of(classic, Race.Red).Ours;
        Assert.Equal((100f, 37f, 37), (ours.Target, ours.Progress, ours.Percent));
        Assert.Equal(0.37f, ours.Taken, 1e-4f);
    }

    [Fact]
    public void A_capture_start_is_called_out_once_and_not_again_for_each_restart_after_a_hit()
    {
        var world = Battles.Begun();
        var calls = new BattleCaptureCalls();
        var called = new List<string>();
        void Look()
        {
            foreach (var e in world.Events.Where(e => e.Kind == GameEventKind.BattleCaptureProgress && e.Other is not null))
            {
                if (calls.Call(e.Other!.Value, e.Detail, world.Time))
                    called.Add(e.Detail!);
            }
            calls.Seen(world.Battle!, world.Time);
        }
        void Run(float seconds)
        {
            for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            {
                world.Step();
                Look();
            }
        }
        Battles.IntoCircle(world, Race.Red);
        Run(3);
        // Seven hits in fourteen seconds: the capture starts again from nothing each time, and is called out once.
        for (var hit = 0; hit < 7; hit++)
        {
            world.Damage(Battles.Hero(world, Race.Red), 1, Race.Green);
            Run(2);
        }
        Assert.Equal(["0"], called);
        // Left alone for a while, a new start is news again; and every tenth taken is called out.
        Battles.Away(world, Race.Red, (world.BattlePod(0)!.Position + world.BattlePod(1)!.Position) / 2);
        Run(BattleCaptureCalls.Quiet + 1);
        Battles.IntoCircle(world, Race.Red);
        Run(11);
        Assert.Equal(["0", "0", "10"], called);
    }
}
