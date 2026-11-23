using Godot;

namespace Machines.Game;

// Stage 7's hook for package S (section 2, the Thresher) in the game (plan stufe7 §3, F item 13): its cues for the view. A stub until the
// package takes this file over.
public partial class Game
{
    /// <summary>A director's cue of this package's section (Game.StoryCue forwards what it does not know); false: not its cue.</summary>
    private bool StoryCueStealth(string[] words, Godot.Vector3 at) => false;

    /// <summary>
    /// The creep held on the pad: the right stick pressed in (R3), as the left stick sprints (Game.StorySprint); Ctrl rides
    /// on DriveCommand.Slow in Game.cs. Creeping halves the moving part of the signature and blocks the sprint (StoryDrive.Creep).
    /// </summary>
    private bool StoryCreepHeld() => World.Story is not null
        && Input.GetConnectedJoypads().Count > 0 && Input.IsJoyButtonPressed(0, JoyButton.RightStick);
}
