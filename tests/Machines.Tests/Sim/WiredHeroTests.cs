using System.Numerics;
using Machines.Game;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>The modular hero of the survival mode Wired: what its parts come to, and what the hangar's commands do.</summary>
public class WiredHeroTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static GameWorld NewRun(WiredOptions? options = null, int seed = 7) =>
        MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, seed,
            new MatchOptions { Wired = options ?? new WiredOptions() });

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    private static int Bank(GameWorld world) => (int)world.Races[Race.Red].Bmu;

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    /// <summary>A run with its hangar open and a bank that does not get in the way.</summary>
    private static GameWorld InHangar(WiredOptions? options = null, int bmu = 100000)
    {
        var world = NewRun(options);
        world.Races[Race.Red].Bmu = bmu;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        return world;
    }

    /// <summary>Buys something, whatever the shop happens to offer.</summary>
    private static WiredItem Buy(GameWorld world, string id)
    {
        var state = Red(world);
        if (WiredCatalogue.FindChassis(id) is null)
            state.Offer.Add(id);
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, id)));
        return state.Items[^1];
    }

    private static WiredItem Wear(GameWorld world, string id, WiredSlot slot)
    {
        var item = Buy(world, id);
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, item.Id, slot)));
        return item;
    }

    /// <summary>Puts the hero on any chassis, without the way there.</summary>
    private static WiredItem Board(GameWorld world, string chassis)
    {
        var item = Red(world).Add(WiredKind.Chassis, chassis, 0);
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, item.Id)));
        return item;
    }

    private static HeroLoadout Loadout(string chassis, params (WiredSlot Slot, string Id, int Mk)[] parts)
    {
        var loadout = HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, chassis));
        var id = 2;
        foreach (var (slot, item, mk) in parts)
            loadout = loadout.With(slot, new WiredItem(id++, WiredCatalogue.KindOf(item)!.Value, item, mk));
        return loadout;
    }

    private static readonly WiredSlot Left = WiredSlot.Weapon(Mounting.Left), Right = WiredSlot.Weapon(Mounting.Right), Top = WiredSlot.Weapon(Mounting.Top);

    private static void Near(float expected, float actual) => Assert.InRange(actual, expected - 0.001f, expected + 0.001f);

    // ---- the user's example -----------------------------------------------------------------------

    [Fact]
    public void A_grunt_changes_to_a_reaper_and_keeps_its_bolter_with_the_second_slot_empty_until_it_can_be_paid_for()
    {
        var world = InHangar(bmu: 500);
        var (state, hero) = (Red(world), Hero(world));
        Assert.Equal("grunt1", state.Loadout.Chassis.Catalogue);
        var bolter = state.Loadout.Weapon(Mounting.Right)!;
        Assert.Equal("BOLTER", bolter.Catalogue);
        Assert.Equal("R_BOLTER", hero.WeaponCombo);

        // The Reaper comes after the second Grunt.
        Assert.Equal("needs grunt2 first", world.WhyCannotBuy(state, "reaper2"));
        Buy(world, "grunt2");
        var reaper = Buy(world, "reaper2");
        Assert.Equal(500 - 110 - 300, Bank(world));

        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, reaper.Id)));

        Assert.Equal("ASSASSIN", hero.Spec.SubType);
        Assert.Equal(2, hero.Stats.HardwareLevel);
        Assert.Equal(1, hero.Refits);
        Assert.Equal(bolter, state.Loadout.Weapon(Mounting.Right));
        Assert.Null(state.Loadout.Weapon(Mounting.Left));
        Assert.Equal("BOLTER", Assert.Single(hero.Weapons).Spec.TypeName);
        Assert.Contains("Empty weapon slot", HeroBuild.Resolve(Data, state.Loadout).Warnings);

        // Later, with money, the empty slot takes something.
        world.Races[Race.Red].Bmu = 200;
        var rifle = Wear(world, "PLASMA_RIFLE", Left);
        Assert.Equal(rifle, state.Loadout.Weapon(Mounting.Left));
        Assert.Equal(["PLASMA_RIFLE", "BOLTER"], hero.Weapons.Select(w => w.Spec.TypeName));
        Assert.Equal("L_PLASMA_RIFLE_R_BOLTER", hero.WeaponCombo);
        Assert.DoesNotContain("Empty weapon slot", HeroBuild.Resolve(Data, state.Loadout).Warnings);

        // The old chassis stay owned and are free to take again.
        var grunt = state.Items.First(i => i.Catalogue == "grunt1");
        var bank = Bank(world);
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, grunt.Id)));
        Assert.Equal(bank, Bank(world));
        Assert.Equal("GRUNT", hero.Spec.SubType);
        Assert.Equal(bolter, state.Loadout.Weapon(Mounting.Right));
        Assert.Contains(rifle, state.Inventory);
    }

    [Fact]
    public void A_heavy_gun_is_refused_on_a_light_slot_and_a_slot_takes_its_size_or_smaller()
    {
        var world = InHangar();
        var state = Red(world);
        var cannon = Buy(world, "PLASMA_CANNON2");
        var worn = state.Loadout;

        Assert.Equal("the weapon is too big for this slot", Do(world, new WiredEquipCommand(Race.Red, cannon.Id, Right)));

        Assert.Same(worn, state.Loadout);
        Assert.Equal(0, Hero(world).Refits);
        Assert.Contains(cannon, state.Inventory);

        Board(world, "knight4");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, cannon.Id, Right)));
        Assert.Equal(cannon, state.Loadout.Weapon(Mounting.Right));
        var bolter = state.Items.First(i => i.Catalogue == "BOLTER");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, bolter.Id, Left)));
        Assert.Equal(bolter, state.Loadout.Weapon(Mounting.Left));
    }

    // ---- load and power ---------------------------------------------------------------------------

    [Theory]
    [InlineData(0, 12, 1f)]
    [InlineData(1, 15, 0.85f)]
    [InlineData(2, 18, 0.7f)]
    public void Each_ten_percent_of_overload_costs_six_percent_of_speed_and_turning(int modules, int load, float left)
    {
        var parts = new List<(WiredSlot, string, int)> { (Left, "HEAVY_BOLTER2", 1), (Right, "MULTI_LAUNCHER5", 1) };
        if (modules > 0)
            parts.Add((WiredSlot.Module(0), "plates", 1));
        if (modules > 1)
            parts.Add((WiredSlot.Module(1), "reactor", 1));

        var build = HeroBuild.Resolve(Data, Loadout("grunt3", [.. parts]));

        var level = Data.Parm.Machine("AGGRESSOR GRUNT").Level(3);
        Assert.Equal(load, build.Load);
        Assert.Equal(12, build.LoadCapacity);
        Near(level.Speed * left, build.Stats.Speed);
        Near(level.Acceleration * left, build.Stats.Acceleration);
        Near(level.RotationSpeed * left, build.Stats.RotationSpeed);
        Near(level.BodyTurnRate!.Value * left, build.Stats.BodyTurnRate!.Value);
        Assert.Equal(modules > 0, build.Warnings.Contains("Overloaded: slower"));
    }

    [Fact]
    public void Overload_up_to_half_the_capacity_is_allowed_and_more_refused()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "grunt3");
        Wear(world, "HEAVY_BOLTER2", Left);
        Wear(world, "MULTI_LAUNCHER5", Right);
        Wear(world, "plates", WiredSlot.Module(0));
        Wear(world, "reactor", WiredSlot.Module(1));
        Assert.Equal(WiredBalance.MostLoad, HeroBuild.Resolve(Data, state.Loadout).LoadShare);

        // Every real chassis stays within it, however it is filled, so the refusal needs more module slots than any has.
        var plates = Enumerable.Range(0, 4).Select(i => (WiredItem?)new WiredItem(50 + i, WiredKind.Module, "plates")).ToList();
        var heavier = Loadout("grunt1", (Right, "FLAME_THROWER2", 1)) with { Modules = plates };
        Assert.Equal(15, HeroBuild.Resolve(Data, heavier).Load);
        Assert.Equal("too heavy for this chassis", state.WhyNotWear(Data, heavier));
        Assert.Contains("Too heavy for this chassis", HeroBuild.Resolve(Data, heavier).Warnings);
    }

    [Fact]
    public void The_heaviest_fill_of_any_real_chassis_comes_to_exactly_what_the_hangar_allows()
    {
        var modules = WiredCatalogue.Modules.Max(m => m.Load);
        float Heaviest(WiredChassis chassis) => (chassis.Modules * modules + chassis.Slots.Where(s => s.Fixed is null)
            .Sum(s => WiredCatalogue.Weapons.Where(w => WiredCatalogue.WhyNotFit(chassis, s.Mounting, w) is null).Max(w => w.Load))) / (float)chassis.Load;

        var limit = WiredCatalogue.Chassis.Where(c => Heaviest(c) >= WiredBalance.MostLoad).Select(c => c.Id);

        Assert.Equal(WiredBalance.MostLoad, WiredCatalogue.Chassis.Max(Heaviest));
        Assert.Equal(["grunt3", "overseer2", "overseer3", "commander5", "boss2"], limit);
    }

    [Fact]
    public void Overload_never_stands_in_the_way_of_getting_lighter()
    {
        var state = WiredState.Create(Data, Race.Red, new WiredOptions(), out _);
        var plates = Enumerable.Range(0, 4).Select(i => (WiredItem?)new WiredItem(50 + i, WiredKind.Module, "plates")).ToList();
        state.Loadout = state.Loadout with { Modules = plates };

        Assert.Null(state.WhyNotWear(Data, state.Loadout with { Modules = plates.Take(3).ToList() }));
    }

    [Fact]
    public void Short_of_power_all_reloads_grow_in_proportion_and_nothing_is_refused()
    {
        var pair = new[] { (Left, "PULSE_CANNON", 1), (Right, "PLASMA_CANNON1", 1) };
        var full = HeroBuild.Resolve(Data, Loadout("grunt3", pair));
        Assert.Equal((9, 9), (full.PowerDraw, full.PowerSupply));
        Assert.Equal(1, full.PowerShare);

        var starved = HeroBuild.Resolve(Data, Loadout("grunt3", [.. pair, (WiredSlot.Module(0), "repair", 1)]));

        Assert.Equal(11, starved.PowerDraw);
        Near(9 / 11f, starved.PowerShare);
        foreach (var (before, after) in full.Weapons.Zip(starved.Weapons))
            Near(before.Spec.ReloadTime * 11 / 9, after.Spec.ReloadTime);
        Assert.Contains("Not enough power: slower reloads", starved.Warnings);

        // An ability draws too, and a reactor makes up for it.
        var ability = HeroBuild.Resolve(Data, Loadout("grunt3", [.. pair, (WiredSlot.Ability(0), "ionstrike", 1)]));
        Assert.Equal(15, ability.PowerDraw);
        var reactor = HeroBuild.Resolve(Data, Loadout("grunt3", [.. pair, (WiredSlot.Ability(0), "ionstrike", 1), (WiredSlot.Module(0), "reactor", 2)]));
        Assert.Equal(19, reactor.PowerSupply);
        Assert.Equal(1, reactor.PowerShare);
    }

    // ---- the build --------------------------------------------------------------------------------

    [Fact]
    public void A_weapon_of_a_higher_mk_is_a_copy_and_the_shared_spec_stays_as_it_is()
    {
        var shared = Data.Parm.Weapon("BOLTER");
        var (damage, reload) = (shared.Damage, shared.ReloadTime);

        var weapon = Assert.Single(HeroBuild.Resolve(Data, Loadout("grunt1", (Right, "BOLTER", 3))).Weapons);

        Near(damage * 1.4f, weapon.Spec.Damage);
        Near(reload * 0.84f, weapon.Spec.ReloadTime);
        Assert.NotSame(shared, weapon.Spec);
        Assert.Equal((damage, reload), (Data.Parm.Weapon("BOLTER").Damage, Data.Parm.Weapon("BOLTER").ReloadTime));
    }

    [Fact]
    public void The_hero_is_tougher_than_its_type_and_the_types_stats_stay_as_they_are()
    {
        var level = Data.Parm.Machine("AGGRESSOR GRUNT").Level(1);
        var (hitPoints, armour) = (level.HitPoints, level.Armour);

        var hero = Hero(NewRun());

        Assert.Equal(hitPoints * WiredBalance.HeroHitPoints, hero.MaxHitPoints);
        Assert.Equal(armour * WiredBalance.HeroArmour, hero.MaxArmour);
        Assert.Equal(hero.MaxHitPoints, hero.HitPoints);
        Assert.NotSame(level, hero.Stats);
        Assert.Equal((hitPoints, armour), (level.HitPoints, level.Armour));
    }

    [Fact]
    public void Modules_work_for_each_mk()
    {
        var level = Data.Parm.Machine("AGGRESSOR KNIGHT").Level(5);
        var bare = HeroBuild.Resolve(Data, Loadout("knight5"));
        WiredSlot Slot(int i) => WiredSlot.Module(i);

        var tough = HeroBuild.Resolve(Data, Loadout("knight5", (Slot(0), "plates", 2), (Slot(1), "frame", 3)));
        Assert.Equal((int)MathF.Round(level.Armour * 3 * 1.5f), tough.Stats.Armour);
        Assert.Equal((int)MathF.Round(level.HitPoints * 3 * (1 + 0.2f + 0.75f)), tough.Stats.HitPoints);
        Assert.Equal(bare.LoadCapacity + 9, tough.LoadCapacity);

        var quick = HeroBuild.Resolve(Data, Loadout("knight5", (Slot(0), "drive", 3), (Slot(1), "gyro", 2), (Slot(2), "repair", 1)));
        Near(level.Speed * 1.36f, quick.Stats.Speed);
        Near(level.Acceleration * 1.36f, quick.Stats.Acceleration);
        Near(level.RotationSpeed * 1.4f, quick.Stats.RotationSpeed);
        Near(level.BodyTurnRate!.Value * 1.4f, quick.Stats.BodyTurnRate!.Value);
        Near(level.RepairRate * 1.6f, quick.Stats.RepairRate);

        var sense = HeroBuild.Resolve(Data, Loadout("knight5", (Slot(0), "scanner", 2), (Slot(1), "targeting", 1), (Slot(2), "scrap", 2), (Slot(3), "thrusters", 3)));
        Assert.Equal((int)MathF.Round(level.ScannerRange * 1.4f), sense.Stats.ScannerRange);
        Assert.Equal(Math.Min(100, (level.Accuracy ?? 100) + 10), sense.Stats.Accuracy);
        Near(2, sense.WreckShare);
        Assert.Equal(6, sense.DashCooldown);
        Assert.Null(bare.DashCooldown);

        var cool = HeroBuild.Resolve(Data, Loadout("knight5", (Left, "BOLTER", 1), (Slot(0), "cooler", 2)));
        Near(Data.Parm.Weapon("BOLTER").ReloadTime * 0.88f, Assert.Single(cool.Weapons, w => w.Item is not null).Spec.ReloadTime);

        Assert.True(HeroBuild.Resolve(Data, Loadout("grunt1", (Slot(0), "nightvision", 1))).Stats.NightVision);
    }

    [Fact]
    public void Gliders_reach_wrecks_further()
    {
        Near(WiredBalance.PickupReach, HeroBuild.Resolve(Data, Loadout("grunt1")).PickupReach);
        Near(WiredBalance.PickupReach * 1.5f, HeroBuild.Resolve(Data, Loadout("boss1")).PickupReach);
    }

    [Fact]
    public void The_combo_of_a_build_reads_back_as_its_weapons()
    {
        var build = HeroBuild.Resolve(Data, Loadout("knight5", (Left, "BOLTER", 1), (Right, "PLASMA_RIFLE", 2), (Top, "AUTO_CANNON", 1)));

        Assert.Equal("L_BOLTER_R_PLASMA_RIFLE_T_AUTO_CANNON", build.WeaponCombo);
        Assert.Equal([(Mounting.Left, "BOLTER"), (Mounting.Right, "PLASMA_RIFLE"), (Mounting.Top, "AUTO_CANNON")], WeaponCombos.Split(build.WeaponCombo!));
        Assert.Null(HeroBuild.Resolve(Data, Loadout("grunt1")).WeaponCombo);
        Assert.Contains("No weapon", HeroBuild.Resolve(Data, Loadout("grunt1")).Warnings);
    }

    [Fact]
    public void Every_combination_of_guns_reads_back_from_its_combo()
    {
        var guns = WiredCatalogue.Weapons.Where(w => !w.GliderOnly).Select(w => w.Id).ToList();
        foreach (var left in guns)
        {
            foreach (var right in guns)
            {
                var combo = HeroBuild.Resolve(Data, Loadout("knight4", (Left, left, 1), (Right, right, 1))).WeaponCombo!;
                Assert.Equal([(Mounting.Left, left), (Mounting.Right, right)], WeaponCombos.Split(combo));
            }
        }
        foreach (var top in WiredCatalogue.Weapons.Where(w => w.GliderOnly).Select(w => w.Id))
            Assert.Equal([(Mounting.Top, top)], WeaponCombos.Split(HeroBuild.Resolve(Data, Loadout("boss2", (Top, top, 1))).WeaponCombo!));
    }

    [Fact]
    public void Dps_and_range_come_from_the_weapons_as_carried()
    {
        var one = HeroBuild.Resolve(Data, Loadout("grunt1", (Right, "BOLTER", 1)));
        var better = HeroBuild.Resolve(Data, Loadout("grunt1", (Right, "BOLTER", 3)));

        Assert.True(one.DamagePerSecond > 0);
        Assert.True(better.DamagePerSecond > one.DamagePerSecond);
        Assert.Equal(Data.Parm.Weapon("BOLTER").Range, one.Range);
    }

    [Fact]
    public void The_gorillas_punch_is_part_of_the_chassis()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "gorilla5");

        // The bolter moved to the left slot, and the punch came with the chassis.
        Assert.Equal(["BOLTER", "GORILLA_PUNCH"], Hero(world).Weapons.Select(w => w.Spec.TypeName));
        Assert.Equal(Mounting.Top, Hero(world).Weapons[1].Mounting);
        Assert.Null(state.Loadout.Weapon(Mounting.Top));

        var cannon = Buy(world, "PLASMA_CANNON2");
        Assert.Equal("that weapon is part of the chassis", Do(world, new WiredEquipCommand(Race.Red, cannon.Id, Top)));

        // It weighs nothing and draws nothing.
        var bare = HeroBuild.Resolve(Data, Loadout("gorilla5"));
        Assert.Null(Assert.Single(bare.Weapons).Item);
        Assert.Equal((0, 0), (bare.Load, bare.PowerDraw));
    }

    [Fact]
    public void Stings_and_bombs_go_only_on_top_of_a_glider_and_any_gun_of_its_size_does_too()
    {
        var world = InHangar();
        var state = Red(world);
        var sting = Buy(world, "WASP_LIGHT_STING");
        Assert.Equal("that fits only the top slot of a glider", Do(world, new WiredEquipCommand(Race.Red, sting.Id, Right)));

        Board(world, "boss1");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, sting.Id, Top)));
        Assert.Equal(sting, state.Loadout.Weapon(Mounting.Top));

        var bolter = state.Items.First(i => i.Catalogue == "BOLTER");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, bolter.Id, Top)));
        Assert.Equal(bolter, state.Loadout.Weapon(Mounting.Top));
    }

    // ---- changing chassis -------------------------------------------------------------------------

    [Fact]
    public void A_weapon_with_no_mounting_of_its_own_on_the_new_chassis_takes_the_slot_there_is()
    {
        var world = InHangar();
        var state = Red(world);
        var bolter = state.Loadout.Weapon(Mounting.Right)!;

        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, Buy(world, "grunt2").Id)));

        Assert.Equal(bolter, state.Loadout.Weapon(Mounting.Left));
        Assert.Null(state.Loadout.Weapon(Mounting.Right));
        Assert.Equal("L_BOLTER", Hero(world).WeaponCombo);
    }

    [Fact]
    public void What_does_not_fit_the_new_chassis_goes_into_the_inventory_and_nothing_is_lost()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "knight4");
        var (first, second) = (Wear(world, "PLASMA_CANNON2", Left), Wear(world, "MULTI_LAUNCHER1", Right));
        var modules = Enumerable.Range(0, 3).Select(i => Wear(world, "plates", WiredSlot.Module(i))).ToList();
        var owned = state.Items.Count;

        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, state.Items.First(i => i.Catalogue == "grunt1").Id)));

        Assert.Equal(owned, state.Items.Count);
        Assert.Contains(first, state.Inventory);
        Assert.Contains(second, state.Inventory);
        Assert.Equal(modules[0], Assert.Single(state.Loadout.Modules));
        Assert.Contains(modules[1], state.Inventory);
        Assert.Contains(modules[2], state.Inventory);
        Assert.Contains(state.Items.First(i => i.Catalogue == "knight4"), state.Inventory);
    }

    [Fact]
    public void A_hero_keeps_its_share_of_hit_points_and_armour_and_a_refit_never_kills_it()
    {
        var world = InHangar();
        var hero = Hero(world);
        hero.HitPoints = hero.MaxHitPoints / 2;
        hero.Armour = hero.MaxArmour / 4;
        var (hitPoints, armour) = (hero.HitPoints / (float)hero.MaxHitPoints, hero.Armour / (float)hero.MaxArmour);

        Board(world, "knight4");

        var level = Data.Parm.Machine("AGGRESSOR KNIGHT").Level(4);
        Assert.Equal(level.HitPoints * WiredBalance.HeroHitPoints, hero.MaxHitPoints);
        Assert.Equal(level.Armour * WiredBalance.HeroArmour, hero.MaxArmour);
        Assert.InRange(hero.HitPoints / (float)hero.MaxHitPoints, hitPoints, hitPoints + 1f / hero.MaxHitPoints + 0.001f);
        Assert.InRange(hero.Armour / (float)hero.MaxArmour, armour, armour + 1f / hero.MaxArmour + 0.001f);

        hero.HitPoints = 1;
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, Red(world).Items.First(i => i.Catalogue == "grunt1").Id)));
        Assert.Equal(1, hero.HitPoints);
        Assert.False(hero.IsDead);
    }

    [Fact]
    public void A_refit_leaves_nothing_behind_of_the_old_machine()
    {
        var world = InHangar();
        var hero = Hero(world);
        var aim = world.GunneryOf(hero);
        Assert.Null(Do(world, new MoveCommand(Race.Red, [hero.Id], hero.Position + new Vector2(15, 0))));
        for (var i = 0; i < 5; i++)
            world.Step();
        Assert.NotEqual(Order.Idle, hero.Order);
        world.PlayerAims[hero.Id] = (Vector2.Zero, null);
        world.PlayerWeapons[hero.Id] = 3;
        world.PlayerHeads[hero.Id] = 1;
        world.DriveInput[hero.Id] = (1, 0, 1, false);

        Board(world, "reaper2");

        Assert.Equal(Order.Idle, hero.Order);
        Assert.Empty(hero.Path);
        Assert.False(world.PlayerAims.ContainsKey(hero.Id));
        Assert.False(world.PlayerWeapons.ContainsKey(hero.Id));
        Assert.False(world.PlayerHeads.ContainsKey(hero.Id));
        Assert.False(world.DriveInput.ContainsKey(hero.Id));
        Assert.NotSame(aim, world.GunneryOf(hero));
        Assert.False(world.Busy(hero));

        // What goes by the type follows it; a plain machine of the same type says what that is.
        var plain = world.AddMachine(Race.Red, hero.Position + new Vector2(20, 0), "AGGRESSOR", "ASSASSIN", 2, "L_AUTO_CANNON");
        Assert.Equal(plain.Radius, hero.Radius);
        Assert.Equal(plain.CanStrafe, hero.CanStrafe);
        Assert.Equal(plain.CanTurnUpperBody, hero.CanTurnUpperBody);
        Assert.Equal(plain.Key with { WeaponCombo = hero.WeaponCombo }, hero.Key);
    }

    [Fact]
    public void A_refitted_hero_drives_and_shoots_with_what_it_wears_now()
    {
        var world = InHangar();
        var hero = Hero(world);
        Board(world, "reaper2");
        Wear(world, "AUTO_CANNON", Left);
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, Red(world).Loadout.Weapon(Mounting.Left)!.Id)));
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));

        var from = hero.Position;
        var enemy = world.AddMachine(Race.Blue, from + new Vector2(25, 0), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var health = enemy.HitPoints + enemy.Armour;
        Assert.Null(Do(world, new AttackCommand(Race.Red, [hero.Id], enemy.Id)));
        for (var i = 0; i < 300 && !enemy.IsDead && enemy.HitPoints + enemy.Armour == health; i++)
            world.Step();

        Assert.True(enemy.HitPoints + enemy.Armour < health, "the hero's new guns hit nothing");
    }

    // ---- what counts as a refit -------------------------------------------------------------------

    [Fact]
    public void Only_a_change_of_the_machine_is_a_refit()
    {
        var world = InHangar();
        var (state, hero) = (Red(world), Hero(world));
        var spare = Buy(world, "BOLTER");
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, spare.Id)));
        var chip = state.Add(WiredKind.Chip, "trader", 0);
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 0)));
        Assert.Equal(0, hero.Refits);

        // A worn weapon's Mk does change it.
        var worn = state.Loadout.Weapon(Mounting.Right)!;
        var damage = hero.Weapons[0].Spec.Damage;
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, worn.Id)));
        Assert.Equal(1, hero.Refits);
        Near(damage * 1.2f, hero.Weapons[0].Spec.Damage);
        Assert.Equal(2, state.Loadout.Weapon(Mounting.Right)!.Mk);
    }

    // ---- the catalogues against the real data -----------------------------------------------------

    [Fact]
    public void Every_chassis_resolves_against_the_real_data_on_the_machine_it_names()
    {
        Assert.Equal(17, WiredCatalogue.Chassis.Count);
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var level = Data.Parm.Machine(chassis.TypeName).Level(chassis.Level);
            var build = HeroBuild.Resolve(Data, Loadout(chassis.Id));

            Assert.Equal(level.HitPoints * WiredBalance.HeroHitPoints, build.Stats.HitPoints);
            Assert.Equal(chassis.Load, build.LoadCapacity);
            Assert.Equal(chassis.Power, build.PowerSupply);
            Assert.Equal(chassis.Slots.Count(s => s.Fixed is not null), build.Weapons.Count);
            Assert.All(chassis.Needs, need => Assert.NotNull(WiredCatalogue.FindChassis(need)));
            Assert.All(chassis.Slots.Where(s => s.Fixed is not null), s => Assert.Equal(s.Fixed, Data.Parm.Weapon(s.Fixed!).TypeName));
        }
    }

    [Fact]
    public void Every_weapon_is_a_real_one_and_each_goes_on_every_chassis_with_a_slot_for_it()
    {
        Assert.Equal(20, WiredCatalogue.Weapons.Count);
        foreach (var weapon in WiredCatalogue.Weapons)
        {
            Assert.Equal(weapon.Id, Data.Parm.Weapon(weapon.Id).TypeName);
            var takers = 0;
            foreach (var chassis in WiredCatalogue.Chassis)
            {
                foreach (var slot in chassis.Slots.Where(s => s.Fixed is null && s.Size >= weapon.Size))
                {
                    if (weapon.GliderOnly && !(chassis.Glider && slot.Mounting == Mounting.Top))
                        continue;
                    Assert.Null(WiredCatalogue.WhyNotFit(chassis, slot.Mounting, weapon));
                    var build = HeroBuild.Resolve(Data, Loadout(chassis.Id, (WiredSlot.Weapon(slot.Mounting), weapon.Id, 3)));
                    Assert.Contains(build.Weapons, w => w.Spec.TypeName == weapon.Id);
                    takers++;
                }
            }
            Assert.True(takers > 0, $"nothing carries {weapon.Id}");
        }
    }

    [Fact]
    public void The_catalogues_hold_together()
    {
        var ids = WiredCatalogue.Chassis.Select(c => c.Id)
            .Concat(WiredCatalogue.Weapons.Select(w => w.Id))
            .Concat(WiredCatalogue.Modules.Select(m => m.Id))
            .Concat(WiredCatalogue.Abilities.Select(a => a.Id))
            .Concat(WiredChips.All.Select(c => c.Id)).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Assert.All(ids, id => Assert.NotNull(WiredCatalogue.KindOf(id)));

        Assert.Equal(12, WiredCatalogue.Modules.Count);
        Assert.Equal(6, WiredCatalogue.Abilities.Count);
        Assert.Equal(24, WiredChips.All.Count);
        Assert.Equal(12, WiredChips.BasePool.Count);
        Assert.All(Enum.GetValues<ChipFamily>(), family => Assert.Equal(6, WiredChips.All.Count(c => c.Family == family)));

        Assert.All(WiredCatalogue.Abilities.Where(a => a.Based is not null), a => Assert.Equal(a.Based, Data.Parm.Weapon(a.Based!).TypeName));
        Assert.Equal(5, WiredCatalogue.FindAbility("ionstrike")!.CooldownWaves);

        foreach (var tier in WiredCatalogue.Tiers)
        {
            var level = Data.Parm.Machine($"{tier.Type} {tier.SubType}").Level(tier.Level);
            Assert.True(level.Cost > 0);
            foreach (var combo in tier.Combos)
                Assert.All(WeaponCombos.Split(combo), w => Assert.Equal(w.Weapon, Data.Parm.Weapon(w.Weapon).TypeName));
        }
    }

    [Fact]
    public void The_way_through_the_chassis_tree_reaches_every_chassis()
    {
        var reached = new HashSet<string> { "grunt1" };
        for (var round = 0; round < WiredCatalogue.Chassis.Count; round++)
            reached.UnionWith(WiredCatalogue.Chassis.Where(c => c.Needs.Any(reached.Contains)).Select(c => c.Id));

        Assert.Equal(WiredCatalogue.Chassis.Count, reached.Count);
    }

    // ---- a run's start ----------------------------------------------------------------------------

    [Fact]
    public void A_run_starts_with_the_pod_and_the_hero_and_nothing_else()
    {
        var world = NewRun();
        var state = Red(world);

        var pod = Assert.Single(world.Constructions);
        Assert.True(pod.IsType("POD"));
        Assert.Equal(Race.Red, pod.Race);
        var hero = Assert.Single(world.Machines);
        Assert.Equal((Race.Red, state.Hero, state.Pod), (hero.Race, (int?)hero.Id, (int?)pod.Id));
        Assert.Empty(world.AiControllers);
        Assert.Null(world.Script);
        Assert.Equal(VictoryCondition.Wired, world.Options.Victory);
        Assert.Equal(WiredPhase.Prepare, world.Wired!.Phase);
        Assert.Equal(0, world.Wired.Wave);
        Assert.Equal(WiredBalance.OfferSize, state.Offer.Count);
        Assert.Equal(state.Offer.Count, state.Offer.Distinct().Count());
    }

    [Fact]
    public void The_victory_alone_makes_a_wired_match_and_other_matches_have_no_run()
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7,
            new MatchOptions { Victory = VictoryCondition.Wired });
        Assert.NotNull(world.Wired);

        var plain = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7);
        var machine = plain.Machines.First(m => m.Race == Race.Red);
        Assert.Null(plain.Wired);
        Assert.Null(plain.HeroOf(Race.Red));
        Assert.True(plain.BmuCapacity(Race.Red) < int.MaxValue);
        plain.Issue(new WiredHangarCommand(Race.Red, true));
        plain.Step();
        Assert.Equal("this is not a Wired match", Assert.Single(plain.Rejected).Reason);
        Assert.Equal(0, machine.Refits);
    }

    [Theory]
    [InlineData(WiredDifficulty.Normal, 150, 100, 2)]
    [InlineData(WiredDifficulty.Hard, 110, 115, 1)]
    [InlineData(WiredDifficulty.Hardcore, 80, 130, 0)]
    public void The_difficulty_sets_the_capital_the_prices_and_the_spare_bodies(WiredDifficulty difficulty, int capital, int percent, int bodies)
    {
        var world = InHangar(new WiredOptions { Difficulty = difficulty }, bmu: 1000);
        var state = Red(world);
        Assert.Equal(capital, WiredBalance.StartCapital(difficulty));
        Assert.Equal(bodies, state.SpareBodies);

        Buy(world, "grunt2");

        Assert.Equal(1000 - (110 * percent + 50) / 100, Bank(world));
        Assert.Equal(capital, (int)NewRun(new WiredOptions { Difficulty = difficulty }).Races[Race.Red].Bmu);
    }

    [Fact]
    public void A_later_start_chassis_is_paid_for_from_a_capital_raised_by_its_price()
    {
        var world = NewRun(new WiredOptions { Chassis = "reaper2" });
        var state = Red(world);

        Assert.Equal(150, Bank(world));
        Assert.Equal("reaper2", state.Loadout.Chassis.Catalogue);
        Assert.Equal("ASSASSIN", Hero(world).Spec.SubType);
        Assert.Equal(["grunt1", "reaper2", "BOLTER"], state.Items.Select(i => i.Catalogue));
        Assert.Equal(0, state.SellValue(state.Items.First(i => i.Catalogue == "reaper2")));
        Assert.Contains(state.Items[0], state.Inventory);

        // The free bolter goes where a slot takes it, on the reaper the left.
        Assert.Equal("BOLTER", state.Loadout.Weapon(Mounting.Left)?.Catalogue);
    }

    [Fact]
    public void Creation_spends_the_capital_on_what_was_chosen_and_passes_over_what_it_cannot_cover()
    {
        var options = new WiredOptions
        {
            Items =
            [
                new WiredStartItem("BOLTER", Right),
                new WiredStartItem("plates", WiredSlot.Module(0)),
                new WiredStartItem("drive"),
                new WiredStartItem("PLASMA_CANNON2", Right),
                new WiredStartItem("nonsense"),
                new WiredStartItem("overload"),
            ],
        };
        var world = NewRun(options);
        var state = Red(world);

        // The plates leave 60 of the 150, which does not cover the drive at 80.
        Assert.Equal(60, Bank(world));
        Assert.Equal("plates", state.Loadout.Modules[0]?.Catalogue);
        Assert.Equal("BOLTER", state.Loadout.Weapon(Mounting.Right)?.Catalogue);
        Assert.DoesNotContain(state.Items, i => i.Catalogue is "drive" or "PLASMA_CANNON2" or "nonsense" or "overload");

        var preview = WiredState.Create(Data, Race.Red, options, out var bank);
        Assert.Equal(Bank(world), bank);
        Assert.Equal(state.Items, preview.Items);
    }

    [Fact]
    public void The_pod_earns_nothing_and_the_bank_has_no_ceiling()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 5000;

        for (var i = 0; i < GameWorld.TicksPerSecond * 20; i++)
            world.Step();

        Assert.Equal(5000, Bank(world));
        Assert.Equal(int.MaxValue, world.BmuCapacity(Race.Red));
    }

    // ---- the run's clock --------------------------------------------------------------------------

    [Fact]
    public void The_first_wave_comes_after_the_preparation_and_the_next_after_the_intermission()
    {
        var world = NewRun();
        var run = world.Wired!;
        var offer = Red(world).Offer.ToList();
        Assert.Equal(1, run.ShopWave);

        while (run.Phase == WiredPhase.Prepare)
            world.Step();

        Assert.Equal(WiredBalance.PrepareTime, world.Time, 0.2f);
        Assert.Equal((WiredPhase.Wave, 1, 1), (run.Phase, run.Wave, run.ShopWave));
        Assert.Equal(offer, Red(world).Offer);

        // The shop's new offer is there for the visit after the wave, and goes by the wave to come.
        world.BeginIntermission();
        Assert.Equal((WiredPhase.Intermission, 2), (run.Phase, run.ShopWave));
        Assert.Equal(WiredBalance.IntermissionTime, run.Countdown);
        Assert.NotEqual(offer, Red(world).Offer);
        Assert.All(Red(world).Offer, id => Assert.Contains(id, WiredCatalogue.ForSale(2)));

        var start = world.Time;
        while (run.Phase == WiredPhase.Intermission)
            world.Step();
        Assert.Equal(WiredBalance.IntermissionTime, world.Time - start, 0.2f);
        Assert.Equal(2, run.Wave);
    }

    [Fact]
    public void An_open_hangar_holds_the_countdown_and_the_world_runs_on()
    {
        var world = InHangar();
        var run = world.Wired!;
        var (left, tick) = (run.Countdown, world.Tick);

        for (var i = 0; i < 40; i++)
            world.Step();

        Assert.Equal(left, run.Countdown);
        Assert.Equal(tick + 40, world.Tick);

        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        world.Step();
        Assert.True(run.Countdown < left);
    }

    [Fact]
    public void The_hangar_opens_only_at_the_pod_and_closes_behind_a_hero_that_leaves()
    {
        var world = InHangar();
        var (state, hero) = (Red(world), Hero(world));
        var pod = world.WiredPod(Race.Red)!;

        hero.Position = pod.Position + new Vector2(WiredBalance.PodReach + 60, 0);
        world.Step();

        Assert.False(state.HangarOpen);
        Assert.Equal("too far from the pod", Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Equal("the hangar is closed", Do(world, new WiredBuyCommand(Race.Red, "grunt2")));
        Assert.Equal("the hangar is closed", Do(world, new WiredEquipCommand(Race.Red, state.Items[0].Id, Right)));
    }

    [Fact]
    public void Nothing_can_be_changed_in_a_wave()
    {
        var world = InHangar();
        var state = Red(world);
        var grunt2 = Buy(world, "grunt2");
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));
        Assert.Equal(WiredPhase.Wave, world.Wired!.Phase);
        Assert.False(state.HangarOpen);
        var bolter = state.Loadout.Weapon(Mounting.Right)!;

        Assert.Equal("the hangar opens only between waves", Do(world, new WiredHangarCommand(Race.Red, true)));
        foreach (var command in new Command[]
        {
            new WiredBuyCommand(Race.Red, "grunt3"),
            new WiredSellCommand(Race.Red, grunt2.Id),
            new WiredEquipCommand(Race.Red, bolter.Id, Right),
            new WiredUnequipCommand(Race.Red, bolter.Id),
            new WiredChassisCommand(Race.Red, grunt2.Id),
            new WiredUpgradeCommand(Race.Red, bolter.Id),
            new WiredRerollCommand(Race.Red),
            new WiredPodRepairCommand(Race.Red),
            new WiredChipCommand(Race.Red, null, 0),
            new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER")),
        })
            Assert.Equal("the hangar is closed", Do(world, command));
        Assert.Equal("there is no wave to wait for", Do(world, new WiredReadyCommand(Race.Red)));
    }

    [Fact]
    public void Ready_pays_for_the_time_saved_in_an_intermission_and_not_before_the_first_wave()
    {
        var world = NewRun();
        var bank = Bank(world);
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));
        Assert.Equal((WiredPhase.Wave, bank), (world.Wired!.Phase, Bank(world)));

        world.BeginIntermission();
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));

        Assert.Equal(bank + (int)WiredBalance.IntermissionTime * WiredBalance.ReadyBonus, Bank(world));
        Assert.Equal((WiredPhase.Wave, 2), (world.Wired.Phase, world.Wired.Wave));
    }

    [Fact]
    public void The_run_ends_with_the_pod_and_nobody_wins_it()
    {
        var world = InHangar();
        world.Damage(world.WiredPod(Race.Red)!, 1000000, Race.Blue);
        world.Step();

        Assert.Equal(WiredPhase.Ended, world.Wired!.Phase);
        Assert.True(world.Races[Race.Red].Defeated);
        Assert.Null(world.Winner);
        Assert.False(Red(world).HangarOpen);
        Assert.Contains(world.Events, e => e.Kind == GameEventKind.RaceDefeated);
        Assert.Equal("the hangar opens only between waves", Do(world, new WiredHangarCommand(Race.Red, true)));
    }

    [Fact]
    public void A_fallen_hero_has_no_hangar()
    {
        var world = NewRun();
        world.Damage(Hero(world), 1000000, Race.Blue);
        world.Step();

        Assert.Null(world.HeroOf(Race.Red));
        Assert.Equal("the hero is down", Do(world, new WiredHangarCommand(Race.Red, true)));
    }

    // ---- the shop ---------------------------------------------------------------------------------

    [Fact]
    public void The_shop_refuses_what_it_should()
    {
        var world = InHangar(bmu: 150);
        var state = Red(world);
        string? Try(string id) => Do(world, new WiredBuyCommand(Race.Red, id));

        Assert.Equal("that is not for sale", Try("nonsense"));
        Assert.Equal("that is not for sale", Try("tough"));
        Assert.Equal("you own that chassis already", Try("grunt1"));
        Assert.Equal("needs reaper3 or grunt3 first", Try("knight3"));
        state.Offer.Remove("MULTI_LAUNCHER2");
        Assert.Equal("that is not on offer", Try("MULTI_LAUNCHER2"));
        state.Offer.Add("MULTI_LAUNCHER2");
        Assert.Equal("not enough BMUs", Try("MULTI_LAUNCHER2"));
        Assert.Equal(150, Bank(world));
    }

    [Fact]
    public void An_offer_is_gone_once_bought_and_the_chassis_tree_stays()
    {
        var world = InHangar();
        var state = Red(world);
        var offer = state.Offer[3]!;

        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, offer)));

        Assert.Null(state.Offer[3]);
        Assert.Equal("that is not on offer", Do(world, new WiredBuyCommand(Race.Red, offer)));
        Assert.Equal(WiredBalance.OfferSize, state.Offer.Count);
        Assert.Equal(offer, state.Items[^1].Catalogue);
    }

    [Fact]
    public void The_offer_rises_with_the_waves()
    {
        // The rifles come in at wave 2, so they can only be in an offer rolled for that wave on.
        var world = NewRun();
        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));
        var seen = new HashSet<string?>();
        for (var i = 0; i < 40; i++)
        {
            world.BeginIntermission();
            seen.UnionWith(Red(world).Offer);
        }
        Assert.Contains("PLASMA_RIFLE", seen);
        Assert.DoesNotContain("HEAVY_BOLTER1", seen);

        Assert.DoesNotContain("PLASMA_CANNON2", WiredCatalogue.ForSale(1));
        Assert.DoesNotContain("vortex", WiredCatalogue.ForSale(1));
        Assert.Contains("BOLTER", WiredCatalogue.ForSale(1));
        Assert.Contains("PLASMA_CANNON2", WiredCatalogue.ForSale(10));
        Assert.Contains("vortex", WiredCatalogue.ForSale(10));
        Assert.All(WiredCatalogue.ForSale(100), id => Assert.NotEqual(WiredKind.Chassis, WiredCatalogue.KindOf(id)));
    }

    [Fact]
    public void Selling_brings_half_of_all_that_went_into_an_item_and_never_what_is_worn()
    {
        var world = InHangar(bmu: 1000);
        var state = Red(world);
        var drive = Buy(world, "drive");
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, drive.Id)));
        Assert.Equal(1000 - 80 - 170, Bank(world));

        Assert.Null(Do(world, new WiredSellCommand(Race.Red, drive.Id)));

        Assert.Equal(1000 - 80 - 170 + 125, Bank(world));
        Assert.Null(state.Item(drive.Id));
        Assert.Equal("you do not own that", Do(world, new WiredSellCommand(Race.Red, drive.Id)));
        Assert.Equal("take it off first", Do(world, new WiredSellCommand(Race.Red, state.Loadout.Weapon(Mounting.Right)!.Id)));
        Assert.Equal("take it off first", Do(world, new WiredSellCommand(Race.Red, state.Loadout.Chassis.Id)));
    }

    [Fact]
    public void Selling_never_counts_as_mining()
    {
        var world = InHangar();
        var mined = world.Races[Race.Red].Score.BmusMined;

        Assert.Null(Do(world, new WiredSellCommand(Race.Red, Buy(world, "drive").Id)));

        Assert.Equal(mined, world.Races[Race.Red].Score.BmusMined);
    }

    [Fact]
    public void A_sold_chassis_leaves_the_way_on_open()
    {
        var world = InHangar();
        var state = Red(world);
        var grunt2 = Buy(world, "grunt2");
        Assert.Null(Do(world, new WiredSellCommand(Race.Red, grunt2.Id)));

        Assert.Null(world.WhyCannotBuy(state, "reaper2"));
    }

    [Fact]
    public void The_inventory_holds_twenty_four_items_besides_what_is_worn()
    {
        var world = InHangar();
        var state = Red(world);
        var worn = state.Loadout.Weapon(Mounting.Right)!;
        for (var i = 0; i < WiredBalance.InventoryLimit; i++)
            state.Add(WiredKind.Module, "drive", 0);
        Assert.Equal(WiredBalance.InventoryLimit, state.Inventory.Count());

        state.Offer.Add("gyro");
        Assert.Equal("the inventory is full", Do(world, new WiredBuyCommand(Race.Red, "gyro")));
        Assert.Equal("no room in the inventory", Do(world, new WiredUnequipCommand(Race.Red, worn.Id)));

        // Swapping one for another keeps the count.
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, state.Inventory.First().Id, WiredSlot.Module(0))));
        Assert.Equal(WiredBalance.InventoryLimit - 1, state.Inventory.Count());
    }

    [Fact]
    public void Reroll_works_once_per_intermission()
    {
        var world = InHangar(bmu: 100);
        var state = Red(world);
        var offer = state.Offer.ToList();

        Assert.Null(Do(world, new WiredRerollCommand(Race.Red)));

        Assert.Equal(100 - WiredBalance.RerollCost, Bank(world));
        Assert.NotEqual(offer, state.Offer);
        Assert.Equal(WiredBalance.OfferSize, state.Offer.Count);
        Assert.Equal("only one reroll per intermission", Do(world, new WiredRerollCommand(Race.Red)));

        Assert.Null(Do(world, new WiredReadyCommand(Race.Red)));
        world.BeginIntermission();
        world.Races[Race.Red].Bmu = 10;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Equal("not enough BMUs", Do(world, new WiredRerollCommand(Race.Red)));
    }

    [Fact]
    public void Mk_prices_come_from_the_tables_and_end_at_the_highest_mk()
    {
        var world = InHangar(bmu: 100000);
        var state = Red(world);
        var plates = Buy(world, "plates");
        Assert.Equal(180, state.UpgradePrice(plates));
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, plates.Id)));
        Assert.Equal(320, state.UpgradePrice(state.Item(plates.Id)!));
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, plates.Id)));
        Assert.Equal(3, state.Item(plates.Id)!.Mk);
        Assert.Equal(90 + 180 + 320, state.Item(plates.Id)!.Paid);
        Assert.Equal("that has no higher Mk", Do(world, new WiredUpgradeCommand(Race.Red, plates.Id)));

        var night = Buy(world, "nightvision");
        Assert.Equal("that has no higher Mk", Do(world, new WiredUpgradeCommand(Race.Red, night.Id)));

        var gun = Buy(world, "AUTO_CANNON");
        Assert.Equal(80, state.UpgradePrice(gun));
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, gun.Id)));
        Assert.Equal(130, state.UpgradePrice(state.Item(gun.Id)!));
        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, gun.Id)));
        Assert.Equal("that has no higher Mk", Do(world, new WiredUpgradeCommand(Race.Red, gun.Id)));

        Assert.Equal("that has no higher Mk", Do(world, new WiredUpgradeCommand(Race.Red, state.Loadout.Chassis.Id)));
        Assert.Equal("you do not own that", Do(world, new WiredUpgradeCommand(Race.Red, 9999)));
        var gyro = state.Add(WiredKind.Module, "gyro", 0);
        world.Races[Race.Red].Bmu = 0;
        Assert.Equal("not enough BMUs", Do(world, new WiredUpgradeCommand(Race.Red, gyro.Id)));
    }

    [Fact]
    public void Equipping_and_taking_off_swap_freely_and_refuse_what_cannot_be()
    {
        var world = InHangar();
        var state = Red(world);
        var bolter = state.Loadout.Weapon(Mounting.Right)!;
        var bank = Bank(world);

        Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, bolter.Id)));
        Assert.Empty(Hero(world).Weapons);
        Assert.Null(Hero(world).WeaponCombo);
        Assert.Contains(bolter, state.Inventory);
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, bolter.Id, Right)));
        Assert.Equal(bank, Bank(world));
        Assert.Single(Hero(world).Weapons);

        var cannon = Buy(world, "AUTO_CANNON");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, cannon.Id, Right)));
        Assert.Contains(bolter, state.Inventory);
        Assert.Equal("AUTO_CANNON", Hero(world).Weapons[0].Spec.TypeName);

        var drive = Buy(world, "drive");
        Assert.Equal("that does not go into this slot", Do(world, new WiredEquipCommand(Race.Red, drive.Id, Right)));
        Assert.Equal("this chassis has no such slot", Do(world, new WiredEquipCommand(Race.Red, drive.Id, WiredSlot.Module(5))));
        Assert.Equal("this chassis has no such slot", Do(world, new WiredEquipCommand(Race.Red, bolter.Id, Left)));
        Assert.Equal("you do not own that", Do(world, new WiredEquipCommand(Race.Red, 9999, Right)));
        Assert.Equal("that does not go into this slot", Do(world, new WiredEquipCommand(Race.Red, state.Loadout.Chassis.Id, Right)));
        Assert.Equal("that is not on the hero", Do(world, new WiredUnequipCommand(Race.Red, bolter.Id)));
        Assert.Equal("that is not on the hero", Do(world, new WiredUnequipCommand(Race.Red, state.Loadout.Chassis.Id)));

        var ability = Buy(world, "discharge");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, ability.Id, WiredSlot.Ability(1))));
        Assert.Equal(ability, state.Loadout.Abilities[1]);
        Assert.Equal("this chassis has no such slot", Do(world, new WiredEquipCommand(Race.Red, ability.Id, WiredSlot.Ability(2))));
    }

    [Fact]
    public void The_chassis_command_takes_owned_chassis_only()
    {
        var world = InHangar();
        var state = Red(world);

        Assert.Equal("you own no such chassis", Do(world, new WiredChassisCommand(Race.Red, 9999)));
        Assert.Equal("you own no such chassis", Do(world, new WiredChassisCommand(Race.Red, state.Loadout.Weapon(Mounting.Right)!.Id)));
        Assert.Equal("the hero is on that chassis", Do(world, new WiredChassisCommand(Race.Red, state.Loadout.Chassis.Id)));
    }

    // ---- the pod ----------------------------------------------------------------------------------

    [Fact]
    public void Mending_the_pod_costs_one_bmu_for_two_hit_points()
    {
        var world = InHangar(bmu: 1000);
        var pod = world.WiredPod(Race.Red)!;
        Assert.Equal("the pod needs no repair", Do(world, new WiredPodRepairCommand(Race.Red)));

        pod.HitPoints = pod.MaxHitPoints - 100;
        Assert.Null(Do(world, new WiredPodRepairCommand(Race.Red, 10)));
        Assert.Equal(pod.MaxHitPoints - 80, pod.HitPoints);
        Assert.Equal(990, Bank(world));

        Assert.Null(Do(world, new WiredPodRepairCommand(Race.Red)));
        Assert.Equal(pod.MaxHitPoints, pod.HitPoints);
        Assert.Equal(950, Bank(world));

        pod.HitPoints = pod.MaxHitPoints - 100;
        world.Races[Race.Red].Bmu = 5;
        Assert.Null(Do(world, new WiredPodRepairCommand(Race.Red)));
        Assert.Equal(pod.MaxHitPoints - 90, pod.HitPoints);
        Assert.Equal(0, Bank(world));
        Assert.Equal("not enough BMUs", Do(world, new WiredPodRepairCommand(Race.Red)));
    }

    [Fact]
    public void A_harder_run_pays_more_for_the_pod_too()
    {
        var world = InHangar(new WiredOptions { Difficulty = WiredDifficulty.Hardcore }, bmu: 1000);
        var pod = world.WiredPod(Race.Red)!;
        pod.HitPoints = pod.MaxHitPoints - 100;

        Assert.Null(Do(world, new WiredPodRepairCommand(Race.Red)));

        Assert.Equal(pod.MaxHitPoints, pod.HitPoints);
        Assert.Equal(1000 - 65, Bank(world));
    }

    // ---- chips ------------------------------------------------------------------------------------

    [Fact]
    public void Chips_go_into_sockets_for_free_and_come_out_again()
    {
        var world = InHangar();
        var state = Red(world);
        var chip = state.Add(WiredKind.Chip, "marksman", 0);
        var bank = Bank(world);

        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 2)));
        Assert.Equal(chip, state.Loadout.Chips[2]);
        Assert.DoesNotContain(chip, state.Inventory);

        // It moves from one socket to another.
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 4)));
        Assert.Null(state.Loadout.Chips[2]);
        Assert.Equal(chip, state.Loadout.Chips[4]);

        Assert.Null(Do(world, new WiredChipCommand(Race.Red, null, 4)));
        Assert.Null(state.Loadout.Chips[4]);
        Assert.Contains(chip, state.Inventory);
        Assert.Equal(bank, Bank(world));

        Assert.Equal("that socket is empty", Do(world, new WiredChipCommand(Race.Red, null, 4)));
        Assert.Equal("no such socket", Do(world, new WiredChipCommand(Race.Red, chip.Id, WiredBalance.ChipSockets)));
        Assert.Equal("no such socket", Do(world, new WiredChipCommand(Race.Red, chip.Id, -1)));
        Assert.Equal("you do not own that", Do(world, new WiredChipCommand(Race.Red, 9999, 0)));
        Assert.Equal("that does not go into this slot", Do(world, new WiredChipCommand(Race.Red, state.Loadout.Weapon(Mounting.Right)!.Id, 0)));
    }

    [Fact]
    public void Chips_of_a_family_joined_by_a_trace_strengthen_each_other()
    {
        WiredItem Chip(int id, string name) => new(id, WiredKind.Chip, name);
        var sockets = new WiredItem?[WiredBalance.ChipSockets];
        sockets[0] = Chip(1, "tough");
        sockets[2] = Chip(2, "damper");
        Assert.False(WiredChips.Linked(sockets, 0));
        Assert.False(WiredChips.Linked(sockets, 2));
        Near(0.2f, WiredChips.Strength(sockets, 0));

        // Next to each other on the ring.
        sockets[1] = Chip(3, "nano");
        Assert.True(WiredChips.Linked(sockets, 0));
        Assert.True(WiredChips.Linked(sockets, 2));
        Near(0.3f, WiredChips.Strength(sockets, 0));

        // Another family next door does nothing, the bridge joins the first and the fourth.
        sockets[1] = Chip(3, "marksman");
        Assert.False(WiredChips.Linked(sockets, 0));
        sockets[3] = Chip(4, "reactive");
        Assert.True(WiredChips.Linked(sockets, 0));
        Assert.Equal(0, WiredChips.Strength(sockets, 5));
    }

    [Fact]
    public void A_boss_chip_is_chosen_out_of_three_from_the_pool_and_the_others_go()
    {
        var world = NewRun();
        var state = Red(world);
        Assert.Equal("no such chip to choose", Do(world, new WiredChipChoiceCommand(Race.Red, 0)));

        world.OfferChips(state);

        Assert.Equal(WiredChips.ChoiceSize, state.ChipChoice.Count);
        Assert.Equal(state.ChipChoice.Count, state.ChipChoice.Distinct().Count());
        Assert.All(state.ChipChoice, id => Assert.Contains(id, WiredChips.BasePool));
        var second = state.ChipChoice[1];

        Assert.Equal("no such chip to choose", Do(world, new WiredChipChoiceCommand(Race.Red, WiredChips.ChoiceSize)));
        Assert.Null(Do(world, new WiredChipChoiceCommand(Race.Red, 1)));

        var chip = state.Items[^1];
        Assert.Equal((WiredKind.Chip, second), (chip.Kind, chip.Catalogue));
        Assert.Empty(state.ChipChoice);

        // What the hero owns is not offered again, and an unlocked pool replaces the base one.
        for (var i = 0; i < 20; i++)
        {
            world.OfferChips(state);
            Assert.DoesNotContain(second, state.ChipChoice);
        }
        var own = NewRun(new WiredOptions { ChipPool = ["overload", "revenge", "vein", "cover"] });
        own.OfferChips(Red(own));
        Assert.All(Red(own).ChipChoice, id => Assert.Contains(id, new[] { "overload", "revenge", "vein", "cover" }));
    }

    // ---- the squad --------------------------------------------------------------------------------

    [Fact]
    public void An_escort_costs_more_than_its_hardware_comes_out_at_the_pod_and_follows_the_hero()
    {
        var world = InHangar(bmu: 1000);
        var key = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var cost = Data.Parm.Machine("AGGRESSOR GRUNT").Level(1).Cost;
        var (hero, pod) = (Hero(world), world.WiredPod(Race.Red)!);

        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));

        var escort = Assert.Single(world.Escorts(Race.Red));
        Assert.Equal(1000 - (cost * WiredBalance.EscortPercent + 50) / 100, Bank(world));
        Assert.Equal(key, escort.Key);
        Assert.Equal(1, escort.Squad);
        Assert.True(escort.DistanceTo(pod) < WiredBalance.PodReach);
        Assert.Equal(new FollowOrder(hero.Id), escort.Order);
        Assert.Same(Data.Parm.Machine("AGGRESSOR GRUNT").Level(1), escort.Stats);
    }

    [Fact]
    public void Escorts_are_limited_and_follow_the_enemy_tiers()
    {
        var world = InHangar();
        var key = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        Assert.Equal("that is not on offer yet", Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 2, "L_BOLTER"))));
        Assert.Equal("that is not on offer yet", Do(world, new WiredSquadBuyCommand(Race.Red, key with { WeaponCombo = "R_PLASMA_RIFLE" })));
        for (var i = 0; i < WiredBalance.SmallestSquad; i++)
            Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));
        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));

        var escorts = world.Escorts(Race.Red).ToList();
        world.Damage(escorts[0], 1000000, Race.Blue);
        world.Step();
        Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));

        world.Races[Race.Red].Bmu = 0;
        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(10, 5)]
    [InlineData(15, 6)]
    [InlineData(40, 6)]
    public void The_squad_grows_with_the_waves(int wave, int size) => Assert.Equal(size, WiredBalance.SquadLimit(wave));

    [Fact]
    public void The_hero_in_squad_one_is_no_escort()
    {
        var world = NewRun();
        Hero(world).Squad = WiredBalance.EscortSquad;

        Assert.Empty(world.Escorts(Race.Red));
    }

    [Fact]
    public void Squadrons_do_not_change_who_is_an_escort_nor_make_room_for_more()
    {
        var world = InHangar();
        var key = new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        for (var i = 0; i < WiredBalance.SmallestSquad; i++)
            Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, key)));
        var escorts = world.Escorts(Race.Red).ToList();

        // Put in another squadron.
        Assert.Null(Do(world, new FormSquadronCommand(Race.Red, escorts.Select(e => e.Id).ToList(), 2)));
        Assert.Equal(escorts, world.Escorts(Race.Red));
        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));

        // In none at all.
        Assert.Null(Do(world, new FormSquadronCommand(Race.Red, [], 2)));
        Assert.All(escorts, e => Assert.Null(e.Squad));
        Assert.Equal(escorts, world.Escorts(Race.Red));
        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));

        // The pod still mends them, and an empty squad one changes nothing either.
        Assert.Null(Do(world, new FormSquadronCommand(Race.Red, [], WiredBalance.EscortSquad)));
        escorts[0].HitPoints = 1;
        for (var i = 0; i < 20; i++)
            world.Step();
        Assert.True(escorts[0].HitPoints > 1);
        Assert.Equal("the squad is full", Do(world, new WiredSquadBuyCommand(Race.Red, key)));
    }

    [Fact]
    public void Only_what_was_bought_is_an_escort()
    {
        var world = NewRun();
        var stray = world.AddMachine(Race.Red, world.WiredPod(Race.Red)!.EntranceOutside!.Value, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        stray.Squad = WiredBalance.EscortSquad;

        Assert.Empty(world.Escorts(Race.Red));
    }

    // ---- the checksum -----------------------------------------------------------------------------

    [Fact]
    public void The_same_commands_make_the_same_run_and_the_checksum_sees_what_the_hero_is()
    {
        GameWorld Played()
        {
            var world = InHangar();
            Buy(world, "grunt2");
            Assert.Null(Do(world, new WiredChassisCommand(Race.Red, Red(world).Items[^1].Id)));
            Assert.Null(Do(world, new WiredRerollCommand(Race.Red)));
            Assert.Null(Do(world, new WiredSquadBuyCommand(Race.Red, new ResearchKey("AGGRESSOR", "GRUNT", 1, "R_BOLTER"))));
            for (var i = 0; i < 30; i++)
                world.Step();
            return world;
        }
        var (one, other) = (Played(), Played());

        Assert.Equal(one.Checksum(), other.Checksum());
        Assert.Equal(Red(one).Offer, Red(other).Offer);

        // The hangar being open changes nothing but the run's state.
        Assert.Null(Do(one, new WiredHangarCommand(Race.Red, false)));
        other.Step();
        Assert.NotEqual(one.Checksum(), other.Checksum());
    }

    // ---- what the review found ----------------------------------------------------------------------

    [Theory]
    [InlineData("reaper2")]
    [InlineData("commander5")]
    [InlineData("gorilla5")]
    public void A_start_chassis_paid_from_raised_capital_is_no_money_in_the_hangar(string chassis)
    {
        var world = NewRun(new WiredOptions { Chassis = chassis });
        var state = Red(world);
        var (start, grunt) = (state.Loadout.Chassis, state.Items.Single(i => i.Catalogue == "grunt1"));
        var bank = Bank(world);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));

        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, grunt.Id)));
        Assert.Null(Do(world, new WiredSellCommand(Race.Red, start.Id)));

        Assert.Equal(bank, Bank(world));
    }

    [Fact]
    public void Trader_makes_the_pods_repair_cheaper_too()
    {
        var world = InHangar();
        var state = Red(world);
        var pod = world.WiredPod(Race.Red)!;
        Assert.Equal(50, state.PodRepairPrice(100));

        var chip = state.Add(WiredKind.Chip, "trader", 0);
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 0)));

        Assert.Equal(45, state.PodRepairPrice(100));
        Assert.Equal(100, state.PodRepairFor(45));
        pod.HitPoints -= 100;
        var bank = Bank(world);
        Assert.Null(Do(world, new WiredPodRepairCommand(Race.Red)));
        Assert.Equal(bank - 45, Bank(world));
        Assert.Equal(pod.MaxHitPoints, pod.HitPoints);
    }

    /// <summary>The hero as far as the bays of the pod, with its hangar open.</summary>
    private static (GameWorld World, Machine Hero, Construction Pod) InThePod()
    {
        var world = NewRun();
        world.Races[Race.Red].Bmu = 100000;
        var (hero, pod) = (Hero(world), world.WiredPod(Race.Red)!);
        Assert.Null(Do(world, new EnterGarrisonCommand(Race.Red, [hero.Id], pod.Id)));
        for (var i = 0; i < 20 * 20 && hero.Inside is null; i++)
            world.Step();
        Assert.Equal(pod.Id, hero.Inside);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        return (world, hero, pod);
    }

    [Fact]
    public void A_chassis_too_big_for_the_pods_door_comes_out_by_it()
    {
        var (world, hero, pod) = InThePod();

        Board(world, "reaper2");

        Assert.Null(hero.Inside);
        Assert.Null(hero.Doorway);
        Assert.DoesNotContain(hero.Id, pod.Garrisoned);
        Assert.Equal(pod.EntranceOutside!.Value, hero.Position);
        Assert.True(world.Nav.IsFree(hero.Position, crossesWater: false));
        Assert.True(Red(world).HangarOpen);

        // Out there it drives off like any other, away from the door.
        var away = hero.Position - pod.Position;
        hero.Facing = DetMath.Atan2(away.Y, away.X);
        var start = hero.Position;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        for (var i = 0; i < 20 * 2; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, 1));
            world.Step();
        }
        Assert.True(Vector2.Distance(start, hero.Position) > 3);
    }

    [Fact]
    public void A_chassis_the_door_takes_stays_in_the_pod()
    {
        var (world, hero, pod) = InThePod();

        Board(world, "grunt2");

        Assert.Equal(pod.Id, hero.Inside);
        Assert.Contains(hero.Id, pod.Garrisoned);
    }

    /// <summary>A hero on the Grunt with two guns, both just fired, and driven with both held.</summary>
    private static (GameWorld World, Machine Hero, WeaponMount Left, WeaponMount Right, Vector2 Aim) BothFired()
    {
        var world = InHangar();
        Board(world, "grunt3");
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, Red(world).Items.Single(i => i.Catalogue == "BOLTER").Id, Left)));
        Wear(world, "AUTO_CANNON", Right);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        var (bolter, cannon) = (hero.Weapons.Single(w => w.Mounting == Mounting.Left), hero.Weapons.Single(w => w.Mounting == Mounting.Right));
        var aim = hero.Position + new Vector2(30, 0);
        world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: aim, Weapons: 0b11));
        world.Step();
        Assert.True(bolter.Reload > 0);
        Assert.True(cannon.Reload > 0);
        return (world, hero, bolter, cannon, aim);
    }

    /// <summary>A hero with twin guns of one kind, cold and ready to fire.</summary>
    private static (GameWorld World, Machine Hero, WeaponMount Left, WeaponMount Right, Vector2 Aim) Twins(string weapon)
    {
        var world = InHangar();
        Board(world, "grunt3");
        Wear(world, weapon, Left);
        Wear(world, weapon, Right);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        return (world, hero, hero.Weapons.Single(w => w.Mounting == Mounting.Left), hero.Weapons.Single(w => w.Mounting == Mounting.Right), hero.Position + new Vector2(30, 0));
    }

    [Theory]
    [InlineData("BOLTER")]
    [InlineData("PULSE_RIFLE")]
    [InlineData("AUTO_CANNON")]
    public void Of_twin_guns_only_the_one_held_shows_a_shot(string weapon)
    {
        var (world, hero, left, right, aim) = Twins(weapon);
        Assert.Equal([left, right], HeroGuns.Firing(hero.Weapons));

        var shots = 0;
        for (var i = 0; i < 20 * 10; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: aim, Weapons: 0b10));
            world.Step();
            if (world.Events.Any(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id))
            {
                shots++;
                Assert.Equal([right], HeroGuns.Firing(hero.Weapons));
            }
        }

        Assert.True(shots >= 3, $"{shots} shots");
    }

    [Theory]
    [InlineData("BOLTER")]
    [InlineData("AUTO_CANNON")]
    public void Twin_guns_held_together_both_show_their_shots(string weapon)
    {
        var (world, hero, left, right, aim) = Twins(weapon);

        var shots = 0;
        for (var i = 0; i < 20 * 10; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: aim, Weapons: 0b11));
            world.Step();
            if (world.Events.Any(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id))
            {
                shots++;
                Assert.Equal([left, right], HeroGuns.Firing(hero.Weapons));
            }
        }

        Assert.True(shots >= 3, $"{shots} shots");
    }

    [Fact]
    public void A_gun_whose_button_is_not_held_goes_on_reloading_and_finishes_its_burst()
    {
        var (world, hero, _, cannon, aim) = BothFired();

        for (var i = 0; i < 20 * 10; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: aim, Weapons: 0b01));
            world.Step();
        }

        Assert.True(cannon.Reload <= 0);
        Assert.Equal(0, cannon.RoundsLeft);
    }

    [Fact]
    public void Guns_reload_with_no_button_held_and_a_gun_not_held_does_not_open_fire()
    {
        var (world, hero, bolter, cannon, aim) = BothFired();

        for (var i = 0; i < 20 * 10; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Weapons: 0));
            world.Step();
        }
        Assert.True(bolter.Reload <= 0);
        Assert.True(cannon.Reload <= 0);

        world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: aim, Weapons: 0b10));
        world.Step();

        Assert.True(bolter.Reload <= 0);
        Assert.True(cannon.Reload > 0);
    }

    /// <summary>A machine with two guns in a match without Wired, both just fired, driven with both held.</summary>
    private static (GameWorld World, Machine Machine, WeaponMount Right, Vector2 Aim) BothFiredInAMatch(bool buttons)
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        var at = world.Nav.NearestFreePoint(new Vector2(600, 600), crossesWater: false)!.Value;
        var machine = world.AddMachine(Race.Red, at, "AGGRESSOR", "GRUNT", 3, "LR_BOLTER_X2");
        var aim = at + new Vector2(30, 0);
        world.Issue(new DriveCommand(Race.Red, machine.Id, Driving: true, Aim: aim, Weapons: 0b11, Buttons: buttons));
        world.Step();
        var right = machine.Weapons[1];
        Assert.True(right.Reload > 0);
        return (world, machine, right, aim);
    }

    [Fact]
    public void Outside_a_run_the_buttons_for_each_weapon_ask_for_it_and_the_tab_selection_stays_as_it_was()
    {
        var (world, machine, right, aim) = BothFiredInAMatch(buttons: true);
        for (var i = 0; i < 20 * 10; i++)
        {
            world.Issue(new DriveCommand(Race.Red, machine.Id, Driving: true, Aim: aim, Weapons: 0b01, Buttons: true));
            world.Step();
        }
        Assert.True(right.Reload <= 0);

        (world, machine, right, aim) = BothFiredInAMatch(buttons: false);
        var left = right.Reload;
        for (var i = 0; i < 20 * 10; i++)
        {
            world.Issue(new DriveCommand(Race.Red, machine.Id, Driving: true, Aim: aim, Weapons: 0b01));
            world.Step();
        }
        Assert.Equal(left, right.Reload);
    }

    /// <summary>The first place of the ring round a taken one, with the given sine and cosine, on the world's free ground: one where they make a difference to the bits.</summary>
    private static Vector2 PlaceWhereTheBitsDiffer(GameWorld world, float distance)
    {
        var (mine, library) = (new Vector2(DetMath.Cos(0), DetMath.Sin(0)), new Vector2(MathF.Cos(0), MathF.Sin(0)));
        var pod = world.Constructions.First().Position;
        for (var step = 0; step < 100000; step++)
        {
            var at = pod + new Vector2(120 + step * 0.0137f, 90);
            if (at + mine * distance != at + library * distance && world.Nav.IsFree(at, crossesWater: false)
                && world.Nav.IsFree(at + mine * distance, crossesWater: false))
                return at;
        }
        throw new InvalidOperationException("no such place");
    }

    private static Vector2 FreeSpotNear(GameWorld world, Vector2 point, float clearance) =>
        (Vector2)typeof(GameWorld).GetMethod("FreeSpotNear", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(world, [point, clearance])!;

    [Fact]
    public void In_a_run_the_ring_round_a_taken_place_has_the_same_bits_everywhere_and_elsewhere_stays_as_it_was()
    {
        const float Clearance = 1, Distance = 2.5f * Clearance;
        var run = NewRun();
        var at = PlaceWhereTheBitsDiffer(run, Distance);
        run.AddMachine(Race.Blue, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        Assert.Equal(at + new Vector2(DetMath.Cos(0), DetMath.Sin(0)) * Distance, FreeSpotNear(run, at, Clearance));

        var match = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        match.AddMachine(Race.Blue, at, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        Assert.Equal(at + new Vector2(MathF.Cos(0), MathF.Sin(0)) * Distance, FreeSpotNear(match, at, Clearance));
    }

    // ---- what the build does to a driven hero -------------------------------------------------------

    /// <summary>A run that stays in its preparation, with its hangar open and a bank that does not get in the way.</summary>
    private static GameWorld Waiting()
    {
        var world = InHangar();
        world.Wired!.Countdown = 100_000;
        return world;
    }

    private static void Leave(GameWorld world) => Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));

    private static void Upgrade(GameWorld world, WiredItem item) => Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, item.Id)));

    private static void Pass(GameWorld world, float seconds)
    {
        for (var i = 0; i < seconds * GameWorld.TicksPerSecond; i++)
            world.Step();
    }

    /// <summary>A free point at a distance from the hero, on the side away from the pod.</summary>
    private static Vector2 Away(GameWorld world, float metres)
    {
        var hero = Hero(world);
        var way = Vector2.Normalize(hero.Position - world.WiredPod(Race.Red)!.Position);
        return world.Nav.NearestFreePoint(hero.Position + way * metres, crossesWater: false)!.Value;
    }

    private static Machine Spy(GameWorld world, Vector2 at) =>
        world.AddMachine(Race.Blue, at, WiredDirector.Spy.Type, WiredDirector.Spy.SubType, WiredDirector.Spy.HardwareLevel, WiredDirector.Spy.WeaponCombo);

    /// <summary>Holds the first trigger on a point until so many rounds are out; what came of it, each round's event and each hit.</summary>
    private static (List<GameEvent> Fired, int Hits) Shoot(GameWorld world, Vector2 aim, float height, int rounds, Action? everyTick = null)
    {
        var hero = Hero(world);
        var (fired, hits) = (new List<GameEvent>(), 0);
        for (var i = 0; i < 20 * 1000 && fired.Count < rounds; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: aim, Weapons: 1, AimHeight: height));
            everyTick?.Invoke();
            world.Step();
            fired.AddRange(world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id));
            hits += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Other == hero.Id);
        }
        Assert.Equal(rounds, fired.Count);
        return (fired, hits);
    }

    /// <summary>A foe far enough off to be picked out, and the hits of rounds sent somewhere on it.</summary>
    private static int HitsOn(GameWorld world, Machine foe, Func<(Vector2 Point, float Height)> aim, int rounds)
    {
        var hero = Hero(world);
        var (fired, hits) = (0, 0);
        for (var i = 0; i < 20 * 1000 && fired < rounds; i++)
        {
            var (point, height) = aim();
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: point, Weapons: 1, AimHeight: height));
            foe.HitPoints = foe.MaxHitPoints;
            foe.Armour = foe.MaxArmour;
            hero.HitPoints = hero.MaxHitPoints;
            world.Step();
            fired += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id);
            hits += world.Events.Count(e => e.Kind == GameEventKind.Hit && e.Actor == foe.Id && e.Other == hero.Id);
        }
        Assert.Equal(rounds, fired);
        return hits;
    }

    [Theory]
    [InlineData("AGGRESSOR", "ASSASSIN", 3, "LR_AUTO_CANNON_X2")]
    [InlineData("AGGRESSOR", "KNIGHT", 3, "LR_HEAVY_BOLTER2_X2")]
    public void A_sight_on_the_top_of_a_tall_foe_is_taken_into_its_box(string type, string subType, int level, string combo)
    {
        var world = Waiting();
        var at = Away(world, 22);
        var foe = world.AddMachine(Race.Blue, at, type, subType, level, combo);
        var top = world.AimPoint(foe).Z * 2 - (world.Terrain?.At(foe.Position) ?? 0);

        // Over the box the rounds go past; taken in, they stop in it.
        Assert.Equal(0, HitsOn(world, foe, () => (foe.Position, top + 3), 8));
        Assert.True(HitsOn(world, foe, () => world.AimInside(foe, foe.Position, top + 3), 8) >= 6);
    }

    [Fact]
    public void A_sight_on_a_flyers_picture_is_taken_into_its_box_and_hits()
    {
        var world = Waiting();
        var wasp = world.AddMachine(Race.Blue, Away(world, 24), "ADMINISTRATOR", "BOSS", 1, "T_WASP_LIGHT_STING");
        var picture = world.AimPoint(wasp).Z + wasp.Radius + 3;

        Assert.Equal(0, HitsOn(world, wasp, () => (wasp.Position, picture), 8));
        var (point, height) = world.AimInside(wasp, wasp.Position, picture);
        Assert.Equal(world.AimPoint(wasp).Z, height);
        Assert.Equal(wasp.Position, point);
        Assert.True(HitsOn(world, wasp, () => world.AimInside(wasp, wasp.Position, picture), 8) >= 6);
    }

    [Fact]
    public void A_point_already_in_the_box_stays_where_the_sight_put_it()
    {
        var world = Waiting();
        var foe = Spy(world, Away(world, 22));
        var ground = world.Terrain?.At(foe.Position) ?? 0;
        var (point, height) = world.AimInside(foe, foe.Position + new Vector2(foe.Radius / 2, 0), ground + 0.4f);
        Assert.Equal(foe.Position + new Vector2(foe.Radius / 2, 0), point);
        Assert.Equal(ground + 0.4f, height);

        // Beside it, the nearest of it.
        Assert.Equal(foe.Position + new Vector2(foe.Radius, 0), world.AimInside(foe, foe.Position + new Vector2(foe.Radius + 3, 0), ground + 0.4f).Point);
    }

    /// <summary>How far the rounds went from a point on average, and at most.</summary>
    private static (float Mean, float Most) Strays(GameWorld world, Vector2 aim, float height, int rounds)
    {
        var offs = Shoot(world, aim, height, rounds).Fired.Select(e => Vector3.Distance(e.Mark!.Value, new Vector3(aim, height))).ToList();
        return (offs.Average(), offs.Max());
    }

    [Fact]
    public void A_driven_heros_rounds_stray_by_its_accuracy_and_a_targeting_computer_draws_them_in()
    {
        var world = Waiting();
        var computer = Wear(world, "targeting", WiredSlot.Module(0));
        Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, computer.Id)));
        Leave(world);
        var aim = Away(world, 30);
        var way = Vector2.Distance(Hero(world).Position, aim);
        var inaccuracy = 100 - Hero(world).Stats.Accuracy!.Value;

        Assert.Equal(inaccuracy * WiredBalance.StrayPerPoint, world.AimSpread(Hero(world)), 3);
        var bare = Strays(world, aim, 2, 150);
        Assert.InRange(bare.Most, 0.1f, way * MathF.Tan(world.AimSpread(Hero(world)) * MathF.PI / 180) * 1.01f);

        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, computer.Id, WiredSlot.Module(0))));
        Upgrade(world, computer);
        Upgrade(world, computer);
        Leave(world);

        Assert.Equal((inaccuracy - 30) * WiredBalance.StrayPerPoint, world.AimSpread(Hero(world)), 3);
        var steady = Strays(world, aim, 2, 150);
        Assert.InRange(steady.Mean / bare.Mean, 0.1f, 0.4f);
    }

    /// <summary>The share of a hero's bolts that hit a target at the far end of the gun's range, on a night as black as can be.</summary>
    private static float HitsAtNight(string? module)
    {
        var world = Waiting();
        if (module is not null)
            Wear(world, module, WiredSlot.Module(0));
        Leave(world);
        var target = Spy(world, Away(world, 33));
        var (place, middle) = (target.Position, world.AimPoint(target));
        world.ForcedDarkness = 1;
        Pass(world, 2);

        const int Rounds = 120;
        var (_, hits) = Shoot(world, place, middle.Z, Rounds, () =>
        {
            (target.Position, target.HitPoints, target.Order) = (place, target.MaxHitPoints, Order.Idle);
            target.Path.Clear();
        });
        return hits / (float)Rounds;
    }

    [Fact]
    public void Night_spoils_a_driven_heros_aim_and_night_vision_saves_it()
    {
        var world = Waiting();
        Leave(world);
        var hero = Hero(world);
        var day = world.AimSpread(hero);
        world.ForcedDarkness = 1;
        Pass(world, 2);
        Assert.Equal(day + 40 * WiredBalance.StrayPerPoint, world.AimSpread(hero), 3);

        var (blind, sighted) = (HitsAtNight(null), HitsAtNight("nightvision"));
        Assert.True(blind < 0.9f, $"{blind} of the rounds hit in the dark");
        Assert.True(sighted > 0.95f, $"{sighted} of the rounds hit with night vision");
    }

    [Fact]
    public void Hits_shake_a_driven_heros_aim()
    {
        var world = Waiting();
        Leave(world);
        var hero = Hero(world);
        var steady = world.AimSpread(hero);

        world.Damage(hero, hero.MaxArmour + hero.MaxHitPoints / 2, Race.Blue);

        Assert.True(world.AimSpread(hero) > steady);
    }

    [Fact]
    public void The_same_shots_stray_and_hurt_alike_in_every_copy_of_a_run()
    {
        (List<Vector3?> Marks, ulong Sum) Play()
        {
            var world = Waiting();
            Upgrade(world, Wear(world, "AUTO_CANNON", Right));
            Leave(world);
            var target = Spy(world, Away(world, 30));
            var marks = Shoot(world, target.Position, 2, 60).Fired.Select(e => e.Mark).ToList();
            return (marks, world.Checksum());
        }

        var (first, second) = (Play(), Play());

        Assert.Equal(first.Marks, second.Marks);
        Assert.Equal(first.Sum, second.Sum);
    }

    [Fact]
    public void Only_a_run_makes_rounds_stray()
    {
        var match = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        var machine = match.AddMachine(Race.Red, match.Constructions.First(c => c.Race == Race.Red).EntranceOutside!.Value, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        var fired = new List<GameEvent>();

        for (var i = 0; i < 20 * 3; i++)
        {
            match.Issue(new DriveCommand(Race.Red, machine.Id, Driving: true, Aim: machine.Position + new Vector2(20, 0), AimHeight: 2));
            match.Step();
            fired.AddRange(match.Events.Where(e => e.Kind == GameEventKind.Fired));
        }

        Assert.NotEmpty(fired);
        Assert.All(fired, e => Assert.Null(e.Mark));
    }

    /// <summary>What a round of a gun comes to on average, driven at something standing right in front of it.</summary>
    private static float DamagePerRound(GameWorld world, string weapon)
    {
        Leave(world);
        var target = Spy(world, Away(world, 6));
        var (place, middle) = (target.Position, world.AimPoint(target));

        Shoot(world, place, middle.Z, 600, () =>
        {
            (target.Position, target.HitPoints, target.Armour, target.Order) = (place, target.MaxHitPoints, target.MaxArmour, Order.Idle);
            target.Path.Clear();
        });
        var use = Red(world).Stats.Weapon(weapon);
        return use.Damage / (float)use.Rounds;
    }

    [Theory]
    [InlineData(1, 3)]
    [InlineData(2, 3.6f)]
    [InlineData(3, 4.2f)]
    public void Every_Mk_of_a_small_gun_lands_as_the_hangar_shows_it(int mk, float damage)
    {
        var world = Waiting();
        var gun = Wear(world, "AUTO_CANNON", Right);
        for (var step = 1; step < mk; step++)
            Upgrade(world, gun);
        Assert.Equal(damage, Hero(world).Weapons.Single().Spec.Damage, 3);

        Assert.InRange(DamagePerRound(world, "AUTO_CANNON"), damage - 0.1f, damage + 0.1f);
    }

    [Fact]
    public void Glowing_barrels_add_their_share_to_a_small_gun()
    {
        var world = Waiting();
        Wear(world, "AUTO_CANNON", Right);
        var chip = Red(world).Add(WiredKind.Chip, "hotbarrels", 0);
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 0)));

        Assert.InRange(DamagePerRound(world, "AUTO_CANNON"), 3.35f, 3.55f);
    }

    [Fact]
    public void Sustained_fire_adds_to_a_small_gun_from_its_first_seconds()
    {
        var world = Waiting();
        var chip = Red(world).Add(WiredKind.Chip, "sustained", 0);
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, chip.Id, 0)));
        Leave(world);
        var (state, hero) = (Red(world), Hero(world));
        var target = world.AddMachine(Race.Blue, Away(world, 40), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        state.LastShotAt = world.Time;
        state.FiringSince = world.Time - 5;

        var lost = 0;
        for (var i = 0; i < 2000; i++)
        {
            target.HitPoints = target.MaxHitPoints;
            target.Armour = target.MaxArmour;
            world.Damage(target, 3, Race.Red, hero, "AUTO_CANNON");
            lost += target.MaxHitPoints + target.MaxArmour - target.HitPoints - target.Armour;
        }

        Assert.InRange(lost / 2000f, 3.2f, 3.4f);
    }

    /// <summary>The hull's turn in a second of the turn key held, in degrees.</summary>
    private static float TurnInASecond(GameWorld world, Machine machine)
    {
        var start = machine.Facing;
        for (var i = 0; i < GameWorld.TicksPerSecond; i++)
        {
            world.Issue(new DriveCommand(machine.Race, machine.Id, Driving: true, Turn: 1));
            world.Step();
        }
        return (machine.Facing - start) * 180 / MathF.PI;
    }

    [Fact]
    public void A_gyro_turns_a_driven_hull_faster_and_each_Mk_faster_still()
    {
        var world = Waiting();
        var fast = Data.Parm.General.FirstPersonMaxFastTurnRate;

        Assert.Equal(fast, TurnInASecond(world, Hero(world)), 1);
        Assert.Equal(fast, world.DrivenTurnRate(Hero(world), slow: false));

        var gyro = Wear(world, "gyro", WiredSlot.Module(0));
        Assert.Equal(fast * 1.2f, TurnInASecond(world, Hero(world)), 1);
        Assert.Equal(Data.Parm.General.FirstPersonMaxSlowTurnRate * 1.2f, world.DrivenTurnRate(Hero(world), slow: true), 3);

        Upgrade(world, gyro);
        Upgrade(world, gyro);
        Assert.Equal(fast * 1.6f, TurnInASecond(world, Hero(world)), 1);
    }

    [Fact]
    public void Overload_turns_a_driven_hull_slower_on_any_chassis()
    {
        var world = Waiting();
        Board(world, "grunt3");
        Wear(world, "HEAVY_BOLTER2", Left);
        Wear(world, "HEAVY_BOLTER2", Right);
        Wear(world, "plates", WiredSlot.Module(0));
        Wear(world, "plates", WiredSlot.Module(1));
        var build = Red(world).Build(Data);
        Assert.Equal(WiredBalance.MostLoad, build.LoadShare, 3);

        Assert.True(Data.Parm.Machine("AGGRESSOR GRUNT").Level(3).RotationSpeed * build.TurnShare > Data.Parm.General.FirstPersonMaxFastTurnRate);
        Assert.Equal(Data.Parm.General.FirstPersonMaxFastTurnRate * 0.7f, TurnInASecond(world, Hero(world)), 1);
    }

    [Fact]
    public void A_driven_machine_that_is_no_hero_turns_as_ever()
    {
        var world = Waiting();
        Wear(world, "gyro", WiredSlot.Module(0));
        var other = world.AddMachine(Race.Red, Away(world, 30), "AGGRESSOR", "GRUNT", 1, "R_BOLTER");

        Assert.Equal(Data.Parm.General.FirstPersonMaxFastTurnRate, TurnInASecond(world, other), 1);
    }

    /// <summary>A hero on the Bee with its bomb, in the air at the pod.</summary>
    private static GameWorld OnTheBee()
    {
        var world = Waiting();
        Board(world, "boss2");
        Wear(world, "BEE_BOMB", Top);
        Leave(world);
        return world;
    }

    [Fact]
    public void A_bee_bomb_leaves_the_bee_that_drops_it_unhurt_and_hits_what_stands_below()
    {
        var world = OnTheBee();
        var hero = Hero(world);
        var (before, aim) = (hero.Durability, Away(world, 28));
        var below = world.AddMachine(Race.Blue, aim, "AGGRESSOR", "GRUNT", 1, "R_BOLTER");
        below.Order = new StandGroundOrder();

        var (fired, _) = Shoot(world, aim, 2, 1);
        Assert.Null(fired[0].Mark);
        Assert.Equal(aim, world.Blasts.Single().Position);
        Pass(world, 5);

        Assert.Equal(before, hero.Durability);
        Assert.True(below.IsDead);
    }

    [Fact]
    public void A_bee_bomb_is_no_danger_to_what_flies_in_a_run_and_elsewhere_it_is_as_ever()
    {
        var world = OnTheBee();
        var wasp = world.AddMachine(Race.Red, Away(world, 10), "ADMINISTRATOR", "BOSS", 1, "T_WASP_LIGHT_STING");
        var before = wasp.Durability;
        world.StartBlast(Data.Parm.Weapon("BEE_BOMB"), wasp.Position, Race.Blue);
        Pass(world, 5);
        Assert.Equal(before, wasp.Durability);

        var match = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Player }, 7);
        var bee = match.AddMachine(Race.Red, match.Constructions.First(c => c.Race == Race.Red).EntranceOutside!.Value, "ADMINISTRATOR", "BOSS", 2, "T_BEE_BOMB");
        before = bee.Durability;
        match.StartBlast(Data.Parm.Weapon("BEE_BOMB"), bee.Position, Race.Blue);
        Pass(match, 5);
        Assert.NotEqual(before, bee.Durability);
    }

    // ---- what was left of the reviews ---------------------------------------------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Every_bolter_among_the_start_items_after_the_first_is_bought_like_any_other_gun(int bolters)
    {
        var entry = new WiredStartItem("BOLTER");

        var state = WiredState.Create(Data, Race.Red, new WiredOptions { Items = Enumerable.Repeat(entry, bolters).ToList() }, out var bank);

        var price = WiredBalance.Priced(WiredCatalogue.Price("BOLTER"), WiredDifficulty.Normal);
        Assert.Equal(bolters, state.Items.Count(i => i.Catalogue == "BOLTER"));
        Assert.Equal(WiredBalance.StartCapital(WiredDifficulty.Normal) - (bolters - 1) * price, bank);
    }

    [Fact]
    public void A_second_bolter_asked_for_on_the_free_ones_slot_takes_that_slot()
    {
        var entry = new WiredStartItem("BOLTER", Right);

        var state = WiredState.Create(Data, Race.Red, new WiredOptions { Items = [entry, entry with { }] }, out _);

        var bolters = state.Items.Where(i => i.Catalogue == "BOLTER").ToList();
        Assert.Equal(2, bolters.Count);
        Assert.Equal(bolters[1], state.Loadout.Weapon(Mounting.Right));
        Assert.Contains(bolters[0], state.Inventory);
    }

    [Fact]
    public void Glowing_barrels_draw_power_for_a_chassis_own_weapon_too()
    {
        var chip = (WiredSlot.Chip(0), "hotbarrels", 1);

        var bare = HeroBuild.Resolve(Data, Loadout("gorilla5", (Left, "BOLTER", 1), (Right, "BOLTER", 1)));
        var hot = HeroBuild.Resolve(Data, Loadout("gorilla5", (Left, "BOLTER", 1), (Right, "BOLTER", 1), chip));
        Assert.Equal(3, hot.Weapons.Count);
        Assert.Equal(bare.PowerDraw + 3, hot.PowerDraw);

        // The chassis' own weapon alone comes to the point too.
        Assert.Equal(HeroBuild.Resolve(Data, Loadout("gorilla5")).PowerDraw + 1, HeroBuild.Resolve(Data, Loadout("gorilla5", chip)).PowerDraw);
    }

    [Theory]
    [InlineData("PLASMA_RIFLE", 6)]
    [InlineData("PULSE_CANNON", 22.5f)]
    [InlineData("PLASMA_CANNON1", 10)]
    public void The_hangar_counts_a_variable_burst_whole(string weapon, float damage) =>
        Near(damage, HeroBuild.Resolve(Data, Loadout("knight4", (Left, weapon, 1))).DamagePerSecond);

    [Theory]
    [InlineData("PLASMA_RIFLE")]
    [InlineData("PULSE_CANNON")]
    [InlineData("PLASMA_CANNON1")]
    [InlineData("PULSE_RIFLE")]
    [InlineData("AUTO_CANNON")]
    public void The_hangars_damage_is_what_a_driven_hero_fires(string weapon)
    {
        const int Seconds = 60;
        var world = Waiting();
        Board(world, "knight4");
        Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, Red(world).Items.Single(i => i.Catalogue == "BOLTER").Id)));
        Wear(world, weapon, Left);
        Leave(world);
        var hero = Hero(world);
        var gun = Assert.Single(Red(world).Build(Data).Weapons);
        var rounds = 0;

        for (var i = 0; i < Seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: hero.Position + new Vector2(20, 0), Weapons: 1));
            world.Step();
            rounds += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id);
        }

        Assert.Equal(gun.DamagePerSecond, rounds * gun.Spec.Damage / Seconds, gun.DamagePerSecond * 0.05f);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void The_hangars_damage_is_what_a_driven_hero_fires_when_the_reload_falls_between_two_ticks(int mk)
    {
        const int Seconds = 60;
        var world = Waiting();
        Board(world, "boss2");
        var sting = Wear(world, "WASP_LIGHT_STING", Top);
        for (var i = 1; i < mk; i++)
            Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, sting.Id)));
        Leave(world);
        var hero = Hero(world);
        var guns = Red(world).Build(Data).Weapons.ToList();
        var gun = guns.Single(w => w.Item?.Id == sting.Id);
        var rounds = 0;

        for (var i = 0; i < Seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: hero.Position + new Vector2(20, 0), Weapons: 1 << guns.IndexOf(gun)));
            world.Step();
            rounds += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id);
        }

        Assert.Equal(gun.DamagePerSecond, rounds * gun.Spec.Damage / Seconds, gun.DamagePerSecond * 0.02f);
    }

    [Fact]
    public void The_hangars_damage_is_what_a_driven_hero_punches()
    {
        const int Seconds = 60;
        var world = OnTheGorilla();
        var hero = Hero(world);
        var gun = Assert.Single(Red(world).Build(Data).Weapons);
        var aim = Away(world, 3);
        Dummy(world, aim);
        var rounds = 0;

        for (var i = 0; i < Seconds * GameWorld.TicksPerSecond; i++)
        {
            world.Issue(new DriveCommand(Race.Red, hero.Id, Driving: true, Aim: aim, Weapons: 1));
            world.Step();
            rounds += world.Events.Count(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id);
        }

        Assert.Equal(gun.DamagePerSecond, rounds * gun.Spec.Damage / Seconds, gun.DamagePerSecond * 0.1f);
    }

    [Theory]
    [InlineData("grunt1", "BOLTER", null)]
    [InlineData("grunt1", "HEAVY_BOLTER1", "that needs a medium slot")]
    [InlineData("grunt1", "PLASMA_CANNON2", "that needs a heavy slot")]
    [InlineData("grunt1", "WASP_LIGHT_STING", "that fits only the top slot of a glider")]
    [InlineData("grunt1", "BEE_BOMB", "that fits only the top slot of a glider")]
    [InlineData("boss1", "WASP_LIGHT_STING", null)]
    public void A_weapon_with_no_slot_says_what_would_take_it(string chassis, string weapon, string? why) =>
        Assert.Equal(why, Loadout(chassis).WhyNoSlot(new WiredItem(1, WiredKind.Weapon, weapon)));

    /// <summary>Puts a chip in the first socket, past the hangar.</summary>
    private static void Socket(GameWorld world, string chip)
    {
        var state = Red(world);
        state.Loadout = state.Loadout.With(WiredSlot.Chip(0), state.Add(WiredKind.Chip, chip, 0));
    }

    /// <summary>A hero on a Gorilla with nothing but its fists.</summary>
    private static GameWorld OnTheGorilla()
    {
        var world = Waiting();
        Board(world, "gorilla5");
        Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, Red(world).Items.Single(i => i.Catalogue == "BOLTER").Id)));
        Leave(world);
        return world;
    }

    /// <summary>A machine of the waves that stands, does not shoot back and takes a good many hits.</summary>
    private static Machine Dummy(GameWorld world, Vector2 at)
    {
        var dummy = world.AddMachine(Race.Blue, at, "AGGRESSOR", "NINJA", 5, "L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7");
        dummy.Weapons.Clear();
        dummy.Order = new StandGroundOrder();
        return dummy;
    }

    /// <summary>What one round of the hero's first gun takes off a machine standing near, after <paramref name="before"/> has set up its chips.</summary>
    private static int DrawnByOneRound(GameWorld world, float metres, Action<WiredState> before)
    {
        var aim = Away(world, metres);
        var target = Dummy(world, aim);
        var start = target.HitPoints + target.Armour;
        before(Red(world));

        Shoot(world, aim, 2, 1);
        Pass(world, 5);

        return start - target.HitPoints - target.Armour;
    }

    private static readonly Action<WiredState> Unbroken = state => (state.LastShotAt, state.FiringSince) = (0, -100);

    [Fact]
    public void Revenge_counts_for_the_heros_bee_bomb()
    {
        var plain = DrawnByOneRound(OnTheBee(), 28, _ => { });
        var world = OnTheBee();
        Socket(world, "revenge");

        var avenged = DrawnByOneRound(world, 28, state => state.RevengeUntil = 1000);

        Assert.True(plain > 0);
        Assert.Equal(plain * 1.3f, avenged, plain * 0.05f);
    }

    [Fact]
    public void Sustained_fire_counts_for_the_heros_bee_bomb()
    {
        var plain = DrawnByOneRound(OnTheBee(), 28, _ => { });
        var world = OnTheBee();
        Socket(world, "sustained");

        var sustained = DrawnByOneRound(world, 28, Unbroken);

        Assert.Equal(plain * 1.3f, sustained, plain * 0.05f);
    }

    [Fact]
    public void Revenge_and_sustained_fire_count_for_the_heros_fists()
    {
        var plain = DrawnByOneRound(OnTheGorilla(), 6, _ => { });
        var (avenging, firing) = (OnTheGorilla(), OnTheGorilla());
        Socket(avenging, "revenge");
        Socket(firing, "sustained");

        Assert.True(plain > 0);
        Assert.Equal(plain * 1.3f, DrawnByOneRound(avenging, 6, state => state.RevengeUntil = 1000), plain * 0.05f);
        Assert.Equal(plain * 1.3f, DrawnByOneRound(firing, 6, Unbroken), plain * 0.05f);
        Assert.True(Red(avenging).Stats.Weapon("GORILLA_PUNCH").Damage > 0);
    }

    [Fact]
    public void A_blast_of_the_heros_race_while_it_is_down_is_on_no_weapons_account()
    {
        var world = OnTheBee();
        var aim = Away(world, 28);
        Dummy(world, aim);
        world.Damage(Hero(world), 1_000_000, Race.Blue);
        world.Step();
        Assert.Null(world.HeroOf(Race.Red));

        world.StartBlast(Data.Parm.Weapon("BEE_BOMB"), aim, Race.Red);
        Pass(world, 5);

        Assert.True(Red(world).Stats.DamageDealt > 0);
        Assert.Equal(0, Red(world).Stats.Weapon("BEE_BOMB").Damage);
    }

    [Fact]
    public void A_bomb_the_hero_throws_on_orders_counts_like_a_driven_one()
    {
        static (int Drawn, WiredState State) Thrown(bool avenging)
        {
            var world = OnTheBee();
            if (avenging)
            {
                Socket(world, "revenge");
                Red(world).RevengeUntil = 1000;
            }
            var target = Dummy(world, Away(world, 28));
            var start = target.HitPoints + target.Armour;

            Assert.Null(Do(world, new AttackCommand(Race.Red, [Hero(world).Id], target.Id)));
            for (var i = 0; i < 20 * 20 && target.HitPoints + target.Armour == start; i++)
                world.Step();

            return (start - target.HitPoints - target.Armour, Red(world));
        }

        var (plain, avenged) = (Thrown(avenging: false), Thrown(avenging: true));

        Assert.True(plain.Drawn > 0);
        Assert.Equal(plain.Drawn * 1.3f, avenged.Drawn, plain.Drawn * 0.05f);
        Assert.True(avenged.State.Stats.Weapon("BEE_BOMB").Damage > 0);
    }

    [Fact]
    public void A_blast_of_that_name_the_hero_did_not_set_off_gets_nothing_from_its_chips_and_is_not_on_its_weapons_account()
    {
        static (int Drawn, WiredState State) Blasted(bool avenging)
        {
            var world = OnTheBee();
            if (avenging)
            {
                Socket(world, "revenge");
                Red(world).RevengeUntil = 1000;
            }
            var aim = Away(world, 28);
            var target = Dummy(world, aim);
            var start = target.HitPoints + target.Armour;

            world.StartBlast(Data.Parm.Weapon("BEE_BOMB"), aim, Race.Red);
            Pass(world, 5);

            return (start - target.HitPoints - target.Armour, Red(world));
        }

        var (plain, avenged) = (Blasted(avenging: false), Blasted(avenging: true));

        Assert.True(plain.Drawn > 0);
        Assert.Equal(plain.Drawn, avenged.Drawn);
        Assert.Equal(plain.State.Stats.DamageDealt, avenged.State.Stats.DamageDealt);
        Assert.True(avenged.State.Stats.DamageDealt > 0);
        Assert.Equal(0, avenged.State.Stats.Weapon("BEE_BOMB").Damage);
    }
}
