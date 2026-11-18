using System.Numerics;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

/// <summary>
/// The Phantom's shadow, the remake's own addition: a hero on a chassis with one that has not fired, nor given itself away
/// otherwise (GameWorld.Reveal: a damaging ability, an enemy pod's capture circle), for
/// <see cref="WiredBalance.ShadowQuiet"/> seconds cannot be picked out by an enemy farther than
/// <see cref="WiredBalance.ShadowReach"/> metres from it, unless an enemy's geo locator has located it (GameWorld.WiredLocate):
/// then it is seen at any distance until that runs out. Nearer it is seen as any machine is, and mines, the ion storm and
/// a human enemy's own aim never look for it. Laying mines does not give it away. What a seeker makes of the shadow is
/// at the seams: <see cref="NearestEnemy"/> passes it over, an attack on it is dropped, the waves' hunters go for the
/// pod instead (GiveOrders and ReviewFoes alike), and a battle bot leaves it off its list of enemies.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>
    /// Whether <paramref name="seeker"/> loses sight of <paramref name="target"/> in its shadow: in a Wired match the
    /// target is a hero wearing a chassis with a shadow, quiet for long enough and not located by an enemy's geo locator,
    /// and the seeker an enemy of it farther than <see cref="WiredBalance.ShadowReach"/> from its edge. Only a question,
    /// cheap enough to be asked of every enemy in reach of every machine that looks for one.
    /// </summary>
    public bool Shadowed(Actor seeker, Actor target)
    {
        if (Wired is not { } run || target is not Machine || !run.Heroes.TryGetValue(target.Owner, out var state)
            || state.Hero != target.Id || !AreEnemies(seeker.Race, target.Race) || !QuietForShadow(state)
            || (WornSpecial(state) & ChassisSpecial.Shadow) == 0 || LocatedFor(target.Id) > 0)
            return false;
        var reach = WiredBalance.ShadowReach + target.Radius;
        return Vector2.DistanceSquared(seeker.Position, target.Position) > reach * reach;
    }

    /// <summary>Whether a hero has neither fired nor given itself away for long enough to be hidden in a shadow, if it has one.</summary>
    private bool QuietForShadow(WiredState state) => Time - MathF.Max(state.LastShotAt, state.RevealedAt) >= WiredBalance.ShadowQuiet;

    /// <summary>
    /// Where a hero's shadow stands, for the HUD: "Hidden" with no wait, or "Seen" with the seconds until it is hidden
    /// again, by the same rule as <see cref="Shadowed"/>: the longer of the quiet still to keep and the time it stays
    /// located.
    /// </summary>
    private (string Phase, float SecondsLeft) ShadowPhase(WiredState state)
    {
        var located = state.Hero is { } hero ? LocatedFor(hero) : 0;
        if (QuietForShadow(state) && located <= 0)
            return ("Hidden", 0);
        var quietFor = Time - MathF.Max(state.LastShotAt, state.RevealedAt);
        return ("Seen", MathF.Max(Math.Clamp(WiredBalance.ShadowQuiet - quietFor, 0, WiredBalance.ShadowQuiet), located));
    }
}
