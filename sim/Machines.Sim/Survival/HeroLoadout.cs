using Machines.Sim.Combat;

namespace Machines.Sim.Survival;

/// <summary>
/// What a Wired hero wears: a chassis and what fills its slots. It never changes; every change makes a new one, so the
/// hangar can try any change out on a copy (<see cref="HeroBuild.Resolve"/>) before a command makes it real.
/// </summary>
public sealed record HeroLoadout
{
    public required WiredItem Chassis { get; init; }

    /// <summary>The weapon on each mounting, by <see cref="Mounting"/>: left, right, top. Null for an empty or missing slot.</summary>
    public required IReadOnlyList<WiredItem?> Weapons { get; init; }

    /// <summary>One place for each module slot of the chassis.</summary>
    public required IReadOnlyList<WiredItem?> Modules { get; init; }

    public required IReadOnlyList<WiredItem?> Abilities { get; init; }

    /// <summary>The board's sockets (<see cref="WiredChips.Traces"/>).</summary>
    public required IReadOnlyList<WiredItem?> Chips { get; init; }

    /// <summary>A chassis with nothing on it.</summary>
    public static HeroLoadout Bare(WiredItem chassis) => new()
    {
        Chassis = chassis,
        Weapons = new WiredItem?[3],
        Modules = new WiredItem?[WiredCatalogue.FindChassis(chassis.Catalogue)?.Modules ?? 0],
        Abilities = new WiredItem?[WiredBalance.AbilitySlots],
        Chips = new WiredItem?[WiredBalance.ChipSockets],
    };

    public WiredChassis ChassisEntry => WiredCatalogue.FindChassis(Chassis.Catalogue)
        ?? throw new KeyNotFoundException($"no chassis '{Chassis.Catalogue}'");

    public WiredItem? Weapon(Mounting mounting) => Weapons[(int)mounting];

    /// <summary>Everything it wears, the chassis first.</summary>
    public IEnumerable<WiredItem> Worn => new[] { Chassis }.Concat(Weapons.Concat(Modules).Concat(Abilities).Concat(Chips).OfType<WiredItem>());

    public bool Holds(int item) => Worn.Any(w => w.Id == item);

    private IReadOnlyList<WiredItem?> Places(WiredSlotKind kind) => kind switch
    {
        WiredSlotKind.Weapon => Weapons,
        WiredSlotKind.Module => Modules,
        WiredSlotKind.Ability => Abilities,
        _ => Chips,
    };

    /// <summary>What is in a slot, or null when nothing or no such slot.</summary>
    public WiredItem? At(WiredSlot slot) => slot.Index >= 0 && slot.Index < Places(slot.Kind).Count ? Places(slot.Kind)[slot.Index] : null;

    /// <summary>The slot an item sits in, or null for none (the chassis has none either).</summary>
    public WiredSlot? SlotOf(int item)
    {
        foreach (var kind in Enum.GetValues<WiredSlotKind>())
        {
            var places = Places(kind);
            for (var i = 0; i < places.Count; i++)
            {
                if (places[i]?.Id == item)
                    return new WiredSlot(kind, i);
            }
        }
        return null;
    }

    /// <returns>null if the item may go into the slot of this chassis, otherwise why not.</returns>
    public string? WhyNotFit(WiredItem item, WiredSlot slot)
    {
        if (item.Kind != slot.Takes)
            return "that does not go into this slot";
        if (slot.Index < 0 || slot.Index >= Places(slot.Kind).Count)
            return "this chassis has no such slot";
        if (slot.Kind != WiredSlotKind.Weapon)
            return null;
        return WiredCatalogue.FindWeapon(item.Catalogue) is { } weapon
            ? WiredCatalogue.WhyNotFit(ChassisEntry, (Mounting)slot.Index, weapon)
            : "that does not go into this slot";
    }

    /// <summary>Why a weapon goes into no slot of this chassis at all, in words that say what would take it; null if one takes it.</summary>
    public string? WhyNoSlot(WiredItem item)
    {
        var reasons = ChassisEntry.Slots.Where(s => s.Fixed is null).Select(s => WhyNotFit(item, WiredSlot.Weapon(s.Mounting))).ToList();
        if (reasons.Count == 0)
            return "this chassis has no such slot";
        if (reasons.Any(r => r is null))
            return null;
        return WiredCatalogue.FindWeapon(item.Catalogue) is { } weapon && reasons.All(r => r == "the weapon is too big for this slot")
            ? weapon.Size == SlotSize.Medium ? "that needs a medium slot" : "that needs a heavy slot"
            : reasons[0];
    }

    /// <summary>The loadout with a slot filled, or emptied by null. An item worn elsewhere moves here.</summary>
    public HeroLoadout With(WiredSlot slot, WiredItem? item)
    {
        var loadout = item is null ? this : Without(item.Id);
        var places = loadout.Places(slot.Kind).ToArray();
        places[slot.Index] = item;
        return slot.Kind switch
        {
            WiredSlotKind.Weapon => loadout with { Weapons = places },
            WiredSlotKind.Module => loadout with { Modules = places },
            WiredSlotKind.Ability => loadout with { Abilities = places },
            _ => loadout with { Chips = places },
        };
    }

    /// <summary>The loadout with an item taken off; it stays as it is if the item is not on it or is the chassis.</summary>
    public HeroLoadout Without(int item) => SlotOf(item) is { } slot ? With(slot, null) : this;

    /// <summary>The loadout with a worn item swapped for a new state of itself, after an upgrade.</summary>
    public HeroLoadout Replacing(WiredItem item)
    {
        if (Chassis.Id == item.Id)
            return this with { Chassis = item };
        return SlotOf(item.Id) is { } slot ? With(slot, null).With(slot, item) : this;
    }

    /// <summary>
    /// The loadout moved to another chassis. Nothing is lost: each weapon stays on its mounting if that still takes it,
    /// else it takes the first free slot that does, else it comes off; modules keep their places as far as the new
    /// chassis has them. Abilities and chips do not depend on the chassis.
    /// </summary>
    public HeroLoadout OnChassis(WiredItem chassis)
    {
        var entry = WiredCatalogue.FindChassis(chassis.Catalogue) ?? throw new KeyNotFoundException($"no chassis '{chassis.Catalogue}'");
        var weapons = new WiredItem?[Weapons.Count];
        var homeless = new List<WiredItem>();
        for (var i = 0; i < Weapons.Count; i++)
        {
            if (Weapons[i] is not { } item)
                continue;
            if (Fits(entry, (Mounting)i, item))
                weapons[i] = item;
            else
                homeless.Add(item);
        }
        foreach (var item in homeless)
        {
            var free = Enum.GetValues<Mounting>().Where(m => weapons[(int)m] is null && Fits(entry, m, item)).Cast<Mounting?>().FirstOrDefault();
            if (free is { } mounting)
                weapons[(int)mounting] = item;
        }
        var modules = new WiredItem?[entry.Modules];
        var kept = Modules.OfType<WiredItem>().Take(entry.Modules).ToList();
        for (var i = 0; i < kept.Count; i++)
            modules[i] = kept[i];
        return this with { Chassis = chassis, Weapons = weapons, Modules = modules };
    }

    private static bool Fits(WiredChassis chassis, Mounting mounting, WiredItem item) =>
        WiredCatalogue.FindWeapon(item.Catalogue) is { } weapon && WiredCatalogue.WhyNotFit(chassis, mounting, weapon) is null;
}
