using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.World;

/// <summary>
/// A round in flight (MachLogLinearProjectile): it flies straight at its weapon's SPEED from where it was fired
/// and strikes the first enemy in its way, the ground where a hill rises into its path, or is spent once it has
/// flown its weapon's RANGE.
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
    private void Launch(Actor shooter, WeaponMount weapon, Vector2 towards, float aimHeight)
    {
        var offset = towards - shooter.Position;
        var direction = offset.LengthSquared() > 0.01f
            ? Vector2.Normalize(offset)
            : shooter is Machine machine ? new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing)) : Vector2.UnitX;
        _projectiles.Add(new Projectile(_nextProjectile++, weapon.Spec, shooter.Id, shooter.Race, shooter.Position, direction,
            weapon.Mounting, weapon.Shots++, towards, AimPoint(shooter).Z, aimHeight));
    }

    /// <summary>
    /// Where a round aimed at a target goes: at the target as it stands now when the shooter's ACCURACY roll
    /// succeeds, otherwise past it to one side. A target that moves meanwhile may still get out of the way.
    /// </summary>
    private Vector2 AimAt(Actor shooter, Actor target, int accuracy)
    {
        if (Random.Next(100) < accuracy)
            return target.Position;
        var line = target.Position - shooter.Position;
        var side = line.LengthSquared() > 0.01f ? Vector2.Normalize(new Vector2(-line.Y, line.X)) : Vector2.UnitY;
        var wide = target.Radius + 1.5f + Random.Next(30) / 10f;
        return target.Position + side * (Random.Next(2) == 0 ? wide : -wide);
    }

    private void UpdateProjectiles()
    {
        var spent = new List<Projectile>();
        foreach (var projectile in _projectiles)
        {
            var step = MathF.Min(projectile.Weapon.ProjectileSpeed * Dt, projectile.Weapon.Range - projectile.Travelled);
            var from = projectile.Position;
            var to = from + projectile.Direction * step;
            // A hill rising into its way stops it there (MachLogLinearProjectile's ON_TERRAIN strike); an enemy
            // before that point is struck first.
            var grounded = Terrain?.FirstBlocked(new Vector3(from, projectile.HeightAfter(projectile.Travelled)),
                new Vector3(to, projectile.HeightAfter(projectile.Travelled + step)), step: 1.5f, ends: 0);
            if (grounded is { } share)
                to = Vector2.Lerp(from, to, share);
            if (FirstInTheWay(projectile, from, to) is { } struck)
            {
                Strike(projectile, struck);
                spent.Add(projectile);
                continue;
            }
            if (grounded is not null)
            {
                Raise(new GameEvent(GameEventKind.Impact, projectile.Shooter, to, null, projectile.Weapon.TypeName));
                spent.Add(projectile);
                continue;
            }
            projectile.Position = to;
            projectile.Travelled += step;
            if (projectile.Travelled >= projectile.Weapon.Range - 0.01f)
                spent.Add(projectile);
        }
        foreach (var projectile in spent)
            _projectiles.Remove(projectile);
    }

    private void Strike(Projectile projectile, Actor struck)
    {
        if (projectile.Weapon.TypeName == "ORB_OF_TREACHERY")
        {
            if (struck is Machine traitor)
                Defect(traitor, projectile.Race);
        }
        else
        {
            Damage(struck, (int)MathF.Round(projectile.Weapon.Damage), projectile.Race);
        }
        Raise(new GameEvent(GameEventKind.Hit, struck.Id, struck.Position, projectile.Shooter, projectile.Weapon.TypeName));
    }

    /// <summary>The enemy a round meets first on its way from one point to the next this tick, if any.</summary>
    private Actor? FirstInTheWay(Projectile projectile, Vector2 from, Vector2 to)
    {
        var reach = _spatialReach + SpatialSlack;
        var (x0, y0) = SpatialCellOf(Vector2.Min(from, to) - new Vector2(reach));
        var (x1, y1) = SpatialCellOf(Vector2.Max(from, to) + new Vector2(reach));
        var segment = to - from;
        var length = segment.Length();
        Actor? first = null;
        var firstAlong = float.MaxValue;
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                foreach (var actor in _spatial[y * _spatialX + x])
                {
                    if (actor.IsDead || actor.Id == projectile.Shooter || !AreEnemies(projectile.Race, actor.Race) || actor is Machine { Inside: not null })
                        continue;
                    // How far along the way the actor is met: machines by their radius, buildings by their footprint.
                    var along = length > 0 ? Math.Clamp(Vector2.Dot(actor.Position - from, segment) / (length * length), 0, 1) : 0;
                    var closest = from + segment * along;
                    var met = actor is Construction building
                        ? building.DistanceFrom(closest) <= 0 || building.DistanceFrom(to) <= 0
                        : Vector2.DistanceSquared(closest, actor.Position) <= actor.Radius * actor.Radius;
                    if (met && (along < firstAlong || along == firstAlong && actor.Id < first!.Id))
                    {
                        first = actor;
                        firstAlong = along;
                    }
                }
            }
        }
        return first;
    }
}
