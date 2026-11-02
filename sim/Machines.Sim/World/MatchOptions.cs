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
    /// <summary>A campaign mission: its script alone says who won or lost (WON and LOST actions).</summary>
    Scripted,
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

    /// <summary>
    /// Whether active artefacts can be shot and destroyed, as in the original. Recordings made before they could be
    /// say false, so they play back as they were played.
    /// </summary>
    public bool DestructibleArtefacts { get; init; } = true;

    /// <summary>
    /// Whether machines move as in the original: they turn on the spot and speed up before they drive off, give way
    /// to one another and keep their places in a group (MachLogMachineMotionSequencer, MachLogGroupMoveInfo), and a
    /// machine driven in first person speeds up, coasts and slows on hills (MachPhys1stPersonDriver). Recordings
    /// made before say false, so they play back as they were played.
    /// </summary>
    public bool OriginalMotion { get; init; } = true;

    /// <summary>
    /// Whether a captured building changes hands as in the original (MachLogSmelter/MachLogPod::assignToDifferentRace,
    /// MachLogCaptureOperation): a smelter or pod brings its share of the loser's BMUs, a factory pays back what was
    /// spent on the machine it was building, and carriers stop serving a mine or smelter that changed sides
    /// (MachLogResourceCarrier::beNotified). Recordings made before say false, so they play back as they were played.
    /// </summary>
    public bool OriginalCapture { get; init; } = true;

    /// <summary>
    /// Whether the BMU bank keeps the original's rules: a finished smelter or pod an enemy destroys takes its share of
    /// the bank with it, one its own race brings down leaves only what no longer fits (MachLogSmelter/MachLogPod::beHit,
    /// MachLogRaces::reduceCapacityWithPenalty/NoPenalty), and a machine cancelled in the factory pays back three
    /// quarters of what it had cost so far (MachLogFactory::cancelProductionUnit). Recordings made before say false,
    /// so they play back as they were played.
    /// </summary>
    public bool OriginalBank { get; init; } = true;

    /// <summary>
    /// Whether the scenario's RESTRICT_CONSTRUCTION lines keep their building from a race, as in the original
    /// (MachLogScenario::parseRestrictConstruction, MachLogConstructionItem::activationLocked): neither its
    /// constructors' menu nor its AI offers it. Recordings made before say false, so they play back as they were played.
    /// </summary>
    public bool RestrictConstruction { get; init; } = true;

    /// <summary>For a campaign mission: what it takes over from the missions the player won before.</summary>
    public CampaignCarry? Campaign { get; init; }

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

/// <summary>
/// What a campaign mission takes over from the missions the player has won (MachGuiDatabaseHandler): the flags they
/// set (<c>SCENARIO_FLAG name FROM scenario</c>) and the machines that survived them (<c>FROM_SCENARIO</c>).
/// Part of the match options, so recordings and saved games bring a campaign mission back exactly.
/// </summary>
public sealed record CampaignCarry
{
    /// <summary>The flags each won scenario set (SET_SCENARIO_FLAG), by scenario name.</summary>
    public Dictionary<string, List<string>> Flags { get; init; } = [];

    /// <summary>The machines left at the end of each won scenario that saves them (SAVE_MACHINES), by scenario name.</summary>
    public Dictionary<string, List<SurvivingMachine>> Survivors { get; init; } = [];
}

/// <summary>A machine that came through a campaign mission (MachLogProductionUnit as the database keeps it).</summary>
public sealed record SurvivingMachine(Race Race, string Type, string? SubType, int HardwareLevel, string? WeaponCombo);
