using System.Numerics;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    /// <summary>How close a machine must be to a building's edge to work on it, load or unload.</summary>
    public const float InteractionDistance = 6f;

    /// <summary>
    /// Whether a machine is close enough to work on a building. The grid blocks up to one cell around a
    /// footprint, so the nearest free spot can be a cell further out; that still counts.
    /// </summary>
    /// <summary>The building a constructor is at work on (building or repairing it, and standing at it), if any.</summary>
    public Construction? WorkedOn(Machine machine) => machine.Order switch
    {
        ConstructOrder construct when Find<Construction>(construct.Construction) is { Complete: false } site && InReach(machine, site) => site,
        RepairOrder repair when Find<Construction>(repair.Building) is { } damaged && InReach(machine, damaged) => damaged,
        _ => null,
    };

    private static bool InReach(Machine machine, Construction building) =>
        building.DistanceFrom(machine.Position) <= InteractionDistance + machine.Radius + Navigation.NavGrid.CellSize * 1.5f;

    private readonly Dictionary<int, (Vector2 Position, float Since)> _progress = [];

    /// <summary>Seconds between route updates while chasing a moving target.</summary>
    private const float RepathInterval = 1.5f;

    private readonly Dictionary<int, float> _nextRepath = [];

    /// <summary>First-person controls per machine: forward speed (-1..1) and turn rate (-1..1, positive = left).</summary>
    public Dictionary<int, (float Forward, float Turn)> DriveInput { get; } = [];

    private void Drive(Machine machine)
    {
        DriveInput.TryGetValue(machine.Id, out var input);
        // The original's first-person turn rate (degrees per second) from GENERAL_DATA.
        var turnRate = Data.Parm.General.FirstPersonMaxFastTurnRate * MathF.PI / 180;
        machine.Facing += input.Turn * turnRate * Dt;
        var direction = new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing));
        var step = direction * (input.Forward * machine.Stats.Speed * Dt);
        if (step == Vector2.Zero)
            return;
        // Blocked ahead, it slides along the obstacle as far as the way is open, on whichever of the grid's axes
        // still lets it on (MachLog1stPersonMachineHandler::nextPositionOk slid along the polygon's edge).
        var (along, across) = MathF.Abs(step.X) >= MathF.Abs(step.Y)
            ? (new Vector2(step.X, 0), new Vector2(0, step.Y))
            : (new Vector2(0, step.Y), new Vector2(step.X, 0));
        foreach (var move in (ReadOnlySpan<Vector2>)[step, along, across])
        {
            if (CanDriveTo(machine, machine.Position + move))
            {
                machine.Position += move;
                return;
            }
        }
    }

    /// <summary>
    /// Whether a machine driven in first person may go on to <paramref name="next"/>: onto open ground on the planet,
    /// or, while it stands on blocked ground (beside a building, or set down on rock), anywhere nearer to open ground.
    /// </summary>
    private bool CanDriveTo(Machine machine, Vector2 next)
    {
        var water = CrossesWater(machine);
        if (!Nav.OnPlanet(next))
            return false;
        if (Nav.IsFree(next, water))
            return true;
        return !Nav.IsFree(machine.Position, water) && Nav.NearestFreePoint(machine.Position, water) is { } open
            && Vector2.DistanceSquared(next, open) < Vector2.DistanceSquared(machine.Position, open);
    }

    private void UpdateMachine(Machine machine)
    {
        if (machine.DrivenByPlayer)
        {
            Drive(machine);
            if (machine.Order is AttackOrder)
                machine.Order = Order.Idle;
            return;
        }

        switch (machine.Order)
        {
            case MoveOrder move:
                if (!FollowPath(machine))
                    machine.Order = Order.Idle;
                else
                    TakeInitiative(machine, move.Target);
                break;

            case StandGroundOrder:
                // It fires from where it stands (UpdateCombat) and goes nowhere.
                break;

            case DropMinesOrder drop:
                UpdateDropMines(machine, drop);
                break;

            case RefillMinesOrder refill:
                UpdateRefillMines(machine, refill);
                break;

            case EvadeOrder evade:
                UpdateEvade(machine, evade);
                break;

            case AttackMoveOrder attackMove:
                if (!FollowPath(machine))
                    machine.Order = Order.Idle;
                else if (NearestEnemy(machine, machine.Stats.ScannerRange) is { } enemy)
                    Send(machine, new AttackOrder(enemy.Id, attackMove.Target), enemy.Position);
                break;

            case AttackOrder attack:
                UpdateAttack(machine, attack);
                break;

            case ConstructOrder construct:
                UpdateConstruct(machine, construct);
                break;

            case LocateOrder locate:
                UpdateLocate(machine, locate);
                break;

            case TransportOrder transport:
                UpdateTransport(machine, transport);
                break;

            case EnterLabOrder enter:
                UpdateEnterLab(machine, enter);
                break;

            case PatrolOrder patrol:
                if (!FollowPath(machine))
                {
                    var next = (patrol.Next + 1) % patrol.Points.Count;
                    Send(machine, patrol with { Next = next }, patrol.Points[next]);
                }
                else if (machine.CanFight && NearestEnemy(machine, machine.Stats.ScannerRange) is { } enemy)
                {
                    Send(machine, new AttackOrder(enemy.Id, patrol.Points[patrol.Next]), enemy.Position);
                }
                break;

            case HealOrder heal:
                UpdateHeal(machine, heal);
                break;

            case RepairOrder repair:
                UpdateRepair(machine, repair);
                break;

            case CaptureOrder capture:
                UpdateCapture(machine, capture);
                break;

            case DeconstructOrder deconstruct:
                UpdateDeconstruct(machine, deconstruct);
                break;

            case RecycleOrder recycle:
                UpdateRecycle(machine, recycle);
                break;

            case EnterGarrisonOrder garrison:
                UpdateEnterGarrison(machine, garrison);
                break;

            case BoardOrder board:
                UpdateBoard(machine, board);
                break;

            case DeployOrder deploy:
                if (!FollowPath(machine))
                {
                    Unload(machine);
                    machine.Order = Order.Idle;
                }
                break;

            case IdleOrder when machine.CanHeal && !machine.DrivenByPlayer:
                SeekPatients(machine);
                break;

            case ScavengeOrder scavenge:
                UpdateScavenge(machine, scavenge);
                break;

            case IdleOrder when machine.IsScavenger:
                SeekDebris(machine);
                break;

            default:
                // Idle fighters take on what their initiative lets them.
                if (machine.CanFight && !machine.DrivenByPlayer && Tick % 10 == machine.Id % 10
                    && InitiativeTarget(machine) is { } threat)
                    Send(machine, new AttackOrder(threat.Id), threat.Position);
                break;
        }
    }

    /// <summary>Moves along the planned path; false once there is nothing left to walk.</summary>
    private bool FollowPath(Machine machine)
    {
        if (machine.DrivenByPlayer)
            return true;
        if (machine.PathIndex >= machine.Path.Count)
            return false;

        var target = machine.Path[machine.PathIndex];
        var step = machine.Stats.Speed * Dt;
        var offset = target - machine.Position;
        var distance = offset.Length();

        if (distance <= step)
        {
            machine.Position = target;
            machine.PathIndex++;
        }
        else
        {
            var direction = offset / distance;
            machine.Position += direction * step;
            machine.Facing = DetMath.Atan2(direction.Y, direction.X);
        }

        // Plan again when the waypoint itself got blocked (usually by a new building), or when the machine
        // has made no headway for a few seconds.
        if (machine.PathIndex < machine.Path.Count && !Nav.IsFree(machine.Path[machine.PathIndex], CrossesWater(machine)) || Stuck(machine))
        {
            var goal = machine.Path[^1];
            machine.Path.Clear();
            machine.PathIndex = 0;
            machine.Path.AddRange(Nav.FindPath(machine.Position, goal, CrossesWater(machine)));
            return machine.Path.Count > 0;
        }
        return true;
    }

    private bool Stuck(Machine machine)
    {
        if (!_progress.TryGetValue(machine.Id, out var last) || Vector2.DistanceSquared(last.Position, machine.Position) > 4)
        {
            _progress[machine.Id] = (machine.Position, Time);
            return false;
        }
        if (Time - last.Since < 3)
            return false;
        _progress[machine.Id] = (machine.Position, Time);
        return true;
    }

    private void Approach(Machine machine, Vector2 goal)
    {
        _nextRepath.TryGetValue(machine.Id, out var due);
        if (machine.PathIndex >= machine.Path.Count || Time >= due)
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            machine.Path.AddRange(Nav.FindPath(machine.Position, goal, CrossesWater(machine)));
            _nextRepath[machine.Id] = Time + RepathInterval;
        }
        FollowPath(machine);
    }

    private void Face(Machine machine, Vector2 point)
    {
        var d = point - machine.Position;
        if (d.LengthSquared() > 0.01f)
            machine.Facing = DetMath.Atan2(d.Y, d.X);
    }

    // ---- fighting -------------------------------------------------------------------------------

    private void UpdateAttack(Machine machine, AttackOrder attack)
    {
        var target = Find(attack.Target);
        if (target is null || target.IsDead || !AreEnemies(machine.Race, target.Race))
        {
            // On an attack-move, the next enemy in sight is taken on at once, as the move would the next tick:
            // when a target falls, everyone who shot at it would otherwise plan the way on to its own place,
            // only to drop it again.
            // One that broke off a move for the fight goes on with its move.
            if (attack is { Resume: true, Then: { } resume })
                Send(machine, new MoveOrder(resume), resume);
            else if (attack.Then is { } then && NearestEnemy(machine, machine.Stats.ScannerRange) is { } next)
                Send(machine, new AttackOrder(next.Id, then), next.Position);
            else if (attack.Then is { } on)
                Send(machine, new AttackMoveOrder(on), on);
            else
                machine.Order = Order.Idle;
            return;
        }

        var range = machine.Weapons.Where(w => w.DealsDamage || w.IsTreachery).Select(w => w.Spec.Range).DefaultIfEmpty(0).Min();
        // In range but hidden behind a hill, it moves closer (MachLogAttackOperation's MOVE_CLOSER for a target behind cover).
        if (machine.DistanceTo(target) <= range * 0.95f && ClearShot(machine, target))
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            // A machine that turns its upper body aims with that and leaves its hull as it stands; the others turn
            // to face the target (MachLogAttackOperation's TURN_TO_FACE is for "non torso-twisty" machines).
            if (!machine.CanTurnUpperBody)
                Face(machine, target.Position);
        }
        else if (!machine.DrivenByPlayer)
        {
            Approach(machine, target.Position);
        }
    }

    // ---- working --------------------------------------------------------------------------------

    private void UpdateConstruct(Machine machine, ConstructOrder construct)
    {
        var site = Find<Construction>(construct.Construction);
        if (site is null || site.Complete)
        {
            machine.Order = Order.Idle;
            return;
        }

        if (!InReach(machine, site))
        {
            Approach(machine, ClosestPointOutside(site, machine.Position));
            return;
        }

        Face(machine, site.Position);
        // The original's rate: RATE_CONSTRUCTION BMUs per minute, paid from the bank as the work goes on.
        var rate = (machine.Stats.ConstructionRate ?? 100) / 60f * Dt;
        var wanted = MathF.Min(rate, site.Stats.Cost - site.Built);
        var paid = SpendBmu(machine.Race, wanted);
        site.AddConstruction(paid);
        if (site.Complete)
        {
            site.MakeComplete();
            Races[site.Race].Score.ConstructionsBuilt++;
            Raise(new GameEvent(GameEventKind.ConstructionComplete, site.Id, site.Position, machine.Id, site.TypeName));
            machine.Order = Order.Idle;
        }
    }

    private void UpdateTransport(Machine carrier, TransportOrder transport)
    {
        var mine = Find<Construction>(transport.Mine);
        var smelter = Find<Construction>(transport.Smelter);
        if (mine is null || smelter is null)
        {
            carrier.Order = Order.Idle;
            return;
        }

        // Loading or dropping, it stands on the pad as long as the rings of light play (MachPhysResourceLoading).
        if (Time < transport.WaitUntil)
            return;
        var capacity = carrier.Stats.Capacity ?? 0;
        var delivering = transport.Delivering;
        var stop = delivering ? smelter : mine;

        // It drives right onto one of the building's pads, loading or dropping once it stands within one and a
        // half times its clearance of it (MachLogPickUpOperation, MachLogPutDownOperation); a building without pads
        // is served from its edge.
        var pads = delivering ? stop.PutDownPads : stop.PickUpPads;
        if (pads.Count > 0)
        {
            var pad = PadFor(carrier, stop, pads);
            var reach = MathF.Max(1, carrier.Stats.HighClearance * 1.5f);
            var distance = Vector2.Distance(carrier.Position, pad);
            if (distance > 0.05f)
            {
                // The grid blocks the cells round a building; a pad lying in one is driven onto straight.
                if (!Nav.IsFree(pad, CrossesWater(carrier)) && distance < Navigation.NavGrid.CellSize * 2.5f)
                {
                    carrier.Path.Clear();
                    carrier.PathIndex = 0;
                    StepTowards(carrier, pad);
                    return;
                }
                // It drives the whole way onto the pad's point (MachLogMoveToOperation); one held up short of it
                // still loads within its reach.
                if (distance > reach)
                {
                    Approach(carrier, pad);
                    return;
                }
                if (carrier.PathIndex < carrier.Path.Count)
                {
                    FollowPath(carrier);
                    return;
                }
            }
        }
        else if (!InReach(carrier, stop))
        {
            Approach(carrier, ClosestPointOutside(stop, carrier.Position));
            return;
        }

        if (!stop.Complete)
            return;

        if (delivering)
        {
            // Ore becomes BMUs at once; whatever does not fit in storage stays on board, and the carrier waits
            // with it until there is room (MachLogPutDownOperation).
            var added = AddBmu(carrier.Race, carrier.Ore);
            carrier.Ore -= (int)added;
            if (added > 0)
                smelter.OreDroppedTick = Tick;
            if (carrier.Ore <= 0)
                carrier.Order = transport with { Delivering = false, WaitUntil = Time + LoadingTime };
        }
        else if (mine.Ore >= 1)
        {
            // It takes what the mine has ready, up to its capacity, and is off with it; at an empty mine it waits
            // (MachLogPickUpOperation).
            var load = (int)MathF.Min(capacity - carrier.Ore, mine.Ore);
            carrier.Ore += load;
            mine.Ore -= load;
            if (carrier.Ore > 0)
                carrier.Order = transport with { Delivering = true, WaitUntil = Time + LoadingTime };
        }
        carrier.Path.Clear();
        carrier.PathIndex = 0;
    }

    /// <summary>Seconds a carrier stands on a pad loading or dropping ore: 22 frames at 22 a second (MachPhysResourceLoading).</summary>
    public const float LoadingTime = 1;

    // The pad each carrier is heading for, and at which building.
    private readonly Dictionary<int, (int Stop, Vector2 Pad)> _carrierPads = [];

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


    private void UpdateEnterLab(Machine technician, EnterLabOrder enter)
    {
        var lab = Find<Construction>(enter.Lab);
        if (lab is null || !lab.Complete)
        {
            technician.Order = Order.Idle;
            return;
        }

        if (lab.Technicians.Count + lab.Garrisoned.Count >= ResearchStations(lab))
        {
            technician.Order = Order.Idle;
            return;
        }
        if (!ThroughDoor(technician, lab))
            return;

        technician.Doorway = null;
        technician.Inside = lab.Id;
        technician.Position = lab.Position;
        technician.Order = Order.Idle;
        // A spy in another race's lab waits there, downloading, until it is ordered out.
        (technician.IsSpy && lab.Race != technician.Race ? lab.Garrisoned : lab.Technicians).Add(technician.Id);
    }

    /// <summary>How many technicians fit in a lab: its <c>STATION ... RESEARCH</c> places in parmdata.</summary>
    public static int ResearchStations(Construction lab) =>
        Math.Max(1, lab.Stats.Geometry.Count(g => g.Keyword == "STATION" && g.Args[^1] == "RESEARCH"));

    /// <summary>
    /// The deposits a locator sent to <paramref name="target"/> drives over on the way (MachLogLocateOperation): those
    /// its race has not found within scanner range of the straight way there (or of where it stands, when it is
    /// there already) on ground it can reach, in the planet's order of sites.
    /// </summary>
    private List<int> SitesOnTheWay(Machine locator, Vector2 target)
    {
        var range = locator.Stats.ScannerRange;
        var water = CrossesWater(locator);
        return Sites.Where(s => !s.DiscoveredBy.Contains(locator.Race) && Nav.IsFree(s.Position, water)
                && DistanceToSegment(s.Position, locator.Position, target) < range)
            .Select(s => s.Id)
            .ToList();
    }

    private static float DistanceToSegment(Vector2 point, Vector2 from, Vector2 to)
    {
        var along = to - from;
        var length = along.LengthSquared();
        var t = length < 1e-4f ? 0 : Math.Clamp(Vector2.Dot(point - from, along) / length, 0, 1);
        return Vector2.Distance(point, from + along * t);
    }

    /// <summary>
    /// A locator drives onto each deposit on its list and finds it only there, within a metre of it, then goes on to
    /// its destination (MachLogLocateOperation::doUpdate); a deposit it cannot reach is passed over.
    /// </summary>
    private void UpdateLocate(Machine locator, LocateOrder locate)
    {
        var next = locate.Sites.FirstOrDefault(id => !Sites[id].DiscoveredBy.Contains(locator.Race), -1);
        if (next >= 0 && Vector2.DistanceSquared(locator.Position, Sites[next].Position) < 1)
        {
            DiscoverSite(Sites[next], locator);
            next = -1;
        }
        if (next < 0 && locate.Sites.Count > 0 || next >= 0 && !FollowPath(locator))
        {
            // On to the next deposit (passing over one it could not get to), or to the destination.
            var rest = locate.Sites.Where(id => id != next && !Sites[id].DiscoveredBy.Contains(locator.Race)).ToList();
            Send(locator, locate with { Sites = rest }, rest.Count > 0 ? Sites[rest[0]].Position : locate.Target);
            return;
        }
        if (next < 0 && !FollowPath(locator))
            locator.Order = Order.Idle;
    }

    private void DiscoverSite(MineralSiteState site, Machine locator)
    {
        site.DiscoveredBy.Add(locator.Race);
        Raise(new GameEvent(GameEventKind.SiteDiscovered, locator.Id, site.Position, site.Id, locator.Race.ToString()));
    }

    /// <summary>A point just outside a building, on the side facing the machine.</summary>
    private Vector2 ClosestPointOutside(Construction building, Vector2 from)
    {
        var (min, max) = building.Footprint;
        var clamped = Vector2.Clamp(from, min, max);
        var direction = clamped == from ? Vector2.Normalize(from - building.Position + new Vector2(0.01f, 0)) : Vector2.Normalize(from - clamped);
        return clamped + direction * (InteractionDistance * 0.5f);
    }
}
