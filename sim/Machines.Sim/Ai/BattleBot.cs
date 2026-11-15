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
/// until all is quiet there for a while, then attacks itself. It sees only the world,
/// thinks inside the world's tick every <see cref="ThinkEvery"/> ticks, and issues through <see cref="GameWorld.Issue"/>,
/// or through <paramref name="issue"/> for a driver that turns its orders into input; all it keeps follows from tick 0.
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
        UseAbilities(state, hero, enemies);

        var mates = run.Teams[team].Order().ToList();
        var defender = mates.IndexOf(side) > 0;
        NoteQuiet(ownPod, enemies);
        if (defender && Defend(run, team, hero, ownPod, enemies, mates))
            return;
        Attack(run, team, hero, enemies);
    }

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
            Fight(hero, intruder);
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
        // Guard the pod: the mate is off taking the other one, or has fallen.
        var guard = HoldPoint(ownPod, enemyPod?.Position ?? hero.Position, mates.IndexOf(side), hero.Radius);
        if (Nearest(enemies, hero.Position, FightReach) is { } near)
            Fight(hero, near);
        else
            Go(hero, guard, stand: true);
        return true;
    }

    /// <summary>The attack: the nearest enemy hero close by, else the enemy pod, else the hunt.</summary>
    private void Attack(BattleRun run, int team, Machine hero, List<Machine> enemies)
    {
        if (Nearest(enemies, hero.Position, FightReach) is { } near)
        {
            Fight(hero, near);
            return;
        }
        if (run.Options.Win != BattleWin.Wipe && world.BattlePod(1 - team) is { } enemyPod)
        {
            var from = world.BattlePod(team)?.Position ?? hero.Position;
            var index = run.Teams[team].Order().ToList().IndexOf(side);
            Go(hero, HoldPoint(enemyPod, from, index, hero.Radius), stand: true, inCircle: enemyPod);
            return;
        }
        if (Nearest(enemies, hero.Position, float.MaxValue) is { } quarry)
            Fight(hero, quarry);
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
    /// for a few seconds before it takes aim again; the dash helps it close a long way.
    /// </summary>
    private void Fight(Machine hero, Machine target)
    {
        var state = world.Wired!.Heroes[side];
        if (target.Id != _fightTarget)
            (_fightTarget, _fightSince, _closeInUntil) = (target.Id, world.Time, 0);
        if (world.Time < _closeInUntil && hero.DistanceTo(target) > CloseEnough)
        {
            if (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, target.Position) > Moved * 3)
                Issue(new MoveCommand(side, [hero.Id], target.Position));
            return;
        }
        if (world.Time - MathF.Max(state.LastShotAt, _fightSince) > StallTime && hero.DistanceTo(target) > CloseEnough)
        {
            (_closeInUntil, _fightSince) = (world.Time + CloseInTime, world.Time + CloseInTime);
            Issue(new MoveCommand(side, [hero.Id], target.Position));
            return;
        }
        if (hero.Order is not AttackOrder attack || attack.Target != target.Id)
            Issue(new AttackCommand(side, [hero.Id], target.Id));
        else if (hero.Weapons.Count > 0 && hero.DistanceTo(target) > hero.Weapons.Max(w => w.Spec.Range) + 40)
            DashTowards(hero, target.Position);
    }

    /// <summary>
    /// Goes to a place and, with <paramref name="stand"/>, stands its ground there, firing at whatever comes in reach. With
    /// <paramref name="inCircle"/> it counts as there once inside that pod's capture circle.
    /// </summary>
    private void Go(Machine hero, Vector2 place, bool stand, Construction? inCircle = null)
    {
        var there = Vector2.Distance(hero.Position, place) <= Arrived
            || inCircle is not null && Vector2.Distance(hero.Position, inCircle.Position) <= BattleBalance.CaptureRadius - 2;
        if (there)
        {
            if (stand && hero.Order is not StandGroundOrder)
                Issue(new StandGroundCommand(side, [hero.Id]));
            return;
        }
        if (hero.Order is not MoveOrder move || Vector2.Distance(move.Target, place) > Moved)
        {
            Issue(new MoveCommand(side, [hero.Id], place));
            return;
        }
        if (Vector2.Distance(hero.Position, place) > DashFrom && hero.PathIndex < hero.Path.Count)
            DashTowards(hero, hero.Path[hero.PathIndex]);
    }

    /// <summary>Fires the thrusters towards a place, when it has them and they are ready.</summary>
    private void DashTowards(Machine hero, Vector2 place)
    {
        var state = world.Wired!.Heroes[side];
        if (state.Build(world.Data).DashCooldown is null || world.Time < state.DashReadyAt)
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

    private void UseAbilities(WiredState state, Machine hero, List<Machine> enemies)
    {
        for (var slot = 0; slot < state.Loadout.Abilities.Count; slot++)
        {
            if (state.Loadout.Abilities[slot] is { } item && AimFor(item.Catalogue, hero, enemies) is { } aim && world.WhyNoAbility(state, slot, aim) is null)
                Issue(new WiredAbilityCommand(side, slot, aim));
        }
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
