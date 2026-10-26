using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>Starting BMUs for every race (MachLog::StartingResources); <see cref="Default"/> keeps the scenario's NBMU.</summary>
public enum StartingBmu
{
    Default,
    Low,
    Medium,
    High,
    VeryHigh,
    SuperHigh,
}

/// <summary>Which research file of the scenario's RESEARCH_ITEMS line is used (MachLog::TechnologyLevel).</summary>
public enum TechLevel
{
    Default,
    Low,
    Medium,
    High,
}

/// <summary>How a skirmish is won (MachLog::VictoryCondition).</summary>
public enum VictoryCondition
{
    /// <summary>The last race with anything left wins.</summary>
    Annihilation,
    /// <summary>A race that loses all its pods is out.</summary>
    Pod,
    /// <summary>When the time is up, the highest score wins.</summary>
    Timer,
}

/// <summary>The skirmish menu's settings, as the original applied them when it created the match.</summary>
public sealed record MatchOptions
{
    public StartingBmu StartingBmu { get; init; } = StartingBmu.Default;
    /// <summary>Mineral sites marked with this level appear, as well as the unmarked ones.</summary>
    public ResourceLevel Resources { get; init; } = ResourceLevel.Default;
    public TechLevel TechLevel { get; init; } = TechLevel.Default;
    public VictoryCondition Victory { get; init; } = VictoryCondition.Annihilation;
    /// <summary>Game length for <see cref="VictoryCondition.Timer"/>, in seconds.</summary>
    public int TimeLimit { get; init; } = 1800;

    /// <summary>The timer choices of the original menu: 5 minutes to 3 hours.</summary>
    public static readonly IReadOnlyList<int> TimeLimits = [300, 600, 900, 1800, 2700, 3600, 5400, 7200, 10800];

    /// <summary>BMUs that replace the scenario's NBMU (MachLogScenario::startingResourcesToInt).</summary>
    public static int? BmuFor(StartingBmu level) => level switch
    {
        StartingBmu.Low => 0,
        StartingBmu.Medium => 100,
        StartingBmu.High => 500,
        StartingBmu.VeryHigh => 1000,
        StartingBmu.SuperHigh => 10000,
        _ => null,
    };
}
