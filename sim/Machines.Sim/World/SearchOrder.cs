using System.Numerics;

namespace Machines.Sim.World;

/// <summary>
/// The story's search (plan WP-D, GameWorld.StorySight): go to <paramref name="Spot"/>, where a hero was last seen or heard,
/// and look about there, sweeping, until <paramref name="Until"/>; then take up again <paramref name="Back"/>, what the
/// hunt or the noise broke off (its patrol), or stand. <paramref name="Since"/> is when it got there, and
/// <paramref name="Facing"/> the heading (radians) it sweeps about.
/// </summary>
public sealed record SearchOrder(Vector2 Spot, float Until, Interrupted? Back, float? Since = null, float Facing = 0) : Order;
