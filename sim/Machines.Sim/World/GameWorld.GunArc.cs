using System.Globalization;
using Machines.Import.Model;
using Machines.Sim.Survival;

namespace Machines.Sim.World;

// ---- the arc of a sighted hero's guns (the remake's addition, as a World of Tanks gun without a turret aims) ----------
//
// A sighted hero without an upper body (a Grunt 1, a Ballista, a Titan ...) aims with its hull. Its weapons still angle a
// little on their mounts, as the original's did in first person: a round went to the point under the crosshair while the
// point lay within 95 % of the weapon's DEVIATION_ANGLES either way of where the weapon pointed
// (MachPhys1stPersonDriver::targetPointAnglesValid; MachPhysWeaponData::horizontalDeviationAngle, 10 degrees unless the
// weapon says otherwise). So while where the driver looks lies within a weapon's arc of the facing, the hull stays put, the
// weapon angles towards it and its rounds go there; past the arc the weapon stands at the arc's edge and its rounds go along
// it, and the driver's client turns the hull on the spot until the view is back within the arc (of its narrowest weapon).
public sealed partial class GameWorld
{
    private readonly Dictionary<WeaponSpec, float> _gunArcs = [];

    /// <summary>
    /// How far a weapon angles either way of its mount in first person, radians: 95 % of the tangent of its horizontal
    /// DEVIATION_ANGLES (10 degrees unless it says otherwise), as the original tested a point against it.
    /// </summary>
    public float ArcOf(WeaponSpec weapon)
    {
        if (_gunArcs.TryGetValue(weapon, out var arc))
            return arc;
        var degrees = weapon.Presentation.FirstOrDefault(p => p.Keyword == "DEVIATION_ANGLES") is { Args: [var a, ..] }
            && float.TryParse(a, NumberStyles.Float, CultureInfo.InvariantCulture, out var given) ? given : 10;
        return _gunArcs[weapon] = DetMath.Atan(0.95f * Tan(degrees * MathF.PI / 180));
    }

    /// <summary>
    /// Whether a weapon angles within its arc on a sighted hero: not one set down rather than fired (the vortex, the bee's
    /// bomb) nor the Gorilla's punch, which go where the hull points.
    /// </summary>
    private static bool Angles(WeaponSpec weapon) => !Placed(weapon) && weapon.TypeName != "GORILLA_PUNCH";

    /// <summary>
    /// The arc a weapon of this machine angles within when its driver is sighted, radians: its own arc for a machine without
    /// an upper body while the match has the arcs (<see cref="MatchOptions.HeroGunArc"/>), else none. Pure of who drives it,
    /// so the driver's client asks it too. A story never has them, whatever its options or recording say (<see cref="HasGunArcs"/>).
    /// </summary>
    public float GunArcOf(Machine machine, WeaponSpec weapon) =>
        HasGunArcs && HeadStep(machine) <= 0 && Angles(weapon) ? ArcOf(weapon) : 0;

    /// <summary>
    /// Whether sighted heroes' guns angle in their arcs in this match: a Wired run's or a battle's with the arcs, never a
    /// story's. The story's trio drives as the original's first person did, the hull following the view at once (its walk,
    /// its doors and ducts are laid out for that); with the arcs L-7's hull would stay up to 28.7 degrees off the view with
    /// the Bolter on, and the prologue's driven run went otherwise from the Commander's squad on (a Grunt down, L-7 stuck at
    /// putting it together).
    /// </summary>
    private bool HasGunArcs => Options.HeroGunArc && Wired is not null && Story is null;

    /// <summary>
    /// The arc of a machine's narrowest weapon that angles (<see cref="GunArcOf(Machine, WeaponSpec)"/>), radians, or 0: while
    /// the view lies within it every one of its weapons fires where the driver looks, and past it the hull turns.
    /// </summary>
    public float GunArcOf(Machine machine)
    {
        var narrowest = float.MaxValue;
        foreach (var weapon in machine.Weapons)
        {
            if (GunArcOf(machine, weapon.Spec) is > 0 and var arc)
                narrowest = MathF.Min(narrowest, arc);
        }
        return narrowest == float.MaxValue ? 0 : narrowest;
    }

    /// <summary>
    /// How far a gun with this arc stands off the line to where the driver looks, radians anticlockwise, ±π, for a hull lag
    /// of <paramref name="lag"/> (the gun's heading less the view's): none within the arc, past it the rest beyond its edge.
    /// </summary>
    public static float BeyondArc(float lag, float arc) => lag - Math.Clamp(lag, -arc, arc);

    /// <summary>
    /// How far the guns of a sighted machine without an upper body stand angled from its hull, radians anticlockwise: towards
    /// where its driver looks, as far as its narrowest weapon's arc lets them; 0 for any other machine.
    /// </summary>
    public float GunAngleOf(Machine machine) =>
        PlayerYaws.TryGetValue(machine.Id, out var yaw) && machine.Inside is null && GunArcOf(machine) is > 0 and var arc
            ? Math.Clamp(MathF.IEEERemainder(yaw - machine.Facing, 2 * MathF.PI), -arc, arc)
            : 0;

    /// <summary>
    /// Where a round of a sighted machine's weapon goes off the view, radians anticlockwise, or null for straight to the
    /// point looked at: within the gun's arc, or for a machine with an upper body when its gun is on the sight.
    /// </summary>
    private float? OffTheSight(Machine machine, float yaw, WeaponSpec weapon)
    {
        if (GunArcOf(machine, weapon) is > 0 and var arc)
        {
            var beyond = BeyondArc(GunLag(machine, yaw), arc);
            return MathF.Abs(beyond) <= WiredBalance.GunPlay ? null : beyond;
        }
        return OnSight(machine.Id) ? null : GunLag(machine, yaw);
    }
}
