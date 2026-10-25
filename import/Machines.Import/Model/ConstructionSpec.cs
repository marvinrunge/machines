using Machines.Import.Parm;

namespace Machines.Import.Model;

/// <summary>One building type from the <c>CONSTRUCTIONS</c> section, e.g. <c>FACTORY MILITARY</c>.</summary>
public sealed record ConstructionSpec(string Type, string? SubType, IReadOnlyList<ConstructionLevel> Levels)
{
    public string TypeName => SubType is null ? Type : $"{Type} {SubType}";

    public ConstructionLevel Level(int hardwareLevel) =>
        Levels.FirstOrDefault(l => l.HardwareLevel == hardwareLevel)
        ?? throw new KeyNotFoundException($"{TypeName} has no hardware level {hardwareLevel}");
}

/// <summary>The stats of one building at one hardware level. Rates are per minute.</summary>
public sealed record ConstructionLevel
{
    public required int HardwareLevel { get; init; }

    public required int Cost { get; init; }
    public required int ResearchCost { get; init; }
    public required int HitPoints { get; init; }
    public required int Armour { get; init; }

    public int? AccessSizeIndex { get; init; }
    /// <summary>BMU storage for smelters and the pod, ore storage for mines.</summary>
    public int? Capacity { get; init; }
    /// <summary>BMUs per minute a factory spends on the machine it is building.</summary>
    public int? BuildRate { get; init; }
    /// <summary>Ore per minute a mine (or the pod) extracts.</summary>
    public int? ExtractionRate { get; init; }
    public int NumberOfWeapons { get; init; }
    public int? MaxRotation { get; init; }
    public int? Accuracy { get; init; }
    public float? RepairRate { get; init; }
    /// <summary>Squared distance a smelter must keep from mines.</summary>
    public int? MineDistance { get; init; }
    public int? MineralDistance { get; init; }

    /// <summary>
    /// Footprint, doors, stations and drop-off points for this level
    /// (<c>BOUNDARY</c>, <c>ENTRANCE</c>, <c>STATION</c>, ...), with the <c>LEVEL_n</c> token removed.
    /// </summary>
    public required IReadOnlyList<ParmLine> Geometry { get; init; }

    /// <summary>Every per-level keyword as written, one value each.</summary>
    public required IReadOnlyDictionary<string, string> Values { get; init; }
}
