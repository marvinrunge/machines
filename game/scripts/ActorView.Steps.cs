using Godot;
using Machines.Sim.World;

namespace Machines.Game;

// L-7's steps in a story (play test 2026-11-24: its walk sounded like the original's engine loop, not like metal legs on a
// steel deck): each foot that comes down (RigView.Plants, found in its walk) plays one of the story's l7_step sounds at the
// foot, a little higher or lower each time, and its engine keeps only its idle hum while it walks.
public partial class ActorView
{
    /// <summary>The machine whose feet step audibly (the Locator of a running story; 0 for none). Game.StoryStealth sets it each frame.</summary>
    internal static int SteppingId { get; set; }

    /// <summary>The steps' level, dB: about 8 % of the sound's own (play test 2026-11-24, "viel leiser, 5-10 % der Lautstärke").</summary>
    private const float StepDb = -22;

    private float? _lastStep;

    /// <summary>Whether this machine walks by its steps instead of its engine's movement loop.</summary>
    private bool Steps => SteppingId != 0 && ActorId == SteppingId;

    partial void StoryFrameSteps(double delta)
    {
        if (!Steps || _rigView is not { } rig || rig.WalkPhase is not { } phase || !Visible)
        {
            _lastStep = null;
            return;
        }
        if (_lastStep is { } last && last != phase)
        {
            var wrapped = phase < last;
            if (!wrapped || last - phase > 0.5f)
                foreach (var plant in rig.Plants)
                    if (wrapped ? plant.Phase > last || plant.Phase <= phase : plant.Phase > last && plant.Phase <= phase)
                    {
                        var foot = rig.PartPosition(plant.Node);
                        StoryAudio.Play("l7_step", World(new System.Numerics.Vector2(foot.X, foot.Z)), 0, (float)GD.RandRange(0.9, 1.1), StepDb);
                    }
        }
        _lastStep = phase;
    }
}
