using Godot;

namespace Machines.Game;

// Stage 7's stub for package S (plan stufe7 C20): the Thresher's stomps, plants and occlusion on the Thresher bus. Until S
// takes this file over, a stomp is one of the existing thresher_step_1..3, so package E can call Stomp already.
public partial class StorySounds
{
    /// <summary>One of the Thresher's steps at a place (the hunt gait's stomp); <paramref name="weight"/> 0-1 for S's stomp system.</summary>
    public void Stomp(Vector3 at, float weight = 1) => Play("thresher_step", at);
}
