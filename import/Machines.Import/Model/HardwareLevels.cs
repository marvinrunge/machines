namespace Machines.Import.Model;

/// <summary>
/// Which hardware levels exist for each machine and construction type.
/// <c>parmdata.dat</c> lists per-level values in this order, one column per level.
/// Copied from the <c>*HardwareLevels[]</c> tables in <c>machphys/levels.cpp</c>.
/// </summary>
public static class HardwareLevels
{
    private static readonly Dictionary<string, int[]> Table = new()
    {
        ["AGGRESSOR GRUNT"] = [1, 2, 3],
        ["AGGRESSOR ASSASSIN"] = [2, 3],
        ["AGGRESSOR KNIGHT"] = [3, 4, 5],
        ["AGGRESSOR BALLISTA"] = [3, 4],
        ["AGGRESSOR NINJA"] = [5],

        ["GEO_LOCATOR"] = [1, 2, 3],
        ["SPY_LOCATOR"] = [3, 5],

        ["RESOURCE_CARRIER"] = [1, 2, 3, 4, 5],
        ["APC"] = [2, 4, 5],

        ["TECHNICIAN LAB_TECH"] = [2],
        ["TECHNICIAN TECH_BOY"] = [3],
        ["TECHNICIAN BRAIN_BOX"] = [5],

        ["CONSTRUCTOR DOZER"] = [1],
        ["CONSTRUCTOR BUILDER"] = [3],
        ["CONSTRUCTOR BEHEMOTH"] = [4, 5],

        ["ADMINISTRATOR BOSS"] = [1, 2],
        ["ADMINISTRATOR OVERSEER"] = [2, 3],
        ["ADMINISTRATOR COMMANDER"] = [4, 5],

        ["POD"] = [1],
        ["HARDWARE_LAB MILITARY"] = [1, 3],
        ["HARDWARE_LAB CIVILIAN"] = [1, 3],
        ["SMELTER"] = [1, 3],
        ["FACTORY MILITARY"] = [1, 3, 4, 5],
        ["FACTORY CIVILIAN"] = [1, 3, 5],

        ["MISSILE_EMPLACEMENT TURRET"] = [1, 2, 3],
        ["MISSILE_EMPLACEMENT SENTRY"] = [3, 4],
        ["MISSILE_EMPLACEMENT LAUNCHER"] = [4],
        ["MISSILE_EMPLACEMENT ICBM"] = [5],

        ["GARRISON"] = [1],
        ["MINE"] = [1, 3, 5],
        ["BEACON"] = [1, 3],
    };

    /// <param name="typeName">The words after <c>TYPE</c>, e.g. <c>"AGGRESSOR GRUNT"</c>.</param>
    public static IReadOnlyList<int> For(string typeName) =>
        Table.TryGetValue(typeName, out var levels)
            ? levels
            : throw new KeyNotFoundException($"no hardware level table for '{typeName}'");
}
