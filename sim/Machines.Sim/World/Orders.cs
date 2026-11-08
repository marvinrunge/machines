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

/// <summary>Stay with a machine for as long as it lives (MachLogFollowOperation).</summary>
public sealed record FollowOrder(int Target) : Order;

/// <summary>
/// Attack one target until it dies, then continue to <paramref name="Then"/> if given, fighting on the way, or take up
/// again <paramref name="Back"/>, what the fight broke off.
/// </summary>
public sealed record AttackOrder(int Target, Vector2? Then = null, Interrupted? Back = null) : Order;

/// <summary>
/// What a fight put aside (MachLogCounterattackOperation's cached operation): the order, with the end of its way. One
/// that <paramref name="Chases"/> gives up a target it cannot catch: <paramref name="TargetLastNotEvading"/> is when
/// the target was last not running from it, and at <paramref name="NextClosingCheck"/> it looks whether it has closed on
/// the <paramref name="BeyondRange"/> metres it was beyond its weapons' reach at the last look.
/// </summary>
public sealed record Interrupted(Order Order, Vector2? To, bool Chases, float TargetLastNotEvading = 0, float NextClosingCheck = 0, float BeyondRange = 0);

/// <summary>
/// Back to where it stood before a fight of its own: from <paramref name="Until"/>, the end of its
/// <paramref name="Pause"/>, it drives to <paramref name="To"/> and turns to <paramref name="Facing"/>
/// (MachLogMoveAndTurnOperation).
/// </summary>
public sealed record ReturnOrder(Vector2 To, Vector2 Facing, float Pause, float Until = 0, bool Moving = false) : Order;

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
/// dropping (MachLogTransportOperation). With <paramref name="Round"/> it calls at each of those mines in turn, on to
/// the smelter once it is full or has been to the last (MachLogResourceCarrier's suppliers).
/// </summary>
public sealed record TransportOrder(int Mine, int Smelter, bool Delivering = false, float WaitUntil = 0, IReadOnlyList<int>? Round = null, bool Assigned = true) : Order;

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
public sealed record ScavengeOrder(IReadOnlyList<int> Debris, Interrupted? Back = null) : Order;

/// <summary>For an APC: drive to a point and let everyone out there.</summary>
public sealed record DeployOrder(Vector2 Target, float NextOut = 0, int Out = 0) : Order;

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

    /// <summary>The bank running low or full is told at most every two minutes (MachLogRaces::smartSubtractBMUs, smartAddBMUs).</summary>
    public float NextLowBankMail { get; set; }

    public float NextFullBankMail { get; set; }

    /// <summary>The mineral sites this race has been told have run dry.</summary>
    public HashSet<int> DrySitesTold { get; } = [];

    public HashSet<ResearchKey> Available { get; } = [];
    public HashSet<ResearchKey> Researched { get; } = [];
    public Dictionary<ResearchKey, float> ResearchProgress { get; } = [];
    public Dictionary<ResearchKey, int> ResearchCostOverrides { get; } = [];

    /// <summary>
    /// The buildings the scenario's RESTRICT_CONSTRUCTION lines keep from this race, whatever its constructors
    /// could raise (MachLogConstructionItem::activationLocked). Only these entries: those after them in
    /// <c>global.cst</c> stay open. <see cref="GameWorld.Buildable"/> leaves them out while
    /// <see cref="MatchOptions.RestrictConstruction"/> holds.
    /// </summary>
    public HashSet<ConstructionOrderEntry> Locked { get; } = [];

    /// <summary>How far down <c>global.cst</c> the race's constructors have reached; what they opened stays open without them.</summary>
    public int ConstructionsReached { get; set; } = -1;

    public bool Defeated { get; set; }
    public bool StartedWithPod { get; set; }

    /// <summary>The level of beacon the scenario's VIRTUAL_BEACON gives the race without building one; 0 for none.</summary>
    public int VirtualBeacon { get; set; }

    /// <summary>False for a race the scenario gives SCORE OFF: the statistics leave it out (MachLogRaces::removeNotDisplayedScores).</summary>
    public bool ScoreDisplayed { get; set; } = true;

    /// <summary>
    /// The races this one takes for allies (the scenario's ALLY lines, MachLogRaces::dispositionToRace): it does not
    /// attack them. The disposition is one race's towards another, as in the original.
    /// </summary>
    public HashSet<Race> Allies { get; } = [];

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
    /// <summary>The mission script sent a voice mail (Other: the race it is for, Detail: its VID, e.g. <c>VID_1O1A</c>).</summary>
    VoiceMail,
    /// <summary>The mission script set a task (Other: its number from 1, Detail: <c>COMPLETE</c> or <c>OPEN</c>).</summary>
    TaskChanged,
    /// <summary>The mission script set off an explosion (Detail: its SIZE in metres).</summary>
    Explosion,
    /// <summary>
    /// A race took another for an ally or no longer (Actor: the race, as a number; Other: the race it is towards;
    /// Detail: <c>ALLY</c> or <c>NEUTRAL</c>).
    /// </summary>
    AllianceChanged,

    /// <summary>A wave of a Wired run began (Other: the wave; Detail: its boss's name, if it has one).</summary>
    WiredWaveStarted,

    /// <summary>The last enemy of a wave is gone (Other: the wave; Detail: the BMUs of the bonus).</summary>
    WiredWaveCleared,

    /// <summary>A boss arrived (Actor: the boss; Detail: its name).</summary>
    WiredBossAppeared,

    /// <summary>A boss fell below half its hit points and called for help (Actor: the boss; Detail: its name).</summary>
    WiredBossBelowHalf,

    /// <summary>The hero took up a wreck (Actor: the hero; Position: the wreck's; Other: the wreck's id; Detail: the BMUs).</summary>
    WiredWreckCollected,

    /// <summary>The hero took up a crate (Actor: the hero; Position: the crate's; Other: the crate's id; Detail: the catalogue id of its content).</summary>
    WiredCrateCollected,

    /// <summary>The hero fell (Actor: the machine it was; Other: its race; Detail: the spare bodies left).</summary>
    WiredHeroDied,

    /// <summary>The hero stands at the pod again (Actor: its new machine; Other: its race; Detail: the spare bodies left).</summary>
    WiredHeroRespawned,

    /// <summary>A Wired run is over (Detail: the name of its <c>WiredEnding</c>).</summary>
    WiredRunEnded,

    /// <summary>A hero has chips to choose from after a boss (Other: its race).</summary>
    WiredChipChoiceReady,

    /// <summary>A machine that hung was set down nearer the pod (Actor: the machine; Position: where it stands now).</summary>
    WiredFoePulled,

    /// <summary>The hero used an ability (Actor: the hero; Position: where it was aimed; Other: its slot; Detail: its catalogue id).</summary>
    WiredAbilityUsed,

    /// <summary>The hero's thrusters fired (Actor: the hero; Position: where it stood).</summary>
    WiredDashed,

    /// <summary>A special wave began (Other: the wave; Detail: the name of its <c>WiredSpecial</c>).</summary>
    WiredSpecialWave,

    /// <summary>An ion strike was announced (Position: where it comes down; see <c>WiredRun.Strikes</c> for when and how wide).</summary>
    WiredIonWarning,

    /// <summary>A machine the hero had turned went back (Actor: the machine; Position: where it stands).</summary>
    WiredFoeWentBack,

    /// <summary>A chip kept the hero from falling (Actor: the hero; Position: where it stands; Detail: the chip).</summary>
    WiredHeroSaved,

    /// <summary>A destroyed machine burst into shrapnel (Actor: the machine; Position: where it stood).</summary>
    WiredShrapnel,
}

/// <param name="Mark">Where a round fired in first person really went, height and all, when that is not under the crosshair (a Wired run).</param>
public sealed record GameEvent(GameEventKind Kind, int Actor, Vector2 Position, int? Other = null, string? Detail = null, Vector3? Mark = null);
