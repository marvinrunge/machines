using System.Numerics;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- orders picked with Shift (the lists MachGuiCommand's pickOnActor builds up) -------------

    // The steps each machine has still to carry out, after the order it has now.
    private readonly Dictionary<int, Queue<Command>> _series = [];

    private string? ApplySeries(SeriesCommand series)
    {
        if (series.Steps.Count == 0 || series.Steps.Any(s => s is SeriesCommand or SquadronCommand || s.Race != series.Race))
            return "not a series of orders";
        var machines = Own<Machine>(series.Race, series.Machines).ToList();
        var steps = new List<Command>();
        string? why = null;
        foreach (var step in series.Steps)
        {
            if (step is not PlaceConstructionCommand place)
            {
                steps.Add(step);
                continue;
            }
            why = Place(place with { Constructors = [] }, out var site);
            if (site is not null)
                steps.Add(new ConstructCommand(series.Race, [], site.Id));
        }
        foreach (var machine in machines)
        {
            Send(machine, Order.Idle, null);
            _series[machine.Id] = new Queue<Command>(steps);
            NextInSeries(machine, first: true);
        }
        return why;
    }

    /// <summary>A machine with nothing to do takes up the next step of its series; a step no longer possible is passed over.</summary>
    private void NextInSeries(Machine machine, bool first = false)
    {
        if (machine.Order is not IdleOrder || machine.DrivenByPlayer || !_series.TryGetValue(machine.Id, out var steps))
            return;
        // Not an order from the player, or it would take the steps left away.
        var command = _commandId;
        _commandId = 0;
        while (machine.Order is IdleOrder && steps.TryDequeue(out var step))
        {
            Apply(Retarget(step, [machine.Id]));
            // A constructor says so when it goes on to its next site (MachLogSuperConstructOperation::doUpdate).
            if (!first && IsConstructor(machine) && machine.Order is not IdleOrder && step is ConstructCommand or RepairCommand or CaptureCommand or DeconstructCommand)
                SendVoiceMail(machine.Race, "VID_DOZER_MOVING_TO_NEXT", machine.Id);
        }
        _commandId = command;
        if (steps.Count == 0)
            _series.Remove(machine.Id);
    }

    /// <summary>The way through the picked points in turn, each stretch from where the last ends (MachLogMoveToOperation's path).</summary>
    private void RouteThrough(Machine machine, IReadOnlyList<Vector2> via, Vector2 destination)
    {
        machine.Path.Clear();
        machine.PathIndex = 0;
        var from = machine.Position;
        foreach (var point in via.Append(destination))
        {
            var stretch = Nav.FindPath(from, point, CrossesWater(machine));
            machine.Path.AddRange(stretch);
            if (stretch.Count > 0)
                from = stretch[^1];
        }
    }
}
