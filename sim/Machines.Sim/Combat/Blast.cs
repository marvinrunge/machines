using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.Combat;

/// <summary>
/// One expanding ring of an area weapon. While it runs, its radius grows from <see cref="FromRadius"/> to
/// <see cref="ToRadius"/>, and every actor the edge reaches is hit once. Damage may fall off with distance.
/// </summary>
/// <param name="Start">Seconds after the blast begins.</param>
/// <param name="FalloffRadius">With a value, damage drops linearly to nothing at this distance, plus <see cref="MinimumDamage"/>.</param>
/// <param name="ConeHalfAngle">With a value, only actors within this angle of <see cref="Blast.Direction"/> are hit (radians).</param>
public sealed record BlastWave(
    float Start,
    float Duration,
    float FromRadius,
    float ToRadius,
    int Damage,
    float? FalloffRadius = null,
    int MinimumDamage = 0,
    float? ConeHalfAngle = null)
{
    public float RadiusAt(float age) =>
        Duration <= 0 ? ToRadius : FromRadius + (ToRadius - FromRadius) * Math.Clamp((age - Start) / Duration, 0, 1);

    public int DamageAt(float distance) => FalloffRadius is { } falloff
        ? (int)(Damage * MathF.Max(0, 1 - distance / falloff)) + MinimumDamage
        : Damage;
}

/// <summary>An area weapon going off: the original's vortex, bee bomb, punch, ion beam and nuclear bomb.</summary>
public sealed class Blast(string weapon, Vector2 position, Race? by, float startTime, IReadOnlyList<BlastWave> waves, Vector2 direction = default)
{
    public string Weapon { get; } = weapon;
    public Vector2 Position { get; } = position;
    public Race? By { get; } = by;
    public float StartTime { get; } = startTime;
    public IReadOnlyList<BlastWave> Waves { get; } = waves;
    public Vector2 Direction { get; } = direction;

    /// <summary>Actors each wave has already hit.</summary>
    public List<HashSet<int>> Hit { get; } = waves.Select(_ => new HashSet<int>()).ToList();

    public float Duration => Waves.Max(w => w.Start + w.Duration);

    /// <summary>The outermost radius any wave has reached, for drawing.</summary>
    public float Radius(float now) => Waves.Where(w => now - StartTime >= w.Start).Select(w => w.RadiusAt(now - StartTime)).DefaultIfEmpty(0).Max();

    /// <summary>
    /// The waves of each area weapon, from machlog's blast classes and machphys' expansion plans.
    /// Weapon data comes from parmdata: DAMAGE and the EXTRAS that the original reads.
    /// </summary>
    public static IReadOnlyList<BlastWave> WavesFor(WeaponSpec weapon)
    {
        var extras = Extras(weapon);
        float Extra(int i, float fallback) => i < extras.Count ? extras[i] : fallback;
        var damage = (int)MathF.Round(weapon.Damage);
        return weapon.TypeName switch
        {
            // vortbomb.cpp: everything inside the kill radius (EXTRAS[0], 40 m) as the sphere grows.
            "VORTEX" => [new BlastWave(0.5f, 2f, 0.75f, Extra(0, 40), damage)],
            // beebomex.cpp / beewave.cpp: a ring from 1.55 m to 34 m, 1.5 s to 4 s after impact.
            "BEE_BOMB" => [new BlastWave(1.5f, 2.5f, 1.55f, 15.5137f * 2.2f, damage)],
            // punch.cpp: a 40 m cone, 0.53 radians either side, out in about a second.
            "GORILLA_PUNCH" => [new BlastWave(0f, 1f, 0f, Extra(0, 40), damage, ConeHalfAngle: 0.53f)],
            // ionbeam.cpp: the beam (EXTRAS[0] = 30 m) burns through, then a wave three times wider flash-burns.
            "ION_ORBITAL_CANNON" => [
                new BlastWave(1f, 1f, 0f, Extra(0, 30), damage),
                new BlastWave(2f, 2f, 12f, Extra(0, 30) * 3, 3)],
            // nuclbomb.cpp: the fireball (30 m), the shock wave from EXTRAS[12] to EXTRAS[13] over EXTRAS[14]
            // seconds, full damage near the centre falling off to the half-way radius, and flash burns beyond.
            "NUCLEAR_MISSILE" => NuclearWaves(damage, Extra(6, 1f), Extra(12, 0.01f), Extra(13, 220), Extra(14, 3)),
            _ => [new BlastWave(0f, 0f, 0f, 5f, damage)],
        };
    }

    public static bool IsArea(string weapon) => weapon is "VORTEX" or "BEE_BOMB" or "GORILLA_PUNCH" or "ION_ORBITAL_CANNON" or "NUCLEAR_MISSILE";

    private static IReadOnlyList<BlastWave> NuclearWaves(int damage, float begin, float from, float to, float duration)
    {
        var mainRadius = from + (to - from) * 0.5f;
        return
        [
            new BlastWave(0f, 0f, 0f, 30f, damage),
            new BlastWave(begin, duration * 0.5f, from, mainRadius, damage, FalloffRadius: mainRadius, MinimumDamage: 5),
            new BlastWave(begin, duration, from, to - 5, 5),
        ];
    }

    private static List<float> Extras(WeaponSpec weapon) => weapon.Presentation
        .Where(p => p.Keyword == "EXTRAS")
        .SelectMany(p => p.Args)
        .TakeWhile(a => !a.StartsWith("//"))
        .Select(a => float.TryParse(a, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) ? v : float.NaN)
        .TakeWhile(v => !float.IsNaN(v))
        .ToList();
}
