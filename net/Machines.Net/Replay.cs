using System.Text.Json;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Net;

/// <summary>
/// A recorded match: how it was set up, every turn that held commands and who left and came back when. Because the simulation is
/// deterministic, playing the turns back into a world built the same way shows the same match.
/// </summary>
public sealed record Replay(MatchInfo Match, IReadOnlyList<Turn> Turns, long Ticks, IReadOnlyList<NetMessage>? Presence = null)
{
    public const string Extension = ".mreplay";

    public void Save(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this, NetJson.Options));

    public static Replay Load(string path)
    {
        var json = File.ReadAllBytes(path);
        var replay = JsonSerializer.Deserialize<Replay>(json, NetJson.Options) ?? throw new JsonException("empty replay");
        var text = System.Text.Encoding.UTF8.GetString(json);
        bool Has(string option) => text.Contains($"\"{option}\"", StringComparison.Ordinal);
        var options = replay.Match.Options;
        // Recorded before artefacts could be destroyed: they stay whole, so it plays back as it was played.
        if (!Has(nameof(MatchOptions.DestructibleArtefacts)))
            options = options with { DestructibleArtefacts = false };
        // Recorded before machines moved as in the original: they move as they did then.
        if (!Has(nameof(MatchOptions.OriginalMotion)))
            options = options with { OriginalMotion = false };
        // Recorded before captured buildings brought their stores: they change hands as they did then.
        if (!Has(nameof(MatchOptions.OriginalCapture)))
            options = options with { OriginalCapture = false };
        // Recorded before the scenarios' construction restrictions held: everything stays buildable as it was then.
        if (!Has(nameof(MatchOptions.RestrictConstruction)))
            options = options with { RestrictConstruction = false };
        // Recorded before lost stores and cancelled machines cost BMUs: the bank keeps them as it did then.
        if (!Has(nameof(MatchOptions.OriginalBank)))
            options = options with { OriginalBank = false };
        return options == replay.Match.Options ? replay : replay with { Match = replay.Match with { Options = options } };
    }

    /// <summary>
    /// The saved games in a folder that belong to a campaign player, which go when the player does. Files that do not
    /// read and saves from before they named their player stay.
    /// </summary>
    public static List<string> CampaignSaves(string folder, string extension, string player)
    {
        var saves = new List<string>();
        if (!Directory.Exists(folder))
            return saves;
        foreach (var file in Directory.EnumerateFiles(folder, "*" + extension))
        {
            try
            {
                if (Load(file).Match.Options.Campaign?.Player == player)
                    saves.Add(file);
            }
            catch (Exception e) when (e is JsonException or IOException)
            {
            }
        }
        return saves;
    }

    /// <summary>The world the match started from: the same map, settings, players and seed.</summary>
    public static GameWorld BuildWorld(GameData data, MatchInfo match) =>
        MatchSetup.Create(data, match.Scenario, match.Planet,
            match.Players.ToDictionary(p => p.Slot, p => p.Ai ? Controller.Ai : Controller.Player), match.Seed, match.Options);

    /// <summary>A session that plays the recording back: every turn is in from the start.</summary>
    public LockstepSession Playback(GameWorld world) => Fill(new LockstepSession(world, local: null, Match.Players.Where(p => !p.Ai).Select(p => p.Slot), Match.InputDelay));

    /// <summary>
    /// A saved game carried on: the recorded turns bring the world back to where it was saved, and from there the
    /// local player plays on.
    /// </summary>
    public LockstepSession Resume(GameWorld world, Race local) =>
        Fill(new LockstepSession(world, local, Match.Players.Where(p => !p.Ai).Select(p => p.Slot), Match.InputDelay, firstLocalTurn: Ticks));

    private LockstepSession Fill(LockstepSession session)
    {
        var recorded = Turns.ToLookup(t => t.Tick);
        // Ticks without a recorded turn were empty; fill them so the session never waits.
        for (var tick = 0L; tick < Ticks; tick++)
        {
            foreach (var player in Match.Players.Where(p => !p.Ai))
                session.Receive(recorded[tick].FirstOrDefault(t => t.Race == player.Slot) ?? new Turn(tick, player.Slot, []));
        }
        foreach (var change in Presence ?? [])
            session.Receive(change);
        return session;
    }
}
