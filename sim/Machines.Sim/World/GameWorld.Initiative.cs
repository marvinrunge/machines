using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- initiative (MachLog::DefCon) -----------------------------------------------------------

    /// <summary>
    /// The enemy a machine would take on of its own accord, within its scanner range and 150 m (MachLogCanAttack's
    /// checkAndAttackCloserTarget): at low DefCon the nearest of all; otherwise only one it can shoot from where it
    /// stands, or, at normal DefCon, an armed one that has it in its reach and in its sights
    /// (acceptableAggressiveTargetForHigherDefconsMachine).
    /// </summary>
    private Actor? InitiativeTarget(Machine machine)
    {
        var range = MathF.Min(ScannerReach(machine), MaximumEngagementDistance);
        return VirtualDefCon(machine) == DefCon.Low
            ? NearestEnemy(machine, range)
            : NearestEnemy(machine, range, accept: enemy => WorthTakingOn(machine, enemy));
    }

    private bool WorthTakingOn(Machine machine, Actor enemy)
    {
        if (machine.DistanceTo(enemy) <= machine.Weapons.Where(w => w.DealsDamage).Select(w => w.Spec.Range).DefaultIfEmpty(0).Max() && ClearShot(machine, enemy))
            return true;
        if (VirtualDefCon(machine) != DefCon.Normal || !_threatsTo.TryGetValue(machine.Id, out var threats) || !threats.Contains(enemy))
            return false;
        return enemy.DistanceTo(machine) <= MaximumWeaponRange(enemy) && ClearShot(enemy, machine);
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
            if (spy.Mines == 0)
                SendVoiceMail(spy.Race, "VID_SPY_NO_MORE_MINES");
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
        // Full, it drives back to where it was sent from (and out of the garrison); otherwise it goes in through the
        // door, bays or no bays (MachLogRefillLandMinesOperation, FREE_BAYS_UNIMPORTANT), and is restocked inside.
        if (spy.Mines >= (spy.Stats.MineCapacity ?? 0))
        {
            Send(spy, new MoveOrder(refill.ReturnTo), refill.ReturnTo);
            return;
        }
        if (spy.Inside == garrison.Id || !ThroughDoor(spy, garrison))
            return;
        spy.Doorway = null;
        spy.Inside = garrison.Id;
        spy.Position = BayFor(spy, garrison);
        spy.Order = refill with { InsideSince = Time };
        garrison.Garrisoned.Add(spy.Id);
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

    /// <summary>
    /// Spies inside buildings: one restocked in a garrison drives back out to where it was sent from; one idle in
    /// another race's hardware lab downloads research (MachLogSpyLocator::executeTheft). It picks, at random, an
    /// item the lab's race has researched in that kind of lab and its own race may research but has not, then
    /// gains DOWNLOAD_RATE research units a minute towards it, until it has it and picks the next.
    /// </summary>
    private void UpdateSpies()
    {
        foreach (var spy in Machines.Where(m => m.IsSpy && !m.IsDead))
        {
            if (spy.Inside is not { } inside || Find<Construction>(inside) is not { } building)
            {
                spy.Stealing = null;
                continue;
            }
            // The garrison restocks it (UpdateSupport); after a second inside it sets off back.
            if (spy.Order is RefillMinesOrder { InsideSince: { } since } refill)
            {
                if (Time - since >= 1)
                {
                    spy.Mines = spy.Stats.MineCapacity ?? 0;
                    Send(spy, new MoveOrder(refill.ReturnTo), refill.ReturnTo);
                }
                continue;
            }
            if (!building.IsType("HARDWARE_LAB") || building.Race == spy.Race || spy.Order is not IdleOrder)
                continue;
            var state = Races[spy.Race];
            if (spy.Stealing is null)
            {
                var military = building.Spec.SubType == "MILITARY";
                var stealable = Data.ResearchTree.Items.Where(item => (item.Lab == LabType.Military) == military
                    && Races[building.Race].Researched.Contains(item.Key)
                    && state.Available.Contains(item.Key) && !state.Researched.Contains(item.Key)).ToList();
                if (stealable.Count == 0)
                    continue;
                spy.Stealing = stealable[Random.Next(stealable.Count)].Key;
                SendVoiceMail(spy.Race, "VID_SPY_NEW_DOWNLOAD");
            }
            var key = spy.Stealing;
            var item = Data.ResearchTree.Get(key);
            state.ResearchProgress.TryGetValue(key, out var progress);
            progress += (spy.Stats.DownloadRate ?? 0) / 60f * Dt;
            state.ResearchProgress[key] = progress;
            if (progress < ResearchCost(spy.Race, item))
                continue;
            state.Researched.Add(key);
            state.Score.ItemsResearched++;
            spy.Stealing = null;
            Raise(new GameEvent(GameEventKind.ResearchStolen, spy.Id, building.Position, building.Id, key.ToString()));
        }
    }

    /// <summary>A stand-in actor for searching round a point that is no actor (a land mine).</summary>
    private sealed class Probe(LandMineState mine) : Actor(0, mine.Race, mine.Position)
    {
        public override float Radius => 0;
        public override int Cost => 0;
    }
}
