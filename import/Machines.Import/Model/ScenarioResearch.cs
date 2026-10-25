using Machines.Import.Parm;

namespace Machines.Import.Model;

/// <summary>Which races a block of an <c>.rsi</c> file applies to.</summary>
public enum RaceTarget
{
    Red,
    Blue,
    Green,
    Yellow,
    /// <summary>Every race controlled by the AI. Also the default before the first <c>RACE</c> line.</summary>
    Ai,
    /// <summary>Every race controlled by a player.</summary>
    Pc,
    /// <summary>Every race; any <c>RACE</c> word the original does not know (the files use <c>ALL</c>).</summary>
    All,
}

/// <summary>One <c>MACHINE</c> or <c>CONSTRUCTION</c> line: the item becomes available, and maybe researched.</summary>
public sealed record ScenarioResearchEntry(
    RaceTarget Race,
    ResearchKind Kind,
    bool Researched,
    ResearchKey Key,
    int? ResearchCostOverride,
    int? BuildingCostOverride,
    int LineNumber);

/// <summary>A scenario's <c>.rsi</c> file.</summary>
/// <param name="IgnoredLines">Lines the original skipped because they start with an unknown word.</param>
public sealed record ScenarioResearch(IReadOnlyList<ScenarioResearchEntry> Entries, IReadOnlyList<ParmLine> IgnoredLines);
