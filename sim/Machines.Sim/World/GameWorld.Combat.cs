using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    private readonly Dictionary<int, int> _turretTargets = [];

    /// <summary>What each machine fighting on its own last chose to fire at; for the view only.</summary>
    private readonly Dictionary<int, int> _machineAims = [];

    /// <summary>
    /// What a tower or a machine fighting on its own (not under an attack order) is firing at, for its head or
    /// upper body to turn to; null when nothing. The simulation itself does not read it.
    /// </summary>
    public Actor? AimOf(int id) =>
        _turretTargets.TryGetValue(id, out var target) || _machineAims.TryGetValue(id, out target) ? Find(target) : null;

    /// <summary>
    /// Where a machine driven in first person is firing while the trigger is held: a point on the ground (or as
    /// far as its weapons reach), whether or not anything stands there; set by the client.
    /// </summary>
    public Dictionary<int, Vector2> PlayerAims { get; } = [];

    /// <summary>Which weapons of a machine driven in first person may fire: a bit for each, in mounting order.</summary>
    public Dictionary<int, int> PlayerWeapons { get; } = [];

    private void UpdateCombat()
    {
        foreach (var machine in Machines.Where(m => m.CanFight && !m.IsDead && m.Inside is null).ToList())
        {
            if (machine.DrivenByPlayer)
            {
                var selected = PlayerWeapons.GetValueOrDefault(machine.Id, -1);
                Fire(machine, machine.Weapons.Where((_, i) => (selected & (1 << i)) != 0).ToList(), null, machine.Stats.Accuracy ?? 100,
                    PlayerAims.TryGetValue(machine.Id, out var point) ? point : null);
                continue;
            }
            var target = machine.Order switch
            {
                AttackOrder attack => Find(attack.Target),
                _ => NearestEnemy(machine, machine.Weapons.Max(w => w.Spec.Range)),
            };
            if (target is null)
                _machineAims.Remove(machine.Id);
            else
                _machineAims[machine.Id] = target.Id;
            Fire(machine, machine.Weapons, target, machine.Stats.Accuracy ?? 100);
        }

        ArmPods();
        foreach (var turret in Constructions.Where(c => c.Complete && c.Weapons.Count > 0 && !c.IsDead).ToList())
        {
            // Strike weapons only charge here; they fire on command.
            foreach (var strike in turret.Weapons.Where(w => w.IsStrike && w.Reload > 0))
                strike.Reload -= Dt;
            if (turret.Weapons.All(w => w.IsStrike))
                continue;

            // Turrets keep their target while it stays in range, then pick the nearest enemy.
            var range = turret.Weapons.Where(w => !w.IsStrike).Max(w => w.Spec.Range);
            var target = _turretTargets.TryGetValue(turret.Id, out var id) ? Find(id) : null;
            if (target is null || target.IsDead || turret.DistanceTo(target) > range)
            {
                target = NearestEnemy(turret, range);
                if (target is null)
                    _turretTargets.Remove(turret.Id);
                else
                    _turretTargets[turret.Id] = target.Id;
            }
            Fire(turret, turret.Weapons.Where(w => !w.IsStrike).ToList(), target, turret.Stats.Accuracy ?? 100);
        }

        UpdateProjectiles();
        UpdateBlasts();
    }

    /// <summary>Applies damage with the original armour rule and remembers who did it, for the score.</summary>
    public void Damage(Actor target, int damage, Race? by)
    {
        if (target.IsDead)
            return;
        target.Durability = ArmourModel.ApplyHit(target.Durability, damage);
        if (by is not null)
            target.LastHitBy = by;
    }

    /// <summary>
    /// Runs each weapon's burst and reload cycle: a burst of BURST rounds spread over its duration, then
    /// RELOAD_TIME seconds. Flying rounds (see <see cref="Flies"/>) are launched and do their damage where they
    /// strike; the others hit at once with the shooter's ACCURACY percent. Damage follows the original armour rule.
    /// A machine driven in first person fires at <paramref name="aimPoint"/> instead, at whatever is there or not.
    /// </summary>
    private void Fire(Actor shooter, List<WeaponMount> weapons, Actor? target, int accuracy, Vector2? aimPoint = null)
    {
        foreach (var weapon in weapons)
        {
            if (weapon.RoundsLeft > 0)
            {
                weapon.RoundTimer -= Dt;
                if (weapon.RoundTimer > 0)
                    continue;

                weapon.RoundsLeft--;
                weapon.RoundTimer = weapon.Spec.BurstDuration / Math.Max(1, weapon.Spec.BurstRounds);
                if (weapon.RoundsLeft == 0)
                    weapon.Reload = weapon.Spec.ReloadTime;

                if (aimPoint is { } point)
                {
                    FireAtPoint(shooter, weapon, point, accuracy);
                    continue;
                }
                if (target is null || target.IsDead || shooter.DistanceTo(target) > weapon.Spec.Range || !AreEnemies(shooter.Race, target.Race))
                    continue;

                Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, target.Id, weapon.Spec.TypeName));
                if (Flies(weapon))
                    Launch(shooter, weapon, AimAt(shooter, target, accuracy));
                else if (weapon.IsArea)
                {
                    // Area weapons go off where the target is and hit everyone the blast reaches, friend or foe,
                    // as in the original. The punch goes off in front of the shooter once its fists come down.
                    if (weapon.Spec.TypeName == "GORILLA_PUNCH")
                        ScheduleBlast(Time + Blast.PunchLanding, weapon.Spec, shooter.Position, shooter.Race, target.Position - shooter.Position);
                    else
                        StartBlast(weapon.Spec, target.Position, shooter.Race);
                }
                else if (weapon.IsTreachery)
                {
                    if (target is Machine traitor && Random.Next(100) < accuracy)
                        Defect(traitor, shooter.Race);
                }
                else if (Random.Next(100) < accuracy)
                {
                    var damage = (int)MathF.Round(weapon.Spec.Damage);
                    Damage(target, damage, shooter.Race);
                    Raise(new GameEvent(GameEventKind.Hit, target.Id, target.Position, shooter.Id, weapon.Spec.TypeName));
                }
                continue;
            }

            if (weapon.Reload > 0)
            {
                weapon.Reload -= Dt;
                continue;
            }

            var triggered = aimPoint is not null
                ? weapon.DealsDamage || weapon.IsTreachery
                : weapon.FiresAt(target) && target is not null && !target.IsDead && shooter.DistanceTo(target) <= weapon.Spec.Range;
            if (triggered)
            {
                weapon.RoundsLeft = Math.Max(1, weapon.Spec.BurstRounds);
                weapon.RoundTimer = 0;
            }
        }
    }

    /// <summary>
    /// A round fired at a point in first person: flying rounds are launched at it and strike whatever they meet;
    /// the others hit the enemy nearest the point if one stands within reach of it, and otherwise nothing.
    /// </summary>
    private void FireAtPoint(Actor shooter, WeaponMount weapon, Vector2 point, int accuracy)
    {
        var offset = point - shooter.Position;
        if (offset.Length() > weapon.Spec.Range)
            point = shooter.Position + Vector2.Normalize(offset) * weapon.Spec.Range;
        float Off(Actor a) => a is Construction building ? building.DistanceFrom(point) : MathF.Max(0, Vector2.Distance(a.Position, point) - a.Radius);
        var there = Actors.Where(a => !a.IsDead && AreEnemies(shooter.Race, a.Race) && a is not Machine { Inside: not null } && Off(a) <= 3)
            .OrderBy(Off).ThenBy(a => a.Id).FirstOrDefault();
        Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, there?.Id, weapon.Spec.TypeName));
        if (Flies(weapon))
        {
            Launch(shooter, weapon, point);
        }
        else if (weapon.IsArea)
        {
            if (weapon.Spec.TypeName == "GORILLA_PUNCH")
                ScheduleBlast(Time + Blast.PunchLanding, weapon.Spec, shooter.Position, shooter.Race, point - shooter.Position);
            else
                StartBlast(weapon.Spec, point, shooter.Race);
        }
        else if (there is not null && Random.Next(100) < accuracy)
        {
            Damage(there, (int)MathF.Round(weapon.Spec.Damage), shooter.Race);
            Raise(new GameEvent(GameEventKind.Hit, there.Id, there.Position, shooter.Id, weapon.Spec.TypeName));
        }
    }
}
