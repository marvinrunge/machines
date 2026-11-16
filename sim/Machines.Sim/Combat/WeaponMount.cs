using Machines.Import.Model;

namespace Machines.Sim.Combat;

/// <summary>One weapon on a machine or building, with its firing state.</summary>
public sealed class WeaponMount(Mounting mounting, WeaponSpec spec)
{
    public Mounting Mounting { get; } = mounting;

    /// <summary>
    /// Where it stands in a Wired hero's battery on its mounting: 0 for the first copy, and for every other weapon of the
    /// game. A machine's copies stand next to each other in its weapons, in order.
    /// </summary>
    public int Copy { get; init; }

    /// <summary>The weapon as the mount was made with it; a copy of the mount copies this, never <see cref="Spec"/>.</summary>
    public WeaponSpec Base { get; } = spec;

    /// <summary>The weapon as it fires now: <see cref="Base"/>, or a tuned copy of it while a Wired machine's special works on it.</summary>
    public WeaponSpec Spec { get; private set; } = spec;

    /// <summary>Fires as <paramref name="tuned"/> from now on, or as <see cref="Base"/> again with null.</summary>
    internal void Tune(WeaponSpec? tuned) => Spec = tuned ?? Base;

    /// <summary>Seconds until the next burst may start.</summary>
    public float Reload { get; set; }

    /// <summary>Rounds still to fire in the current burst.</summary>
    public int RoundsLeft { get; set; }

    /// <summary>Seconds until the next round of the burst.</summary>
    public float RoundTimer { get; set; }

    /// <summary>How the burst under way goes.</summary>
    public FireData Burst { get; set; }

    /// <summary>
    /// Whether it stood loaded and was passed over the last time its machine fired: true until it first opens fire, false
    /// from then until its reload is over and its burst out and a tick goes by without it opening fire. A copy of a battery
    /// that waited for the trigger so takes its turn behind the copy before it, while one whose reload has only just ended
    /// does not, however the reload was cut short. Not part of the checksum.
    /// </summary>
    internal bool Waited { get; set; } = true;

    /// <summary>Rounds fired so far; their barrels take turns.</summary>
    public int Shots { get; set; }

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
