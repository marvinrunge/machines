using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The match the menu asked for; the match scene reads it when it starts. A multiplayer match brings the relay's
/// <see cref="MatchInfo"/> (seed and players included); a replay brings its recording.
/// </summary>
public sealed record MatchRequest(string Scenario, string Planet, StartPositions Starts, MatchOptions Options)
{
    public static MatchRequest Current { get; set; } = FromEnvironment() ?? Default;

    /// <summary>The world's random seed; a new one each time when not given.</summary>
    public int? Seed { get; init; }

    /// <summary>A multiplayer match started by the relay.</summary>
    public MatchInfo? Network { get; init; }

    /// <summary>A recorded match to watch.</summary>
    public Replay? Replay { get; init; }

    /// <summary>Rejoining a running match: the tick this player plays again from (the world catches up to it first).</summary>
    public long? ResumeFrom { get; init; }

    /// <summary>A saved game to carry on: its recording brings the world back to where it was saved.</summary>
    public Replay? Saved { get; init; }

    /// <summary>The M1 match: Red against Blue on m_desert.</summary>
    public static MatchRequest Default => new("m_desert", "m_desert",
        StartPositions.Assign([Race.Red, Race.Blue], 2, Race.Red, random: false, new Random(0)), new MatchOptions());

    /// <summary>A relay's match, from the point of view of the local client (or the first player, for replays).</summary>
    public static MatchRequest FromMatch(MatchInfo match, int clientId, Race? slot = null)
    {
        var local = match.Players.FirstOrDefault(p => !p.Ai && (slot is null ? p.ClientId == clientId : p.Slot == slot))
                    ?? match.Players.First(p => !p.Ai);
        var starts = new StartPositions(local.Slot,
            match.Players.ToDictionary(p => p.Slot, p => p.Ai ? Controller.Ai : Controller.Player),
            match.Players.ToDictionary(p => p.Slot, p => p.Colour));
        return new MatchRequest(match.Scenario, match.Planet, starts, match.Options) { Seed = match.Seed };
    }

    /// <summary>
    /// This match as the relay would describe it, for recording a replay or saving a single-player game, with the
    /// ground it is played on (<paramref name="terrain"/>, its fingerprint).
    /// </summary>
    public MatchInfo ToMatchInfo(int seed, int inputDelay, long? terrain) => Network ?? Saved?.Match ?? Replay?.Match
        ?? new MatchInfo(Scenario, Planet, Options, seed, inputDelay,
            Starts.Players.Select(p => new MatchPlayer(0, p.Value == Controller.Ai ? "AI" : NetLink.Name, p.Key,
                Starts.Colours.GetValueOrDefault(p.Key, p.Key), p.Value == Controller.Ai)).ToList(), terrain);

    /// <summary>
    /// For automated checks: MACHINES_MAP=&lt;scenario&gt;,&lt;planet&gt; starts that map straight away, with an AI
    /// in every other start position.
    /// </summary>
    private static MatchRequest? FromEnvironment()
    {
        // MACHINES_REPLAY=<file> watches a recorded match.
        if (System.Environment.GetEnvironmentVariable("MACHINES_REPLAY") is { Length: > 0 } recording)
        {
            var replay = Replay.Load(recording);
            return FromMatch(replay.Match, clientId: -1) with { Replay = replay };
        }
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
