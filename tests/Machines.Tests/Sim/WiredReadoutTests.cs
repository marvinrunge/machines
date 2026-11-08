using System.Numerics;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The figures of a run's HUD, each held against what the simulation has at that moment.</summary>
public class WiredReadoutTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld NewRun(WiredOptions? options = null) =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7,
            new MatchOptions { Wired = options ?? new WiredOptions() });

    private static GameWorld AtWave(int wave)
    {
        var world = NewRun();
        world.Wired!.Wave = wave - 1;
        world.Wired.Countdown = 0;
        world.Step();
        world.Step();
        Assert.Equal((WiredPhase.Wave, wave), (world.Wired.Phase, world.Wired.Wave));
        return world;
    }

    private static WiredReadout Now(GameWorld world) => WiredReadout.Of(world, Race.Red)!;

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    private static Construction Pod(GameWorld world) => world.WiredPod(Race.Red)!;

    private static void Run(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds / GameWorld.Dt; i++)
            world.Step();
    }

    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    private static void Wear(GameWorld world, string id, WiredSlot slot)
    {
        var state = Red(world);
        state.Loadout = state.Loadout.With(slot, state.Add(WiredCatalogue.KindOf(id)!.Value, id, 0));
    }

    [Fact]
    public void A_match_without_a_run_has_no_figures()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7, new MatchOptions());

        Assert.Null(WiredReadout.Of(world, Race.Red));
    }

    [Fact]
    public void Before_the_first_wave_it_counts_down_to_wave_one()
    {
        var world = NewRun();
        Run(world, 1.3f);
        var now = Now(world);

        Assert.Equal(1, now.Wave);
        Assert.True(now.BetweenWaves);
        Assert.Equal((int)MathF.Ceiling(world.Wired!.Countdown), now.Seconds);
        Assert.Equal(0, now.EnemiesLeft);
        Assert.Equal((int)world.Races[Race.Red].Bmu, now.Bank);
        Assert.Equal(1, now.Pod);
        Assert.Equal(Hero(world).DistanceTo(Pod(world)), now.PodDistance);
        Assert.True(now.HangarHere);
        Assert.Equal(WiredBalance.SpareBodies(WiredDifficulty.Normal), now.SpareBodies);
        Assert.Equal(now.SpareBodies, now.BodiesAtStart);
        Assert.Null(now.DownSeconds);
        Assert.Null(now.Dash);
        Assert.Empty(now.Abilities);
        Assert.Empty(now.Squad);
        Assert.Empty(now.Bosses);
    }

    [Theory]
    [InlineData(WiredDifficulty.Hard)]
    [InlineData(WiredDifficulty.Hardcore)]
    public void The_bodies_at_start_follow_the_difficulty(WiredDifficulty difficulty)
    {
        var now = Now(NewRun(new WiredOptions { Difficulty = difficulty }));

        Assert.Equal(WiredBalance.SpareBodies(difficulty), now.BodiesAtStart);
        Assert.Equal(now.BodiesAtStart, now.SpareBodies);
    }

    [Fact]
    public void A_wave_under_way_shows_its_number_and_what_is_left_of_it()
    {
        var world = AtWave(3);
        var now = Now(world);

        Assert.Equal(3, now.Wave);
        Assert.False(now.BetweenWaves);
        Assert.Equal(0, now.Seconds);
        Assert.Equal(world.Wired!.EnemiesLeft, now.EnemiesLeft);
        Assert.True(now.EnemiesLeft > 0);
        Assert.False(now.HangarHere);
    }

    [Fact]
    public void Between_waves_it_names_the_special_wave_to_come_and_during_it_the_one_under_way()
    {
        var world = NewRun();
        var run = world.Wired!;
        run.NextSpecial = WiredSpecial.Night;

        Assert.Equal(WiredSpecial.Night, Now(world).Special);

        run.Special = WiredSpecial.Storm;
        run.Phase = WiredPhase.Wave;

        Assert.Equal(WiredSpecial.Storm, Now(world).Special);
    }

    [Fact]
    public void A_boss_stands_with_its_name_health_and_armour()
    {
        var world = AtWave(5);
        var run = world.Wired!;
        var boss = run.Bosses.First().Machine;
        boss.HitPoints = boss.MaxHitPoints / 4;
        boss.Armour = boss.MaxArmour / 2;
        var now = Now(world);

        var shown = Assert.Single(now.Bosses);
        Assert.Equal(WiredDirector.BossOf(5)!.Name, now.BossName);
        Assert.Equal(WiredDirector.BossOf(5)!.Name, shown.Name);
        Assert.Equal(boss.Id, shown.Id);
        Assert.Equal(boss.HitPoints / (float)boss.MaxHitPoints, shown.Health);
        Assert.Equal(boss.Armour / (float)boss.MaxArmour, shown.Armour);
    }

    [Fact]
    public void A_run_that_ended_names_no_boss_of_a_wave_that_never_comes()
    {
        var world = AtWave(4);
        var run = world.Wired!;
        Assert.Null(Now(world).BossName);
        run.Phase = WiredPhase.Intermission;
        Assert.Equal(WiredDirector.BossOf(5)!.Name, Now(world).BossName);

        run.Phase = WiredPhase.Ended;

        Assert.Equal(4, Now(world).Wave);
        Assert.Null(Now(world).BossName);
    }

    [Fact]
    public void A_run_that_ended_on_a_boss_wave_still_names_it()
    {
        var world = AtWave(5);
        world.Wired!.Phase = WiredPhase.Ended;

        Assert.Equal(WiredDirector.BossOf(5)!.Name, Now(world).BossName);
    }

    [Fact]
    public void The_twins_show_two_bars()
    {
        Assert.Equal(2, Now(AtWave(25)).Bosses.Count);
    }

    [Fact]
    public void A_dead_boss_leaves_the_bars()
    {
        var world = AtWave(5);
        world.Damage(world.Wired!.Bosses.First().Machine, 1_000_000, Race.Red);
        Run(world, 0.1f);

        Assert.Empty(Now(world).Bosses);
    }

    [Fact]
    public void The_pod_shows_its_share_and_null_once_it_is_gone()
    {
        var world = AtWave(1);
        var pod = Pod(world);
        pod.HitPoints = pod.MaxHitPoints / 2;

        Assert.Equal(pod.HitPoints / (float)pod.MaxHitPoints, Now(world).Pod);

        world.Damage(pod, 10_000_000, Race.Blue);
        Run(world, 0.1f);

        Assert.Null(Now(world).Pod);
        Assert.Null(Now(world).PodDistance);
    }

    [Fact]
    public void A_dead_hero_counts_down_to_its_next_body_and_the_bank_is_gone()
    {
        var world = AtWave(1);
        var state = Red(world);
        var bodies = state.SpareBodies;
        world.Races[Race.Red].Bmu = 300;

        world.Damage(Hero(world), 1_000_000, Race.Blue);
        Run(world, 1);
        var now = Now(world);

        Assert.Equal(state.RespawnAt! - world.Time, now.DownSeconds);
        Assert.InRange(now.DownSeconds!.Value, WiredBalance.RespawnTime - 1.2f, WiredBalance.RespawnTime - 0.8f);
        Assert.Equal(bodies - 1, now.SpareBodies);
        Assert.Equal((int)world.Races[Race.Red].Bmu, now.Bank);
        Assert.Null(now.PodDistance);
        Assert.False(now.HangarHere);

        Run(world, WiredBalance.RespawnTime);

        Assert.Null(Now(world).DownSeconds);
        Assert.NotNull(Now(world).PodDistance);
    }

    [Fact]
    public void Abilities_are_shown_by_their_slot_with_what_is_left_of_the_cooldown()
    {
        var world = AtWave(1);
        Wear(world, "supercharge", WiredSlot.Ability(1));
        var ability = WiredCatalogue.FindAbility("supercharge")!;

        var ready = Assert.Single(Now(world).Abilities);
        Assert.Equal((1, "supercharge"), (ready.Slot, ready.Catalogue));
        Assert.True(ready.Ready);

        Assert.Null(Do(world, new WiredAbilityCommand(Race.Red, 1, Hero(world).Position)));
        Run(world, 2);
        var cooling = Assert.Single(Now(world).Abilities);

        Assert.False(cooling.Ready);
        Assert.Equal(Red(world).AbilitySecondsLeft(1, world.Time), cooling.SecondsLeft);
        Assert.Equal(1 - cooling.SecondsLeft / ability.Cooldown, cooling.Share, 4);
    }

    [Fact]
    public void An_ability_counted_in_waves_shows_the_waves_left()
    {
        var world = AtWave(1);
        var counted = WiredCatalogue.Abilities.First(a => a.CooldownWaves > 0);
        Wear(world, counted.Id, WiredSlot.Ability(0));
        var state = Red(world);
        state.AbilityReadyWave[state.Loadout.Abilities[0]!.Id] = world.Wired!.Wave + 1;

        var shown = Assert.Single(Now(world).Abilities);

        Assert.Equal(state.AbilityWavesLeft(0, world.Wired.Wave), shown.WavesLeft);
        Assert.True(shown.WavesLeft > 0);
        Assert.False(shown.Ready);
    }

    [Fact]
    public void The_thrusters_show_their_cooldown_and_when_they_fire()
    {
        var world = AtWave(1);
        Wear(world, "thrusters", WiredSlot.Module(0));
        var state = Red(world);

        Assert.True(Now(world).Dash!.Value.Ready);

        Assert.Null(Do(world, new WiredDashCommand(Race.Red, Vector2.UnitX)));
        var firing = Now(world).Dash!.Value;

        Assert.True(firing.Firing);
        Assert.False(firing.Ready);
        Assert.Equal(state.DashReadyAt - world.Time, firing.SecondsLeft);

        Run(world, WiredBalance.DashTime + 0.2f);

        Assert.False(Now(world).Dash!.Value.Firing);
    }

    [Fact]
    public void The_squad_strip_follows_the_escorts_and_what_they_are_told()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 5000;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var escort = world.Escorts(Race.Red).Single();
        escort.HitPoints = escort.MaxHitPoints / 2;
        escort.Order = new StandGroundOrder();
        var now = Now(world);

        var shown = Assert.Single(now.Squad);
        Assert.Equal(escort.Id, shown.Id);
        Assert.Equal(escort.Key.SubType, shown.Model);
        Assert.Equal(escort.Key.HardwareLevel, shown.Level);
        Assert.Equal(escort.HitPoints / (float)escort.MaxHitPoints, shown.Health);
        Assert.Equal(EscortDoing.Holding, shown.Doing);
        Assert.Equal(WiredBalance.SquadLimit(world.Wired!.ShopWave, 0), now.SquadLimit);
    }

    [Fact]
    public void The_chips_keep_their_order_and_a_lit_one_is_lit()
    {
        var world = AtWave(1);
        Wear(world, "nano", WiredSlot.Chip(0));
        Wear(world, "marksman", WiredSlot.Chip(1));
        var state = Red(world);

        var chips = Now(world).Chips;

        Assert.Equal(state.Chips.Select(c => c.Chip.Id), chips.Select(c => c.Id));
        Assert.Equal(WiredChips.Lit(world, state).ToHashSet(), chips.Where(c => c.Lit).Select(c => c.Id).ToHashSet());
    }

    [Fact]
    public void A_chip_to_choose_is_shown()
    {
        var world = NewRun();
        Assert.False(Now(world).ChipChoice);

        Red(world).ChipChoice.Add(WiredChips.All[0].Id);

        Assert.True(Now(world).ChipChoice);
    }

    [Fact]
    public void An_ion_strike_counts_down_and_knows_whether_the_hero_stands_in_it()
    {
        var world = AtWave(1);
        var run = world.Wired!;
        var hero = Hero(world);
        run.Strikes.Add(new WiredStrike(hero.Position + Vector2.UnitX * 10_000, WiredBalance.IonRadius, world.Time + 3));

        Assert.Equal(3, Now(world).StrikeIn!.Value, 3);
        Assert.False(Now(world).InStrike);

        run.Strikes.Add(new WiredStrike(hero.Position, WiredBalance.IonRadius, world.Time + 4));

        Assert.True(Now(world).InStrike);
    }

    [Fact]
    public void Inside_the_pod_an_ion_strike_is_no_reason_to_get_out()
    {
        var world = NewRun();
        var (hero, pod) = (Hero(world), Pod(world));
        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [hero.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && hero.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, hero.Inside);

        world.Wired!.Strikes.Add(new WiredStrike(hero.Position, WiredBalance.IonRadius, world.Time + 4));

        Assert.NotNull(Now(world).StrikeIn);
        Assert.False(Now(world).InStrike);
    }

    [Fact]
    public void The_enemies_left_count_against_the_most_the_wave_had_however_late_they_are_read()
    {
        var world = AtWave(3);
        var most = Now(world).EnemiesLeft;
        Assert.Equal(most, Now(world).MostEnemies);
        var killed = world.Wired!.Foes.Select(f => f.Machine).ToList();
        Assert.NotEmpty(killed);

        foreach (var foe in killed)
            world.Damage(foe, 1_000_000, Race.Red);
        Run(world, 0.1f);
        // What a HUD made now, after a load, reads: no figure of the frames before it.
        var now = Now(world);

        Assert.Equal(most - killed.Count, now.EnemiesLeft);
        Assert.Equal(most, now.MostEnemies);
    }

    [Fact]
    public void The_clock_counts_down_from_the_whole_of_its_wait()
    {
        var world = NewRun();
        Run(world, 1.3f);

        Assert.Equal((int)WiredBalance.PrepareTime, Now(world).WaitLength);
        Assert.True(Now(world).Seconds < Now(world).WaitLength);

        world = AtWave(1);
        world.BeginIntermission();

        Assert.Equal((int)WiredBalance.IntermissionTime, Now(world).WaitLength);
        Assert.Equal(Now(world).WaitLength, Now(world).Seconds);
    }

    [Fact]
    public void The_score_is_the_one_the_run_will_end_with()
    {
        var world = AtWave(1);
        var state = Red(world);

        world.Damage(world.Wired!.Foes[0].Machine, 1_000_000, Race.Red, Hero(world));
        Run(world, 0.1f);

        Assert.Equal(WiredBalance.Score(state.Stats, world.Wired.Options.Difficulty), Now(world).Score);
    }
}
