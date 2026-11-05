using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// Resource carriers on their rounds (machlog rescarr.cpp, optransp.cpp, oppickup.cpp, opputdwn.cpp): a list of
/// suppliers, mines or other races' smelters and pods, called at until the carrier is full, and one smelter or pod to
/// bring it all to.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>Seconds a carrier stands on a pad loading or dropping ore: 22 frames at 22 a second (MachPhysResourceLoading).</summary>
    public const float LoadingTime = 1;

    // The pad each carrier is heading for, and at which building.
    private readonly Dictionary<int, (int Stop, Vector2 Pad)> _carrierPads = [];

    /// <summary>MachLogMine::worthVisiting: it has ore ready or a deposit to dig.</summary>
    public static bool WorthVisiting(Construction mine) => mine.Ore >= 1 || mine.MineralSite is not null;

    /// <summary>
    /// Whether a carrier of the race can fetch from the building (MachGuiTransportCommand::pickOnActor): any mine, and
    /// other races' smelters and pods, whose BMUs it steals.
    /// </summary>
    public static bool Supplies(Construction stop, Race race) =>
        stop.IsType("MINE") || stop.Spec.Type is "SMELTER" or "POD" && stop.Race != race;

    private static IReadOnlyList<int> Suppliers(TransportOrder transport) =>
        transport.Round ?? (transport.Mine != 0 ? [transport.Mine] : []);

    /// <summary>
    /// The race's finished smelter or pod with the lowest total distance to all suppliers, dormant mines left out, a
    /// smelter before a pod as near; none when no supplier counts (MachLogResourceCarrier::findBestSmeltingBuilding).
    /// </summary>
    private Construction? BestSmelter(Machine carrier, IEnumerable<int> suppliers)
    {
        var stops = suppliers.Select(Find<Construction>).OfType<Construction>().Where(s => !s.IsType("MINE") || WorthVisiting(s)).ToList();
        if (stops.Count == 0)
            return null;
        return Constructions
            .Where(c => c.Race == carrier.Race && c.Complete && !c.IsDead && c.Spec.Type is "SMELTER" or "POD")
            .OrderBy(c => c.Spec.Type == "POD" ? 1 : 0)
            .MinBy(c => stops.Sum(s => Vector2.Distance(s.Position, c.Position)));
    }

    /// <summary>
    /// Nearest first, from the smelter to the nearest supplier and on from there, unless the machine's DefCon is high,
    /// which leaves the player the order they clicked (MachLogResourceCarrier::reorderTransportRoute).
    /// </summary>
    private TransportOrder Reordered(Machine carrier, TransportOrder transport)
    {
        if (transport.Round is not { Count: > 1 } round || carrier.DefCon == DefCon.High || Find<Construction>(transport.Smelter) is not { } smelter)
            return transport;
        var left = round.Select(Find<Construction>).OfType<Construction>().ToList();
        var ordered = new List<int>(left.Count);
        var from = smelter.Position;
        while (left.Count > 0)
        {
            var next = left.MinBy(s => Vector2.DistanceSquared(from, s.Position))!;
            left.Remove(next);
            ordered.Add(next.Id);
            from = next.Position;
        }
        return transport with { Round = ordered };
    }

    /// <summary>
    /// A carrier told to fetch from <paramref name="stops"/>: with <paramref name="smelter"/> set it stays with that
    /// one, else it finds its own (MachLogResourceCarrier::setSuppliers, addSuppliers, setSmeltingBuilding). Already on a
    /// round it keeps what it was doing: one putting down goes on doing so, any other starts with a pick-up.
    /// </summary>
    private void GiveRound(Machine carrier, IReadOnlyList<int> stops, Construction? smelter, bool adding)
    {
        var current = carrier.Order as TransportOrder;
        var suppliers = (adding && current is not null ? Suppliers(current).Concat(stops) : stops).Distinct().ToList();
        var assigned = smelter is not null || current is { Assigned: true };
        var store = smelter?.Id ?? (assigned ? current!.Smelter : BestSmelter(carrier, suppliers)?.Id ?? 0);
        var order = Reordered(carrier, new TransportOrder(adding && current is not null ? current.Mine : suppliers[0], store,
            Delivering: current is { Delivering: true }, Round: suppliers.Count > 1 ? suppliers : null, Assigned: assigned));
        var to = order.Delivering && Find<Construction>(store) is { } going ? going : Find<Construction>(order.Mine);
        Send(carrier, order, to?.Position);
    }

    private void UpdateTransport(Machine carrier, TransportOrder transport)
    {
        if (RouteStands(carrier, transport) is not { } route)
            return;
        // Loading or dropping, it stands on the pad as long as the rings of light play (MachPhysResourceLoading).
        if (Time < route.WaitUntil)
            return;
        if (route.Delivering)
            PutDown(carrier, route);
        else
            PickUp(carrier, route);
    }

    /// <summary>
    /// Sees to what became of the route (MachLogResourceCarrier::beNotified, mayHaveLostRouteElement): suppliers gone,
    /// the smelter gone or gone over to another race, and, once a second, a better smelter for a carrier free to choose
    /// (informOfNewSmeltingBuildingCompletion, MINE_CHANGED_OPERATIONAL_STATUS). Returns what is left to do, or null if
    /// nothing is.
    /// </summary>
    private TransportOrder? RouteStands(Machine carrier, TransportOrder transport)
    {
        var standing = Suppliers(transport).Where(id => Find<Construction>(id) is not null).ToList();
        if (standing.Count < Suppliers(transport).Count)
        {
            if (LoseSuppliers(carrier, transport, standing) is not { } left)
                return null;
            transport = left;
        }

        if (transport.Smelter != 0 && (Find<Construction>(transport.Smelter) is not { } store || Options.OriginalCapture && store.Race != carrier.Race))
        {
            // A smelter lost gives way to the race's best other, however it had been chosen.
            transport = transport with { Smelter = 0, Assigned = false };
            if (BestSmelter(carrier, Suppliers(transport)) is { } other)
            {
                transport = Reordered(carrier, transport with { Smelter = other.Id });
                ReportNewDestination(carrier);
            }
            else
            {
                ReportRouteLost(carrier);
                if (Suppliers(transport).Count == 0)
                {
                    carrier.Order = Order.Idle;
                    return null;
                }
            }
            carrier.Order = transport;
            carrier.Path.Clear();
            carrier.PathIndex = 0;
        }
        else if (!transport.Assigned && Tick % TicksPerSecond == carrier.Id % TicksPerSecond
            && BestSmelter(carrier, Suppliers(transport)) is { } best && best.Id != transport.Smelter)
        {
            if (transport.Smelter != 0)
                ReportNewDestination(carrier);
            carrier.Order = transport = Reordered(carrier, transport with { Smelter = best.Id });
            if (transport.Delivering)
            {
                carrier.Path.Clear();
                carrier.PathIndex = 0;
            }
        }
        return transport;
    }

    /// <summary>
    /// Some of a carrier's suppliers are gone and <paramref name="left"/> remain: it goes on with the one after the
    /// lost one, or starts delivering if that was the last; with none left it reports the route lost and stops, but for
    /// bringing home what it is already on its way with (MachLogResourceCarrier::mayHaveLostRouteElement).
    /// </summary>
    private TransportOrder? LoseSuppliers(Machine carrier, TransportOrder transport, IReadOnlyList<int> left)
    {
        var suppliers = Suppliers(transport).ToList();
        var next = suppliers.Skip(Math.Max(0, suppliers.IndexOf(transport.Mine))).FirstOrDefault(left.Contains);
        transport = transport with { Mine = next, Round = left };
        if (left.Count == 0)
        {
            ReportRouteLost(carrier);
            if (!transport.Delivering)
            {
                carrier.Order = Order.Idle;
                return null;
            }
            carrier.Order = transport;
            return transport;
        }
        if (!transport.Assigned && BestSmelter(carrier, left) is { } best)
            transport = transport with { Smelter = best.Id };
        transport = Reordered(carrier, transport);
        carrier.Order = transport;
        if (next == 0)
            EndPickUp(carrier, transport);
        return carrier.Order as TransportOrder;
    }

    /// <summary>
    /// A building changed sides: it drops out of the routes of carriers it no longer serves, whether it is now another
    /// race's or now theirs and a store they would only be stealing from themselves (MachLogResourceCarrier::beNotified,
    /// RACE_CHANGED).
    /// </summary>
    private void DropFromRoutes(Construction building)
    {
        foreach (var carrier in Machines)
        {
            if (carrier.Order is TransportOrder transport && Suppliers(transport).Contains(building.Id)
                && (building.Race != carrier.Race || building.Spec.Type is "SMELTER" or "POD"))
                LoseSuppliers(carrier, transport, Suppliers(transport).Where(id => id != building.Id).ToList());
        }
    }

    /// <summary>Done with the suppliers for now: ore on board goes to the smelter, and next time it starts at the first again (MachLogPickUpOperation::terminateOp).</summary>
    private void EndPickUp(Machine carrier, TransportOrder transport)
    {
        carrier.Order = transport with { Mine = Suppliers(transport).FirstOrDefault(), Delivering = carrier.Ore > 0 };
        carrier.Path.Clear();
        carrier.PathIndex = 0;
    }

    private static bool ReadyToLoad(Construction stop) => stop.Complete && (!stop.IsType("MINE") || stop.Ore >= 1);

    /// <summary>
    /// MachLogPickUpOperation::doUpdate. A supplier unfinished or without ore gives way to a later one that has some;
    /// with none, a carrier with cargo takes that home instead of waiting, and at a mine whose deposit is gone there is
    /// nothing to wait for: it stands and starts over with the first. At any other it waits for ore.
    /// </summary>
    private void PickUp(Machine carrier, TransportOrder transport)
    {
        var suppliers = Suppliers(transport).ToList();
        var capacity = carrier.Stats.Capacity ?? 0;
        if (carrier.Ore >= capacity || suppliers.Count == 0)
        {
            EndPickUp(carrier, transport);
            return;
        }
        var stop = Find<Construction>(transport.Mine)!;
        if (!ReadyToLoad(stop))
        {
            var better = suppliers.Skip(suppliers.IndexOf(stop.Id)).Select(Find<Construction>).OfType<Construction>().FirstOrDefault(ReadyToLoad);
            if (better is not null)
            {
                carrier.Order = transport = transport with { Mine = better.Id };
                carrier.Path.Clear();
                carrier.PathIndex = 0;
                stop = better;
            }
            else if (carrier.Ore > 0 && transport.Smelter != 0 || stop.IsType("MINE") && stop.MineralSite is null)
            {
                EndPickUp(carrier, transport);
                return;
            }
        }

        // From a mine's pickup pads; other races' stores give at their putdown pads, which is all they have.
        var mine = stop.IsType("MINE");
        if (!AtPad(carrier, stop, mine ? stop.PickUpPads : stop.PutDownPads) || !stop.Complete)
            return;

        var room = capacity - carrier.Ore;
        var loaded = 0;
        if (!mine)
        {
            // It steals as much as it can; what comes of nothing lets it try again (MachLogPickUpOperation::doUpdate).
            loaded = (int)SpendBmu(stop.Race, MathF.Min(room, MathF.Floor(Races[stop.Race].Bmu)));
            carrier.Ore += loaded;
            FinishLoading(carrier, transport, loaded > 0 ? LoadingTime : 2);
        }
        else if (stop.Ore >= 1)
        {
            // It takes what the mine has ready, up to its capacity, and is off with it; at an empty mine it waits.
            loaded = (int)MathF.Min(room, stop.Ore);
            carrier.Ore += loaded;
            stop.Ore -= loaded;
            FinishLoading(carrier, transport, LoadingTime);
        }
        carrier.Path.Clear();
        carrier.PathIndex = 0;
    }

    /// <summary>On to the next supplier while it has room and has not been to the last, else home (MachLogPickUpOperation::moveToNextSupplier).</summary>
    private void FinishLoading(Machine carrier, TransportOrder transport, float wait)
    {
        transport = transport with { WaitUntil = Time + wait };
        var suppliers = Suppliers(transport).ToList();
        var next = suppliers.IndexOf(transport.Mine) + 1;
        if (next < suppliers.Count && carrier.Ore < (carrier.Stats.Capacity ?? 0))
            carrier.Order = transport with { Mine = suppliers[next] };
        else
            EndPickUp(carrier, transport);
    }

    private void PutDown(Machine carrier, TransportOrder transport)
    {
        // Without a smelter it stands with its cargo until one is finished.
        if (Find<Construction>(transport.Smelter) is not { } smelter || !AtPad(carrier, smelter, smelter.PutDownPads) || !smelter.Complete)
            return;

        // Ore becomes BMUs at once; whatever does not fit in storage stays on board, and the carrier waits
        // with it until there is room (MachLogPutDownOperation).
        // An AI's ore is worth more or less as its level says (MatchOptions.AiLevel).
        var worth = OreWorth(carrier.Race);
        var added = AddBmu(carrier.Race, carrier.Ore * worth);
        carrier.Ore -= worth == 1 ? (int)added : Math.Min(carrier.Ore, (int)MathF.Round(added / worth));
        if (added > 0)
            smelter.OreDroppedTick = Tick;
        if (carrier.Ore <= 0)
        {
            var suppliers = Suppliers(transport);
            carrier.Order = suppliers.Count == 0
                ? Order.Idle
                : transport with { Delivering = false, WaitUntil = Time + LoadingTime, Mine = suppliers[0] };
        }
        carrier.Path.Clear();
        carrier.PathIndex = 0;
    }

    /// <summary>
    /// Drives right onto one of the building's pads, and tells when it may load or drop: once it stands within one
    /// and a half times its clearance of it (MachLogPickUpOperation, MachLogPutDownOperation); a building without pads
    /// is served from its edge.
    /// </summary>
    private bool AtPad(Machine carrier, Construction stop, IReadOnlyList<Vector2> pads)
    {
        if (pads.Count == 0)
        {
            if (InReach(carrier, stop))
                return true;
            Approach(carrier, ClosestPointOutside(stop, carrier.Position));
            return false;
        }
        var pad = PadFor(carrier, stop, pads);
        var reach = MathF.Max(1, carrier.Stats.HighClearance * 1.5f);
        var distance = Vector2.Distance(carrier.Position, pad);
        if (distance <= 0.05f)
            return true;
        // The grid blocks the cells round a building; a pad lying in one is driven onto straight.
        if (!Nav.IsFree(pad, CrossesWater(carrier)) && distance < Navigation.NavGrid.CellSize * 2.5f)
        {
            carrier.Path.Clear();
            carrier.PathIndex = 0;
            StepTowards(carrier, pad);
            return false;
        }
        // It drives the whole way onto the pad's point (MachLogMoveToOperation); one held up short of it
        // still loads within its reach.
        if (distance > reach)
        {
            Approach(carrier, pad);
            return false;
        }
        if (carrier.PathIndex < carrier.Path.Count)
        {
            FollowPath(carrier);
            return false;
        }
        return true;
    }

    /// <summary>
    /// The pad a carrier heads for at a building: the one it chose on the way there, else the nearest that no
    /// other carrier is heading for, else the nearest (MachLogPickUpOperation chose the nearest free one).
    /// </summary>
    private Vector2 PadFor(Machine carrier, Construction stop, IReadOnlyList<Vector2> pads)
    {
        if (_carrierPads.TryGetValue(carrier.Id, out var chosen) && chosen.Stop == stop.Id)
            return chosen.Pad;
        bool Taken(Vector2 pad) => _carrierPads.Any(p => p.Key != carrier.Id && p.Value.Stop == stop.Id && p.Value.Pad == pad
            && Find<Machine>(p.Key) is { Order: TransportOrder });
        var byDistance = pads.OrderBy(p => Vector2.DistanceSquared(p, carrier.Position)).ToList();
        var pad = byDistance.FirstOrDefault(p => !Taken(p), byDistance[0]);
        _carrierPads[carrier.Id] = (stop.Id, pad);
        return pad;
    }
}
