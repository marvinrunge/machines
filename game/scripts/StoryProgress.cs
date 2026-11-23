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

    /// <summary>
    /// Whether this player has seen the Locator's boot in its clamp to its end once: from then on it may be skipped (Esc held),
    /// the first time not.
    /// </summary>
    public bool BootSeen { get; set; }

    /// <summary>
    /// How the checkpoints of this file are numbered: <see cref="CurrentCheckpoints"/> since stage 7 (0–14). A file of an older
    /// numbering, or one without it, is not read at all (<see cref="FromJson"/>): nothing was played for real before.
    /// </summary>
    public int CheckpointVersion { get; set; } = CurrentCheckpoints;

    /// <summary>The checkpoints' numbering of today: 2, stage 7's 0–14.</summary>
    public const int CurrentCheckpoints = 2;

    /// <summary>What the Locator stripped and marked in the wreck for act 2, in the order stripped (plan stufe7 §7; act 2 reads it).</summary>
    public List<string> Salvaged { get; set; } = [];

    /// <summary>A part stripped in the wreck is kept, once; the host's game saves it as it happens. True when it is new.</summary>
    public bool AddSalvaged(string id)
    {
        if (Salvaged.Contains(id))
            return false;
        Salvaged.Add(id);
        return true;
    }

    /// <summary>
    /// The progress a file holds, or null when it is of an older checkpoint numbering (before stage 7, or without one): such
    /// a save is dropped and the story starts afresh. Throws <see cref="System.Text.Json.JsonException"/> on a broken file.
    /// </summary>
    public static StoryProgress? FromJson(string text, System.Text.Json.JsonSerializerOptions? options = null)
    {
        using (var document = System.Text.Json.JsonDocument.Parse(text))
        {
            if (!document.RootElement.TryGetProperty(nameof(CheckpointVersion), out var version)
                || version.ValueKind != System.Text.Json.JsonValueKind.Number || version.GetInt32() < CurrentCheckpoints)
                return null;
        }
        return System.Text.Json.JsonSerializer.Deserialize<StoryProgress>(text, options);
    }

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
        new()
        {
            Chapter = chapter, Checkpoint = checkpoint, IkeName = StorySeats.Named(IkeName, "Ike"), BobName = StorySeats.Named(BobName, "Bob"),
            Difficulty = AskedDifficulty ?? Difficulty,
        };

    /// <summary>
    /// The story's difficulty for one run, when MACHINES_STORY_DIFFICULTY=easy|normal|hard asks for one; null otherwise, and
    /// the story page's choice (<see cref="Difficulty"/>) holds.
    /// </summary>
    public static StoryDifficulty? AskedDifficulty =>
        Enum.TryParse<StoryDifficulty>(System.Environment.GetEnvironmentVariable("MACHINES_STORY_DIFFICULTY"), ignoreCase: true, out var asked)
            ? asked : null;

    /// <summary>The story's difficulty as the story page sets it (StoryOptions.Difficulty): Normal until the player picks another.</summary>
    public StoryDifficulty Difficulty { get; set; } = StoryDifficulty.Normal;
}
