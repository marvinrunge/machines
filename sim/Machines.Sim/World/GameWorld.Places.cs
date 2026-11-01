using System.Numerics;

namespace Machines.Sim.World;

/// <summary>
/// Where machines stand at their work with the original's motion: the free-space search of
/// MachLogSpacialManipulation, the places attackers and healers pick round their target, and the build points
/// constructors take round a building (MachLogConstruction, MachLogLabourOperation).
/// </summary>
public sealed partial class GameWorld
{
    private enum Pattern
    {
        /// <summary>Straight out along the axis first, then either side of it in turn (getNextInCircularPattern).</summary>
        Clock,

        /// <summary>From a random point off to one side round past the axis (getNextInFlankingPattern).</summary>
        Flanking,
    }

    // getNearestFreeSpacePoint's rings lie 2.5 m apart, 20 m deep unless the caller says otherwise.
    private const float RingStep = 2.5f;
    private const float FreeSpaceDepth = 20;

    // How much further than its build point a constructor may stand (MachLogLabourOperation::proximityLeewayMultiplier).
    private const float Leeway = 0.8f;

    // Build points lie on the edge of the building's footprint, at most 14 m apart.
    private const float BuildPointSpacing = 14;

    /// <summary>
    /// MachLogSpacialManipulation::getNearestFreeSpacePoint: the first point, ring by ring outward from
    /// <paramref name="inner"/> round <paramref name="centre"/>, whose circle of <paramref name="clearance"/> is open
    /// ground clear of buildings and resting machines (the asker and its target aside); the rings start along <paramref name="axis"/>. Like the original it
    /// probes once beyond the last ring, and keeps a point it finds there while saying it found none.
    /// </summary>
    private bool FreeSpace(Vector2 centre, Vector2 axis, float inner, float clearance, out Vector2 point,
        float depth = FreeSpaceDepth, Pattern pattern = Pattern.Clock, Machine? asker = null, Actor? target = null)
    {
        var heading = axis.LengthSquared() > 1e-12f ? DetMath.Atan2(axis.Y, axis.X) : 0;
        var water = asker is not null && CrossesWater(asker);
        var found = default(Vector2);
        var radius = inner;
        while (!(pattern == Pattern.Clock ? OnClock(radius) : OnFlank(radius)) && radius < inner + depth)
            radius += RingStep;
        point = found;
        return radius < inner + depth;

        // 16 sectors: 0, +1, -1, +2, -2 ... +8.
        bool OnClock(float ring)
        {
            const int sectors = 16;
            if (Probe(0, ring, sectors))
                return true;
            int at = 0, sign = 1;
            for (var step = 1; step < sectors; step++)
            {
                at += sign * step;
                if (Probe(at, ring, sectors))
                    return true;
                sign = -sign;
            }
            return false;
        }

        // 32 sectors: either way round, from somewhere between 7 and 3 sectors back to 8 sectors on.
        bool OnFlank(float ring)
        {
            const int sectors = 32, quarter = sectors / 4;
            var sense = 1 - 2 * Random.Next(2);
            var progression = -quarter + 1 + Random.Next(5);
            var at = progression * sense;
            for (; progression < quarter; progression++)
            {
                at += sense;
                if (Probe(at, ring, sectors))
                    return true;
            }
            return false;
        }

        bool Probe(int sector, float ring, int sectors)
        {
            var angle = heading + sector * MathF.Tau / sectors;
            var candidate = centre + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * ring;
            if (!PointFree(candidate, clearance, water, asker, target))
                return false;
            found = candidate;
            return true;
        }
    }

    /// <summary>The original's free-space search in its clock pattern, for tools and tests.</summary>
    public bool FindFreeSpace(Vector2 centre, Vector2 axis, float inner, float clearance, out Vector2 point, float depth = FreeSpaceDepth) =>
        FreeSpace(centre, axis, inner, clearance, out point, depth);

    /// <summary>
    /// MachLogSpacialManipulation::pointIsFree: a circle a hair wider than the clearance clear of obstacles and of the
    /// squares resting machines take up in the original's configuration space. Moving machines do not count.
    /// </summary>
    private bool PointFree(Vector2 point, float clearance, bool water, Machine? asker, Actor? target)
    {
        var radius = clearance * 1.01f;
        if (!Nav.OnPlanet(point) || !Nav.IsGround(point, water))
            return false;
        for (var i = 0; i < 8; i++)
        {
            var edge = point + new Vector2(DetMath.Cos(i * MathF.PI / 4), DetMath.Sin(i * MathF.PI / 4)) * radius;
            if (!Nav.OnPlanet(edge) || !Nav.IsGround(edge, water))
                return false;
        }
        // Buildings by their footprints, as the navigation grid's 4 m cells would shut off the ground round them.
        foreach (var actor in FiledNear(point, radius + _spatialReach))
        {
            if (actor == asker || actor == target || actor.IsDead)
                continue;
            if (actor is Construction building)
            {
                if (Vector2.DistanceSquared(Vector2.Clamp(point, building.Footprint.Min, building.Footprint.Max), point) < radius * radius)
                    return false;
                continue;
            }
            if (actor is not Machine other || other.Inside is not null || !IsResting(other))
                continue;
            var half = new Vector2(other.Radius);
            if (Vector2.DistanceSquared(Vector2.Clamp(point, other.Position - half, other.Position + half), point) < radius * radius)
                return false;
        }
        return true;
    }

    // ---- attacking and healing ------------------------------------------------------------------

    /// <summary>The place a machine picked to work at its target from, and when.</summary>
    private sealed class Approaching
    {
        public Order? For;
        public Vector2 TargetAt;
        public float RetryAt;
    }

    private readonly Dictionary<int, Approaching> _approaching = [];

    /// <summary>A group attacker's place in its clump, kept round the corners on its way (MachLogGroupMoveInfo).</summary>
    private readonly Dictionary<int, (Order For, Vector2 Offset, float Radius)> _formation = [];

    /// <summary>
    /// MachLogAttackOperation::moveCloserToTarget and calculateNewApproachPoint: a place on a ring round the target
    /// just inside the longest weapon's reach (well inside if a machine runs away; 3 m behind one that flees), at least
    /// 7 m nearer than now and never nearer than 5 m; turreted machines from further than 40 m mostly come at it from
    /// the flank.
    /// </summary>
    private void MoveCloserToAttack(Machine machine, Actor target)
    {
        // A gorilla always wants to come within reach of its punch (MachLogAttackOperation's "special clause for
        // George"): it goes straight for the target.
        if (machine.Weapons.Any(w => w.Spec.TypeName == "GORILLA_PUNCH"))
        {
            Approach(machine, target.Position);
            return;
        }
        var reach = machine.Weapons.Where(w => w.DealsDamage || w.IsTreachery).Select(w => w.Spec.Range).DefaultIfEmpty(0).Max();
        var chasing = target is Machine { Order: EvadeOrder };
        var distance = Vector2.Distance(machine.Position, target.Position);
        if (ChooseAgain(machine, target, reach, chasing && distance < 50 ? 0.9f : 0.45f))
        {
            float desired;
            if (chasing)
                desired = 3;
            else if (distance > reach * 0.95f)
                desired = reach * 0.95f * (target is Machine runner && RunningAway(runner, machine.Position, distance) ? 0.8f : 0.97f);
            else
                desired = distance;
            desired = MathF.Max(MathF.Min(desired, distance - 7), 5);
            var pattern = machine.CanTurnUpperBody && distance > 40 && Random.Next(3) != 0 ? Pattern.Flanking : Pattern.Clock;
            GoNear(machine, target, desired, MathF.Max(distance - desired, 10), pattern);
        }
        FollowPath(machine);
    }

    /// <summary>
    /// MachLogHealOperation::moveCloserToTarget: the nearest free place from 5 m round the patient out to the healer,
    /// chosen again on the attack's rule.
    /// </summary>
    private void MoveCloserToHeal(Machine healer, Machine patient, float reach)
    {
        if (ChooseAgain(healer, patient, reach, 0.45f))
            GoNear(healer, patient, 5, Vector2.Distance(healer.Position, patient.Position) - 5, Pattern.Clock);
        FollowPath(healer);
    }

    /// <summary>
    /// Whether to pick a new place: when it has none, or when the one it has is out of reach of a target that has moved
    /// more than <paramref name="ratio"/> of the distance between them since.
    /// </summary>
    private bool ChooseAgain(Machine machine, Actor target, float reach, float ratio)
    {
        if (!_approaching.TryGetValue(machine.Id, out var approaching) || !ReferenceEquals(approaching.For, machine.Order))
        {
            _approaching[machine.Id] = new Approaching { For = machine.Order };
            return true;
        }
        if (Time < approaching.RetryAt)
            return false;
        if (machine.PathIndex >= machine.Path.Count)
            return true;
        if (Vector2.DistanceSquared(machine.Path[^1], target.Position) <= reach * reach)
            return false;
        var distance = Vector2.Distance(machine.Position, target.Position);
        return distance > 0 && Vector2.Distance(approaching.TargetAt, target.Position) / distance > ratio;
    }

    /// <summary>Heads for a free place on the rings round the target, 1.5 times its clearance wide; with none, tries again in a second.</summary>
    private void GoNear(Machine machine, Actor target, float inner, float depth, Pattern pattern)
    {
        var approaching = _approaching[machine.Id];
        approaching.TargetAt = target.Position;
        if (!FreeSpace(target.Position, machine.Position - target.Position, inner, machine.Radius * 1.5f, out var place, depth, pattern, machine, target))
        {
            approaching.RetryAt = Time + 1;
            return;
        }
        var path = Nav.FindPath(machine.Position, place, CrossesWater(machine));
        if (_formation.TryGetValue(machine.Id, out var keep) && ReferenceEquals(keep.For, machine.Order))
            KeepPlace(machine, path, keep.Offset, keep.Radius);
        machine.Path.Clear();
        machine.PathIndex = 0;
        machine.Path.AddRange(path);
    }

    /// <summary>Whether a machine will be further away in a second than now (predictedTargetOffset).</summary>
    private bool RunningAway(Machine runner, Vector2 from, float distance)
    {
        if (!_motion.TryGetValue(runner.Id, out var motion) || !IsMoving(runner))
            return false;
        var ahead = runner.Position + new Vector2(DetMath.Cos(runner.Facing), DetMath.Sin(runner.Facing)) * motion.Speed;
        return Vector2.DistanceSquared(from, ahead) > distance * distance;
    }

    // ---- build points ---------------------------------------------------------------------------

    /// <summary>A place round a building one constructor at a time works from (MachLogBuildPoint).</summary>
    private sealed class BuildPoint(Vector2 position)
    {
        public Vector2 Position { get; } = position;
        public int Holder;
        public bool ByBehemoth;
    }

    /// <summary>What a constructor at work on a building is about (MachLogLabourOperation).</summary>
    private sealed class Labour
    {
        public Order? For;
        public BuildPoint? Point;
        public bool Going, TurnOnArrival;
        public float NextTry, NextMove;
    }

    private readonly Dictionary<int, List<BuildPoint>> _buildPoints = [];
    private readonly Dictionary<int, Labour> _labour = [];

    /// <summary>
    /// MachLogLabourOperation::doUpdate: the constructor takes the nearest free build point it fits at, stands just
    /// outside it and turns to the building; true once it is near enough and facing it. With none free it waits
    /// 30 to 45 m off, and looks again every 3 s.
    /// </summary>
    private bool AtBuildPoint(Machine machine, Construction building)
    {
        if (!_labour.TryGetValue(machine.Id, out var labour) || !ReferenceEquals(labour.For, machine.Order))
            _labour[machine.Id] = labour = new Labour { For = machine.Order };
        var going = labour.Going && machine.PathIndex < machine.Path.Count;
        if (labour.Point is null && !going && Time >= labour.NextTry)
            labour.Point = TakeBuildPoint(machine, building);
        if (labour.Point is not { } point)
        {
            WaitBy(machine, building, labour);
            return false;
        }

        var clearance = machine.Radius;
        var reach = clearance * (2.6f + Leeway);
        var near = Vector2.DistanceSquared(machine.Position, point.Position) <= reach * reach;
        var toward = building.Position - machine.Position;
        if (near && MathF.Abs(AngleBetween(machine.Facing, DetMath.Atan2(toward.Y, toward.X))) < FaceBuildingWithin)
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            return true;
        }
        if (going)
        {
            FollowPath(machine);
            return false;
        }
        labour.Going = false;
        if (near)
        {
            machine.Path.Clear();
            machine.PathIndex = 0;
            Face(machine, building.Position, FaceBuildingWithin);
            return false;
        }
        if (Time < labour.NextMove)
            return false;
        // Somewhere it fits from 1.8 times its clearance out past the point; without one it lets the point go.
        if (!FreeSpace(point.Position, point.Position - building.Position, clearance * 1.8f, clearance, out var stand, clearance * Leeway, asker: machine))
        {
            point.Holder = 0;
            labour.Point = null;
            return false;
        }
        GoTo(machine, stand, labour);
        return false;
    }

    /// <summary>Without a build point it stays 30 to 45 m from the building, out of the others' way.</summary>
    private void WaitBy(Machine machine, Construction building, Labour labour)
    {
        if (labour.Going && machine.PathIndex < machine.Path.Count)
        {
            FollowPath(machine);
            return;
        }
        if (labour.Going)
        {
            labour.Going = false;
            labour.TurnOnArrival = true;
        }
        var distance = Vector2.DistanceSquared(machine.Position, building.Position);
        if (distance is > 45 * 45 or < 30 * 30)
        {
            if (Time < labour.NextMove)
                return;
            for (var radius = 35f; radius < 1000; radius += 20)
            {
                if (FreeSpace(building.Position, machine.Position - building.Position, radius, machine.Radius, out var spot, asker: machine))
                {
                    GoTo(machine, spot, labour);
                    return;
                }
            }
            return;
        }
        if (labour.TurnOnArrival)
            Face(machine, building.Position, FaceBuildingWithin);
        if (Time >= labour.NextTry)
            labour.NextTry = Time + 3;
    }

    private void GoTo(Machine machine, Vector2 place, Labour labour)
    {
        machine.Path.Clear();
        machine.PathIndex = 0;
        machine.Path.AddRange(Nav.FindPath(machine.Position, place, CrossesWater(machine)));
        labour.Going = true;
        labour.NextMove = Time + 2;
    }

    /// <summary>
    /// MachLogConstruction::bestAvailableBuildPoint: of the points nobody holds, the one nearest the constructor where
    /// it fits; a behemoth leaves a point free beside another behemoth's when there are more than four.
    /// </summary>
    private BuildPoint? TakeBuildPoint(Machine machine, Construction building)
    {
        var points = BuildPointsOf(building);
        var behemoth = machine.Spec.SubType == "BEHEMOTH";
        var clearance = machine.Radius;
        BuildPoint? best = null;
        var closest = float.MaxValue;
        foreach (var point in points)
        {
            if (Held(point) || behemoth && points.Count > 4 && BehemothBeside(points, point))
                continue;
            if (!FreeSpace(point.Position, point.Position - building.Position, clearance * 1.8f, clearance, out _, clearance * Leeway, asker: machine))
                continue;
            var distance = Vector2.DistanceSquared(machine.Position, point.Position);
            if (distance < closest)
            {
                closest = distance;
                best = point;
            }
        }
        if (best is not null)
        {
            best.Holder = machine.Id;
            best.ByBehemoth = behemoth;
        }
        return best;
    }

    /// <summary>A point is held while its holder lives and still works on the order it took it for.</summary>
    private bool Held(BuildPoint point) =>
        point.Holder != 0 && Find<Machine>(point.Holder) is { IsDead: false } holder && _labour.TryGetValue(holder.Id, out var labour)
        && labour.Point == point && ReferenceEquals(labour.For, holder.Order);

    /// <summary>MachLogConstruction::behemothsRestrict: whether a behemoth holds either of the two points nearest this one.</summary>
    private bool BehemothBeside(List<BuildPoint> points, BuildPoint candidate)
    {
        BuildPoint? best = null, second = null;
        float bestDistance = float.MaxValue, secondDistance = float.MaxValue;
        foreach (var point in points)
        {
            if (point == candidate)
                continue;
            var distance = Vector2.DistanceSquared(candidate.Position, point.Position);
            if (best is null)
            {
                (best, bestDistance) = (point, distance);
            }
            else if (distance < bestDistance)
            {
                (second, secondDistance) = (best, bestDistance);
                (best, bestDistance) = (point, distance);
            }
            else if (second is null || distance < secondDistance)
            {
                (second, secondDistance) = (point, distance);
            }
        }
        return best is not null && Held(best) && best.ByBehemoth || second is not null && Held(second) && second.ByBehemoth;
    }

    /// <summary>
    /// The build points round a building (MachLogConstruction::createBuildPoints): the edge of its footprint cut into
    /// stretches of at most 14 m, less the one point nearest its door (removeDoorPoint), unless two are as near.
    /// </summary>
    private List<BuildPoint> BuildPointsOf(Construction building)
    {
        if (_buildPoints.TryGetValue(building.Id, out var points))
            return points;
        points = [];
        var (min, max) = building.Footprint;
        var size = max - min;
        var xIntervals = (int)(size.X / BuildPointSpacing) + 1;
        var yIntervals = (int)(size.Y / BuildPointSpacing) + 1;
        for (var x = 0; x <= xIntervals; x++)
            for (var y = 0; y <= yIntervals; y++)
                if (!(x > 0 && x < xIntervals && y > 0 && y < yIntervals))
                    points.Add(new BuildPoint(min + new Vector2(x * (size.X / xIntervals), y * (size.Y / yIntervals))));
        if (building.EntranceOutside is { } door)
        {
            BuildPoint? closest = null;
            var closestDistance = float.MaxValue;
            var found = false;
            foreach (var point in points)
            {
                var distance = Vector2.DistanceSquared(point.Position, door);
                if (distance < closestDistance)
                {
                    (closest, closestDistance, found) = (point, distance, true);
                }
                else if (distance == closestDistance)
                {
                    found = false;
                }
            }
            if (found)
                points.Remove(closest!);
        }
        _buildPoints[building.Id] = points;
        return points;
    }
}
