using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Package E's cues and frames in the view (plan stufe7 §4 E, design-escape §7, §13): the lift groaning and bulging, the
// breach's sparks racing ahead and the Notgang caving in, the hold's rolls (chains, scrapes, crashes into the bulkheads), the
// shutter's klaxon, slam, rams and burst, the gallery's wheel and the lane lever, the plates on the pack, the footbridge's
// creak and fall, the crane's beam, door Y rammed and burst, the Commander's cameo, the pods dropping. Each frame it hands
// the hazards' state to DeckView.Escape, hangs door Y's leaves as they slide shut (the deck's scenario does not have them)
// and leaves the shutter's to DeckView. The music's cues of the section (score, hush, thresher near/state, launch) are
// StoryMusic's, which hears every cue first. Sounds only from game/assets/story/sfx (StorySounds.EscapeSfx's fallbacks).
public partial class Game
{
    /// <summary>A director's cue of this package's section (Game.StoryCue forwards what it does not know); false: not its cue.</summary>
    private bool StoryCueEscape(string[] words, Vector3 at)
    {
        ActorView.EscapeWorld = World;
        float Number(int i) => words.Length > i && float.TryParse(words[i], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) ? n : 0;
        var soft = GameOptions.SoftScares;
        switch (words)
        {
            // ---- the music's (StoryMusic heard them already) and the playlist's fade, which nothing plays here
            case ["score", ..] or ["hush", ..] or ["launch"] or ["thresher", "near" or "state", ..] or ["playlist", ..]:
                return true;

            // ---- C0, the lift
            case ["lift", "groan"]:
                StoryAudio.Play("lift_groan", at);
                return true;
            case ["lift", "bulge"]:
                LiftBulge(at, soft);
                return true;
            case ["thresher", "roar"]:
                StoryAudio.Play("thresher_roar", at);
                if (!soft)
                    _screen.Interference(0.15f, 0.5f);
                return true;

            // ---- C1, the Notgang: the breach and its collapse
            case ["breach", "warn"]:
                BreachWarn(at);
                return true;
            case ["collapse", "notgang"]:
                StoryAudio.Play(StorySounds.EscapeSfx("breach_tear"), at);
                StoryAudio.Play("hull_breach", at);
                _effects.Explosion(at + Vector3.Up * 4, 10);
                CameraRig.Shake(at, soft ? 5 : 9, 1.2f);
                for (var i = 0; i < 4; i++)
                    _deckView?.Throw(i % 2 == 0 ? "debris_panel" : "debris_beam", at + new Vector3((float)GD.RandRange(-5.0, 5.0), 8, 4),
                        new Vector3((float)GD.RandRange(-2.0, 2.0), -2, (float)GD.RandRange(3.0, 7.0)), new Vector3(2, 1, 3), 1.2f);
                return true;
            case ["reopen", "notgang"]:
                StoryAudio.Play("gate_burst", at);
                _effects.Explosion(at + Vector3.Up * 3, 9);
                CameraRig.Shake(at, 7, 0.8f);
                _deckView?.BlastHeap(at);
                return true;

            // ---- C2, the hold: the rolls, the rails, the chocks, the rack, the lever
            case ["roll", _, _, _]:
                // The ship rolls: the chains that hold the containers snap loose one after another along the hold.
                StoryAudio.Play(GD.Randf() < 0.5f ? "chain_snap_1" : "chain_snap_2", at);
                StoryAudio.Play("metal_groan_big", at);
                return true;
            case ["rail", "slide", _]:
                StoryAudio.Play("chain_snap_2", at);
                return true;
            case ["rail", "brush", _, _]:
                StoryAudio.Play("container_crash", at);
                CameraRig.Shake(at, 4, 0.5f);
                _effects.SparkShower(at + Vector3.Up * 1.5f, Vector3.Up, 30);
                return true;
            case ["rail", "end", _]:
                // Into the bulkhead at its rail's end.
                StoryAudio.Play("container_crash", at);
                CameraRig.Shake(at, 5, 0.7f);
                _effects.SparkShower(at + Vector3.Up * 2, Vector3.Up, 50);
                return true;
            case ["rail", "stop", _]:
                StoryAudio.Play("crate_fall", at);
                CameraRig.Shake(at, 2, 0.4f);
                return true;
            case ["chock", _]:
                StoryAudio.Play("crate_fall", at);
                _effects.SparkShower(at + Vector3.Up * 0.5f, Vector3.Up, 20);
                return true;
            case ["rack", "loose"]:
                StoryAudio.Play("chain_snap_1", at);
                StoryAudio.Play("chain_snap_2", at);
                _effects.SparkShower(at + Vector3.Up * 4, Vector3.Up, 40);
                return true;
            case ["lever", "lanes"]:
                StoryAudio.Play("lockdown_bolt", at);
                _deckView?.EscapeDevice("lever");
                return true;

            // ---- C3, the shutter X
            case ["shutter", "klaxon"]:
                StoryAudio.Play(StorySounds.EscapeSfx("shutter_klaxon"), at);
                return true;
            case ["shutter", "slam" or "shut"]:
                StoryAudio.Play("shutter_slam", at);
                CameraRig.Shake(at, words[1] == "slam" ? 6 : 3, 0.6f);
                _effects.SparkShower(at + Vector3.Up * 0.5f, Vector3.Up, 30);
                return true;
            case ["shutter", "up"]:
                StoryAudio.Play("gate_grind", at);
                return true;
            case ["shutter", "ram", var ram]:
                StoryAudio.Play($"shutter_ram_{Math.Clamp((int)Number(2), 1, 3)}", at);
                _deckView?.RamShutter();
                CameraRig.Shake(at, 3 + Number(2), 0.5f);
                _effects.SparkShower(at + Vector3.Up * 3, Vector3.Up, 25 + 10 * ram.Length);
                return true;
            case ["shutter", "burst"]:
                StoryAudio.Play("gate_burst", at);
                if (_deckView?.BurstShutter() is { } burst)
                    _effects.Explosion(burst, 6);
                CameraRig.Shake(at, soft ? 5 : 9, 0.9f);
                return true;

            // ---- C4, the gallery
            case ["wheel", "j4"]:
                StoryAudio.Play("clamp_release", at);
                _deckView?.EscapeDevice("wheel");
                return true;

            // ---- the pack's hazards: plates on it, the jets' fire, the containers' blows
            case ["plate", "falling", _, _]:
                StoryAudio.Play("chain_snap_1", at);
                _effects.SparkShower(at + Vector3.Up * 8, Vector3.Down, 40);
                return true;
            case ["plate", "landed", _, _, _]:
                var landed = new Vector3(Number(3), 0, Number(4));
                StoryAudio.Play("crate_fall", landed);
                CameraRig.Shake(landed, 3, 0.4f);
                _effects.SparkShower(landed + Vector3.Up * 0.5f, Vector3.Up, 40);
                _deckView?.Throw("debris_panel", landed + Vector3.Up * 3, new Vector3(0, -4, 0), new Vector3(1.5f, 0.5f, 1), 1.5f);
                return true;
            case ["pack", "catch", "up", _]:
                return true;
            case ["pack", var what, var id, var outcome]:
                if (int.TryParse(id, out var member) && what is "fire" or "gas")
                    ActorView.EscapeBurning[member] = World.Time + ActorView.EscapeBurnSeconds;
                if (outcome == "destroyed")
                    _effects.Explosion(at + Vector3.Up * 2, 5);
                else
                {
                    StoryAudio.Play("sparks", at);
                    _effects.SparkShower(at + Vector3.Up * 2.5f, Vector3.Up, 30);
                }
                return true;
            case ["shove", _, _]:
                StoryAudio.Play("crate_push", at);
                return true;

            // ---- C5, the footbridge
            case ["bridge", "creak"]:
                StoryAudio.Play("bridge_creak", at);
                GetTree().CreateTimer(1.3).Timeout += () => StoryAudio.Play("bridge_sag", at);
                CameraRig.Shake(at, 2, 1.5f);
                return true;
            case ["bridge", "fall"]:
                StoryAudio.Play("bridge_fall", at);
                _deckView?.DropBridge(animate: true);
                CameraRig.Shake(at, soft ? 4 : 8, 1.5f);
                _effects.SparkShower(at + Vector3.Up, Vector3.Up, 80);
                return true;

            // ---- C6, C7, the bay
            case ["gas", "burst"]:
                // The gas main shot through (GameWorld.UpdateGasMain): the pipe bursts and Y's apron goes up in flame.
                StoryAudio.Play("pipe_burst", at + Vector3.Up * 2);
                StoryAudio.Play("jet_hiss_rise", at);
                CameraRig.Shake(at, soft ? 4 : 7, 0.8f);
                _effects.SparkShower(at + Vector3.Up * 2, Vector3.Up, 70);
                return true;
            case ["crane", "beam"]:
                StoryAudio.Play("crane_snap", at + Vector3.Up * 12);
                _deckView?.Throw("debris_beam", at + Vector3.Up * 12, new Vector3(0, -3, 0), new Vector3(0.6f, 0.2f, 1.4f), 2);
                GetTree().CreateTimer(0.9).Timeout += () =>
                {
                    StoryAudio.Play("beam_impact", at);
                    CameraRig.Shake(at, 6, 0.6f);
                    _effects.SparkShower(at + Vector3.Up, Vector3.Up, 60);
                };
                return true;
            case ["door", "Y", "ram"]:
                StoryAudio.Play($"shutter_ram_{1 + GD.Randi() % 3}", at);
                JoltLeaves('Y', new Vector3(0, 0, -0.35f));
                CameraRig.Shake(at, 3, 0.4f);
                _effects.SparkShower(at + Vector3.Up * 3, Vector3.Down, 25);
                return true;
            case ["door", "Y", "burst"]:
                // The door itself goes with its "door blown Y" and its leaves' "thrown" (BlowDoor): the dread under it.
                StoryAudio.Sting("sting_dread");
                return true;
            case ["commander", "cameo"]:
                // He raises his cannons in Y's frame and never fires (design-escape §14, question 5): their charge's whine only.
                StoryAudio.Play("thresher_step_2", at);
                GetTree().CreateTimer(1.2).Timeout += () => StoryAudio.Play("plasma_charge", at);
                return true;
            case ["pod1", "alarm"]:
                StoryAudio.Play("pod_clamp_alarm", at);
                return true;
            case ["pod1" or "pod2", "drop"]:
                DropPod(words[0] == "pod1" ? "pod_1" : "pod_2", at);
                return true;
        }
        return false;
    }

    /// <summary>The Thresher against the lift's door S: three blows, its leaves bulging towards the trio, the floor shaking.</summary>
    private void LiftBulge(Vector3 at, bool soft)
    {
        for (var i = 0; i < 3; i++)
        {
            var blow = i;
            GetTree().CreateTimer(0.05 + blow * 0.9).Timeout += () =>
            {
                StoryAudio.Play($"lift_bulge_{blow + 1}", at);
                JoltLeaves('S', new Vector3(0, 0, -0.3f - 0.15f * blow));
                CameraRig.Shake(at, (soft ? 1.5f : 3) + blow, 0.5f);
                _effects.SparkShower(at + Vector3.Up * (3 + blow), Vector3.Down, 15 + 10 * blow);
            };
        }
    }

    /// <summary>A door's leaves jolted by a blow from beyond: out <paramref name="by"/>, springing back.</summary>
    private void JoltLeaves(char letter, Vector3 by)
    {
        foreach (var leaf in World.DoorLeaves(letter))
        {
            if (!_artefactModels.TryGetValue(leaf.Placement, out var model) || !IsInstanceValid(model) || !model.Visible)
                continue;
            var rest = model.GetMeta("rest", model.Position).AsVector3();
            model.SetMeta("rest", rest);
            var tween = model.CreateTween();
            tween.TweenProperty(model, "position", rest + by, 0.06);
            tween.TweenProperty(model, "position", rest, 0.4).SetTrans(Tween.TransitionType.Elastic).SetEase(Tween.EaseType.Out);
        }
    }

    /// <summary>The breach's warning (design-escape §3 C1): sparks racing along the ceiling ahead, south from the trio to the breach.</summary>
    private void BreachWarn(Vector3 at)
    {
        StoryAudio.Play("spark_line", at);
        for (var i = 0; i < 8; i++)
        {
            var spot = at + new Vector3(0, 7, 18 - i * 4.5f);
            GetTree().CreateTimer(i * 0.15).Timeout += () => _effects.SparkShower(spot, Vector3.Down, 25);
        }
    }

    /// <summary>A pod off its cradle (C6, its drop's cue): the clamps let go, it drops out of the bay and is gone.</summary>
    private void DropPod(string marker, Vector3 at)
    {
        StoryAudio.Play("clamp_release", at);
        StoryAudio.Play("pod_drop_wind", at);
        foreach (var pod in _pods.Values.Where(p => p.GetMeta("marker").AsString() == marker && p.Visible))
        {
            var tween = pod.CreateTween();
            tween.TweenProperty(pod, "position", pod.Position + Vector3.Down * 40, 1.6).SetTrans(Tween.TransitionType.Quad).SetEase(Tween.EaseType.In);
            tween.TweenCallback(Callable.From(() => pod.Visible = false));
        }
    }

    // ---- each frame --------------------------------------------------------------------------------------------

    private bool _escapeShown;

    /// <summary>
    /// Section 7's hazards drawn from the sim's state (DeckView.Escape), between the ticks; door Y's leaves hung as they slide
    /// shut, and the shutter's leaves left to the deck's shutter; the test switch if asked. From Game.ShowDeckStory.
    /// </summary>
    private void ShowEscape(StoryRun story, double delta)
    {
        var hazards = story.Hazards;
        if (_deckView is null || story.Section < 7 && !hazards.Any)
            return;
        ActorView.EscapeWorld = World;
        _deckView.Fx ??= _effects;
        var share = Mathf.Clamp((float)(_accumulator / GameWorld.Dt), 0, 1);
        var craneLock = World.Artefacts.FirstOrDefault(a => a.Tag == GameWorld.CraneLockTag && !a.IsDead)?.Position;
        var gasMain = World.Artefacts.FirstOrDefault(a => a.Tag == GameWorld.GasMainTag && !a.IsDead)?.Position;
        _deckView.ShowEscape(hazards, World.Tick, share, delta, craneLock, gasMain);
        EscapeLeaves();
        _escapeShown = true;
        EscapeTestIfAsked(story, delta);
    }

    /// <summary>
    /// The leaves the escape shuts into X and Y (GameWorld.HangEscapeDoors) as they stand closed: Y's slid out of the walls
    /// over the door's time (at once in the first frame of a checkpoint's world), turned as the sim hung them; X's are the
    /// deck's shutter (DeckView.Escape), so they get an empty node only, which FollowCrates then leaves alone.
    /// </summary>
    private void EscapeLeaves()
    {
        if (GetNodeOrNull<Node3D>("Artefacts") is not { } root || World.Deck is not { } deck)
            return;
        foreach (var letter in "XY")
        {
            var leaves = World.DoorLeaves(letter);
            foreach (var leaf in leaves)
            {
                if (_artefactModels.ContainsKey(leaf.Placement))
                    continue;
                Node3D? model;
                if (letter == 'X')
                    model = new Node3D { Name = "ShutterLeaf" };
                else
                {
                    var placement = new Machines.Import.Model.ArtefactPlacement(leaf.Type.Name, new System.Numerics.Vector3(leaf.Position, 0), 0, leaf.Tag, 0);
                    model = Props.Artefact(leaf.Type, placement, new Vector3(leaf.Position.X, 0, leaf.Position.Y));
                }
                if (model is null)
                    continue;
                root.AddChild(model);
                _artefactModels[leaf.Placement] = model;
                if (letter == 'X' || !_escapeShown || deck.Doors.FirstOrDefault(d => d.Letter == letter) is not { } door)
                    continue;
                var closed = model.Position;
                var slide = GameWorld.LeafSlide(leaf, leaves);
                model.Position = closed + new Vector3(slide.X, 0, slide.Y);
                model.CreateTween().TweenProperty(model, "position", closed, GameWorld.SwingSeconds(door.Kind))
                    .SetTrans(Tween.TransitionType.Sine).SetEase(Tween.EaseType.InOut);
            }
        }
    }

    // ---- MACHINES_STORY_ESCAPE_TEST ----------------------------------------------------------------------------

    private bool _escapeTestSet;
    private double _escapeLogAt;

    /// <summary>Where the trio is put for each set piece of MACHINES_STORY_ESCAPE_TEST: the Locator's spot and the way it looks (degrees, 0 east, 90 north).</summary>
    private static readonly Dictionary<string, (NVector2 At, float Facing)> EscapeTestSpots = new(StringComparer.Ordinal)
    {
        ["lift"] = (new(520, 236), 90),
        ["breach"] = (new(520, 200), 270),
        ["hold"] = (new(496, 170), 180),
        ["shutter"] = (new(378, 168), 180),
        ["jets"] = (new(338, 146), 270),
        ["bridge"] = (new(438, 92), 180),
        ["bay"] = (new(466, 92), 0),
    };

    /// <summary>
    /// A look at a set piece of the escape (MACHINES_STORY_ESCAPE_TEST=lift|breach|hold|shutter|jets|bridge|bay, with
    /// MACHINES_STORY=prolog,12 and MACHINES_MAP=prolog; offline only): a few ticks into the match the trio, unhurtable,
    /// stands where the piece plays, looking at it, and the world is as a run that got there has it (the Notgang caved in and
    /// door P blown for the hold and on, the jets started for the gallery and on); the director's own beats then play it
    /// (the rolls, the shutter at the west landing, J1, the bridge's fall once all three stand in the bay, pod 1). Every
    /// second the console prints the hazards: the rails, the shutter, the jets, the collapse, the bridge.
    /// </summary>
    private void EscapeTestIfAsked(StoryRun story, double delta)
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_STORY_ESCAPE_TEST") is not { Length: > 0 } asked || asked == "0")
            return;
        if (!_escapeTestSet)
        {
            if (World.Tick < 5 || story.Section != 7)
                return;
            _escapeTestSet = true;
            if (Online)
            {
                GD.PrintErr("escape test: offline only");
                return;
            }
            if (!EscapeTestSpots.TryGetValue(asked, out var spot))
            {
                GD.PrintErr($"escape test: no set piece '{asked}' (lift, breach, hold, shutter, jets, bridge, bay)");
                return;
            }
            var later = asked is not ("lift" or "breach");
            if (later)
            {
                World.Collapse(story);
                World.BlowDoor('P');
            }
            if (asked is "jets" or "bridge" or "bay")
                World.StartJets(story);
            var facing = Mathf.DegToRad(spot.Facing);
            // The Grunts beside the Locator, across the way it looks.
            var across = new NVector2(-MathF.Sin(facing), MathF.Cos(facing)) * 4;
            for (var i = 0; i < story.Seats.Count; i++)
            {
                if (World.HeroOf(story.Seats[i]) is not { } hero)
                    continue;
                hero.Position = spot.At + across * (i == 0 ? 0 : i == 1 ? 1 : -1);
                hero.Facing = facing;
                if (World.Wired?.Heroes.GetValueOrDefault(story.Seats[i]) is { } state)
                    state.InvulnerableUntil = float.MaxValue;
            }
            _aimYaw = facing;
            SyncViews();
            GD.Print($"escape test: {asked}, the trio at {spot.At} looking {spot.Facing:0}°, unhurtable"
                + (later ? "; the Notgang caved in, door P blown" : "") + (asked is "jets" or "bridge" or "bay" ? "; the jets started" : ""));
            return;
        }
        _escapeLogAt += delta;
        if (_escapeLogAt < 1)
            return;
        _escapeLogAt = 0;
        var hazards = story.Hazards;
        var rails = string.Join(" ", hazards.Rails.Select(r => $"{r.Name}:{r.State}@{r.Centre / 1000f:0.0}"));
        var jets = hazards.JetsFrom < 0 ? "off" : string.Join(" ", Enumerable.Range(1, 5).Select(j => $"{j}:{hazards.Phase(j, World.Tick).ToString()[0]}"));
        GD.Print($"escape: t={World.Time:0} rails [{rails}] shutter {hazards.Shutter} {hazards.ShutterDown}/{StoryHazards.ShutterLower} rams {hazards.ShutterRammed}"
            + $" jets [{jets}] collapsed {hazards.Collapsed} fire {hazards.FireFace} bridge {(hazards.BridgeFallen ? "fallen" : hazards.BridgeFallAt >= 0 ? "creaking" : "up")}"
            + $" crane {(hazards.CraneDown ? "down" : "up")} staggered {hazards.Staggered.Count}");
    }
}
