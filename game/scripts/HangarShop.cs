using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>Where a chassis stands for the hero, on the shop's tree.</summary>
public enum ChassisStep
{
    /// <summary>The hero is on it.</summary>
    Worn,

    /// <summary>It is there to be taken, for nothing.</summary>
    Owned,

    /// <summary>Next on the way, and the bank covers it.</summary>
    ForSale,

    /// <summary>Next on the way, and the bank does not cover it yet.</summary>
    TooDear,

    /// <summary>Not reached yet.</summary>
    Closed,
}

/// <summary>How a line of the tree is drawn.</summary>
public enum ChassisLine
{
    /// <summary>A way not open yet.</summary>
    Ahead,

    /// <summary>A way open next.</summary>
    Next,

    /// <summary>A way the hero has come.</summary>
    Come,
}

/// <summary>
/// The chassis on the shop's tree: where each stands and which lines run between them, the catalogue's <c>Needs</c>. Any one
/// line into a chassis opens it. The grunts run down the middle, the Ballistas down the left and the administrators
/// down the right, and all come together in the Gorilla. Free of Godot, so the tests can compile it.
/// </summary>
public static class ChassisTree
{
    public const int Columns = 5, Rows = 8;

    private static readonly Dictionary<string, (int Column, int Row)> Places = new()
    {
        ["grunt1"] = (2, 0),
        ["grunt2"] = (2, 1),
        ["reaper2"] = (1, 2),
        ["grunt3"] = (2, 2),
        ["overseer2"] = (4, 2),
        ["reaper3"] = (1, 3),
        ["overseer3"] = (4, 3),
        ["ballista3"] = (0, 4),
        ["knight3"] = (2, 4),
        ["boss1"] = (4, 4),
        ["ballista4"] = (0, 5),
        ["knight4"] = (2, 5),
        ["commander4"] = (3, 5),
        ["boss2"] = (4, 5),
        ["knight5"] = (2, 6),
        ["commander5"] = (3, 6),
        ["gorilla5"] = (1, 7),
    };

    public static (int Column, int Row) Place(string chassis) => Places[chassis];

    /// <summary>
    /// What a line between two chassis is: <paramref name="reached"/> says the way on from the first is open. Only in a
    /// <paramref name="run"/> has the hero come by what it has; before one, two open starts are no way walked.
    /// </summary>
    public static ChassisLine Line(ChassisStep from, ChassisStep to, bool reached, bool run)
    {
        var there = to is ChassisStep.Worn or ChassisStep.Owned;
        if (there && from is ChassisStep.Worn or ChassisStep.Owned)
            return run ? ChassisLine.Come : ChassisLine.Ahead;
        return !there && to != ChassisStep.Closed && reached ? ChassisLine.Next : ChassisLine.Ahead;
    }

    /// <summary>Each line from a chassis to one it opens.</summary>
    public static IEnumerable<(string From, string To)> Lines =>
        WiredCatalogue.Chassis.SelectMany(c => c.Needs.Select(need => (need, c.Id)));

    /// <summary>
    /// Whether a line can run down the column it ends in, so that it turns aside right under the chassis it starts from.
    /// Otherwise something stands in that column and it turns aside only right over the chassis it ends at.
    /// </summary>
    public static bool TurnsAtOnce(string from, string to)
    {
        var ((_, start), (column, end)) = (Places[from], Places[to]);
        return !Places.Values.Any(p => p.Column == column && p.Row > start && p.Row < end);
    }
}

/// <summary>A weapon slot both chassis have, at another size.</summary>
public sealed record SlotChange(Mounting Mounting, SlotSize Was, SlotSize Is);

/// <summary>What changing to a chassis does, as the tree tells it.</summary>
/// <param name="WeaponSlots">Weapon slots gained, or lost if below 0; a weapon that is part of the chassis counts for none.</param>
/// <param name="StaysOn">Weapons and modules that stay on.</param>
/// <param name="FallsOff">Weapons and modules that go to the inventory.</param>
/// <param name="EmptySlots">Slots left empty, modules included.</param>
/// <param name="Resized">The slots that stay but take another size of weapon.</param>
public sealed record ChassisGain(int WeaponSlots, int ModuleSlots, int HitPoints, int Armour, int Carries, int Supplies, int StaysOn, int FallsOff, int EmptySlots, IReadOnlyList<SlotChange> Resized)
{
    /// <summary>Slots, toughness, load and power all stay as they are.</summary>
    public bool Same => WeaponSlots == 0 && ModuleSlots == 0 && HitPoints == 0 && Armour == 0 && Carries == 0 && Supplies == 0 && FallsOff == 0 && Resized.Count == 0;
}

/// <summary>
/// The decision the hangar is there for, side by side: the next chassis or the next weapon, what each costs and brings, and
/// how far the bank goes. Either may be missing where nothing of the kind is to be had. <paramref name="Full"/> says the
/// inventory takes nothing more, whatever the bank says.
/// </summary>
public sealed record ShopChoice(WiredChassis? Chassis, int ChassisPrice, ChassisGain? Step, string? Weapon, int WeaponPrice, float WeaponGain, int Bank, bool Full)
{
    public bool ChassisAffordable => Chassis is not null && ChassisPrice <= Bank;

    public bool WeaponAffordable => Weapon is not null && WeaponPrice <= Bank;

    /// <summary>What both come to; only the one there is when there is only one.</summary>
    public int Both => (Chassis is null ? 0 : ChassisPrice) + (Weapon is null ? 0 : WeaponPrice);

    /// <summary>What is left of the bank with both bought; below 0 it is what is missing.</summary>
    public int LeftWithBoth => Bank - Both;
}

/// <summary>
/// The shop of a run as the hangar asks about it. All of it reads the world and changes nothing: what is for sale and at
/// what price stays the sim's to say. Free of Godot, so the tests can compile it. Wired is the remake's own addition.
/// </summary>
public sealed class HangarShop(GameWorld world, WiredState state)
{
    public int Bank => (int)world.Races[state.Race].Bmu;

    /// <summary>The wave the shop is stocked for: the next one.</summary>
    public int Wave => world.Wired!.ShopWave;

    /// <summary>What clearing the next wave pays, on top of what its wrecks bring.</summary>
    public int WaveBonus => WiredBalance.WaveBonus(Wave) + WiredChips.WaveExtra(state, Wave);

    public int SquadSize => world.Escorts(state.Race).Count();

    public int SquadLimit => WiredBalance.SquadLimit(Wave, WiredChips.SquadExtra(state));

    /// <summary>What lies in the inventory, against <see cref="WiredBalance.InventoryLimit"/>.</summary>
    public int Spares => state.Inventory.Count();

    /// <summary>Nothing more can be bought or taken.</summary>
    public bool Full => world.WhyNoRoom(state) is not null;

    public int RerollPrice => state.Priced(WiredBalance.RerollCost);

    /// <summary>What a catalogue id costs this hero.</summary>
    public int Price(string catalogue) => state.Priced(WiredCatalogue.Price(catalogue));

    public ChassisStep Step(WiredChassis chassis)
    {
        if (state.Loadout.Chassis.Catalogue == chassis.Id)
            return ChassisStep.Worn;
        if (state.Items.Any(i => i.Catalogue == chassis.Id))
            return ChassisStep.Owned;
        if (chassis.Needs.Count > 0 && !chassis.Needs.Any(state.Reached.Contains))
            return ChassisStep.Closed;
        return Bank >= Price(chassis.Id) ? ChassisStep.ForSale : ChassisStep.TooDear;
    }

    /// <summary>What a chassis would be worn as: the hero's own if it has it, else one to try out.</summary>
    private WiredItem Item(WiredChassis chassis) =>
        state.Items.Find(i => i.Catalogue == chassis.Id) ?? new WiredItem(0, WiredKind.Chassis, chassis.Id);

    private HeroBuild Build(HeroLoadout loadout) => HeroBuild.Resolve(world.Data, loadout);

    /// <summary>What changing to a chassis does to the hero as it stands.</summary>
    public ChassisGain Gain(WiredChassis chassis)
    {
        var now = state.Loadout;
        var then = now.OnChassis(Item(chassis));
        var (before, after) = (Build(now), Build(then));
        var worn = now.Weapons.Concat(now.Modules).OfType<WiredItem>().ToList();
        var staying = worn.Count(i => then.Holds(i.Id));
        static int Free(WiredChassis c) => c.Slots.Count(s => s.Fixed is null);
        var empty = then.ChassisEntry.Slots.Count(s => s.Fixed is null && then.Weapon(s.Mounting) is null) + then.Modules.Count(m => m is null);
        var resized = chassis.Slots.Where(s => s.Fixed is null).Select(s => (Now: s, Was: now.ChassisEntry.Slot(s.Mounting)))
            .Where(s => s.Was is { Fixed: null } was && was.Size != s.Now.Size).Select(s => new SlotChange(s.Now.Mounting, s.Was!.Size, s.Now.Size)).ToList();
        return new ChassisGain(Free(chassis) - Free(now.ChassisEntry), chassis.Modules - now.ChassisEntry.Modules,
            after.Stats.HitPoints - before.Stats.HitPoints, after.Stats.Armour - before.Stats.Armour,
            after.LoadCapacity - before.LoadCapacity, after.PowerSupply - before.PowerSupply, staying, worn.Count - staying, empty, resized);
    }

    /// <summary>What a weapon on offer adds to the hero's damage per second in the best slot it fits, or null if it fits none.</summary>
    public float? WeaponGain(string weapon)
    {
        var now = state.Loadout;
        var item = new WiredItem(0, WiredKind.Weapon, weapon);
        var fits = now.ChassisEntry.Slots.Select(s => WiredSlot.Weapon(s.Mounting)).Where(s => now.WhyNotFit(item, s) is null).ToList();
        return fits.Count == 0 ? null : fits.Max(s => Build(now.With(s, item)).DamagePerSecond) - Build(now).DamagePerSecond;
    }

    /// <summary>The cheapest chassis that is next on the way, one that loses nothing if there is any, or null when the tree is climbed.</summary>
    public WiredChassis? NextChassis()
    {
        var open = WiredCatalogue.Chassis.Where(c => Step(c) is ChassisStep.ForSale or ChassisStep.TooDear).ToList();
        return open.Where(c => Gain(c) is { WeaponSlots: >= 0, ModuleSlots: >= 0, HitPoints: >= 0 }).MinBy(c => c.Price) ?? open.MinBy(c => c.Price);
    }

    /// <summary>What the offer holds for the guns as they stand: the most damage the bank covers, else the cheapest that fits.</summary>
    public string? NextWeapon()
    {
        var fitting = state.Offer.OfType<string>().Where(id => WiredCatalogue.FindWeapon(id) is not null)
            .Select(id => (Id: id, Gain: WeaponGain(id), Price: Price(id))).Where(o => o.Gain is not null).ToList();
        var within = fitting.Where(o => o.Price <= Bank).ToList();
        return within.Count > 0 ? within.MaxBy(o => o.Gain).Id : fitting.Count > 0 ? fitting.MinBy(o => o.Price).Id : null;
    }

    /// <summary>The decision as it stands, for the chassis and weapon the player last looked at; for what it has not looked at yet, the next of each.</summary>
    public ShopChoice Choice(string? chassis, string? weapon)
    {
        var (bank, entry) = (Bank, chassis is null ? null : WiredCatalogue.FindChassis(chassis));
        if (entry is null || Step(entry) is not (ChassisStep.ForSale or ChassisStep.TooDear))
            entry = NextChassis();
        if (weapon is null || !state.Offer.Contains(weapon) || WeaponGain(weapon) is null)
            weapon = NextWeapon();
        return new ShopChoice(entry, entry is null ? 0 : Price(entry.Id), entry is null ? null : Gain(entry),
            weapon, weapon is null ? 0 : Price(weapon), weapon is null ? 0 : WeaponGain(weapon) ?? 0, bank, Full);
    }

    /// <summary>The escorts for sale for the wave, the newest first.</summary>
    public IEnumerable<ResearchKey> Escorts() => WiredCatalogue.Tiers.Where(t => t.Wave <= Wave).Reverse()
        .SelectMany(t => t.Combos.Select(combo => new ResearchKey(t.Type, t.SubType, t.Level, combo)));
}

/// <summary>
/// Tells a deal from the second half of a double click. A deal that lands makes the cards anew, and the next spare may then lie
/// under the mouse, so what counts is the spot and the time, never the card. Free of Godot, so the tests can compile it.
/// Wired is the remake's own addition.
/// </summary>
public sealed class DealClicks
{
    /// <summary>How long a click on the same spot still belongs to the deal, in milliseconds: the time of a double click.</summary>
    public const ulong Window = 500;

    private (float X, float Y, ulong At)? _last;

    /// <summary>Whether a click belongs to the last deal; <paramref name="reach"/> is how far a hand may slip between the two.</summary>
    public bool Second(float x, float y, ulong now, float reach) =>
        _last is { } last && now - last.At < Window && MathF.Abs(x - last.X) <= reach && MathF.Abs(y - last.Y) <= reach;

    /// <summary>A deal was done from this spot.</summary>
    public void Dealt(float x, float y, ulong now) => _last = (x, y, now);
}
