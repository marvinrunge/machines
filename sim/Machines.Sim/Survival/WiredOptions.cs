using Machines.Sim.World;

namespace Machines.Sim.Survival;

/// <summary>
/// Something a hero bought at creation, and where it goes; with no slot it stays in the inventory. A weapon may come as a
/// battery of <paramref name="Count"/> copies on its slot, each bought on its own; a record from before batteries has one.
/// </summary>
public sealed record WiredStartItem(string Catalogue, WiredSlot? Slot = null, int Count = 1);

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

    /// <summary>
    /// The heroes of a run with more than one, by player; a hero player without an entry plays the run's own
    /// <see cref="HeroName"/>, <see cref="Chassis"/> and <see cref="Items"/>, as a run of one hero always does.
    /// </summary>
    public IReadOnlyList<WiredHero> Heroes { get; init; } = [];

    /// <summary>The hero a player starts the run with: its entry in <see cref="Heroes"/>, else the run's own.</summary>
    public WiredHero HeroFor(PlayerId player) =>
        Heroes.FirstOrDefault(h => h.Player == player) ?? new WiredHero(player, HeroName, Chassis, Items);
}

/// <summary>One hero of a Wired run with more than one: whose it is, its name, the chassis it starts on and what it bought at creation.</summary>
public sealed record WiredHero(PlayerId Player, string Name, string Chassis, IReadOnlyList<WiredStartItem> Items);
