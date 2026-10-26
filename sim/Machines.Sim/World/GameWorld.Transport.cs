using System.Numerics;

namespace Machines.Sim.World;

/// <summary>
/// APCs carry machines (machlog apc.cpp): a machine boards when the APC has room for its VOLUME within the
/// APC's CAPACITY, rides off the map, and comes out where the APC deploys. Passengers die with their APC, and
/// an APC near destruction lets them out where it is.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>MachLogAPC: with passengers aboard, an APC below this share of its hit points evacuates them.</summary>
    public const float ApcEvacuationRatio = 0.10f;

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
        if (machine.DistanceTo(apc) > InteractionDistance + machine.Radius)
        {
            Approach(machine, apc.Position);
            return;
        }

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

    /// <summary>Lets every passenger out on free ground around the APC.</summary>
    private void Unload(Machine apc)
    {
        var passengers = apc.Passengers.Select(Find<Machine>).OfType<Machine>().ToList();
        apc.Passengers.Clear();
        for (var i = 0; i < passengers.Count; i++)
        {
            var machine = passengers[i];
            machine.Inside = null;
            machine.Order = Order.Idle;
            machine.Position = FreeSpotAround(apc.Position, i, machine);
        }
        if (passengers.Count > 0)
            Raise(new GameEvent(GameEventKind.Deployed, apc.Id, apc.Position, Detail: passengers.Count.ToString()));
    }

    /// <summary>The n-th free spot on rings around a point, 8 m apart.</summary>
    private Vector2 FreeSpotAround(Vector2 centre, int n, Machine machine)
    {
        var tried = 0;
        for (var ring = 1; ring <= 6; ring++)
        {
            var spots = 6 * ring;
            for (var s = 0; s < spots; s++)
            {
                var angle = MathF.Tau * s / spots;
                var spot = centre + new Vector2(MathF.Cos(angle), MathF.Sin(angle)) * (8 * ring);
                if (!Nav.IsFree(spot, CrossesWater(machine)))
                    continue;
                if (tried++ == n)
                    return spot;
            }
        }
        return centre;
    }

    /// <summary>Passengers ride along; an APC close to destruction lets them out (apc.cpp specialAPCHitStuff).</summary>
    private void UpdateTransport()
    {
        foreach (var apc in Machines.Where(m => m.Passengers.Count > 0).ToList())
        {
            apc.Passengers.RemoveAll(id => Find<Machine>(id) is null);
            foreach (var passenger in apc.Passengers.Select(Find<Machine>).OfType<Machine>())
                passenger.Position = apc.Position;
            if (!apc.IsDead && apc.HitPoints < apc.MaxHitPoints * ApcEvacuationRatio)
                Unload(apc);
        }
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
