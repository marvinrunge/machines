using System.Numerics;
using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// How machines drive with <see cref="MatchOptions.OriginalMotion"/>, after the original's motion sequencer
/// (MachLogMachineMotionSequencer, MachPhysMachine, PhysRampAcceleration). A machine at rest first turns on the spot
/// to its way, ramped up to its ROTATION_SPEED; then it speeds up at its ACCELERATION to its SPEED and slows to a
/// stop at the next rest point: the goal, a corner sharper than 45° or the sixth stretch of way. Through a gentler
/// corner it drives on at speed, turning over its last two metres before it. Where the original booked every stretch
/// in space and time, the remake checks each tick's step against the other machines, and a machine that would run
/// into one stops dead and reacts as the original did: it swerves, asks the other to make way, steps aside, picks
/// another place or waits.
/// </summary>
public sealed partial class GameWorld
{
    // A corner sharper than this is a rest point (MachLogMachineMotionSequencer's handbrakeTurnLimit).
    private const float SharpCorner = 45 * MathF.PI / 180;

    // At a gentler corner the machine turns to the new way over its last metres before it (START_ROTATION_DISTANCE).
    private const float StartRotationDistance = 2;

    // A run between rest points is at most this many stretches of way (MachLogMessageBroker::maximumMotionChunks).
    private const int MostChunks = 6;

    // Touching counts as running into: clearances are tested with this to spare.
    private const float Touch = 1e-4f;

    // A machine held up by another tries again after this long (MachLogMachineMotionSequencer::initiateMove).
    private const float BlockedRetry = 2;

    // The player's machines first try again this much later (MachLogMachineMotionSequencer::reserveChunk).
    private const float PlayerRetry = 0.5f;

    // How long a machine makes no headway before it plans a new way (the remake's 3 s are too short among waiting machines).
    private const float StuckWaiting = 10;

    // The time a machine asked to make way is given before the asker tries again.
    private const float MakeWayWait = 1.5f;

    // How far ahead a machine looks for one driven in first person (MachLog1stPersonHandler::preventImpendingCollisions).
    private const float DrivenLookAhead = 0.5f;

    // A machine facing a target or a building turns to it once it is further off than this.
    private const float FaceWithin = 0.05f;

    // A worker faces the building it works on to within this (MachLogLabourOperation).
    private const float FaceBuildingWithin = 0.3f;

    /// <summary>
    /// PhysRampAcceleration: from a starting speed up to a top speed at a rate, on at the top speed and down to a stop
    /// at the same rate, over a given length. A stretch too short to stop on from the starting speed doubles the rate
    /// until it is long enough, as the original did.
    /// </summary>
    private readonly struct Ramp
    {
        private readonly float _start, _peak, _rate, _up, _cruise, _upLength, _cruiseLength;

        public float Length { get; }
        public float Duration { get; }

        public Ramp(float length, float start, float top, float rate)
        {
            Length = MathF.Max(0, length);
            top = MathF.Max(top, 1e-3f);
            _start = Math.Clamp(start, 0, top);
            if (rate <= 0 || Length == 0)
            {
                // Without an acceleration it goes at its top speed at once.
                _peak = top;
                _cruiseLength = Length;
                _cruise = Length / top;
                Duration = _cruise;
                return;
            }
            while (_start * _start / (2 * rate) > Length)
                rate *= 2;
            _rate = rate;
            _peak = MathF.Max(_start, MathF.Min(top, MathF.Sqrt(rate * Length + _start * _start / 2)));
            _up = (_peak - _start) / rate;
            _upLength = (_peak * _peak - _start * _start) / (2 * rate);
            _cruiseLength = MathF.Max(0, Length - _upLength - _peak * _peak / (2 * rate));
            _cruise = _cruiseLength / _peak;
            Duration = _up + _cruise + _peak / rate;
        }

        /// <summary>The way covered after <paramref name="t"/> seconds.</summary>
        public float Covered(float t)
        {
            if (t <= 0)
                return 0;
            if (t >= Duration)
                return Length;
            if (t < _up)
                return _start * t + _rate * t * t / 2;
            t -= _up;
            if (t < _cruise)
                return _upLength + _peak * t;
            t -= _cruise;
            return MathF.Min(Length, _upLength + _cruiseLength + _peak * t - _rate * t * t / 2);
        }

        /// <summary>The speed after <paramref name="t"/> seconds.</summary>
        public float SpeedAt(float t)
        {
            if (t >= Duration)
                return 0;
            if (t < _up)
                return _start + _rate * MathF.Max(0, t);
            return t < _up + _cruise ? _peak : MathF.Max(0, _peak - _rate * (t - _up - _cruise));
        }
    }

    /// <summary>How a machine is moving: its run of way between rest points and its turn on the spot.</summary>
    private sealed class Motion
    {
        public float Speed;
        // Sideways, driven in first person (positive to the left).
        public float Side;
        public bool Driving;
        public Ramp Run;
        public float RunStart, RunCovered;
        public Vector2 RunGoal;
        public bool Turning;
        public Ramp Turn;
        public float TurnStart, TurnFrom, TurnBy, TurnTo;
        // The last tick it drove or turned; a machine that did neither is at rest.
        public long MovedTick = -1;
        public float WaitUntil;
        // Times it was held up by a moving machine since it last got going, and the share of its speed it tries.
        public int Tries;
        public float SpeedShare = 1;
        // Moved off its way (stepping sideways or aside, a new goal): it plans the way again if it cannot see on.
        public bool OffWay;
        // Times it stepped sideways for its goal, and that goal.
        public int Sidesteps;
        public Vector2 SidestepGoal;
    }

    // Stepping sideways for a goal that stays taken would go on for ever: after this many it looks for space near by.
    private const int MostSidesteps = 2;

    private readonly Dictionary<int, Motion> _motion = [];

    private Motion MotionOf(Machine machine)
    {
        if (!_motion.TryGetValue(machine.Id, out var motion))
            _motion[machine.Id] = motion = new Motion();
        return motion;
    }

    /// <summary>How a machine's way may be changed when another is in it.</summary>
    private enum Way
    {
        /// <summary>Its order's path: it may swerve, step sideways or pick another place.</summary>
        Order,
        /// <summary>Straight at a door or a pad: it only asks the other to make way, or waits.</summary>
        Straight,
        /// <summary>Making way for another: it gives up when held up.</summary>
        Aside,
    }

    // Machines asked to make way: the point each steps aside to before it goes on with its order (moveOutOfWay).
    private readonly Dictionary<int, List<Vector2>> _aside = [];

    // The one-point way of a machine walking straight at a door or a pad.
    private readonly List<Vector2> _straightWay = [];

    /// <summary>
    /// Whether a machine stands at rest: neither moving nor about to, with a way to go and nothing to wait for (the
    /// original's resting obstacle).
    /// </summary>
    private bool IsResting(Machine machine) =>
        !IsMoving(machine) && !(machine.PathIndex < machine.Path.Count && !machine.DrivenByPlayer
            && (!_motion.TryGetValue(machine.Id, out var motion) || Time >= motion.WaitUntil));

    /// <summary>Whether a machine drove or turned during the last tick (or this one, once it has).</summary>
    private bool IsMoving(Machine machine) =>
        machine.DrivenByPlayer
            ? DriveInput.TryGetValue(machine.Id, out var input) && (input.Forward != 0 || input.Turn != 0 || input.Strafe != 0)
                || _motion.TryGetValue(machine.Id, out var driven) && (driven.Speed != 0 || driven.Side != 0)
            : _motion.TryGetValue(machine.Id, out var motion) && motion.MovedTick >= Tick - 1 && (motion.Driving || motion.Turning);

    /// <summary>
    /// Drives a machine one tick along a way from <paramref name="index"/>; returns the index of the next point it is
    /// heading for, the way's length once it is through.
    /// </summary>
    private int Travel(Machine machine, List<Vector2> path, int index, Way way)
    {
        var motion = MotionOf(machine);
        // Points it stands on already count as reached.
        while (index < path.Count && Vector2.DistanceSquared(machine.Position, path[index]) < 1e-6f)
            index++;
        if (index >= path.Count)
            return index;
        if (Time < motion.WaitUntil)
            return index;

        var end = motion.Driving ? path.IndexOf(motion.RunGoal, index) : -1;
        if (end < 0)
        {
            if (!PlanRun(machine, motion, path, ref index, way))
                return index;
            end = path.IndexOf(motion.RunGoal, index);
        }
        if (motion.Turning)
        {
            // Turning on the spot counts as headway: it is not stuck.
            _progress[machine.Id] = (machine.Position, Time);
            if (TurnOnTheSpot(machine, motion))
                motion.RunStart = Time + Dt;
            return index;
        }
        motion.MovedTick = Tick;

        // Along the way by what the ramp covers this tick.
        var t = Time + Dt - motion.RunStart;
        var covered = motion.Run.Covered(t);
        var left = covered - motion.RunCovered;
        var position = machine.Position;
        var next = index;
        while (left > 1e-6f && next <= end)
        {
            var offset = path[next] - position;
            var length = offset.Length();
            if (length <= left)
            {
                position = path[next];
                left -= length;
                next++;
            }
            else
            {
                position += offset * (left / length);
                left = 0;
            }
        }
        if (t >= motion.Run.Duration)
        {
            position = path[end];
            next = end + 1;
        }

        if (Blocker(machine, machine.Position, position) is { } blocker)
            return HeldUp(machine, motion, blocker, path, index, way);

        // It faces along its way, and over the last metres before a gentle corner turns to the next stretch.
        var heading = position != machine.Position
            ? DetMath.Atan2(position.Y - machine.Position.Y, position.X - machine.Position.X)
            : machine.Facing;
        if (next <= end)
        {
            var ahead = path[next] - position;
            if (ahead.LengthSquared() > 1e-6f)
                heading = DetMath.Atan2(ahead.Y, ahead.X);
            if (next < end && ahead.Length() < StartRotationDistance)
            {
                var after = path[next + 1] - path[next];
                var turnTo = DetMath.Atan2(after.Y, after.X);
                heading += AngleBetween(heading, turnTo) * (1 - ahead.Length() / StartRotationDistance);
            }
        }
        machine.Position = position;
        machine.Facing = heading;
        motion.RunCovered = covered;
        motion.Speed = motion.Run.SpeedAt(t);
        if (next > end)
        {
            // At a rest point.
            motion.Driving = false;
            motion.Speed = 0;
            motion.Tries = 0;
            motion.SpeedShare = 1;
        }
        return next;
    }

    /// <summary>
    /// Plans the run from where the machine is to its next rest point; a machine at rest that faces elsewhere turns to
    /// its way first. Before it sets off, a machine resting in the way is swerved round (trySwerveMove), or driven up
    /// to (tryNearerPoint). False when it cannot set off this tick.
    /// </summary>
    private bool PlanRun(Machine machine, Motion motion, List<Vector2> path, ref int index, Way way)
    {
        var water = CrossesWater(machine);
        // A machine moved off its way (stepping sideways or aside, a new goal) plans it again when it cannot see the
        // next point (MachLogMachineMotionSequencer's movedOffPath).
        var offWay = motion.OffWay;
        motion.OffWay = false;
        if (offWay && way == Way.Order && ReferenceEquals(path, machine.Path) && Nav.IsFree(machine.Position, water)
            && !Nav.LineOfSight(machine.Position, path[index], water))
        {
            var goal = path[^1];
            path.Clear();
            path.AddRange(Nav.FindPath(machine.Position, goal, water));
            index = 0;
            machine.PathIndex = 0;
            if (path.Count == 0)
                return false;
        }

        if (way == Way.Order && motion.Speed == 0 && RestingBlocker(machine, machine.Position, path[index]) is { } resting)
        {
            if (Swerve(machine, machine.Position, path[index], resting, 0) is { } swerve)
                path.Insert(index, swerve);
            else if (NearerPoint(machine, machine.Position, path[index], resting) is { } nearer)
                path.Insert(index, nearer);
        }

        var from = machine.Position;
        var first = path[index] - from;
        var heading = DetMath.Atan2(first.Y, first.X);
        var start = motion.Speed;
        // A new way that turns sharply off the one it was on: it stops and turns.
        if (start > 0 && MathF.Abs(AngleBetween(machine.Facing, heading)) > SharpCorner)
            start = 0;

        var end = index;
        var length = first.Length();
        for (var k = index; k < path.Count - 1 && k - index + 1 < MostChunks; k++)
        {
            var inbound = path[k] - (k == index ? from : path[k - 1]);
            var outbound = path[k + 1] - path[k];
            if (MathF.Abs(AngleBetween(DetMath.Atan2(inbound.Y, inbound.X), DetMath.Atan2(outbound.Y, outbound.X))) > SharpCorner)
                break;
            length += outbound.Length();
            end = k + 1;
        }

        var top = machine.Stats.Speed * motion.SpeedShare * (machine.Order is LocateOrder ? 0.5f : 1);
        motion.Run = new Ramp(length, start, top, machine.Stats.Acceleration);
        motion.Driving = true;
        motion.RunGoal = path[end];
        motion.RunStart = Time;
        motion.RunCovered = 0;
        motion.Speed = start;
        motion.Turning = false;
        if (start == 0)
            StartTurn(machine, motion, heading);
        return true;
    }

    /// <summary>Starts a turn on the spot to a heading (MachPhysMachine::turn): ramped at ROTATION_ACCELERATION up to ROTATION_SPEED.</summary>
    private static void StartTurn(Machine machine, Motion motion, float heading)
    {
        var by = AngleBetween(machine.Facing, heading);
        var top = machine.Stats.RotationSpeed * MathF.PI / 180;
        if (MathF.Abs(by) < 1e-4f || top <= 0)
        {
            machine.Facing = heading;
            motion.Turning = false;
            return;
        }
        motion.Turning = true;
        motion.Turn = new Ramp(MathF.Abs(by), 0, top, machine.Stats.RotationAcceleration * MathF.PI / 180);
        motion.TurnFrom = machine.Facing;
        motion.TurnBy = by;
        motion.TurnTo = heading;
        motion.TurnStart = -1;
    }

    /// <summary>One tick of a turn on the spot; true once it faces the new heading.</summary>
    private bool TurnOnTheSpot(Machine machine, Motion motion)
    {
        motion.MovedTick = Tick;
        if (motion.TurnStart < 0)
            motion.TurnStart = Time;
        var t = Time + Dt - motion.TurnStart;
        if (t >= motion.Turn.Duration)
        {
            machine.Facing = motion.TurnTo;
            motion.Turning = false;
            return true;
        }
        machine.Facing = motion.TurnFrom + MathF.Sign(motion.TurnBy) * motion.Turn.Covered(t);
        return false;
    }

    /// <summary>Turns a standing machine to face a point, at its own turning speed (MachLogTurnAnimation).</summary>
    private void TurnToFace(Machine machine, Vector2 point, float within)
    {
        var offset = point - machine.Position;
        if (offset.LengthSquared() <= 0.01f)
            return;
        var heading = DetMath.Atan2(offset.Y, offset.X);
        var motion = MotionOf(machine);
        motion.Driving = false;
        motion.Speed = 0;
        if (motion.Turning && MathF.Abs(AngleBetween(motion.TurnTo, heading)) > within)
            motion.Turning = false;
        if (!motion.Turning)
        {
            if (MathF.Abs(AngleBetween(machine.Facing, heading)) <= within)
                return;
            StartTurn(machine, motion, heading);
            if (!motion.Turning)
                return;
        }
        TurnOnTheSpot(machine, motion);
    }

    /// <summary>A machine that neither drove nor turned this tick is at rest.</summary>
    private void Settle(Machine machine)
    {
        if (_motion.TryGetValue(machine.Id, out var motion) && motion.MovedTick != Tick)
        {
            motion.Speed = 0;
            motion.Side = 0;
            motion.Driving = false;
            motion.Turning = false;
        }
    }

    /// <summary>Stops at once, with no way to slow down (MachLogMachineMotionSequencer::stopDead).</summary>
    private static void StopDead(Motion motion)
    {
        motion.Speed = 0;
        motion.Side = 0;
        motion.Driving = false;
        motion.Turning = false;
    }

    // ---- other machines in the way ---------------------------------------------------------------

    /// <summary>
    /// The machine a step from <paramref name="from"/> to <paramref name="to"/> runs into, nearest first and the lower
    /// id on a tie. Machines it already overlaps where it stands are passed over, so those can drive apart
    /// (PhysConfigSpace2d's intersecting ids); one driven in first person is seen half a second ahead.
    /// </summary>
    private Machine? Blocker(Machine machine, Vector2 from, Vector2 to, bool restingOnly = false)
    {
        Machine? nearest = null;
        var best = float.MaxValue;
        var step = to - from;
        var stepLength = step.Length();
        var ahead = stepLength > 1e-6f ? step / stepLength * (MotionOf(machine).Speed * DrivenLookAhead) : Vector2.Zero;
        foreach (var actor in FiledNear(from, stepLength + machine.Radius + _machineReach + ahead.Length()))
        {
            if (actor is not Machine other || other == machine || other.IsDead || other.Inside is not null)
                continue;
            var clearance = machine.Radius + other.Radius + Touch;
            var already = Vector2.DistanceSquared(from, other.Position);
            if (already < clearance * clearance || restingOnly && !IsResting(other))
                continue;
            var end = other.DrivenByPlayer ? to + ahead : to;
            if (DistanceToSegment(other.Position, from, end) >= clearance)
                continue;
            if (already < best || already == best && other.Id < nearest!.Id)
            {
                best = already;
                nearest = other;
            }
        }
        return nearest;
    }

    private Machine? RestingBlocker(Machine machine, Vector2 from, Vector2 to) => Blocker(machine, from, to, restingOnly: true);

    /// <summary>
    /// What a machine does when its step runs into another (MachLogMachineMotionSequencer::initiateMove): it stops
    /// dead, then by turns tries again a little later (the player's machines, against a moving one), arrives (almost
    /// there), has the other make way, steps sideways, picks another place near its goal or waits two seconds.
    /// Returns the index of the point it heads for next.
    /// </summary>
    private int HeldUp(Machine machine, Motion motion, Machine blocker, List<Vector2> path, int index, Way way)
    {
        StopDead(motion);
        motion.MovedTick = Tick;
        if (way == Way.Aside)
            return path.Count;
        var resting = IsResting(blocker);
        // One driven in first person is never asked to make way: the other stops and waits.
        if (blocker.DrivenByPlayer)
        {
            motion.WaitUntil = Time + PlayerRetry;
            return index;
        }
        // The player's machines try once more half a second later, then at four fifths of their speed.
        if (!resting && Races[machine.Race].Controller == Controller.Player && motion.Tries < 2)
        {
            if (motion.Tries == 0)
                motion.WaitUntil = Time + PlayerRetry;
            else
                motion.SpeedShare = SlowerShare;
            motion.Tries++;
            return index;
        }

        var toward = path[index];
        if (motion.SidestepGoal != path[^1])
            (motion.SidestepGoal, motion.Sidesteps) = (path[^1], 0);
        // Almost there: it has arrived, whoever is in the way.
        if (way == Way.Order && index == path.Count - 1 && Vector2.Distance(machine.Position, toward) < MathF.Max(0.01f, machine.Stats.Speed * Dt))
            return path.Count;
        if (way == Way.Order && index == path.Count - 1 && resting && !SameMove(blocker, machine))
        {
            if (blocker.Race == machine.Race && MakeWay(blocker, machine.Position, toward, machine.Radius))
            {
                motion.WaitUntil = Time + MakeWayWait;
                return index;
            }
            if (motion.Sidesteps < MostSidesteps && StepSideways(machine, path, index))
            {
                motion.Sidesteps++;
                return index;
            }
            // Somewhere else near its goal, or it stops where it is (findSpace).
            if (!FreeSpace(toward, toward - machine.Position, 0, machine.Radius, out var space, asker: machine))
                return path.Count;
            path[^1] = space;
            motion.OffWay = true;
            return index;
        }

        if (resting && MakeWay(blocker, machine.Position, toward, machine.Radius))
        {
            motion.WaitUntil = Time + MakeWayWait;
            return index;
        }
        if (!SameMove(blocker, machine) && way == Way.Order && motion.Sidesteps < MostSidesteps && StepSideways(machine, path, index))
        {
            motion.Sidesteps++;
            return index;
        }
        motion.WaitUntil = Time + BlockedRetry;
        return index;
    }

    // The share of its speed a player's machine tries when a moving machine is in the way (speedProportion).
    /// <summary>
    /// Whether the blocker drives on under the same command (the same CommandId): one of the same group that will
    /// clear the way. One that has got where it was going, or waits itself, counts as any other.
    /// </summary>
    private bool SameMove(Machine blocker, Machine machine) =>
        blocker.CommandId == machine.CommandId && blocker.PathIndex < blocker.Path.Count && IsMoving(blocker);

    private const float SlowerShare = 0.8f;

    /// <summary>
    /// Asks a standing machine to make way for one going from <paramref name="from"/> to <paramref name="to"/>
    /// (MachLogMachineMotionSequencer::moveOutOfWay): it steps twice the larger clearance across the other's way, to
    /// its own side first, else across and ahead or behind, else straight ahead. False when it cannot.
    /// </summary>
    private bool MakeWay(Machine machine, Vector2 from, Vector2 to, float askerClearance)
    {
        if (!IsResting(machine) || machine.DrivenByPlayer || machine.Inside is not null || machine.Doorway is not null
            || _aside.ContainsKey(machine.Id))
            return false;
        var direction = to - from;
        if (direction.LengthSquared() < 1e-12f)
            direction = machine.Position - from;
        if (direction.LengthSquared() < 1e-12f)
            return false;
        direction = Vector2.Normalize(direction);
        var normal = new Vector2(direction.Y, -direction.X);
        var side = Vector2.Dot(machine.Position - from, normal) >= 0 ? 1 : -1;
        var distance = 2 * MathF.Max(machine.Radius, askerClearance);
        var along = direction * distance;
        var across = normal * (distance * side);
        var position = machine.Position;
        foreach (var point in (ReadOnlySpan<Vector2>)[position + across, position - across, position + across + along,
                     position - across + along, position + across - along, position - across - along, position + along])
        {
            if (CanMoveTo(machine, position, point))
            {
                _aside[machine.Id] = [point];
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Steps sideways off the way to let the other by (tryMoveSideways, findAvoidancePoint): twice its clearance
    /// ahead and to the right, ahead and to the left, right, left, the way a little shaken at random.
    /// </summary>
    private bool StepSideways(Machine machine, List<Vector2> path, int index)
    {
        var position = machine.Position;
        var direction = path[index] - position;
        do
        {
            direction += new Vector2((float)(Random.NextDouble() * 2 - 1), (float)(Random.NextDouble() * 2 - 1));
        } while (direction == Vector2.Zero);
        var along = Vector2.Normalize(direction) * (machine.Radius * 2);
        var normal = new Vector2(along.Y, -along.X);
        foreach (var point in (ReadOnlySpan<Vector2>)[position + along + normal, position + along - normal, position + normal, position - normal])
        {
            if (CanMoveTo(machine, position, point))
            {
                path.Insert(index, point);
                MotionOf(machine).OffWay = true;
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// A way round a machine resting in the way (MachLogMachineMotionSequencer::trySwerveMove): across from where it
    /// would touch, one and a half times both clearances to the right, else the left, if the machine can get there
    /// and on from there; up to three swerves deep.
    /// </summary>
    private Vector2? Swerve(Machine machine, Vector2 from, Vector2 to, Machine resting, int depth)
    {
        if (depth >= 3)
            return null;
        var water = CrossesWater(machine);
        var travel = to - from;
        if (travel.LengthSquared() < 1e-6f)
            return null;
        var direction = Vector2.Normalize(travel);
        // The original kept 5 m from anything that was not one of its own race's machines.
        var obstructing = resting.Race == machine.Race ? resting.Radius : 5;
        var normal = new Vector2(direction.Y, -direction.X) * (1.5f * (obstructing + machine.Radius));
        var hit = ContactPoint(from, direction, resting.Position, machine.Radius + resting.Radius);
        foreach (var point in (ReadOnlySpan<Vector2>)[hit + normal, hit - normal])
        {
            if (!Nav.OnPlanet(point) || !Nav.IsFree(point, water) || !Nav.LineOfSight(from, point, water) || !Nav.LineOfSight(point, to, water))
                continue;
            if (RestingBlocker(machine, point, to) is not null)
                continue;
            if (RestingBlocker(machine, from, point) is { } next)
            {
                if (Swerve(machine, from, point, next, depth + 1) is null)
                    continue;
            }
            return point;
        }
        return null;
    }

    /// <summary>
    /// Short of a machine resting in the way, when it is further off than three clearances: a point two clearances
    /// before it (MachLogMachineMotionSequencer::tryNearerPoint).
    /// </summary>
    private static Vector2? NearerPoint(Machine machine, Vector2 from, Vector2 to, Machine resting)
    {
        var travel = to - from;
        if (travel.LengthSquared() < 1e-6f)
            return null;
        var direction = Vector2.Normalize(travel);
        var hit = ContactPoint(from, direction, resting.Position, machine.Radius + resting.Radius);
        var clearance = machine.Radius;
        return Vector2.Distance(from, hit) > 3 * clearance ? hit - direction * (2 * clearance) : null;
    }

    /// <summary>Where a machine going from <paramref name="from"/> along <paramref name="direction"/> first touches a circle.</summary>
    private static Vector2 ContactPoint(Vector2 from, Vector2 direction, Vector2 centre, float radius)
    {
        var along = Vector2.Dot(centre - from, direction);
        var square = radius * radius - (Vector2.DistanceSquared(centre, from) - along * along);
        return from + direction * MathF.Max(0, along - MathF.Sqrt(MathF.Max(0, square)));
    }

    /// <summary>Whether a machine can drive straight to a point: open ground in sight, and nobody in the way.</summary>
    private bool CanMoveTo(Machine machine, Vector2 from, Vector2 to)
    {
        var water = CrossesWater(machine);
        return Nav.OnPlanet(to) && Nav.IsFree(to, water) && Nav.LineOfSight(from, to, water) && Blocker(machine, from, to) is null;
    }

    /// <summary>A machine asked to make way steps aside before it goes on with its order; true while it does.</summary>
    private bool StepAside(Machine machine)
    {
        if (!_aside.TryGetValue(machine.Id, out var aside))
            return false;
        if (machine.DrivenByPlayer || Travel(machine, aside, 0, Way.Aside) >= aside.Count)
        {
            _aside.Remove(machine.Id);
            MotionOf(machine).OffWay = true;
        }
        return true;
    }

    // ---- groups ----------------------------------------------------------------------------------

    /// <summary>
    /// Moves the corners of a group member's way to its own place in the group (MachLogMachineMotionSequencer::
    /// calculateGroupMovePortalPoint). Its offset from the clump's centroid is kept as an angle and a distance from its
    /// first way (MachLogMachineMotionSequencerData::calculateOffset); at each corner the group's centre stands off the
    /// corner by the clump's radius if there is room, and the member's place is the offset turned with the mean of the
    /// way in and the way out. A place that is blocked, or out of sight of the points either side, leaves the corner
    /// as it was. The last point, the member's own place round the target, stays.
    /// </summary>
    private void KeepPlace(Machine machine, List<Vector2> path, Vector2 offset, float radius)
    {
        if (path.Count < 2)
            return;
        var water = CrossesWater(machine);
        var first = path[0] - machine.Position;
        if (first.LengthSquared() < 1e-6f)
            return;
        var angle = offset.LengthSquared() < 1e-12f ? 0 : DetMath.Atan2(first.X * offset.Y - first.Y * offset.X, Vector2.Dot(first, offset));
        var distance = offset.Length();
        var previous = machine.Position;
        for (var k = 0; k < path.Count - 1; k++)
        {
            var corner = path[k];
            var next = path[k + 1];
            var inbound = Normal(corner - previous);
            var outbound = Normal(next - corner);
            // The group's centre stands off the corner, out from the turn, by the clump's radius if it fits there.
            var centre = corner;
            var outward = inbound - outbound;
            if (radius > 0 && outward.LengthSquared() > 1e-6f)
            {
                var standOff = corner + Vector2.Normalize(outward) * radius;
                if (SpaceFree(standOff, radius, water) && Nav.LineOfSight(previous, standOff, water) && Nav.LineOfSight(standOff, next, water))
                    centre = standOff;
            }
            var mean = Normal(Normal(centre - previous) + Normal(next - centre));
            var (sin, cos) = (DetMath.Sin(angle), DetMath.Cos(angle));
            var place = centre + new Vector2(mean.X * cos - mean.Y * sin, mean.X * sin + mean.Y * cos) * distance;
            if (SpaceFree(place, machine.Radius, water) && Nav.LineOfSight(previous, place, water) && Nav.LineOfSight(place, next, water))
                path[k] = place;
            previous = path[k];
        }

        static Vector2 Normal(Vector2 v) => v.LengthSquared() > 1e-12f ? Vector2.Normalize(v) : Vector2.Zero;
    }

    /// <summary>Whether a circle is open ground: its middle and eight points round its edge.</summary>
    private bool SpaceFree(Vector2 centre, float radius, bool water)
    {
        if (!Nav.OnPlanet(centre) || !Nav.IsFree(centre, water))
            return false;
        for (var i = 0; i < 8; i++)
        {
            var edge = centre + new Vector2(DetMath.Cos(i * MathF.PI / 4), DetMath.Sin(i * MathF.PI / 4)) * radius;
            if (!Nav.OnPlanet(edge) || !Nav.IsFree(edge, water))
                return false;
        }
        return true;
    }

    // ---- first person ----------------------------------------------------------------------------

    // Steeper than this (the sine of the slope ahead), a driven machine goes a fifth slower uphill and a fifth faster
    // downhill (MachPhys1stPersonMachineDriver::nextPosition).
    private const float SteepSlope = 0.1f;

    /// <summary>
    /// First-person driving as the original's driver did it (MachPhys1stPersonMachineDriver::nextPosition): it turns at
    /// its ROTATION_SPEED, capped at the first-person rate (the slow one with Ctrl); it speeds up and slows down at its
    /// ACCELERATION, coasts to a halt when let go, goes a quarter of its SPEED in reverse, and a fifth slower up a slope
    /// steeper than one in ten (a fifth faster down). Sideways, on legs, hover or glider, it does the same at full
    /// speed (the remake's addition; both at once go no faster than one). Running into another machine it stops dead.
    /// </summary>
    private void DriveAsTheOriginal(Machine machine, (float Forward, float Turn, float Strafe, bool Slow) input)
    {
        var motion = MotionOf(machine);
        var general = Data.Parm.General;
        var limit = (input.Slow ? general.FirstPersonMaxSlowTurnRate : general.FirstPersonMaxFastTurnRate) * MathF.PI / 180;
        machine.Facing += input.Turn * MathF.Min(machine.Stats.RotationSpeed * MathF.PI / 180, limit) * Dt;

        var ahead = new Vector2(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing));
        var left = new Vector2(-ahead.Y, ahead.X);
        var share = MathF.Max(1, MathF.Sqrt(input.Forward * input.Forward + input.Strafe * input.Strafe));
        var forward = DrivenRamp(ref motion.Speed, input.Forward / share, Slope(machine, ahead), machine.Stats, reverse: 0.25f);
        var sideways = DrivenRamp(ref motion.Side, input.Strafe / share, Slope(machine, left), machine.Stats, reverse: 1);
        if (input.Turn != 0 || motion.Speed != 0 || motion.Side != 0)
            motion.MovedTick = Tick;

        var step = ahead * forward + left * sideways;
        if (step == Vector2.Zero)
            return;
        if (Blocker(machine, machine.Position, machine.Position + step) is not null || !SlideOn(machine, step))
            StopDead(motion);
    }

    /// <summary>
    /// One tick of a driven machine's speed along one direction (PhysRampAcceleration::distanceAtTime): towards the
    /// speed the input asks for at its ACCELERATION; returns the way covered.
    /// </summary>
    private static float DrivenRamp(ref float speed, float input, float slope, MachineLevel stats, float reverse)
    {
        var gradient = slope > SteepSlope ? (input > 0 ? 0.8f : 1.2f) : slope < -SteepSlope ? (input > 0 ? 1.2f : 0.8f) : 1;
        var acceleration = stats.Acceleration;
        var top = stats.Speed * gradient * MathF.Abs(input);
        if (input < 0)
        {
            acceleration = -acceleration;
            top *= -reverse;
        }
        else if (input == 0)
        {
            // Let go, it comes to rest.
            if (speed > 0)
                acceleration = -acceleration;
            top = 0;
        }
        if (acceleration == 0)
        {
            speed = top;
            return top * Dt;
        }
        var reaching = (top - speed) / acceleration;
        float distance, end;
        if (reaching > Dt)
        {
            distance = (speed + acceleration * Dt / 2) * Dt;
            end = speed + acceleration * Dt;
        }
        else
        {
            end = top;
            distance = Dt * top - reaching * (top - speed) / 2;
        }
        speed = MathF.Abs(end) < 1e-6f ? 0 : end;
        return distance;
    }

    /// <summary>The sine of the ground's slope along a direction under a machine, uphill positive; 0 without the ground.</summary>
    private float Slope(Machine machine, Vector2 direction)
    {
        if (Terrain is not { } ground)
            return 0;
        var reach = machine.Radius;
        var rise = ground.At(machine.Position + direction * reach) - ground.At(machine.Position - direction * reach);
        return rise / MathF.Sqrt(rise * rise + 4 * reach * reach);
    }

    /// <summary>The signed turn from one heading to another, the short way round.</summary>
    private static float AngleBetween(float from, float to)
    {
        var by = (to - from) % MathF.Tau;
        if (by > MathF.PI)
            by -= MathF.Tau;
        else if (by <= -MathF.PI)
            by += MathF.Tau;
        return by;
    }
}
