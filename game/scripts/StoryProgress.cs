using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// How far the story has come, kept in <c>saves/story.json</c> (StoryProgress.Files.cs): the chapter played last and the
/// checkpoint reached in it, the chapters begun and played to their end, and the names given to the two Grunts. The story
/// page's Continue and the co-op room the host opens start from it; a chapter goes on from its checkpoint as the deck's
/// script sets the world up for it (Prologue.SetUp). Only the host's (the Locator's) game writes it. This part is plain
/// data and rules, linked into the tests.
/// </summary>
public sealed partial class StoryProgress
{
    /// <summary>The chapter played last; null before the story was ever begun.</summary>
    public string? Chapter { get; set; }

    /// <summary>The last checkpoint reached in <see cref="Chapter"/>.</summary>
    public int Checkpoint { get; set; }

    /// <summary>Every chapter begun, in the order begun: the chapter list offers these.</summary>
    public List<string> Begun { get; set; } = [];

    /// <summary>Every chapter ever played to its end (its film and card seen), in the order first done.</summary>
    public List<string> Done { get; set; } = [];

    /// <summary>Whether <see cref="Chapter"/> was played to its end the last time: nothing of it is left to continue.</summary>
    public bool Ended { get; set; }

    public string IkeName { get; set; } = "Ike";

    public string BobName { get; set; } = "Bob";

    /// <summary>The chapters there are, in order, as the chapter list shows them.</summary>
    public static IReadOnlyList<string> Chapters { get; } = [StoryOptions.Prologue];

    /// <summary>Whether there is a chapter to go on with: one begun and not yet played to its end (there is no chapter after the prologue yet).</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool CanContinue => Chapter is not null && !Ended;

    /// <summary>
    /// The chapter and checkpoint a story goes on from (the story page's Continue, a co-op room the host opens): the last
    /// reached, or the chapter from its start before any was or once it was played to its end.
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public (string Chapter, int Checkpoint) Next => CanContinue ? (Chapter!, Checkpoint) : (Chapter ?? StoryOptions.Prologue, 0);

    /// <summary>A chapter played to its end: noted as done, nothing left of it to continue; begun again, it starts from its start.</summary>
    public void Finished(string chapter)
    {
        if (!Begun.Contains(chapter))
            Begun.Add(chapter);
        if (!Done.Contains(chapter))
            Done.Add(chapter);
        (Chapter, Checkpoint, Ended) = (chapter, 0, true);
    }

    /// <summary>A chapter is begun or reached a checkpoint: it is the one to continue, from there.</summary>
    public void Reached(string chapter, int checkpoint)
    {
        (Chapter, Checkpoint) = (chapter, checkpoint);
        if (!Begun.Contains(chapter))
            Begun.Add(chapter);
        // Played again from its start or a checkpoint: it is under way until it ends again.
        Ended = false;
    }

    /// <summary>The options a chapter starts with from <paramref name="checkpoint"/>, with the Grunts' names given.</summary>
    public StoryOptions Options(string chapter, int checkpoint) =>
        new() { Chapter = chapter, Checkpoint = checkpoint, IkeName = StorySeats.Named(IkeName, "Ike"), BobName = StorySeats.Named(BobName, "Bob") };
}
