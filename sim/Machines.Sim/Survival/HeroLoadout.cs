using Machines.Sim.Combat;

namespace Machines.Sim.Survival;

/// <summary>
/// What a Wired hero wears: a chassis and what fills its slots. It never changes; every change makes a new one, so the
/// hangar can try any change out on a copy (<see cref="HeroBuild.Resolve"/>) before a command makes it real.
/// </summary>
public sealed record HeroLoadout
{
    public required WiredItem Chassis { get; init; }

    /// <summary>
    /// The weapon on each mounting, by <see cref="Mounting"/>: left, right, top. Null for an empty or missing slot. Where a
    /// slot carries a battery, this is its first copy, copy 0.
    /// </summary>
    public required IReadOnlyList<WiredItem?> Weapons { get; init; }

    /// <summary>
    /// The further copies of each mounting's weapon, by <see cref="Mounting"/>: a battery's copies 1 and on, in order, all
    /// of copy 0's catalogue, never more than the slot carries. Empty where a slot carries one weapon or none, as it does
    /// on every loadout from before batteries.
    /// </summary>
    public IReadOnlyList<IReadOnlyList<WiredItem>> Copies { get; init; } = NoCopies;

    private static readonly IReadOnlyList<IReadOnlyList<WiredItem>> NoCopies = [[], [], []];

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

    /// <summary>The weapons on a mounting: copy 0, then its copies; empty for an empty or missing slot.</summary>
    public IReadOnlyList<WiredItem> Battery(Mounting mounting) =>
        Weapons[(int)mounting] is { } first ? [first, .. Copies[(int)mounting]] : [];

    /// <summary>Where a worn weapon stands in its battery, 0 for the first copy or a single weapon; null for anything else.</summary>
    public int? CopyOf(int item)
    {
        for (var mounting = 0; mounting < Weapons.Count; mounting++)
        {
            if (Weapons[mounting]?.Id == item)
                return 0;
            if (IndexIn(Copies[mounting], item) is var at and >= 0)
                return at + 1;
        }
        return null;
    }

    /// <summary>Everything it wears, the chassis first, the weapons' copies right after the weapons.</summary>
    public IEnumerable<WiredItem> Worn => new[] { Chassis }.Concat(Weapons.OfType<WiredItem>()).Concat(Copies.SelectMany(c => c))
        .Concat(Modules.Concat(Abilities).Concat(Chips).OfType<WiredItem>());

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

    /// <summary>The slot an item sits in, a copy's that of its battery, or null for none (the chassis has none either).</summary>
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
        for (var mounting = 0; mounting < Copies.Count; mounting++)
        {
            if (IndexIn(Copies[mounting], item) >= 0)
                return WiredSlot.Weapon((Mounting)mounting);
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

    /// <summary>
    /// The loadout with a slot filled, or emptied by null. An item worn elsewhere moves here. A weapon slot's whole battery
    /// comes off and the item stands alone: a put is no add (<see cref="Adding"/>), even of the same weapon.
    /// </summary>
    public HeroLoadout With(WiredSlot slot, WiredItem? item)
    {
        var loadout = item is null ? this : Without(item.Id);
        var placed = loadout.Placing(slot.Kind, slot.Index, item);
        return slot.Kind == WiredSlotKind.Weapon ? placed with { Copies = placed.CopiesWith(slot.Index, []) } : placed;
    }

    /// <summary>
    /// The loadout with an item taken off; it stays as it is if the item is not on it or is the chassis. A copy of a battery
    /// leaves it and the later ones close up; the first copy leaving makes the second the first.
    /// </summary>
    public HeroLoadout Without(int item)
    {
        for (var mounting = 0; mounting < Copies.Count; mounting++)
        {
            var copies = Copies[mounting];
            if (copies.Count == 0)
                continue;
            if (Weapons[mounting]?.Id == item)
                return Placing(WiredSlotKind.Weapon, mounting, copies[0]) with { Copies = CopiesWith(mounting, [.. copies.Skip(1)]) };
            if (IndexIn(copies, item) is var at and >= 0)
                return this with { Copies = CopiesWith(mounting, [.. copies.Where((_, i) => i != at)]) };
        }
        return SlotOf(item) is { } slot ? With(slot, null) : this;
    }

    /// <summary>The loadout with a worn item swapped for a new state of itself, after an upgrade: a copy keeps its place in its battery.</summary>
    public HeroLoadout Replacing(WiredItem item)
    {
        if (Chassis.Id == item.Id)
            return this with { Chassis = item };
        for (var mounting = 0; mounting < Copies.Count; mounting++)
        {
            if (IndexIn(Copies[mounting], item.Id) is var at and >= 0)
                return this with { Copies = CopiesWith(mounting, [.. Copies[mounting].Select((copy, i) => i == at ? item : copy)]) };
        }
        return SlotOf(item.Id) is { } slot ? Placing(slot.Kind, slot.Index, item) : this;
    }

    /// <summary>
    /// The loadout with a weapon added to a weapon slot's battery, as its last copy; onto an empty slot, or a slot of
    /// another kind, it is a plain put. The item comes off wherever else it was first. <see cref="WhyNotAdd"/> says
    /// whether it may.
    /// </summary>
    public HeroLoadout Adding(WiredSlot slot, WiredItem item)
    {
        var loadout = Without(item.Id);
        if (slot.Kind != WiredSlotKind.Weapon || loadout.Places(slot.Kind)[slot.Index] is null)
            return loadout.With(slot, item);
        return loadout with { Copies = loadout.CopiesWith(slot.Index, [.. loadout.Copies[slot.Index], item]) };
    }

    /// <returns>
    /// null if the item may join the battery on a weapon slot (<see cref="Adding"/>), otherwise why not. Asked of the
    /// loadout with the item taken off wherever it was, in this order: what <see cref="WhyNotFit"/> says; an empty slot
    /// takes it as a plain put, of any kind; a filled slot of another kind holds no battery; another weapon on the slot;
    /// a weapon that forms no battery; a full slot.
    /// </returns>
    public string? WhyNotAdd(WiredItem item, WiredSlot slot)
    {
        var loadout = Without(item.Id);
        if (loadout.WhyNotFit(item, slot) is { } why)
            return why;
        if (loadout.At(slot) is not { } first)
            return null;
        if (slot.Kind != WiredSlotKind.Weapon)
            return "a battery needs a weapon slot";
        var mounting = (Mounting)slot.Index;
        if (first.Catalogue != item.Catalogue)
            return "a battery takes only the same weapon";
        return WiredCatalogue.WhyNotCopies(ChassisEntry, mounting, WiredCatalogue.FindWeapon(item.Catalogue)!, loadout.Battery(mounting).Count + 1);
    }

    /// <summary>
    /// The loadout moved to another chassis. Nothing is lost: each weapon stays on its mounting if that still takes it,
    /// else it takes the first free slot that does, else it comes off; a battery goes with its first copy and is cut to
    /// what its new slot carries, its last copies coming off. Modules keep their places as far as the new chassis has
    /// them. Abilities and chips do not depend on the chassis.
    /// </summary>
    public HeroLoadout OnChassis(WiredItem chassis)
    {
        var entry = WiredCatalogue.FindChassis(chassis.Catalogue) ?? throw new KeyNotFoundException($"no chassis '{chassis.Catalogue}'");
        var weapons = new WiredItem?[Weapons.Count];
        var copies = new IReadOnlyList<WiredItem>[Weapons.Count];
        // The mountings whose battery must find another slot.
        var homeless = new List<int>();
        for (var i = 0; i < Weapons.Count; i++)
        {
            copies[i] = [];
            if (Weapons[i] is not { } item)
                continue;
            if (Fits(entry, (Mounting)i, item))
                (weapons[i], copies[i]) = (item, Copies[i]);
            else
                homeless.Add(i);
        }
        foreach (var left in homeless)
        {
            var item = Weapons[left]!;
            var free = Enum.GetValues<Mounting>().Where(m => weapons[(int)m] is null && Fits(entry, m, item)).Cast<Mounting?>().FirstOrDefault();
            if (free is { } mounting)
                (weapons[(int)mounting], copies[(int)mounting]) = (item, Copies[left]);
        }
        for (var i = 0; i < weapons.Length; i++)
        {
            if (weapons[i] is { } first && copies[i].Count > 0)
                copies[i] = [.. copies[i].Take(WiredCatalogue.Copies(entry, WiredSlot.Weapon((Mounting)i), first.Catalogue, copies[i].Count + 1) - 1)];
        }
        var modules = new WiredItem?[entry.Modules];
        var kept = Modules.OfType<WiredItem>().Take(entry.Modules).ToList();
        for (var i = 0; i < kept.Count; i++)
            modules[i] = kept[i];
        return this with { Chassis = chassis, Weapons = weapons, Copies = copies.All(c => c.Count == 0) ? NoCopies : copies, Modules = modules };
    }

    private static bool Fits(WiredChassis chassis, Mounting mounting, WiredItem item) =>
        WiredCatalogue.FindWeapon(item.Catalogue) is { } weapon && WiredCatalogue.WhyNotFit(chassis, mounting, weapon) is null;

    /// <summary>The loadout with one place set and nothing else changed, a battery's copies neither.</summary>
    private HeroLoadout Placing(WiredSlotKind kind, int index, WiredItem? item)
    {
        var places = Places(kind).ToArray();
        places[index] = item;
        return kind switch
        {
            WiredSlotKind.Weapon => this with { Weapons = places },
            WiredSlotKind.Module => this with { Modules = places },
            WiredSlotKind.Ability => this with { Abilities = places },
            _ => this with { Chips = places },
        };
    }

    /// <summary>The copies with those of one mounting changed; the shared empty set again once none are left.</summary>
    private IReadOnlyList<IReadOnlyList<WiredItem>> CopiesWith(int mounting, IReadOnlyList<WiredItem> copies)
    {
        if (copies.Count == 0 && Copies[mounting].Count == 0)
            return Copies;
        var all = Copies.ToArray();
        all[mounting] = copies;
        return all.All(c => c.Count == 0) ? NoCopies : all;
    }

    private static int IndexIn(IReadOnlyList<WiredItem> items, int id)
    {
        for (var i = 0; i < items.Count; i++)
        {
            if (items[i].Id == id)
                return i;
        }
        return -1;
    }
}
