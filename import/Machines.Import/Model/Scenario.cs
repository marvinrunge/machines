using System.Numerics;
using Machines.Import.Parm;

namespace Machines.Import.Model;

public enum Race
{
    Red,
    Blue,
    Green,
    Yellow,
}

/// <summary>Who plays a race when the scenario is started from the campaign (<c>RACES_DEFAULT</c>).</summary>
public enum DefaultController
{
    /// <summary>Not listed or listed as <c>none</c>: the race does not take part.</summary>
    None,
    Player,
    Ai,
}

/// <summary>Which game setting a mineral site belongs to; <see cref="Default"/> sites appear in every setting.</summary>
public enum ResourceLevel
{
    Default,
    Low,
    Medium,
    High,
}

/// <summary>A <c>.scn</c> file: everything needed to set up one map.</summary>
public sealed record Scenario
{
    public required IReadOnlyDictionary<Race, DefaultController> DefaultPlayers { get; init; }
    public required IReadOnlyList<MineralSite> MineralSites { get; init; }
    public required IReadOnlyList<RaceSetup> Races { get; init; }
    public ResearchFiles? Research { get; init; }
    public ArtefactSection? Artefacts { get; init; }
    public required IReadOnlyList<ScenarioCondition> Conditions { get; init; }
    public required IReadOnlyList<ScenarioAction> Actions { get; init; }
    public required IReadOnlyList<ConstructionRestriction> ConstructionRestrictions { get; init; }

    /// <summary>Lines outside any section, which the original skipped (e.g. <c>#include</c>).</summary>
    public required IReadOnlyList<ParmLine> IgnoredLines { get; init; }

    /// <summary>Places where the file is malformed but the original read it anyway, and how.</summary>
    public required IReadOnlyList<ScenarioWarning> Warnings { get; init; }

    public RaceSetup? Race(Race race) => Races.FirstOrDefault(r => r.Race == race);
}

public sealed record ScenarioWarning(int LineNumber, string Message)
{
    public override string ToString() => $"line {LineNumber}: {Message}";
}

/// <param name="Grade">1 to 4.</param>
/// <param name="Ore">Ore in the site, in BMUs.</param>
public sealed record MineralSite(int Grade, int Ore, Vector3 Position, ResourceLevel Level, int LineNumber);

/// <summary>The <c>.rsi</c> files to use per technology-level setting; the first one is the default.</summary>
public sealed record ResearchFiles(string Default, string Low, string Medium, string High);

/// <summary>A <c>RACE ... ENDRACE</c> block.</summary>
public sealed record RaceSetup
{
    public required Race Race { get; init; }
    /// <summary>Path of the <c>.spl</c> AI script, or null when the file says <c>none</c>.</summary>
    public string? AiScript { get; init; }
    public CameraStart? Camera { get; init; }
    public int? Bmu { get; init; }
    public int? Ore { get; init; }
    public bool ScoreDisplayed { get; init; } = true;
    public required IReadOnlyList<Race> Allies { get; init; }
    /// <summary><c>LEVEL_1_BEACON</c> or <c>LEVEL_3_BEACON</c>: radar coverage without building a beacon.</summary>
    public string? VirtualBeacon { get; init; }
    public required IReadOnlyList<Vector2> AggressorAssemblyPoints { get; init; }
    public required IReadOnlyList<Vector2> AdministratorAssemblyPoints { get; init; }
    public required IReadOnlyList<ScenarioMachine> Machines { get; init; }
    public required IReadOnlyList<ScenarioConstruction> Constructions { get; init; }
    public required int LineNumber { get; init; }
}

/// <param name="Azimuth">Degrees.</param>
/// <param name="Elevation">Degrees.</param>
/// <param name="Roll">Degrees.</param>
/// <param name="Type"><c>ZENITH</c>, <c>GROUND</c>, <c>FREE_MOVING</c> or <c>THIRD_PERSON</c>.</param>
public sealed record CameraStart(Vector3 Position, float Azimuth, float Elevation, float Roll, string Type);

/// <summary>A machine placed at the start. Type names use the <c>parmdata.dat</c> spelling.</summary>
public sealed record ScenarioMachine
{
    public required string Type { get; init; }
    public string? SubType { get; init; }
    public required int HardwareLevel { get; init; }
    public required int SoftwareLevel { get; init; }
    public required Vector3 Position { get; init; }
    public string? WeaponCombo { get; init; }
    /// <summary>AI squadron the machine starts in. The original only applies it to AI races.</summary>
    public int? Squad { get; init; }
    /// <summary>Set inside a <c>DISPLAY AS</c> block: the machine looks like another race's.</summary>
    public Race? DisplayAs { get; init; }
    public required int LineNumber { get; init; }

    public string TypeName => SubType is null ? Type : $"{Type} {SubType}";
}

/// <summary>A building placed at the start, or (with <see cref="NotBuilt"/>) a plan for the AI to build.</summary>
public sealed record ScenarioConstruction
{
    public required string Type { get; init; }
    public string? SubType { get; init; }
    public required int HardwareLevel { get; init; }
    public required Vector3 Position { get; init; }
    /// <summary>0, 90, 180 or 270 degrees (negative values are allowed and mean the same).</summary>
    public required int Orientation { get; init; }
    /// <summary>As written; the original ignores weapon combos on buildings.</summary>
    public string? WeaponCombo { get; init; }
    /// <summary>The AI rebuilds it when destroyed. Only the exact word <c>REBUILD</c> counts.</summary>
    public bool Rebuild { get; init; }
    /// <summary>Not placed; an AI race stores it as a building plan.</summary>
    public bool NotBuilt { get; init; }
    public bool ActivateIonCannon { get; init; }
    public Race? DisplayAs { get; init; }
    /// <summary>Other places the AI may use for this plan (<c>ALTERNATIVE_SITE</c> lines after it).</summary>
    public required IReadOnlyList<(Vector3 Position, int Orientation)> AlternativeSites { get; init; }
    /// <summary>Words after the fixed fields that the original does not recognise, e.g. the typo <c>REBUILT</c>.</summary>
    public required IReadOnlyList<string> UnknownWords { get; init; }
    public required int LineNumber { get; init; }

    public string TypeName => SubType is null ? Type : $"{Type} {SubType}";
}

/// <summary>The <c>ARTEFACTS</c> section: scenery and destructible objects.</summary>
public sealed record ArtefactSection(
    string? ModelsPath,
    IReadOnlyList<ArtefactType> Types,
    IReadOnlyList<ArtefactPlacement> Placements,
    IReadOnlyList<ArtefactOnDestroy> OnDestroy);

/// <summary>
/// <c>DUMB</c> artefacts are scenery. <c>ACTIVE</c> ones can be damaged and destroyed and carry stats.
/// </summary>
public sealed record ArtefactType(string Name, bool Active, int? Cost, int? HitPoints, int? Armour, int? StringId, float? Height, IReadOnlyList<Vector2> Boundary);

public sealed record ArtefactPlacement(string TypeName, Vector3 Position, float Orientation, int? Id, int LineNumber);

/// <summary>When artefact <see cref="Id"/> is destroyed, apply this damage, HP and armour change around it.</summary>
public sealed record ArtefactOnDestroy(int Id, int Damage, int HitPoints, int Armour, int LineNumber);

/// <summary>
/// A named condition from the <c>CONDITIONS</c> block, e.g. <c>TIMER apctimer 20</c>:
/// <see cref="Type"/> = <c>TIMER</c>, <see cref="Name"/> = <c>apctimer</c>, <see cref="Args"/> = <c>[20]</c>.
/// </summary>
public sealed record ScenarioCondition(string Type, string Name, IReadOnlyList<string> Args, int LineNumber)
{
    /// <summary>Names of other conditions this one combines (<c>ANY</c>, <c>ALL</c>, <c>NOT</c>).</summary>
    public IReadOnlyList<string> ReferencedConditions =>
        Type is "ANY" or "ALL" or "NOT" ? Args : [];
}

/// <summary>
/// An action from the <c>ACTIONS</c> block: <c>TYPE ON condition args...</c>.
/// <c>REINFORCEMENTS</c> carries the lines up to its <c>END</c> in <see cref="Body"/>.
/// </summary>
public sealed record ScenarioAction(string Type, string Condition, bool Enabled, IReadOnlyList<string> Args, IReadOnlyList<ParmLine> Body, int LineNumber);

/// <summary><c>RESTRICT_CONSTRUCTION RACE RED FACTORY CIVILIAN 3</c>: the race may not build this.</summary>
public sealed record ConstructionRestriction(RaceTarget Race, string Type, string? SubType, int HardwareLevel, int LineNumber);
