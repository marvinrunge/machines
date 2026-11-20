using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>
/// The Locator plugs its probe into a target (a deck marker's name such as <c>terminal_ike</c>, or one the director
/// registered): it must stand within <see cref="GameWorld.HackReach"/> of it, and from then on it stands still while the
/// world goes on, until the hack is won, walked away from or traced.
/// </summary>
public sealed record StoryHackOpenCommand(Race Race, string Target) : StoryCommand(Race)
{
    protected internal override string? ApplyTo(GameWorld world) => world.OpenHack(this);
}

/// <summary>
/// Turns a tile of the hack's board a quarter, clockwise or back (a cell by its index, row by row from the north-west).
/// Each turn is its own lockstep command, so the trace-back, the firewall and the story's break-off happen on the world's
/// ticks for every player and every recording alike.
/// </summary>
public sealed record StoryHackTurnCommand(Race Race, string Target, int Cell, bool Clockwise = true) : StoryCommand(Race)
{
    protected internal override string? ApplyTo(GameWorld world) => world.TurnHack(this);
}

/// <summary>The Locator pulls its probe out: the board stays as it lies, at the cost of some trace-back.</summary>
public sealed record StoryHackAbortCommand(Race Race) : StoryCommand(Race)
{
    protected internal override string? ApplyTo(GameWorld world) => world.AbortHack(this);
}
