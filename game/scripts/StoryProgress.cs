using System.Text.Json;
using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// How far the story has come, kept in <c>saves/story.json</c>: the chapter played last and the checkpoint reached in it,
/// the chapters begun, and the names given to the two Grunts. The story page's Continue starts from it. A stand-in for the
/// checkpoints' own saves (plan WP-K), which will also keep what the trio carries; until then a chapter goes on from its
/// checkpoint as the deck has it.
/// </summary>
public sealed class StoryProgress
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    /// <summary>The chapter played last; null before the story was ever begun.</summary>
    public string? Chapter { get; set; }

    /// <summary>The last checkpoint reached in <see cref="Chapter"/>.</summary>
    public int Checkpoint { get; set; }

    /// <summary>Every chapter begun, in the order begun: the chapter list offers these.</summary>
    public List<string> Begun { get; set; } = [];

    public string IkeName { get; set; } = "Ike";

    public string BobName { get; set; } = "Bob";

    /// <summary>The chapters there are, in order, as the chapter list shows them.</summary>
    public static IReadOnlyList<string> Chapters { get; } = [StoryOptions.Prologue];

    public static string FilePath => System.IO.Path.Combine(Game.SaveFolder, "story.json");

    /// <summary>Whether there is a chapter to go on with.</summary>
    public bool CanContinue => Chapter is not null;

    /// <summary>What is saved, or a story not yet begun when there is no file or it cannot be read.</summary>
    public static StoryProgress Load()
    {
        try
        {
            return File.Exists(FilePath) ? JsonSerializer.Deserialize<StoryProgress>(File.ReadAllText(FilePath), Json) ?? new() : new();
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            GD.PushWarning($"story progress not read: {exception.Message}");
            return new();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Game.SaveFolder);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Json));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            GD.PushWarning($"story progress not saved: {exception.Message}");
        }
    }

    /// <summary>A chapter is begun or reached a checkpoint: it is the one to continue, from there.</summary>
    public void Reached(string chapter, int checkpoint)
    {
        (Chapter, Checkpoint) = (chapter, checkpoint);
        if (!Begun.Contains(chapter))
            Begun.Add(chapter);
    }

    /// <summary>The options a chapter starts with from <paramref name="checkpoint"/>, with the Grunts' names given.</summary>
    public StoryOptions Options(string chapter, int checkpoint) =>
        new() { Chapter = chapter, Checkpoint = checkpoint, IkeName = StorySeats.Named(IkeName, "Ike"), BobName = StorySeats.Named(BobName, "Bob") };

    /// <summary>A chapter's title as the menus and the loading screen show it, in the player's language.</summary>
    public static string Title(string chapter) => chapter == StoryOptions.Prologue ? Lang.T("Prologue") : chapter;

    /// <summary>A chapter's name under its title: the prologue is "Fault", the ship's word for L-7.</summary>
    public static string Subtitle(string chapter) => chapter == StoryOptions.Prologue ? Lang.T("Fault") : "";

    /// <summary>The loading screen's line for a chapter: "PROLOGUE – FAULT".</summary>
    public static string Heading(string chapter) =>
        (Subtitle(chapter) is { Length: > 0 } name ? $"{Title(chapter)} – {name}" : Title(chapter)).ToUpperInvariant();

    /// <summary>
    /// Starts a chapter from a checkpoint behind the loading screen, noting it as the one to continue. The menus' Story page
    /// and the HUD's way back to the checkpoint come here.
    /// </summary>
    public static void Play(SceneTree tree, StoryOptions options)
    {
        var progress = Load();
        progress.Reached(options.Chapter, options.Checkpoint);
        (progress.IkeName, progress.BobName) = (options.IkeName, options.BobName);
        progress.Save();
        MatchRequest.Current = MatchRequest.ForStory(options);
        var detail = options.Checkpoint > 0 ? $"{Lang.T("STORY")}   ·   {Lang.T("Checkpoint {0}", options.Checkpoint).ToUpperInvariant()}" : Lang.T("STORY");
        LoadingScreen.Show(tree, Heading(options.Chapter), detail);
    }
}
