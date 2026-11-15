namespace Machines.Sim.World;

/// <summary>
/// The Phantom's shadow, the remake's own addition: a quiet Phantom hero cannot be locked on from afar. Not built yet:
/// nothing is hidden.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>Whether <paramref name="seeker"/> loses sight of <paramref name="target"/> in its shadow. Only a question.</summary>
    public bool Shadowed(Actor seeker, Actor target) => false;
}
