using System.Numerics;

namespace Machines.Sim.World;

/// <summary>
/// APCs carry machines (machlog apc.cpp): a machine boards when the APC has room for its VOLUME within the
/// APC's CAPACITY, rides off the map, and comes out where the APC deploys, one a second. Passengers die with their APC,
/// and an APC near destruction lets them out where it is.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>MachLogAPC: with passengers aboard, an APC hit below this share of its hit points evacuates them; at low DefCon sooner, at high never.</summary>
    public const float ApcEvacuationRatio = 0.10f, LowDefConApcEvacuationRatio = 0.20f;

    /// <summary>How far from the APC's middle beyond its clearance a machine gets in (MachLogEnterAPCOperation's desiredRange_).</summary>
    private const float ApcEntryRange = 15;

    /// <summary>How far round from the APC's nose a machine must stand to get in: at its rear (MachLogEnterAPCOperation).</summary>
    private const float ApcEntryAngle = 2.8f;

    /// <summary>How far an APC's drop zone may be for one being hit to carry on with it (MachLogAPC::nearDropzone).</summary>
    private const float NearDropzone = 50;

    /// <summary>What counts as nearer among incoming machines, and how near they all count as alike (MachLogAPC::graceDistance, sqrCloseDistance).</summary>
    private const float ApcGraceDistance = 10, ApcCloseDistance = 20;

    // The machines on their way into each APC this tick, and whom each APC is to turn its rear to.
    private readonly Dictionary<int, List<Machine>> _boarding = [];
    private readonly Dictionary<int, Vector2> _rearTo = [];

    /// <summary>The volume an APC carries now.</summary>
    public int CarriedVolume(Machine apc) =>
        apc.Passengers.Select(Find<Machine>).OfType<Machine>().Sum(m => m.Stats.Volume);

    /// <summary>MachLogAPC::roomForMachine.</summary>
    public bool RoomFor(Machine apc, Machine machine) =>
        CarriedVolume(apc) + machine.Stats.Volume <= (apc.Stats.Capacity ?? 0);

    /// <summary>machine.cpp: a machine goes through a building's door when the building's access size allows it.</summary>
    public static bool FitsThrough(Machine machine, Construction building) =>
        (building.Stats.AccessSizeIndex ?? 0) >= machine.Stats.MinAccessSizeIndex;

    private void UpdateBoard(Machine machine, BoardOrder board)
    {
        var apc = Find<Machine>(board.Apc);
        if (apc is null || apc.IsDead || apc.Race != machine.Race || apc.Inside is not null)
        {
            machine.Order = Order.Idle;
            return;
        }
        if (!_boarding.TryGetValue(apc.Id, out var incoming))
            _boarding[apc.Id] = incoming = [];
        incoming.Add(machine);
        var reach = apc.Stats.HighClearance + ApcEntryRange;
        if (Vector2.DistanceSquared(machine.Position, apc.Position) >= reach * reach)
        {
            Approach(machine, apc.Position);
            return;
        }
        // It stands and waits for the APC's rear to come round to it (MachLogEnterAPCOperation::doUpdate).
        machine.Path.Clear();
        machine.PathIndex = 0;
        if (MathF.Abs(AngleToFace(apc, machine.Position)) <= ApcEntryAngle)
            return;

        machine.Order = Order.Idle;
        if (!RoomFor(apc, machine))
            return;
        machine.Inside = apc.Id;
        machine.Position = apc.Position;
        machine.Path.Clear();
        machine.PathIndex = 0;
        apc.Passengers.Add(machine.Id);
        Raise(new GameEvent(GameEventKind.Boarded, machine.Id, apc.Position, apc.Id));
    }

    /// <summary>
    /// Drives to the drop zone and lets one machine out a second, the last in first; with no room about the APC it waits
    /// (MachLogDeployAPCOperation::doUpdate, MachLogAPC::nextDeployPoint).
    /// </summary>
    private void UpdateDeploy(Machine apc, DeployOrder deploy)
    {
        if (FollowPath(apc) || Time < deploy.NextOut)
            return;
        if (apc.Passengers.Count == 0 || apc.Inside is not null)
        {
            apc.Order = Order.Idle;
            return;
        }
        apc.Order = deploy = deploy with { NextOut = Time + 1 };
        var machine = Find<Machine>(apc.Passengers[^1]);
        if (machine is null)
        {
            apc.Passengers.RemoveAt(apc.Passengers.Count - 1);
            return;
        }
        var heading = new Vector2(DetMath.Cos(apc.Facing), DetMath.Sin(apc.Facing));
        if (!FreeSpace(apc.Position, heading, apc.Stats.HighClearance + 10, machine.Stats.HighClearance * 1.5f, out var spot, asker: machine))
            return;
        apc.Passengers.RemoveAt(apc.Passengers.Count - 1);
        machine.Inside = null;
        machine.Order = Order.Idle;
        machine.Position = spot;
        // It comes into the world pumped and alert (MachLogMachine::teleportIntoWorld).
        machine.Alert.Raise(FullAlert);
        apc.Order = deploy = deploy with { Out = deploy.Out + 1 };
        if (apc.Passengers.Count == 0)
            Raise(new GameEvent(GameEventKind.Deployed, apc.Id, apc.Position, Detail: deploy.Out.ToString()));
    }

    /// <summary>
    /// A hit below a fifth of its hit points at low DefCon, a tenth at normal, has it stop and let everyone out where it
    /// stands, unless it is deploying near here already; at high DefCon it never does (MachLogAPC::specialAPCHitStuff).
    /// </summary>
    private void ConsiderEvacuating(Machine apc)
    {
        var defCon = VirtualDefCon(apc);
        var ratio = defCon == DefCon.Low ? LowDefConApcEvacuationRatio : ApcEvacuationRatio;
        if (defCon == DefCon.High || apc.HitPoints >= apc.MaxHitPoints * ratio)
            return;
        if (apc.Order is DeployOrder deploy && Vector2.DistanceSquared(apc.Position, deploy.Target) < NearDropzone * NearDropzone)
            return;
        var orderedAt = apc.OrderedAt;
        Send(apc, new DeployOrder(apc.Position), null);
        apc.OrderedAt = orderedAt;
    }

    /// <summary>
    /// Passengers ride along. An APC with machines on their way in stops and turns its rear to the nearest, or, of those
    /// close by, the one it is already most turned away from, unless it is under way at high DefCon, evading or driven by
    /// the player (MachLogAPC::update, turnRearToFaceNearestIncoming, currentNearestMachine).
    /// </summary>
    private void UpdateTransport()
    {
        foreach (var apc in Machines.Where(m => m.Passengers.Count > 0).ToList())
        {
            apc.Passengers.RemoveAll(id => Find<Machine>(id) is null);
            foreach (var passenger in apc.Passengers.Select(Find<Machine>).OfType<Machine>())
                passenger.Position = apc.Position;
        }
        foreach (var (id, incoming) in _boarding)
        {
            if (Find<Machine>(id) is { IsDead: false, DrivenByPlayer: false } apc
                && (apc.Order is IdleOrder || VirtualDefCon(apc) != DefCon.High && apc.Order is not EvadeOrder))
                TurnRearTo(apc, NearestIncoming(apc, incoming));
        }
        _boarding.Clear();
    }

    private Machine NearestIncoming(Machine apc, List<Machine> incoming)
    {
        var nearest = incoming[0];
        var distance = float.MaxValue;
        foreach (var machine in incoming)
        {
            var to = Vector2.Distance(machine.Position, apc.Position);
            if (to + ApcGraceDistance < distance)
                (nearest, distance) = (machine, to);
        }
        if (distance > ApcCloseDistance)
            return nearest;
        return incoming.Where(m => Vector2.Distance(m.Position, apc.Position) < ApcCloseDistance)
            .MaxBy(m => MathF.Abs(AngleToFace(apc, m.Position))) ?? nearest;
    }

    private void TurnRearTo(Machine apc, Machine machine)
    {
        apc.Order = Order.Idle;
        apc.Path.Clear();
        apc.PathIndex = 0;
        _rearTo[apc.Id] = machine.Position;
    }

    /// <summary>MachLogSpacialManipulation::angleToTurnToFace.</summary>
    private static float AngleToFace(Machine machine, Vector2 point)
    {
        var to = point - machine.Position;
        return AngleBetween(machine.Facing, DetMath.Atan2(to.Y, to.X));
    }

    /// <summary>Everyone inside a destroyed APC dies with it.</summary>
    private void KillPassengers(Machine apc)
    {
        foreach (var passenger in apc.Passengers.Select(Find<Machine>).OfType<Machine>())
        {
            passenger.HitPoints = 0;
            passenger.LastHitBy = apc.LastHitBy;
        }
        apc.Passengers.Clear();
    }
}
