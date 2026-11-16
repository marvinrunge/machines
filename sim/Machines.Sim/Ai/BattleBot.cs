using System.Numerics;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Sim.Ai;

/// <summary>
/// The computer's hero in a battle. It plays by orders, as a player's machines take them (move, attack, stand, abilities
/// and the dash), and holds its target: its hero is at high DefCon, so neither it picks fights of its own nor runs. The
/// first of a team by race attacks: it fights the nearest enemy hero near it, else takes the enemy pod, else hunts. The
/// second defends: an enemy in its own circle first, then one near the pod, else it guards the pod (with victory by wipe
/// alone, where there is no pod to take, it goes with the attacker instead), and once its mate has fallen it guards the pod
/// until all is quiet there for a while, then attacks itself. It uses what its chassis can do: Old Iron rams what it
/// fights at close quarters, a Colossus anchors where it holds and lets go before it moves on, a Widow that is not under
/// fire stands and lurks before its first shot, a Harvester presses in for the kill, a Marshal on the attack and not
/// fighting keeps by a team-mate out in the field (one that guards stays at its pod), a spy lays a mine where it stands
/// when an enemy comes close, never on another and out of its own fire, and on its pod's approach while it guards, and a
/// geo locator sends its pulse at enemies in its scanner's reach. It sees only the world, thinks inside the world's tick
/// every <see cref="ThinkEvery"/> ticks, and issues through <see cref="GameWorld.Issue"/>, or through
/// <paramref name="issue"/> for a driver that turns its orders into input; all it keeps follows from tick 0, and what its
/// chassis' specials go by (anchors, lurks, mines, the pulse) it reads off the world each time.
/// </summary>
public sealed class BattleBot(GameWorld world, Race side, Action<Command>? issue = null)
{
    /// <summary>The ticks between two looks at the world.</summary>
    public const int ThinkEvery = 10;

    /// <summary>How near an enemy hero must be for the attacker to fight it rather than go on.</summary>
    private const float FightReach = 120;

    /// <summary>How near the own pod an enemy must be for the defender to go for it.</summary>
    private const float GuardReach = 100;

    /// <summary>How long the own pod must be quiet before a defender whose mate has fallen attacks.</summary>
    private const float QuietTime = 60;

    /// <summary>How far from the middle of the enemy pod it holds, inside the capture circle.</summary>
    private const float HoldAt = 22;

    /// <summary>How far from the middle of its own pod the defender guards it: further out, clear of the walls.</summary>
    private const float GuardAt = 28;

    /// <summary>How near its goal a move counts as there, and how far a goal may move before the order is given anew.</summary>
    private const float Arrived = 4, Moved = 3;

    /// <summary>How far off a goal must be for the dash to be worth firing on the way.</summary>
    private const float DashFrom = 150;

    /// <summary>How long it may go without firing at its target before it closes in, for how long, and how near is near enough.</summary>
    private const float StallTime = 4, CloseInTime = 3, CloseEnough = 20;

    /// <summary>How long a Widow must have gone unhit for it to stand and lurk before its first shot.</summary>
    private const float LurkQuiet = 3;

    /// <summary>How near, edge to edge, a Harvester keeps to what it fights, so that the kill falls within its harvest.</summary>
    private const float HarvestClose = 12;

    /// <summary>How near a Marshal that is not fighting keeps to a team-mate out in the field, well within its field.</summary>
    private const float MarshalNear = 25;

    /// <summary>How near an enemy hero on the ground must come for a spy to lay a mine under itself.</summary>
    private const float MineNear = 15;

    /// <summary>
    /// A spy guarding its pod lays a mine on the approach this often; the places lie this far apart at least, and it lays
    /// one once it is this near the place.
    /// </summary>
    private const float MineEvery = 10, MineApart = 4.5f, LayAt = 2.5f;

    /// <summary>A geo locator sends its pulse again at an enemy hero located for no longer than this.</summary>
    private const float LocateAgain = 2;

    public Race Side => side;

    // Whether the defender has taken over the attack, and since when its pod has been quiet.
    private bool _attacks;
    private float _quietSince;
    private readonly Dictionary<int, Vector2> _holds = [];

    // The hero it fights, since when, and until when it closes in on it.
    private int _fightTarget;
    private float _fightSince, _closeInUntil;

    private void Issue(Command command) => (issue ?? world.Issue)(command);

    /// <summary>One look at the world: a command or a few, or none.</summary>
    public void Think()
    {
        if (world.Battle is not { Over: false } run || run.InOpening(world.Time) || world.Wired?.Heroes.GetValueOrDefault(side) is not { } state)
            return;
        var team = run.TeamOf(side);
        if (team < 0)
            return;
        var ownPod = world.BattlePod(team);
        var own = world.HeroOf(side);
        var enemies = Enemies(run, team, own);
        if (own is not { Inside: null } hero)
        {
            NoteQuiet(ownPod, enemies);
            return;
        }
        if (hero.DefCon != DefCon.High)
            Issue(new DefConCommand(side, [hero.Id], DefCon.High));
        UseSpecial(state, hero, enemies, UseAbilities(state, hero, enemies));

        var mates = run.Teams[team].Order().ToList();
        var defender = mates.IndexOf(side) > 0;
        NoteQuiet(ownPod, enemies);
        if (defender && Defend(run, team, hero, ownPod, enemies, mates))
            return;
        Attack(run, team, hero, enemies);
    }

    /// <summary>What the chassis of the bot's hero can do besides carrying weapons.</summary>
    private ChassisSpecial Special(WiredState state) => state.Build(world.Data).Chassis.Special;

    /// <summary>The enemy heroes standing outside, in race order, leaving out those hidden in their shadow from the bot's own hero.</summary>
    private List<Machine> Enemies(BattleRun run, int team, Machine? own) =>
        run.Teams[1 - team].Order().Select(world.HeroOf).OfType<Machine>().Where(m => m.Inside is null && (own is null || !world.Shadowed(own, m))).ToList();

    /// <summary>Keeps the time since an enemy was last near the own pod.</summary>
    private void NoteQuiet(Construction? ownPod, List<Machine> enemies)
    {
        if (ownPod is null || enemies.Any(e => Vector2.Distance(e.Position, ownPod.Position) <= GuardReach))
            _quietSince = world.Time;
    }

    /// <returns>Whether the defender did its own part; false sends it along with the attack.</returns>
    private bool Defend(BattleRun run, int team, Machine hero, Construction? ownPod, List<Machine> enemies, List<Race> mates)
    {
        if (ownPod is null)
            return false;
        // One that has gone over to the attack and is nearer the other pod than its own does not walk the whole way back
        // (two lone attackers turned back at each other's circles and the battle ran out of time); nor is there anything to
        // guard against once no enemy stands and none has a body to come (victory by taking the pod alone goes on then).
        var enemyPod = world.BattlePod(1 - team);
        if (_attacks && enemyPod is not null && Vector2.Distance(hero.Position, enemyPod.Position) < Vector2.Distance(hero.Position, ownPod.Position)
            || EnemiesOut(run, team))
            return false;
        // An enemy in the own circle first, then one near the pod.
        var intruder = Nearest(enemies, ownPod.Position, BattleBalance.CaptureRadius + 2) ?? Nearest(enemies, ownPod.Position, GuardReach);
        if (intruder is not null)
        {
            Fight(hero, intruder, enemies);
            return true;
        }
        var mateStands = mates.Where(r => r != side).Any(r => world.HeroOf(r) is not null);
        if (mateStands)
        {
            _attacks = false;
            // With a pod to take, its mate goes for the other pod and it guards its own; it only hunts with its mate when
            // there is nothing to take (else all four heroes would race each other's pods and never meet).
            if (run.Options.Win == BattleWin.Wipe)
                return false;
        }
        else if (_attacks || world.Time - _quietSince >= QuietTime)
        {
            _attacks = true;
            return false;
        }
        // Guard the pod: the mate is off taking the other one, or has fallen. A Marshal guards it too, its field round the
        // pod (the pod would stand open while it went with its mate), and a spy lays its mines on the approach as they
        // come due.
        var state = world.Wired!.Heroes[side];
        var guard = HoldPoint(ownPod, enemyPod?.Position ?? hero.Position, mates.IndexOf(side), hero.Radius);
        if (Nearest(enemies, hero.Position, FightReach) is { } near)
            Fight(hero, near, enemies);
        else if (!MineApproach(state, hero, ownPod, enemyPod?.Position ?? hero.Position))
            Go(hero, guard, stand: true);
        return true;
    }

    /// <summary>The attack: the nearest enemy hero close by, else the enemy pod (or, for a Marshal, a mate out in the field), else the hunt.</summary>
    private void Attack(BattleRun run, int team, Machine hero, List<Machine> enemies)
    {
        if (Nearest(enemies, hero.Position, FightReach) is { } near)
        {
            Fight(hero, near, enemies);
            return;
        }
        if (run.Options.Win != BattleWin.Wipe && world.BattlePod(1 - team) is { } enemyPod)
        {
            if (KeepNear(world.Wired!.Heroes[side], run, team, hero))
                return;
            var from = world.BattlePod(team)?.Position ?? hero.Position;
            var index = run.Teams[team].Order().ToList().IndexOf(side);
            Go(hero, HoldPoint(enemyPod, from, index, hero.Radius), stand: true, inCircle: enemyPod);
            return;
        }
        if (Nearest(enemies, hero.Position, float.MaxValue) is { } quarry)
            Fight(hero, quarry, enemies);
    }

    /// <summary>
    /// A Marshal on the attack that is not fighting keeps within <see cref="MarshalNear"/> of a team-mate out in the field,
    /// so that its field reaches it: at its own place in the enemy's circle while the mate takes the pod, else close by it,
    /// standing its ground once there. A mate guarding the own pod, or chasing what came near it, is no reason to stay back
    /// from the attack (<see cref="MateAfield"/>); a Marshal guarding the pod stays at it. False for any other chassis, and
    /// with no mate out in the field.
    /// </summary>
    private bool KeepNear(WiredState state, BattleRun run, int team, Machine hero)
    {
        if ((Special(state) & ChassisSpecial.Field) == 0 || MateAfield(run, team) is not { } mate)
            return false;
        if (run.Options.Win != BattleWin.Wipe && world.BattlePod(1 - team) is { } enemyPod
            && Vector2.Distance(mate.Position, enemyPod.Position) <= BattleBalance.CaptureRadius)
        {
            var from = world.BattlePod(team)?.Position ?? hero.Position;
            Go(hero, HoldPoint(enemyPod, from, run.Teams[team].Order().ToList().IndexOf(side), hero.Radius), stand: true, inCircle: enemyPod);
            return true;
        }
        if (Vector2.Distance(hero.Position, mate.Position) > MarshalNear)
        {
            if (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, mate.Position) > Moved * 3)
                Issue(new MoveCommand(side, [hero.Id], mate.Position));
        }
        else if (hero.Order is not StandGroundOrder)
            Issue(new StandGroundCommand(side, [hero.Id]));
        return true;
    }

    /// <summary>
    /// The team-mate standing out in the open away from the own pod, or null: further from it than a guard goes for what
    /// comes near it, and a Marshal's reach beyond, so that a guard chasing an intruder is not followed home.
    /// </summary>
    private Machine? MateAfield(BattleRun run, int team)
    {
        var home = world.BattlePod(team);
        foreach (var race in run.Teams[team].Order())
        {
            if (race != side && world.HeroOf(race) is { Inside: null } mate
                && (home is null || Vector2.Distance(mate.Position, home.Position) > GuardReach + MarshalNear))
                return mate;
        }
        return null;
    }

    /// <summary>
    /// A spy guarding its pod lays a mine on the pod's approach every <see cref="MineEvery"/> seconds while it has any to
    /// lay: at the first place round its guard's circle, facing <paramref name="from"/> and then to either side by turns,
    /// where no mine of its race lies yet, going there first. False when none is due or no place is left, and it guards.
    /// </summary>
    private bool MineApproach(WiredState state, Machine hero, Construction pod, Vector2 from)
    {
        if ((Special(state) & ChassisSpecial.Mines) == 0 || world.Time - state.MineLaidAt < MineEvery || world.WhyNoMine(state) is not null
            || ApproachPlace(pod, from) is not { } place)
            return false;
        if (Vector2.Distance(hero.Position, place) <= LayAt)
            Issue(new WiredSpecialCommand(side));
        else if (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, place) > 0.5f)
            Issue(new MoveCommand(side, [hero.Id], place));
        return true;
    }

    /// <summary>The next place for a mine on the approach to a pod: round its guard's circle from the side facing <paramref name="from"/>, on open ground and clear of the race's mines.</summary>
    private Vector2? ApproachPlace(Construction pod, Vector2 from)
    {
        var way = from - pod.Position;
        var facing = way.LengthSquared() > 1 ? DetMath.Atan2(way.Y, way.X) : 0;
        for (var step = 0; step < 12; step++)
        {
            var turn = (step + 1) / 2 * (step % 2 == 0 ? 1 : -1);
            var angle = facing + turn * MathF.Tau / 24;
            var point = pod.Position + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * GuardAt;
            if (!world.Nav.IsFree(point, crossesWater: false))
                continue;
            var taken = false;
            foreach (var mine in world.LandMines)
                taken |= mine.Race == side && Vector2.DistanceSquared(mine.Position, point) < MineApart * MineApart;
            if (!taken)
                return point;
        }
        return null;
    }

    /// <summary>Whether no hero of the other team stands and none has a body to come.</summary>
    private bool EnemiesOut(BattleRun run, int team) =>
        run.Teams[1 - team].All(race => world.HeroOf(race) is null && world.Wired?.Heroes.GetValueOrDefault(race)?.RespawnAt is null);

    /// <summary>The nearest of the machines within reach of a place, ties by id.</summary>
    private static Machine? Nearest(List<Machine> machines, Vector2 place, float reach)
    {
        Machine? nearest = null;
        var best = reach;
        foreach (var machine in machines)
        {
            var distance = Vector2.Distance(machine.Position, place);
            if (distance < best || distance == best && nearest is not null && machine.Id < nearest.Id)
                (nearest, best) = (machine, distance);
        }
        return nearest;
    }

    /// <summary>
    /// Attacks a hero. One that has fired at nothing for a while, with ground or rock between it and its target, closes in
    /// for a few seconds before it takes aim again; the dash helps it close a long way. Old Iron rams a target close in
    /// front, a Widow not under fire stands until it lurks before its first shot, a Harvester closes in between its shots,
    /// and an anchored Colossus fights from where it stands what it reaches and lets go of its tracks for anything else.
    /// </summary>
    private void Fight(Machine hero, Machine target, List<Machine> enemies)
    {
        var state = world.Wired!.Heroes[side];
        var special = Special(state);
        if (target.Id != _fightTarget)
            (_fightTarget, _fightSince, _closeInUntil) = (target.Id, world.Time, 0);
        if ((special & ChassisSpecial.Plough) != 0 && Ram(state, hero, target))
            return;
        if ((special & ChassisSpecial.Lurk) != 0 && WaitsToLurk(state, hero, target, enemies))
        {
            if (hero.Order is not StandGroundOrder)
                Issue(new StandGroundCommand(side, [hero.Id]));
            return;
        }
        if (world.Time < _closeInUntil && hero.DistanceTo(target) > CloseEnough)
        {
            if (Free(hero) && (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, target.Position) > Moved * 3))
                Issue(new MoveCommand(side, [hero.Id], target.Position));
            return;
        }
        if (world.Time - MathF.Max(state.LastShotAt, _fightSince) > StallTime && hero.DistanceTo(target) > CloseEnough)
        {
            if (!Free(hero))
                return;
            (_closeInUntil, _fightSince) = (world.Time + CloseInTime, world.Time + CloseInTime);
            Issue(new MoveCommand(side, [hero.Id], target.Position));
            return;
        }
        if ((special & ChassisSpecial.Harvest) != 0 && hero.DistanceTo(target) > HarvestClose && Reloading(hero))
        {
            if (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, target.Position) > Moved * 3)
                Issue(new MoveCommand(side, [hero.Id], target.Position));
            return;
        }
        // Anchored, it waits for its tracks to come free before it goes after what it does not reach from there.
        if (world.AnchorPhaseOf(hero) != AnchorPhase.Free && hero.DistanceTo(target) > RootedReach(hero) && !Free(hero))
            return;
        if (hero.Order is not AttackOrder attack || attack.Target != target.Id)
            Issue(new AttackCommand(side, [hero.Id], target.Id));
        else if (hero.Weapons.Count > 0 && hero.DistanceTo(target) > hero.Weapons.Max(w => w.Spec.Range) + 40)
            DashTowards(hero, target.Position);
    }

    /// <summary>
    /// Old Iron's ram: with its dash ready and its target on the ground, out in the open and within
    /// <see cref="WiredBalance.RamReach"/> in the front third of its hull, it dashes at it. True when it did.
    /// </summary>
    private bool Ram(WiredState state, Machine hero, Machine target)
    {
        if (world.WhyNoDash(state) is not null || target.Inside is not null || target.Stats.Locomotion == Locomotion.Glider)
            return false;
        var way = target.Position - hero.Position;
        if (way.LengthSquared() > WiredBalance.RamReach * WiredBalance.RamReach || way.LengthSquared() < 1e-4f)
            return false;
        var ahead = new Vector2(DetMath.Cos(hero.Facing), DetMath.Sin(hero.Facing));
        if (Vector2.Dot(Vector2.Normalize(way), ahead) < WiredBalance.PloughCos)
            return false;
        Issue(new WiredDashCommand(side, Vector2.Normalize(way)));
        return true;
    }

    /// <summary>
    /// Whether a Widow should stand and lurk before it fires: it has not been hit for <see cref="LurkQuiet"/> seconds, no
    /// enemy is within its weapons' reach as made, its target is within their reach as the lurk tunes them, and it neither
    /// lurks yet nor has spent the lurk where it stands. Then its first shot, once it lurks, reaches further and hurts more.
    /// </summary>
    private bool WaitsToLurk(WiredState state, Machine hero, Machine target, List<Machine> enemies)
    {
        if (world.Time - state.LastHitAt < LurkQuiet || world.SpecialView(side) is not { Phase: "Standing" })
            return false;
        var reach = hero.Weapons.Where(w => w.DealsDamage).Select(w => w.Base.Range).DefaultIfEmpty(0).Max() * GameWorld.FiringRangeShare;
        if (reach <= 0 || enemies.Exists(e => hero.DistanceTo(e) <= reach))
            return false;
        return hero.DistanceTo(target) <= reach * WiredBalance.LurkRange;
    }

    /// <summary>Whether every weapon of the hero that deals damage is reloading past the bot's next look, so that it can move in between shots.</summary>
    private static bool Reloading(Machine hero)
    {
        var any = false;
        foreach (var weapon in hero.Weapons)
        {
            if (!weapon.DealsDamage)
                continue;
            if (weapon.Reload <= ThinkEvery * GameWorld.Dt)
                return false;
            any = true;
        }
        return any;
    }

    /// <summary>How far a Colossus whose tracks lock or hold reaches from where it stands: its furthest weapon as the anchor tunes it, at the share of the range it opens fire at.</summary>
    private static float RootedReach(Machine hero) =>
        hero.Weapons.Where(w => w.DealsDamage).Select(w => w.Base.Range).DefaultIfEmpty(0).Max() * WiredBalance.AnchorRange * GameWorld.FiringRangeShare;

    /// <summary>
    /// Whether the hero may set off: a Colossus whose tracks lock or hold lets them go first, and one whose tracks are
    /// coming free waits for them, so that no order to go waits on locked tracks. Any other hero always may.
    /// </summary>
    private bool Free(Machine hero)
    {
        switch (world.AnchorPhaseOf(hero))
        {
            case AnchorPhase.Free:
                return true;
            case AnchorPhase.Releasing:
                return false;
            default:
                Issue(new WiredSpecialCommand(side, On: false));
                return false;
        }
    }

    /// <summary>
    /// Goes to a place and, with <paramref name="stand"/>, stands its ground there, firing at whatever comes in reach; a
    /// Colossus anchors there too, and lets go of its tracks before it sets off again. With <paramref name="inCircle"/> it
    /// counts as there once inside that pod's capture circle.
    /// </summary>
    private void Go(Machine hero, Vector2 place, bool stand, Construction? inCircle = null)
    {
        var there = Vector2.Distance(hero.Position, place) <= Arrived
            || inCircle is not null && Vector2.Distance(hero.Position, inCircle.Position) <= BattleBalance.CaptureRadius - 2;
        if (there)
        {
            if (stand && hero.Order is not StandGroundOrder)
                Issue(new StandGroundCommand(side, [hero.Id]));
            if (stand && (Special(world.Wired!.Heroes[side]) & ChassisSpecial.Anchor) != 0 && world.AnchorPhaseOf(hero) == AnchorPhase.Free)
                Issue(new WiredSpecialCommand(side, On: true));
            return;
        }
        if (!Free(hero))
            return;
        if (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, place) > Moved)
        {
            Issue(new MoveCommand(side, [hero.Id], place));
            return;
        }
        if (Vector2.Distance(hero.Position, place) > DashFrom && hero.PathIndex < hero.Path.Count)
            DashTowards(hero, hero.Path[hero.PathIndex]);
    }

    /// <summary>Fires the thrusters (or the chassis' own dash) towards a place, when it has them, they are ready and no anchor holds it.</summary>
    private void DashTowards(Machine hero, Vector2 place)
    {
        if (world.WhyNoDash(world.Wired!.Heroes[side]) is not null)
            return;
        var way = place - hero.Position;
        if (way.LengthSquared() < 1)
            return;
        Issue(new WiredDashCommand(side, Vector2.Normalize(way)));
    }

    /// <summary>
    /// A free place in a pod's circle, on the side facing <paramref name="from"/>, a little apart for each of a team; found
    /// once for each pod and place in the team. It keeps off the pod's door, where heroes are set down and come back (a guard
    /// parked there stood on its own mate's spot), and the hero's whole breadth is on open ground; the own pod is guarded
    /// from further out than the other is held in, clear of its walls.
    /// </summary>
    private Vector2 HoldPoint(Construction pod, Vector2 from, int index, float radius)
    {
        var key = pod.Id * 8 + index;
        if (_holds.TryGetValue(key, out var known))
            return known;
        var way = from - pod.Position;
        var facing = way.LengthSquared() > 1 ? DetMath.Atan2(way.Y, way.X) : 0;
        var hold = pod.EntranceOutside ?? pod.Position;
        var at = world.Battle?.TeamOf(side) is { } team and >= 0 && world.BattlePod(team)?.Id == pod.Id ? GuardAt : HoldAt;
        var offDoor = 2 * radius + 4;
        bool Open(Vector2 p) => world.Nav.IsFree(p, crossesWater: false)
            && Enumerable.Range(0, 4).All(k => world.Nav.IsFree(p + new Vector2(DetMath.Cos(k * MathF.Tau / 4), DetMath.Sin(k * MathF.Tau / 4)) * radius, crossesWater: false));
        for (var step = 0; step < 16; step++)
        {
            // From the side facing the team outwards, alternately left and right, the second of the team a step aside.
            var turn = (step + 1) / 2 * (step % 2 == 0 ? 1 : -1) + (index > 0 ? 0.5f : 0);
            var angle = facing + turn * MathF.Tau / 16;
            var point = pod.Position + new Vector2(DetMath.Cos(angle), DetMath.Sin(angle)) * at;
            if (Open(point) && (pod.EntranceOutside is not { } door || Vector2.Distance(point, door) >= offDoor))
            {
                hold = point;
                break;
            }
        }
        _holds[key] = hold;
        return hold;
    }

    // ---- abilities --------------------------------------------------------------------------------

    /// <summary>
    /// The special key where it is used wherever the hero is: a spy lays a mine under itself with an enemy hero on the
    /// ground within <see cref="MineNear"/>, where <see cref="MineHere"/> allows one, and a geo locator sends its pulse once
    /// an enemy hero within its scanner's reach is not located, or for no longer than <see cref="LocateAgain"/>. The anchor
    /// goes with where the bot stands (<see cref="Go"/>, <see cref="Free"/>), the Marshal's field with where it goes
    /// (<see cref="KeepNear"/>), and the mines on the approach with guarding (<see cref="MineApproach"/>).
    /// </summary>
    /// <param name="blasting">Whether it set off a vortex or an ion strike in this look.</param>
    private void UseSpecial(WiredState state, Machine hero, List<Machine> enemies, bool blasting)
    {
        var special = Special(state);
        if ((special & ChassisSpecial.Mines) != 0 && world.WhyNoMine(state) is null && MineHere(hero, enemies, blasting))
            Issue(new WiredSpecialCommand(side));
        else if ((special & ChassisSpecial.Locate) != 0 && world.WhyNoLocate(state) is null)
        {
            var reach = world.ScannerReach(hero);
            if (enemies.Exists(e => Vector2.DistanceSquared(e.Position, hero.Position) <= reach * reach && world.LocatedFor(e.Id) <= LocateAgain))
                Issue(new WiredSpecialCommand(side));
        }
    }

    /// <summary>
    /// Whether a spy lays a mine where it stands: an enemy hero on the ground has come within <see cref="MineNear"/>, no
    /// mine of its race lies within <see cref="MineApart"/> (a heap of mines goes off at once under one machine), and its
    /// own fire would not set it off: no enemy hero stands within twice the burst of its rounds (a burst sets off a mine
    /// at twice its reach), and no blast of its race, running or set off in this look, reaches the place.
    /// </summary>
    private bool MineHere(Machine hero, List<Machine> enemies, bool blasting)
    {
        if (blasting || !enemies.Exists(e => e.Stats.Locomotion != Locomotion.Glider && Vector2.DistanceSquared(e.Position, hero.Position) <= MineNear * MineNear))
            return false;
        foreach (var mine in world.LandMines)
        {
            if (mine.Race == side && Vector2.DistanceSquared(mine.Position, hero.Position) < MineApart * MineApart)
                return false;
        }
        var burst = 0f;
        foreach (var weapon in hero.Weapons)
        {
            if (weapon.DealsDamage)
                burst = MathF.Max(burst, GameWorld.BurstOf(weapon.Spec.TypeName).Radius);
        }
        if (enemies.Exists(e => Vector2.DistanceSquared(e.Position, hero.Position) <= 4 * burst * burst))
            return false;
        foreach (var blast in world.Blasts)
        {
            var reach = blast.Waves.Max(w => w.ToRadius);
            if (blast.By == side && Vector2.DistanceSquared(blast.Position, hero.Position) <= reach * reach)
                return false;
        }
        return true;
    }

    /// <returns>Whether it set off a vortex or an ion strike, whose blast a mine laid now could lie in.</returns>
    private bool UseAbilities(WiredState state, Machine hero, List<Machine> enemies)
    {
        var blasting = false;
        for (var slot = 0; slot < state.Loadout.Abilities.Count; slot++)
        {
            if (state.Loadout.Abilities[slot] is { } item && AimFor(item.Catalogue, hero, enemies) is { } aim && world.WhyNoAbility(state, slot, aim) is null)
            {
                Issue(new WiredAbilityCommand(side, slot, aim));
                blasting |= item.Catalogue is "vortex" or "ionstrike";
            }
        }
        return blasting;
    }

    /// <summary>Where an ability would be worth using now, or null. Treachery cannot turn a hero and is never used.</summary>
    private static Vector2? AimFor(string ability, Machine hero, List<Machine> enemies) => ability switch
    {
        "discharge" => Nearest(enemies, hero.Position, 12) is not null ? hero.Position : null,
        "supercharge" => hero.HitPoints < hero.MaxHitPoints * 0.6f && Nearest(enemies, hero.Position, FightReach) is not null ? hero.Position : null,
        "minefield" => Nearest(enemies, hero.Position, 60)?.Position,
        "vortex" => Nearest(enemies, hero.Position, 85)?.Position,
        "ionstrike" => Nearest(enemies, hero.Position, 300)?.Position,
        _ => null,
    };
}
