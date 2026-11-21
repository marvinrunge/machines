using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// Who a line of the story is for (docs/STORY.md, "Die beiden Grunts" and "Koop"). Every world raises the same lines on the
/// same ticks; which screen shows one is the client's to decide, by <see cref="StoryAudiences.Shows"/>.
/// </summary>
public enum StoryAudience
{
    /// <summary>Everyone: the ship, the automatic voices, the Locator's own readouts.</summary>
    Everyone,

    /// <summary>
    /// A Grunt's personality, its bark over the radio: only while the computer plays it. "Spielen echte Mitspieler die Grunts,
    /// bleiben die Persönlichkeiten in den geschriebenen Funksprüchen weg."
    /// </summary>
    Companion,

    /// <summary>
    /// The foreign messages only the Grunt's own player gets (Bob's story: "der Spieler bekommt fremde Meldungen, die nur er
    /// sieht"): shown on the screen of the person who plays that seat, and on no other; while the computer plays it, nowhere.
    /// </summary>
    Private,
}

/// <summary>The rule of <see cref="StoryAudience"/>, free of Godot so the tests can hold it.</summary>
public static class StoryAudiences
{
    /// <summary>
    /// Whether the screen of <paramref name="me"/> shows a line meant for <paramref name="audience"/> about the story's seat
    /// <paramref name="seat"/> (its place: 0 the Locator, 1 Ike, 2 Bob). A replay watched by nobody (<paramref name="me"/> no
    /// seat of the story) shows every line meant for everyone and the companions' barks, never a private one.
    /// </summary>
    public static bool Shows(GameWorld world, StoryAudience audience, int seat, PlayerId me)
    {
        if (world.Story is not { } run || seat < 0 || seat >= run.Seats.Count)
            return audience == StoryAudience.Everyone;
        var player = run.Seats[seat];
        return audience switch
        {
            StoryAudience.Everyone => true,
            StoryAudience.Companion => world.StoryBotPlays(player),
            StoryAudience.Private => player == me && !world.StoryBotPlays(player),
            _ => false,
        };
    }
}
