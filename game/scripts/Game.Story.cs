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

    /// <summary>
    /// For pictures of a chapter: MACHINES_SCREENSHOT_STORY=x,y[,angle] sets the trio down at that point of the deck (metres,
    /// the deck's x east and y north), the Locator looking at that angle (degrees, 0 east, 90 north), Ike and Bob 7 m ahead
    /// of it to either side, in sight. Doors and walls are not asked; pick a floor cell. MACHINES_SCREENSHOT_GOGGLES=1 puts
    /// the night vision goggles on once the Locator is driven, as the deck is dark.
    /// </summary>
    private void PlaceStoryTrioIfAsked()
    {
        if (World.Story is not null && !_storyGoggles && _driven is not null
            && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_GOGGLES") == "1")
        {
            _storyGoggles = true;
            _screen.ToggleGoggles();
        }
        if (_runTime != 0 || World.Story is not { } story
            || System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_STORY")?.Split(',') is not { Length: >= 2 } at)
            return;
        float Number(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var spot = new System.Numerics.Vector2(Number(at[0]), Number(at[1]));
        var facing = at.Length > 2 ? Godot.Mathf.DegToRad(Number(at[2])) : 0f;
        var forward = new System.Numerics.Vector2(MathF.Cos(facing), MathF.Sin(facing));
        var side = new System.Numerics.Vector2(-forward.Y, forward.X);
        for (var i = 0; i < story.Seats.Count; i++)
        {
            if (World.HeroOf(story.Seats[i]) is not { } hero)
                continue;
            hero.Position = i == 0 ? spot : spot + forward * 7 + side * (i == 1 ? 2.5f : -2.5f);
            hero.Facing = facing;
        }
        _aimYaw = facing;
        SyncViews();
    }

    private bool _storyGoggles;

    public override void _ExitTree()
    {
        // The menus after a chapter show the look as the player chose it.
        GraphicsSettings.Story = false;
        base._ExitTree();
    }
}
