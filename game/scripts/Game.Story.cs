namespace Machines.Game;

// A story chapter in the match: the remake's own. It plays through Wired's paths (the hero driven in first person from the
// first tick, its deaths, the static), since the sim borrows Wired's heroes for it; what is the story's own is here. In any
// other match World.Story is null and none of this does anything.
public partial class Game
{
    /// <summary>
    /// The story is told in the remake's look (GraphicsSettings.Story), set before anything of the view is built and kept
    /// for the match alone.
    /// </summary>
    private static void SetUpStoryLook(MatchRequest request) => GraphicsSettings.Story = request.Options.IsStory;

    public override void _ExitTree()
    {
        // The menus after a chapter show the look as the player chose it.
        GraphicsSettings.Story = false;
        base._ExitTree();
    }
}
