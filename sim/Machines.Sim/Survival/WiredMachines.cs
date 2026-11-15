using Machines.Import.Model;

namespace Machines.Sim.Survival;

/// <summary>
/// A hardware level Wired adds to a machine type of the original: Sarge is Grunt 4, Thresher Reaper 4. It is the level
/// <paramref name="From"/> of the same type with its own values; what is not given here (locomotion, evasion, weapons'
/// links) stays as there. <paramref name="Size"/> is how much bigger its model is (tools/wired-models), which widens its
/// clearance and so its radius. <paramref name="Name"/> is what the game calls it, in English. Speeds and turns carry their accelerations along in proportion.
/// </summary>
public sealed record WiredLevel(string TypeName, int Level, int From, int HitPoints, int Armour, float Repair, float Speed,
    float Turn, int Accuracy, int Scanner, int Cost, float Size, string Name);

/// <summary>
/// The machines Wired adds to the original's: new levels of its types, put into the game data as it is loaded. No
/// research, factory or scenario of the original names them, so a match of the original's kind never meets them; only
/// Wired's catalogue (<see cref="WiredCatalogue"/>) does. Wired is the remake's own addition; none of these is original.
/// </summary>
public static class WiredMachines
{
    /// <summary>The values of the concept (renders/concepts/endgame), the starting point for balancing.</summary>
    public static readonly IReadOnlyList<WiredLevel> Levels =
    [
        new("AGGRESSOR GRUNT", 4, 3, 110, 70, 1.0f, 10.5f, 70, 65, 130, 200, 1.15f, "Sarge"),
        new("AGGRESSOR GRUNT", 5, 3, 380, 200, 1.6f, 9.5f, 60, 65, 130, 480, 1.5f, "Old Iron"),
        new("AGGRESSOR ASSASSIN", 4, 3, 120, 90, 1.0f, 13, 90, 50, 130, 220, 1.2f, "Thresher"),
        new("AGGRESSOR ASSASSIN", 5, 3, 330, 200, 1.4f, 12, 90, 55, 140, 550, 1.45f, "Harvester"),
        new("AGGRESSOR ASSASSIN", 6, 3, 110, 70, 0.8f, 21, 120, 90, 200, 420, 1.2f, "Widow"),
        new("AGGRESSOR BALLISTA", 5, 4, 420, 260, 2.4f, 4.5f, 60, 65, 200, 650, 1.25f, "Colossus"),
        new("ADMINISTRATOR BOSS", 3, 1, 120, 90, 0.4f, 20, 300, 60, 180, 300, 1.4f, "Hornet"),
        new("ADMINISTRATOR COMMANDER", 6, 5, 210, 130, 1.4f, 16, 360, 85, 170, 600, 1.25f, "Marshal"),
    ];

    /// <summary>Whether a type and level is one of Wired's own, not the original's.</summary>
    public static bool IsWired(string typeName, int level) => Find(typeName, level) is not null;

    /// <summary>Wired's own level of a type, or null for one of the original's.</summary>
    public static WiredLevel? Find(string typeName, int level) => Levels.FirstOrDefault(l => l.TypeName == typeName && l.Level == level);

    /// <summary>The original's data with Wired's levels added to their types; a level the data has already stays as it is.</summary>
    public static ParmData Extend(ParmData parm) => parm with
    {
        Machines = parm.Machines.Select(spec =>
        {
            var added = Levels.Where(l => l.TypeName == spec.TypeName && spec.Levels.All(o => o.HardwareLevel != l.Level))
                .Select(l => Made(l, spec.Level(l.From))).ToList();
            return added.Count == 0 ? spec : spec with { Levels = [.. spec.Levels, .. added] };
        }).ToList(),
    };

    private static MachineLevel Made(WiredLevel wired, MachineLevel basis)
    {
        var (move, turn) = (wired.Speed / basis.Speed, wired.Turn / basis.RotationSpeed);
        return basis with
        {
            HardwareLevel = wired.Level,
            Cost = wired.Cost,
            HitPoints = wired.HitPoints,
            Armour = wired.Armour,
            RepairRate = wired.Repair,
            Speed = wired.Speed,
            TaskSpeed = basis.TaskSpeed * move,
            Acceleration = basis.Acceleration * move,
            RotationSpeed = wired.Turn,
            RotationAcceleration = basis.RotationAcceleration * turn,
            Accuracy = wired.Accuracy,
            ScannerRange = wired.Scanner,
            HighClearance = basis.HighClearance * wired.Size,
            LowClearance = basis.LowClearance * wired.Size,
        };
    }
}
