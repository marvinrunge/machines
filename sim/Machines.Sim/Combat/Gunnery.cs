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
/// (MachLogFireData): the seed for how many rounds a VARIABLE burst has, and how far off it drifts. In the story a hunter
/// whose aim falls off with the distance (the prologue's Thresher) may send a burst wide of one of the trio: to the
/// <paramref name="Wide"/> side (1 left, -1 right, 0 not wide), <paramref name="WideBy"/> of the way across the band beside
/// the target that its rounds strike, and <paramref name="Lift"/> of the way from the floor up to the target's middle
/// (GameWorld.StoryAim, StorySight.WildChance).
/// </summary>
public readonly record struct FireData(float BurstSeed, bool Missed = false, float FirstDrift = 0, float SecondDrift = 0, int Wide = 0, float WideBy = 0,
    float Lift = 0);
