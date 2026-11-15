using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>How a battle is fought out: the capture of the pods, its end and the clock, bodies and respawns, and what it is worth.</summary>
public class BattleRulesTests
{
    private static BattleRun Run(GameWorld world) => world.Battle!;

    [Fact]
    public void An_enemy_hero_in_the_circle_takes_the_pod_at_a_point_a_second()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        var events = Battles.Run(world, 10);
        Assert.Equal(10, Run(world).Shares[Race.Red], 0.2f);
        Assert.Equal(10, Run(world).Progress(0), 0.2f);
        Assert.Equal(0, Run(world).Progress(1));
        Assert.Contains(events, e => e.Kind == GameEventKind.BattleCaptureProgress && e.Other == 0 && e.Actor == world.BattlePod(1)!.Id);
        Assert.Equal(10, Run(world).Tally[Race.Red].Capture, 0.2f);
    }

    [Fact]
    public void The_shares_of_a_team_add_up_and_defenders_do_not_hold_it_up()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        Battles.IntoCircle(world, Race.Blue, 1);
        // The pod's own team stands in the circle too, as it does from the start.
        Assert.True(Vector2.Distance(Battles.Hero(world, Race.Green).Position, world.BattlePod(1)!.Position) <= BattleBalance.CaptureRadius);
        Battles.Run(world, 10);
        Assert.Equal(20, Run(world).Progress(0), 0.4f);
        Assert.Equal(10, Run(world).Shares[Race.Blue], 0.2f);
    }

    [Fact]
    public void A_capturer_that_is_hit_loses_its_share_at_once()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        Battles.IntoCircle(world, Race.Blue, 1);
        Battles.Run(world, 5);
        world.Damage(Battles.Hero(world, Race.Red), 10, Race.Green);
        Assert.False(Run(world).Shares.ContainsKey(Race.Red));
        world.Step();
        Assert.Contains(Race.Red, Run(world).Shares.Keys);
        Assert.Equal(5, Run(world).Shares[Race.Blue], 0.2f);
        Assert.InRange(Run(world).Shares[Race.Red], 0, 0.1f);
        // The points of the lost share went with it; only the new share counts.
        Assert.InRange(Run(world).Tally[Race.Red].Capture, 0, 0.1f);
        Assert.Equal(5, Run(world).Tally[Race.Blue].Capture, 0.2f);
    }

    [Fact]
    public void Capture_points_are_paid_for_the_share_that_stands_at_the_end_so_a_pod_never_pays_more_than_itself()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        Battles.IntoCircle(world, Race.Blue, 1);
        // Red is hit again and again while it takes the pod, as by a defender; stepping out and in pays nothing either.
        for (var round = 0; round < 6; round++)
        {
            Battles.Run(world, 8);
            world.Damage(Battles.Hero(world, Race.Red), 1, Race.Green);
        }
        Battles.Away(world, Race.Blue, (world.BattlePod(0)!.Position + world.BattlePod(1)!.Position) / 2);
        Battles.Run(world, 1);
        Battles.IntoCircle(world, Race.Blue, 1);
        Battles.Run(world, 3);
        var run = Run(world);
        Assert.InRange(run.Tally[Race.Red].Capture, 3.9f, 4.1f);
        Assert.InRange(run.Tally[Race.Blue].Capture, 2.9f, 3.1f);
        Assert.Equal(run.Progress(0), run.Teams[0].Sum(r => run.Tally[r].Capture), 0.01f);
        Assert.Equal(100 + 4, BattleRewards.Of(run, Race.Red).Xp);
    }

    [Fact]
    public void A_hero_that_falls_to_no_enemy_is_called_fallen_and_nobody_counts_the_kill()
    {
        var world = Battles.Begun();
        var events = Battles.Run(world, 0);
        world.Damage(Battles.Hero(world, Race.Red), 1_000_000, null);
        events.AddRange(world.Events);
        var fell = Assert.Single(events, e => e.Kind == GameEventKind.BattleHeroKilled);
        Assert.Null(fell.Other);
        Assert.Equal("Red", fell.Detail);
        Assert.All(Run(world).Tally.Values, t => Assert.Equal(0, t.Kills));
    }

    [Fact]
    public void A_hit_by_a_team_mate_does_nothing_and_takes_no_share()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        Battles.Run(world, 5);
        world.Damage(Battles.Hero(world, Race.Red), 30, Race.Blue);
        Battles.Run(world, 1);
        Assert.Equal(6, Run(world).Shares[Race.Red], 0.2f);
    }

    [Fact]
    public void A_capturer_that_leaves_the_circle_or_falls_loses_its_share()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        Battles.IntoCircle(world, Race.Blue, 1);
        Battles.Run(world, 5);
        Battles.Away(world, Race.Red, (world.BattlePod(0)!.Position + world.BattlePod(1)!.Position) / 2);
        var events = Battles.Kill(world, Race.Blue, Race.Green);
        events.AddRange(Battles.Run(world, 1));
        Assert.Empty(Run(world).Shares);
        Assert.Equal(0, Run(world).Progress(0));
        Assert.Contains(events, e => e.Kind == GameEventKind.BattleCaptureReset && e.Other == (int)Race.Red && e.Detail == "left");
        Assert.Contains(events, e => e.Kind == GameEventKind.BattleCaptureReset && e.Other == (int)Race.Blue && e.Detail == "hit");
    }

    [Fact]
    public void Taking_the_pod_wins_the_battle()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        Battles.IntoCircle(world, Race.Blue, 1);
        var events = Battles.Run(world, 55);
        Assert.Equal(new BattleResult(0, BattleEnd.Capture, Run(world).Result!.EndedAt), Run(world).Result);
        Assert.InRange(Run(world).Result!.EndedAt - BattleBalance.Opening, 49, 51);
        Assert.Contains(events, e => e.Kind == GameEventKind.BattleEnded && e.Other == 0 && e.Detail == "Capture");
        Assert.True(world.Won(Race.Red) && world.Won(Race.Blue));
        Assert.True(world.Lost(Race.Green) && world.Lost(Race.Yellow));
        Assert.True(world.Races[Race.Green].Defeated && world.Races[Race.Yellow].Defeated);
        Assert.False(world.Races[Race.Red].Defeated || world.Races[Race.Blue].Defeated);
        Assert.Null(world.Winner);
    }

    [Fact]
    public void With_victory_by_wipe_alone_there_is_no_capture()
    {
        var world = Battles.Begun(new BattleOptions { Win = BattleWin.Wipe });
        Battles.IntoCircle(world, Race.Red);
        Battles.Run(world, 120);
        Assert.Null(Run(world).Result);
        Assert.Equal(0, Run(world).Progress(0));
        Assert.Equal(0, Run(world).Tally[Race.Red].Capture);
    }

    [Fact]
    public void A_team_wiped_out_loses_even_while_it_takes_the_pod()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Green);
        Battles.IntoCircle(world, Race.Yellow, 1);
        Battles.Run(world, 49.9f);
        var events = Battles.Kill(world, Race.Green, Race.Red);
        events.AddRange(Battles.Kill(world, Race.Yellow, Race.Blue));
        events.AddRange(Battles.Run(world, 1));
        Assert.Equal((0, BattleEnd.Wipe), (Run(world).Result!.Winner, Run(world).Result!.Reason));
        Assert.Equal(2, events.Count(e => e.Kind == GameEventKind.BattleHeroKilled));
        Assert.Equal([1, 1, 0, 0], Run(world).Tally.Values.Select(t => t.Kills));
        Assert.True(world.Won(Race.Red));
    }

    [Fact]
    public void A_capture_wins_even_while_the_team_has_its_last_hero_only()
    {
        var world = Battles.Begun();
        Battles.Kill(world, Race.Red, Race.Green);
        Battles.IntoCircle(world, Race.Blue);
        Battles.Run(world, 101);
        Assert.Equal((0, BattleEnd.Capture), (Run(world).Result!.Winner, Run(world).Result!.Reason));
        Assert.False(Run(world).Tally[Race.Red].AliveAtEnd);
        Assert.True(Run(world).Tally[Race.Blue].AliveAtEnd);
    }

    [Fact]
    public void With_victory_by_taking_the_pod_a_team_wiped_out_has_lost_only_once_its_pod_is_taken()
    {
        var world = Battles.Begun(new BattleOptions { Win = BattleWin.Capture });
        Battles.Kill(world, Race.Green, Race.Red);
        Battles.Kill(world, Race.Yellow, Race.Blue);
        Battles.Run(world, 5);
        // Wiped out, and the battle goes on: the other team must still take the pod.
        Assert.Null(Run(world).Result);
        Assert.False(world.Won(Race.Red) || world.Lost(Race.Green));
        Battles.IntoCircle(world, Race.Red);
        var events = Battles.Run(world, 101);
        Assert.Equal((0, BattleEnd.Capture), (Run(world).Result!.Winner, Run(world).Result!.Reason));
        Assert.Contains(events, e => e.Kind == GameEventKind.BattleEnded && e.Other == 0 && e.Detail == "Capture");
        Assert.True(world.Won(Race.Red) && world.Won(Race.Blue) && world.Lost(Race.Green) && world.Lost(Race.Yellow));

        // Both wiped out is still the end, a draw; and with both ways to win a wipe ends it at once, as before.
        var both = Battles.Begun(new BattleOptions { Win = BattleWin.Capture });
        foreach (var race in Enum.GetValues<Race>())
            Battles.Kill(both, race, race is Race.Red or Race.Blue ? Race.Green : Race.Red);
        Battles.Run(both, 1);
        Assert.Equal((null, BattleEnd.Wipe), (Run(both).Result!.Winner, Run(both).Result!.Reason));
        var wipe = Battles.Begun(new BattleOptions { Win = BattleWin.Both });
        Battles.Kill(wipe, Race.Green, Race.Red);
        Battles.Kill(wipe, Race.Yellow, Race.Blue);
        Battles.Run(wipe, 1);
        Assert.Equal((0, BattleEnd.Wipe), (Run(wipe).Result!.Winner, Run(wipe).Result!.Reason));
    }

    [Fact]
    public void Both_teams_wiped_in_one_tick_is_a_draw()
    {
        var world = Battles.Begun();
        foreach (var race in Enum.GetValues<Race>())
            Battles.Kill(world, race, race is Race.Red or Race.Blue ? Race.Green : Race.Red);
        var events = Battles.Run(world, 1);
        Assert.Equal((null, BattleEnd.Wipe), (Run(world).Result!.Winner, Run(world).Result!.Reason));
        Assert.Contains(events, e => e.Kind == GameEventKind.BattleEnded && e.Other is null);
        Assert.All(world.Races.Values, r => Assert.False(r.Defeated));
        Assert.All(Enum.GetValues<Race>(), r => Assert.False(world.Won(r) || world.Lost(r)));
    }

    [Fact]
    public void When_the_time_is_up_it_is_a_draw()
    {
        var world = Battles.New(new BattleOptions { TimeLimit = 30 });
        Assert.Equal(30, Run(world).TimeLeft);
        Battles.Run(world, BattleBalance.Opening + 20);
        Assert.Equal(10, Run(world).TimeLeft, 0.1f);
        Assert.Null(Run(world).Result);
        Battles.Run(world, 10.1f);
        Assert.Equal((null, BattleEnd.Time), (Run(world).Result!.Winner, Run(world).Result!.Reason));
        Assert.Equal(0, Run(world).TimeLeft);
        Assert.All(world.Races.Values, r => Assert.False(r.Defeated));
    }

    [Fact]
    public void Nothing_counts_after_the_end()
    {
        var world = Battles.Begun(new BattleOptions { Lives = 2, TimeLimit = 5 });
        Battles.Kill(world, Race.Red, Race.Green);
        Battles.Run(world, 6);
        Assert.Equal(BattleEnd.Time, Run(world).Result!.Reason);
        var fingerprint = Run(world).Fingerprint();
        Battles.IntoCircle(world, Race.Blue);
        Battles.Run(world, 20);
        Assert.Null(world.HeroOf(Race.Red));
        Assert.Empty(Run(world).Shares);
        Assert.Equal(fingerprint, Run(world).Fingerprint());

        // After the end nothing is hurt any more: a hero cannot fall, costs no body and no death is counted.
        Battles.Kill(world, Race.Blue, Race.Green);
        Battles.Run(world, 20);
        Assert.NotNull(world.HeroOf(Race.Blue));
        Assert.Equal(1, world.Wired!.Heroes[Race.Blue].SpareBodies);
        Assert.Null(world.Wired.Heroes[Race.Blue].RespawnAt);
        Assert.Equal(0, Run(world).Tally[Race.Blue].Deaths);
        Assert.Equal(fingerprint, Run(world).Fingerprint());
    }

    [Fact]
    public void Once_the_battle_is_over_nobody_fires_and_nothing_is_hurt()
    {
        var world = Battles.Begun(new BattleOptions { Tier = 3, TimeLimit = 6 });
        var red = Battles.Hero(world, Race.Red);
        var green = Battles.Hero(world, Race.Green);
        red.DrivenByPlayer = false;
        Battles.Away(world, Race.Red, green.Position + new Vector2(25, 0));
        Assert.Null(Battles.Do(world, new AttackCommand(Race.Red, [red.Id], green.Id)));
        Assert.Contains(Battles.Run(world, 3), e => e.Kind == GameEventKind.Fired && e.Actor == red.Id);

        Battles.Run(world, 4);
        Assert.Equal(BattleEnd.Time, Run(world).Result!.Reason);
        // Rounds still in the air come down harmlessly, and the order to attack stands but nothing is fired.
        Battles.Run(world, 3);
        var hitPoints = green.HitPoints;
        Assert.Null(Battles.Do(world, new AttackCommand(Race.Red, [red.Id], green.Id)));
        var after = Battles.Run(world, 5);
        Assert.DoesNotContain(after, e => e.Kind == GameEventKind.Fired);
        Assert.Empty(world.Projectiles);
        Assert.Equal(hitPoints, green.HitPoints);
        world.Damage(green, 1_000_000, Race.Red);
        Assert.Equal(hitPoints, green.HitPoints);
        Assert.False(green.IsDead);
        Assert.Equal("the battle is over", Battles.Do(world, new WiredAbilityCommand(Race.Red, 0, green.Position)));
    }

    [Fact]
    public void Capture_points_come_in_whole_ticks_without_drift()
    {
        var world = Battles.Begun();
        Battles.IntoCircle(world, Race.Red);
        for (var tick = 0; tick < 20 * GameWorld.TicksPerSecond; tick++)
            world.Step();
        Assert.Equal(20f, Run(world).Tally[Race.Red].Capture);
        Assert.Equal(20f, Run(world).Shares[Race.Red]);
        Assert.Equal(100 + 20, BattleRewards.Of(Run(world), Race.Red).Xp);

        // The pod falls on the very tick the hundredth point comes in.
        for (var tick = 20 * GameWorld.TicksPerSecond; tick < 100 * GameWorld.TicksPerSecond - 1; tick++)
            world.Step();
        Assert.Null(Run(world).Result);
        world.Step();
        Assert.Equal((0, BattleEnd.Capture), (Run(world).Result!.Winner, Run(world).Result!.Reason));
        Assert.Equal(100f, Run(world).Tally[Race.Red].Capture);
    }

    [Fact]
    public void A_team_whose_pod_is_gone_has_no_body_to_come_and_can_be_wiped()
    {
        var world = Battles.Begun(new BattleOptions { Lives = 3 });
        // However it went (self-destruct is refused), a pod that is gone calls up no body.
        world.BattlePod(0)!.HitPoints = 0;
        world.Step();
        Assert.Null(world.BattlePod(0));
        Battles.Kill(world, Race.Red, Race.Green);
        Battles.Kill(world, Race.Blue, Race.Yellow);
        Battles.Run(world, 0.1f);
        Assert.Equal((1, BattleEnd.Wipe), (Run(world).Result!.Winner, Run(world).Result!.Reason));
        Assert.True(world.Won(Race.Green));
        Assert.True(world.Lost(Race.Red));
    }

    [Fact]
    public void With_lives_left_a_fallen_hero_comes_back_at_its_pod_after_the_wait_shielded_for_a_while()
    {
        var world = Battles.Begun(new BattleOptions { Lives = 2, RespawnWait = 10 });
        var state = world.Wired!.Heroes[Race.Blue];
        Assert.Equal(1, state.SpareBodies);
        var away = (world.BattlePod(0)!.Position + world.BattlePod(1)!.Position) / 2;
        Battles.Away(world, Race.Blue, away);
        var events = Battles.Kill(world, Race.Blue, Race.Green);
        Assert.Contains(events, e => e.Kind == GameEventKind.BattleHeroKilled && e.Other == (int)Race.Green && e.Detail == "Blue");
        Battles.Run(world, 9.5f);
        Assert.Null(world.HeroOf(Race.Blue));
        Assert.Null(Run(world).Result);
        events = Battles.Run(world, 1);
        var back = Battles.Hero(world, Race.Blue);
        Assert.Contains(events, e => e.Kind == GameEventKind.WiredHeroRespawned);
        Assert.True(Vector2.Distance(back.Position, world.BattlePod(0)!.Position) < 45);
        Assert.Equal(0, state.SpareBodies);
        Assert.Equal(1, Run(world).Tally[Race.Blue].Deaths);
        Assert.Equal(["grunt1"], Run(world).Tally[Race.Green].Killed);

        // The shield holds for a while, then a hit lands.
        var whole = back.Durability;
        world.Damage(back, 10, Race.Green);
        Assert.Equal(whole, back.Durability);
        Battles.Run(world, BattleBalance.RespawnShield);
        world.Damage(back, 10, Race.Green);
        Assert.NotEqual(whole, back.Durability);

        // The last body down is out of the fight, not defeated, while its mate fights on.
        Battles.Kill(world, Race.Blue, Race.Green);
        Battles.Run(world, 20);
        Assert.Null(world.HeroOf(Race.Blue));
        Assert.Null(world.Wired.Heroes[Race.Blue].RespawnAt);
        Assert.False(world.Races[Race.Blue].Defeated);
        Assert.Null(Run(world).Result);
    }

    [Fact]
    public void The_tally_counts_damage_to_enemy_heroes_only()
    {
        var world = Battles.Begun();
        var green = Battles.Hero(world, Race.Green);
        var before = green.HitPoints + green.Armour;
        world.Damage(green, 12, Race.Red);
        var lost = before - green.HitPoints - green.Armour;
        Assert.True(lost > 0);
        world.Damage(Battles.Hero(world, Race.Blue), 12, Race.Red);
        world.Damage(Battles.Hero(world, Race.Red), 12, Race.Red);
        Assert.Equal(lost, Run(world).Tally[Race.Red].Damage);
        Assert.Equal(0, Run(world).Tally[Race.Blue].Damage);
    }

    [Fact]
    public void Rewards_follow_the_formula()
    {
        var world = Battles.Begun();
        var run = Run(world);
        // Red takes 300 points of damage off enemy heroes, kills two, earns 12 capture points, and its team wins.
        var red = run.Tally[Race.Red];
        red.Damage = 300;
        red.Killed.AddRange(["grunt1", "knight3"]);
        red.Capture = 12.6f;
        Battles.Kill(world, Race.Green, Race.Red);
        Battles.Kill(world, Race.Yellow, Race.Red);
        Battles.Run(world, 0.1f);
        Assert.True(run.Won(Race.Red));
        Assert.True(red.Damage > 300);
        var xp = (100 + red.Damage / BattleBalance.DamagePerXp + 40 * 4 + 12) * 1.5f;
        var reward = BattleRewards.Of(run, Race.Red);
        Assert.Equal((int)MathF.Round(xp), reward.Xp);
        Assert.Equal((int)MathF.Round(reward.Xp * 0.05f), reward.FreeXp);
        // Salvage: a tenth of ten times the Wired price of each chassis killed.
        Assert.Equal(0 + 750 + 0 + 0, reward.Salvage);
        Assert.Equal(6 * reward.Xp + reward.Salvage, reward.Credits);
        Assert.True(reward.Won);

        // A loser that did nothing gets the part for taking part.
        var lost = BattleRewards.Of(run, Race.Yellow);
        Assert.Equal((100, 5, 600, 0, false), (lost.Xp, lost.FreeXp, lost.Credits, lost.Salvage, lost.Won));
        Assert.Equal((150, 450), BattleRewards.OfWired(15));
    }
}
