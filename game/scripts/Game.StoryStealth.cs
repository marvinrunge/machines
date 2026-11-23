namespace Machines.Game;

// Stage 7's hook for package S (section 2, the Thresher) in the game (plan stufe7 §3, F item 13): its cues for the view. A stub until the
// package takes this file over.
public partial class Game
{
    /// <summary>A director's cue of this package's section (Game.StoryCue forwards what it does not know); false: not its cue.</summary>
    private bool StoryCueStealth(string[] words, Godot.Vector3 at) => false;

    /// <summary>The creep held (Ctrl rides on DriveCommand.Slow, Game.cs); package S binds the pad here. False until then.</summary>
    private bool StoryCreepHeld() => false;
}
