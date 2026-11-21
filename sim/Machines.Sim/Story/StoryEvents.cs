using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>What the sight's and the companions' parts of the story tell the director, which turns each into its event.</summary>
public enum StoryNewsKind
{
    /// <summary>A hunter grows suspicious (Level: its suspicion, 0 to 1).</summary>
    Suspicious,

    /// <summary>A hunter sees one of the trio.</summary>
    Spotted,

    /// <summary>A hunter lost the trio from sight.</summary>
    Lost,

    /// <summary>A Grunt the computer plays says something over the radio (Line: the line's id).</summary>
    Bark,
}

/// <summary>
/// A piece of news for the director (<see cref="StoryDirector.Hear"/>) from the parts of the story that are not its own: the
/// hunters' sight (plan WP-D) and the companions (plan WP-F). It is heard on the tick it is given and turned into its event
/// there, so it never outlives a tick and needs no place in the checksum.
/// </summary>
/// <param name="Actor">The hunter, or the Grunt that barks.</param>
/// <param name="Seat">The seat it is about (whom a hunter sees; the barking Grunt's), if any.</param>
/// <param name="Position">Where: what a hunter turns to, where it saw or lost them, where the bark is said.</param>
/// <param name="Line">A bark's line id (tools/voice/lines.json).</param>
/// <param name="Level">A suspicion, 0 to 1.</param>
public sealed record StoryNews(StoryNewsKind Kind, int Actor, PlayerId? Seat, Vector2 Position, string? Line = null, float Level = 0);

/// <summary>
/// The story's events (the <c>Story…</c> kinds of <see cref="GameEventKind"/>), made here and read here, so the director and
/// the view agree on what each field carries. Every world raises the same on the same ticks; which screen shows what is the
/// view's to decide (<see cref="StoryAudiences"/>).
/// </summary>
public static class StoryEvents
{
    internal static void Line(GameWorld world, string id, StoryAudience audience = StoryAudience.Everyone, PlayerId? seat = null, int speaker = 0, Vector2 at = default) =>
        world.Raise(new GameEvent(GameEventKind.StoryLine, speaker, at, (int)audience, id, Player: seat));

    internal static void Objective(GameWorld world, string? text, Vector2? at) =>
        world.Raise(new GameEvent(GameEventKind.StoryObjective, 0, at ?? Vector2.Zero, at is null ? 0 : 1, text));

    internal static void Checkpoint(GameWorld world, int checkpoint, Vector2 at) =>
        world.Raise(new GameEvent(GameEventKind.StoryCheckpoint, 0, at, checkpoint));

    internal static void Cue(GameWorld world, string words, Vector2 at = default, int actor = 0) =>
        world.Raise(new GameEvent(GameEventKind.StoryCue, actor, at, Detail: words));

    internal static void Alarm(GameWorld world, bool on, Vector2 at = default) =>
        world.Raise(new GameEvent(GameEventKind.StoryAlarm, 0, at, on ? 1 : 0));

    internal static void Countdown(GameWorld world, string what, float left) =>
        world.Raise(new GameEvent(GameEventKind.StoryCountdown, 0, Vector2.Zero, (int)MathF.Ceiling(MathF.Max(0, left)), what));

    internal static void Hack(GameWorld world, string what, string target, Vector2 at, PlayerId? hacker) =>
        world.Raise(new GameEvent(GameEventKind.StoryHack, 0, at, Detail: $"{what} {target}", Player: hacker));

    /// <summary>A piece of news as its event: the sight's as theirs, a bark as a companion's line.</summary>
    internal static void News(GameWorld world, StoryNews news)
    {
        switch (news.Kind)
        {
            case StoryNewsKind.Suspicious:
                world.Raise(new GameEvent(GameEventKind.StorySuspicious, news.Actor, news.Position, (int)MathF.Round(Math.Clamp(news.Level, 0, 1) * 100), Player: news.Seat));
                break;
            case StoryNewsKind.Spotted:
                world.Raise(new GameEvent(GameEventKind.StorySpotted, news.Actor, news.Position, Player: news.Seat));
                break;
            case StoryNewsKind.Lost:
                world.Raise(new GameEvent(GameEventKind.StoryLost, news.Actor, news.Position, Player: news.Seat));
                break;
            case StoryNewsKind.Bark when news.Line is { Length: > 0 } line:
                Line(world, line, StoryAudience.Companion, news.Seat, news.Actor, news.Position);
                break;
        }
    }

    // ---- reading them ----------------------------------------------------------------------------

    /// <summary>A line event's line id, who it is for and the place of the seat it is about (-1 for none); null for any other event.</summary>
    public static (string Id, StoryAudience Audience, int Seat)? LineOf(GameWorld world, GameEvent e)
    {
        if (e is not { Kind: GameEventKind.StoryLine, Detail: { Length: > 0 } id })
            return null;
        var seat = e.Player is { } player && world.Story is { } run ? run.Seats.IndexOf(player) : -1;
        return (id, (StoryAudience)(e.Other ?? 0), seat);
    }

    /// <summary>A hack event's step (<c>open</c>, <c>board</c>, <c>done</c>, <c>traced</c>, <c>broken</c>, <c>left</c>) and target; null for any other event.</summary>
    public static (string What, string Target)? HackOf(GameEvent e) =>
        e is { Kind: GameEventKind.StoryHack, Detail: { } detail } && detail.IndexOf(' ') is > 0 and var space
            ? (detail[..space], detail[(space + 1)..])
            : null;

    /// <summary>A cue's words, split at the spaces; empty for any other event.</summary>
    public static string[] CueOf(GameEvent e) =>
        e is { Kind: GameEventKind.StoryCue, Detail: { } words } ? words.Split(' ', StringSplitOptions.RemoveEmptyEntries) : [];
}
