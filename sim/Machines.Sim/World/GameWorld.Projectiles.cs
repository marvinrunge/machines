using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.World;

/// <summary>
/// A round in flight (MachLogLinearProjectile): it flies straight at its weapon's SPEED from where it was fired
/// and bursts on the first thing in its way, friend or foe, on the ground where a hill rises into its path, or in the
/// air once it has flown its weapon's RANGE.
/// </summary>
public sealed class Projectile(int id, WeaponSpec weapon, int shooter, Race race, Vector2 start, Vector2 direction, Mounting mounting = Mounting.Top, int barrel = 0, Vector2? aim = null,
    float startHeight = 0, float aimHeight = 0)
{
    /// <summary>Its height where it was fired and over the point it was fired at; between and beyond, a straight line.</summary>
    public float StartHeight { get; } = startHeight;
    public float AimHeight { get; } = aimHeight;

    /// <summary>Its height over a point <paramref name="travelled"/> metres along its way.</summary>
    public float HeightAfter(float travelled)
    {
        var toAim = Vector2.Distance(Start, Aim);
        return toAim < 0.01f ? StartHeight : StartHeight + (AimHeight - StartHeight) * travelled / toAim;
    }

    public int Id { get; } = id;
    public WeaponSpec Weapon { get; } = weapon;
    public int Shooter { get; } = shooter;

    /// <summary>Which of the shooter's weapons fired it, and from which of its barrels (LAUNCH_OFFSET, in turn).</summary>
    public Mounting Mounting { get; } = mounting;
    public int Barrel { get; } = barrel;
    public Race Race { get; } = race;
    public Vector2 Start { get; } = start;

    /// <summary>The point it was fired at (a view flies it there in a straight line, height and all).</summary>
    public Vector2 Aim { get; } = aim ?? start + direction;

    /// <summary>Which way it flies (a unit vector).</summary>
    public Vector2 Direction { get; } = direction;

    public Vector2 Position { get; set; } = start;
    public float Travelled { get; set; }

    /// <summary>
    /// Whether it was fired in first person at the very point under the crosshair, so that <see cref="AimHeight"/> is
    /// that point's height (a view flies it through the point) rather than a guess at the middle of what is there.
    /// </summary>
    public bool Sighted { get; init; }
}

public sealed partial class GameWorld
{
    private readonly List<Projectile> _projectiles = [];
    private int _nextProjectile = 1;

    /// <summary>The rounds in flight, in the order they were fired.</summary>
    public IReadOnlyList<Projectile> Projectiles => _projectiles;

    /// <summary>
    /// Weapons whose rounds fly to their mark and can be dodged: plasma and pulse bolts, missiles, stings, the orb.
    /// Bolters and cannons (1000 m/s), the short sprays and the area weapons hit at once, as before.
    /// </summary>
    public static bool Flies(WeaponMount weapon) => Flies(weapon.Spec);

    public static bool Flies(WeaponSpec weapon) =>
        !Blast.IsArea(weapon.TypeName) && !weapon.TypeName.StartsWith("SUPERCHARGE", StringComparison.Ordinal)
        && weapon.ProjectileSpeed is > 20 and < 500 && weapon.Range > 20;

    /// <summary>
    /// Sends a round from the shooter's middle towards a point <paramref name="aimHeight"/> up; it flies on past it
    /// until its range is spent.
    /// </summary>
    private void Launch(Actor shooter, WeaponMount weapon, Vector2 towards, float aimHeight, bool sighted = false)
    {
        var offset = towards - shooter.Position;
        var direction = offset.LengthSquared() > 0.01f
            ? Vector2.Normalize(offset)
            : shooter is Machine machine ? new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing)) : Vector2.UnitX;
        _projectiles.Add(new Projectile(_nextProjectile++, weapon.Spec, shooter.Id, shooter.Race, shooter.Position, direction,
            weapon.Mounting, weapon.Shots++, towards, AimPoint(shooter).Z, aimHeight) { Sighted = sighted });
    }

    private void UpdateProjectiles()
    {
        var spent = new List<Projectile>();
        foreach (var projectile in _projectiles)
        {
            var step = MathF.Min(projectile.Weapon.ProjectileSpeed * Dt, projectile.Weapon.Range - projectile.Travelled);
            var from = projectile.Position;
            var to = from + projectile.Direction * step;
            var (fromHeight, toHeight) = (projectile.HeightAfter(projectile.Travelled), projectile.HeightAfter(projectile.Travelled + step));
            // A hill rising into its way stops it there (MachLogLinearProjectile's ON_TERRAIN strike); anything
            // before that point is struck first.
            var grounded = Terrain?.FirstBlocked(new Vector3(from, fromHeight), new Vector3(to, toHeight), step: 1.5f, ends: 0);
            if (grounded is { } share)
                (to, toHeight) = (Vector2.Lerp(from, to, share), fromHeight + (toHeight - fromHeight) * share);
            var (start, end) = (new Vector3(from, fromHeight), new Vector3(to, toHeight));
            if (FirstOnLine(start, end, actor => actor.Id != projectile.Shooter) is { } struck)
            {
                End(projectile, Vector3.Lerp(start, end, struck.Along));
                spent.Add(projectile);
                continue;
            }
            if (grounded is not null)
            {
                Raise(new GameEvent(GameEventKind.Impact, projectile.Shooter, to, null, projectile.Weapon.TypeName));
                End(projectile, end);
                spent.Add(projectile);
                continue;
            }
            projectile.Position = to;
            projectile.Travelled += step;
            if (projectile.Travelled >= projectile.Weapon.Range - 0.01f)
            {
                End(projectile, end);
                spent.Add(projectile);
            }
        }
        foreach (var projectile in spent)
            _projectiles.Remove(projectile);
    }

    /// <summary>The end of a round's flight, wherever that is: it bursts (<see cref="Burst"/>), and the orb of treachery does its work.</summary>
    private void End(Projectile projectile, Vector3 point)
    {
        if (projectile.Weapon.TypeName == "ORB_OF_TREACHERY")
            Treachery(projectile, point);
        else
            Burst(InWorld(point), projectile.Weapon, projectile.Shooter, projectile.Race);
    }

    /// <summary>The first machine of any other race within a metre of where the orb ends changes sides, an ally's too (MachLogTreacheryOrb::doBeDestroyed).</summary>
    private void Treachery(Projectile orb, Vector3 point)
    {
        foreach (var actor in FiledNear(new Vector2(point.X, point.Y), 1 + _spatialReach).ToList())
        {
            if (actor is not Machine { IsDead: false, Inside: null } traitor || traitor.Race == orb.Race)
                continue;
            var (min, max) = BoxOf(traitor);
            if (Vector3.DistanceSquared(point, Vector3.Clamp(point, min, max)) >= 1)
                continue;
            Defect(traitor, orb.Race);
            Raise(new GameEvent(GameEventKind.Hit, traitor.Id, traitor.Position, orb.Shooter, orb.Weapon.TypeName));
            return;
        }
    }
}
