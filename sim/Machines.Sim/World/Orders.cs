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

/// <summary>Attack one target until it dies, then continue to <paramref name="Then"/> if given.</summary>
public sealed record AttackOrder(int Target, Vector2? Then = null) : Order;

/// <summary>Move to a point, fighting anything met on the way.</summary>
public sealed record AttackMoveOrder(Vector2 Target) : Order;

/// <summary>Walk to a construction site and build it.</summary>
public sealed record ConstructOrder(int Construction) : Order;

/// <summary>Walk to a point, finding mineral sites within scanner range on the way and there.</summary>
public sealed record LocateOrder(Vector2 Target) : Order;

/// <summary>Shuttle ore from a mine to a smelter or pod, forever.</summary>
public sealed record TransportOrder(int Mine, int Smelter) : Order;

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

public enum Controller
{
    Player,
    Ai,
}

/// <summary>A race's bank, research and standing.</summary>
public sealed class RaceState(Race race, Controller controller)
{
    public Race Race { get; } = race;
    public Controller Controller { get; } = controller;

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
}

public sealed record GameEvent(GameEventKind Kind, int Actor, Vector2 Position, int? Other = null, string? Detail = null);
