using Machines.Import.Model;

namespace Machines.Sim.Survival;

/// <summary>How a battle can be won: by wiping out the other team, by taking its pod, or either. A wiped-out team always loses.</summary>
public enum BattleWin
{
    Both,
    Wipe,
    Capture,
}

/// <summary>
/// Something a battle hero wears, at its Mk; with no slot it stays in the inventory, as modules, abilities and spares can. A
/// weapon on a weapon slot may come as a battery of <paramref name="Count"/> copies, all at that Mk; an entry from before
/// batteries has one.
/// </summary>
public sealed record BattleItem(string Catalogue, WiredSlot? Slot, int Mk = 1, int Count = 1);

/// <summary>One hero of a battle: its race (the scenario slot it plays), its name, its chassis and what it wears. <paramref name="Bot"/> says the computer drives it.</summary>
public sealed record BattleHero(Race Race, string Name, string Chassis, IReadOnlyList<BattleItem> Items, bool Bot = false)
{
    /// <summary>The longest name a hero has, in characters, as the hangar lets a player type it.</summary>
    public const int MostName = 24;
}

/// <summary>
/// The settings of a battle, the remake's own team mode: two teams of heroes on one map, each with a pod. Part of the match
/// options, so that a recording plays the same whatever the players' profiles hold by then. Every hero is listed, the
/// computer's too; the sim never invents one.
/// </summary>
public sealed record BattleOptions
{
    /// <summary>The top of the bracket: heroes of this tier and the one below may play.</summary>
    public int Tier { get; init; } = 1;

    /// <summary>Bodies per hero, 1 to 5; more than one means a fallen hero comes back at its team's pod.</summary>
    public int Lives { get; init; } = 1;

    /// <summary>Seconds a fallen hero waits for its next body.</summary>
    public float RespawnWait { get; init; } = 10;

    public BattleWin Win { get; init; } = BattleWin.Both;

    /// <summary>Seconds of fighting after the opening; then it is a draw.</summary>
    public int TimeLimit { get; init; } = 720;

    /// <summary>The scenario slots of each team; the first is the anchor, whose pod is the team's.</summary>
    public IReadOnlyList<Race> TeamA { get; init; } = [];

    public IReadOnlyList<Race> TeamB { get; init; } = [];

    /// <summary>One hero for each race of the two teams, sorted by race.</summary>
    public IReadOnlyList<BattleHero> Heroes { get; init; } = [];
}
