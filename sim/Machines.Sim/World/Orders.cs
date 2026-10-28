using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>What a machine is currently doing.</summary>
public abstract record Order
{
    public static readonly Order Idle = new IdleOrder();
}

public sealed record IdleOrder : Order;

public sealed record MoveOrder(Vector2 Target) : Order;

/// <summary>
/// Attack one target until it dies, then continue to <paramref name="Then"/> if given: fighting on the way, or with
/// <paramref name="Resume"/> just moving on (a machine that broke off its move for the fight).
/// </summary>
public sealed record AttackOrder(int Target, Vector2? Then = null, bool Resume = false) : Order;

/// <summary>Stay put and shoot at what comes into range, never moving to it (MachLogStandGroundOperation).</summary>
public sealed record StandGroundOrder : Order;

/// <summary>For a spy locator: drive to each point in turn and lay a land mine there (MachLogDropLandMineOperation).</summary>
public sealed record DropMinesOrder(IReadOnlyList<Vector2> Points) : Order;

/// <summary>
/// For a spy locator: go to the nearest own garrison for a full load of mines, then back to where it was
/// (MachLogRefillLandMinesOperation).
/// </summary>
public sealed record RefillMinesOrder(int Garrison, Vector2 ReturnTo, float? InsideSince = null) : Order;

/// <summary>
/// Getting out of danger (MachLogEvadeOperation): Resume is what it was doing, to take up again (with the end of its
/// way, ResumeTo); LastThreat when something last threatened it; Goal and Refuge where it is going.
/// </summary>
public sealed record EvadeOrder(Order Resume, Vector2? ResumeTo, float LastThreat, EvadeGoal Goal, int? Refuge) : Order;

/// <summary>Move to a point, fighting anything met on the way.</summary>
public sealed record AttackMoveOrder(Vector2 Target) : Order;

/// <summary>Walk to a construction site and build it.</summary>
public sealed record ConstructOrder(int Construction) : Order;

/// <summary>
/// Walk to a point, driving over the deposits on the list (those in scanner range of the way) to find them first.
/// </summary>
public sealed record LocateOrder(Vector2 Target, IReadOnlyList<int> Sites) : Order;

/// <summary>
/// Shuttle ore from a mine to a smelter or pod, forever: with <paramref name="Delivering"/> on the way to drop
/// what it carries, else on the way to load; until <paramref name="WaitUntil"/> it stands on the pad loading or
/// dropping (MachLogTransportOperation).
/// </summary>
public sealed record TransportOrder(int Mine, int Smelter, bool Delivering = false, float WaitUntil = 0) : Order;

/// <summary>Walk into a lab and work there as a researcher.</summary>
public sealed record EnterLabOrder(int Lab) : Order;

/// <summary>Walk the points in a loop.</summary>
public sealed record PatrolOrder(IReadOnlyList<Vector2> Points, int Next) : Order;

/// <summary>Restore a friendly machine's hit points with a supercharger (administrators).</summary>
public sealed record HealOrder(int Target) : Order;

/// <summary>Restore a friendly building's hit points, paying BMUs (constructors).</summary>
public sealed record RepairOrder(int Building) : Order;

/// <summary>Wear an enemy building down to a third of its hit points and take it over (constructors).</summary>
public sealed record CaptureOrder(int Building) : Order;

/// <summary>Take a building apart for half its BMUs back (constructors).</summary>
public sealed record DeconstructOrder(int Building) : Order;

/// <summary>Drive to a smelter or pod and be melted down for half the machine's cost.</summary>
public sealed record RecycleOrder(int Smelter) : Order;

/// <summary>Drive into a garrison to be repaired.</summary>
public sealed record EnterGarrisonOrder(int Garrison) : Order;

/// <summary>Drive to an APC and get in.</summary>
public sealed record BoardOrder(int Apc) : Order;

/// <summary>For a scavenger: drive onto each wreck in turn and gather its BMUs (MachLogScavengeOperation).</summary>
public sealed record ScavengeOrder(IReadOnlyList<int> Debris) : Order;

/// <summary>For an APC: drive to a point and let everyone out there.</summary>
public sealed record DeployOrder(Vector2 Target) : Order;

public enum Controller
{
    Player,
    Ai,
}

/// <summary>A race's bank, research and standing.</summary>
public sealed class RaceState(Race race, Controller controller)
{
    public Race Race { get; } = race;
    public Controller Controller { get; set; } = controller;

    /// <summary>BMUs in the bank. The original counts whole BMUs; fractions accumulate here between ticks.</summary>
    public float Bmu { get; set; }

    public HashSet<ResearchKey> Available { get; } = [];
    public HashSet<ResearchKey> Researched { get; } = [];
    public Dictionary<ResearchKey, float> ResearchProgress { get; } = [];
    public Dictionary<ResearchKey, int> ResearchCostOverrides { get; } = [];

    public bool Defeated { get; set; }
    public bool StartedWithPod { get; set; }

    public Score Score { get; } = new();
}

/// <summary>The original's score (machlog/score.cpp); it decides timed games.</summary>
public sealed class Score
{
    /// <summary>Every BMU added to the bank: mining, the pod trickle and returned ore.</summary>
    public float BmusMined { get; set; }
    public float ResearchUnits { get; set; }
    /// <summary>Full cost of enemy actors destroyed, less half the cost of own actors lost to enemies.</summary>
    public int Destruction { get; set; }

    public int Gross => (int)(BmusMined / 4) + (int)(ResearchUnits / 2) + Destruction;

    // The statistics screen's figures (MachLogRace's counts, shown by MachGuiCtxStatistics).
    public int MachinesBuilt { get; set; }
    public int MilitaryMachinesBuilt { get; set; }
    public int ConstructionsBuilt { get; set; }
    public int ItemsResearched { get; set; }
    public int MachinesLost { get; set; }
    public int ConstructionsLost { get; set; }

    /// <summary>The machines and buildings of each race this one destroyed.</summary>
    public Dictionary<Race, int> MachinesDestroyed { get; } = [];
    public Dictionary<Race, int> ConstructionsDestroyed { get; } = [];
}

/// <summary>Something the client may want to show or play a sound for.</summary>
public enum GameEventKind
{
    Fired,
    Hit,
    Destroyed,
    ConstructionComplete,
    MachineBuilt,
    ResearchComplete,
    SiteDiscovered,
    RaceDefeated,
    /// <summary>A machine was melted down at a smelter or pod (Other: the smelter).</summary>
    Recycled,
    /// <summary>An actor changed sides: captured or turned by treachery (Detail: the new race).</summary>
    RaceChanged,
    /// <summary>An area weapon went off (Detail: the weapon).</summary>
    Blast,
    /// <summary>A machine got into an APC (Other: the APC).</summary>
    Boarded,
    /// <summary>An APC let its passengers out (Detail: how many).</summary>
    Deployed,
    /// <summary>A scavenger picked up a wreck (Other: the wreck, Detail: its BMUs).</summary>
    Scavenged,
    /// <summary>A round struck the ground: a hill in its way (Actor: the shooter, Detail: the weapon).</summary>
    Impact,
    /// <summary>A spy locator laid a land mine (Other: the mine).</summary>
    MineLaid,
    /// <summary>A land mine went off under a machine (Actor: the machine, Other: the mine).</summary>
    MineExploded,
    /// <summary>A spy finished downloading research in an enemy lab (Actor: the spy, Other: the lab, Detail: the item).</summary>
    ResearchStolen,
}

public sealed record GameEvent(GameEventKind Kind, int Actor, Vector2 Position, int? Other = null, string? Detail = null);
