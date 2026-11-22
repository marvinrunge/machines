using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// The Locator's boot in its clamp at the prologue's start (Prologue.BootSteps): what each of the director's "boot <step>"
// cues sounds and looks like in the view (StoryHud.Boot writes the lines and draws the optics). Nothing here changes the
// world: the lamp's darkness and its test are the view's alone (the sim has the Locator's lamp on throughout).
public partial class Game
{
    // The lamp's test (by the engine's clock): when it began, null while it is off. When the next drop of coolant falls.
    private double? _bootLampFrom;
    private double _nextDrip;

    /// <summary>Skips the boot (Esc held, StoryHud): a Use command, so every peer and every replay skips it in the same tick.</summary>
    public void SkipStoryBoot()
    {
        if (World.Story is { } story && Me == story.Locator && story.Flags.Contains(StoryMarks.Boot))
            Issue(new StoryUseCommand(Player, StoryMarks.SkipBoot));
    }

    /// <summary>
    /// The Locator's lamp in the view while it boots: dark until the step "lamp", then it stutters three times and stays on.
    /// Null for any other machine and once the boot is over (the sim's rule, GameWorld.StoryLampOn, again).
    /// </summary>
    private bool? BootLamp(int id)
    {
        if (World.Story is not { } story || !story.Flags.Contains(StoryMarks.Boot) || World.HeroOf(story.Locator)?.Id != id)
            return null;
        if (_bootLampFrom is not { } from)
            return false;
        var t = Time.GetTicksMsec() / 1000.0 - from;
        return t is >= 0 and < 0.1 or >= 0.25 and < 0.32 or >= 0.5 and < 0.62 or >= 0.85;
    }

    /// <summary>A step of the boot (the director's "boot &lt;step&gt;"): its sounds, the camera's jolt, the lamp's test.</summary>
    private void BootCue(string step)
    {
        var hero = World.Story is { } story && World.HeroOf(story.Locator) is { } locator ? locator : null;
        var at = hero is null ? Vector3.Zero : ActorView.World(hero.Position) + Vector3.Up * 1.5f;
        var facing = hero is null ? 0 : hero.Facing;
        var ahead = new Vector3(MathF.Cos(facing), 0, MathF.Sin(facing));
        var side = new Vector3(-ahead.Z, 0, ahead.X);
        switch (step)
        {
            case "dark":
                // A moment of true silence, then the reactor's hum comes through the clamp, muffled until the optics.
                StoryAudio.Hush(1.5f);
                StoryAudio.SetMuffled("boot", true);
                _bootLampFrom = null;
                _nextDrip = World.Time + 4;
                break;
            case "relay":
                StoryAudio.Play("hack_click", at);
                break;
            case "start":
                StoryAudio.Play("hud_glitch");
                break;
            case "core":
                StoryAudio.Play("hud_glitch");
                StoryAudio.Play("hack_click", at);
                break;
            case "memory":
                StoryAudio.Play("tracker_tick");
                break;
            case "optics":
                StoryAudio.Play("hud_glitch");
                GetTree().CreateTimer(Prologue.OpticsClear).Timeout += () => StoryAudio.SetMuffled("boot", false);
                break;
            case "legs":
                // The legs pull once; the clamp creaks and holds.
                StoryAudio.Play("loc_servo", at);
                GetTree().CreateTimer(0.45).Timeout += () => StoryAudio.Play("loc_servo", at);
                GetTree().CreateTimer(0.7).Timeout += () => StoryAudio.Play("door_jammed", at);
                CameraRig.Shake(at, 1.2f, 0.6f);
                break;
            case "drive":
                StoryAudio.Play("loc_fan", at);
                break;
            case "screen":
                // The cracked wall screen across the bay flickers on.
                StoryAudio.Play("sparks", at + ahead * 6);
                break;
            case "edge":
                StoryAudio.Play("data_squeal");
                _screen.Interference(GameOptions.SoftScares ? 0.15f : 0.3f, 0.3f);
                break;
            case "update":
                StoryAudio.Play("hack_fail");
                StoryAudio.Play("hud_glitch");
                _screen.Interference(GameOptions.SoftScares ? 0.25f : 0.5f, 0.4f);
                break;
            case "radio":
                // Two faint signals in the bays either side; far down in the decks a single clang.
                StoryAudio.Play("tracker_tick", at + side * 8);
                GetTree().CreateTimer(0.4).Timeout += () => StoryAudio.Play("tracker_tick", at - side * 8);
                StoryAudio.Play("metal_groan_big", at - ahead * 60);
                break;
            case "lamp":
                StoryAudio.Play("searchlight_servo", at);
                StoryAudio.Play("sparks", at);
                _bootLampFrom = Time.GetTicksMsec() / 1000.0;
                break;
            case "lamp_off":
                // The test passed: the lamp's relay clicks home and it stays on.
                StoryAudio.Play("hack_click", at);
                break;
            case "done":
                // The boot over (or skipped: the thought speaking breaks off, and the call comes after).
                StoryAudio.SetMuffled("boot", false);
                _bootLampFrom = null;
                if (World.Story?.Flags.Contains(StoryMarks.BootSkip) == true)
                    StoryAudio.CutVoice();
                break;
        }
    }

    /// <summary>While the Locator boots: now and then a drop of coolant falls on the grating beside it.</summary>
    private void BootDrips()
    {
        if (World.Story is not { } story || !story.Flags.Contains(StoryMarks.Boot) || World.Time < _nextDrip
            || World.HeroOf(story.Locator) is not { } hero)
            return;
        _nextDrip = World.Time + 3 + GD.Randf() * 3;
        var side = GD.Randf() < 0.5f ? -1 : 1;
        var across = new Vector3(-MathF.Sin(hero.Facing), 0, MathF.Cos(hero.Facing));
        StoryAudio.Play("cooling_tick", ActorView.World(hero.Position) + across * side * (2 + GD.Randf() * 2));
    }
}
