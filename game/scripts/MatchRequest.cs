using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>The match the skirmish menu asked for; the match scene reads it when it starts.</summary>
public sealed record MatchRequest(string Scenario, string Planet, StartPositions Starts, MatchOptions Options)
{
    public static MatchRequest Current { get; set; } = FromEnvironment() ?? Default;

    /// <summary>The M1 match: Red against Blue on m_desert.</summary>
    public static MatchRequest Default => new("m_desert", "m_desert",
        StartPositions.Assign([Race.Red, Race.Blue], 2, Race.Red, random: false, new Random(0)), new MatchOptions());

    /// <summary>
    /// For automated checks: MACHINES_MAP=&lt;scenario&gt;,&lt;planet&gt; starts that map straight away, with an AI
    /// in every other start position.
    /// </summary>
    private static MatchRequest? FromEnvironment()
    {
        var map = System.Environment.GetEnvironmentVariable("MACHINES_MAP");
        if (string.IsNullOrEmpty(map))
            return null;
        var parts = map.Split(',');
        var scenario = parts[0];
        var planet = parts.Length > 1 ? parts[1] : scenario;
        var slots = Paths.Data.LoadScenario(scenario).Races.Select(r => r.Race).ToList();
        return new MatchRequest(scenario, planet, StartPositions.Assign(slots, slots.Count, slots.Min(), random: false, new Random(0)),
            new MatchOptions { Resources = ResourceLevel.High });
    }
}
