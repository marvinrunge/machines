using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// A command of the story's own (hacking, orders to the Grunts, the lamp, using things). Each kind applies itself, so that
/// the work packages that add them never share a switch: <c>GameWorld.Apply</c> hands every one to <see cref="ApplyTo"/>,
/// and refuses it outside a story. The lockstep's JSON finds every subclass by itself (NetJson); a new one changes the
/// protocol.
/// </summary>
public abstract record StoryCommand(Race Race) : Command(Race)
{
    /// <returns>null when applied, otherwise why it was refused.</returns>
    protected internal abstract string? ApplyTo(GameWorld world);
}
