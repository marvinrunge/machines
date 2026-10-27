using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- initiative (MachLog::DefCon) -----------------------------------------------------------

    /// <summary>
    /// The enemy a machine would take on of its own accord, within its scanner range (MachLogCanAttack's
    /// checkAndAttackCloserTarget): at low DefCon the nearest of all; otherwise only one it can shoot from where it
    /// stands, or an armed one that has it in its own reach (acceptableAggressiveTargetForHigherDefconsMachine).
    /// </summary>
    private Actor? InitiativeTarget(Machine machine) => machine.DefCon == DefCon.Low
        ? NearestEnemy(machine, machine.Stats.ScannerRange)
        : NearestEnemy(machine, machine.Stats.ScannerRange, accept: enemy => WorthTakingOn(machine, enemy));

    private bool WorthTakingOn(Machine machine, Actor enemy)
    {
        var distance = machine.DistanceTo(enemy);
        if (distance <= machine.Weapons.Where(w => w.DealsDamage).Select(w => w.Spec.Range).DefaultIfEmpty(0).Max() && ClearShot(machine, enemy))
            return true;
        var reach = enemy switch
        {
            Machine { CanFight: true } armed => armed.Weapons.Where(w => w.DealsDamage).Select(w => w.Spec.Range).DefaultIfEmpty(0).Max(),
            Construction tower => tower.Weapons.Where(w => w.DealsDamage).Select(w => w.Spec.Range).DefaultIfEmpty(0).Max(),
            _ => 0,
        };
        return enemy.DistanceTo(machine) <= reach && ClearShot(enemy, machine);
    }

    /// <summary>
    /// A machine on the move breaks off to fight, then goes on, when its initiative allows: never at high DefCon,
    /// and not in the first VIRTUAL_DEFCON_INTERVAL seconds after its order, while it counts as high
    /// (MachLogMachine::virtualDefCon).
    /// </summary>
    private void TakeInitiative(Machine machine, Vector2 destination)
    {
        if (!machine.CanFight || machine.DrivenByPlayer || machine.DefCon == DefCon.High || Tick % 10 != machine.Id % 10
            || Time - machine.OrderedAt < Data.Parm.General.VirtualDefconInterval)
            return;
        if (InitiativeTarget(machine) is { } enemy)
        {
            var orderedAt = machine.OrderedAt;
            Send(machine, new AttackOrder(enemy.Id, destination, Resume: true), enemy.Position);
            // The fight is its own idea, not a new order.
            machine.OrderedAt = orderedAt;
        }
    }

    // ---- land mines -----------------------------------------------------------------------------

    private int _nextMineId = 1;

    /// <summary>The land mines on the ground, in the order they were laid.</summary>
    public List<LandMineState> LandMines { get; } = [];

    /// <summary>
    /// A spy drives to each point in turn and lays a mine where it stands on arriving; a point it cannot get to is
    /// passed over; it stops when it has no more points or mines (MachLogDropLandMineOperation).
    /// </summary>
    private void UpdateDropMines(Machine spy, DropMinesOrder drop)
    {
        if (spy.Mines <= 0 || drop.Points.Count == 0)
        {
            spy.Order = Order.Idle;
            return;
        }
        var point = drop.Points[0];
        if (Vector2.DistanceSquared(spy.Position, point) > 0.25f && FollowPath(spy))
            return;
        if (Vector2.DistanceSquared(spy.Position, point) <= 1)
        {
            var mine = new LandMineState(_nextMineId++, spy.Race, spy.Position);
            LandMines.Add(mine);
            spy.Mines--;
            Raise(new GameEvent(GameEventKind.MineLaid, spy.Id, spy.Position, mine.Id));
        }
        var rest = drop.Points.Skip(1).ToList();
        if (rest.Count == 0 || spy.Mines <= 0)
            spy.Order = Order.Idle;
        else
            Send(spy, drop with { Points = rest }, rest[0]);
    }

    /// <summary>
    /// A spy goes to its garrison, takes on a full load of mines there and drives back to where it was
    /// (MachLogRefillLandMinesOperation; MachLogGarrison restocks the spies inside it).
    /// </summary>
    private void UpdateRefillMines(Machine spy, RefillMinesOrder refill)
    {
        if (Find<Construction>(refill.Garrison) is not { Complete: true } garrison || garrison.Race != spy.Race)
        {
            spy.Order = Order.Idle;
            return;
        }
        if (!AtBuilding(spy, garrison))
            return;
        spy.Mines = spy.Stats.MineCapacity ?? 0;
        Send(spy, new MoveOrder(refill.ReturnTo), refill.ReturnTo);
    }

    /// <summary>
    /// A mine goes off under the first enemy machine on the ground to come within five metres and its clearance of
    /// it, when no other mine of its race lies nearer that machine: 80 points of damage, and the mine is gone
    /// (MachLogLandMine::update). Looked at every fifth of a second.
    /// </summary>
    private void UpdateLandMines()
    {
        if (LandMines.Count == 0 || Tick % 4 != 0)
            return;
        foreach (var mine in LandMines.ToList())
        {
            var victim = NearestEnemy(new Probe(mine), 5 + _spatialReach, seeCamouflage: true, accept: a => a is Machine { Inside: null } machine
                && machine.Stats.Locomotion != Locomotion.Glider
                && Vector2.Distance(machine.Position, mine.Position) < machine.Stats.HighClearance + 5
                && !LandMines.Any(other => other != mine && other.Race == mine.Race
                    && Vector2.DistanceSquared(other.Position, machine.Position) < Vector2.DistanceSquared(mine.Position, machine.Position)));
            if (victim is null)
                continue;
            LandMines.Remove(mine);
            Damage(victim, 80, mine.Race);
            Raise(new GameEvent(GameEventKind.MineExploded, victim.Id, mine.Position, mine.Id));
        }
    }

    /// <summary>A stand-in actor for searching round a point that is no actor (a land mine).</summary>
    private sealed class Probe(LandMineState mine) : Actor(0, mine.Race, mine.Position)
    {
        public override float Radius => 0;
        public override int Cost => 0;
    }
}
