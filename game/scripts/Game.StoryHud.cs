namespace Machines.Game;

/// <summary>What the story's HUD asks of the game: the static over the view, and the way back to the last checkpoint.</summary>
public partial class Game
{
    /// <summary>What lies over the driver's view: the story's HUD lays static over it as it boots.</summary>
    internal FirstPersonScreen? Screen => _screen;

    /// <summary>
    /// The Locator is lost: the chapter starts again from the last checkpoint reached, with the same Grunts' names, behind
    /// the loading screen (no replay is kept of a chapter's attempts).
    /// </summary>
    public void FromCheckpoint()
    {
        if (World.Story is not { } story)
            return;
        StoryProgress.Play(GetTree(), story.Options with { Checkpoint = story.Checkpoint });
    }
}
