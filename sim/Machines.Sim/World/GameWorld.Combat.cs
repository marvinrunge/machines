using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    private readonly Dictionary<int, int> _turretTargets = [];

    /// <summary>Target a machine driven in first person aims at; set by the client.</summary>
    public Dictionary<int, int> PlayerAims { get; } = [];

    private void UpdateCombat()
    {
        foreach (var machine in Machines.Where(m => m.CanFight && !m.IsDead && m.Inside is null).ToList())
        {
            var target = machine.DrivenByPlayer
                ? (PlayerAims.TryGetValue(machine.Id, out var aim) ? Find(aim) : null)
                : machine.Order switch
                {
                    AttackOrder attack => Find(attack.Target),
                    _ => NearestEnemy(machine, machine.Weapons.Max(w => w.Spec.Range)),
                };
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
    /// RELOAD_TIME seconds. Each round hits with the shooter's ACCURACY percent and is applied with the
    /// original armour rule.
    /// </summary>
    private void Fire(Actor shooter, List<WeaponMount> weapons, Actor? target, int accuracy)
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

                if (target is null || target.IsDead || shooter.DistanceTo(target) > weapon.Spec.Range || !AreEnemies(shooter.Race, target.Race))
                    continue;

                Raise(new GameEvent(GameEventKind.Fired, shooter.Id, shooter.Position, target.Id, weapon.Spec.TypeName));
                if (weapon.IsArea)
                {
                    // Area weapons go off where the target is (the punch in front of the shooter) and hit
                    // everyone the blast reaches, friend or foe, as in the original.
                    var punch = weapon.Spec.TypeName == "GORILLA_PUNCH";
                    StartBlast(weapon.Spec, punch ? shooter.Position : target.Position, shooter.Race,
                        punch ? target.Position - shooter.Position : default);
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

            if (weapon.FiresAt(target) && target is not null && !target.IsDead && shooter.DistanceTo(target) <= weapon.Spec.Range)
            {
                weapon.RoundsLeft = Math.Max(1, weapon.Spec.BurstRounds);
                weapon.RoundTimer = 0;
            }
        }
    }
}
