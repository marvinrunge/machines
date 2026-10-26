using System.Text.Json;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Net;

/// <summary>
/// A recorded match: how it was set up and every turn that held commands. Because the simulation is
/// deterministic, playing the turns back into a world built the same way shows the same match.
/// </summary>
public sealed record Replay(MatchInfo Match, IReadOnlyList<Turn> Turns, long Ticks)
{
    public const string Extension = ".mreplay";

    public void Save(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, NetJson.Options));

    public static Replay Load(string path) =>
        JsonSerializer.Deserialize<Replay>(File.ReadAllBytes(path), NetJson.Options) ?? throw new JsonException("empty replay");

    /// <summary>The world the match started from: the same map, settings, players and seed.</summary>
    public static GameWorld BuildWorld(GameData data, MatchInfo match) =>
        MatchSetup.Create(data, match.Scenario, match.Planet,
            match.Players.ToDictionary(p => p.Slot, p => p.Ai ? Controller.Ai : Controller.Player), match.Seed, match.Options);

    /// <summary>A session that plays the recording back: every turn is in from the start.</summary>
    public LockstepSession Playback(GameWorld world)
    {
        var session = new LockstepSession(world, local: null, Match.Players.Where(p => !p.Ai).Select(p => p.Slot), Match.InputDelay);
        var recorded = Turns.ToLookup(t => t.Tick);
        // Ticks without a recorded turn were empty; fill them so the session never waits.
        for (var tick = 0L; tick < Ticks; tick++)
        {
            foreach (var player in Match.Players.Where(p => !p.Ai))
                session.Receive(recorded[tick].FirstOrDefault(t => t.Race == player.Slot) ?? new Turn(tick, player.Slot, []));
        }
        return session;
    }
}
