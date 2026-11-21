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

    // ---- the story's keys ----------------------------------------------------------------------------

    /// <summary>
    /// The keys of a chapter, driving or down: the Locator tells its Grunts to follow, wait or distract, sends out its
    /// scanner's pulse (the chassis' special), switches its lamp (any hero of the trio), and dashes or uses an ability where
    /// its chassis has one. None of Wired's squad orders and no hangar: the story has neither (GameWorld.Borrowed). Hacking
    /// and using are the hack's key (HackHost).
    /// </summary>
    private void StoryInput(Godot.InputEvent @event)
    {
        if (KeyMap.Is(@event, KeyMap.Act.GruntFollow))
            GruntOrder(KeyMap.Act.GruntFollow);
        else if (KeyMap.Is(@event, KeyMap.Act.GruntWait))
            GruntOrder(KeyMap.Act.GruntWait);
        else if (KeyMap.Is(@event, KeyMap.Act.GruntDistract))
            GruntOrder(KeyMap.Act.GruntDistract);
        else if (KeyMap.Is(@event, KeyMap.Act.Scan))
            UseSpecial();
        else if (KeyMap.Is(@event, KeyMap.Act.Light))
            SwitchStoryLamp();
        else if (KeyMap.Is(@event, KeyMap.Act.Ability1))
            UseAbility(0);
        else if (KeyMap.Is(@event, KeyMap.Act.Ability2))
            UseAbility(1);
        else if (KeyMap.Is(@event, KeyMap.Act.Dash))
            Dash();
    }

    /// <summary>
    /// An order to the Grunts the computer plays, which the Locator alone gives: follow it, wait where they stand, or go and
    /// distract where its sight rests. The command is the companions' (plan WP-F, <see cref="SendGruntOrder"/>); the tab over
    /// the Grunts' cards lights up and the click answers, as a squad order does in a run.
    /// </summary>
    private void GruntOrder(KeyMap.Act act)
    {
        if (World.Story is not { } story || Me != story.Locator)
            return;
        if (World.HeroOf(Me) is null)
        {
            RunRefused(HangarText.Reason("the hero is down"));
            return;
        }
        if (RefusesOrders())
            return;
        var (sight, _) = SightAt(GetViewport().GetVisibleRect().Size / 2, OrderSight);
        SendGruntOrder(act, new System.Numerics.Vector2(sight.X, sight.Z));
        _audio.PlayGui("gui/sounds/taskmach.wav");
        _hud.Story?.OrderGiven(act);
        if (_sceneScripted)
            Godot.GD.Print($"scene: grunt order {act}");
    }

    /// <summary>
    /// Sends the Grunts' order (Follow, Wait or Distract, by <paramref name="act"/>) as the story's command, with the point
    /// the sight rests on. The companions' package (plan WP-F, Story/CompanionCommands) gives it its body; until then no
    /// command goes out and only the tab lights up.
    /// </summary>
    partial void SendGruntOrder(KeyMap.Act act, System.Numerics.Vector2 point);

    // ---- the story's death view -------------------------------------------------------------------------

    /// <summary>
    /// A hero of the story is down: the view stays where its eyes were, under the static, with the world dimly behind it,
    /// until the chapter goes on from its checkpoint (StoryHud's card) or the companions' revive brings it back. The camera
    /// does not move, so indoors it cannot pass through a wall as the battle's camera behind a team-mate or the run's at
    /// the pod would; nor does the view leave for the field above the deck.
    /// </summary>
    private void HoldStoryDeathView()
    {
        _driven = null;
        _heroDown = true;
        _screen.Hold();
        _hud.HeroDown();
    }

    /// <summary>
    /// A driver who comes into a chapter with the hero down (a save, a late join): the same static, looked at from where
    /// the hero's seat began, the deck's marker for it, at about a hero's eye height.
    /// </summary>
    private Godot.Vector3? StoryDeathSpot()
    {
        if (World.Story is not { } story || World.Deck is not { } deck || story.Seats.IndexOf(Me) is var seat && seat < 0)
            return null;
        return ActorView.World(deck.Marker(Machines.Sim.Story.StorySeats.Spawns[seat]).Position) + Godot.Vector3.Up * 2;
    }

    public override void _ExitTree()
    {
        // The menus after a chapter show the look as the player chose it.
        GraphicsSettings.Story = false;
        base._ExitTree();
    }
}
