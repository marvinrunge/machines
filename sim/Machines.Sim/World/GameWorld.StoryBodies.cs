using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// What a story adds to the fitted bodies every match stands and is hit in (GameWorld.Bodies): a story always plays in
/// them, whatever its options or recording say (<see cref="FittedBodies"/>); a few of its cast keep the bodies measured from
/// the looks the story draws (<see cref="StoryBodies"/>); every machine, on an order or not, stops where its body would
/// meet another's (<see cref="Blocker"/>); and a round that does not fall off with the distance reaches a body only by
/// <see cref="StorySplash"/>, so one that went past a Grunt and struck the wall behind it no longer hurts it.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>How far the end of a round that does not fall off reaches a machine's body in a story, metres (the original: 1 m for a bolt, 3 m for the rest).</summary>
    public const float StorySplash = 0.5f;

    /// <summary>A machine's fitted body, in a story the story's own where it has one; null for a flyer and one without a body.</summary>
    private MachineBodies.Body? FittedBodyFor(Machine machine) =>
        Story is not null && machine.FittedBody is not null
            && StoryBodies.Own(machine.Spec.Type, machine.Spec.SubType, machine.Stats.HardwareLevel) is { } own
            ? own
            : machine.FittedBody;
}
