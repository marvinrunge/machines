using Machines.Import.Model;

namespace Machines.Sim.Combat;

/// <summary>One weapon on a machine or building, with its firing state.</summary>
public sealed class WeaponMount(Mounting mounting, WeaponSpec spec)
{
    public Mounting Mounting { get; } = mounting;
    public WeaponSpec Spec { get; } = spec;

    /// <summary>Seconds until the next burst may start.</summary>
    public float Reload { get; set; }

    /// <summary>Rounds still to fire in the current burst.</summary>
    public int RoundsLeft { get; set; }

    /// <summary>Seconds until the next round of the burst.</summary>
    public float RoundTimer { get; set; }

    /// <summary>The ion cannon and the nuclear missile: fired on command at any point, never automatically.</summary>
    public bool IsStrike => Spec.TypeName is "ION_ORBITAL_CANNON" or "NUCLEAR_MISSILE";

    /// <summary>Goes off as a blast at the target rather than hitting it directly.</summary>
    public bool IsArea => Blast.IsArea(Spec.TypeName);

    /// <summary>Superchargers: they restore hit points instead of doing damage.</summary>
    public bool IsHealer => Spec.TypeName.StartsWith("SUPERCHARGE", StringComparison.Ordinal);

    /// <summary>The orb of treachery: a machine it hits changes sides.</summary>
    public bool IsTreachery => Spec.TypeName == "ORB_OF_TREACHERY";

    /// <summary>Whether this weapon is fired at the given enemy.</summary>
    public bool FiresAt(object? target) => DealsDamage || IsTreachery && target is World.Machine;

    public float ChargePercent => Spec.ReloadTime <= 0 ? 100 : 100 * (1 - Math.Clamp(Reload / Spec.ReloadTime, 0, 1));

    /// <summary>
    /// Fires on its own at enemies in range. Strikes wait for a command; weapons whose effect is not damage
    /// (healing, treachery) are handled elsewhere.
    /// </summary>
    public bool DealsDamage => Spec.Damage > 0 && !IsStrike
        && Spec.TypeName is not ("SUPERCHARGE_ADVANCED" or "SUPERCHARGE_SUPER");
}
