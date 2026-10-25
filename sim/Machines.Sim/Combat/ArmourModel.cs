namespace Machines.Sim.Combat;

/// <summary>Armour and hit points of one actor after a hit.</summary>
public readonly record struct Durability(int HitPoints, int Armour)
{
    public bool IsDead => HitPoints <= 0;
}

/// <summary>
/// The original damage rule (<c>MachActor::updateArmourAndHps</c>): armour absorbs all but one
/// point of every hit until it runs out, and hit points take the rest.
/// </summary>
public static class ArmourModel
{
    public static Durability ApplyHit(Durability current, int damage)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(damage);

        // Kept exactly as in 1999: a zero-damage hit gives absorbed = -1, so it adds one armour
        // point and takes one hit point. Weapons with no damage never call this in the original.
        var absorbed = Math.Min(damage - 1, current.Armour);

        return new Durability(
            HitPoints: current.HitPoints - (damage - absorbed),
            Armour: current.Armour - absorbed);
    }

    /// <summary>
    /// Armour grows back by itself at the machine's repair rate (per second), up to its maximum.
    /// Hit points are not restored this way.
    /// </summary>
    public static float Regenerate(float armour, int maxArmour, float repairRatePerSecond, float seconds) =>
        Math.Min(maxArmour, armour + repairRatePerSecond * seconds);
}
