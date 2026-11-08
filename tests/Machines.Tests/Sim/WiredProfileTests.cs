using Machines.Game;
using Machines.Sim;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Tests.Sim;

/// <summary>What stays of Wired between runs: the file, the unlocks, the milestones and the records.</summary>
public class WiredProfileTests
{
    private static WiredRunResult Run(int waves, WiredDifficulty difficulty = WiredDifficulty.Normal, int squad = 0, int kills = 0, int bosses = 0,
        int wrecks = 0, int crates = 0, int clean = 0, long score = 0, string arena = "m_desert", string hero = "Kai") =>
        new(arena, difficulty, hero, "grunt1", score,
            new WiredStats { WavesCleared = waves, Kills = kills, BossesKilled = bosses, WrecksCollected = wrecks, CratesCollected = crates, CleanWaves = clean, LargestSquad = squad });

    [Fact]
    public void A_new_profile_opens_the_grunt_two_arenas_and_the_base_chips()
    {
        var profile = new WiredProfile();

        Assert.True(profile.CanStartOn(WiredBalance.StartChassis));
        Assert.False(profile.CanStartOn("reaper2"));
        Assert.False(profile.CanStartOn("grunt3"));
        Assert.Equal(["m_desert", "m_green"], WiredProfile.Arenas.Where(a => profile.CanPlay(a.Scenario)).Select(a => a.Scenario));
        Assert.False(profile.CanPlay(WiredDifficulty.Hardcore));
        Assert.Equal(12, profile.ChipPool.Count);
    }

    [Fact]
    public void Every_chip_is_in_the_base_pool_or_has_a_milestone()
    {
        Assert.All(WiredChips.All, chip => Assert.True(chip.Base || WiredProfile.Milestones.Any(m => m.Chip == chip.Id), chip.Id));
        Assert.All(WiredProfile.Milestones, m => Assert.False(WiredChips.Find(m.Chip)!.Base, m.Chip));
    }

    [Fact]
    public void Every_chassis_to_start_on_is_in_the_catalogue()
    {
        Assert.All(WiredProfile.StartChassis, start => Assert.NotNull(WiredCatalogue.FindChassis(start.Chassis)));
    }

    [Theory]
    [InlineData(9, 0, new string[0])]
    [InlineData(10, 0, new[] { "reaper2", "m_moon" })]
    [InlineData(10, 3, new[] { "reaper2", "overseer2", "m_moon" })]
    [InlineData(15, 0, new[] { "reaper2", "knight3", "ballista3", "m_moon" })]
    [InlineData(20, 0, new[] { "reaper2", "knight3", "ballista3", "commander4", "boss1", "m_moon", "m_dales" })]
    public void Waves_cleared_open_chassis_and_arenas(int waves, int squad, string[] opened)
    {
        var profile = new WiredProfile();

        var news = profile.Note(Run(waves, squad: squad));

        Assert.Equal(opened, news);
        Assert.All(opened.Where(o => !o.StartsWith("m_")), chassis => Assert.True(profile.CanStartOn(chassis)));
    }

    [Fact]
    public void What_is_opened_is_named_once()
    {
        var profile = new WiredProfile();

        Assert.Contains("reaper2", profile.Note(Run(10)));
        Assert.DoesNotContain("reaper2", profile.Note(Run(10)));
    }

    [Fact]
    public void Hardcore_opens_on_hard_only()
    {
        var profile = new WiredProfile();

        profile.Note(Run(15));
        Assert.False(profile.CanPlay(WiredDifficulty.Hardcore));

        profile.Note(Run(14, WiredDifficulty.Hard));
        Assert.False(profile.CanPlay(WiredDifficulty.Hardcore));

        Assert.Contains(WiredProfile.Hardcore, profile.Note(Run(15, WiredDifficulty.Hard)));
        Assert.True(profile.CanPlay(WiredDifficulty.Hardcore));
    }

    [Fact]
    public void Milestones_count_over_all_runs_and_widen_the_pool()
    {
        var profile = new WiredProfile();

        profile.Note(Run(1, kills: 60));
        Assert.DoesNotContain("revenge", profile.ChipPool);

        Assert.Contains("revenge", profile.Note(Run(1, kills: 40)));
        Assert.Contains("revenge", profile.ChipPool);
        Assert.Equal(13, profile.ChipPool.Count);
        Assert.Equal(100, profile.Count(WiredMeasure.Kills));
    }

    [Fact]
    public void Clean_waves_count_the_best_run_not_the_sum()
    {
        var profile = new WiredProfile();

        profile.Note(Run(1, clean: 4));
        profile.Note(Run(1, clean: 3));

        Assert.Equal(4, profile.Count(WiredMeasure.CleanWaves));
        Assert.DoesNotContain("lastditch", profile.Unlocks);
    }

    [Fact]
    public void Records_keep_the_best_per_arena_and_difficulty()
    {
        var profile = new WiredProfile();

        profile.Note(Run(8, score: 500, kills: 30, hero: "Kai"));
        profile.Note(Run(5, score: 200, kills: 60, hero: "Ute"));
        profile.Note(Run(3, WiredDifficulty.Hard, score: 90));

        var best = profile.Record("m_desert", WiredDifficulty.Normal);
        Assert.Equal(8, best.Waves);
        Assert.Equal(500, best.Score);
        Assert.Equal(60, best.Kills);
        Assert.Equal("Kai", best.Hero);
        Assert.Equal(3, profile.Record("m_desert", WiredDifficulty.Hard).Waves);
        Assert.Equal(0, profile.Record("m_green", WiredDifficulty.Normal).Waves);
        Assert.Equal(3, profile.Totals.Runs);
    }

    [Fact]
    public void The_file_holds_all_of_it()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wired-{Guid.NewGuid():N}.json");
        try
        {
            var profile = new WiredProfile
            {
                LastHero = new WiredHeroSheet
                {
                    Name = "Kai",
                    Colour = Race.Green,
                    Chassis = "reaper2",
                    Difficulty = WiredDifficulty.Hard,
                    Arena = "m_green",
                    Items = [new WiredStartItem("BOLTER", WiredSlot.Weapon(Mounting.Right)), new WiredStartItem("FLAME_THROWER2", null)],
                },
            };
            profile.Note(Run(10, score: 700, kills: 120));

            profile.Save(path);
            var back = WiredProfile.Load(path);

            Assert.Equal(profile.Unlocks, back.Unlocks);
            Assert.Equal(profile.Record("m_desert", WiredDifficulty.Normal), back.Record("m_desert", WiredDifficulty.Normal));
            Assert.Equal(profile.Totals, back.Totals);
            Assert.Equal("Kai", back.LastHero!.Name);
            Assert.Equal(Race.Green, back.LastHero.Colour);
            Assert.Equal(WiredDifficulty.Hard, back.LastHero.Difficulty);
            Assert.Equal(profile.LastHero!.Items, back.LastHero.Items);
            Assert.False(File.Exists(path + ".new"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void A_broken_file_gives_a_new_profile_and_says_so()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wired-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, "{ not json");
            string? warning = null;

            var profile = WiredProfile.Load(path, text => warning = text);

            Assert.NotNull(warning);
            Assert.Empty(profile.Unlocks);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static void InAFolder(Action<string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"wired-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            test(Path.Combine(folder, "wired.json"));
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void A_broken_file_is_kept_and_the_last_good_one_stands_in()
    {
        InAFolder(path =>
        {
            var profile = new WiredProfile();
            profile.Note(Run(10));
            profile.Save(path);
            profile.Note(Run(15));
            profile.Save(path);
            File.WriteAllText(path, "{ not json");

            var back = WiredProfile.Load(path);

            Assert.Equal("{ not json", File.ReadAllText(path + ".bad"));
            Assert.Contains("reaper2", back.Unlocks);
            Assert.DoesNotContain("knight3", back.Unlocks);
            back.Save(path);
            Assert.Contains("reaper2", WiredProfile.Load(path).Unlocks);
        });
    }

    [Fact]
    public void A_file_that_cannot_be_opened_just_now_is_not_written_over()
    {
        InAFolder(path =>
        {
            var profile = new WiredProfile();
            profile.Note(Run(15));
            profile.Save(path);
            var text = File.ReadAllText(path);

            using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                string? warning = null;
                var held = WiredProfile.Load(path, words => warning = words);

                Assert.True(held.Held);
                Assert.NotNull(warning);
                held.LastHero = new WiredHeroSheet { Name = "Kai" };
                held.Save(path);
            }

            Assert.Equal(text, File.ReadAllText(path));
            Assert.Contains("knight3", WiredProfile.Load(path).Unlocks);
        });
    }

    [Theory]
    [InlineData(FileShare.None)]
    [InlineData(FileShare.Read)]
    public void A_run_that_cannot_be_written_waits_and_is_counted_once(FileShare share) => InAFolder(path =>
    {
        new WiredProfile().Save(path);
        var ledger = new WiredLedger(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, share))
        {
            Assert.Null(ledger.Tell(Run(10)));
            Assert.True(ledger.Waiting);
            Assert.Null(ledger.Write());
        }

        Assert.Equal(0, WiredProfile.Load(path).Totals.Waves);
        Assert.Equal(["reaper2", "m_moon"], ledger.Write());
        Assert.False(ledger.Waiting);
        Assert.Empty(ledger.Write()!);
        Assert.Equal(10, WiredProfile.Load(path).Totals.Waves);
    });

    [Fact]
    public void Runs_that_waited_go_in_together() => InAFolder(path =>
    {
        new WiredProfile().Save(path);
        var ledger = new WiredLedger(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(ledger.Tell(Run(3)));
            Assert.Null(ledger.Tell(Run(4)));
        }

        ledger.Write();
        Assert.Equal(7, WiredProfile.Load(path).Totals.Waves);
    });

    [Fact]
    public void Saving_leaves_no_half_written_file_and_keeps_what_stood_there()
    {
        InAFolder(path =>
        {
            var profile = new WiredProfile();
            profile.Save(path);
            var first = File.ReadAllText(path);
            profile.Note(Run(10));
            profile.Save(path);

            Assert.False(File.Exists(path + ".new"));
            Assert.Equal(first, File.ReadAllText(path + ".bak"));
            Assert.Contains("reaper2", WiredProfile.Load(path).Unlocks);
        });
    }

    [Theory]
    [InlineData("""{ "Unlocks": null, "Records": null, "Totals": null, "LastHero": null }""")]
    [InlineData("""{ "Unlocks": [null, "reaper2"], "Records": { "m_desert/Normal": null }, "LastHero": { "Items": null, "Name": null, "Chassis": null, "Arena": null } }""")]
    [InlineData("""{ "LastHero": { "Items": [null, { "Catalogue": null }, { "Catalogue": "NO_SUCH_THING" }], "Colour": 99, "Difficulty": 99 } }""")]
    public void A_file_with_holes_in_it_reads_as_far_as_it_goes(string json)
    {
        InAFolder(path =>
        {
            File.WriteAllText(path, json);

            var profile = WiredProfile.Load(path);

            Assert.False(profile.CanPlay("m_moon"));
            Assert.Equal(new WiredRecord(), profile.Record("m_desert", WiredDifficulty.Normal));
            Assert.Equal(0, profile.Count(WiredMeasure.Waves));
            Assert.NotEmpty(profile.ChipPool);
            var draft = new HeroDraft(GameData.Load(RepoPaths.OriginalDataFolder()), profile);
            Assert.Equal(WiredBalance.StartChassis, draft.Chassis);
            Assert.NotNull(draft.State);
            profile.Note(Run(1));
            profile.Save(path);
            Assert.Equal(1, WiredProfile.Load(path).Totals.Runs);
        });
    }

    [Theory]
    [InlineData("Hero", "")]
    [InlineData("Held", "")]
    [InlineData("Kai", "Kai")]
    public void A_hero_kept_under_the_word_for_one_is_unnamed_again(string kept, string name)
    {
        InAFolder(path =>
        {
            new WiredProfile { LastHero = new WiredHeroSheet { Name = kept } }.Save(path);

            Assert.Equal(name, WiredProfile.Load(path).LastHero!.Name);
        });
    }

    [Fact]
    public void Unlocks_come_back_in_their_order()
    {
        InAFolder(path =>
        {
            var profile = new WiredProfile();
            profile.Unlocks.Add("b");
            profile.Unlocks.Add("B");
            profile.Save(path);

            Assert.Equal(["B", "b"], WiredProfile.Load(path).Unlocks);
        });
    }

    [Fact]
    public void No_file_is_a_new_profile()
    {
        Assert.Empty(WiredProfile.Load(Path.Combine(Path.GetTempPath(), $"wired-{Guid.NewGuid():N}.json")).Unlocks);
    }
}
