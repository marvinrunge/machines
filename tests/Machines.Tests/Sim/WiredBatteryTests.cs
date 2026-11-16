using System.Globalization;
using System.Numerics;
using System.Reflection;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.Tests.Net;

namespace Machines.Tests.Sim;

/// <summary>
/// Batteries of a Wired hero: up to three copies of one weapon on a slot, by the slot's size, each bought, weighed, powered
/// and reloaded as a weapon of its own, the copies taking turns as one gun that fires n times as often.
/// </summary>
public class WiredBatteryTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    private static GameData Data => DataLoader.Value;

    private static readonly WiredSlot Left = WiredSlot.Weapon(Mounting.Left), Right = WiredSlot.Weapon(Mounting.Right), Top = WiredSlot.Weapon(Mounting.Top);

    /// <summary>A run in which no wave comes while a test lasts.</summary>
    private static GameWorld NewRun(WiredOptions? options = null)
    {
        var world = MatchSetup.Create(Data, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Player, [Race.Blue] = Controller.Ai }, 7,
            new MatchOptions { Wired = options ?? new WiredOptions() });
        world.Wired!.Countdown = 100_000;
        return world;
    }

    private static WiredState Red(GameWorld world) => world.Wired!.Heroes[Race.Red];

    private static Machine Hero(GameWorld world) => world.HeroOf(Race.Red)!;

    private static int Bank(GameWorld world) => (int)world.Races[Race.Red].Bmu;

    private static WiredChassis Chassis(string id) => WiredCatalogue.FindChassis(id)!;

    private static WiredItem Gun(int id, string weapon, int mk = 1) => new(id, WiredKind.Weapon, weapon, mk);

    private static void Near(float expected, float actual) => Assert.InRange(actual, expected - 0.001f, expected + 0.001f);

    /// <returns>Why the command was refused, or null.</returns>
    private static string? Do(GameWorld world, Command command)
    {
        world.Issue(command);
        world.Step();
        return world.Rejected.FirstOrDefault()?.Reason;
    }

    /// <returns>All that happened in that time.</returns>
    private static List<GameEvent> Run(GameWorld world, float seconds)
    {
        var events = new List<GameEvent>();
        for (var i = 0; i < MathF.Round(seconds * GameWorld.TicksPerSecond); i++)
        {
            world.Step();
            events.AddRange(world.Events);
        }
        return events;
    }

    /// <summary>A run with its hangar open and a bank that does not get in the way.</summary>
    private static GameWorld InHangar(WiredOptions? options = null, int bmu = 100000)
    {
        var world = NewRun(options);
        world.Races[Race.Red].Bmu = bmu;
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, true)));
        return world;
    }

    /// <summary>Puts the hero on any chassis, without the way there; the start BOLTER stays on its right slot unless it goes bare.</summary>
    private static void Board(GameWorld world, string chassis, bool bare = false)
    {
        var state = Red(world);
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, state.Add(WiredKind.Chassis, chassis, 0).Id)));
        foreach (var weapon in bare ? state.Loadout.Weapons.OfType<WiredItem>().ToList() : [])
            Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, weapon.Id)));
    }

    /// <summary>Gives the hero an item, as loot would, and puts it on: a weapon joins the battery on its slot, a chip goes into its socket.</summary>
    private static WiredItem Wear(GameWorld world, string id, WiredSlot slot, int mk = 1, bool add = true)
    {
        var item = Red(world).Add(WiredCatalogue.KindOf(id)!.Value, id, 0, mk);
        Assert.Null(Do(world, item.Kind == WiredKind.Chip
            ? new WiredChipCommand(Race.Red, item.Id, slot.Index)
            : new WiredEquipCommand(Race.Red, item.Id, slot, add && slot.Kind == WiredSlotKind.Weapon)));
        return item;
    }

    /// <summary>A loadout made by hand: each part <paramref name="parts"/> names comes as that many copies at its Mk, the ids from 2 on.</summary>
    private static HeroLoadout Loadout(string chassis, params (WiredSlot Slot, string Id, int Mk, int Count)[] parts)
    {
        var loadout = HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, chassis));
        var id = 2;
        foreach (var (slot, catalogue, mk, count) in parts)
        {
            for (var copy = 0; copy < count; copy++)
            {
                var item = new WiredItem(id++, WiredCatalogue.KindOf(catalogue)!.Value, catalogue, mk);
                Assert.Null(copy == 0 ? loadout.WhyNotFit(item, slot) : loadout.WhyNotAdd(item, slot));
                loadout = copy == 0 ? loadout.With(slot, item) : loadout.Adding(slot, item);
            }
        }
        return loadout;
    }

    /// <summary>Asks the world something it keeps to itself, or has it do it.</summary>
    private static object? Ask(GameWorld world, string method, params object?[] arguments) =>
        typeof(GameWorld).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(world, arguments);

    /// <summary>A place with open ground 70 m round it, well away from every building.</summary>
    private static Vector2 OpenGround(GameWorld world) => Enumerable.Range(0, 400)
        .Select(i => new Vector2(200 + i % 20 * 60, 200 + i / 20 * 60))
        .First(p => world.Constructions.All(c => Vector2.Distance(p, c.Position) > 200)
            && world.Nav.IsAreaFree((p - new Vector2(70, 70), p + new Vector2(70, 70))));

    /// <summary>
    /// A run whose hero wears the chassis and nothing but the parts, each weapon added to the battery on its slot, out on
    /// open ground away from the pod, facing east. At the wheel with no input it neither moves nor shoots.
    /// </summary>
    private static GameWorld OutOn(string chassis, bool driven, params (WiredSlot Slot, string Id, int Mk)[] parts)
    {
        var world = InHangar();
        Board(world, chassis, bare: true);
        foreach (var (slot, id, mk) in parts)
            Wear(world, id, slot, mk);
        Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
        var hero = Hero(world);
        Ask(world, "StartAfresh", hero);
        hero.Doorway = null;
        hero.Position = OpenGround(world);
        hero.Facing = 0;
        hero.DrivenByPlayer = driven;
        world.Step();
        return world;
    }

    private static void FireAtWill(GameWorld world, int weapons = -1)
    {
        var hero = Hero(world);
        world.Issue(new DriveCommand(Race.Red, hero.Id, true, Aim: hero.Position + new Vector2(40, 0), Weapons: weapons));
    }

    /// <summary>
    /// What a hero's guns did, counted in ticks from the first step watched: the tick each opened fire in, the ticks of
    /// its rounds, and the rounds that flew, each with the tick it left in.
    /// </summary>
    private sealed record Salvos(int[] Opened, List<int>[] Rounds, List<(int Tick, Projectile Round)> Flown);

    private static Salvos Watch(GameWorld world, Machine hero, int ticks)
    {
        var guns = hero.Weapons;
        Assert.All(guns, gun => Assert.True(gun.Reload <= 0));
        var opened = guns.Select(_ => -1).ToArray();
        var rounds = guns.Select(_ => new List<int>()).ToArray();
        var flown = new List<(int, Projectile)>();
        var seen = world.Projectiles.Select(p => p.Id).ToHashSet();
        for (var tick = 1; tick <= ticks; tick++)
        {
            world.Step();
            for (var gun = 0; gun < guns.Count; gun++)
            {
                if (opened[gun] < 0 && guns[gun].Reload > 0)
                    opened[gun] = tick;
            }
            foreach (var fired in world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id))
            {
                // The event names the mount that fired.
                Assert.InRange(fired.Gun, 0, guns.Count - 1);
                Assert.Equal(guns[fired.Gun].Spec.TypeName, fired.Detail);
                rounds[fired.Gun].Add(tick);
            }
            flown.AddRange(world.Projectiles.Where(p => p.Shooter == hero.Id && seen.Add(p.Id)).Select(p => (tick, p)));
        }
        return new Salvos(opened, rounds, flown);
    }

    /// <summary>
    /// Hears a hero's guns while the test goes on changing what they do, whatever state they are in: the tick of each
    /// gun's every round, counted from the first step heard.
    /// </summary>
    private sealed class Ear(GameWorld world, Machine hero)
    {
        public int Tick { get; private set; }

        public List<int>[] Rounds { get; } = [.. hero.Weapons.Select(_ => new List<int>())];

        /// <summary>Steps the world <paramref name="ticks"/> times, after each step telling <paramref name="each"/> its tick.</summary>
        public void Hear(int ticks, Action<int>? each = null)
        {
            for (var i = 0; i < ticks; i++)
            {
                world.Step();
                Tick++;
                each?.Invoke(Tick);
                foreach (var fired in world.Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == hero.Id))
                    Rounds[fired.Gun].Add(Tick);
            }
        }

        /// <summary>The ticks of all rounds of the guns <paramref name="guns"/> in order, from tick <paramref name="from"/> to before <paramref name="to"/>.</summary>
        public List<int> Between(IEnumerable<int> guns, int from, int to = int.MaxValue) =>
            [.. guns.SelectMany(gun => Rounds[gun]).Where(tick => tick >= from && tick < to).Order()];
    }

    // ---- capacity ---------------------------------------------------------------------------------

    [Fact]
    public void A_light_slot_carries_one_weapon_a_medium_two_and_a_heavy_three_whatever_their_size()
    {
        Assert.Equal(3, WiredCatalogue.MostCopies(Chassis("knight4"), Mounting.Left));
        Assert.Equal(2, WiredCatalogue.MostCopies(Chassis("knight3"), Mounting.Left));
        Assert.Equal(1, WiredCatalogue.MostCopies(Chassis("grunt1"), Mounting.Right));
        // No slot there, or the gorilla's own punch.
        Assert.Equal(0, WiredCatalogue.MostCopies(Chassis("grunt1"), Mounting.Left));
        Assert.Equal(0, WiredCatalogue.MostCopies(Chassis("gorilla5"), Mounting.Top));

        foreach (var (chassis, weapon, count, full) in new[]
        {
            ("knight4", "BOLTER", 3, "a heavy slot carries three at most"),
            ("knight4", "HEAVY_BOLTER1", 3, "a heavy slot carries three at most"),
            ("knight4", "PLASMA_CANNON2", 3, "a heavy slot carries three at most"),
            ("knight3", "BOLTER", 2, "a medium slot carries two at most"),
            ("knight3", "HEAVY_BOLTER1", 2, "a medium slot carries two at most"),
            ("reaper2", "BOLTER", 1, "a light slot carries one weapon"),
        })
        {
            var slot = chassis == "reaper2" ? Right : Left;
            var loadout = Loadout(chassis, (slot, weapon, 1, count));
            Assert.Equal(count, loadout.Battery((Mounting)slot.Index).Count);
            Assert.Equal(full, loadout.WhyNotAdd(Gun(50, weapon), slot));
        }
    }

    [Fact]
    public void The_bomb_and_the_stings_form_no_battery_and_a_battery_takes_only_its_own_weapon()
    {
        foreach (var (chassis, weapon) in new[] { ("boss2", "BEE_BOMB"), ("boss3", "WASP_LIGHT_STING"), ("boss3", "WASP_METAL_STING") })
        {
            Assert.True(WiredCatalogue.FindWeapon(weapon)!.Single);
            var loadout = Loadout(chassis, (Top, weapon, 1, 1));
            Assert.Equal("that weapon cannot form a battery", loadout.WhyNotAdd(Gun(50, weapon), Top));
            Assert.Equal("that weapon cannot form a battery", WiredCatalogue.WhyNotCopies(Chassis(chassis), Mounting.Top, WiredCatalogue.FindWeapon(weapon)!, 2));
        }

        var bolters = Loadout("knight4", (Left, "BOLTER", 1, 2));
        Assert.Equal("a battery takes only the same weapon", bolters.WhyNotAdd(Gun(50, "AUTO_CANNON"), Left));
        Assert.Equal("a battery takes only the same weapon", bolters.WhyNotAdd(Gun(50, "BOLTER", 2) with { Catalogue = "PLASMA_RIFLE" }, Left));
        // A copy of another Mk is the same weapon.
        Assert.Null(bolters.WhyNotAdd(Gun(50, "BOLTER", 3), Left));
        // What does not fit the slot at all says so, and a battery stands only on a weapon slot: onto an empty module slot
        // an add is a put, onto a filled one it is refused.
        Assert.Equal("the weapon is too big for this slot", Loadout("grunt1").WhyNotAdd(Gun(50, "PLASMA_CANNON2"), Right));
        var plates = new WiredItem(50, WiredKind.Module, "plates");
        Assert.Null(Loadout("knight4").WhyNotAdd(plates, WiredSlot.Module(0)));
        Assert.Equal(plates, Loadout("knight4").Adding(WiredSlot.Module(0), plates).Modules[0]);
        var plated = Loadout("knight4", (WiredSlot.Module(0), "plates", 1, 1));
        Assert.Equal("a battery needs a weapon slot", plated.WhyNotAdd(plates, WiredSlot.Module(0)));
    }

    [Fact]
    public void An_add_onto_an_empty_slot_is_a_plain_put()
    {
        var empty = Loadout("knight4", (Left, "BOLTER", 1, 2));
        var cannon = Gun(50, "AUTO_CANNON");

        Assert.Null(empty.WhyNotAdd(cannon, Right));
        var added = empty.Adding(Right, cannon);
        var put = empty.With(Right, cannon);

        Assert.Equal(put.Weapons, added.Weapons);
        Assert.Equal(put.Copies.Select(c => c.ToList()), added.Copies.Select(c => c.ToList()));
        Assert.Equal([cannon], added.Battery(Mounting.Right));
        Assert.Equal(0, added.CopyOf(cannon.Id));
    }

    // ---- costs ------------------------------------------------------------------------------------

    [Theory]
    [InlineData(WiredDifficulty.Normal, false, 40)]
    [InlineData(WiredDifficulty.Hard, false, 46)]
    [InlineData(WiredDifficulty.Hard, true, 41)]
    public void Each_copy_is_paid_on_its_own_at_the_hangars_price(WiredDifficulty difficulty, bool trader, int price)
    {
        var world = InHangar(new WiredOptions { Difficulty = difficulty });
        var state = Red(world);
        Board(world, "knight4");
        if (trader)
            Assert.Null(Do(world, new WiredChipCommand(Race.Red, state.Add(WiredKind.Chip, "trader", 0).Id, 0)));
        Assert.Equal(price, state.Priced(WiredCatalogue.Price("BOLTER")));
        var bank = Bank(world);

        for (var copy = 1; copy < 3; copy++)
        {
            Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
            // Trader's discount is rounded for each copy, not for the battery.
            Assert.Equal(bank - copy * price, Bank(world));
            Assert.Equal(price, state.Items[^1].Paid);
            Assert.Equal(copy, state.Loadout.CopyOf(state.Items[^1].Id));
        }
        Assert.Equal("a heavy slot carries three at most", Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
        Assert.Equal(bank - 2 * price, Bank(world));
        Assert.Equal(3, Hero(world).Weapons.Count);
    }

    [Fact]
    public void A_copy_is_upgraded_at_its_own_price_in_its_place_and_sold_for_what_went_into_it_once_it_is_off()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "knight4");
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
        var battery = state.Loadout.Battery(Mounting.Right);
        var middle = battery[1];
        var price = state.UpgradePrice(middle)!.Value;
        var bank = Bank(world);

        Assert.Null(Do(world, new WiredUpgradeCommand(Race.Red, middle.Id)));
        Assert.Equal(bank - price, Bank(world));
        Assert.Equal(battery.Select(i => i.Id), state.Loadout.Battery(Mounting.Right).Select(i => i.Id));
        Assert.Equal([1, 2, 1], state.Loadout.Battery(Mounting.Right).Select(i => i.Mk));
        Assert.Equal(40 + price, state.Item(middle.Id)!.Paid);
        var guns = Hero(world).Weapons;
        Assert.Equal([0, 1, 2], guns.Select(w => w.Copy));
        Near(guns[0].Spec.Damage * (1 + WiredBalance.DamagePerMk), guns[1].Spec.Damage);

        // A worn copy is not sold; taken off, it brings half of all that went into it.
        Assert.Equal("take it off first", Do(world, new WiredSellCommand(Race.Red, middle.Id)));
        Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, middle.Id)));
        Assert.Equal([battery[0].Id, battery[2].Id], state.Loadout.Battery(Mounting.Right).Select(i => i.Id));
        bank = Bank(world);
        Assert.Null(Do(world, new WiredSellCommand(Race.Red, middle.Id)));
        Assert.Equal(bank + (40 + price) * WiredBalance.SellPercent / 100, Bank(world));
        Assert.Equal("take it off first", Do(world, new WiredSellCommand(Race.Red, battery[2].Id)));
    }

    [Fact]
    public void A_copy_of_a_worn_weapon_is_bought_off_the_offer_at_its_full_price_and_the_offer_stays()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "knight4");
        for (var i = 0; i < state.Offer.Count; i++)
        {
            if (state.Offer[i] == "BOLTER")
                state.Offer[i] = null;
        }
        var offer = state.Offer.ToList();
        var bank = Bank(world);

        Assert.Null(world.WhyCannotBuy(state, "BOLTER", Right));
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
        Assert.Equal(bank - 40, Bank(world));
        Assert.Equal(offer, state.Offer);
        Assert.Equal(2, state.Loadout.Battery(Mounting.Right).Count);

        // A put needs the offer, even of the weapon the slot carries; so does an add onto a slot without that weapon.
        Assert.Equal("that is not on offer", world.WhyCannotBuy(state, "BOLTER"));
        Assert.Equal("that is not on offer", world.WhyCannotBuy(state, "BOLTER", Left));
        Assert.Equal("that is not on offer", Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right)));
        Assert.Equal("that is not on offer", Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Left, Add: true)));
        Assert.Equal("that is not on offer", Do(world, new WiredBuyCommand(Race.Red, "BOLTER", null, Add: true)));
        Assert.Equal(bank - 40, Bank(world));

        // Short of BMUs, nothing is paid and nothing comes.
        world.Races[Race.Red].Bmu = 39;
        var items = state.Items.Count;
        Assert.Equal("not enough BMUs", Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
        Assert.Equal((39, items, 2), (Bank(world), state.Items.Count, state.Loadout.Battery(Mounting.Right).Count));
    }

    // ---- the user's Harvester ---------------------------------------------------------------------

    [Fact]
    public void The_users_harvester_carries_six_plasma_cannons_bare_and_not_one_weapon_more()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "reaper5", bare: true);
        foreach (var slot in new[] { Left, Right })
        {
            for (var copy = 0; copy < 3; copy++)
                Wear(world, "PLASMA_CANNON2", slot);
        }
        var build = state.Build(Data);
        Assert.Equal((48, 32), (build.Load, build.LoadCapacity));
        Assert.Equal(WiredBalance.MostLoad, build.LoadShare);
        Assert.Equal(6, Hero(world).Weapons.Count);

        var bolter = state.Add(WiredKind.Weapon, "BOLTER", 0);
        Assert.Equal("too heavy for this chassis", Do(world, new WiredEquipCommand(Race.Red, bolter.Id, Top)));
        Assert.Equal(6, Hero(world).Weapons.Count);

        // A frame and a reactor, both Mk III, carry it and nearly feed it.
        Wear(world, "frame", WiredSlot.Module(0), 3);
        Wear(world, "reactor", WiredSlot.Module(1), 3);
        build = state.Build(Data);
        Assert.Equal((53, 41, 36, 37), (build.Load, build.LoadCapacity, build.PowerDraw, build.PowerSupply));

        // Glowing barrels draw their extra power for every copy.
        Assert.Null(Do(world, new WiredChipCommand(Race.Red, state.Add(WiredKind.Chip, "hotbarrels", 0).Id, 0)));
        Assert.Equal(36 + 6, state.Build(Data).PowerDraw);
    }

    [Fact]
    public void Short_of_power_every_copy_reloads_slower_and_the_single_gun_too()
    {
        var build = HeroBuild.Resolve(Data, Loadout("reaper5", (Left, "PLASMA_CANNON2", 1, 3), (Right, "PLASMA_CANNON2", 1, 2), (Top, "BOLTER", 1, 1)));

        Assert.Equal((31, 22), (build.PowerDraw, build.PowerSupply));
        Assert.Equal(6, build.Weapons.Count);
        foreach (var weapon in build.Weapons)
            Near(Data.Parm.Weapon(weapon.Spec.TypeName).ReloadTime * 31 / 22, weapon.Spec.ReloadTime);
        Assert.Contains("Not enough power: slower reloads", build.Warnings);
    }

    // ---- the build --------------------------------------------------------------------------------

    [Fact]
    public void A_build_lists_the_left_copies_then_the_right_then_the_top_and_its_damage_is_their_sum()
    {
        var loadout = Loadout("knight5", (Left, "BOLTER", 1, 3), (Right, "AUTO_CANNON", 1, 2), (Top, "HEAVY_BOLTER1", 1, 2));
        var build = HeroBuild.Resolve(Data, loadout);

        Assert.Equal(
            [(Mounting.Left, 0), (Mounting.Left, 1), (Mounting.Left, 2), (Mounting.Right, 0), (Mounting.Right, 1), (Mounting.Top, 0), (Mounting.Top, 1)],
            build.Weapons.Select(w => (w.Mounting, w.Copy)));
        Assert.Equal(Enum.GetValues<Mounting>().SelectMany(loadout.Battery).Select(i => i.Id), build.Weapons.Select(w => w.Item!.Id));
        Assert.Equal((7, 3 * 2 + 2 * 2 + 2 * 4), (build.Stats.NumberOfWeapons, build.Load));

        var singles = HeroBuild.Resolve(Data, Loadout("knight5", (Left, "BOLTER", 1, 1), (Right, "AUTO_CANNON", 1, 1), (Top, "HEAVY_BOLTER1", 1, 1)));
        Near(build.Weapons.Sum(w => w.DamagePerSecond), build.DamagePerSecond);
        Near(3 * singles.Weapons[0].DamagePerSecond + 2 * singles.Weapons[1].DamagePerSecond + 2 * singles.Weapons[2].DamagePerSecond, build.DamagePerSecond);
        Assert.Equal(singles.Range, build.Range);
    }

    [Fact]
    public void The_combo_names_the_mounting_for_every_copy_and_reads_back_copy_for_copy()
    {
        var build = HeroBuild.Resolve(Data, Loadout("knight5", (Left, "BOLTER", 1, 3), (Right, "AUTO_CANNON", 1, 2), (Top, "HEAVY_BOLTER1", 1, 2)));

        Assert.Equal("L_BOLTER_L_BOLTER_L_BOLTER_R_AUTO_CANNON_R_AUTO_CANNON_T_HEAVY_BOLTER1_T_HEAVY_BOLTER1", build.WeaponCombo);
        Assert.Equal(build.Weapons.Select(w => (w.Mounting, w.Spec.TypeName)), WeaponCombos.Split(build.WeaponCombo!));
        // A hero without a battery keeps the combo it always had.
        Assert.Equal("L_BOLTER_R_AUTO_CANNON_T_HEAVY_BOLTER1",
            HeroBuild.Resolve(Data, Loadout("knight5", (Left, "BOLTER", 1, 1), (Right, "AUTO_CANNON", 1, 1), (Top, "HEAVY_BOLTER1", 1, 1))).WeaponCombo);
    }

    /// <summary>
    /// For every chassis its heaviest fill of single weapons, one per slot, and plates in every module slot, as the build
    /// came to before batteries (76c51a0): batteries leave every hero without one as it was.
    /// </summary>
    private static readonly string[] SingleFills =
    [
        "grunt1 R_FLAME_THROWER2 load 6/6 power 2/6 dps 5 range 15 hp 40 armour 38 acc 60 speed 15 n 1 reloads 3",
        "grunt2 L_FLAME_THROWER2 load 6/8 power 2/7 dps 5 range 15 hp 82 armour 56 acc 70 speed 11 n 1 reloads 3",
        "grunt3 L_MULTI_LAUNCHER5_R_MULTI_LAUNCHER5 load 18/12 power 8/9 dps 20 range 90 hp 144 armour 90 acc 65 speed 7 n 2 reloads 3,3",
        "reaper2 L_MULTI_LAUNCHER5_R_FLAME_THROWER2 load 15/12 power 6/10 dps 15 range 90 hp 108 armour 90 acc 45 speed 12.6225 n 2 reloads 3,3",
        "reaper3 L_MULTI_LAUNCHER5_R_MULTI_LAUNCHER5 load 18/15 power 8/12 dps 20 range 90 hp 108 armour 135 acc 50 speed 13.068 n 2 reloads 3,3",
        "overseer2 L_FLAME_THROWER2_R_FLAME_THROWER2 load 15/10 power 4/14 dps 10 range 15 hp 156 armour 158 acc 60 speed 7 n 2 reloads 3,3",
        "overseer3 L_MULTI_LAUNCHER5_R_FLAME_THROWER2 load 18/12 power 6/16 dps 15 range 90 hp 234 armour 158 acc 70 speed 9.8 n 2 reloads 3,3",
        "knight3 L_MULTI_LAUNCHER5_R_MULTI_LAUNCHER5 load 21/20 power 8/16 dps 20 range 90 hp 273 armour 315 acc 50 speed 12.610001 n 2 reloads 3,3",
        "knight4 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2 load 33/26 power 14/20 dps 32 range 120 hp 312 armour 368 acc 55 speed 14.253847 n 2 reloads 5,5",
        "knight5 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2_T_MULTI_LAUNCHER5 load 42/34 power 18/26 dps 42 range 120 hp 378 armour 480 acc 60 speed 16.317648 n 3 reloads 5,5,3",
        "commander4 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER5 load 30/22 power 11/28 dps 26 range 120 hp 294 armour 180 acc 70 speed 11.727272 n 2 reloads 5,3",
        "commander5 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2 load 39/26 power 14/34 dps 32 range 120 hp 360 armour 338 acc 80 speed 11.9 n 2 reloads 5,5",
        "ballista3 T_MULTI_LAUNCHER2 load 18/24 power 7/16 dps 16 range 120 hp 108 armour 90 acc 60 speed 8 n 1 reloads 5",
        "ballista4 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2 load 33/36 power 14/22 dps 32 range 120 hp 546 armour 420 acc 60 speed 6 n 2 reloads 5,5",
        "boss1 T_FLAME_THROWER2 load 9/8 power 2/14 dps 5 range 15 hp 288 armour 270 acc 40 speed 12.95 n 1 reloads 3",
        "boss2 T_MULTI_LAUNCHER2 load 21/14 power 7/20 dps 16 range 120 hp 780 armour 1312 acc 75 speed 6.2999997 n 1 reloads 5",
        "gorilla5 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2_T_GORILLA_PUNCH load 36/44 power 14/30 dps 69.5 range 120 hp 1890 armour 1500 acc 65 speed 9 n 3 reloads 5,5,4",
        "grunt4 L_MULTI_LAUNCHER5_R_MULTI_LAUNCHER5_T_FLAME_THROWER2 load 24/20 power 10/13 dps 25 range 90 hp 429 armour 368 acc 65 speed 9.24 n 3 reloads 3,3,3",
        "grunt5 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER5_T_MULTI_LAUNCHER5 load 36/34 power 15/20 dps 36 range 120 hp 1596 armour 1200 acc 65 speed 9.164705 n 3 reloads 5,3,3",
        "reaper4 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER5 load 27/24 power 11/16 dps 26 range 120 hp 468 armour 472 acc 50 speed 12.025001 n 2 reloads 5,3",
        "reaper5 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2_T_FLAME_THROWER2 load 36/32 power 16/22 dps 37 range 120 hp 1287 armour 1050 acc 55 speed 11.1 n 3 reloads 5,5,3",
        "widow6 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER5 load 27/18 power 11/24 dps 26 range 120 hp 429 armour 368 acc 90 speed 14.7 n 2 reloads 5,3",
        "ballista5 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2_T_MULTI_LAUNCHER2 load 45/52 power 21/30 dps 48 range 120 hp 1638 armour 1365 acc 65 speed 4.5 n 3 reloads 5,5,5",
        "boss3 L_FLAME_THROWER2_R_FLAME_THROWER2_T_MULTI_LAUNCHER5 load 18/14 power 8/20 dps 20 range 90 hp 432 armour 405 acc 60 speed 16.571428 n 3 reloads 3,3,3",
        "commander6 L_MULTI_LAUNCHER2_R_MULTI_LAUNCHER2_T_MULTI_LAUNCHER5 load 48/32 power 18/44 dps 42 range 120 hp 1008 armour 975 acc 85 speed 11.2 n 3 reloads 5,5,3",
        "dozer1 T_FLAME_THROWER2 load 9/8 power 2/8 dps 5 range 15 hp 180 armour 135 acc 60 speed 8.325 n 1 reloads 3",
        "builder3 T_FLAME_THROWER2 load 9/12 power 2/10 dps 5 range 15 hp 360 armour 225 acc 60 speed 8.5 n 1 reloads 3",
        "behemoth4 T_MULTI_LAUNCHER5 load 15/18 power 4/12 dps 10 range 90 hp 780 armour 394 acc 60 speed 8 n 1 reloads 3",
        "titan6 T_MULTI_LAUNCHER2 load 24/28 power 7/18 dps 16 range 120 hp 1260 armour 600 acc 60 speed 9 n 1 reloads 5",
        "spy3 T_FLAME_THROWER2 load 12/8 power 2/12 dps 5 range 15 hp 117 armour 32 acc 60 speed 8.4 n 1 reloads 3",
        "spy5 L_FLAME_THROWER2_R_FLAME_THROWER2 load 15/10 power 4/16 dps 10 range 15 hp 140 armour 32 acc 60 speed 14 n 2 reloads 3,3",
        "phantom6 L_MULTI_LAUNCHER5_R_MULTI_LAUNCHER5 load 24/16 power 8/26 dps 20 range 90 hp 504 armour 360 acc 75 speed 14 n 2 reloads 3,3",
        // The locators, added after batteries: one light top slot each, so never a battery.
        "geo1 T_FLAME_THROWER2 load 6/6 power 2/6 dps 5 range 15 hp 40 armour 19 acc 60 speed 16 n 1 reloads 3",
        "geo3 T_FLAME_THROWER2 load 9/7 power 2/9 dps 5 range 15 hp 79 armour 22 acc 60 speed 16.571428 n 1 reloads 3",
    ];

    private static string Describe(HeroBuild build) => FormattableString.Invariant(
        $"{build.Chassis.Id} {build.WeaponCombo} load {build.Load}/{build.LoadCapacity} power {build.PowerDraw}/{build.PowerSupply} dps {build.DamagePerSecond:R} range {build.Range:R} hp {build.Stats.HitPoints} armour {build.Stats.Armour} acc {build.Stats.Accuracy} speed {build.Stats.Speed:R} n {build.Stats.NumberOfWeapons} reloads {string.Join(",", build.Weapons.Select(w => w.Spec.ReloadTime.ToString("R", CultureInfo.InvariantCulture)))}");

    [Fact]
    public void The_heaviest_single_fill_of_every_chassis_resolves_as_it_did_before_batteries()
    {
        var plates = WiredCatalogue.Modules.MaxBy(m => m.Load)!;
        var fills = new List<string>();
        foreach (var chassis in WiredCatalogue.Chassis)
        {
            var loadout = HeroLoadout.Bare(new WiredItem(1, WiredKind.Chassis, chassis.Id));
            var id = 2;
            foreach (var slot in chassis.Slots.Where(s => s.Fixed is null))
            {
                var weapon = WiredCatalogue.Weapons.Where(w => WiredCatalogue.WhyNotFit(chassis, slot.Mounting, w) is null).MaxBy(w => w.Load)!;
                loadout = loadout.With(WiredSlot.Weapon(slot.Mounting), Gun(id++, weapon.Id));
            }
            for (var m = 0; m < chassis.Modules; m++)
                loadout = loadout.With(WiredSlot.Module(m), new WiredItem(id++, WiredKind.Module, plates.Id));
            var build = HeroBuild.Resolve(Data, loadout);
            Assert.All(build.Weapons, w => Assert.Equal(0, w.Copy));
            fills.Add(Describe(build));
        }

        Assert.Equal(SingleFills, fills);
    }

    [Fact]
    public void The_aim_lost_per_extra_barrel_is_nothing_for_now_and_a_knob_for_later()
    {
        Assert.Equal(0, WiredBalance.BatteryAimLoss);
        Assert.Equal(70, HeroBuild.BatteryAccuracy(70, 0, 0));
        Assert.Equal(70, HeroBuild.BatteryAccuracy(70, 6, 0));
        Assert.Equal(100, HeroBuild.BatteryAccuracy(100, 6, 0));
        Assert.Equal(60, HeroBuild.BatteryAccuracy(70, 2, 5));
        Assert.Equal(0, HeroBuild.BatteryAccuracy(7, 2, 5));

        var single = HeroBuild.Resolve(Data, Loadout("knight4", (Left, "BOLTER", 1, 1), (Right, "BOLTER", 1, 1)));
        var battery = HeroBuild.Resolve(Data, Loadout("knight4", (Left, "BOLTER", 1, 3), (Right, "BOLTER", 1, 3)));
        Assert.Equal(single.Stats.Accuracy, battery.Stats.Accuracy);
        // Past 100 the accuracy is cut first, battery or not.
        (WiredSlot, string, int, int)[] aimed = [(WiredSlot.Module(0), "targeting", 3, 1), (WiredSlot.Module(1), "targeting", 3, 1), (WiredSlot.Module(2), "targeting", 3, 1)];
        Assert.Equal(100, HeroBuild.Resolve(Data, Loadout("commander6", [(Left, "BOLTER", 1, 3), .. aimed])).Stats.Accuracy);
    }

    // ---- the loadout ------------------------------------------------------------------------------

    [Fact]
    public void Taking_off_the_first_copy_makes_the_second_the_first_and_a_later_one_leaves_the_others_closed_up()
    {
        var loadout = Loadout("knight4", (Left, "BOLTER", 1, 3));
        Assert.Equal([2, 3, 4], loadout.Battery(Mounting.Left).Select(i => i.Id));
        Assert.Equal(new int?[] { 0, 1, 2, null }, new[] { 2, 3, 4, 5 }.Select(loadout.CopyOf));
        Assert.Equal(Left, loadout.SlotOf(4));
        Assert.True(loadout.Holds(4));
        Assert.Equal([1, 2, 3, 4], loadout.Worn.Select(i => i.Id));

        var first = loadout.Without(2);
        Assert.Equal([3, 4], first.Battery(Mounting.Left).Select(i => i.Id));
        Assert.Equal(3, first.Weapon(Mounting.Left)!.Id);
        Assert.Null(first.CopyOf(2));

        Assert.Equal([2, 4], loadout.Without(3).Battery(Mounting.Left).Select(i => i.Id));
        Assert.Equal([2, 3], loadout.Without(4).Battery(Mounting.Left).Select(i => i.Id));
        var none = loadout.Without(3).Without(2).Without(4);
        Assert.Empty(none.Battery(Mounting.Left));
        Assert.All(none.Copies, copies => Assert.Empty(copies));
    }

    [Fact]
    public void A_put_without_add_takes_the_whole_battery_into_the_inventory_even_for_the_same_weapon()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "knight4");
        var battery = new List<WiredItem> { state.Loadout.Weapon(Mounting.Right)! };
        battery.Add(Wear(world, "BOLTER", Right));
        battery.Add(Wear(world, "BOLTER", Right));
        Assert.Equal(battery, state.Loadout.Battery(Mounting.Right));

        var bolter = Wear(world, "BOLTER", Right, add: false);
        Assert.Equal([bolter], state.Loadout.Battery(Mounting.Right));
        Assert.All(battery, copy => Assert.Contains(copy, state.Inventory));
        Assert.Single(Hero(world).Weapons);

        Assert.Null(Do(world, new WiredEquipCommand(Race.Red, battery[0].Id, Right, Add: true)));
        var cannon = Wear(world, "AUTO_CANNON", Right, add: false);
        Assert.Equal([cannon], state.Loadout.Battery(Mounting.Right));
        Assert.Contains(bolter, state.Inventory);
        Assert.Contains(battery[0], state.Inventory);
    }

    [Fact]
    public void A_battery_moves_whole_to_another_chassis_and_is_cut_to_what_its_new_slot_carries()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "ballista5", bare: true);
        var battery = Enumerable.Range(0, 3).Select(_ => Wear(world, "HEAVY_BOLTER1", Top)).ToList();

        // The knight has no top: the battery takes the first slot that takes it, whole.
        Board(world, "knight4");
        Assert.Equal(battery, state.Loadout.Battery(Mounting.Left));
        Assert.Equal([0, 1, 2], Hero(world).Weapons.Select(w => w.Copy));

        // A medium slot carries two: the last copy comes off into the inventory, unless there is no room for it.
        var reaper = state.Add(WiredKind.Chassis, "reaper3", 0);
        while (state.Inventory.Count() < WiredBalance.InventoryLimit)
            state.Add(WiredKind.Module, "plates", 0);
        Assert.Equal("no room in the inventory", Do(world, new WiredChassisCommand(Race.Red, reaper.Id)));
        Assert.Equal(battery, state.Loadout.Battery(Mounting.Left));

        state.Items.Remove(state.Items.Last(i => i.Catalogue == "plates"));
        Assert.Null(Do(world, new WiredChassisCommand(Race.Red, reaper.Id)));
        Assert.Equal(battery.Take(2), state.Loadout.Battery(Mounting.Left));
        Assert.Contains(battery[2], state.Inventory);
        Assert.Equal(2, Hero(world).Weapons.Count);
    }

    // ---- the rhythm -------------------------------------------------------------------------------

    /// <summary>The ticks a copy's first round waits behind copy 0's when the <paramref name="chain"/>-th of <paramref name="size"/> opens fire with it.</summary>
    private static int TurnOf(int chain, int size, float reload) =>
        (int)MathF.Round(GameWorld.BatteryOffset(chain, size, reload, GameWorld.Dt) / GameWorld.Dt);

    /// <summary>The ticks of the rounds of the first <paramref name="guns"/> guns watched, all together and in order: a battery's rounds from its one gun.</summary>
    private static List<int> RoundsOf(Salvos salvos, int guns) => [.. salvos.Rounds.Take(guns).SelectMany(r => r).Order()];

    /// <summary>Asserts that every round of <paramref name="rounds"/> follows the one before by a share of a reload, give or take a tick.</summary>
    private static void EverySharedTurn(List<int> rounds, float reload, int size)
    {
        var turn = reload * GameWorld.TicksPerSecond / size;
        Assert.All(rounds.Zip(rounds.Skip(1), (before, after) => after - before), gap => Assert.InRange(gap, turn - 1, turn + 1));
    }

    /// <summary>A Blue grunt that does not fall, 30 m in front of the hero, which it is told to attack.</summary>
    private static void AttackAStandIn(GameWorld world, Machine hero)
    {
        var target = world.AddMachine(Race.Blue, hero.Position + new Vector2(30, 0), "AGGRESSOR", "GRUNT", 1, null);
        target.HitPoints = 1_000_000;
        world.Issue(new AttackCommand(Race.Red, [hero.Id], target.Id));
    }

    [Theory]
    [InlineData(2, true)]
    [InlineData(3, true)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    public void A_battery_of_n_fires_its_copies_a_nth_of_a_reload_apart_and_keeps_that_rhythm_while_it_fires(int n, bool driven)
    {
        var world = OutOn("knight4", driven, [.. Enumerable.Repeat((Left, "PLASMA_CANNON2", 1), n), (Right, "PLASMA_CANNON2", 1), (WiredSlot.Module(0), "reactor", 3)]);
        var hero = Hero(world);
        Assert.Equal([.. Enumerable.Range(0, n).Select(copy => (Mounting.Left, copy)), (Mounting.Right, 0)], hero.Weapons.Select(w => (w.Mounting, w.Copy)));
        // Fed in full, each copy reloads as the plasma cannon does: two seconds, which Fire counts down in 41 ticks; a
        // battery of two takes turns after 20 ticks, one of three after 13 and 27.
        var reload = hero.Weapons[0].Spec.ReloadTime;
        Assert.Equal((2f, 41), (reload, GameWorld.ReloadTicks(reload, GameWorld.Dt)));
        int[] turns = n == 2 ? [0, 20] : [0, 13, 27];
        Assert.Equal(turns, Enumerable.Range(0, n).Select(copy => TurnOf(copy, n, reload)));
        if (driven)
            FireAtWill(world);
        else
            AttackAStandIn(world, hero);

        var salvo = Watch(world, hero, 6 * 2 * GameWorld.TicksPerSecond);

        var opened = salvo.Opened[0];
        Assert.True(opened > 0, "the battery never opened fire");
        // All copies open fire in one tick, copy 0's round leaves at once and every other copy's at its turn.
        Assert.Equal(Enumerable.Repeat(opened, n), salvo.Opened.Take(n));
        Assert.Equal(turns.Select(turn => opened + 1 + turn), salvo.Rounds.Take(n).Select(r => r[0]));
        // The single gun beside it starts at once.
        Assert.Equal(salvo.Opened[n] + 1, salvo.Rounds[n][0]);
        // From then on the one gun fires every n-th of a reload, for five reloads and more.
        var rounds = RoundsOf(salvo, n);
        Assert.True(rounds.Count >= 5 * n, $"the battery fired {rounds.Count} rounds: {string.Join(",", rounds)}");
        EverySharedTurn(rounds, reload, n);
        // Each round flies from the copy that fired it.
        for (var copy = 0; copy < n; copy++)
        {
            var round = Assert.Single(salvo.Flown, f => f.Tick == opened + 1 + turns[copy] && f.Round.Mounting == Mounting.Left).Round;
            Assert.Equal(copy, round.Copy);
        }
    }

    [Fact]
    public void Copy_0_and_a_single_gun_fire_on_the_ticks_they_fire_on_without_a_battery()
    {
        (WiredSlot, string, int) reactor = (WiredSlot.Module(0), "reactor", 3);
        var battery = OutOn("knight4", true, [.. Enumerable.Repeat((Left, "PLASMA_CANNON2", 1), 3), (Right, "PLASMA_CANNON2", 1), reactor]);
        var single = OutOn("knight4", true, (Left, "PLASMA_CANNON2", 1), (Right, "PLASMA_CANNON2", 1), reactor);
        FireAtWill(battery);
        FireAtWill(single);

        var ticks = 10 * GameWorld.TicksPerSecond;
        var with = Watch(battery, Hero(battery), ticks);
        var without = Watch(single, Hero(single), ticks);

        Assert.Equal((without.Opened[0], without.Opened[1]), (with.Opened[0], with.Opened[3]));
        Assert.Equal(without.Rounds[0], with.Rounds[0]);
        Assert.Equal(without.Rounds[1], with.Rounds[3]);
        // As ever: the first round in the tick after the gun opened fire, and one every reload of 41 ticks, the 40 of its
        // two seconds and the tick to open fire again.
        Assert.Equal(Enumerable.Range(0, 5).Select(cycle => without.Opened[0] + 1 + cycle * 41), without.Rounds[0]);
    }

    [Fact]
    public void After_a_pause_longer_than_a_reload_the_battery_takes_its_turns_anew_from_copy_0()
    {
        var world = OutOn("knight4", true, [.. Enumerable.Repeat((Left, "PLASMA_CANNON2", 1), 3)]);
        var hero = Hero(world);
        var reload = hero.Weapons[0].Spec.ReloadTime;
        FireAtWill(world);
        var before = Watch(world, hero, 5 * GameWorld.TicksPerSecond + 7);
        Assert.Equal([1, 14, 28], before.Rounds.Select(r => r[0] - before.Opened[0]));

        // Let go: every copy reloads, the last one its turn later than the first.
        FireAtWill(world, weapons: 0);
        Run(world, reload + 1.5f);
        Assert.All(hero.Weapons, w => Assert.True(w.Reload <= 0 && w.RoundsLeft == 0));

        FireAtWill(world);
        var again = Watch(world, hero, 3 * GameWorld.TicksPerSecond);

        var opened = again.Opened[0];
        Assert.Equal([opened, opened, opened], again.Opened);
        Assert.Equal([opened + 1, opened + 14, opened + 28], again.Rounds.Select(r => r[0]));
        EverySharedTurn(RoundsOf(again, 3), reload, 3);
    }

    [Fact]
    public void Let_go_for_less_than_a_reload_the_battery_fires_on_in_its_turns()
    {
        var world = OutOn("knight4", true, [.. Enumerable.Repeat((Left, "PLASMA_CANNON2", 1), 3)]);
        var hero = Hero(world);
        var reload = hero.Weapons[0].Spec.ReloadTime;
        var ear = new Ear(world, hero);
        FireAtWill(world);
        ear.Hear(30);
        var first = ear.Rounds.Select(r => Assert.Single(r)).ToList();
        Assert.Equal([13, 27], first.Skip(1).Select(tick => tick - first[0]));

        // Let go, and take hold again while copies 0 and 1 are loaded and copy 2 is not.
        FireAtWill(world, weapons: 0);
        ear.Hear(30);
        Assert.Equal([true, true, false], hero.Weapons.Select(w => w.Reload <= 0));
        FireAtWill(world);
        var resumed = ear.Tick + 1;
        ear.Hear(8 * GameWorld.TicksPerSecond);

        // Copy 2, loaded later, waits for its turn rather than fire between the other two: one round every third of a reload.
        var rounds = ear.Between([0, 1, 2], resumed);
        Assert.Equal(resumed + 1, rounds[0]);
        Assert.True(rounds.Count >= 3 * 3, $"the battery fired {rounds.Count} rounds: {string.Join(",", rounds)}");
        EverySharedTurn(rounds, reload, 3);
    }

    [Fact]
    public void A_turn_is_nothing_for_copy_0_whole_ticks_for_the_others_and_never_a_whole_reload()
    {
        const float dt = GameWorld.Dt;
        // Fire counts a reload down in whole ticks, and in one more where the float sum leaves a crumb: 2 s in 41.
        Assert.Equal((41, 23, 20, 0), (GameWorld.ReloadTicks(2, dt), GameWorld.ReloadTicks(1.125f, dt), GameWorld.ReloadTicks(0.99f, dt), GameWorld.ReloadTicks(0, dt)));
        Assert.Equal((13, 27, 20), (TurnOf(1, 3, 2), TurnOf(2, 3, 2), TurnOf(1, 2, 2)));
        // The anchored Colossus' heavy bolter, 1.125 s or 23 ticks: 7⅔ and 15⅓ ticks down to whole ones.
        Assert.Equal((7, 15), (TurnOf(1, 3, 1.125f), TurnOf(2, 3, 1.125f)));
        var reloads = Enumerable.Range(1, 1200).Select(hundredths => hundredths / 100f)
            .Concat(WiredCatalogue.Weapons.Select(w => Data.Parm.Weapon(w.Id).ReloadTime)).Concat([1.125f, 1.68f, 1.84f]);
        foreach (var reload in reloads)
        {
            for (var size = 1; size <= 3; size++)
            {
                Assert.Equal(0, GameWorld.BatteryOffset(0, size, reload, dt));
                // Even a chain longer than the battery, which Fire never makes.
                for (var chain = 1; chain <= size + 1; chain++)
                {
                    var offset = GameWorld.BatteryOffset(chain, size, reload, dt);
                    Assert.True(offset >= 0 && offset < reload, $"{chain} of {size} at {reload} s waits {offset} s");
                    Assert.Equal(MathF.Round(offset / dt), offset / dt, 0.001f);
                    // Within a tick of its share of the reload.
                    var share = Math.Min(chain, size - 1) * reload / size;
                    Assert.True(Math.Abs(offset - share) < dt + 0.0001f, $"{chain} of {size} at {reload} s waits {offset} s, not about {share} s");
                }
            }
        }
        // A battery of one, and a weapon without a reload, wait for nothing.
        Assert.Equal(0, GameWorld.BatteryOffset(1, 1, 2, dt));
        Assert.Equal(0, GameWorld.BatteryOffset(1, 3, 0, dt));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void Over_a_long_fight_a_battery_of_n_fires_n_times_the_rounds_per_minute_of_its_single_weapon(int n)
    {
        const int minutes = 5;
        static List<int>[] Fought(int copies)
        {
            var world = OutOn("knight4", true, [.. Enumerable.Repeat((Left, "PLASMA_CANNON2", 1), copies)]);
            FireAtWill(world);
            return Watch(world, Hero(world), minutes * 60 * GameWorld.TicksPerSecond).Rounds;
        }
        // Rounds per minute from the first round to the last.
        static float PerMinute(List<int> rounds) => (rounds.Count - 1) * 60f * GameWorld.TicksPerSecond / (rounds[^1] - rounds[0]);

        var single = Fought(1)[0];
        var battery = Fought(n);

        Assert.InRange(PerMinute(single), 29, 30);
        var rounds = battery.SelectMany(r => r).Order().ToList();
        Assert.InRange(PerMinute(rounds), n * PerMinute(single) - 1, n * PerMinute(single) + 1);
        // Every copy fires as often as the single gun, the later copies a round fewer where their turn came too late.
        Assert.All(battery, copy => Assert.InRange(copy.Count, single.Count - 1, single.Count));
        Assert.Equal(single.Count, battery[0].Count);
    }

    /// <returns>The rounds each gun fires in <paramref name="seconds"/>, counted from its own first one on, firing at will at the wheel.</returns>
    private static int[] RoundsIn(GameWorld world, float seconds)
    {
        FireAtWill(world);
        var ticks = (int)MathF.Round(seconds * GameWorld.TicksPerSecond);
        // Long enough for the last copy's own count too, whose first round waits most of a reload.
        var salvo = Watch(world, Hero(world), ticks + 3 * GameWorld.TicksPerSecond);
        return [.. salvo.Rounds.Select(r => r.Count(tick => tick < r[0] + ticks))];
    }

    [Fact]
    public void Over_twenty_seconds_each_copy_fires_as_many_rounds_as_a_single_weapon()
    {
        // Reload-bound: a plasma cannon's one round every two seconds, three of them with the power they need.
        var three = OutOn("knight4", true, [.. Enumerable.Repeat((Left, "PLASMA_CANNON2", 1), 3)]);
        Assert.Equal((18, 20), (Red(three).Build(Data).PowerDraw, Red(three).Build(Data).PowerSupply));
        var one = Assert.Single(RoundsIn(OutOn("knight4", true, (Left, "PLASMA_CANNON2", 1)), 20));
        Assert.Equal([one, one, one], RoundsIn(three, 20));

        // Burst-bound: with coolers an auto cannon is reloaded before its burst of six is out.
        (WiredSlot, string, int)[] coolers = [(WiredSlot.Module(0), "cooler", 3), (WiredSlot.Module(1), "cooler", 3), (WiredSlot.Module(2), "cooler", 3)];
        var two = OutOn("knight3", true, [(Left, "AUTO_CANNON", 1), (Left, "AUTO_CANNON", 1), .. coolers]);
        var gun = Hero(two).Weapons[1].Spec;
        Assert.True(gun.ReloadTime < gun.BurstDuration, $"reload {gun.ReloadTime}, burst {gun.BurstDuration}");
        var single = Assert.Single(RoundsIn(OutOn("knight3", true, [(Left, "AUTO_CANNON", 1), .. coolers]), 20));
        Assert.Equal([single, single], RoundsIn(two, 20));
    }

    [Theory]
    [InlineData(1, 2, 3)]
    [InlineData(3, 1, 1)]
    public void Copies_of_different_mk_drift_apart_each_at_its_own_pace_and_none_stalls(int first, int second, int third)
    {
        var world = OutOn("knight4", true, (Left, "PLASMA_CANNON2", first), (Left, "PLASMA_CANNON2", second), (Left, "PLASMA_CANNON2", third));
        var hero = Hero(world);
        FireAtWill(world);

        const int ticks = 60 * GameWorld.TicksPerSecond;
        var salvo = Watch(world, hero, ticks);

        var counts = salvo.Rounds.Select(r => r.Count).ToArray();
        int[] mks = [first, second, third];
        // A higher Mk reloads sooner and fires more.
        for (var copy = 1; copy < 3; copy++)
        {
            if (mks[copy] != mks[copy - 1])
                Assert.True(counts[copy] > counts[copy - 1] == mks[copy] > mks[copy - 1], $"Mk {string.Join(",", mks)} fired {string.Join(", ", counts)}");
        }
        var opened = salvo.Opened[0];
        for (var copy = 0; copy < 3; copy++)
        {
            var reload = hero.Weapons[copy].Spec.ReloadTime;
            var cycle = GameWorld.ReloadTicks(reload, GameWorld.Dt);
            var rounds = salvo.Rounds[copy];
            // Each copy's turn is its share of its own reload; then it fires every reload of its own, the tick to open
            // fire again included, never set back a turn when it meets another copy as they drift past each other.
            Assert.Equal(opened + 1 + TurnOf(copy, 3, reload), rounds[0]);
            Assert.All(rounds.Zip(rounds.Skip(1), (before, after) => after - before), gap => Assert.Equal(cycle, gap));
            Assert.True(rounds[^1] > ticks - cycle, $"copy {copy} stopped at tick {rounds[^1]}");
        }
    }

    [Fact]
    public void Copies_of_different_mk_keep_each_its_own_pace_while_overload_cuts_their_reloads_short()
    {
        // Overload takes its share off every reload after the tick's fighting, so a reload it ends looks loaded at the
        // next; a copy that has only just reloaded so must still not count as waiting for the trigger.
        var world = OutOn("knight4", true, (Left, "PLASMA_CANNON2", 1), (Left, "PLASMA_CANNON2", 2), (Left, "PLASMA_CANNON2", 3), (WiredSlot.Chip(0), "overload", 1));
        var hero = Hero(world);
        hero.HitPoints = 1;
        FireAtWill(world);

        const int ticks = 60 * GameWorld.TicksPerSecond;
        var salvo = Watch(world, hero, ticks);

        Assert.Contains("overload", WiredChips.Lit(world, Red(world)));
        var counts = salvo.Rounds.Select(r => r.Count).ToArray();
        Assert.True(counts[0] < counts[1] && counts[1] < counts[2], $"fired {string.Join(", ", counts)}");
        for (var copy = 0; copy < 3; copy++)
        {
            var rounds = salvo.Rounds[copy];
            // After the first, which holds the copy's turn, every reload of a copy is as long as the next: never set
            // back a turn when it meets another copy.
            var cycles = rounds.Zip(rounds.Skip(1), (before, after) => after - before).Skip(1).Distinct().ToList();
            var cycle = Assert.Single(cycles);
            Assert.True(cycle < GameWorld.ReloadTicks(hero.Weapons[copy].Spec.ReloadTime, GameWorld.Dt), $"copy {copy} reloads in {cycle} ticks");
            Assert.True(rounds[^1] > ticks - cycle, $"copy {copy} stopped at tick {rounds[^1]}");
        }
    }

    // ---- the specials -----------------------------------------------------------------------------

    [Fact]
    public void A_widows_lurk_tunes_the_first_round_of_every_copy_triggered_with_copy_0_even_the_last_turn_and_the_next_rounds_fly_as_made()
    {
        var world = OutOn("widow6", true, [.. Enumerable.Repeat((Left, "PLASMA_CANNON2", 1), 3)]);
        var hero = Hero(world);
        FireAtWill(world, weapons: 0);
        Run(world, WiredBalance.LurkStill + 0.1f);
        Assert.All(hero.Weapons, w => Assert.Equal(w.Base.Damage * WiredBalance.LurkDamage, w.Spec.Damage));

        FireAtWill(world);
        var salvo = Watch(world, hero, 5 * GameWorld.TicksPerSecond);

        // Copy 0's round spends the lurk; the copies triggered with it fire theirs a third and two thirds of a reload
        // later, still tuned.
        var opened = salvo.Opened[0];
        Assert.Equal([opened + 1, opened + 14, opened + 28], salvo.Rounds.Select(r => r[0]));
        for (var copy = 0; copy < 3; copy++)
        {
            var made = hero.Weapons[copy].Base;
            var rounds = salvo.Flown.Where(f => f.Round.Copy == copy).ToList();
            Assert.True(rounds.Count >= 2, $"copy {copy} fired {rounds.Count} rounds");
            Assert.Equal(salvo.Rounds[copy][0], rounds[0].Tick);
            Near(made.Damage * WiredBalance.LurkDamage, rounds[0].Round.Weapon.Damage);
            Near(made.Range * WiredBalance.LurkRange, rounds[0].Round.Weapon.Range);
            Assert.All(rounds.Skip(1), round => Assert.Equal(made.Damage, round.Round.Weapon.Damage));
        }
        Assert.DoesNotContain(hero.Weapons, w => !ReferenceEquals(w.Spec, w.Base));
    }

    [Fact]
    public void An_anchored_colossus_tunes_every_copy_and_its_battery_takes_turns_by_the_shortened_reload()
    {
        var world = OutOn("ballista5", true, [(Left, "BOLTER", 1), .. Enumerable.Repeat((Top, "HEAVY_BOLTER1", 1), 3)]);
        var hero = Hero(world);
        Assert.Equal([0, 0, 1, 2], hero.Weapons.Select(w => w.Copy));
        var free = hero.Weapons[1].Spec.ReloadTime;

        Assert.Null(Do(world, new WiredSpecialCommand(Race.Red)));
        Run(world, WiredBalance.AnchorTime + 0.1f);

        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(hero));
        Assert.All(hero.Weapons, w =>
        {
            Assert.Equal(w.Base.Range * WiredBalance.AnchorRange, w.Spec.Range);
            Assert.Equal(w.Base.ReloadTime * WiredBalance.AnchorReload, w.Spec.ReloadTime);
        });
        var anchored = hero.Weapons[1].Spec.ReloadTime;
        int[] turns = [.. Enumerable.Range(0, 3).Select(copy => TurnOf(copy, 3, anchored))];
        Assert.True(turns[1] < TurnOf(1, 3, free) && turns[2] < TurnOf(2, 3, free), $"anchored {string.Join(",", turns)}, free {TurnOf(1, 3, free)},{TurnOf(2, 3, free)}");

        FireAtWill(world);
        var salvo = Watch(world, hero, 4 * GameWorld.TicksPerSecond);

        var opened = salvo.Opened[1];
        Assert.Equal([opened, opened, opened], salvo.Opened.Skip(1));
        Assert.Equal(turns.Select(turn => opened + 1 + turn), salvo.Rounds.Skip(1).Select(r => r[0]));
        var rounds = RoundsOf(salvo with { Rounds = salvo.Rounds[1..] }, 3);
        Assert.True(rounds.Count >= 3 * 3, $"the battery fired {rounds.Count} rounds");
        EverySharedTurn(rounds, anchored, 3);
    }

    [Fact]
    public void A_colossus_anchored_and_released_while_its_battery_fires_takes_its_turns_by_the_new_reload_within_a_reload()
    {
        var world = OutOn("ballista5", true, [.. Enumerable.Repeat((Top, "HEAVY_BOLTER1", 1), 3)]);
        var hero = Hero(world);
        Assert.Equal([0, 1, 2], hero.Weapons.Select(w => w.Copy));
        var free = hero.Weapons[0].Spec.ReloadTime;
        var anchored = free * WiredBalance.AnchorReload;
        // The longer of the two reloads in ticks, within which the battery has found its new turns.
        var settle = GameWorld.ReloadTicks(free, GameWorld.Dt);
        var ear = new Ear(world, hero);
        FireAtWill(world);
        ear.Hear(3 * GameWorld.TicksPerSecond);
        EverySharedTurn(ear.Between([0, 1, 2], 0), free, 3);

        // Anchored while it fires: its reloads shorten in the tick the tracks lock, wherever the battery is in its turns.
        world.Issue(new WiredSpecialCommand(Race.Red));
        var locked = -1;
        ear.Hear((int)((WiredBalance.AnchorTime + 7) * GameWorld.TicksPerSecond), tick =>
        {
            if (locked < 0 && hero.Weapons[0].Spec.ReloadTime == anchored)
                locked = tick;
        });
        Assert.Equal(AnchorPhase.Anchored, world.AnchorPhaseOf(hero));
        Assert.All(hero.Weapons, w => Assert.Equal(anchored, w.Spec.ReloadTime));
        var tight = ear.Between([0, 1, 2], locked + settle);
        Assert.True(tight.Count >= 4 * 3, $"anchored at tick {locked}, the battery fired {string.Join(",", ear.Between([0, 1, 2], locked))}");
        EverySharedTurn(tight, anchored, 3);

        // Released while it fires: the reloads lengthen again at once.
        world.Issue(new WiredSpecialCommand(Race.Red, false));
        var released = ear.Tick + 1;
        ear.Hear(9 * GameWorld.TicksPerSecond);
        Assert.All(hero.Weapons, w => Assert.Equal(free, w.Spec.ReloadTime));
        var loose = ear.Between([0, 1, 2], released + settle);
        Assert.True(loose.Count >= 4 * 3, $"released at tick {released}, the battery fired {string.Join(",", ear.Between([0, 1, 2], released))}");
        EverySharedTurn(loose, free, 3);
    }

    // ---- saved data and commands ------------------------------------------------------------------

    [Fact]
    public void What_was_saved_or_sent_before_batteries_loads_as_one_copy_and_a_put()
    {
        Assert.Equal(new WiredStartItem("BOLTER", Left), WiredReplayTests.Old(new WiredStartItem("BOLTER", Left, 3), "count"));
        Assert.Equal(new BattleItem("BOLTER", Left, 2), WiredReplayTests.Old(new BattleItem("BOLTER", Left, 2, 3), "count"));
        Assert.Equal(new WiredBuyCommand(Race.Red, "BOLTER", Left), WiredReplayTests.Old<Command>(new WiredBuyCommand(Race.Red, "BOLTER", Left, Add: true), "add"));
        var equip = WiredReplayTests.Old<Command>(new WiredEquipCommand(Race.Red, 4, Right, Add: true), "add");
        Assert.Equal(new WiredEquipCommand(Race.Red, 4, Right), equip);

        // Such a put replaces the battery as it replaced a weapon before.
        var world = InHangar();
        var state = Red(world);
        Board(world, "knight4");
        Wear(world, "BOLTER", Right);
        var spare = state.Add(WiredKind.Weapon, "BOLTER", 0);
        Assert.Null(Do(world, (WiredEquipCommand)equip! with { Item = spare.Id }));
        Assert.Equal([spare], state.Loadout.Battery(Mounting.Right));
    }

    [Fact]
    public void Creation_buys_each_copy_and_cuts_a_count_to_what_the_slot_carries()
    {
        var knight = new WiredOptions { Chassis = "knight4", Items = [new WiredStartItem("BOLTER", Left, 3)] };
        var state = WiredState.Create(Data, Race.Red, knight, out var bank);
        // The free BOLTER is the first copy; the others are paid for.
        Assert.Equal([0, 40, 40], state.Loadout.Battery(Mounting.Left).Select(i => i.Paid));
        Assert.Equal(150 - 2 * 40, bank);

        state = WiredState.Create(Data, Race.Red, knight with { Items = [new WiredStartItem("BOLTER", Left, 9)] }, out bank);
        Assert.Equal((3, 70), (state.Loadout.Battery(Mounting.Left).Count, bank));
        state = WiredState.Create(Data, Race.Red, new WiredOptions { Chassis = "grunt3", Items = [new WiredStartItem("BOLTER", Left, 9)] }, out bank);
        Assert.Equal((2, 110), (state.Loadout.Battery(Mounting.Left).Count, bank));
        state = WiredState.Create(Data, Race.Red, knight with { Items = [new WiredStartItem("BOLTER", Left, 0)] }, out bank);
        Assert.Equal((1, 150), (state.Loadout.Battery(Mounting.Left).Count, bank));

        // A weapon bought besides the free one, each copy as the capital covers it; a copy it does not cover is passed over.
        state = WiredState.Create(Data, Race.Red, knight with { Items = [new WiredStartItem("ELECTRIC_CHARGE", Right, 2), new WiredStartItem("BOLTER", Left)] }, out bank);
        Assert.Equal((2, 1, 150 - 2 * 60), (state.Loadout.Battery(Mounting.Right).Count, state.Loadout.Battery(Mounting.Left).Count, bank));
        state = WiredState.Create(Data, Race.Red, knight with { Items = [new WiredStartItem("AUTO_CANNON", Right, 2), new WiredStartItem("BOLTER", Left)] }, out bank);
        Assert.Equal((1, 1, 150 - 100), (state.Loadout.Battery(Mounting.Right).Count, state.Loadout.Battery(Mounting.Left).Count, bank));
        Assert.Single(state.Items, i => i.Catalogue == "AUTO_CANNON");

        // A module comes once, whatever its count, and so does a weapon that forms no battery.
        state = WiredState.Create(Data, Race.Red, knight with { Items = [new WiredStartItem("plates", WiredSlot.Module(0), 3)] }, out bank);
        Assert.Single(state.Items, i => i.Catalogue == "plates");
        Assert.Equal(150 - 90, bank);
        Assert.Equal(1, WiredCatalogue.Copies(Chassis("knight4"), WiredSlot.Module(0), "plates", 3));
        Assert.Equal(1, WiredCatalogue.Copies(Chassis("boss2"), Top, "BEE_BOMB", 3));
        Assert.Equal(1, WiredCatalogue.Copies(Chassis("boss3"), Top, "WASP_METAL_STING", 2));
        Assert.Equal(1, WiredCatalogue.Copies(Chassis("knight4"), null, "BOLTER", 3));
        Assert.Equal(1, WiredCatalogue.Copies(Chassis("knight4"), Left, "BOLTER", -4));
        Assert.Equal(3, WiredCatalogue.Copies(Chassis("knight4"), Left, "BOLTER", int.MaxValue));
    }

    [Fact]
    public void A_battle_hero_wears_its_battery_at_the_entrys_mk()
    {
        var hero = new BattleHero(Race.Red, "Ace", "reaper3",
            [new BattleItem("HEAVY_BOLTER1", Left, 2, 2), new BattleItem("BOLTER", Right, 1, 5), new BattleItem("plates", WiredSlot.Module(0), 1, 3)]);

        var state = WiredState.ForBattle(Data, Race.Red, hero, 1);

        Assert.Equal([("HEAVY_BOLTER1", 2), ("HEAVY_BOLTER1", 2)], state.Loadout.Battery(Mounting.Left).Select(i => (i.Catalogue, i.Mk)));
        // A count the check would refuse still makes a hero, cut to what the slot carries.
        Assert.Equal(2, state.Loadout.Battery(Mounting.Right).Count);
        Assert.Single(state.Items, i => i.Catalogue == "plates");
        Assert.All(state.Items, i => Assert.Equal(0, i.Paid));
        Assert.Equal(4, state.Build(Data).Weapons.Count);
    }

    // ---- the checksum -----------------------------------------------------------------------------

    [Fact]
    public void Two_worlds_fed_the_same_battery_commands_keep_the_same_checksums()
    {
        GameWorld Played()
        {
            var world = InHangar();
            Board(world, "knight4");
            Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
            Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
            Assert.Null(Do(world, new WiredUnequipCommand(Race.Red, Red(world).Loadout.Weapon(Mounting.Right)!.Id)));
            Assert.Null(Do(world, new WiredHangarCommand(Race.Red, false)));
            Hero(world).DrivenByPlayer = true;
            FireAtWill(world);
            return world;
        }
        var worlds = new[] { Played(), Played() };
        Assert.Equal(worlds[0].Checksum(), worlds[1].Checksum());

        var guns = new HashSet<int>();
        for (var tick = 0; tick < 3 * GameWorld.TicksPerSecond; tick++)
        {
            foreach (var world in worlds)
                world.Step();
            Assert.Equal(worlds[0].Checksum(), worlds[1].Checksum());
            guns.UnionWith(worlds[0].Events.Where(e => e.Kind == GameEventKind.Fired && e.Actor == Hero(worlds[0]).Id).Select(e => e.Gun));
        }
        Assert.Equal([0, 1], guns.Order());
    }

    [Fact]
    public void A_battery_changes_the_runs_fingerprint_and_taking_its_copy_off_gives_it_back()
    {
        var (with, without) = (InHangar(), InHangar());
        var spares = new[] { with, without }.Select(world =>
        {
            Board(world, "knight4");
            return Red(world).Add(WiredKind.Weapon, "BOLTER", 0);
        }).ToList();
        Assert.Equal(with.Wired!.Fingerprint(), without.Wired!.Fingerprint());

        Assert.Null(Do(with, new WiredEquipCommand(Race.Red, spares[0].Id, Right, Add: true)));
        without.Step();
        Assert.NotEqual(with.Wired.Fingerprint(), without.Wired.Fingerprint());

        Assert.Null(Do(with, new WiredUnequipCommand(Race.Red, spares[0].Id)));
        without.Step();
        Assert.Equal(with.Wired.Fingerprint(), without.Wired.Fingerprint());
    }

    [Fact]
    public void Asking_about_batteries_changes_nothing_in_the_world()
    {
        var world = InHangar();
        var state = Red(world);
        Board(world, "knight4");
        Assert.Null(Do(world, new WiredBuyCommand(Race.Red, "BOLTER", Right, Add: true)));
        var (before, fingerprint, loadout, bank) = (world.Checksum(), world.Wired!.Fingerprint(), state.Loadout, Bank(world));

        foreach (var weapon in WiredCatalogue.Weapons)
        {
            foreach (var slot in new[] { Left, Right, Top, WiredSlot.Module(0) })
            {
                state.Loadout.WhyNotAdd(Gun(-1, weapon.Id), slot);
                foreach (var worn in state.Loadout.Worn)
                    state.Loadout.WhyNotAdd(worn, slot);
                world.WhyCannotBuy(state, weapon.Id, slot);
                world.WhyCannotBuy(state, weapon.Id);
            }
        }

        Assert.Same(loadout, state.Loadout);
        Assert.Equal((before, fingerprint, bank), (world.Checksum(), world.Wired.Fingerprint(), Bank(world)));
    }

    // ---- battles ----------------------------------------------------------------------------------

    private static BattleHero Fighter(string chassis, params BattleItem[] items) => new(Race.Red, "Ace", chassis, items);

    private static string? WhyNot(BattleHero hero) => BattleBots.WhyNot(hero, BattleCatalogue.Tier(hero.Chassis));

    [Fact]
    public void A_battle_hero_brings_a_battery_as_one_entry_up_to_what_its_slot_carries()
    {
        Assert.Null(WhyNot(Fighter("knight4", new BattleItem("BOLTER", Left, 1, 3), new BattleItem("PLASMA_CANNON2", Right, 2, 2))));
        Assert.Equal("a heavy slot carries three at most", WhyNot(Fighter("knight4", new BattleItem("BOLTER", Left, 1, 4))));
        Assert.Equal("a medium slot carries two at most", WhyNot(Fighter("commander4", new BattleItem("BOLTER", Right, 1, 3))));
        Assert.Equal("a light slot carries one weapon", WhyNot(Fighter("reaper2", new BattleItem("BOLTER", Right, 1, 2))));
        Assert.Equal("that weapon cannot form a battery", WhyNot(Fighter("boss2", new BattleItem("BEE_BOMB", Top, 1, 2))));
        Assert.Equal("a battery needs a weapon slot", WhyNot(Fighter("knight4", new BattleItem("plates", WiredSlot.Module(0), 1, 2))));
        Assert.Equal("a battery needs a weapon slot", WhyNot(Fighter("knight4", new BattleItem("BOLTER", null, 1, 2))));
        Assert.Equal("BOLTER cannot come 0 times", WhyNot(Fighter("knight4", new BattleItem("BOLTER", Left, 1, 0))));

        // Every copy weighs: a heavy launcher on each side is carried, three on one side are far too heavy.
        Assert.Null(WhyNot(Fighter("knight4", new BattleItem("MULTI_LAUNCHER2", Left), new BattleItem("MULTI_LAUNCHER2", Right))));
        Assert.Equal("too heavy for this chassis", WhyNot(Fighter("knight4", new BattleItem("MULTI_LAUNCHER2", Left, 1, 3), new BattleItem("MULTI_LAUNCHER2", Right))));
    }

    [Fact]
    public void A_battle_heros_item_cap_counts_entries_and_a_refused_battery_is_clamped_to_a_bots_loadout()
    {
        var chassis = Chassis("knight4");
        BattleItem[] items =
        [
            new("BOLTER", Left, 1, 3), new("BOLTER", Right, 1, 3), new("plates", WiredSlot.Module(0)), new("plates", WiredSlot.Module(1)),
            new("plates", WiredSlot.Module(2)), new("discharge", WiredSlot.Ability(0)), new("supercharge", WiredSlot.Ability(1)),
        ];
        Assert.Equal(chassis.Slots.Count + chassis.Modules + WiredBalance.AbilitySlots, items.Length);
        var fine = Fighter("knight4", items);
        Assert.Null(WhyNot(fine));
        Assert.Equal("more items than the chassis wears", WhyNot(Fighter("knight4", [.. items, new BattleItem("plates", null)])));

        var tier = BattleCatalogue.Tier("knight4");
        Assert.Same(fine, BattleBots.Resolved(fine, tier, 0, "m_desert"));
        var refused = Fighter("knight4", new BattleItem("BOLTER", Left, 1, 4));
        var clamped = BattleBots.Resolved(refused, tier, 0, "m_desert");
        Assert.NotSame(refused, clamped);
        Assert.Null(BattleBots.WhyNot(clamped, tier));
        Assert.Equal(BattleBots.Loadout(clamped.Chassis, BattleCatalogue.Tier(clamped.Chassis)), clamped.Items);
    }
}
