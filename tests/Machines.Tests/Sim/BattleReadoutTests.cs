using Machines.Game;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>What the HUD of a battle reads off the world: the clock and the opening, both teams, the capture, lives and the end.</summary>
public class BattleReadoutTests
{
    private static BattleReadout Of(GameWorld world, Race race) => BattleReadout.Of(world, race)!;

    [Fact]
    public void A_hero_with_an_empty_name_is_called_by_its_race()
    {
        var composed = BattleBots.Compose(new BattleOptions { TeamA = [Race.Red, Race.Blue], TeamB = [Race.Green, Race.Yellow] }, []);
        var options = composed with
        {
            Heroes = composed.Heroes.Select(h => h.Race == Race.Red ? h with { Name = "" } : h).ToList(),
        };
        var world = Battles.New(options);

        Assert.Equal("Red", BattleReadout.HeroName(world.Battle!, Race.Red));
        Assert.Equal("Red", Of(world, Race.Red).You.Name);
        var blue = world.Battle!.Options.Heroes.Single(h => h.Race == Race.Blue).Name;
        Assert.NotEmpty(blue);
        Assert.Equal(blue, BattleReadout.HeroName(world.Battle, Race.Blue));
    }

    [Fact]
    public void There_is_none_outside_a_battle_or_for_a_race_in_no_team()
    {
        var players = Enum.GetValues<Race>().ToDictionary(r => r, _ => Controller.Player);
        var normal = MatchSetup.Create(Battles.Data, "m_desert", "m_desert", players, 7, new MatchOptions());
        var wired = MatchSetup.Create(Battles.Data, "m_desert", "m_desert", new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai },
            7, new MatchOptions { Wired = new WiredOptions { Arena = "m_desert" } });
        Assert.Null(BattleReadout.Of(normal, Race.Red));
        Assert.Null(BattleReadout.Of(wired, Race.Red));
        var pair = Battles.New(new BattleOptions { TeamA = [Race.Red], TeamB = [Race.Green] });
        Assert.Null(BattleReadout.Of(pair, Race.Blue));
        Assert.NotNull(BattleReadout.Of(pair, Race.Green));
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
        Assert.Equal([Race.Blue], Of(world, Race.Red).Watchable);
        Battles.Kill(world, Race.Red, Race.Green);
        Battles.Run(world, 1);
        var red = Of(world, Race.Red);
        Assert.True(red.Out);
        Assert.Null(red.DownSeconds);
        Assert.Equal(0, red.You.LivesLeft);
        Assert.Equal(0, red.You.Health);
        Assert.True(red.Ours.InTheFight);
        Assert.Equal([Race.Blue], red.Watchable);
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
