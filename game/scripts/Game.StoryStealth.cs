using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// Package S's view of section 2, the Thresher (plan stufe7 C20, design-stealth §9): the director's and the sim's cues of the
// section as sound and dust (the window's hush, door H's shriek and slam, the valves' bangs running along their pipes to
// the far end, a spent source's burst, a Grunt's reboot chirp, L-7's fan winding down), the music's word for him each frame
// (thresher near <m> at least once a second, thresher state <word> as it changes), his plants as heard and felt (the stomp
// on his bus, the view shaking within 30 m, grit off the ceiling within 25 m), and the creep held (KeyMap.Act.Creep or the
// pad's R3). It reads the world and changes nothing in it, but for MACHINES_STORY_STOMP_TEST's offline set-up.
public partial class Game
{
    private double _nearCueClock;
    private string? _stateCue;
    private double _bigShakeUntil;
    private Action<Vector3, float>? _planted;
    private Func<int, float>? _crabOf;
    private Func<int, bool>? _trioMember;

    /// <summary>A director's cue of this package's section (Game.StoryCue forwards what it does not know); false: not its cue.</summary>
    private bool StoryCueStealth(string[] words, Godot.Vector3 at)
    {
        switch (words)
        {
            case ["window"]:
                // His beam rests on the niche's mouth: the music and the world hold their breath, only he is heard.
                StoryMusic.Hear(["hush", "1.5", "-60"]);
                StoryAudio.Hush(1.5f);
                return true;
            case ["door", "h", "shriek"]:
                StoryAudio.Play("door_h_shriek", at);
                ShakeFor(at, 3, 0.5f);
                return true;
            case ["door", "h", "slam"]:
                // No door_h_slam among the effects: the gate's slam stands in for it (the nearest heavy door).
                StoryAudio.Play("gate_slam", at);
                StoryMusic.Hear(["hush", "2", "-60"]);
                ShakeFor(at, 6, 0.6f);
                _effects.CeilingDust(at + Vector3.Up * 6, 20);
                return true;
            case ["fan", "down"]:
                // L-7 keeps so still its fan winds down (P_L7_STILL).
                if (World.Story is { } run && World.HeroOf(run.Locator) is { } locator)
                    StoryAudio.Play("loc_fan", ActorView.World(locator.Position), 0, 0.6f);
                return true;
            case ["pipe", "far", _]:
                // The valve's bang comes out at its pipe's far end (GameWorld.UpdateStealth): loud, and grit shaken off the pipe.
                StoryAudio.Play("pipe_far_clang", at + Vector3.Up * 4);
                _effects.CeilingDust(at + Vector3.Up * 5, 10);
                return true;
            case ["valve", "bang", var source, var far]:
                // No valve_bang among the effects: the pipe's bang stands in for it, a little at the valve, then the travel.
                StoryAudio.Play("pipe_bang", at + Vector3.Up * 1.5f, 0, 1.1f);
                StoryAudio.Play("pipe_travel", at + Vector3.Up * 4);
                if (World.Deck is { } deck && StoryMarks.Locate(deck, far) is { } end)
                    _effects.PipeRattle(at + Vector3.Up * 4.5f, ActorView.World(end) + Vector3.Up * 4.5f, StorySight.PipeDelay);
                _ = source;
                return true;
            case ["source", "bang", _]:
                StoryAudio.Play("pipe_bang", at + Vector3.Up);
                return true;
            case ["lure", "volley", _]:
                // The third bang: he fires into the source and it is spent (the sim fires nothing itself; burst, steam and sparks).
                StoryAudio.Play("pipe_burst", at + Vector3.Up * 2);
                _effects.SpentSource(at);
                return true;
            case ["lure", "heard", _]:
                // His halt and ping play from his own state (StorySounds.FollowThresher).
                return true;
            case ["reboot", "chirp"]:
                StoryAudio.Play("grunt_reboot_chirp", at + Vector3.Up);
                return true;
        }
        return false;
    }

    /// <summary>
    /// What first person goes slow with: in a story the creep's own key (KeyMap.Act.Creep, Ctrl at first) or the pad's R3,
    /// elsewhere Ctrl as ever. The hull then turns slowly too, and the gun's prediction counts with the same.
    /// </summary>
    private bool SlowHeld() => World.Story is not null ? KeyMap.Held(KeyMap.Act.Creep) || StoryCreepHeld() : Input.IsKeyPressed(Key.Ctrl);

    /// <summary>
    /// The creep held on the pad: the right stick pressed in (R3), as the left stick sprints (Game.StorySprint). Creeping
    /// halves the moving part of the signature and blocks the sprint (StoryDrive.Creep).
    /// </summary>
    private bool StoryCreepHeld() => World.Story is not null
        && Input.GetConnectedJoypads().Count > 0 && Input.IsJoyButtonPressed(0, JoyButton.RightStick);

    /// <summary>MACHINES_HEAVY_GAIT=1: the Thresher walks by his story gait outside a story too (a screenshot run, to look at it).</summary>
    private static readonly bool GaitTest = System.Environment.GetEnvironmentVariable("MACHINES_HEAVY_GAIT") == "1";

    /// <summary>Each frame: the Thresher of section 2 for the views, his sounds and the music's words for him.</summary>
    private void FollowStealth(double delta)
    {
        var look = World.Story is null ? null : StoryStalker.Look(World);
        ActorView.Stalker = look;
        // His feet stomp wherever he walks in a story (the escape's chase too), by his heavy gait.
        ActorView.StoryGaits = World.Story is not null || GaitTest;
        ActorView.TrioMember = World.Story is null ? null : _trioMember ??= id => World.Story is { } trio && trio.Seats.Any(s => World.HeroOf(s)?.Id == id);
        ActorView.SteppingId = World.Story is { } stepping && World.HeroOf(stepping.Locator) is { } locator ? locator.Id : 0;
        ActorView.CrabOf = World.Story is null ? null : _crabOf ??= World.CrabOf;
        ActorView.StalkerPlanted = World.Story is null ? null : _planted ??= Planted;
        // The gait test's camera keeps the walker in the middle.
        if (GaitTest && World.Story is null && World.Machines.FirstOrDefault(m => m.IsType("AGGRESSOR", "ASSASSIN") && m.Race == Player) is { } walker)
            _rig.Position = ActorView.World(walker.Position);
        StoryAudio.Current?.Sounds.FollowThresher(look, delta);
        StompTestIfAsked(look);
        if (look is not { } him)
        {
            _stateCue = null;
            return;
        }
        _nearCueClock += delta;
        if (_nearCueClock >= 1 && World.HeroOf(Me) is { } mine)
        {
            _nearCueClock = 0;
            var metres = System.Numerics.Vector2.Distance(mine.Position, him.Position);
            StoryMusic.Hear(["thresher", "near", metres.ToString("0", System.Globalization.CultureInfo.InvariantCulture)]);
        }
        var word = StoryStalker.Word(him.State);
        if (word != _stateCue)
        {
            _stateCue = word;
            StoryMusic.Hear(["thresher", "state", word]);
        }
    }

    // One of his feet came down (ActorView.Stealth): the stomp on his bus, the view shaken within 30 m, grit off the ceiling
    // within 25 m, dust at the foot when it is near enough to be seen.
    private void Planted(Vector3 foot, float weight)
    {
        if (!IsInstanceValid(this) || !IsInsideTree())
            return;
        StoryAudio.Current?.Sounds.Stomp(foot, weight);
        if (GetViewport().GetCamera3D() is not { } camera)
            return;
        var at = camera.GlobalPosition;
        var distance = new Vector2(foot.X - at.X, foot.Z - at.Z).Length();
        if (distance < 80)
            _effects.PlantDust(foot, weight);
        var strength = StoryStalker.ShakeStrength(distance) * Mathf.Min(weight, 1.5f);
        // A bigger shake (a door's slam) is not cut short by his feet.
        if (strength > 0.02f && FrameClock.Now >= _bigShakeUntil)
            CameraRig.Shake(at, 2.5f * strength, 0.35f);
        if (distance <= StoryStalker.BreathWithin)
        {
            var ahead = -camera.GlobalBasis.Z with { Y = 0 };
            _effects.CeilingDust(at + Vector3.Up * 3.5f + ahead.Normalized() * 3, (int)(4 + 10 * (1 - distance / StoryStalker.BreathWithin)));
        }
    }

    private void ShakeFor(Vector3 at, float degrees, float seconds)
    {
        CameraRig.Shake(at, degrees, seconds);
        _bigShakeUntil = FrameClock.Now + seconds;
    }

    // ---- MACHINES_STORY_STOMP_TEST ------------------------------------------------------------------

    private bool _stompTestSet;
    private double _lastStompAt = -1;

    /// <summary>
    /// MACHINES_STORY_STOMP_TEST=1 (with MACHINES_STORY=prolog,1 and MACHINES_MAP=prolog), offline: the Locator is put in the
    /// niche off deck A (niche_l7) with its lamp off, out of his sight and unhurt, and the Thresher walks his round past it,
    /// in and out of the deck's walls; =x,y puts it there instead (metres of the deck). Each plant prints a line: the time,
    /// the gap since the last, distance, walls and grates between, gain, the shares of the near, middle and far families and whether the sub came with it.
    /// </summary>
    private void StompTestIfAsked(StalkerLook? look)
    {
        if (_stompTestSet || look is null
            || System.Environment.GetEnvironmentVariable("MACHINES_STORY_STOMP_TEST") is not { Length: > 0 } asked || asked == "0"
            || World.Story is not { } story || World.Deck is not { } deck)
            return;
        _stompTestSet = true;
        if (Online)
        {
            GD.PrintErr("stomp test: offline only");
            return;
        }
        if (World.HeroOf(story.Locator) is not { } locator)
            return;
        float Number(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        var at = asked.Split(',') is { Length: >= 2 } spot ? new System.Numerics.Vector2(Number(spot[0]), Number(spot[1]))
            : deck.Markers.FirstOrDefault(m => m.Name == "niche_l7")?.Position ?? locator.Position;
        locator.Position = at;
        World.SetStoryLamp(locator.Id, false);
        story.Stealth.Unseen.Add(story.Locator.Value);
        if (World.Wired?.Heroes.GetValueOrDefault(story.Locator) is { } state)
            state.InvulnerableUntil = float.MaxValue;
        SyncViews();
        StorySounds.StompLog = line =>
        {
            var now = FrameClock.Now;
            var gap = _lastStompAt < 0 ? 0 : now - _lastStompAt;
            _lastStompAt = now;
            GD.Print(FormattableString.Invariant($"stomp test: t={World.Time:0.00} +{gap:0.00} s {line}"));
        };
        GD.Print(FormattableString.Invariant($"stomp test: the Locator at ({at.X:0.0},{at.Y:0.0}), the Thresher {look.Value.Id} at ({look.Value.Position.X:0},{look.Value.Position.Y:0}) {StoryStalker.Word(look.Value.State)}"));
    }
}
