namespace Machines.Game;

/// <summary>What the story's HUD asks of the game: the static over the view, and the way back to the last checkpoint.</summary>
public partial class Game
{
    /// <summary>What lies over the driver's view: the story's HUD lays static over it as it boots.</summary>
    internal FirstPersonScreen? Screen => _screen;

    /// <summary>
    /// The chapter was lost (the Locator fell, or the ship came down before the pod was away): it starts again from the last
    /// checkpoint reached, with the same Grunts' names, behind the loading screen (no replay is kept of a chapter's attempts).
    /// Online everyone goes back to a room like this one, which the host opens again from that checkpoint
    /// (<see cref="MultiplayerPage.BackToStoryRoom"/>), and starts when the others are in.
    /// </summary>
    public void FromCheckpoint()
    {
        if (World.Story is not { } story)
            return;
        if (Online)
        {
            MultiplayerPage.BackToStoryRoom(story.Options.Chapter, story.Checkpoint);
            ExitToMenus();
            return;
        }
        StoryProgress.Play(GetTree(), story.Options with { Checkpoint = story.Checkpoint });
    }

    /// <summary>Whether this player opens the room again after a chapter lost online: the host, who plays the Locator.</summary>
    public bool StoryHost => World.Story is { } story && Me == story.Locator;
}
