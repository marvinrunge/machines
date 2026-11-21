using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// The rules of the story played together that the game asks of a match's players, free of a world so the lobby and the
/// tests can hold them (docs/STORY.md, "Koop"): which seat a player is, and whose game keeps the progress.
/// </summary>
public static class StoryCoop
{
    /// <summary>The seat names the lobby and the HUD give the story's three places, before any person or Grunt name.</summary>
    public static IReadOnlyList<string> Roles { get; } = ["Locator", "Ike", "Bob"];

    /// <summary>A player's place among the story's seats (0 the Locator, 1 Ike, 2 Bob), or -1 for one who is none of them.</summary>
    public static int SeatOf(PlayerTable players, StoryOptions story, PlayerId player) =>
        StorySeats.Of(players, story.Race).ToList().IndexOf(player);

    /// <summary>
    /// Whether the game of <paramref name="me"/> keeps the story's progress (checkpoints, the Grunts' names, saves/story.json):
    /// only the Locator's, which is the host's. "Der Fortschritt gehört zum Spielstand des Hosts, auch der der Grunts." A Grunt's
    /// player plays along in the host's story and keeps nothing of it; a recording watched keeps nothing either.
    /// </summary>
    public static bool KeepsProgress(PlayerTable players, StoryOptions story, PlayerId? me) =>
        me is { } player && SeatOf(players, story, player) == 0;
}
