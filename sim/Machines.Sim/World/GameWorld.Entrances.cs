using System.Numerics;

namespace Machines.Sim.World;

/// <summary>
/// Going into a building as the original's machines did (MachLog1stPersonMachineHandler::doUpdate): a door to pass and
/// a floor behind it. Driven in first person a machine goes through the door of any building of its race or an ally's
/// that it fits, and whoever is in stands at one of the bays there.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>Whether a machine may go into a building at all (MachLogMachine::canEnterConstruction).</summary>
    public bool CanEnter(Machine machine, Construction building) =>
        building.HasInterior && building.Complete && !building.IsDead && !AreEnemies(machine.Race, building.Race) && FitsThrough(machine, building);

    /// <summary>Whether a building has room for one more: a bay no one holds. A machine driven round it holds none.</summary>
    public bool HasFreeBay(Construction building) =>
        building.Technicians.Count + building.Garrisoned.Count(id => Find<Machine>(id) is not { DrivenByPlayer: true }) < Math.Max(1, building.Bays.Count);

    /// <summary>Where a machine going into a building comes to stand: the first bay free, or the middle of the building.</summary>
    private Vector2 BayFor(Machine machine, Construction building)
    {
        foreach (var bay in building.Bays)
            if (!Machines.Any(m => m != machine && m.Inside == building.Id && Vector2.DistanceSquared(m.Position, bay) < 1))
                return bay;
        return building.Position;
    }

    /// <summary>The building whose doorway or floor a machine at <paramref name="point"/> would be on, if any.</summary>
    private Construction? WayInAt(Machine machine, Vector2 point)
    {
        foreach (var building in Constructions)
            if (OnWayIn(machine, building, point) && CanEnter(machine, building))
                return building;
        return null;
    }

    private static bool OnWayIn(Machine machine, Construction building, Vector2 point)
    {
        if (!building.HasInterior)
            return false;
        // The machine's middle stays a clearance from the walls, as far as the floor has room for that.
        var (min, max) = building.Interior!.Value;
        var clearance = Vector2.Min(new Vector2(machine.Radius), (max - min) / 4);
        if (point.X >= min.X + clearance.X && point.X <= max.X - clearance.X && point.Y >= min.Y + clearance.Y && point.Y <= max.Y - clearance.Y)
            return !building.InteriorObstacles.Any(o => point.X >= o.Min.X && point.X <= o.Max.X && point.Y >= o.Min.Y && point.Y <= o.Max.Y);

        // The doorway: from the point before the door to a clearance beyond the point inside it.
        var outside = building.EntranceOutside!.Value;
        var way = building.EntranceInside!.Value - outside;
        var length = way.Length();
        if (length < 1e-3f)
            return false;
        var along = way / length;
        var offset = point - outside;
        var through = Vector2.Dot(offset, along);
        var across = MathF.Abs(offset.X * along.Y - offset.Y * along.X);
        return through >= 0 && through <= length + machine.Radius && across <= MathF.Max(building.DoorWidth / 2 - 1, 1.5f);
    }

    /// <summary>Whether a point of a building's doorway or floor lies behind its door (MachLogConstruction::onOutsideSideOfThreshold).</summary>
    private static bool BehindDoor(Construction building, Vector2 point) =>
        Vector2.Dot(point - building.Door!.Value, building.EntranceInside!.Value - building.EntranceOutside!.Value) >= 0;

    /// <summary>
    /// A driven machine crosses the threshold: behind the door it is inside the building, in front of it out again. The
    /// door stands open while it is on the way out there or still near it inside.
    /// </summary>
    private void CrossThreshold(Machine machine)
    {
        var building = WayInAt(machine, machine.Position);
        var inside = building is not null && BehindDoor(building, machine.Position);
        machine.Doorway = building is not null && (!inside || Vector2.Distance(machine.Position, building.Door!.Value) < building.DoorWidth)
            ? building.Id : null;
        if (machine.Inside is { } id && (!inside || building!.Id != id))
        {
            Find<Construction>(id)?.Garrisoned.Remove(machine.Id);
            machine.Inside = null;
        }
        if (inside && machine.Inside is null)
        {
            machine.Inside = building!.Id;
            building.Garrisoned.Add(machine.Id);
        }
    }

    /// <summary>
    /// The player lets go of a machine in a building: it drives to a bay, and a technician left in a lab of its race
    /// takes it up as if ordered in. Let go in the doorway, it backs out.
    /// </summary>
    private void LetGoInside(Machine machine)
    {
        var doorway = machine.Doorway;
        machine.Doorway = null;
        if (machine.Inside is { } id && Find<Construction>(id) is { } building)
        {
            machine.Position = BayFor(machine, building);
            if (machine.Spec.Type == "TECHNICIAN" && building.IsType("HARDWARE_LAB") && building.Race == machine.Race
                && building.Garrisoned.Remove(machine.Id))
                building.Technicians.Add(machine.Id);
        }
        else if (doorway is { } way && Find<Construction>(way) is { } door && !Nav.IsFree(machine.Position, CrossesWater(machine)))
        {
            machine.Position = DoorOf(door, machine);
        }
    }
}
