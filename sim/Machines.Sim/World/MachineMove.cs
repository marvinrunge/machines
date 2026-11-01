namespace Machines.Sim.World;

/// <summary>What kind of move a machine began (see <see cref="MachineMove"/>).</summary>
public enum MoveKind
{
    /// <summary>A turn on the spot (MachPhysMachine::turn, or a move's first segment).</summary>
    Turn,
    /// <summary>A run along straight chunks of way to a stop (MachPhysMachine::move, one per chunk).</summary>
    Run,
    /// <summary>Stopped at once, in the middle of a move (MachPhysMachine::stopDead).</summary>
    StopDead,
    /// <summary>Driven in first person and not driving on (MachPhysLocomotionMethod::STOPPED; turning counts as this).</summary>
    Halted,
    /// <summary>Driven in first person, forwards (MOVING_FORWARDS).</summary>
    Forwards,
    /// <summary>Driven in first person, backwards (MOVING_BACKWARDS).</summary>
    Backwards,
}

/// <summary>
/// One straight chunk of a run, in seconds from the run's start: when it begins and ends, and how long of it the
/// machine speeds up and how long it slows down (PhysRampAcceleration's first and second acceleration times).
/// </summary>
public readonly record struct MoveChunk(float Start, float End, float SpeedingUp, float SlowingDown);

/// <summary>
/// The last move a machine began, as the original told its physical machine of it, for the view to animate a
/// hovering or gliding machine's parts as MachPhysLocomotionMethod::moveAnimations planned them. A turn has the angle
/// (radians, anticlockwise) and how long it takes; a run has its chunks. Nothing in the simulation reads it.
/// </summary>
/// <param name="Start">World time the move began at.</param>
public sealed record MachineMove(MoveKind Kind, float Start, float Duration = 0, float TurnBy = 0, IReadOnlyList<MoveChunk>? Chunks = null);
