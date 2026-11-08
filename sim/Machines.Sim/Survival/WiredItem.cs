using Machines.Sim.Combat;

namespace Machines.Sim.Survival;

/// <summary>What a catalogue entry is.</summary>
public enum WiredKind
{
    Chassis,
    Weapon,
    Module,
    Ability,
    Chip,
}

/// <summary>
/// One thing a hero owns: <paramref name="Id"/> tells it from others of its kind, <paramref name="Catalogue"/> says what
/// it is. <paramref name="Paid"/> is all that went into it, price and Mk upgrades; half of that comes back when sold.
/// </summary>
public sealed record WiredItem(int Id, WiredKind Kind, string Catalogue, int Mk = 1, int Paid = 0);

/// <summary>What kind of slot of the hero an item goes into.</summary>
public enum WiredSlotKind
{
    Weapon,
    Module,
    Ability,
    Chip,
}

/// <summary>One slot of the hero: a weapon's by its <see cref="Mounting"/> (as a number), the others by their place from 0.</summary>
public sealed record WiredSlot(WiredSlotKind Kind, int Index)
{
    public static WiredSlot Weapon(Mounting mounting) => new(WiredSlotKind.Weapon, (int)mounting);
    public static WiredSlot Module(int index) => new(WiredSlotKind.Module, index);
    public static WiredSlot Ability(int index) => new(WiredSlotKind.Ability, index);
    public static WiredSlot Chip(int socket) => new(WiredSlotKind.Chip, socket);

    /// <summary>The kind of item that goes into it.</summary>
    public WiredKind Takes => Kind switch
    {
        WiredSlotKind.Weapon => WiredKind.Weapon,
        WiredSlotKind.Module => WiredKind.Module,
        WiredSlotKind.Ability => WiredKind.Ability,
        _ => WiredKind.Chip,
    };
}
