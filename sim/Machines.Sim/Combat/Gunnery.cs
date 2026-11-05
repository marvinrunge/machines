namespace Machines.Sim.Combat;

/// <summary>
/// How steady an actor's aim is (MachLogCanAttack): its inaccuracy starts at 100 less its ACCURACY, hits shake it up
/// to at most 75 and time settles it again, and keeping on aiming at something brings even the floor it settles to down.
/// </summary>
public sealed class Gunnery(int accuracy)
{
    private const float WorstInaccuracy = 75;

    /// <summary>What its inaccuracy settles to (MachLogCanAttack::minimumInaccuracy).</summary>
    public float Minimum { get; private set; } = 100 - accuracy;

    public float Inaccuracy { get; private set; } = 100 - accuracy;

    /// <summary>What its last aim came to: below its inaccuracy the round drifts by the difference (lastFireDiceRoll_).</summary>
    public float LastRoll { get; set; }

    /// <summary>When it last aimed at something in its sights (lastFireTime_).</summary>
    public float LastAimed { get; set; } = float.NegativeInfinity;

    /// <summary>MachLogCanAttack::increaseCurrentInaccuracy.</summary>
    public void Shake(float by) => Inaccuracy = MathF.Max(Inaccuracy, MathF.Min(WorstInaccuracy, Inaccuracy + by));

    /// <summary>MachLogCanAttack::decreaseCurrentInaccuracy.</summary>
    public void Settle(float by) => Inaccuracy = MathF.Max(Minimum, Inaccuracy - by);

    /// <summary>MachLogCanAttack::decreaseMinimumInaccuracy.</summary>
    public void Learn(float by) => Minimum = MathF.Max(0, Minimum - by);
}

/// <summary>
/// How one burst goes, the same for all the weapons fired together so that guns miss together
/// (MachLogFireData): the seed for how many rounds a VARIABLE burst has, and how far off it drifts.
/// </summary>
public readonly record struct FireData(float BurstSeed, bool Missed = false, float FirstDrift = 0, float SecondDrift = 0);
