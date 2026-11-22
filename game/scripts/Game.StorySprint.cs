using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// The trio's sprint in a story (Machines.Sim.Story.StoryDrive): Shift held while driving, or the left stick's click on a pad;
// locked by the maintenance limiter until the story frees it, and limited by the drive's heat after. The sim decides
// everything; here are the key, the HUD's lines and the sounds.
public partial class Game
{
    /// <summary>
    /// Whether Shift turns the upper body and looks with the arrows, as in the original's first person: everywhere but in a
    /// story, where it is the sprint.
    /// </summary>
    private static bool ShiftLooks => Input.IsKeyPressed(Key.Shift) && !GraphicsSettings.Story;

    /// <summary>Whether the driver holds the sprint now: Shift or the left stick's click, in a story only.</summary>
    private bool StorySprintHeld() => World.Story is not null
        && (Input.IsKeyPressed(Key.Shift) || Input.GetConnectedJoypads().Count > 0 && Input.IsJoyButtonPressed(0, JoyButton.LeftStick));

    // The limiter's line is shown on the first press only, a chapter long.
    private bool _limiterShown;

    /// <summary>The sprint pressed: while the limiter holds it, the drive says so, the first time.</summary>
    private void StorySprintPressed()
    {
        if (World.Story is null || World.StorySprintFree || _limiterShown || _driven is null)
            return;
        _limiterShown = true;
        _hud.Story?.Readout(Lang.T("DRIVE: MAINTENANCE LIMITER ACTIVE"), HudInk.Warning);
        StoryAudio.Play("hack_fail");
    }

    /// <summary>The story overrode the limiter (in the prologue as the core is hit): the drive says so, and how to sprint.</summary>
    private void SprintFreed()
    {
        _limiterShown = true;
        StoryAudio.Play("hack_connect");
        _hud.Story?.Readout(Lang.T("SPEED LIMITER ... OVERRIDDEN"), MenuTheme.Glow);
        _hud.Story?.SprintHint();
    }

    /// <summary>
    /// A drive of the trio overheated ("hot": it sputters, sparks fly off it, its driver reads it) or cooled down enough to
    /// sprint again ("cool").
    /// </summary>
    private void DriveCue(string state, int hero, Vector3 at)
    {
        var mine = World.HeroOf(Me)?.Id == hero;
        if (state == "hot")
        {
            StoryAudio.Play("hud_glitch", mine ? null : at);
            StoryAudio.Play("sparks", at);
            _effects.SparkShower(at + Vector3.Up * 0.8f, null, 18);
            if (mine)
            {
                _hud.Story?.Readout(Lang.T("DRIVE OVERHEATED"), HudInk.Danger);
                _screen.Interference(GameOptions.SoftScares ? 0.08f : 0.15f, 0.4f);
            }
        }
        else if (state == "cool" && mine)
            StoryAudio.Play("loc_servo");
    }
}
