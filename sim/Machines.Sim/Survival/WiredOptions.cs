namespace Machines.Sim.Survival;

/// <summary>Something a hero bought at creation, and where it goes; with no slot it stays in the inventory.</summary>
public sealed record WiredStartItem(string Catalogue, WiredSlot? Slot = null);

/// <summary>
/// The settings of a Wired run, part of the match options, so that a recording plays the same whatever the player's
/// profile holds by then. Wired is the remake's own addition.
/// </summary>
public sealed record WiredOptions
{
    public WiredDifficulty Difficulty { get; init; } = WiredDifficulty.Normal;

    public string HeroName { get; init; } = "";

    /// <summary>The arena's scenario, for the records.</summary>
    public string Arena { get; init; } = "";

    /// <summary>The chassis the hero starts on; any other than the first is paid for from a capital raised by its price.</summary>
    public string Chassis { get; init; } = WiredBalance.StartChassis;

    /// <summary>What the hero bought at creation, in order. The first BOLTER listed is the free one and says where that goes.</summary>
    public IReadOnlyList<WiredStartItem> Items { get; init; } = [];

    /// <summary>The chips the player has unlocked; empty means the base pool.</summary>
    public IReadOnlyList<string> ChipPool { get; init; } = [];
}
