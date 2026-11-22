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
    /// For pictures of a chapter: MACHINES_SCREENSHOT_STORY=x,y[,angle[,ahead]] sets the trio down at that point of the deck (metres,
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
        // A fourth number sets Ike and Bob that far ahead instead (behind when negative), to keep them out of a picture.
        var ahead = at.Length > 3 ? Number(at[3]) : 7f;
        for (var i = 0; i < story.Seats.Count; i++)
        {
            if (World.HeroOf(story.Seats[i]) is not { } hero)
                continue;
            hero.Position = i == 0 ? spot : spot + forward * ahead + side * (i == 1 ? 2.5f : -2.5f);
            hero.Facing = facing;
        }
        _aimYaw = facing;
        SyncViews();
    }

    private bool _storyGoggles;

    /// <summary>The objective last called out, and in which world (a checkpoint's world calls its objective out afresh).</summary>
    private (string? Text, object? World) _calledObjective;

    // ---- the story's keys ----------------------------------------------------------------------------

    /// <summary>
    /// The keys of a chapter, driving or down: the Locator tells its Grunts to follow, wait or distract, sends out its
    /// scanner's pulse (the chassis' special), switches its lamp (any hero of the trio), and dashes or uses an ability where
    /// its chassis has one. None of Wired's squad orders and no hangar: the story has neither (GameWorld.Borrowed). Hacking
    /// and using are the hack's key (HackHost).
    /// </summary>
    private void StoryInput(Godot.InputEvent @event)
    {
        // Shift (or the left stick's click) is the sprint, held while driving (StorySprintHeld); pressed while the limiter
        // holds it, the HUD says why, once.
        if (@event is Godot.InputEventKey { Pressed: true, Echo: false, Keycode: Godot.Key.Shift }
            or Godot.InputEventJoypadButton { Pressed: true, ButtonIndex: Godot.JoyButton.LeftStick })
            StorySprintPressed();
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
        FilmLimiter(false);
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
    /// where they stand; its cues play the cutscene (Abwurf) and the core fight (the barricades straining and thrown aside,
    /// the break-in, the core hit and blowing), lean the view, show the props it set down and the scares; a new objective
    /// is announced; the alarm sounds; and the deck's lights follow the run's. Nothing here changes the world.
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
                case GameEventKind.StoryHack when Machines.Sim.Story.StoryEvents.HackOf(e) is { } hack:
                    // A line waiting on the loudspeakers that the hack made untrue ("launch clearance denied") is not said.
                    if (hack.What == "done" && Machines.Sim.Story.Prologue.StaleOnceHacked.TryGetValue(hack.Target, out var stale))
                        StoryAudio.Withdraw(stale);
                    // This player's own hack sounds from its board; another's is heard at the target.
                    if (e.Player != Me && hack.What switch { "open" or "done" or "board" => HackSounds.Connect, "traced" => HackSounds.TraceBack, "broken" => HackSounds.Fail, _ => null } is { } sound)
                        StoryAudio.Play(sound, at);
                    break;
                case GameEventKind.StoryCue when Machines.Sim.Story.StoryEvents.CueOf(e) is ["drive", var state]:
                    DriveCue(state, e.Actor, at);
                    break;
                case GameEventKind.StoryCue:
                    StoryCue(Machines.Sim.Story.StoryEvents.CueOf(e), at);
                    break;
                case GameEventKind.StoryObjective when e.Detail is { Length: > 0 } text:
                    // The same objective set again (a beat that only moves its place) is not called out a second time.
                    if (text != _calledObjective.Text || !ReferenceEquals(World, _calledObjective.World))
                        _hud.Story?.Announce(Lang.T("Objective").Caps(), Lang.T(text), MenuTheme.Glow, 2.5f);
                    _calledObjective = (text, World);
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
        BootDrips();
        // The deck's light groups follow the sight's (GameWorld.StoryLightState) in FollowDeck.
    }

    /// <summary>A line of the line table spoken (its subtitle comes as it starts); without the story's audio only its subtitle.</summary>
    private void SayStoryLine(string id)
    {
        if (StoryAudio.Current is not null)
            StoryAudio.Say(id);
        else
            ShowStoryLine(id, null);
    }

    /// <summary>A line's voice starts (or, without one, its turn): its subtitle, timed by the voice file when there is one.</summary>
    private void StoryLineStarted(StoryLineCue cue) => ShowStoryLine(cue.Id, cue.Voiced ? cue.Seconds : null);

    /// <summary>
    /// What a line shows as it starts: its subtitle; the core's decryption is the HUD's own bar instead (it says what the
    /// subtitle would, with its share); the update's whisper breaks the view up with static as it comes.
    /// </summary>
    private void ShowStoryLine(string id, double? clip)
    {
        if (id == Machines.Sim.Story.Prologue.Lines.HudDecrypt && _hud.Story is { } hud)
        {
            hud.Decrypt();
            return;
        }
        if (StoryLines.Find(id) is not { } line || SubtitleQueue.Parse(line.Speaker) is not { } speaker)
            return;
        if (speaker == StorySpeaker.Whisper)
            _screen.Interference(GameOptions.SoftScares ? 0.3f : 0.6f, 0.9f);
        _hud.Subtitles?.Say(speaker, line.English, clip);
    }


    /// <summary>The words of a director's cue: what the view shows and plays of it.</summary>
    private void StoryCue(string[] words, Godot.Vector3 at)
    {
        float Number(int i) => words.Length > i && float.TryParse(words[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        switch (words)
        {
            case ["cutscene", var name]:
                // The chapter's last cutscene (the pod's drop in the bay) ends black, and black it stays until its film goes on outside.
                var last = World.Story is { } ending && StoryFilm.Of(ending.Options.Chapter, Online)?.Cutscene == name;
                // The music runs into the last cutscene and out over its first seconds (its silence would let the buses
                // back up as it ends, music and all, under the black).
                if (last)
                    StoryAudio.MusicOut(6);
                PlayCutscene(name, last ? _ => HoldStoryBlack() : null);
                break;
            case ["debris", "falling", ..]:
                // Debris on its way down (the director's Fall): sparks where it tears loose and a groan overhead, a moment before it lands.
                _effects.SparkShower(at + Godot.Vector3.Up * 7, Godot.Vector3.Down, 60);
                StoryAudio.Play("metal_groan_big", at);
                break;
            case ["debris", "landed", _]:
                StoryAudio.Play("crate_fall", at);
                CameraRig.Shake(at, 2.5f, 0.5f);
                _effects.SparkShower(at + Godot.Vector3.Up * 0.5f, Godot.Vector3.Up, 40);
                break;
            case ["artefact", var placement, var type, ..] when int.TryParse(placement, out var index):
                ShowPlacedArtefact(index, type, new System.Numerics.Vector2(Number(3), Number(4)), Number(5));
                break;
            case ["tilt", ..]:
                TiltTo(new CameraTilt(Number(1), Number(2), Sway: Number(1) > 6 ? 0.8f : 0), Number(3));
                break;
            case ["door", _]:
                // The door's sound comes with its leaves sliding open (Game.Deck, SlideDoorLeaf).
                break;
            case ["boot", var step]:
                BootCue(step);
                break;
            case ["clamp", _]:
                StoryAudio.Play("clamp_release", at);
                break;
            case ["sprint", "free"]:
                SprintFreed();
                break;
            case ["hint", var hint]:
                // How to push crates is for a Grunt's player, how to take cover for the Locator's.
                if (World.Story is { } told && (hint, Me == told.Locator) is not ("crates", true) and not ("cover", false))
                    _hud.Story?.Hint(hint);
                break;
            case ["alarm"]:
                StoryAudio.Sting("sting_alarm");
                break;
            case ["squad"]:
                // The lift's doors open far down the spine.
                StoryAudio.Play("door_open", at);
                break;
            case ["crate", "pushed"]:
                StoryAudio.Play("crate_push", at);
                break;
            case ["crate", "set"]:
                StoryAudio.Play("crate_fall", at);
                break;
            case ["barricade", var door, "strain"]:
                ShakeBarricade(door, at);
                break;
            case ["burst", _]:
                // The squad through a door: a blast in the doorway (the crates fly by their own cue, "thrown").
                _effects.Explosion(at + Godot.Vector3.Up * 2, 6);
                StoryAudio.Play("hull_breach", at);
                CameraRig.Shake(at, 6, 0.8f);
                break;
            case ["break", "in"]:
                // Erwischt: the score cuts to the loud part of Lost In The Dark.
                StoryAudio.Caught();
                StoryAudio.Sting("sting_found");
                _screen.Interference(GameOptions.SoftScares ? 0.2f : 0.45f, 0.8f);
                break;
            case ["barricade", _, "fire"]:
                StoryAudio.Sting("sting_dread");
                break;
            case ["thrown", var thrown, _] when int.TryParse(thrown, out var index):
                ThrowCrate(index, at);
                break;
            case ["core", "hit", _]:
                CoreHit(at, (int)Number(2));
                break;
            case ["core", "blows"]:
                CoreBlows(at);
                break;
            case ["control", "lost"]:
                StoryAudio.Play("pa_cutoff");
                _screen.Interference(GameOptions.SoftScares ? 0.25f : 0.5f, 1.2f);
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
                // The weapon is on the Locator's chassis already (the refit shows it); its driver hears and reads what it took.
                StoryAudio.Play("clamp_release", at);
                if (World.Story is { } taken && Me == taken.Locator)
                    _hud.Story?.Float("+ " + Lang.T("Bolter"));
                break;
            case ["rumble", ..]:
                StoryAudio.Play("metal_groan_big");
                if (World.HeroOf(Me) is { } hero)
                    CameraRig.Shake(ActorView.World(hero.Position), 3 * Math.Max(0.2f, Number(1)), 2);
                break;
        }
    }

    /// <summary>
    /// The squad at a barricade (hammering, then shooting): its crates jolt where they stand, the blows boom and grind, sparks
    /// fly off them and the floor shakes a little.
    /// </summary>
    private void ShakeBarricade(string door, Godot.Vector3 at)
    {
        StoryAudio.Play(Godot.GD.Randf() < 0.5f ? "door_jammed" : "gate_slam", at);
        CameraRig.Shake(at, 1.6f, 0.35f);
        foreach (var crate in World.BarricadeCrates(door))
        {
            if (!_artefactModels.TryGetValue(crate.Placement, out var model) || !IsInstanceValid(model))
                continue;
            var rest = model.Position;
            var jolt = new Godot.Vector3((float)Godot.GD.RandRange(-0.25, 0.25), (float)Godot.GD.RandRange(0, 0.15), (float)Godot.GD.RandRange(-0.25, 0.25));
            var tween = model.CreateTween();
            tween.TweenProperty(model, "position", rest + jolt, 0.06);
            tween.TweenProperty(model, "position", rest, 0.18).SetTrans(Godot.Tween.TransitionType.Bounce).SetEase(Godot.Tween.EaseType.Out);
            _effects.SparkShower(ActorView.World(crate.Position) + Godot.Vector3.Up * 1.2f, null, 12);
        }
    }

    /// <summary>Crates taken off whole this frame, by placement, until a "thrown" cue sends them flying (or FollowCrates frees them).</summary>
    private readonly Dictionary<int, Godot.Node3D> _justRemoved = [];

    /// <summary>
    /// A barricade's crate blown aside as the squad bursts in: it tumbles away from the doorway into the room, lands with a
    /// crash and lies where it fell (the world has it no more).
    /// </summary>
    private void ThrowCrate(int placement, Godot.Vector3 at)
    {
        if (!_justRemoved.Remove(placement, out var model) || !IsInstanceValid(model))
            return;
        var into = World.HeroOf(Me) is { } hero ? ActorView.World(hero.Position) - at : Godot.Vector3.Forward;
        into.Y = 0;
        into = into.LengthSquared() < 0.01f ? Godot.Vector3.Forward : into.Normalized();
        var side = new Godot.Vector3(-into.Z, 0, into.X) * (float)Godot.GD.RandRange(-0.6, 0.6);
        var start = model.Position;
        var land = start + (into + side).Normalized() * (float)Godot.GD.RandRange(4.0, 7.0);
        var spin = model.Rotation + new Godot.Vector3((float)Godot.GD.RandRange(-2.5, 2.5), (float)Godot.GD.RandRange(-3.0, 3.0), (float)Godot.GD.RandRange(-2.5, 2.5));
        var flight = model.CreateTween().SetParallel();
        flight.TweenMethod(Godot.Callable.From((float t) => model.Position = start.Lerp(land, t) + Godot.Vector3.Up * 3.5f * 4 * t * (1 - t)), 0f, 1f, 0.7);
        flight.TweenProperty(model, "rotation", new Godot.Vector3(Godot.Mathf.Round(spin.X / Godot.Mathf.Pi * 2) * Godot.Mathf.Pi / 2, spin.Y, Godot.Mathf.Round(spin.Z / Godot.Mathf.Pi * 2) * Godot.Mathf.Pi / 2), 0.7);
        GetTree().CreateTimer(0.7).Timeout += () => StoryAudio.Play("crate_fall", land);
    }

    /// <summary>The Reapers' rounds crack the core: sparks off it, the higher the more; at the third, fire breaks out on it.</summary>
    private void CoreHit(Godot.Vector3 at, int hit)
    {
        StoryAudio.Play("sparks", at);
        _effects.SparkShower(at + Godot.Vector3.Up * (6 + 3 * hit), null, 40 + 20 * hit);
        if (hit >= 3)
        {
            Burn(at + Godot.Vector3.Up * 3, 2, 3);
            _screen.Interference(GameOptions.SoftScares ? 0.15f : 0.35f, 0.6f);
        }
    }

    /// <summary>
    /// The core blows: the ship's announcement breaks off mid-word, a great blast and a second, the deck shakes, the view
    /// breaks up, and the core's wreck burns on in the red emergency light.
    /// </summary>
    private void CoreBlows(Godot.Vector3 at)
    {
        StoryAudio.CutVoice();
        _effects.Explosion(at + Godot.Vector3.Up * 4, 18);
        StoryAudio.Play("hull_breach", at);
        CameraRig.Shake(at, 12, 2);
        Burn(at + Godot.Vector3.Up * 2, 3, 4.5f);
        // The blast is seen first; the second, higher up, breaks the Locator's view up for a moment.
        GetTree().CreateTimer(0.7).Timeout += () =>
        {
            if (!IsInstanceValid(this) || !IsInstanceValid(_effects))
                return;
            _effects.Explosion(at + Godot.Vector3.Up * 16, 12);
            _screen.Interference(GameOptions.SoftScares ? 0.4f : 0.75f, 1.4f);
        };
        StoryAudio.SetLayer("amb_fire", true, -16);
    }

    /// <summary>Flames left burning for good under the match's effects.</summary>
    private void Burn(Godot.Vector3 at, float width, float height)
    {
        if (Effects.DamageFire(width, height) is not { } fire)
            return;
        _effects.AddChild(fire);
        fire.GlobalPosition = at;
        fire.Emitting = true;
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
