namespace Machines.WiredBench;

/// <summary>What a strategy can spend on.</summary>
internal enum Goal
{
    Weapon,
    Chassis,
    Module,
    Ability,
    Squad,
}

/// <summary>
/// How a bot shops. At each visit to the hangar it goes down the <paramref name="Turns"/>, one purchase each; a chassis it
/// cannot pay yet ends the visit, as it is saving up, while what the shop does not offer is simply skipped.
/// <paramref name="Ladder"/> is the way up the chassis tree.
/// </summary>
internal sealed record Strategy(string Name, IReadOnlyList<string> Ladder, IReadOnlyList<Goal> Turns)
{
    /// <summary>What the names of the weapons it buys contain; empty for any.</summary>
    public IReadOnlyList<string> Guns { get; init; } = [];

    public IReadOnlyList<string> Modules { get; init; } = ["plates", "repair", "frame", "reactor", "targeting", "drive", "cooler", "scrap"];

    public IReadOnlyList<string> Abilities { get; init; } = ["discharge", "supercharge", "minefield", "vortex", "treachery", "ionstrike"];

    /// <summary>Steps of the ladder too poor to stay on: it saves for the next one as well and goes straight through.</summary>
    public IReadOnlyList<string> Passing { get; init; } = [];

    /// <summary>It leaves what it finds in the inventory as well.</summary>
    public bool WearsNothing { get; init; }

    /// <summary>Whether it spends anything at all.</summary>
    public bool Buys => Turns.Count > 0;

    private static readonly string[] Knights = ["grunt2", "grunt3", "knight3", "knight4", "knight5"];
    private static readonly string[] Reapers = ["grunt2", "reaper2", "reaper3"];
    private static readonly string[] Commanders = ["grunt2", "overseer2", "overseer3", "commander4", "commander5"];
    private static readonly string[] Ballistas = ["grunt2", "grunt3", "ballista3", "ballista4"];
    private static readonly string[] Gliders = ["grunt2", "overseer2", "overseer3", "boss1", "boss2"];
    private static readonly string[] Heavies = ["grunt2", "grunt3", "knight3", "ballista3", "ballista4", "gorilla5"];

    private static readonly Goal[] Even = [Goal.Weapon, Goal.Chassis, Goal.Module, Goal.Ability];
    private static readonly Goal[] Climbing = [Goal.Chassis, Goal.Weapon, Goal.Module, Goal.Ability];

    public static readonly IReadOnlyList<Strategy> All =
    [
        new("none", [], []),
        new("bare", [], []) { WearsNothing = true },
        new("guns-first", Knights, [Goal.Weapon, Goal.Weapon, Goal.Module, Goal.Chassis]),
        new("chassis-first", Knights, [Goal.Chassis, Goal.Weapon, Goal.Module]),
        new("balanced", Knights, Even),
        new("squad", Knights, [Goal.Squad, Goal.Weapon, Goal.Squad, Goal.Chassis]),
        new("reaper", Reapers, Climbing),
        new("commander", Commanders, Climbing),
        // These bodies carry one gun at first, so the guns come before the next step up.
        new("ballista", Ballistas, Even),
        new("glider", Gliders, Climbing),
        new("heavy", Heavies, Even) { Passing = ["ballista3"] },
        new("gorilla", [.. Knights, "gorilla5"], Climbing),
        new("bolters", Knights, Even) { Guns = ["BOLTER", "AUTO_CANNON"] },
        new("plasma", Knights, Even) { Guns = ["PLASMA"] },
        new("pulse", Knights, Even) { Guns = ["PULSE"] },
        new("launchers", Knights, Even) { Guns = ["MULTI_LAUNCHER"] },
    ];
}
