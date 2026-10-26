namespace Machines.Import.Model;

/// <summary>
/// <c>sceninfo.dat</c>: what the campaign, skirmish and multiplayer menus offer, and in which order.
/// </summary>
public sealed record ScenarioCatalogue(
    IReadOnlyList<StarSystem> Campaign,
    IReadOnlyList<SkirmishTerrain> Skirmish,
    IReadOnlyList<SkirmishTerrain> Multiplayer)
{
    public IEnumerable<CatalogueScenario> CampaignScenarios => Campaign.SelectMany(s => s.Planets).SelectMany(p => p.Scenarios);

    public IEnumerable<CatalogueScenario> AllScenarios =>
        CampaignScenarios.Concat(Skirmish.SelectMany(t => t.Scenarios)).Concat(Multiplayer.SelectMany(t => t.Scenarios));
}

/// <param name="StringId">Id of the menu name (<c>IDS</c>).</param>
/// <param name="Text">Description text file.</param>
/// <param name="Flic">Intro video.</param>
/// <param name="After">Entries (systems, planets or scenarios) that must be completed to unlock this one.</param>
public sealed record StarSystem(string Name, int StringId, string? Text, string? Flic, IReadOnlyList<string> After, IReadOnlyList<CampaignPlanet> Planets, int LineNumber);

public sealed record CampaignPlanet(string Name, int StringId, string? Text, string? Flic, IReadOnlyList<string> After, IReadOnlyList<CatalogueScenario> Scenarios, int LineNumber);

public enum MapSize
{
    Small,
    Medium,
    Large,
}

/// <summary>A skirmish or multiplayer terrain group, e.g. "ice", with its scenarios.</summary>
public sealed record SkirmishTerrain(MapSize Size, string Name, int StringId, string? Text, IReadOnlyList<CatalogueScenario> Scenarios, int LineNumber);

/// <summary>
/// One menu entry. <see cref="Name"/> is the <c>.scn</c> file, <see cref="Planet"/> the planet folder; the same
/// scenario can be listed on several planets (e.g. a day and a night version of the map).
/// </summary>
/// <param name="Media">Campaign videos and images by slot: <c>CAMPAIGN</c>, <c>BRIEFING</c>, <c>ENTRY</c>,
/// <c>WIN</c>, <c>LOSE</c>, <c>DEBRIEFING</c>, <c>DEBRIEFINGLOSE</c>.</param>
/// <param name="SaveMachines">Race whose surviving machines carry over to a later mission.</param>
public sealed record CatalogueScenario(
    string Name,
    string Planet,
    int StringId,
    string? Text,
    int? Music,
    IReadOnlyList<string> After,
    int? MaxPlayers,
    IReadOnlyDictionary<string, string> Media,
    Race? SaveMachines,
    int LineNumber);
