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
        Assert.All(places, p => Assert.InRange(p.Column, 0, ChassisTree.Columns - 1));
        Assert.All(places, p => Assert.InRange(p.Row, 0, ChassisTree.Rows - 1));
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

        Assert.Equal(before, world.Checksum());
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
        var later = WiredCatalogue.Tiers.Where(t => t.Wave > shop.Wave).Select(t => new ResearchKey(t.Type, t.SubType, t.Level, t.Combos[0])).First();

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
