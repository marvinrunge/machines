using System.Text.Json;
using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// The story's progress on disk and in the menus: its file, its words and the way a chapter starts from it.
public sealed partial class StoryProgress
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    public static string FilePath => System.IO.Path.Combine(Game.SaveFolder, "story.json");

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
