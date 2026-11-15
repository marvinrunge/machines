using System.Numerics;

namespace Machines.Sim.Survival;

/// <summary>Where a Colossus' tracks stand: free, locking, anchored or being released.</summary>
public enum AnchorPhase
{
    Free,
    Locking,
    Anchored,
    Releasing,
}

/// <summary>
/// What a Wired match keeps in mind about one machine with a special of its body (<see cref="WiredMachines.BodySpecials"/>),
/// hero, escort or machine of the waves alike: where it stands still, its anchor, and its own dash. Kept by actor id in
/// <see cref="WiredRun.Bodies"/> while the machine lives; a hero's new body is a new actor and starts afresh. Wired is the
/// remake's own addition.
/// </summary>
public sealed class WiredBody
{
    /// <summary>Where it has stood since <see cref="StillSince"/>, give or take <see cref="WiredBalance.LurkDrift"/>.</summary>
    public Vector2 StillAt { get; set; }

    public float StillSince { get; set; }

    /// <summary>Its lurk has been fired since it came to stand there.</summary>
    public bool LurkSpent { get; set; }

    public AnchorPhase Anchor { get; set; }

    /// <summary>When the anchor's phase began.</summary>
    public float AnchorSince { get; set; }

    /// <summary>Since when an anchored machine of the waves has had nothing in reach.</summary>
    public float AnchorIdleSince { get; set; }

    /// <summary>When its own dash is ready again, until when one lasts, and which way it goes; a hero's are in <see cref="WiredState"/>.</summary>
    public float DashReadyAt { get; set; }

    public float DashUntil { get; set; }

    public Vector2 DashWay { get; set; }

    /// <summary>The machines the dash under way has rammed already, by id.</summary>
    public SortedSet<int> Rammed { get; } = [];

    internal void MixInto(WiredHash hash)
    {
        foreach (var value in (ReadOnlySpan<float>)[StillAt.X, StillAt.Y, StillSince, AnchorSince, AnchorIdleSince, DashReadyAt, DashUntil, DashWay.X, DashWay.Y])
            hash.Mix(BitConverter.SingleToInt32Bits(value));
        hash.Mix(LurkSpent ? 1 : 0);
        hash.Mix((int)Anchor);
        hash.Mix(Rammed.Count);
        foreach (var rammed in Rammed)
            hash.Mix(rammed);
    }
}
