using System.Numerics;
using Machines.Import.Parm;

namespace Machines.Import.Model;

/// <summary>
/// An AI race script (<c>.spl</c>): what the race keeps producing, and its squadrons with their orders.
/// </summary>
/// <param name="Production">Machines the race keeps in stock, from the production list.</param>
/// <param name="Squads">Squadrons in file order; a squad's position is its squadron number.</param>
/// <param name="IgnoredLines">Lines the original skipped, e.g. the unused <c>N_MINES</c> production entries.</param>
public sealed record AiScript(
    IReadOnlyList<ProductionOrder> Production,
    IReadOnlyList<AiSquad> Squads,
    IReadOnlyList<ParmLine> IgnoredLines,
    IReadOnlyList<ScenarioWarning> Warnings);

/// <summary><c>NUMBER count priority type level [subtype] [combo]</c>: keep this many of a machine.</summary>
/// <param name="Priority">0 is built first.</param>
public sealed record ProductionOrder(int Count, int Priority, ResearchKey Machine, int LineNumber);

/// <summary>A <c>SQUAD ... END SQUAD</c> block.</summary>
/// <param name="Defcon"><c>LOW</c>, <c>NORMAL</c> or <c>HIGH</c> when the squad sets it.</param>
public sealed record AiSquad(
    int Number,
    IReadOnlyList<ProductionOrder> Members,
    IReadOnlyList<SquadOperation> Operations,
    string? Defcon,
    int LineNumber);

/// <summary>An <c>AI ...</c> line: the task a squad carries out.</summary>
public abstract record SquadOperation(int LineNumber);

/// <summary><c>AI LOCATE [SITES n] [RANGE_POD r]</c>: find mineral sites near the pod.</summary>
public sealed record LocateOperation(int DesiredSites, float MaxRangeFromPod, int LineNumber) : SquadOperation(LineNumber);

/// <summary><c>AI CONSTRUCT</c>: build the race's planned constructions.</summary>
public sealed record ConstructOperation(int LineNumber) : SquadOperation(LineNumber);

public enum AttackTarget
{
    /// <summary>No target system given: the nearest enemy.</summary>
    Default,
    Resource,
    Research,
    Object,
}

/// <summary><c>AI ATTACK DELAY minutes ...</c>: gather, then attack.</summary>
public sealed record AttackOperation : SquadOperation
{
    public AttackOperation(int lineNumber) : base(lineNumber) { }

    public required float DelayMinutes { get; init; }
    public AttackTarget Target { get; init; }
    /// <summary>For <see cref="AttackTarget.Object"/>: the building or machine type to go for.</summary>
    public string? ObjectType { get; init; }
    public string? ObjectSubType { get; init; }
    public Vector2? StartPoint { get; init; }
    /// <summary>Wait until the squad has at least this many machines.</summary>
    public int? Minimum { get; init; }
    public bool UseNuke { get; init; }
    public bool UseIonCannon { get; init; }
    /// <summary>Index of the race's aggressor assembly point to gather at.</summary>
    public int? AssemblyPoint { get; init; }
    /// <summary>How many times to repeat the attack.</summary>
    public int? DoCount { get; init; }
    public bool NoClusterCheck { get; init; }
}

/// <summary><c>AI DROP_MINES min max POD|START x y</c>: spies lay land mines.</summary>
/// <param name="Start">Where to lay them, or null for around the pod.</param>
public sealed record DropMinesOperation(float MinRange, float MaxRange, Vector2? Start, int LineNumber) : SquadOperation(LineNumber);

/// <summary><c>AI PATROL [DELAY minutes] x y x y ...</c>: walk the path in a loop.</summary>
public sealed record PatrolOperation(float DelayMinutes, IReadOnlyList<Vector2> Path, int LineNumber) : SquadOperation(LineNumber);
