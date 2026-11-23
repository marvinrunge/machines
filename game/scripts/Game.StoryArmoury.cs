namespace Machines.Game;

// Stage 7's hook for package W (section 6, the armoury) in the game (plan stufe7 §3, F item 13): its cues for the view. A stub until the
// package takes this file over.
public partial class Game
{
    /// <summary>A director's cue of this package's section (Game.StoryCue forwards what it does not know); false: not its cue.</summary>
    private bool StoryCueArmoury(string[] words, Godot.Vector3 at) => false;
}
