using Machines.Import.Parm;

namespace Machines.Import.Model;

/// <summary>One weapon from the <c>WEAPONS</c> section. Times are in seconds, distances in metres.</summary>
public sealed record WeaponSpec
{
    public required string TypeName { get; init; }

    /// <summary>Damage of one round; see the armour model in the simulation.</summary>
    public required float Damage { get; init; }
    public required float Range { get; init; }
    public required float ProjectileSpeed { get; init; }

    /// <summary>Rounds fired per burst.</summary>
    public required int BurstRounds { get; init; }
    /// <summary>Time the whole burst takes.</summary>
    public required float BurstDuration { get; init; }
    /// <summary><c>FIXED</c> or <c>VARIABLE</c>; required by the original when a burst has more than one round.</summary>
    public string? BurstTiming { get; init; }

    public required float ReloadTime { get; init; }

    /// <summary>Visual-only parameters (offsets, recoil, trails, <c>EXTRAS</c>) kept as written.</summary>
    public required IReadOnlyList<ParmLine> Presentation { get; init; }
}
