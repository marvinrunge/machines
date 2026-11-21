using Machines.Sim.World;

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
        // Use held for a downed Grunt, a switch, a rack or the hatch (the companions', Game.Companions).
        if (StoryUse(@event))
            return;
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
            StoryDash();
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
    /// the sight rests on (Game.Companions: StoryOrderCommand).
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
        if (_storyHeard)
        {
            StoryAudio.LineStarted -= StoryLineStarted;
            HackSounds.Player = null;
        }
        base._ExitTree();
    }

    // ---- what the director tells ------------------------------------------------------------------------

    private bool _storyHeard;
    private static Machines.Sim.Story.StoryLines? _storyLines;

    /// <summary>The game's line table (game/assets/story/prologue.lines.json), read once; empty without it.</summary>
    public static Machines.Sim.Story.StoryLines StoryLines
    {
        get
        {
            if (_storyLines is null)
            {
                try
                {
                    var text = Godot.FileAccess.GetFileAsString("res://assets/story/prologue.lines.json");
                    _storyLines = text.Length > 0 ? Machines.Sim.Story.StoryLines.Parse(text) : Machines.Sim.Story.StoryLines.Empty;
                }
                catch (FormatException e)
                {
                    Godot.GD.PushError($"story lines: {e.Message}");
                    _storyLines = Machines.Sim.Story.StoryLines.Empty;
                }
            }
            return _storyLines;
        }
    }

    /// <summary>
    /// After every tick of a story (Stepped): what the director told (StoryDirector, StoryEvents) is shown and heard. Its
    /// lines are spoken by the story's audio for whoever is to hear them, with their subtitles; the others' hacks are heard
    /// where they stand; its cues play the cutscenes (Erwischt, Abwurf), lean the view, show the props it set down and the
    /// scares; a new objective is announced; the alarm sounds; and the deck's lights follow the run's. Nothing here changes
    /// the world.
    /// </summary>
    private void ShowStoryEvents()
    {
        if (World.Story is not { } story)
            return;
        if (!_storyHeard)
        {
            _storyHeard = true;
            StoryAudio.LineStarted += StoryLineStarted;
            HackSounds.Player = id => StoryAudio.Play(id);
            StoryDriver.Attend(this);
        }
        foreach (var e in World.Events)
        {
            var at = ActorView.World(e.Position);
            switch (e.Kind)
            {
                case GameEventKind.StoryLine when Machines.Sim.Story.StoryEvents.LineOf(World, e) is { } line:
                    if (Machines.Sim.Story.StoryAudiences.Shows(World, line.Audience, line.Seat, Me))
                        SayStoryLine(line.Id);
                    break;
                case GameEventKind.StoryHack when Machines.Sim.Story.StoryEvents.HackOf(e) is { } hack && e.Player != Me:
                    // This player's own hack sounds from its board; another's is heard at the target.
                    if (hack.What switch { "open" or "done" or "board" => HackSounds.Connect, "traced" => HackSounds.TraceBack, "broken" => HackSounds.Fail, _ => null } is { } sound)
                        StoryAudio.Play(sound, at);
                    break;
                case GameEventKind.StoryCue:
                    StoryCue(Machines.Sim.Story.StoryEvents.CueOf(e), at);
                    break;
                case GameEventKind.StoryObjective when e.Detail is { Length: > 0 } text:
                    _hud.Story?.Announce(Lang.T("Objective").Caps(), Lang.T(text), MenuTheme.Glow, 2.5f);
                    break;
                case GameEventKind.StoryAlarm:
                    StoryAudio.SetLayer("amb_alarm_klaxon", e.Other == 1, -14);
                    break;
                case GameEventKind.StorySpotted when e.Player is null || e.Player == Me:
                    // A hunter of the ship's has one of the trio in sight (the sight's news, via the director).
                    StoryAudio.Sting("sting_found");
                    break;
                case GameEventKind.StorySuspicious when (e.Player is null || e.Player == Me) && e.Other is > 0 and < 100:
                    // Something caught a seeker's eye or ear: it stops and turns (the music's threat follows the suspicion).
                    StoryAudio.Sting("sting_dread");
                    break;
                case GameEventKind.StoryCountdown when e.Detail == "stage":
                    StoryAudio.Play("metal_groan_big");
                    if (World.HeroOf(Me) is { } hero)
                        CameraRig.Shake(ActorView.World(hero.Position), 1.5f, 1.2f);
                    break;
            }
        }
        // The deck's light groups follow the sight's (GameWorld.StoryLightState) in FollowDeck.
    }

    /// <summary>A line of the line table spoken (its subtitle comes as it starts); without the story's audio only its subtitle.</summary>
    private void SayStoryLine(string id)
    {
        if (StoryAudio.Current is not null)
            StoryAudio.Say(id);
        else if (StoryLines.Find(id) is { } line && SubtitleQueue.Parse(line.Speaker) is { } speaker)
            _hud.Subtitles?.Say(speaker, line.English);
    }

    /// <summary>A line's voice starts (or, without one, its turn): its subtitle, timed by the voice file when there is one.</summary>
    private void StoryLineStarted(StoryLineCue cue)
    {
        if (StoryLines.Find(cue.Id) is { } line && SubtitleQueue.Parse(line.Speaker) is { } speaker)
            _hud.Subtitles?.Say(speaker, line.English, cue.Voiced ? cue.Seconds : null);
    }

    /// <summary>The words of a director's cue: what the view shows and plays of it.</summary>
    private void StoryCue(string[] words, Godot.Vector3 at)
    {
        float Number(int i) => words.Length > i && float.TryParse(words[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        switch (words)
        {
            case ["cutscene", var name]:
                PlayCutscene(name);
                break;
            case ["artefact", var placement, var type, ..] when int.TryParse(placement, out var index):
                ShowPlacedArtefact(index, type, new System.Numerics.Vector2(Number(3), Number(4)), Number(5));
                break;
            case ["tilt", ..]:
                TiltTo(new CameraTilt(Number(1), Number(2), Sway: Number(1) > 6 ? 0.8f : 0), Number(3));
                break;
            case ["door", _]:
                StoryAudio.Play("door_open", at);
                break;
            case ["clamp", _]:
                StoryAudio.Play("clamp_release", at);
                break;
            case ["steps"]:
                StoryAudio.Play("thresher_step", at);
                break;
            case ["scare", var what]:
                // "scare wake": the scare Grunt's lamp flares in the sim (the director's Lamp), which its view follows.
                StoryAudio.Sting(what == "found" ? "sting_found" : "sting_dread");
                if (what switch { "crates" => "crate_fall", "dust" => "amb_duct_rumble", "wake" => "searchlight_servo", _ => null } is { } sound)
                    StoryAudio.Play(sound, at);
                break;
            case ["pickup", _]:
                // TODO(merge, WP-G): the weapon goes onto the Locator's chassis.
                StoryAudio.Play("clamp_release", at);
                break;
            case ["rumble", ..]:
                StoryAudio.Play("metal_groan_big");
                if (World.HeroOf(Me) is { } hero)
                    CameraRig.Shake(ActorView.World(hero.Position), 3 * Math.Max(0.2f, Number(1)), 2);
                break;
        }
    }

    /// <summary>A prop the director set down (debris in the Notgang), shown as the deck's artefacts are and kept by its placement.</summary>
    private void ShowPlacedArtefact(int placement, string type, System.Numerics.Vector2 position, float degrees)
    {
        if (_artefactModels.ContainsKey(placement) || World.FindTarget(Machines.Sim.World.Artefact.IdBase + placement) is not Machines.Sim.World.Artefact artefact)
            return;
        var place = new Machines.Import.Model.ArtefactPlacement(type, new System.Numerics.Vector3(position.X, position.Y, 0), degrees, null, 0);
        if (Props.Artefact(artefact.Type, place, new Godot.Vector3(position.X, 0, position.Y)) is not { } model)
            return;
        AddChild(model);
        _artefactModels[placement] = model;
    }

}
