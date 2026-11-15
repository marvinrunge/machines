using Machines.Game;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Tests.Sim;

/// <summary>
/// The battle account in the local profile: what every account starts with, what research and purchases cost and who pays,
/// what a battle and a Wired run bring, and that the battle section survives the file, Wired's writes and a held file.
/// </summary>
public class BattleAccountTests
{
    private static LocalBattleAccount NewAccount(Action<BattleProfile>? setup = null)
    {
        var profile = new WiredProfile();
        setup?.Invoke(profile.Battle);
        return new LocalBattleAccount(profile);
    }

    private static void InAFolder(Action<string> test)
    {
        var folder = Path.Combine(Path.GetTempPath(), $"battle-{Guid.NewGuid():N}");
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
    public void A_new_account_is_on_the_first_grunt_with_its_bolter_and_nothing_else()
    {
        var account = NewAccount();

        Assert.True(account.Researched(WiredBalance.StartChassis));
        Assert.True(account.Owned(WiredBalance.StartChassis));
        Assert.Equal(1, account.Mk(WiredBalance.StartWeapon));
        Assert.Equal(0, account.Xp(WiredBalance.StartChassis));
        Assert.Equal(0, account.FreeXp);
        Assert.Equal(0, account.Credits);
        Assert.Equal(0, account.Battles);
        Assert.Equal([WiredBalance.StartChassis], account.Profile.Battle.Researched);
        Assert.Equal(2, account.Profile.Battle.Owned.Count);
    }

    [Fact]
    public void Prices_are_ten_times_wireds_and_research_three_times()
    {
        Assert.Equal(330, BattleRules.ResearchCost("grunt2"));
        Assert.Equal(1100, BattleRules.ChassisPrice("grunt2"));
        Assert.Equal(1000, BattleRules.Price("AUTO_CANNON"));
        Assert.Equal(800, BattleRules.Price("AUTO_CANNON", 2));
        Assert.Equal(1300, BattleRules.Price("AUTO_CANNON", 3));
        Assert.Equal(900, BattleRules.Price("plates"));
        Assert.Equal(3200, BattleRules.Price("plates", 3));
        Assert.Equal(2000, BattleRules.Price("nightvision"));
        Assert.Null(BattleRules.Price("nightvision", 2));
        Assert.Equal(9000, BattleRules.Price("vortex"));
        Assert.Null(BattleRules.Price("vortex", 2));
        Assert.Equal(0, BattleRules.ChassisPrice(WiredBalance.StartChassis));
    }

    [Fact]
    public void Research_is_paid_by_the_richest_parent_first_then_the_other_then_free_xp()
    {
        // Assassin 3 costs 1440 XP and comes after Assassin 2 or Grunt 3.
        var account = NewAccount(book =>
        {
            book.Researched.Add("grunt3");
            book.Xp["reaper2"] = 300;
            book.Xp["grunt3"] = 1000;
            book.FreeXp = 500;
        });

        Assert.Null(account.Research("reaper3"));

        Assert.True(account.Researched("reaper3"));
        Assert.Equal(0, account.Xp("grunt3"));
        Assert.Equal(0, account.Xp("reaper2"));
        Assert.Equal(360, account.FreeXp);
        Assert.False(account.Owned("reaper3"));
        Assert.DoesNotContain("grunt3", account.Profile.Battle.Xp.Keys);
    }

    [Fact]
    public void Research_takes_no_more_than_it_costs()
    {
        var account = NewAccount(book =>
        {
            book.Xp[WiredBalance.StartChassis] = 500;
            book.FreeXp = 40;
        });

        Assert.Null(account.Research("grunt2"));

        Assert.Equal(170, account.Xp(WiredBalance.StartChassis));
        Assert.Equal(40, account.FreeXp);
    }

    [Fact]
    public void Research_is_refused_without_a_parent_without_the_xp_and_twice()
    {
        var account = NewAccount(book =>
        {
            book.Xp[WiredBalance.StartChassis] = 300;
            book.FreeXp = 29;
        });

        var closed = BattleRules.WhyNotResearch(account, "reaper2");
        Assert.Equal("Research {0} first", closed!.Words);
        Assert.Equal(new[] { "grunt2" }, closed.Args.Single());

        var shortOf = account.Research("grunt2");
        Assert.Equal("Needs {0} XP on {1}", shortOf!.Words);
        Assert.Equal(330, shortOf.Args[0]);
        Assert.Equal(new[] { WiredBalance.StartChassis }, shortOf.Args[1]);
        Assert.False(account.Researched("grunt2"));
        Assert.Equal(300, account.Xp(WiredBalance.StartChassis));

        Assert.Equal("that is researched already", account.Research(WiredBalance.StartChassis)!.Words);
        Assert.Equal("no such chassis", account.Research("tank9")!.Words);
    }

    [Fact]
    public void A_chassis_is_bought_once_researched()
    {
        var account = NewAccount(book => book.Credits = 5000);

        Assert.Equal("Research {0} first", account.Buy("grunt2")!.Words);
        account.Profile.Battle.Researched.Add("grunt2");

        Assert.Null(account.Buy("grunt2"));

        Assert.True(account.Owned("grunt2"));
        Assert.Equal(3900, account.Credits);
        Assert.Equal("you own that already", account.Buy("grunt2")!.Words);
    }

    [Fact]
    public void Items_are_bought_a_mk_at_a_time_for_credits()
    {
        var account = NewAccount(book => book.Credits = 2500);

        Assert.Equal("Buy Mk {0} first", account.Buy("AUTO_CANNON", 2)!.Words);
        Assert.Null(account.Buy("AUTO_CANNON"));
        Assert.Null(account.Buy("AUTO_CANNON", 2));
        Assert.Equal(2, account.Mk("AUTO_CANNON"));
        Assert.Equal(700, account.Credits);

        var dear = account.Buy("AUTO_CANNON", 3);
        Assert.Equal("Needs {0} Cr", dear!.Words);
        Assert.Equal(1300, dear.Args[0]);
        Assert.Equal(2, account.Mk("AUTO_CANNON"));
        Assert.Equal(700, account.Credits);

        Assert.Equal("that has no higher Mk", account.Buy("BOLTER", 4)!.Words);
        Assert.Equal("that is not offered in a battle", account.Buy("treachery")!.Words);
        Assert.Equal("no such item", account.Buy("overload")!.Words);
    }

    [Fact]
    public void A_battle_brings_xp_on_its_chassis_free_xp_and_credits_and_says_what_came_within_reach()
    {
        var account = NewAccount();

        var opened = account.Note(new BattleNote(WiredBalance.StartChassis, 250, 13, 1500, Won: true));

        Assert.Equal(250, account.Xp(WiredBalance.StartChassis));
        Assert.Equal(13, account.FreeXp);
        Assert.Equal(1500, account.Credits);
        Assert.Equal(1, account.Battles);
        Assert.Equal(1, account.Wins);
        Assert.Empty(opened);

        opened = account.Note(new BattleNote(WiredBalance.StartChassis, 100, 5, 600));

        Assert.Equal(["grunt2"], opened);
        Assert.Equal(["grunt2"], account.Fresh);
        Assert.Equal(2, account.Battles);
        Assert.Equal(1, account.Wins);
        Assert.Equal(["grunt2"], account.TakeFresh());
        Assert.Empty(account.Fresh);
    }

    [Fact]
    public void A_wired_run_gives_its_final_chassis_less_and_is_no_battle()
    {
        var note = BattleNote.OfWired("reaper2", 12);

        Assert.Equal("reaper2", note.Chassis);
        Assert.Equal(BattleBalance.WiredXpPerWave * 12, note.Xp);
        Assert.Equal(BattleBalance.WiredCreditsPerXp * note.Xp, note.Credits);
        Assert.Equal(0, note.FreeXp);

        var account = NewAccount();
        account.Note(note);

        Assert.Equal(120, account.Xp("reaper2"));
        Assert.Equal(360, account.Credits);
        Assert.Equal(0, account.Battles);
    }

    [Fact]
    public void The_file_holds_the_battle_section_and_wireds_runs_keep_it() => InAFolder(path =>
    {
        var profile = new WiredProfile();
        var account = new LocalBattleAccount(profile, path);
        account.Note(new BattleNote(WiredBalance.StartChassis, 400, 20, 3000, Won: true));
        Assert.Null(account.Research("grunt2"));
        Assert.Null(account.Buy("grunt2"));
        Assert.Null(account.Buy("FLAME_THROWER2"));
        account.LastHero = new BattleHeroSheet
        {
            Name = "Kai",
            Colour = Race.Green,
            Chassis = "grunt2",
            Items = [new BattleItem("FLAME_THROWER2", WiredSlot.Weapon(Mounting.Left))],
            Map = "micey",
            Lives = 3,
            Win = BattleWin.Capture,
        };
        Assert.True(account.Save());

        // A Wired run ends and its ledger writes the file: the battle section stays.
        new WiredLedger(path).Tell(new WiredRunResult("m_desert", WiredDifficulty.Normal, "Kai", "grunt1", 100, new WiredStats { WavesCleared = 3 }));
        var back = new LocalBattleAccount(WiredProfile.Load(path), path);

        Assert.Equal(3, WiredProfile.Load(path).Totals.Waves);
        Assert.Equal(70, back.Xp(WiredBalance.StartChassis));
        Assert.Equal(20, back.FreeXp);
        Assert.Equal(3000 - 1100 - 800, back.Credits);
        Assert.True(back.Researched("grunt2"));
        Assert.True(back.Owned("grunt2"));
        Assert.Equal(1, back.Mk("FLAME_THROWER2"));
        Assert.Equal(1, back.Wins);
        Assert.Equal("Kai", back.LastHero!.Name);
        Assert.Equal(Race.Green, back.LastHero.Colour);
        Assert.Equal("micey", back.LastHero.Map);
        Assert.Equal(3, back.LastHero.Lives);
        Assert.Equal(BattleWin.Capture, back.LastHero.Win);
        Assert.Equal("FLAME_THROWER2", back.LastHero.Items.Single().Catalogue);
        Assert.Equal(WiredSlot.Weapon(Mounting.Left), back.LastHero.Items.Single().Slot);
    });

    [Theory]
    [InlineData("""{ "Unlocks": ["reaper2"] }""")]
    [InlineData("""{ "Battle": null }""")]
    [InlineData("""{ "Battle": { "Xp": null, "Researched": null, "Owned": null, "Fresh": null, "LastHero": { "Items": null, "Name": null, "Chassis": null, "Map": null, "Lives": 99, "Colour": 99, "Win": 99 } } }""")]
    [InlineData("""{ "Battle": { "Xp": { "grunt1": -5 }, "FreeXp": -1, "Credits": -9, "Researched": ["tank9", "grunt2"], "Owned": { "NO_SUCH": 2, "BOLTER": 7 }, "Wins": 4 } }""")]
    public void A_file_without_or_with_holes_in_its_battle_section_starts_the_account_afresh(string json) => InAFolder(path =>
    {
        File.WriteAllText(path, json);

        var account = new LocalBattleAccount(WiredProfile.Load(path), path);

        Assert.True(account.Researched(WiredBalance.StartChassis));
        Assert.True(account.Owned(WiredBalance.StartChassis));
        Assert.InRange(account.Mk(WiredBalance.StartWeapon), 1, 3);
        Assert.Equal(0, account.Xp(WiredBalance.StartChassis));
        Assert.True(account.FreeXp >= 0 && account.Credits >= 0);
        Assert.DoesNotContain("tank9", account.Profile.Battle.Researched);
        Assert.DoesNotContain("NO_SUCH", account.Profile.Battle.Owned.Keys);
        Assert.True(account.Wins <= account.Battles);
        if (account.LastHero is { } hero)
        {
            Assert.InRange(hero.Lives, 1, 5);
            Assert.True(Enum.IsDefined(hero.Colour));
            Assert.True(Enum.IsDefined(hero.Win));
            Assert.NotNull(hero.Items);
        }
        Assert.True(account.Save());
        Assert.True(new LocalBattleAccount(WiredProfile.Load(path)).Researched(WiredBalance.StartChassis));
    });

    [Fact]
    public void Research_and_purchases_are_taken_back_when_the_file_is_held() => InAFolder(path =>
    {
        var profile = new WiredProfile();
        profile.Battle.Xp[WiredBalance.StartChassis] = 400;
        profile.Battle.Credits = 5000;
        profile.Save(path);
        profile.Save(path);

        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var held = new LocalBattleAccount(WiredProfile.Load(path), path);

            Assert.Equal("the profile cannot be written just now", held.Research("grunt2")!.Words);
            Assert.Equal("the profile cannot be written just now", held.Buy("AUTO_CANNON")!.Words);
            Assert.False(held.Researched("grunt2"));
            Assert.Equal(0, held.Mk("AUTO_CANNON"));
            Assert.False(held.Save());
        }

        var back = new LocalBattleAccount(WiredProfile.Load(path), path);
        Assert.Equal(400, back.Xp(WiredBalance.StartChassis));
        Assert.Equal(5000, back.Credits);
    });

    [Fact]
    public void A_battle_that_cannot_be_written_waits_and_is_counted_once() => InAFolder(path =>
    {
        new WiredProfile().Save(path);
        var ledger = new BattleLedger(path);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
        {
            Assert.Null(ledger.Tell(new BattleNote(WiredBalance.StartChassis, 200, 10, 1200)));
            Assert.Null(ledger.Tell(BattleNote.OfWired(WiredBalance.StartChassis, 15)));
            Assert.True(ledger.Waiting);
        }

        Assert.Equal(0, new LocalBattleAccount(WiredProfile.Load(path)).Credits);
        Assert.Equal(["grunt2"], ledger.Write());
        Assert.False(ledger.Waiting);
        Assert.Empty(ledger.Write()!);
        var account = new LocalBattleAccount(WiredProfile.Load(path));
        Assert.Equal(350, account.Xp(WiredBalance.StartChassis));
        Assert.Equal(10, account.FreeXp);
        Assert.Equal(1200 + 450, account.Credits);
        Assert.Equal(1, account.Battles);
        Assert.Equal(["grunt2"], account.Fresh);
        Assert.Empty(WiredProfile.Load(path).Fresh);
    });
}
