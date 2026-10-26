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
        var direction = new Vector2(MathF.Cos(machine.Facing), MathF.Sin(machine.Facing));
        var next = machine.Position + direction * (input.Forward * machine.Stats.Speed * Dt);
        if (Nav.IsFree(next, CrossesWater(machine)))
            machine.Position = next;
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
            case MoveOrder:
                if (!FollowPath(machine))
                    machine.Order = Order.Idle;
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
                if (Tick % TicksPerSecond == machine.Id % TicksPerSecond)
                    DiscoverSites(machine);
                if (!FollowPath(machine))
                {
                    DiscoverSites(machine);
                    machine.Order = Order.Idle;
                }
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

            case IdleOrder when machine.CanHeal && !machine.DrivenByPlayer:
                SeekPatients(machine);
                break;

            default:
                // Idle fighters engage whatever comes into scanner range.
                if (machine.CanFight && !machine.DrivenByPlayer && Tick % 10 == machine.Id % 10
                    && NearestEnemy(machine, machine.Stats.ScannerRange) is { } threat)
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
            machine.Facing = MathF.Atan2(direction.Y, direction.X);
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
            machine.Facing = MathF.Atan2(d.Y, d.X);
    }

    // ---- fighting -------------------------------------------------------------------------------

    private void UpdateAttack(Machine machine, AttackOrder attack)
    {
        var target = Find(attack.Target);
        if (target is null || target.IsDead || !AreEnemies(machine.Race, target.Race))
        {
            if (attack.Then is { } then)
                Send(machine, new AttackMoveOrder(then), then);
            else
                machine.Order = Order.Idle;
            return;
        }

        var range = machine.Weapons.Where(w => w.DealsDamage || w.IsTreachery).Select(w => w.Spec.Range).DefaultIfEmpty(0).Min();
        if (machine.DistanceTo(target) <= range * 0.95f)
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            Face(machine, target.Position);
        }
        else if (!machine.DrivenByPlayer)
        {
            Approach(machine, target.Position);
        }
    }

    /// <summary>The closest enemy within range, preferring armed machines.</summary>
    public Actor? NearestEnemy(Actor from, float range) => Actors
        .Where(a => !a.IsDead && AreEnemies(from.Race, a.Race) && a is not Machine { Inside: not null })
        .Select(a => (Actor: a, Distance: from.DistanceTo(a)))
        .Where(x => x.Distance <= range)
        .OrderBy(x => x.Actor is Machine { CanFight: true } ? 0 : 1)
        .ThenBy(x => x.Distance)
        .Select(x => x.Actor)
        .FirstOrDefault();

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

        var capacity = carrier.Stats.Capacity ?? 0;
        var delivering = carrier.Ore >= capacity || (carrier.Ore > 0 && mine.Ore < 1);
        var stop = delivering ? smelter : mine;

        if (!InReach(carrier, stop))
        {
            Approach(carrier, ClosestPointOutside(stop, carrier.Position));
            return;
        }

        if (!stop.Complete)
            return;

        if (delivering)
        {
            // Ore becomes BMUs at once; whatever does not fit in storage stays on board.
            var added = AddBmu(carrier.Race, carrier.Ore);
            carrier.Ore -= (int)added;
        }
        else
        {
            var load = (int)MathF.Min(capacity - carrier.Ore, mine.Ore);
            carrier.Ore += load;
            mine.Ore -= load;
        }
        carrier.Path.Clear();
        carrier.PathIndex = 0;
    }

    private void UpdateEnterLab(Machine technician, EnterLabOrder enter)
    {
        var lab = Find<Construction>(enter.Lab);
        if (lab is null || !lab.Complete)
        {
            technician.Order = Order.Idle;
            return;
        }

        if (!InReach(technician, lab))
        {
            Approach(technician, ClosestPointOutside(lab, technician.Position));
            return;
        }

        if (lab.Technicians.Count >= ResearchStations(lab))
        {
            technician.Order = Order.Idle;
            return;
        }

        technician.Inside = lab.Id;
        technician.Position = lab.Position;
        technician.Order = Order.Idle;
        lab.Technicians.Add(technician.Id);
    }

    /// <summary>How many technicians fit in a lab: its <c>STATION ... RESEARCH</c> places in parmdata.</summary>
    public static int ResearchStations(Construction lab) =>
        Math.Max(1, lab.Stats.Geometry.Count(g => g.Keyword == "STATION" && g.Args[^1] == "RESEARCH"));

    private void DiscoverSites(Machine locator)
    {
        foreach (var site in Sites)
        {
            if (!site.DiscoveredBy.Contains(locator.Race)
                && Vector2.Distance(site.Position, locator.Position) <= locator.Stats.ScannerRange)
            {
                site.DiscoveredBy.Add(locator.Race);
                Raise(new GameEvent(GameEventKind.SiteDiscovered, locator.Id, site.Position, site.Id, locator.Race.ToString()));
            }
        }
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
