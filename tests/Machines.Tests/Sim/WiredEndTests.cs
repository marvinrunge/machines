using Machines.Game;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The end of a run of Wired: how a score is told, what is said of a record, what is new and what is next, and that every unlock leads to a run.</summary>
public class WiredEndTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static WiredRunResult Run(int waves, WiredDifficulty difficulty = WiredDifficulty.Normal, int squad = 0, int kills = 0, int bosses = 0, int crates = 0, long score = 0, string arena = "m_desert") =>
        new(arena, difficulty, "Kai", "grunt1", score, new WiredStats { WavesCleared = waves, Kills = kills, BossesKilled = bosses, CratesCollected = crates, LargestSquad = squad });

    private static MatchInfo Match(WiredOptions options, string arena = "m_desert") => new(arena, arena, new MatchOptions { Wired = options }, 4, 1,
    [
        new MatchPlayer(0, "Kai", Race.Red, Race.Red, Ai: false),
        new MatchPlayer(0, "AI", Race.Blue, Race.Blue, Ai: true),
    ]);

    [Theory]
    [InlineData(WiredDifficulty.Normal)]
    [InlineData(WiredDifficulty.Hard)]
    [InlineData(WiredDifficulty.Hardcore)]
    public void The_lines_of_a_score_come_to_the_score(WiredDifficulty difficulty)
    {
        var stats = new WiredStats { Kills = 37, KillValue = 1237, WavesCleared = 11, BossesKilled = 2, BmusEarned = 4573 };
        var report = new WiredReport(stats, difficulty);

        Assert.Equal(WiredBalance.Score(stats, difficulty), report.Score);
        Assert.Equal(report.Score, report.Lines.Sum(l => l.Points));
        Assert.Equal(difficulty != WiredDifficulty.Normal, report.Lines.Any(l => l.Kind == WiredScoreKind.Difficulty));
        Assert.Equal(11 * WiredBalance.ScorePerWave, report.Lines.Single(l => l.Kind == WiredScoreKind.Waves).Points);
    }

    [Fact]
    public void A_score_of_nothing_is_told_as_nothing()
    {
        var report = new WiredReport(new WiredStats(), WiredDifficulty.Hardcore);

        Assert.Equal(0, report.Score);
        Assert.All(report.Lines, line => Assert.Equal(0, line.Points));
        Assert.Empty(report.Weapons);
    }

    [Fact]
    public void Weapons_are_told_by_their_damage_and_those_never_fired_are_left_out()
    {
        var stats = new WiredStats();
        stats.Weapon("BOLTER").Rounds = 300;
        stats.Weapon("BOLTER").Damage = 50;
        stats.Weapon("PLASMA_CANNON1").Rounds = 10;
        stats.Weapon("PLASMA_CANNON1").Damage = 900;
        stats.Weapon("AUTO_CANNON");

        Assert.Equal(["PLASMA_CANNON1", "BOLTER"], new WiredReport(stats, WiredDifficulty.Normal).Weapons.Select(w => w.Key));
    }

    [Theory]
    [InlineData(0, false, "0")]
    [InlineData(999, true, "999")]
    [InlineData(1234567, true, "1.234.567")]
    [InlineData(1234567, false, "1,234,567")]
    public void Figures_are_set_apart_as_the_language_does(long figure, bool german, string shown) => Assert.Equal(shown, WiredReport.Figure(figure, german));

    [Fact]
    public void The_first_run_somewhere_sets_a_record_but_beats_none()
    {
        var news = new WiredRecordNews(new WiredRecord(), 500, 3, 10);

        Assert.True(news.First);
        Assert.False(news.Beaten);
        Assert.True(news.NewScore && news.NewWaves && news.NewKills);
    }

    [Fact]
    public void A_run_of_nothing_is_no_first_record()
    {
        var news = new WiredRecordNews(new WiredRecord(), 0, 0, 0);

        Assert.False(news.First || news.Beaten || news.NewScore);
        Assert.Equal(0, news.Short);
    }

    [Fact]
    public void A_best_beaten_is_a_new_record_and_a_best_missed_says_by_how_much()
    {
        var best = new WiredRecord(Waves: 12, Score: 4000, Kills: 80);

        var beaten = new WiredRecordNews(best, 4001, 11, 90);
        Assert.True(beaten.Beaten && beaten.NewScore && beaten.NewKills);
        Assert.False(beaten.First || beaten.NewWaves);

        var missed = new WiredRecordNews(best, 3500, 13, 70);
        Assert.False(missed.Beaten);
        Assert.True(missed.NewWaves);
        Assert.Equal(500, missed.Short);

        var level = new WiredRecordNews(best, 4000, 12, 80);
        Assert.False(level.Beaten || level.NewScore);
        Assert.Equal(0, level.Short);
    }

    [Fact]
    public void What_a_run_opens_is_new_until_the_hangar_has_been_up()
    {
        var profile = new WiredProfile();
        var opened = profile.Note(Run(10, squad: 3));

        Assert.Equal(opened.Order(StringComparer.Ordinal), profile.Fresh);
        Assert.Contains("overseer2", profile.Fresh);

        var taken = profile.TakeFresh();
        Assert.Contains("m_moon", taken);
        Assert.Empty(profile.Fresh);
        Assert.True(profile.CanPlay("m_moon"));

        Assert.Empty(profile.Note(Run(10, squad: 3)));
        Assert.Empty(profile.Fresh);
    }

    [Fact]
    public void What_is_new_stays_new_in_the_file_and_an_old_file_has_nothing_new()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wired-{Guid.NewGuid():N}.json");
        var written = new WiredProfile();
        written.Note(Run(10));
        written.Save(path);
        var back = WiredProfile.Load(path);
        File.WriteAllText(path, """{ "Unlocks": ["reaper2"] }""");
        var old = WiredProfile.Load(path);
        File.Delete(path);

        Assert.Equal(written.Fresh, back.Fresh);
        Assert.NotEmpty(back.Fresh);
        Assert.Empty(old.Fresh);
        Assert.True(old.CanStartOn("reaper2"));
    }

    [Fact]
    public void The_best_wave_is_any_arena_s_and_a_difficulty_counts_only_its_own()
    {
        var profile = new WiredProfile();
        profile.Note(Run(7, WiredDifficulty.Hard, arena: "m_green"));
        profile.Note(Run(9, WiredDifficulty.Normal));

        Assert.Equal(9, profile.BestWave());
        Assert.Equal(7, profile.BestWave(WiredDifficulty.Hard));
        Assert.Equal(0, profile.BestWave(WiredDifficulty.Hardcore));
    }

    [Fact]
    public void All_that_is_closed_is_a_goal_with_how_far_off_it_is_the_nearest_first()
    {
        var profile = new WiredProfile();
        var fresh = profile.Goals();

        Assert.Equal(WiredProfile.StartChassis.Count - 1 + 2 + 1 + WiredProfile.Milestones.Count, fresh.Count);
        Assert.All(fresh, goal => Assert.Equal(0, goal.Have));

        profile.Note(Run(9, WiredDifficulty.Hard));
        var goals = profile.Goals();

        Assert.Equal(9, goals.Single(g => g.Id == "reaper2").Have);
        Assert.Equal(3, goals.Single(g => g.Id == "overseer2").Squad);
        Assert.Equal(9, goals.Single(g => g.Id == WiredProfile.Hardcore).Have);
        Assert.Equal(goals.OrderByDescending(g => g.Share), goals);
        Assert.All(goals, goal => Assert.True(goal.Have < goal.Need, goal.Id));
    }

    [Fact]
    public void A_goal_passed_but_not_met_is_still_short_of_its_end()
    {
        var profile = new WiredProfile();
        profile.Note(Run(12));

        var overseer = profile.Goals().Single(g => g.Id == "overseer2");
        Assert.Equal(9, overseer.Have);
        Assert.True(overseer.Share < 1);
    }

    [Fact]
    public void Hardcore_waits_for_hard_whatever_normal_has_done()
    {
        var profile = new WiredProfile();
        profile.Note(Run(40));
        Assert.Equal(0, profile.Goals().Single(g => g.Id == WiredProfile.Hardcore).Have);
        Assert.False(profile.CanPlay(WiredDifficulty.Hardcore));

        Assert.Contains(WiredProfile.Hardcore, profile.Note(Run(15, WiredDifficulty.Hard)));
        Assert.True(profile.CanPlay(WiredDifficulty.Hardcore));
        Assert.DoesNotContain(profile.Goals(), g => g.Id == WiredProfile.Hardcore);
    }

    [Fact]
    public void With_everything_open_nothing_is_left_to_do()
    {
        var profile = new WiredProfile();
        profile.Note(Run(30, squad: 3));
        profile.Note(Run(15, WiredDifficulty.Hard));
        foreach (var milestone in WiredProfile.Milestones)
            profile.Unlocks.Add(milestone.Chip);

        Assert.Empty(profile.Goals());
    }

    [Fact]
    public void One_boss_is_said_as_one()
    {
        var one = WiredProfile.Milestones.First(m => m is { Measure: WiredMeasure.Bosses, Amount: 1 });
        Assert.Equal("{0} boss killed", new WiredProfile().Goals().Single(g => g.Id == one.Chip).Words);
        Assert.Equal("{0} bosses killed", WiredProfile.Words(WiredMeasure.Bosses, 3));
    }

    [Fact]
    public void Every_chassis_that_opens_starts_a_run_from_the_hangar()
    {
        var profile = new WiredProfile();
        profile.Note(Run(30, squad: 3));

        foreach (var start in WiredProfile.StartChassis)
        {
            Assert.True(profile.CanStartOn(start.Chassis), start.Chassis);
            var draft = new HeroDraft(Data, profile);
            draft.Wear(start.Chassis);
            var world = Replay.BuildWorld(Data, Match(draft.Options));
            var run = world.Wired!;

            Assert.Equal(start.Chassis, run.Heroes[Race.Red].Loadout.Chassis.Catalogue);
            Assert.NotNull(world.HeroOf(Race.Red));
            Assert.Equal(WiredCatalogue.FindChassis(start.Chassis)!.SubType, world.HeroOf(Race.Red)!.Spec.SubType);
        }
    }

    [Theory]
    [InlineData("m_desert")]
    [InlineData("m_green")]
    [InlineData("m_moon")]
    [InlineData("m_dales")]
    public void Every_arena_that_opens_holds_a_run(string arena)
    {
        var world = Replay.BuildWorld(Data, Match(new WiredOptions { Arena = arena, ChipPool = new WiredProfile().ChipPool }, arena));

        Assert.NotNull(world.WiredPod(Race.Red));
        Assert.NotNull(world.HeroOf(Race.Red));
    }

    [Fact]
    public void A_run_on_hardcore_has_no_spare_body()
    {
        var profile = new WiredProfile();
        profile.Note(Run(15, WiredDifficulty.Hard));
        var draft = new HeroDraft(Data, profile);
        draft.Choose(WiredDifficulty.Hardcore);

        var run = Replay.BuildWorld(Data, Match(draft.Options)).Wired!;

        Assert.Equal(WiredDifficulty.Hardcore, run.Options.Difficulty);
        Assert.Equal(0, run.Heroes[Race.Red].SpareBodies);
    }

    [Fact]
    public void A_chip_opened_goes_into_the_next_run_and_nothing_makes_a_hero_stronger_at_its_start()
    {
        var before = new WiredProfile();
        var after = new WiredProfile();
        var chip = WiredProfile.Milestones.First(m => m.Measure == WiredMeasure.Crates);
        after.Note(Run(0, crates: chip.Amount));

        var (plain, opened) = (new HeroDraft(Data, before), new HeroDraft(Data, after));
        Assert.DoesNotContain(chip.Chip, plain.Options.ChipPool);
        Assert.Contains(chip.Chip, opened.Options.ChipPool);

        var (one, other) = (Replay.BuildWorld(Data, Match(plain.Options)), Replay.BuildWorld(Data, Match(opened.Options)));
        var (hero, same) = (one.HeroOf(Race.Red)!, other.HeroOf(Race.Red)!);
        Assert.Equal((hero.MaxHitPoints, hero.MaxArmour, hero.Weapons.Count), (same.MaxHitPoints, same.MaxArmour, same.Weapons.Count));
        Assert.Equal(one.Races[Race.Red].Bmu, other.Races[Race.Red].Bmu);
    }

    /// <summary>What a simple player does in a run: it goes for the enemy, and between waves it is ready at once.</summary>
    private static void PlayOn(LockstepSession live, int tick)
    {
        var world = live.World;
        var run = world.Wired!;
        if (tick % 50 != 0 || world.HeroOf(Race.Red) is not { } hero)
            return;
        if (run.BetweenWaves)
            live.Queue(new WiredReadyCommand(Race.Red));
        else if (world.Debris.Count > 0)
            live.Queue(new MoveCommand(Race.Red, [hero.Id], world.Debris[0].Position));
        else if (run.Foes.Count > 0)
            live.Queue(new AttackCommand(Race.Red, [hero.Id], run.Foes[0].Machine.Id));
    }

    [Fact]
    public void A_run_played_to_its_end_opens_what_its_figures_have_earned_and_its_recording_owes_nothing_to_the_profile()
    {
        var pool = new WiredProfile().ChipPool;
        var match = Match(new WiredOptions { HeroName = "Kai", Arena = "m_desert", Chassis = "grunt1", ChipPool = pool });
        var live = new LockstepSession(Replay.BuildWorld(Data, match), Race.Red, [Race.Red], match.InputDelay);
        var run = live.World.Wired!;
        var state = run.Heroes[Race.Red];
        for (var tick = 0; tick < 20 * 3600 && run.Phase != WiredPhase.Ended; tick++)
        {
            PlayOn(live, tick);
            live.Pump();
            Assert.True(live.TryStep());
        }
        Assert.Equal(WiredPhase.Ended, run.Phase);
        Assert.NotNull(run.Ending);
        Assert.True(state.Stats.Kills >= 1, $"{state.Stats.Kills} kills");

        var report = new WiredReport(state.Stats, run.Options.Difficulty);
        Assert.Equal(report.Score, report.Lines.Sum(l => l.Points));
        var result = new WiredRunResult("m_desert", run.Options.Difficulty, "Kai", state.Loadout.Chassis.Catalogue, report.Score, state.Stats);

        // The figures of this run, again and again, reach what asks for kills, and from then on it is open.
        var profile = new WiredProfile();
        var kills = WiredProfile.Milestones.First(m => m.Measure == WiredMeasure.Kills);
        var runs = 0;
        while (!profile.Unlocks.Contains(kills.Chip) && runs++ < 2000)
            profile.Note(result);
        Assert.Equal((runs - 1) * (long)state.Stats.Kills < kills.Amount, runs * (long)state.Stats.Kills >= kills.Amount);
        Assert.Contains(kills.Chip, profile.ChipPool);
        Assert.Equal(report.Score, profile.Record("m_desert", run.Options.Difficulty).Score);

        // Whatever the profile holds by now, the recording plays as it was.
        var recorded = new Replay(match, live.Played, live.World.Tick);
        Assert.Equal(pool, recorded.Match.Options.Wired!.ChipPool);
        var shown = recorded.Playback(Replay.BuildWorld(Data, recorded.Match));
        while (shown.World.Tick < recorded.Ticks && shown.TryStep())
        {
        }
        Assert.Equal(live.World.Checksum(), shown.World.Checksum());
        Assert.Equal(WiredPhase.Ended, shown.World.Wired!.Phase);
        Assert.Equal(report.Score, new WiredReport(shown.World.Wired.Heroes[Race.Red].Stats, run.Options.Difficulty).Score);
    }
}
