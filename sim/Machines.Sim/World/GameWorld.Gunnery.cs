using System.Globalization;
using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Sim.World;

public sealed partial class GameWorld
{
    // ---- taking aim (MachLogCanAttack::createFireData) --------------------------------------------
    //
    // A machine with something in its sights aims all the while, charged or not, and improves on its aim for as long as
    // no more than 3.5 s pass between looks. A tower has no such patience: it aims afresh for every burst.

    /// <summary>Within this many seconds an aim goes on from the last one (MachLogCanAttack::improveAimInterval).</summary>
    private const float ImproveAimInterval = 3.5f;

    private readonly Dictionary<int, Gunnery> _gunnery = [];

    public Gunnery GunneryOf(Actor actor)
    {
        if (!_gunnery.TryGetValue(actor.Id, out var aim))
            _gunnery[actor.Id] = aim = new Gunnery(actor switch { Machine machine => machine.Stats.Accuracy ?? 100, Construction tower => tower.Stats.Accuracy ?? 100, _ => 100 });
        return aim;
    }

    /// <summary>What a hit takes from a fighting machine's aim: 40 points for all its hit points (MachLogAggressor::beHit, MachLogAdministrator::beHit).</summary>
    private void Shake(Machine machine, int hitPointsLost) => GunneryOf(machine).Shake(hitPointsLost / (float)machine.MaxHitPoints * 40);

    private void Steady(Actor shooter, Actor target)
    {
        var aim = GunneryOf(shooter);
        var interval = Time - aim.LastAimed;
        if (interval >= ImproveAimInterval)
            aim.LastRoll = MathF.Min(100, Random.NextSingle() * 100 - AimPenalty(shooter, target)) - Random.NextSingle() * 10;
        else if (aim.LastRoll < aim.Inaccuracy)
        {
            aim.LastRoll += interval / 10 * 80;
            aim.Learn(interval * 4);
        }
    }

    /// <summary>What a machine loses from an aim taken afresh in the dark and at flyers, and gains within 100 m of its squad's commander.</summary>
    private float AimPenalty(Actor shooter, Actor target)
    {
        if (shooter is not Machine machine)
            return 0;
        var penalty = PerceivedDarkness(machine) * 40;
        if (target is Machine { Stats.Locomotion: Locomotion.Glider })
            penalty += 35;
        if (machine.Spec.Type != "ADMINISTRATOR" && machine.Squad is { } squad && CommanderOf(machine.Race, squad) is { } commander
            && Vector2.DistanceSquared(machine.Position, commander.Position) <= 100 * 100)
            penalty -= 20;
        return penalty;
    }

    /// <summary>The burst from an aim: one that fell short of the inaccuracy drifts by the difference, split at random between sideways and up or down.</summary>
    private FireData TakeAim(Actor shooter, Actor target)
    {
        if (shooter is not Machine)
            Steady(shooter, target);
        var aim = GunneryOf(shooter);
        var seed = Random.NextSingle() * 0.9999f;
        if (aim.LastRoll >= aim.Inaccuracy)
            return new FireData(seed);
        var total = aim.Inaccuracy - aim.LastRoll;
        var first = Random.NextSingle() * total;
        var second = total - first;
        if (Random.Next(2) == 0)
            first = -first;
        if (Random.Next(2) == 0)
            second = -second;
        return new FireData(seed, true, first, second);
    }

    /// <summary>A VARIABLE burst has as many rounds as its seed says, from one to BURST (MachLogLinearWeapon::doFire); a flamethrower throws its balls all at once (<see cref="Spray"/>).</summary>
    private static int RoundsIn(WeaponSpec weapon, FireData data) => weapon.TypeName.StartsWith("FLAME_THROWER", StringComparison.Ordinal) ? 1
        : weapon.BurstTiming == "VARIABLE" ? (int)(data.BurstSeed * weapon.BurstRounds) + 1
        : Math.Max(1, weapon.BurstRounds);

    /// <summary>Where a moving target will be, along the way it is facing (MachActor::predictedTargetOffset).</summary>
    private Vector3 Predicted(Actor target, float seconds)
    {
        var aim = AimPoint(target);
        if (target is not Machine machine)
            return aim;
        var speed = _motion.TryGetValue(machine.Id, out var motion) ? motion.Speed : machine.PathIndex < machine.Path.Count ? machine.Stats.Speed : 0;
        return aim + new Vector3(DetMath.Cos(machine.Facing), DetMath.Sin(machine.Facing), 0) * (speed * seconds);
    }

    /// <summary>
    /// Where a flying round goes: where the target will be when it gets there, or off to one side and up or down by its
    /// burst's drift, the latter half as much and never so far down as to hit the ground at the shooter's feet
    /// (MachLogLinearWeapon::doFire). The two launchers of a leviathan drift the other way about, each its own.
    /// </summary>
    private Vector3 Aimed(Actor shooter, WeaponMount weapon, Actor target)
    {
        var (from, spec) = (AimPoint(shooter), weapon.Spec);
        var mark = Predicted(target, Vector2.Distance(shooter.Position, target.Position) / MathF.Max(1, spec.ProjectileSpeed));
        var way = mark - from;
        var distance = way.Length();
        if (!weapon.Burst.Missed || distance < 0.01f)
            return mark;
        var (sideways, upwards) = (weapon.Burst.FirstDrift / 150, MathF.Max(-0.05f, weapon.Burst.SecondDrift / 300));
        if (shooter is Construction launcher && launcher.IsType("MISSILE_EMPLACEMENT", "LAUNCHER"))
            (sideways, upwards) = weapon == launcher.Weapons[0] ? (-upwards, sideways) : (upwards, -sideways);
        var forward = way / distance;
        var left = new Vector3(-forward.Y, forward.X, 0);
        left = left.LengthSquared() < 1e-6f ? Vector3.UnitY : Vector3.Normalize(left);
        return from + Vector3.Normalize(forward + left * sideways + Vector3.Cross(forward, left) * upwards) * distance;
    }

    // ---- what can fire at what, and from where (MachLogCanAttack::canFireAt, rangeData) ---------------

    /// <summary>
    /// MachLogCanAttack::canFireAt: a SAM site (a level 4 sentry) fires only at flyers, an SSM (level 3) at anything but;
    /// neither what reaches no more than 20 m nor the Bee fires at flyers, and a tower not at a machine indoors.
    /// </summary>
    public bool CanFireAt(Actor shooter, Actor target)
    {
        var flyer = target is Machine { Stats.Locomotion: Locomotion.Glider };
        if (shooter is Construction sentry && sentry.IsType("MISSILE_EMPLACEMENT", "SENTRY"))
            return sentry.Stats.HardwareLevel == 4 == flyer;
        if (flyer)
            return MaximumWeaponRange(shooter) is 0 or > 20 && !(shooter is Machine bee && bee.IsType("ADMINISTRATOR", "BOSS") && bee.Stats.HardwareLevel == 2);
        return target is not Machine { Inside: not null } || shooter is Machine;
    }

    /// <summary>The share of a weapon's range it opens fire at, to allow for errors in working it out (MachLogCanAttack::rangeData).</summary>
    private const float FiringRangeShare = 0.95f;

    private readonly record struct WeaponAngles(float TanHorizontal, float Vertical, float MostUp, float MostDown);

    private readonly Dictionary<WeaponSpec, WeaponAngles> _weaponAngles = [];

    /// <summary>A weapon's DEVIATION_ANGLES, 10 degrees either way unless it says otherwise, and its TILT_ANGLES, none unless it says, in radians.</summary>
    private WeaponAngles AnglesOf(WeaponSpec weapon)
    {
        if (_weaponAngles.TryGetValue(weapon, out var angles))
            return angles;
        (float, float) Line(string keyword, float first, float second) =>
            weapon.Presentation.FirstOrDefault(p => p.Keyword == keyword) is { Args: [var a, var b, ..] }
                ? (float.Parse(a, CultureInfo.InvariantCulture), float.Parse(b, CultureInfo.InvariantCulture))
                : (first, second);
        const float radians = MathF.PI / 180;
        var (horizontal, vertical) = Line("DEVIATION_ANGLES", 10, 10);
        var (up, down) = Line("TILT_ANGLES", 0, 0);
        return _weaponAngles[weapon] = new WeaponAngles(Tan(horizontal * radians) * 0.85f, DetMath.Atan(Tan(vertical * radians) * 0.85f), -up * radians, -down * radians);
    }

    private static float Tan(float angle) => DetMath.Sin(angle) / DetMath.Cos(angle);

    // How far each head (a machine's upper body, a tower's turning part) stands from its hull, in radians anticlockwise.
    private readonly Dictionary<int, float> _heads = [];

    private static float HullFacing(Actor actor) => actor switch
    {
        Machine machine => machine.Facing,
        Construction tower => tower.Orientation * MathF.PI / 180,
        _ => 0,
    };

    /// <summary>Which way an actor's weapons point, in radians.</summary>
    private float Heading(Actor actor) => HullFacing(actor) + _heads.GetValueOrDefault(actor.Id);

    /// <summary>
    /// Turns a head at its rate towards what it is firing at, or back to the hull with nothing to fire at: BODY_TURN_RATE
    /// for a machine with an upper body, MAX_ROTATION for a tower (MachPhysTurnerTracker). The hull of any other machine does it all.
    /// </summary>
    private void TurnHead(Actor actor, Actor? target)
    {
        var rate = actor switch
        {
            Machine { CanTurnUpperBody: true } machine => machine.Stats.BodyTurnRate ?? 0,
            Construction tower => tower.Stats.MaxRotation ?? 0,
            _ => 0,
        } * MathF.PI / 180;
        if (rate <= 0)
            return;
        var head = _heads.GetValueOrDefault(actor.Id);
        var wanted = 0f;
        if (target is not null)
        {
            var way = target.Position - actor.Position;
            wanted = DetMath.Atan2(way.Y, way.X) - HullFacing(actor);
        }
        var turn = MathF.IEEERemainder(wanted - head, 2 * MathF.PI);
        _heads[actor.Id] = head + Math.Clamp(turn, -rate * Dt, rate * Dt);
    }

    /// <summary>
    /// The space an actor fills (its bounding volume), for what hits it and what it hides: from the ground to twice the
    /// height of its middle; a flyer's in the air, as high as it is long.
    /// </summary>
    private (Vector3 Min, Vector3 Max) BoxOf(Actor actor)
    {
        var (ground, top) = (Terrain?.At(actor.Position) ?? 0, AimPoint(actor).Z);
        if (actor is Construction building)
            return (new Vector3(building.Footprint.Min, ground), new Vector3(building.Footprint.Max, 2 * top - ground));
        var reach = new Vector2(actor.Radius);
        return actor is Machine { Stats.Locomotion: Locomotion.Glider }
            ? (new Vector3(actor.Position - reach, top - actor.Radius), new Vector3(actor.Position + reach, top + actor.Radius))
            : (new Vector3(actor.Position - reach, ground), new Vector3(actor.Position + reach, 2 * top - ground));
    }

    /// <summary>Where a straight line first enters a box, as a share of its way along the line; null if it misses.</summary>
    private static float? EntryInto(Vector3 from, Vector3 to, Vector3 min, Vector3 max)
    {
        var (near, far) = (0f, 1f);
        for (var axis = 0; axis < 3; axis++)
        {
            var way = to[axis] - from[axis];
            if (MathF.Abs(way) < 1e-6f)
            {
                if (from[axis] < min[axis] || from[axis] > max[axis])
                    return null;
                continue;
            }
            var (a, b) = ((min[axis] - from[axis]) / way, (max[axis] - from[axis]) / way);
            near = MathF.Max(near, MathF.Min(a, b));
            far = MathF.Min(far, MathF.Max(a, b));
            if (near > far)
                return null;
        }
        return near;
    }

    /// <summary>Whether the target lies in reach of a weapon, as a weapon opens fire.</summary>
    private static bool InReach(Actor shooter, WeaponMount weapon, Actor target) => shooter.DistanceTo(target) <= weapon.Spec.Range * FiringRangeShare;

    /// <summary>
    /// MachLogCanAttack::rangeData: the line from the weapon as near the target as its DEVIATION_ANGLES let it from where the
    /// weapon points must meet the target, or it has to turn. A tower with no head to turn fires all round.
    /// </summary>
    private bool InArc(Actor shooter, WeaponMount weapon, Actor target)
    {
        var angles = AnglesOf(weapon.Spec);
        var (min, max) = BoxOf(target);
        var heading = Heading(shooter);
        var forward = new Vector2(DetMath.Cos(heading), DetMath.Sin(heading));
        var left = new Vector2(-forward.Y, forward.X);
        var way = new Vector2((min.X + max.X) / 2, (min.Y + max.Y) / 2) - shooter.Position;
        var x = Vector2.Dot(way, forward);
        if (shooter is Construction { Stats.MaxRotation: not > 0 })
            return WithinTilt(shooter, weapon, target);
        if (x <= 0.01f)
            return false;
        var limit = x * angles.TanHorizontal;
        var line = forward * x + left * Math.Clamp(Vector2.Dot(way, left), -limit, limit);
        return EntryInto(new Vector3(shooter.Position, 0), new Vector3(shooter.Position + line, 0), min with { Z = -1 }, max with { Z = 1 }) is not null
            && WithinTilt(shooter, weapon, target, x, forward);
    }

    /// <summary>Whether a weapon can tilt to something at this distance, on the slope its machine stands on (rangeData's vertical test, TILT_ANGLES).</summary>
    private bool WithinTilt(Actor shooter, WeaponMount weapon, Actor target, float distance, Vector2 forward)
    {
        var angles = AnglesOf(weapon.Spec);
        var (min, max) = BoxOf(target);
        var height = AimPoint(shooter).Z;
        var pitch = shooter is Machine { Stats.Locomotion: not Locomotion.Glider } && Terrain is { } ground
            ? DetMath.Atan2(ground.At(shooter.Position + forward * 2) - ground.At(shooter.Position - forward * 2), 4)
            : 0;
        const float nearlyStraight = 1.55f;
        var highest = distance * Tan(MathF.Min(nearlyStraight, pitch + angles.MostUp + angles.Vertical));
        var lowest = distance * Tan(MathF.Max(-nearlyStraight, pitch + angles.MostDown - angles.Vertical));
        return highest >= min.Z - height && lowest <= max.Z - height;
    }

    /// <summary>The same with the weapon turned to the target.</summary>
    private bool WithinTilt(Actor shooter, WeaponMount weapon, Actor target)
    {
        var way = target.Position - shooter.Position;
        var distance = way.Length();
        return distance < 0.01f || WithinTilt(shooter, weapon, target, distance, way / distance);
    }

    // ---- what stands on a line (MachLogCanAttack::testActorBehindCover, MachLogLinearProjectile) ------

    private List<Artefact>[] _artefactCells = [];
    private int _artefactsFiled = -1;
    private float _artefactReach;

    private void FileArtefacts()
    {
        if (_artefactsFiled == _artefacts.Count)
            return;
        if (_spatial.Length == 0)
            IndexActors();
        _artefactCells = new List<Artefact>[_spatial.Length];
        for (var i = 0; i < _artefactCells.Length; i++)
            _artefactCells[i] = [];
        _artefactReach = 0;
        foreach (var artefact in _artefacts.Values)
        {
            var (x, y) = SpatialCellOf(artefact.Position);
            _artefactCells[y * _spatialX + x].Add(artefact);
            _artefactReach = MathF.Max(_artefactReach, artefact.Radius);
        }
        _artefactsFiled = _artefacts.Count;
    }

    /// <summary>What a straight line meets first of what <paramref name="counts"/> takes, and how far along it, 0 to 1.</summary>
    private (Actor Actor, float Along)? FirstOnLine(Vector3 from, Vector3 to, Func<Actor, bool> counts)
    {
        FileArtefacts();
        var (a, b) = (new Vector2(from.X, from.Y), new Vector2(to.X, to.Y));
        (Actor Actor, float Along)? first = null;

        void Meet(Actor actor)
        {
            if (actor.IsDead || actor is Machine { Inside: not null } || !counts(actor))
                return;
            var (min, max) = BoxOf(actor);
            if (EntryInto(from, to, min, max) is not { } along)
                return;
            if (first is not { } best || along < best.Along || along == best.Along && actor.Id < best.Actor.Id)
                first = (actor, along);
        }

        var reach = new Vector2(MathF.Max(_spatialReach, _artefactReach) + SpatialSlack);
        var (x0, y0) = SpatialCellOf(Vector2.Min(a, b) - reach);
        var (x1, y1) = SpatialCellOf(Vector2.Max(a, b) + reach);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                foreach (var actor in _spatial[y * _spatialX + x])
                    Meet(actor);
                foreach (var artefact in _artefactCells[y * _spatialX + x])
                    Meet(artefact);
            }
        }
        return first;
    }

    // ---- where rounds end (MachLogLinearProjectile::genericCheckForDamage) ----------------------------

    /// <summary>How far a round's burst reaches and whether its damage falls off with the distance (each round's doBeDestroyed).</summary>
    private static (float Radius, bool FallsOff) BurstOf(string weapon) => weapon switch
    {
        "LARGE_MISSILE" => (20, true),
        _ when weapon.StartsWith("MULTI_LAUNCHER", StringComparison.Ordinal) => (10, true),
        _ when weapon.StartsWith("FLAME_THROWER", StringComparison.Ordinal) => (4, true),
        _ when weapon.Contains("BOLTER") || weapon is "AUTO_CANNON" or "VIRUS" => (1, false),
        _ => (3, false),
    };

    /// <summary>
    /// A round's end, wherever that is, on something, on the ground or in the air: whatever lies within reach of the point
    /// is hit, friend or foe, the shooter too unless it threw flame or lightning. A land mine goes up at twice the
    /// reach. Buildings and artefacts take it all; machines all but what falls off with the distance from the
    /// point to where they stand, unless the point lies within them.
    /// </summary>
    private void Burst(Vector3 point, WeaponSpec weapon, int shooter, Race race)
    {
        var (radius, fallsOff) = BurstOf(weapon.TypeName);
        var damage = (int)MathF.Round(weapon.Damage);
        var by = Find(shooter);
        var spares = Sprays(weapon);
        var flat = new Vector2(point.X, point.Y);
        FileArtefacts();

        void Reach(Actor actor)
        {
            if (actor.IsDead || actor is Machine { Inside: not null } || spares && actor.Id == shooter)
                return;
            var (min, max) = BoxOf(actor);
            var off = Vector3.DistanceSquared(point, Vector3.Clamp(point, min, max));
            if (off >= radius * radius)
                return;
            var hit = damage;
            if (actor is Machine && fallsOff && off > 1e-4f && Vector3.Distance(point, new Vector3(actor.Position, min.Z)) is > 1 and var distance)
                hit = Math.Max(1, (int)(damage - distance * damage / radius));
            Damage(actor, hit, race, by);
            Raise(new GameEvent(GameEventKind.Hit, actor.Id, actor.Position, shooter, weapon.TypeName));
        }

        foreach (var actor in FiledNear(flat, radius + _spatialReach).ToList())
            Reach(actor);
        var near = new Vector2(radius + _artefactReach + SpatialSlack);
        var (x0, y0) = SpatialCellOf(flat - near);
        var (x1, y1) = SpatialCellOf(flat + near);
        for (var y = y0; y <= y1; y++)
        {
            for (var x = x0; x <= x1; x++)
            {
                foreach (var artefact in _artefactCells[y * _spatialX + x])
                    Reach(artefact);
            }
        }
        foreach (var mine in LandMines.ToList())
        {
            if (Vector3.DistanceSquared(point, new Vector3(mine.Position, Terrain?.At(mine.Position) ?? 0)) < 4 * radius * radius)
                BlowUp(mine);
        }
    }

    /// <summary>Any hit sets off a land mine, which is gone then (MachLogLandMine::beHit); it hurts nothing by that.</summary>
    private void BlowUp(LandMineState mine)
    {
        LandMines.Remove(mine);
        Raise(new GameEvent(GameEventKind.MineExploded, 0, mine.Position, mine.Id));
    }

    /// <summary>
    /// A bolt hits at once where the target's middle is (MachLogBolterWeapon::doFire: no line is tested); one that goes wide
    /// misses by a tenth of its drift and carries on 5 m past, so that it rather hits what stands behind.
    /// </summary>
    private void Bolt(Actor shooter, WeaponMount weapon, Actor target)
    {
        var point = AimPoint(target);
        if (weapon.Burst.Missed)
        {
            var way = point - new Vector3(shooter.Position, Terrain?.At(shooter.Position) ?? 0);
            point += new Vector3(weapon.Burst.FirstDrift / 10, weapon.Burst.SecondDrift / 10, 0);
            if (way.LengthSquared() > 1e-4f)
                point += Vector3.Normalize(way) * 5;
        }
        Burst(InWorld(point), weapon.Spec, shooter.Id, shooter.Race);
    }

    private Vector3 InWorld(Vector3 point) =>
        new(Math.Clamp(point.X, 0, Planet.Surface.Width), Math.Clamp(point.Y, 0, Planet.Surface.Depth), point.Z);

    /// <summary>Flame and lightning: rounds of their own in the original, too slow and short to be shown as rounds here.</summary>
    private static bool Sprays(WeaponSpec weapon) =>
        weapon.TypeName.StartsWith("FLAME_THROWER", StringComparison.Ordinal) || weapon.TypeName == "ELECTRIC_CHARGE";

    // The sprays on their way: when each ends, and where.
    private readonly List<(float At, Vector3 Point, WeaponSpec Weapon, int Shooter, Race Race)> _sprays = [];

    /// <summary>
    /// Sends off a spray towards a point. A flamethrower throws all its balls at once, each by its EXTRAS: when it leaves, how
    /// much further than the range it goes and how far up (MachPhysFlameThrower::createFlameBall). Each flies until it
    /// meets something, what stands in its way found as it leaves (MachLogLinearProjectile's constructor).
    /// </summary>
    private void Spray(Actor shooter, WeaponSpec weapon, Vector3 towards)
    {
        var from = AimPoint(shooter);
        var way = towards - from;
        var heading = Heading(shooter);
        var along = way.LengthSquared() > 1e-4f ? Vector3.Normalize(way) : new Vector3(DetMath.Cos(heading), DetMath.Sin(heading), 0);
        var extras = Blast.Extras(weapon);
        if (weapon.TypeName == "ELECTRIC_CHARGE" || extras.Count < 5)
        {
            Throw(shooter, weapon, from, from + along * weapon.Range, 0);
            return;
        }
        var first = Enumerable.Range(0, extras.Count / 5).Min(i => extras[i * 5]);
        for (var i = 0; i + 4 < extras.Count; i += 5)
            Throw(shooter, weapon, from, from + along * (weapon.Range + extras[i + 2]) + Vector3.UnitZ * extras[i + 3], extras[i] - first);
    }

    private void Throw(Actor shooter, WeaponSpec weapon, Vector3 from, Vector3 to, float delay)
    {
        var flight = Terrain?.FirstBlocked(from, to, ends: 0) ?? 1;
        if (FirstOnLine(from, to, actor => actor.Id != shooter.Id) is { } met)
            flight = MathF.Min(flight, met.Along);
        var point = Vector3.Lerp(from, to, flight);
        _sprays.Add((Time + delay + Vector3.Distance(from, point) / MathF.Max(1, weapon.ProjectileSpeed), InWorld(point), weapon, shooter.Id, shooter.Race));
    }

    private void UpdateSprays()
    {
        for (var i = 0; i < _sprays.Count; i++)
        {
            var (at, point, weapon, shooter, race) = _sprays[i];
            if (at > Time)
                continue;
            _sprays.RemoveAt(i--);
            Burst(point, weapon, shooter, race);
        }
    }

    // ---- the vortex (MachLogVortexWeapon) ---------------------------------------------------------------

    /// <summary>How long an Eradicator stands after firing its vortex (setBusyPeriod), unless ordered otherwise.</summary>
    public const float VortexBusy = 9;

    /// <summary>The vortex goes off where its target will be in 1.3 s, but far enough off not to reach the shooter.</summary>
    private void FireVortex(Actor shooter, WeaponSpec vortex, Actor target)
    {
        var mark = Predicted(target, 1.3f);
        var point = new Vector2(mark.X, mark.Y);
        var safe = Blast.Extras(vortex)[0] + 6;
        var way = point - shooter.Position;
        if (way.LengthSquared() <= safe * safe)
        {
            var heading = Heading(shooter);
            point = shooter.Position + (way.LengthSquared() > 1e-4f ? Vector2.Normalize(way) : new Vector2(DetMath.Cos(heading), DetMath.Sin(heading))) * safe;
        }
        var safely = InWorld(new Vector3(point, 0));
        Vortex(shooter, vortex, new Vector2(safely.X, safely.Y));
    }

    private void Vortex(Actor shooter, WeaponSpec vortex, Vector2 point)
    {
        if (shooter is Machine eradicator)
            _busyUntil[eradicator.Id] = Time + VortexBusy;
        StartBlast(vortex, point, shooter.Race);
    }

    /// <summary>An order given to an Eradicator takes it out of its wait (MachLogMachine::manualCommandIssued).</summary>
    private void ManualCommand(Machine machine)
    {
        if (machine.Weapons.Any(w => w.Spec.TypeName == "VORTEX"))
            _busyUntil.Remove(machine.Id);
    }

    /// <summary>
    /// What goes with an Eradicator or a finished silo set to destroy itself, also if something else got there first: its
    /// vortex or nuke, if charged, goes off where it stood (MachActor::checkAndDoSpecialDeaths).
    /// </summary>
    private void LastShot(Actor actor)
    {
        switch (actor)
        {
            case Machine eradicator when eradicator.Weapons.FirstOrDefault(w => w.Spec.TypeName == "VORTEX") is { Reload: <= 0 } vortex:
                Raise(new GameEvent(GameEventKind.Fired, eradicator.Id, eradicator.Position, null, vortex.Spec.TypeName));
                StartBlast(vortex.Spec, eradicator.Position, eradicator.Race);
                break;
            case Construction { Complete: true } silo when silo.Weapons.FirstOrDefault(w => w.Spec.TypeName == "NUCLEAR_MISSILE") is { Reload: <= 0 } missile:
                LaunchStrike(silo, missile, silo.Position);
                break;
        }
    }

    // ---- towers looking for targets (MachLogMissileEmplacement, MachLogCanAttack::checkAndAttackCloserTarget) ---

    private static float ScannerOf(Construction tower) => MaximumWeaponRange(tower) * TurretScanner;

    /// <summary>MachLogMissileEmplacement::inScanningRange.</summary>
    private static bool Detects(Construction tower, Actor other) =>
        (other is Construction building ? building.DistanceFrom(tower.Position) : Vector2.Distance(tower.Position, other.Position)) <= ScannerOf(tower);

    private bool TooClose(Construction tower, Actor other) => tower.IsType("MISSILE_EMPLACEMENT", "LAUNCHER") && HitPointShare(tower) > 0.25f
        && Vector2.DistanceSquared(tower.Position, other.Position) < LauncherMinimum * LauncherMinimum;

    /// <summary>What a tower fires at. It keeps that for as long as its scanner sees it, then has none (MachLogMissileEmplacementAttackOperation::doIsFinished).</summary>
    private Actor? TowerTarget(Construction tower)
    {
        if (!_turretTargets.TryGetValue(tower.Id, out var id))
            return null;
        if (FindTarget(id) is { IsDead: false } target && CanAttack(tower.Race, target) && Detects(tower, target) && !TooClose(tower, target))
            return target;
        _turretTargets.Remove(tower.Id);
        return null;
    }

    /// <summary>MachLogMissileEmplacement::update: a tower looks about as often as its alertness says.</summary>
    private void LookOut(Construction tower)
    {
        var alert = tower.Alert;
        if (Time < alert.NextLook)
            return;
        if (WillCheckForTargets(alert))
            CheckAndAttackCloserTarget(tower);
        Diminish(alert, 50);
        var interval = 4.2f - Math.Min(100, alert.Level) / 30f;
        alert.NextLook = Time + (_threatsTo.ContainsKey(tower.Id) ? MathF.Min(interval, 1) : interval);
    }

    /// <summary>
    /// MachLogCanAttack::checkAndAttackCloserTarget for a tower: with a target it thinks of another one time in five. It changes
    /// only for one it can shoot from here; with its target shootable too an AI's tower never does, a player's only
    /// for one that can hurt it if its target cannot.
    /// </summary>
    private void CheckAndAttackCloserTarget(Construction tower, Actor? firedAtMe = null)
    {
        var current = TowerTarget(tower);
        Actor? candidate;
        if (firedAtMe is not null)
            candidate = TooClose(tower, firedAtMe) ? null : firedAtMe;
        else if (current is not null && Random.Next(5) != 0)
        {
            tower.Alert.Raise(FullAlert);
            return;
        }
        else
            candidate = NearestEnemy(tower, ScannerOf(tower), accept: enemy => enemy.Id != current?.Id && !TooClose(tower, enemy));
        if (candidate is null or Machine { Inside: not null } || !Detects(tower, candidate))
            return;
        tower.Alert.Raise(FullAlert);
        if (current is not null)
        {
            if (current.Id == candidate.Id || !ShootableFromHere(tower, candidate))
                return;
            if (ShootableFromHere(tower, current) && (Races[tower.Race].Controller == Controller.Ai || DirectThreat(current) || !DirectThreat(candidate)))
                return;
        }
        _turretTargets[tower.Id] = candidate.Id;
    }

    /// <summary>MachLogMissileEmplacement::beHit: a tower turns on what hit it, at most every ten seconds for what is not its target.</summary>
    private void TowerHit(Construction tower, Actor shooter)
    {
        var alert = tower.Alert;
        if (Time < alert.NoChecksBefore || !CanFireAt(tower, shooter) || tower.Spec.SubType == "ICBM")
            return;
        var current = TowerTarget(tower);
        var test = current is null || current.Id != shooter.Id && Time >= alert.NextHitByTest;
        if (test || current?.Id == shooter.Id)
            alert.NextHitByTest = Time + 10;
        if (test && AreEnemies(tower.Race, shooter.Race))
            CheckAndAttackCloserTarget(tower, shooter);
    }
}
