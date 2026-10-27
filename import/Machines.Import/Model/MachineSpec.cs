namespace Machines.Import.Model;

public enum Locomotion
{
    Wheels,
    Tracks,
    Spider,
    Hover,
    Glider,
}

/// <summary>A building type and level, e.g. the most advanced construction a constructor can raise.</summary>
public sealed record ConstructionRef(string Type, string SubType, int HardwareLevel);

/// <summary>
/// One machine type from the <c>MACHINES</c> section, e.g. <c>AGGRESSOR GRUNT</c>,
/// with one <see cref="MachineLevel"/> per hardware level it exists at.
/// </summary>
public sealed record MachineSpec(
    string Type,
    string? SubType,
    IReadOnlyList<MachineLevel> Levels,
    IReadOnlyList<int> SoftwareCosts,
    IReadOnlyList<int> SoftwareResearchCosts)
{
    public string TypeName => SubType is null ? Type : $"{Type} {SubType}";

    public MachineLevel Level(int hardwareLevel) =>
        Levels.FirstOrDefault(l => l.HardwareLevel == hardwareLevel)
        ?? throw new KeyNotFoundException($"{TypeName} has no hardware level {hardwareLevel}");
}

/// <summary>
/// The stats of one machine at one hardware level. Costs are in BMUs, rates are per minute,
/// speeds in metres per second, angles in degrees.
/// Keywords not modelled here stay readable through <see cref="Values"/>.
/// </summary>
public sealed record MachineLevel
{
    public required int HardwareLevel { get; init; }

    public required int Cost { get; init; }
    public required int ResearchCost { get; init; }

    public required int HitPoints { get; init; }
    public required int Armour { get; init; }
    /// <summary>Armour regained per second while damaged.</summary>
    public required float RepairRate { get; init; }

    public required float Speed { get; init; }
    public required float TaskSpeed { get; init; }
    public required float Acceleration { get; init; }
    public required float RotationSpeed { get; init; }
    public required float RotationAcceleration { get; init; }
    public required Locomotion Locomotion { get; init; }
    public float? LocomotionParameter { get; init; }
    public required float HighClearance { get; init; }
    public required float LowClearance { get; init; }
    public required int Volume { get; init; }
    public required int MinAccessSizeIndex { get; init; }

    public required int ScannerRange { get; init; }
    /// <summary>Name of the <see cref="EvasionStrategy"/> this machine uses.</summary>
    public required string Evasion { get; init; }

    public int NumberOfWeapons { get; init; }
    public int? Accuracy { get; init; }
    public string? BodyTurnLink { get; init; }
    public float? BodyTurnRate { get; init; }
    public bool NightVision { get; init; }

    /// <summary>Ore load for transporters and total machine volume for APCs. Constructors carry a value too.</summary>
    public int? Capacity { get; init; }
    public int? MaxSubordinates { get; init; }
    public int? ResearchRate { get; init; }
    public int? DownloadRate { get; init; }
    /// <summary>Land mines a spy locator carries (MINE_CAPACITY).</summary>
    public int? MineCapacity { get; init; }
    public int? ConstructionRate { get; init; }
    public int? MineralGrade { get; init; }
    public ConstructionRef? MostAdvancedConstruction { get; init; }

    /// <summary>Every keyword of this level as written, one value each.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}
