using System.Numerics;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The shop of the hangar as the screen asks about it: the chassis tree, the decision it is there for, and what the sim says before a click.</summary>
public class HangarShopTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameWorld InHangar(int bmu = 100000)
    {
        var world = MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7,
            new MatchOptions { Wired = new WiredOptions() });
        world.Races[Race.Red].Bmu = bmu;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        return world;
    }

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static HangarShop Shop(GameWorld world) => new(world, Red(world));

    private static WiredChassis Chassis(string id) => WiredCatalogue.FindChassis(id)!;

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    private static WiredItem Buy(GameWorld world, string id)
    {
        var state = Red(world);
        if (WiredCatalogue.FindChassis(id) is null)
            state.Offer.Add(id);
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, id)));
        return state.Items[^1];
    }

    // ---- what goes straight onto a slot ------------------------------------------------------------

    private static readonly WiredSlot Right = WiredSlot.Weapon(Mounting.Right);

    [Fact]
    public void What_is_bought_onto_a_slot_is_the_new_one_and_never_an_older_spare()
    {
        var world = InHangar();
        var state = Red(world);
        var older = Buy(world, "BOLTER");
        state.Offer.Add("BOLTER");
        var offered = state.Offer.Count(o => o == "BOLTER");

        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right)));

        var worn = state.Loadout.Weapon(Mounting.Right)!;
        Assert.Equal("BOLTER", worn.Catalogue);
        Assert.NotEqual(older.Id, worn.Id);
        Assert.Contains(older, state.Inventory);
        Assert.Equal(offered - 1, state.Offer.Count(o => o == "BOLTER"));
    }

    [Fact]
    public void What_cannot_go_onto_the_slot_is_not_paid_for()
    {
        var world = InHangar();
        var state = Red(world);
        state.Offer.Add("PLASMA_CANNON2");
        var (bank, spares) = (world.Races[Race.Red].Bmu, state.Inventory.Count());

        Assert.Equal("the weapon is too big for this slot", Do(world, new WiredBuyCommand(Race.Red, "PLASMA_CANNON2", Right)));

        Assert.Equal(bank, world.Races[Race.Red].Bmu);
        Assert.Equal(spares, state.Inventory.Count());
        Assert.Contains("PLASMA_CANNON2", state.Offer);
    }

    [Fact]
    public void What_is_bought_onto_a_slot_gets_there_even_if_the_hangar_closes_right_behind_it()
    {
        var world = InHangar();
        var state = Red(world);
        state.Offer.Add("AUTO_CANNON");

        world.Issue(new WiredBuyCommand(Race.Red, "AUTO_CANNON", Right));
        world.Issue(new WiredHangarCommand(Race.Red, false));
        world.Step();

        Assert.Empty(world.Rejected);
        Assert.False(state.HangarOpen);
        Assert.Equal("AUTO_CANNON", state.Loadout.Weapon(Mounting.Right)?.Catalogue);
    }

    [Fact]
    public void A_chip_chosen_onto_a_socket_goes_there()
    {
        var world = InHangar();
        var state = Red(world);
        world.OfferChips(state);
        var chip = state.ChipChoice[1];

        Assert.Null(Do(world, new WiredChipChoiceCommand(Race.Red, 1, 2)));

        Assert.Equal(chip, state.Loadout.Chips[2]?.Catalogue);
        Assert.Empty(state.ChipChoice);
        Assert.DoesNotContain(state.Inventory, i => i.Catalogue == chip);
    }

    [Fact]
    public void A_chip_chosen_with_the_hangar_shut_waits_in_the_inventory()
    {
        var world = InHangar();
        var state = Red(world);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        world.OfferChips(state);
        var chip = state.ChipChoice[0];

        Assert.Null(Do(world, new WiredChipChoiceCommand(Race.Red, 0, 2)));

        Assert.Null(state.Loadout.Chips[2]);
        Assert.Contains(state.Inventory, i => i.Catalogue == chip);
    }

    // ---- what the shop says before a click ---------------------------------------------------------

    private static void Fill(GameWorld world)
    {
        var state = Red(world);
        while (state.Inventory.Count() < WiredBalance.InventoryLimit)
            state.Add(WiredKind.Weapon, "BOLTER", 0);
    }

    [Fact]
    public void A_full_inventory_is_known_before_a_click()
    {
        var world = InHangar();
        var shop = Shop(world);
        Assert.False(shop.Full);
        Assert.False(shop.Choice("grunt2", "AUTO_CANNON").Full);

        Fill(world);

        Assert.Equal(WiredBalance.InventoryLimit, shop.Spares);
        Assert.True(shop.Full);
        Assert.True(shop.Choice("grunt2", "AUTO_CANNON").Full);
        Assert.Equal("the inventory is full", Do(world, new WiredBuyCommand(Race.Red, "grunt2")));
    }

    [Fact]
    public void A_slot_that_grows_is_part_of_what_a_step_brings()
    {
        var world = InHangar();
        OnTheReaper(world);

        var step = Shop(world).Gain(Chassis("reaper3"));

        Assert.Equal(new SlotChange(Mounting.Right, SlotSize.Light, SlotSize.Medium), Assert.Single(step.Resized));
        Assert.False(step.Same);
    }

    [Fact]
    public void No_step_of_the_tree_leaves_the_hero_as_it_was()
    {
        foreach (var (from, to) in ChassisTree.Lines)
        {
            var world = InHangar();
            var state = Red(world);
            state.Loadout = HeroLoadout.Bare(new WiredItem(-1, WiredKind.Chassis, from));

            Assert.False(Shop(world).Gain(Chassis(to)).Same, $"{from} to {to}");
        }
    }

    [Theory]
    [InlineData(ChassisStep.Worn, ChassisStep.Owned, true, true, ChassisLine.Come)]
    [InlineData(ChassisStep.Owned, ChassisStep.Owned, true, true, ChassisLine.Come)]
    [InlineData(ChassisStep.Owned, ChassisStep.Owned, true, false, ChassisLine.Ahead)]
    [InlineData(ChassisStep.Worn, ChassisStep.Owned, false, false, ChassisLine.Ahead)]
    [InlineData(ChassisStep.Worn, ChassisStep.ForSale, true, true, ChassisLine.Next)]
    [InlineData(ChassisStep.Worn, ChassisStep.TooDear, true, false, ChassisLine.Next)]
    [InlineData(ChassisStep.Worn, ChassisStep.ForSale, false, true, ChassisLine.Ahead)]
    [InlineData(ChassisStep.Worn, ChassisStep.Closed, true, true, ChassisLine.Ahead)]
    public void A_line_of_the_tree_is_walked_only_in_a_run(ChassisStep from, ChassisStep to, bool reached, bool run, ChassisLine line) =>
        Assert.Equal(line, ChassisTree.Line(from, to, reached, run));

    [Fact]
    public void A_click_on_the_spot_of_a_deal_is_the_second_half_of_a_double_click_whatever_the_card()
    {
        var deals = new DealClicks();
        Assert.False(deals.Second(100, 100, 1000, 8));

        deals.Dealt(100, 100, 1000);

        Assert.True(deals.Second(100, 100, 1000 + DealClicks.Window - 1, 8));
        Assert.True(deals.Second(106, 94, 1200, 8));
        Assert.False(deals.Second(120, 100, 1200, 8));
        Assert.False(deals.Second(100, 100, 1000 + DealClicks.Window, 8));
    }

    [Fact]
    public void The_largest_squad_stays_when_the_squad_gets_smaller()
    {
        var world = InHangar();
        var state = Red(world);
        var key = Shop(world).Escorts().First();

        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));
        Assert.Equal(1, state.Stats.LargestSquad);
        world.Damage(world.Escorts(Race.Red).First(), 1_000_000, Race.Blue);
        world.Step();

        Assert.Empty(world.Escorts(Race.Red));
        Assert.Equal(1, state.Stats.LargestSquad);
    }

    [Fact]
    public void A_linked_recycler_says_what_selling_really_brings()
    {
        var chip = WiredChips.Find("recycler")!;

        Assert.Equal(87, WiredChips.SellPercent(chip.Value * (1 + WiredChips.LinkBonus)));
        Assert.Equal(WiredBalance.SellPercent, WiredChips.SellPercent(0f));
    }

    /// <summary>Over the way the tree shows, the hero ends on the Reaper.</summary>
    private static WiredItem OnTheReaper(GameWorld world)
    {
        Buy(world, "grunt2");
        var reaper = Buy(world, "reaper2");
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, reaper.Id)));
        return reaper;
    }

    [Fact]
    public void Every_chassis_has_a_place_of_its_own_on_the_tree()
    {
        var places = WiredCatalogue.Chassis.Select(c => ChassisTree.Place(c.Id)).ToList();

        Assert.Equal(places.Count, places.Distinct().Count());
        Assert.Equal((11, 8), (ChassisTree.Columns, ChassisTree.Rows));
        Assert.All(places, p => Assert.InRange(p.Column, 0, ChassisTree.Columns - 1));
        Assert.All(places, p => Assert.InRange(p.Row, 0, ChassisTree.Rows - 1));
        Assert.Equal(0, places.Min(p => p.Column));
        Assert.Equal(10, places.Max(p => p.Column));
    }

    [Theory]
    [InlineData("grunt1", 4, 0)]
    [InlineData("gorilla5", 5, 7)]
    [InlineData("dozer1", 0, 2)]
    [InlineData("builder3", 0, 3)]
    [InlineData("behemoth4", 0, 5)]
    [InlineData("titan6", 0, 7)]
    [InlineData("spy3", 10, 3)]
    [InlineData("spy5", 10, 5)]
    [InlineData("phantom6", 10, 7)]
    public void The_constructors_stand_at_the_far_left_and_the_spies_at_the_far_right(string chassis, int column, int row) =>
        Assert.Equal((column, row), ChassisTree.Place(chassis));

    [Theory]
    [InlineData("geo1", 8, 0)]
    [InlineData("geo3", 8, 1)]
    [InlineData("boss1", 8, 4)]
    [InlineData("boss3", 8, 5)]
    public void The_flyer_line_runs_down_column_eight_from_the_crab(string chassis, int column, int row) =>
        Assert.Equal((column, row), ChassisTree.Place(chassis));

    [Fact]
    public void The_line_from_the_glider_to_the_first_boss_runs_straight_down_its_column()
    {
        Assert.Contains(("geo3", "boss1"), ChassisTree.Lines);
        Assert.True(ChassisTree.TurnsAtOnce("geo3", "boss1"));
    }

    [Fact]
    public void The_lines_of_the_tree_are_the_catalogues_needs_and_run_downwards()
    {
        var lines = ChassisTree.Lines.ToList();

        Assert.Equal(WiredCatalogue.Chassis.Sum(c => c.Needs.Count), lines.Count);
        Assert.All(lines, l => Assert.True(ChassisTree.Place(l.From).Row < ChassisTree.Place(l.To).Row, $"{l.From} to {l.To}"));
    }

    [Fact]
    public void A_chassis_is_worn_owned_for_sale_too_dear_or_closed()
    {
        var world = InHangar(bmu: 100);
        var shop = Shop(world);

        Assert.Equal(ChassisStep.Worn, shop.Step(Chassis("grunt1")));
        Assert.Equal(ChassisStep.TooDear, shop.Step(Chassis("grunt2")));
        Assert.Equal(ChassisStep.Closed, shop.Step(Chassis("reaper2")));

        world.Races[Race.Red].Bmu = 1000;
        Assert.Equal(ChassisStep.ForSale, shop.Step(Chassis("grunt2")));

        Buy(world, "grunt2");
        Assert.Equal(ChassisStep.Owned, shop.Step(Chassis("grunt2")));
        Assert.Equal(ChassisStep.ForSale, shop.Step(Chassis("reaper2")));
    }

    [Fact]
    public void The_step_up_keeps_the_weapon_and_leaves_the_new_slots_empty()
    {
        var world = InHangar();
        var step = Shop(world).Gain(Chassis("reaper2"));

        Assert.Equal((1, 0), (step.StaysOn, step.FallsOff));
        Assert.Equal(1, step.WeaponSlots);
        Assert.Equal(1, step.ModuleSlots);
        Assert.Equal(3, step.EmptySlots);
        Assert.True(step.HitPoints > 0);
    }

    [Fact]
    public void The_step_down_says_what_falls_off()
    {
        var world = InHangar();
        OnTheReaper(world);
        var spare = Buy(world, "BOLTER");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, spare.Id, WiredSlot.Weapon(Mounting.Left))));

        var step = Shop(world).Gain(Chassis("grunt1"));

        Assert.Equal((1, 1), (step.StaysOn, step.FallsOff));
        Assert.Equal(-1, step.WeaponSlots);
    }

    [Fact]
    public void The_choice_sets_price_against_price_and_says_what_the_bank_leaves()
    {
        var world = InHangar(bmu: 150);
        Red(world).Offer.Clear();
        Red(world).Offer.Add("AUTO_CANNON");
        var choice = Shop(world).Choice(null, null);

        Assert.Equal("grunt2", choice.Chassis!.Id);
        Assert.Equal("AUTO_CANNON", choice.Weapon);
        Assert.True(choice.ChassisAffordable && choice.WeaponAffordable);
        Assert.Equal(choice.ChassisPrice + choice.WeaponPrice, choice.Both);
        Assert.Equal(150 - choice.Both, choice.LeftWithBoth);
        Assert.True(choice.LeftWithBoth < 0, "both are out of reach at once: that is the decision");
        Assert.True(choice.WeaponGain > 0);
    }

    [Fact]
    public void The_choice_keeps_what_the_player_looked_at_while_it_is_to_be_had()
    {
        var world = InHangar();
        Red(world).Offer.Clear();
        Red(world).Offer.Add("AUTO_CANNON");
        Red(world).Offer.Add("BOLTER");
        Buy(world, "grunt2");

        var choice = Shop(world).Choice("overseer2", "BOLTER");
        Assert.Equal(("overseer2", "BOLTER"), (choice.Chassis!.Id, choice.Weapon));

        // A chassis no longer to be bought and a weapon no longer offered give way to the next.
        Assert.NotEqual("grunt2", Shop(world).Choice("grunt2", null).Chassis!.Id);
        Assert.Equal("AUTO_CANNON", Shop(world).Choice(null, "FLAME_THROWER2").Weapon);
    }

    [Fact]
    public void The_next_chassis_is_no_step_back()
    {
        var world = InHangar();
        OnTheReaper(world);
        var grunt = Red(world).Items.Find(i => i.Catalogue == "grunt2")!;
        Assert.Null(Do(world, new WiredSellCommand(Race.Red, grunt.Id)));

        Assert.Equal(ChassisStep.ForSale, Shop(world).Step(Chassis("grunt2")));
        Assert.NotEqual("grunt2", Shop(world).NextChassis()!.Id);
    }

    [Fact]
    public void The_crab_stands_for_sale_for_nothing_but_a_start_of_the_tree_is_never_the_next_chassis()
    {
        var world = InHangar();
        var shop = Shop(world);

        Assert.Equal(ChassisStep.ForSale, shop.Step(Chassis("geo1")));
        Assert.Equal(0, shop.Price("geo1"));
        Assert.Equal("grunt2", shop.NextChassis()!.Id);
        // Nor on the crab: the first grunt, owned in every run, is a start of the tree as well.
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, Buy(world, "geo1").Id)));
        foreach (var wave in new[] { 0, 4, 12, 30 })
        {
            world.Wired!.Wave = wave;
            Assert.DoesNotContain(shop.NextChassis(), WiredCatalogue.Roots);
        }
    }

    [Fact]
    public void The_glider_is_closed_before_wave_five()
    {
        var world = InHangar();
        var shop = Shop(world);
        Buy(world, "geo1");

        world.Wired!.Wave = 3;
        Assert.Equal(4, world.Wired.ShopWave);
        Assert.Equal(ChassisStep.Closed, shop.Step(Chassis("geo3")));

        world.Wired.Wave = 4;
        Assert.Equal(ChassisStep.ForSale, shop.Step(Chassis("geo3")));
    }

    [Fact]
    public void The_glider_is_closed_to_a_hero_that_never_had_the_crab()
    {
        var world = InHangar();
        world.Wired!.Wave = 10;

        Assert.Equal(ChassisStep.Closed, Shop(world).Step(Chassis("geo3")));
        Assert.Equal("needs geo1 first", world.WhyCannotBuy(Red(world), "geo3"));
    }

    [Fact]
    public void The_first_boss_opens_from_the_glider()
    {
        var world = InHangar();
        var shop = Shop(world);
        world.Wired!.Wave = 10;
        Buy(world, "geo1");
        Assert.Equal(ChassisStep.Closed, shop.Step(Chassis("boss1")));

        Buy(world, "geo3");

        Assert.DoesNotContain("overseer3", Red(world).Reached);
        Assert.Equal(ChassisStep.ForSale, shop.Step(Chassis("boss1")));
    }

    [Fact]
    public void Asking_the_shop_changes_nothing_in_the_world()
    {
        var world = InHangar();
        var (state, before) = (Red(world), world.Checksum());
        var shop = Shop(world);

        foreach (var chassis in WiredCatalogue.Chassis)
        {
            shop.Step(chassis);
            shop.Gain(chassis);
        }
        shop.Choice(null, null);
        foreach (var key in shop.Escorts())
            world.WhyNoEscort(state, key);
        world.WhyNoReroll(state);
        world.WhyNoPodRepair(state);
        AskAboutCopies(world, state, shop);

        Assert.Equal(before, world.Checksum());
    }

    /// <summary>All that the hangar may ask about a further copy of every weapon onto every slot.</summary>
    private static void AskAboutCopies(GameWorld world, WiredState state, HangarShop shop)
    {
        foreach (var weapon in WiredCatalogue.Weapons)
        {
            var copy = new WiredItem(-1, WiredKind.Weapon, weapon.Id);
            foreach (var mounting in Enum.GetValues<Mounting>())
            {
                var slot = WiredSlot.Weapon(mounting);
                state.Loadout.WhyNotAdd(copy, slot);
                world.WhyCannotBuy(state, weapon.Id, slot);
                foreach (var worn in state.Loadout.Battery(mounting))
                    state.Loadout.WhyNotAdd(worn, slot);
            }
            shop.Ways(weapon.Id).ToList();
            shop.WeaponGain(weapon.Id);
            shop.Choice(null, weapon.Id);
        }
        shop.NextWeapon();
    }

    /// <summary>The Reaper of <see cref="OnTheReaper"/> with a bolter on its medium left slot, whose battery has room for one more.</summary>
    private static WiredState WithRoomForACopy(GameWorld world)
    {
        var state = Red(world);
        OnTheReaper(world);
        state.Offer.Add("BOLTER");
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", WiredSlot.Weapon(Mounting.Left))));
        state.Offer.Clear();
        return state;
    }

    [Fact]
    public void A_further_copy_of_a_worn_weapon_is_a_candidate_of_the_next_weapon_though_not_on_offer()
    {
        var world = InHangar();
        var state = WithRoomForACopy(world);
        var shop = Shop(world);
        var left = WiredSlot.Weapon(Mounting.Left);

        Assert.Equal("BOLTER", shop.NextWeapon());
        var way = shop.BestWay("BOLTER")!;
        Assert.Equal((left, true), (way.Slot, way.Copy));
        Assert.True(way.Gain > 0);

        var choice = shop.Choice(null, null);
        Assert.Equal("BOLTER", choice.Weapon);
        Assert.Equal(left, choice.CopyOnto);
        Assert.Equal(shop.Price("BOLTER"), choice.WeaponPrice);
        Assert.Equal(way.Gain, choice.WeaponGain);

        // Bought as the shop advises it, the battery is full, and off the offer nothing goes onto the empty right slot.
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", left, Add: true)));
        Assert.Equal(2, state.Loadout.Battery(Mounting.Left).Count);
        Assert.Null(shop.NextWeapon());
        Assert.Empty(shop.Ways("BOLTER"));
    }

    [Fact]
    public void A_weapon_on_offer_is_still_advised_for_a_slot_and_a_copy_not_on_offer_only_onto_its_battery()
    {
        var world = InHangar();
        var state = WithRoomForACopy(world);
        var shop = Shop(world);

        // Off the offer the bolter goes nowhere but onto its battery.
        Assert.All(shop.Ways("BOLTER"), w => Assert.True(w.Copy));
        Assert.Null(shop.BestWay("FLAME_THROWER2"));

        // On offer it may go into every slot it fits as well; the put is named first where both bring the same.
        state.Offer.Add("BOLTER");
        var ways = shop.Ways("BOLTER").ToList();
        Assert.Contains(ways, w => !w.Copy && w.Slot == WiredSlot.Weapon(Mounting.Right));
        Assert.Contains(ways, w => w.Copy && w.Slot == WiredSlot.Weapon(Mounting.Left));
        Assert.Equal(ways.Max(w => w.Gain), shop.WeaponGain("BOLTER"));
    }

    [Fact]
    public void A_copy_the_hero_cannot_carry_is_not_advised()
    {
        var world = InHangar();
        var state = Red(world);
        state.Loadout = HeroLoadout.Bare(new WiredItem(-1, WiredKind.Chassis, "reaper5"))
            .With(WiredSlot.Weapon(Mounting.Left), state.Add(WiredKind.Weapon, "PLASMA_CANNON2", 0))
            .Adding(WiredSlot.Weapon(Mounting.Left), state.Add(WiredKind.Weapon, "PLASMA_CANNON2", 0))
            .With(WiredSlot.Weapon(Mounting.Right), state.Add(WiredKind.Weapon, "PLASMA_CANNON2", 0))
            .Adding(WiredSlot.Weapon(Mounting.Right), state.Add(WiredKind.Weapon, "PLASMA_CANNON2", 0))
            .Adding(WiredSlot.Weapon(Mounting.Right), state.Add(WiredKind.Weapon, "PLASMA_CANNON2", 0))
            .With(WiredSlot.Weapon(Mounting.Top), state.Add(WiredKind.Weapon, "BOLTER", 0));
        state.Offer.Clear();
        var shop = Shop(world);

        // Five plasma cannons and a bolter weigh 42 of 32 at most 48: a sixth cannon would make 50.
        Assert.Equal("too heavy for this chassis", state.WhyNotWear(world.Data, state.Loadout.Adding(WiredSlot.Weapon(Mounting.Left), new WiredItem(-1, WiredKind.Weapon, "PLASMA_CANNON2"))));
        Assert.Empty(shop.Ways("PLASMA_CANNON2"));
        Assert.Null(shop.NextWeapon());
    }

    [Fact]
    public void Asking_about_copies_with_a_battery_on_changes_nothing_in_the_world()
    {
        var world = InHangar();
        var state = WithRoomForACopy(world);
        state.Offer.Add("AUTO_CANNON");
        var (before, fingerprint, bank) = (world.Checksum(), state.Loadout, world.Races[Race.Red].Bmu);

        AskAboutCopies(world, state, Shop(world));

        Assert.Equal(before, world.Checksum());
        Assert.Same(fingerprint, state.Loadout);
        Assert.Equal(bank, world.Races[Race.Red].Bmu);
    }

    /// <summary>All that the hangar and the HUD may ask about the special of the chassis the hero wears, every frame.</summary>
    private static void AskAboutTheSpecial(GameWorld world, WiredState state, WiredChassis chassis, Machine foe)
    {
        var hero = world.HeroOf(Race.Red);
        var at = hero?.Position ?? world.WiredPod(Race.Red)!.Position;
        foreach (var on in new[] { true, false })
        {
            world.WhyNoSpecial(state, on);
            world.WhyNoAnchor(state, on);
        }
        foreach (var race in Enum.GetValues<Race>())
            world.SpecialView(race);
        world.WhyNoLocate(state);
        world.LocatedFor(foe.Id);
        world.SquadLimit(state);
        world.WhyNoRally(state);
        world.WhyNoMine(state);
        foreach (var tower in WiredCatalogue.Towers)
        {
            foreach (var orientation in new[] { 0, 45, 90 })
                world.WhyNoTower(state, tower.Id, at + new Vector2(20, 0), orientation);
        }
        world.WhyNoTower(state, "nothing", at, 0);
        world.TowersFor(chassis);
        world.TowersOf(Race.Red);
        world.MostTowers(state);
        world.WhyNoScrap(state, world.WiredPod(Race.Red)!.Id);
        world.WhyNoScrap(state, 0);
        if (hero is not null)
        {
            world.LocatedFor(hero.Id);
            world.AnchorPhaseOf(hero);
            world.Shadowed(foe, hero);
            world.Shadowed(hero, foe);
        }
    }

    [Fact]
    public void Asking_about_the_special_of_any_chassis_changes_nothing_in_the_world()
    {
        var world = InHangar();
        var state = Red(world);
        // Far off, so that it does nothing between the questions.
        var far = world.Nav.NearestFreePoint(world.WiredPod(Race.Red)!.Position + new Vector2(300, 0), crossesWater: false)!.Value;
        var foe = world.AddMachine(Race.Blue, far, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        world.Step();
        // Located, as a geo hero's pulse leaves it, so that the questions about it go the located way as well.
        world.Wired!.Located[foe.Id] = world.Time + 1000;

        foreach (var chassis in WiredCatalogue.Chassis)
        {
            if (!state.HangarOpen)
                Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
            if (state.Loadout.Chassis.Catalogue != chassis.Id)
                Assert.Null(Do(world, new WiredChassisCommand(Race.Red, state.Add(WiredKind.Chassis, chassis.Id, 0).Id)));
            // In the hangar and out on the field, where the questions get past the hero's whereabouts.
            foreach (var open in new[] { true, false })
            {
                if (state.HangarOpen != open)
                    Assert.Null(Do(world, new WiredHangarCommand(Race.Red, open)));
                var before = world.Checksum();

                AskAboutTheSpecial(world, state, chassis, foe);

                Assert.True(before == world.Checksum(), $"{chassis.Id}, hangar {(open ? "open" : "closed")}: asking changed the world");
            }
        }
    }

    [Fact]
    public void What_the_sim_says_before_a_reroll_is_what_it_says_to_one()
    {
        var world = InHangar(bmu: 10);
        Assert.Equal("not enough BMUs", world.WhyNoReroll(Red(world)));
        Assert.Equal(world.WhyNoReroll(Red(world)), Do(world, new WiredRerollCommand(Race.Red)));

        world.Races[Race.Red].Bmu = 100;
        Assert.Null(world.WhyNoReroll(Red(world)));
        Assert.Null(Do(world, new WiredRerollCommand(Race.Red)));

        Assert.Equal("only one reroll per intermission", world.WhyNoReroll(Red(world)));
        Assert.Equal(world.WhyNoReroll(Red(world)), Do(world, new WiredRerollCommand(Race.Red)));
    }

    [Fact]
    public void What_the_sim_says_before_an_upgrade_is_what_it_says_to_one()
    {
        var world = InHangar(bmu: 0);
        var state = Red(world);
        var bolter = state.Items.Find(i => i.Catalogue == "BOLTER")!;

        Assert.Equal("not enough BMUs", world.WhyNoUpgrade(state, bolter));
        Assert.Equal(world.WhyNoUpgrade(state, bolter), Do(world, new WiredUpgradeCommand(Race.Red, bolter.Id)));

        world.Races[Race.Red].Bmu = 100000;
        Assert.Null(world.WhyNoUpgrade(state, bolter));
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, bolter.Id)));
    }

    [Fact]
    public void What_the_sim_says_before_a_pod_repair_is_what_it_says_to_one()
    {
        var world = InHangar(bmu: 0);
        var state = Red(world);

        Assert.Equal("the pod needs no repair", world.WhyNoPodRepair(state));
        Assert.Equal(world.WhyNoPodRepair(state), Do(world, new WiredPodRepairCommand(Race.Red)));

        world.WiredPod(Race.Red)!.HitPoints -= 100;
        Assert.Equal("not enough BMUs", world.WhyNoPodRepair(state));
        Assert.Equal(world.WhyNoPodRepair(state), Do(world, new WiredPodRepairCommand(Race.Red)));

        world.Races[Race.Red].Bmu = 100;
        Assert.Null(world.WhyNoPodRepair(state));
        Assert.Null(Do(world, new WiredPodRepairCommand(Race.Red)));
    }

    [Fact]
    public void What_the_sim_says_before_an_escort_is_what_it_says_to_one()
    {
        var world = InHangar(bmu: 0);
        var state = Red(world);
        var shop = Shop(world);
        var key = shop.Escorts().First();
        var later = WiredCatalogue.Tiers.Where(t => t.EscortFrom > shop.Wave).Select(t => new ResearchKey(t.Type, t.SubType, t.Level, t.Combos[0])).First();

        Assert.Equal("not enough BMUs", world.WhyNoEscort(state, key));
        Assert.Equal(world.WhyNoEscort(state, key), Do(world, new WiredSquadBuyCommand(Race.Red, key)));
        Assert.Equal("that is not on offer yet", world.WhyNoEscort(state, later));
        Assert.Equal(world.WhyNoEscort(state, later), Do(world, new WiredSquadBuyCommand(Race.Red, later)));

        world.Races[Race.Red].Bmu = 100000;
        Assert.Null(world.WhyNoEscort(state, key));
        var bank = shop.Bank;
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));
        Assert.Equal(bank - world.EscortPrice(state, key), shop.Bank);
        Assert.Equal(1, shop.SquadSize);
    }

    [Fact]
    public void Ready_pays_what_the_screen_says_and_the_first_wave_pays_nothing()
    {
        var world = InHangar(bmu: 0);
        Assert.Equal(0, world.ReadyPays);
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));

        world.BeginIntermission();
        var (said, bank) = (world.ReadyPays, Shop(world).Bank);
        Assert.True(said > 0);
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));
        Assert.Equal(bank + said, Shop(world).Bank);
    }
}
