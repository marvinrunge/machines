using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>A hero being made in the hangar of Wired: what it costs, what stays on, and that the run starts with just that.</summary>
public class HeroDraftTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly WiredSlot Right = WiredSlot.Weapon(Mounting.Right);

    private static readonly WiredSlot Left = WiredSlot.Weapon(Mounting.Left);

    private static HeroDraft NewDraft(params string[] unlocks)
    {
        var profile = new WiredProfile();
        foreach (var unlock in unlocks)
            profile.Unlocks.Add(unlock);
        return new HeroDraft(Data, profile);
    }

    private static WiredItem Owned(HeroDraft draft, string catalogue) => draft.State.Items.First(i => i.Catalogue == catalogue);

    private static string[] Worn(HeroDraft draft) => draft.State.Loadout.Worn.Skip(1).Select(i => i.Catalogue).ToArray();

    private static string[] Spares(HeroDraft draft) => draft.State.Inventory.Select(i => i.Catalogue).ToArray();

    [Fact]
    public void A_new_hero_is_the_grunt_with_its_free_bolter_and_all_the_capital()
    {
        var draft = NewDraft();

        Assert.Equal(WiredBalance.StartChassis, draft.Chassis);
        Assert.Equal([WiredBalance.StartWeapon], Worn(draft));
        Assert.Equal(WiredBalance.StartCapital(WiredDifficulty.Normal), draft.Bank);
        Assert.Equal(draft.Capital, draft.Bank);
    }

    [Fact]
    public void Buying_into_a_taken_slot_keeps_what_sat_there()
    {
        var draft = NewDraft();

        Assert.Null(draft.Buy("FLAME_THROWER2", Right));

        Assert.Equal(["FLAME_THROWER2"], Worn(draft));
        Assert.Contains(WiredBalance.StartWeapon, Spares(draft));
        Assert.Equal(draft.Capital - draft.Price("FLAME_THROWER2"), draft.Bank);
    }

    [Fact]
    public void What_does_not_fit_or_cannot_be_paid_is_refused()
    {
        var draft = NewDraft();

        Assert.NotNull(draft.Buy("FLAME_THROWER2", WiredSlot.Module(0)));
        Assert.NotNull(draft.Buy("FLAME_THROWER2", Left));
        Assert.Equal(draft.Capital, draft.Bank);

        Assert.Null(draft.Buy("AUTO_CANNON", Right));
        Assert.Equal("not enough BMUs", draft.Buy("AUTO_CANNON", null));
        Assert.Equal(["AUTO_CANNON"], Worn(draft));
    }

    [Fact]
    public void A_fit_is_named_before_the_price()
    {
        var draft = NewDraft();
        draft.Buy("AUTO_CANNON", Right);

        Assert.NotEqual("not enough BMUs", draft.WhyNotBuy("AUTO_CANNON", WiredSlot.Module(0)));
    }

    [Fact]
    public void Taking_off_and_putting_on_again_costs_nothing()
    {
        var draft = NewDraft();
        var bolter = Owned(draft, WiredBalance.StartWeapon);

        draft.TakeOff(bolter.Id);
        Assert.Empty(Worn(draft));
        Assert.Equal([WiredBalance.StartWeapon], Spares(draft));

        Assert.Null(draft.Put(Owned(draft, WiredBalance.StartWeapon).Id, Right));
        Assert.Equal([WiredBalance.StartWeapon], Worn(draft));
        Assert.Equal(draft.Capital, draft.Bank);
    }

    [Fact]
    public void Giving_back_returns_the_price_but_the_free_weapon_stays()
    {
        var draft = NewDraft();
        draft.Buy("FLAME_THROWER2", Right);

        Assert.False(draft.GiveBack(Owned(draft, WiredBalance.StartWeapon).Id));
        Assert.True(draft.GiveBack(Owned(draft, "FLAME_THROWER2").Id));

        Assert.Equal(draft.Capital, draft.Bank);
        Assert.DoesNotContain(draft.State.Items, i => i.Catalogue == "FLAME_THROWER2");
    }

    [Fact]
    public void The_grunts_weapon_stays_on_a_reaper_and_the_reapers_second_falls_off_on_the_way_back()
    {
        var draft = NewDraft("reaper2");

        draft.Wear("reaper2");
        Assert.Equal("reaper2", draft.Chassis);
        Assert.Equal([WiredBalance.StartWeapon], Worn(draft));
        Assert.Equal(draft.Capital, draft.Bank);

        Assert.Null(draft.Buy("FLAME_THROWER2", Left));
        var bank = draft.Bank;
        Assert.Equal(2, Worn(draft).Length);

        draft.Wear(WiredBalance.StartChassis);
        Assert.Equal([WiredBalance.StartWeapon], Worn(draft));
        Assert.Contains("FLAME_THROWER2", Spares(draft));
        Assert.Equal(bank, draft.Bank);
    }

    [Fact]
    public void A_harder_difficulty_gives_back_what_it_can_no_longer_pay_for()
    {
        var draft = NewDraft();
        draft.Buy("AUTO_CANNON", Right);

        draft.Choose(WiredDifficulty.Hard);

        Assert.Equal(WiredBalance.StartCapital(WiredDifficulty.Hard), draft.Capital);
        Assert.True(draft.Bank >= 0);
        Assert.Equal(draft.Capital - (draft.State.Items.Any(i => i.Catalogue == "AUTO_CANNON") ? draft.Price("AUTO_CANNON") : 0), draft.Bank);
    }

    [Fact]
    public void Another_difficulty_says_what_it_gave_back()
    {
        var draft = NewDraft();
        draft.Buy("AUTO_CANNON", Right);
        var easier = WiredDifficulty.Normal;

        Assert.Empty(draft.Choose(easier));

        Assert.Equal(["AUTO_CANNON"], draft.Choose(WiredDifficulty.Hardcore));
        Assert.DoesNotContain(draft.State.Items, i => i.Catalogue == "AUTO_CANNON");
    }

    [Fact]
    public void What_the_draft_shows_is_what_the_state_of_a_run_comes_to()
    {
        var draft = NewDraft("reaper2");
        draft.Wear("reaper2");
        draft.Buy("FLAME_THROWER2", Left);

        var state = WiredState.Create(Data, Race.Red, draft.Options, out var bank);

        Assert.Equal(draft.Bank, bank);
        Assert.Equal(draft.State.Loadout.Worn.Select(i => i.Catalogue), state.Loadout.Worn.Select(i => i.Catalogue));
        Assert.Equal(draft.State.Inventory.Select(i => i.Catalogue), state.Inventory.Select(i => i.Catalogue));
    }

    [Fact]
    public void A_run_starts_with_the_hero_of_the_draft()
    {
        var draft = NewDraft("reaper2");
        draft.Name = "Kai";
        draft.Wear("reaper2");
        draft.Buy("FLAME_THROWER2", Left);

        var world = MatchSetup.Create(Data, draft.Arena, draft.Arena,
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7,
            new MatchOptions { Wired = draft.Options });

        var hero = world.Wired!.Heroes[Race.Red];
        Assert.Equal("Kai", world.Wired.Options.HeroName);
        Assert.Equal("reaper2", hero.Loadout.Chassis.Catalogue);
        Assert.Equal(draft.State.Loadout.Worn.Select(i => i.Catalogue), hero.Loadout.Worn.Select(i => i.Catalogue));
        Assert.Equal(draft.Bank, (int)world.Races[Race.Red].Bmu);
        Assert.NotNull(world.HeroOf(Race.Red));
    }

    [Fact]
    public void The_last_hero_comes_back()
    {
        var profile = new WiredProfile();
        profile.Unlocks.Add("reaper2");
        var first = new HeroDraft(Data, profile) { Name = "Kai", Colour = Race.Blue, Arena = "m_green" };
        first.Wear("reaper2");
        first.Buy("FLAME_THROWER2", Left);

        profile.LastHero = first.Sheet;
        var again = new HeroDraft(Data, profile);

        Assert.Equal("Kai", again.Name);
        Assert.Equal(Race.Blue, again.Colour);
        Assert.Equal("m_green", again.Arena);
        Assert.Equal("reaper2", again.Chassis);
        Assert.Equal(Worn(first), Worn(again));
        Assert.Equal(first.Bank, again.Bank);
    }

    [Fact]
    public void A_last_hero_on_something_locked_starts_on_what_is_open()
    {
        var profile = new WiredProfile
        {
            LastHero = new WiredHeroSheet { Chassis = "knight3", Difficulty = WiredDifficulty.Hardcore, Arena = "m_dales" },
        };

        var draft = new HeroDraft(Data, profile);

        Assert.Equal(WiredBalance.StartChassis, draft.Chassis);
        Assert.Equal(WiredDifficulty.Normal, draft.Difficulty);
        Assert.Equal(WiredProfile.Arenas[0].Scenario, draft.Arena);
    }

    [Fact]
    public void Only_what_the_first_wave_offers_is_for_sale()
    {
        var draft = NewDraft();

        Assert.Contains("FLAME_THROWER2", draft.ForSale(WiredKind.Weapon));
        Assert.DoesNotContain("PULSE_RIFLE", draft.ForSale(WiredKind.Weapon));
    }
}
