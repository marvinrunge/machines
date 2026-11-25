using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Story;

/// <summary>Where a hit on the trio comes from, for the budget: a hazard and the pack's wild fire count, aimed fire does not.</summary>
public enum StoryHarmKind
{
    /// <summary>Aimed fire (the fair rule) and anything not the escape's: outside the budget, it is what kills and it is avoidable.</summary>
    Aimed,

    /// <summary>A hazard of the ship (a container, a plate, a jet, the fire face): in the budget, and never below the floor.</summary>
    Hazard,

    /// <summary>The pack's wild fire while it runs: in the budget.</summary>
    Wild,
}

/// <summary>
/// The harm budget of the escape (design-escape §5.1, package E): what the ship's hazards and the pack's wild fire may take
/// from a hero of the trio. One event takes at most <see cref="EventShare"/> of its hit points; all of them together at most
/// the difficulty's share in any 10 s (<see cref="Window"/>); after a hit of <see cref="EventShare"/> or more it is spared
/// for <see cref="Grace"/>; and a hazard never takes a hero below <see cref="Floor"/>. Aimed fire passes untouched. It
/// holds only while the run's flag <see cref="StoryMarks.Escape"/> is set (<see cref="Active"/>); elsewhere every hit passes.
/// Everything is in ticks and goes into <see cref="Fingerprint"/> once it was active.
/// </summary>
public sealed class StoryBudget
{
    /// <summary>The sliding window, ticks (10 s); the grace after a big hit, ticks (0.5 s).</summary>
    public const int Window = 200, Grace = 10;

    /// <summary>One event's most, a share of max hit points; the floor hazards never go below.</summary>
    public const float EventShare = 0.15f, Floor = 0.20f;

    /// <summary>The window's share by difficulty (Leicht 15 %, Normal 25 %, Schwer 35 %).</summary>
    public static float WindowShare(StoryDifficulty difficulty) => difficulty switch
    {
        StoryDifficulty.Easy => 0.15f,
        StoryDifficulty.Hard => 0.35f,
        _ => 0.25f,
    };

    /// <summary>Whether the budget holds now (the escape's flag); set by the escape's tick.</summary>
    public bool Active { get; set; }

    /// <summary>Whether it ever held (then the checksum keeps it).</summary>
    public bool Used { get; set; }

    /// <summary>The tick of the world now, as the escape's tick last set it.</summary>
    public long Now { get; set; }

    /// <summary>The window's share now (the run's difficulty).</summary>
    public float Share { get; set; } = 0.25f;

    /// <summary>What the hit being dealt is (set by its dealer around one hit, back to Aimed after).</summary>
    public StoryHarmKind Kind { get; set; }

    /// <summary>The hits the budget let through, by the hero's machine id: the tick and the hit points.</summary>
    public SortedDictionary<int, List<(long Tick, int Harm)>> Taken { get; } = [];

    /// <summary>Spared until this tick after a big hit, by the hero's machine id.</summary>
    public SortedDictionary<int, long> SparedUntil { get; } = [];

    /// <summary>What the budget held back, all told (for the driver's timeline).</summary>
    public int HeldBack { get; set; }

    /// <summary>A fingerprint of what this part keeps, for the run's checksum.</summary>
    public long Fingerprint()
    {
        var hash = new WiredHash();
        if (!Used)
            return hash.Value;
        hash.Mix(Active ? 1 : 0);
        hash.Mix(Now);
        hash.Mix(BitConverter.SingleToInt32Bits(Share));
        foreach (var (id, hits) in Taken)
        {
            hash.Mix(id);
            foreach (var (tick, harm) in hits)
            {
                hash.Mix(tick);
                hash.Mix(harm);
            }
        }
        foreach (var (id, until) in SparedUntil)
        {
            hash.Mix(id);
            hash.Mix(until);
        }
        hash.Mix(HeldBack);
        return hash.Value;
    }

    /// <summary>
    /// A hit of so much harm on a hero of the trio as the budget lets through (GameWorld.StoryHarm): all of it while the budget
    /// does not hold or the hit is aimed; else cut to the event's most, the window's rest, the floor (a hazard), and none while
    /// the hero is spared.
    /// </summary>
    public int Filter(Actor target, int damage)
    {
        if (!Active || Kind == StoryHarmKind.Aimed || damage <= 0)
            return damage;
        var max = Math.Max(1, target.MaxHitPoints);
        if (SparedUntil.TryGetValue(target.Id, out var spared) && Now < spared)
        {
            HeldBack += damage;
            return 0;
        }
        if (!Taken.TryGetValue(target.Id, out var hits))
            Taken[target.Id] = hits = [];
        hits.RemoveAll(h => Now - h.Tick >= Window);
        var harm = Math.Min(damage, (int)MathF.Ceiling(EventShare * max));
        var left = (int)MathF.Floor(Share * max) - hits.Sum(h => h.Harm);
        harm = Math.Min(harm, Math.Max(0, left));
        if (Kind == StoryHarmKind.Hazard)
            harm = Math.Min(harm, Math.Max(0, target.HitPoints - (int)MathF.Ceiling(Floor * max)));
        HeldBack += damage - harm;
        if (harm <= 0)
            return 0;
        hits.Add((Now, harm));
        if (harm >= EventShare * max - 0.5f)
            SparedUntil[target.Id] = Now + Grace;
        return harm;
    }
}
