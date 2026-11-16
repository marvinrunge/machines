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
    public void A_start_on_the_crab_has_the_free_bolter_on_top_owns_the_grunt_as_well_and_keeps_all_the_capital()
    {
        var profile = new WiredProfile { LastHero = new WiredHeroSheet { Chassis = "geo1" } };

        var draft = new HeroDraft(Data, profile);

        Assert.Equal("geo1", draft.Chassis);
        Assert.Equal([WiredBalance.StartWeapon], draft.State.Loadout.Battery(Mounting.Top).Select(i => i.Catalogue));
        Assert.Contains(draft.State.Items, i => i is { Kind: WiredKind.Chassis, Catalogue: WiredBalance.StartChassis });
        Assert.Equal(WiredBalance.StartCapital(WiredDifficulty.Normal), draft.Bank);
        Assert.Equal(draft.Capital, draft.Bank);
    }

    [Fact]
    public void Changing_to_the_crab_puts_the_free_bolter_on_its_top_for_nothing()
    {
        var draft = NewDraft();

        draft.Wear("geo1");

        Assert.Equal("geo1", draft.Chassis);
        Assert.Equal([WiredBalance.StartWeapon], Worn(draft));
        Assert.Equal([WiredBalance.StartWeapon], draft.State.Loadout.Battery(Mounting.Top).Select(i => i.Catalogue));
        Assert.Equal(draft.Capital, draft.Bank);
    }

    [Fact]
    public void Only_what_the_first_wave_offers_is_for_sale()
    {
        var draft = NewDraft();

        Assert.Contains("FLAME_THROWER2", draft.ForSale(WiredKind.Weapon));
        Assert.DoesNotContain("PULSE_RIFLE", draft.ForSale(WiredKind.Weapon));
    }

    // ---- batteries -----------------------------------------------------------------------------------

    private static string[] Battery(HeroDraft draft, Mounting mounting) => draft.State.Loadout.Battery(mounting).Select(i => i.Catalogue).ToArray();

    /// <summary>The weapons in the inventory: on another chassis than the first, that chassis lies there too.</summary>
    private static WiredItem[] Guns(HeroDraft draft) => draft.State.Inventory.Where(i => i.Kind == WiredKind.Weapon).ToArray();

    private static string[] SpareGuns(HeroDraft draft) => Guns(draft).Select(i => i.Catalogue).ToArray();

    /// <summary>The Reaper of the draft: a medium left slot, its free bolter on the light right one.</summary>
    private static HeroDraft OnTheReaper()
    {
        var draft = NewDraft("reaper2");
        draft.Wear("reaper2");
        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Right));
        return draft;
    }

    [Fact]
    public void The_same_weapon_bought_onto_its_slot_joins_the_battery_and_each_copy_is_paid()
    {
        var draft = OnTheReaper();
        var price = draft.Price("BOLTER");

        Assert.Null(draft.Buy("BOLTER", Left));
        Assert.Null(draft.Buy("BOLTER", Left));

        Assert.Equal(["BOLTER", "BOLTER"], Battery(draft, Mounting.Left));
        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Right));
        Assert.Empty(Guns(draft));
        Assert.Equal(draft.Capital - 2 * price, draft.Bank);
        Assert.Contains(new WiredStartItem("BOLTER", Left, 2), draft.Options.Items);
    }

    [Fact]
    public void A_full_battery_refuses_another_copy_and_nothing_is_paid()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", Left);
        draft.Buy("BOLTER", Left);
        var bank = draft.Bank;

        Assert.Equal("a medium slot carries two at most", draft.WhyNotBuy("BOLTER", Left));
        Assert.Equal("a medium slot carries two at most", draft.Buy("BOLTER", Left));
        Assert.Equal("a light slot carries one weapon", draft.Buy("BOLTER", Right));

        Assert.Equal(bank, draft.Bank);
        Assert.Equal(2, Battery(draft, Mounting.Left).Length);
    }

    [Fact]
    public void Replacing_takes_the_whole_battery_off_into_the_inventory()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", Left);
        draft.Buy("BOLTER", Left);

        Assert.Null(draft.Buy("BOLTER", Left, replace: true));

        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Left));
        Assert.Equal(["BOLTER", "BOLTER"], SpareGuns(draft));
        Assert.Equal(draft.Capital - 3 * draft.Price("BOLTER"), draft.Bank);
    }

    [Fact]
    public void Another_weapon_replaces_a_battery_without_being_told_to()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", Left);
        draft.Buy("BOLTER", Left);

        Assert.Null(draft.Buy("ELECTRIC_CHARGE", Left));

        Assert.Equal(["ELECTRIC_CHARGE"], Battery(draft, Mounting.Left));
        Assert.Equal(["BOLTER", "BOLTER"], SpareGuns(draft));
    }

    [Fact]
    public void A_spare_put_onto_its_weapons_slot_joins_the_battery_and_costs_nothing_more()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", null);
        draft.Buy("BOLTER", Left);
        var bank = draft.Bank;
        var spare = Guns(draft).Single();

        Assert.Null(draft.Put(spare.Id, Left));

        Assert.Equal(["BOLTER", "BOLTER"], Battery(draft, Mounting.Left));
        Assert.Empty(Guns(draft));
        Assert.Equal(bank, draft.Bank);
    }

    [Fact]
    public void The_free_bolter_joins_a_battery_and_stays_free()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", Left);
        var bank = draft.Bank;

        Assert.Null(draft.Put(draft.State.Loadout.Weapon(Mounting.Right)!.Id, Left));

        Assert.Equal(["BOLTER", "BOLTER"], Battery(draft, Mounting.Left));
        Assert.Empty(Battery(draft, Mounting.Right));
        Assert.Equal(bank, draft.Bank);
        Assert.Equal(1, draft.State.Items.Count(i => i.Catalogue == "BOLTER" && i.Paid == 0));
        // The free one leads the options still, as the run needs it.
        Assert.Equal(new WiredStartItem("BOLTER", Left, 2), draft.Options.Items[0]);
    }

    [Fact]
    public void Taking_off_takes_one_copy_and_the_battery_stays_together()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", Left);
        draft.Buy("BOLTER", Left);
        var bank = draft.Bank;

        draft.TakeOff(draft.State.Loadout.Weapon(Mounting.Left)!.Id);

        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Left));
        Assert.Equal(["BOLTER"], SpareGuns(draft));
        Assert.Equal(bank, draft.Bank);

        // The spare given back returns its price; the free bolter on the right is left alone.
        Assert.True(draft.GiveBack(Guns(draft).Single().Id));
        Assert.Equal(bank + draft.Price("BOLTER"), draft.Bank);
        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Right));
    }

    [Fact]
    public void A_copy_moved_to_another_slot_leaves_the_rest_of_its_battery_together()
    {
        var draft = NewDraft("grunt2", "grunt3");
        draft.Wear("grunt3");
        draft.Buy("BOLTER", Right);
        Assert.Equal(["BOLTER", "BOLTER"], Battery(draft, Mounting.Right));
        var bank = draft.Bank;

        Assert.Null(draft.Put(draft.State.Loadout.Battery(Mounting.Right)[0].Id, Left));

        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Left));
        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Right));
        Assert.Equal(bank, draft.Bank);

        // And back: it joins the right one again.
        Assert.Null(draft.Put(draft.State.Loadout.Weapon(Mounting.Left)!.Id, Right));
        Assert.Equal(["BOLTER", "BOLTER"], Battery(draft, Mounting.Right));
        Assert.Empty(Battery(draft, Mounting.Left));
    }

    [Fact]
    public void A_battery_moves_with_its_chassis_and_what_the_new_slot_cannot_carry_falls_off()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", Left);
        draft.Buy("BOLTER", Left);
        var bank = draft.Bank;

        // grunt1 has one light slot, on the right: the free bolter stays, the battery cannot follow.
        draft.Wear(WiredBalance.StartChassis);
        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Right));
        Assert.Equal(["BOLTER", "BOLTER"], SpareGuns(draft));

        draft.Wear("reaper2");
        Assert.Null(draft.Put(Guns(draft).First().Id, Left));
        Assert.Null(draft.Put(Guns(draft).Single().Id, Left));
        Assert.Equal(["BOLTER", "BOLTER"], Battery(draft, Mounting.Left));
        Assert.Equal(bank, draft.Bank);
    }

    [Fact]
    public void What_the_draft_shows_with_a_battery_is_what_the_run_starts_with()
    {
        var draft = OnTheReaper();
        draft.Buy("BOLTER", Left);
        draft.Buy("BOLTER", Left);

        var state = WiredState.Create(Data, Race.Red, draft.Options, out var bank);

        Assert.Equal(draft.Bank, bank);
        Assert.Equal(draft.State.Loadout.Worn.Select(i => (i.Id, i.Catalogue)), state.Loadout.Worn.Select(i => (i.Id, i.Catalogue)));
        Assert.Equal(2, state.Loadout.Battery(Mounting.Left).Count);
    }

    [Fact]
    public void The_last_hero_comes_back_with_its_batteries()
    {
        var profile = new WiredProfile();
        profile.Unlocks.Add("reaper2");
        var first = new HeroDraft(Data, profile);
        first.Wear("reaper2");
        first.Buy("BOLTER", Left);
        first.Buy("BOLTER", Left);

        profile.LastHero = first.Sheet;
        var again = new HeroDraft(Data, profile);

        Assert.Equal(["BOLTER", "BOLTER"], Battery(again, Mounting.Left));
        Assert.Equal(first.Bank, again.Bank);
        Assert.Equal(first.Sheet.Items, again.Sheet.Items);
    }

    [Fact]
    public void A_kept_count_beyond_what_the_slot_carries_buys_no_more_than_it_carries()
    {
        var profile = new WiredProfile
        {
            LastHero = new WiredHeroSheet { Items = [new WiredStartItem("BOLTER", Right, 3)] },
        };

        var draft = new HeroDraft(Data, profile);

        Assert.Equal(["BOLTER"], Battery(draft, Mounting.Right));
        Assert.Empty(Spares(draft));
        Assert.Equal(draft.Capital, draft.Bank);
    }
}
