using System.Text.Json;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Net;

/// <summary>
/// A recorded match: how it was set up (its players included, <see cref="MatchInfo.Players"/>), every turn that held commands
/// and who left and came back when, each by player. Because the simulation is deterministic, playing the turns back into a
/// world built the same way shows the same match. Saved games are recordings too, ending where the game was saved.
/// </summary>
/// <param name="Format">The protocol the recording was saved with (<see cref="NetJson.Protocol"/>); a file from before
/// <see cref="OldestFormat"/> does not load.</param>
public sealed record Replay(MatchInfo Match, IReadOnlyList<Turn> Turns, long Ticks, IReadOnlyList<NetMessage>? Presence = null, int Format = 0)
{
    public const string Extension = ".mreplay";

    /// <summary>The oldest recordings that still play: those from 13 on, whose turns and comings and goings are by player.</summary>
    public const int OldestFormat = 13;

    /// <summary>Writes the recording, stamped with today's protocol.</summary>
    public void Save(string path) => File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(this with { Format = NetJson.Protocol }, NetJson.Options));

    /// <summary>Reads a recording or a saved game.</summary>
    /// <exception cref="RecordingTooOldException">The file is from before <see cref="OldestFormat"/>: it can no longer be played.</exception>
    /// <exception cref="JsonException">The file is no recording.</exception>
    public static Replay Load(string path)
    {
        var json = File.ReadAllBytes(path);
        // Its stamp first, so that a file from before is told apart from a broken one whatever else changed since.
        var stamp = JsonSerializer.Deserialize<Stamp>(json, NetJson.Options) ?? throw new JsonException("empty replay");
        if (stamp.Format < OldestFormat)
            throw new RecordingTooOldException();
        return JsonSerializer.Deserialize<Replay>(json, NetJson.Options) ?? throw new JsonException("empty replay");
    }

    /// <summary>The one field of a recording read before the rest.</summary>
    private sealed record Stamp(int Format = 0);

    /// <summary>
    /// The saved games in a folder that belong to a campaign player, which go when the player does. Files that do not
    /// read, saves from before they named their player and saves too old to play stay.
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

    /// <summary>The world the match started from: the same map, settings, players (the table the match records, in its rows' order) and seed.</summary>
    public static GameWorld BuildWorld(GameData data, MatchInfo match) =>
        MatchSetup.Create(data, match.Scenario, match.Planet, match.Table(), match.Seed, match.Options);

    /// <summary>A session that plays the recording back: every turn is in from the start.</summary>
    public LockstepSession Playback(GameWorld world) => Fill(new LockstepSession(world, local: null, Match.InputDelay));

    /// <summary>
    /// A saved game carried on: the recorded turns bring the world back to where it was saved, and from there the
    /// local player plays on.
    /// </summary>
    public LockstepSession Resume(GameWorld world, PlayerId local) =>
        Fill(new LockstepSession(world, local, Match.InputDelay, firstLocalTurn: Ticks));

    private LockstepSession Fill(LockstepSession session)
    {
        var recorded = Turns.ToLookup(t => t.Tick);
        // Ticks without a recorded turn were empty; fill them so the session never waits.
        for (var tick = 0L; tick < Ticks; tick++)
        {
            foreach (var player in session.Humans)
                session.Receive(recorded[tick].FirstOrDefault(t => t.Player == player) ?? new Turn(tick, player, []));
        }
        foreach (var change in Presence ?? [])
            session.Receive(change);
        return session;
    }
}

/// <summary>
/// A recording or saved game from before players were told apart from races (<see cref="Replay.OldestFormat"/>): it can no
/// longer be played. A <see cref="JsonException"/>, so that whoever already passes over a file that does not read passes
/// over this one too.
/// </summary>
public sealed class RecordingTooOldException() : JsonException(Text)
{
    /// <summary>What the player is told, in English; the game shows it in the player's language.</summary>
    public const string Text = "This recording is from an older version of the game and can no longer be played.";
}
