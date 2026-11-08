using Godot;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.WiredBench;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// The driver in the field: every half second it asks the bot what to do and does it with the keys and the mouse, a target
// in the sight with the triggers held, a place to go along a way round what stands in it. Squad orders, the dash on a long
// way and giving up at a wave are its own; everything else is the bot's.
public partial class WiredDriver
{
    /// <summary>The bot is asked again every so many ticks, as often as the bench asks it.</summary>
    private const int ThinkEvery = 10;

    /// <summary>The most the view turns in a frame, radians: a quick hand, not a jump.</summary>
    private const float TurnPerFrame = 0.3f;

    /// <summary>How far a way goes before the dash is worth it, metres.</summary>
    private const float DashFrom = 45;

    /// <summary>Seconds between two orders to attack, so that the squad gets to it.</summary>
    private const double OrderEvery = 6;

    private static readonly KeyMap.Act[] TriggerActs = [KeyMap.Act.FireLeft, KeyMap.Act.FireRight, KeyMap.Act.FireTop];

    private Bot _bot = null!;
    private readonly List<Command> _orders = [];
    private readonly HashSet<Key> _keys = [];
    private readonly HashSet<MouseButton> _buttons = [];
    private readonly Dictionary<KeyMap.Act, double> _pulled = [];
    private long _thinkAt;

    // What the bot wants now, and what it asked for once.
    private int? _target;
    private NVector2? _place;
    private (NVector2 Way, string Why)? _dash;
    private (int Slot, NVector2 Aim)? _ability;
    private bool _hangar;
    private KeyMap.Act? _squad;

    // The way being driven, and how the driving goes.
    private List<NVector2> _way = [];
    private NVector2 _wayTo;
    private double _wayAt, _backUntil, _asideUntil, _lastDash, _lastAttackOrder, _lastGetIn, _blocked;
    private (double At, NVector2 Where)? _stuck;
    private NVector2 _aside;
    private int _stucks;
    private bool _forward;
    private int? _orderedAt;
    private int _heldAt = -1;

    // The save and the load in the middle of the run.
    private bool _saved, _loaded;
    private float? _savedAt;
    private string? _saveName;

    private static double Now => Time.GetTicksMsec() / 1000.0;

    private static float Wrap(float angle) => Mathf.Wrap(angle, -Mathf.Pi, Mathf.Pi);

    private static float Heading(NVector2 from, NVector2 to) => MathF.Atan2(to.Y - from.Y, to.X - from.X);

    /// <summary>
    /// One match of the run, frame by frame, until its end screen is up (true) or a load leaves it (false).
    /// </summary>
    private async Task<bool> Drive(Game game)
    {
        var world = game.World;
        LetGo();
        _bot = new Bot(world, world.Data, Shopping, Game.Player, _orders.Add);
        (_thinkAt, _target, _place, _dash, _ability, _hangar, _squad) = (0, null, null, null, null, false, null);
        _way = [];
        game.Ticked += () => Ticked(game);
        await Until(() => game.Driven is not null || world.Tick > 40, 30);
        Say($"in the run{(_loaded ? " from the save" : "")}: {game.RunReport}");
        await Moment(_loaded ? "load" : "start");
        while (true)
        {
            await Frames(1);
            if (!IsInstanceValid(game) || Scene != game)
            {
                LetGo();
                return false;
            }
            await Pictures();
            if (WiredEndScreen.Current is not null)
            {
                LetGo();
                return true;
            }
            var run = world.Wired!;
            if (run.Phase == WiredPhase.Ended || game.MatchOver || game.MenuShown is not null)
            {
                LetGo();
                continue;
            }
            if (game.HangarShown is { } page)
            {
                LetGo();
                await Shop(game, page);
                continue;
            }
            if (plan.SaveAfter is { } wave && !_saved && run.BetweenWaves && run.Wave >= wave)
            {
                await Save(game);
                continue;
            }
            if (_savedAt is { } savedAt && !_loaded && plan.LoadAfter > 0 && world.Time - savedAt >= plan.LoadAfter)
            {
                await Load(game);
                continue;
            }
            if (game.HeroDown || world.HeroOf(Game.Player) is null)
            {
                LetGo();
                continue;
            }
            if (game.Driven is null)
            {
                LetGo();
                await GetIn(game);
                continue;
            }
            await Pilot(game, run);
        }
    }

    /// <summary>After every tick: the world's state now and then, and what happened that the driver answers or takes a picture of.</summary>
    private void Ticked(Game game)
    {
        Report(game);
        var world = game.World;
        if (plan.Shots.Contains($"t{world.Tick}"))
            _pictures.Enqueue($"t{world.Tick}");
        foreach (var e in world.Events)
        {
            switch (e.Kind)
            {
                case GameEventKind.WiredWaveStarted:
                    var wave = e.Other ?? 0;
                    Say($"tick {world.Tick}: wave {wave} begins, {world.Wired!.EnemiesLeft} foes{(e.Detail is { Length: > 0 } boss ? $", boss {boss}" : "")}");
                    _squad = KeyMap.Act.SquadFollow;
                    if (wave >= plan.GiveUp)
                        Say($"tick {world.Tick}: from wave {plan.GiveUp} on the driver gives up: no more fire, into the enemy");
                    Queue(wave >= plan.GiveUp ? "lose" : "wave", $"wave{wave}");
                    break;
                case GameEventKind.WiredWaveCleared:
                    Say($"tick {world.Tick}: wave {e.Other} cleared, bonus {e.Detail}");
                    _squad = KeyMap.Act.SquadBack;
                    break;
                case GameEventKind.WiredBossAppeared:
                    Say($"tick {world.Tick}: boss {e.Detail}");
                    Queue("boss", "boss");
                    break;
                case GameEventKind.WiredChipChoiceReady when e.Other == (int)Game.Player:
                    Say($"tick {world.Tick}: chips to choose from");
                    break;
                case GameEventKind.WiredHeroDied when e.Other == (int)Game.Player:
                    Say($"tick {world.Tick}: the hero fell, {e.Detail} spare bodies left");
                    Queue("down", "down");
                    break;
                case GameEventKind.WiredHeroRespawned when e.Other == (int)Game.Player:
                    Say($"tick {world.Tick}: a new body at the pod, {e.Detail} spare bodies left");
                    Queue("body", "body");
                    break;
                case GameEventKind.WiredWreckCollected:
                    Say($"tick {world.Tick}: wreck taken up, +{e.Detail} BMU");
                    break;
                case GameEventKind.WiredCrateCollected:
                    Say($"tick {world.Tick}: crate taken up: {e.Detail}");
                    break;
                case GameEventKind.WiredAbilityUsed:
                    Say($"tick {world.Tick}: ability {e.Detail} went off");
                    Queue("ability", $"ability-{e.Detail}");
                    break;
                case GameEventKind.WiredDashed:
                    Say($"tick {world.Tick}: the thrusters fired");
                    break;
                case GameEventKind.WiredRunEnded:
                    Say($"tick {world.Tick}: the run is over: {e.Detail}");
                    break;
            }
        }
    }

    private void Queue(string moment, string name)
    {
        if (plan.Shoots(moment))
            _pictures.Enqueue(name);
    }

    /// <summary>Back into the hero: the key for first person, as a player presses it.</summary>
    private async Task GetIn(Game game)
    {
        if (Now - _lastGetIn < 1.5)
            return;
        _lastGetIn = Now;
        await Tap(KeyMap.Of(KeyMap.Act.FirstPerson));
        await Frames(3);
        Say($"pressed {KeyMap.Name(KeyMap.Act.FirstPerson)} to drive the hero again: driven {game.Driven?.Id.ToString() ?? "no"}");
    }

    /// <summary>One frame of driving.</summary>
    private async Task Pilot(Game game, WiredRun run)
    {
        var world = game.World;
        var hero = game.Driven!;
        var givingUp = run.Phase == WiredPhase.Wave && run.Wave >= plan.GiveUp;
        if (world.Tick >= _thinkAt)
            Think(world, givingUp);
        if (_hangar)
        {
            _hangar = false;
            await OpenHangar(game, run);
            return;
        }
        if (_squad is { } act)
        {
            _squad = null;
            await Order(game, act);
            return;
        }
        if (_dash is { } dash)
        {
            _dash = null;
            await Dash(game, dash.Way, dash.Why);
            return;
        }
        if (_ability is { } ability)
        {
            _ability = null;
            await UseAbility(game, hero, ability.Slot, ability.Aim);
            return;
        }
        if (givingUp)
            Charge(game, hero, run);
        else if (_target is { } id && world.Find<Machine>(id) is { IsDead: false } target)
            Attack(game, hero, target);
        else if (_place is { } place)
        {
            Fire(game, hero, 0, false);
            GoTo(game, hero, place, 2);
        }
        else
            Idle(game);
    }

    /// <summary>
    /// The bot's look at the world, its commands caught and kept as what the driver is to do. Giving up, the driver still
    /// uses its abilities and dashes, but goes and fights no more as the bot says.
    /// </summary>
    private void Think(GameWorld world, bool givingUp)
    {
        _thinkAt = world.Tick + ThinkEvery;
        _orders.Clear();
        _bot.Think();
        (_target, _place) = (null, null);
        foreach (var order in _orders)
        {
            switch (order)
            {
                case AttackCommand or MoveCommand when givingUp:
                    break;
                case AttackCommand attack:
                    _target = attack.Target;
                    break;
                case MoveCommand move:
                    _place = move.Target;
                    break;
                case WiredDashCommand dash:
                    _dash = (dash.Direction, "out of a strike, the bot's");
                    break;
                case WiredAbilityCommand ability:
                    _ability = (ability.Slot, ability.Aim);
                    break;
                case WiredHangarCommand { Open: true }:
                    _hangar = true;
                    break;
                default:
                    Say($"the bot asked in the field for {order}");
                    break;
            }
        }
    }

    // ---- going ------------------------------------------------------------------------------------

    /// <summary>Towards a place, along a way round what stands in it, with the view on the way; true once near enough.</summary>
    private bool GoTo(Game game, Machine hero, NVector2 place, float near, bool look = true)
    {
        if (NVector2.Distance(hero.Position, place) <= near)
        {
            Stop(hero);
            return true;
        }
        if (Now < _backUntil)
        {
            Hold(KeyMap.Act.Forward, false);
            Hold(KeyMap.Act.Back, true);
            return false;
        }
        Hold(KeyMap.Act.Back, false);
        var next = Now < _asideUntil ? hero.Position + _aside * 10 : Waypoint(game.World, hero, place);
        var heading = Heading(hero.Position, next);
        if (look)
            Look(game, heading, -0.08f);
        TurnHull(hero, heading);
        var off = MathF.Abs(Wrap(heading - hero.Facing));
        _forward = off < (_forward ? 0.7f : 0.45f);
        Hold(KeyMap.Act.Forward, _forward);
        Unstick(hero);
        // The driver's own: on a long way the thrusters, where it has them and they are ready.
        if (_forward && off < 0.15f && Remaining(hero) > DashFrom && Now - _lastDash > 8 && CanDash(game.World))
        {
            _lastDash = Now;
            _dash = (new NVector2(MathF.Cos(heading), MathF.Sin(heading)), "a long way, the driver's own");
        }
        return false;
    }

    private float Remaining(Machine hero)
    {
        var (sum, from) = (0f, hero.Position);
        foreach (var point in _way)
            (sum, from) = (sum + NVector2.Distance(from, point), point);
        return sum;
    }

    private static bool CanDash(GameWorld world) =>
        world.Wired!.Heroes[Game.Player] is var state && state.Build(world.Data).DashCooldown is not null && world.Time >= state.DashReadyAt;

    private void Stop(Machine hero)
    {
        _forward = false;
        Hold(KeyMap.Act.Forward, false);
        Hold(KeyMap.Act.Back, false);
        TurnHull(hero, null);
        _stuck = null;
    }

    /// <summary>A machine whose upper body follows the view has its hull turned by the keys; any other follows the view by itself.</summary>
    private void TurnHull(Machine hero, float? heading)
    {
        var off = heading is { } way && hero.CanTurnUpperBody ? Wrap(way - hero.Facing) : 0;
        Hold(KeyMap.Act.TurnRight, off > 0.08f);
        Hold(KeyMap.Act.TurnLeft, off < -0.08f);
    }

    /// <summary>
    /// Driving on the spot for two seconds: back off a little, a few metres to one side (what blocks may be a machine the
    /// map does not show), and find the way again.
    /// </summary>
    private void Unstick(Machine hero)
    {
        if (!_forward)
        {
            _stuck = null;
            return;
        }
        if (_stuck is not { } stuck)
        {
            _stuck = (Now, hero.Position);
            return;
        }
        if (Now - stuck.At < 2)
            return;
        if (NVector2.Distance(stuck.Where, hero.Position) < 1)
        {
            _backUntil = Now + 0.8;
            _asideUntil = _backUntil + 1.5;
            var side = ++_stucks % 2 == 0 ? MathF.PI / 2 : -MathF.PI / 2;
            _aside = new NVector2(MathF.Cos(hero.Facing + side), MathF.Sin(hero.Facing + side));
            _way = [];
            Say($"stuck at {hero.Position.X:0},{hero.Position.Y:0}: backing off");
            if (_stucks <= 3)
                Queue("stuck", $"stuck-{hero.Position.X:0}-{hero.Position.Y:0}");
        }
        _stuck = (Now, hero.Position);
    }

    /// <summary>The next point of the way to a place, found again when the place moves or every few seconds.</summary>
    private NVector2 Waypoint(GameWorld world, Machine hero, NVector2 place)
    {
        if (_way.Count == 0 || NVector2.Distance(_wayTo, place) > 4 || Now - _wayAt > 3)
        {
            _way = FindWay(world.Nav, hero.Position, place);
            (_wayTo, _wayAt) = (place, Now);
        }
        while (_way.Count > 1 && NVector2.Distance(_way[0], hero.Position) < 3)
            _way.RemoveAt(0);
        return _way.Count > 0 ? _way[0] : place;
    }

    /// <summary>
    /// A way over the free cells of the ground, straightened: the driver's own reading of the map, as a player sees where
    /// the buildings stand. The world's own search is not asked, as it keeps searches shared within a tick.
    /// </summary>
    private static List<NVector2> FindWay(NavGrid nav, NVector2 from, NVector2 to)
    {
        if (Clear(nav, from, to))
            return [to];
        var width = nav.Width;
        var (start, goal) = (Index(nav.Cell(from)), Index(nav.Cell(to)));
        float Guess(int cell) => NVector2.Distance(new NVector2(cell % width, cell / width), new NVector2(goal % width, goal / width));
        var cost = new Dictionary<int, float> { [start] = 0 };
        var came = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, Guess(start));
        var (best, nearest) = (start, Guess(start));
        for (var expanded = 0; expanded < 40_000 && open.TryDequeue(out var cell, out _); expanded++)
        {
            if (cell == goal)
            {
                best = goal;
                break;
            }
            if (Guess(cell) < nearest)
                (best, nearest) = (cell, Guess(cell));
            var (x, y) = (cell % width, cell / width);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (nx, ny) = (x + dx, y + dy);
                    var next = ny * width + nx;
                    if (dx == 0 && dy == 0 || nx < 0 || ny < 0 || nx >= width || ny >= nav.Depth || !nav.IsFree(nx, ny, false) && next != goal)
                        continue;
                    if (dx != 0 && dy != 0 && (!nav.IsFree(x + dx, y, false) || !nav.IsFree(x, y + dy, false)))
                        continue;
                    var through = cost[cell] + (dx != 0 && dy != 0 ? 1.4142f : 1);
                    if (cost.TryGetValue(next, out var known) && known <= through)
                        continue;
                    cost[next] = through;
                    came[next] = cell;
                    open.Enqueue(next, through + Guess(next));
                }
            }
        }
        var cells = new List<int>();
        for (var at = best; at != start; at = came[at])
            cells.Add(at);
        cells.Reverse();
        var points = cells.Select(c => nav.Centre(c % width, c / width)).ToList();
        if (best == goal && points.Count > 0)
            points[^1] = to;
        // Straightened: from each point on to the furthest one in a clear line.
        var way = new List<NVector2>();
        var anchor = from;
        for (var i = 0; i < points.Count;)
        {
            var furthest = i;
            for (var j = points.Count - 1; j > i; j--)
            {
                if (Clear(nav, anchor, points[j]))
                {
                    furthest = j;
                    break;
                }
            }
            way.Add(points[furthest]);
            anchor = points[furthest];
            i = furthest + 1;
        }
        return way.Count > 0 ? way : [to];

        int Index((int X, int Y) c) => c.Y * width + c.X;
    }

    /// <summary>Whether a straight drive crosses no blocked cell, leaving out the first metres (the hero may stand by a wall).</summary>
    private static bool Clear(NavGrid nav, NVector2 a, NVector2 b)
    {
        var length = NVector2.Distance(a, b);
        for (var at = 3f; at < length; at += NavGrid.CellSize / 2)
        {
            if (!nav.IsFree(NVector2.Lerp(a, b, at / length), false))
                return false;
        }
        return true;
    }

    // ---- fighting ---------------------------------------------------------------------------------

    /// <summary>Closes in to a good distance, keeps the sight on the target and holds the triggers of what reaches it.</summary>
    private void Attack(Game game, Machine hero, Machine target)
    {
        var guns = game.GunsOf(hero).Where(g => g.Weapon is not null).ToList();
        var reach = guns.Count > 0 ? guns.Max(g => g.Weapon!.Spec.Range) : 0;
        var distance = NVector2.Distance(hero.Position, target.Position);
        var sighted = game.CrosshairSight == Crosshair.Sight.Enemy;
        _blocked = sighted ? 0 : _blocked + GetProcessDeltaTime();
        var centre = game.CentreOf(target.Id);
        if (distance > reach * 0.8f || _blocked > 2.5)
        {
            // On the way the sight stays on the target as long as the way leads at it.
            var next = Waypoint(game.World, hero, target.Position);
            var straight = MathF.Abs(Wrap(Heading(hero.Position, next) - Heading(hero.Position, target.Position))) < 0.5f;
            GoTo(game, hero, target.Position, 4, look: !straight || centre is null);
            if (straight && centre is { } aim)
                LookAt(game, aim);
        }
        else
        {
            Stop(hero);
            TurnHull(hero, Heading(hero.Position, target.Position));
            if (centre is { } aim)
                LookAt(game, aim);
        }
        Fire(game, hero, distance, sighted);
        if (sighted && _orderedAt != target.Id && Now - _lastAttackOrder > OrderEvery && game.World.Escorts(Game.Player).Any())
        {
            (_orderedAt, _lastAttackOrder) = (target.Id, Now);
            _squad = KeyMap.Act.SquadAttack;
        }
    }

    /// <summary>The triggers of the guns that reach, held while the crosshair rests on an enemy; the others let go.</summary>
    private void Fire(Game game, Machine hero, float distance, bool sighted)
    {
        var wanted = new HashSet<KeyMap.Act>();
        if (sighted)
        {
            foreach (var place in game.GunsOf(hero))
            {
                if (place.Weapon is { } weapon && distance <= weapon.Spec.Range * 0.98f)
                    wanted.UnionWith(place.Triggers);
            }
        }
        foreach (var act in TriggerActs)
        {
            var want = wanted.Contains(act);
            // A trigger the game let go of (the window lost the focus) is pressed again.
            if (want && _pulled.TryGetValue(act, out var since) && Now - since > 0.4 && !game.TriggerDown(TriggerOf(act)))
            {
                Hold(act, false);
                continue;
            }
            Hold(act, want);
        }
    }

    private static Mounting TriggerOf(KeyMap.Act act) => act switch
    {
        KeyMap.Act.FireLeft => Mounting.Left,
        KeyMap.Act.FireRight => Mounting.Right,
        _ => Mounting.Top,
    };

    /// <summary>Giving up: no more fire, straight at the nearest machine of the wave until the hero falls.</summary>
    private void Charge(Game game, Machine hero, WiredRun run)
    {
        Fire(game, hero, 0, false);
        var foe = run.Foes.Select(f => f.Machine).Where(m => !m.IsDead).OrderBy(m => NVector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).FirstOrDefault();
        if (foe is null)
            Idle(game);
        else
            GoTo(game, hero, foe.Position, 3);
    }

    private void Idle(Game game)
    {
        Fire(game, game.Driven!, 0, false);
        Stop(game.Driven!);
        Look(game, game.AimYaw, -0.05f);
    }

    // ---- the view ---------------------------------------------------------------------------------

    /// <summary>The mouse moves the view towards a heading and a pitch, as far as a hand does in a frame.</summary>
    private void Look(Game game, float yaw, float pitch)
    {
        var turn = Math.Clamp(Wrap(yaw - game.AimYaw), -TurnPerFrame, TurnPerFrame);
        var tilt = Math.Clamp(pitch - game.LookPitch, -TurnPerFrame, TurnPerFrame);
        if (MathF.Abs(turn) < 0.002f && MathF.Abs(tilt) < 0.002f)
            return;
        var step = Game.LookStep;
        // The mouse looks down as it comes back, unless the options reverse it.
        var down = GameOptions.ReverseMouse ? 1 : -1;
        Input.ParseInputEvent(new InputEventMouseMotion { Relative = new Vector2(turn / step, tilt / (down * step)), Position = ScreenMiddle, GlobalPosition = ScreenMiddle });
    }

    private void LookAt(Game game, Vector3 point)
    {
        var to = point - game.Eye;
        Look(game, MathF.Atan2(to.Z, to.X), MathF.Atan2(to.Y, new Vector2(to.X, to.Z).Length()));
    }

    // ---- one press --------------------------------------------------------------------------------

    /// <summary>The view along the way, then the dash's key: it throws the hero the way the view looks.</summary>
    private async Task Dash(Game game, NVector2 way, string why)
    {
        var yaw = MathF.Atan2(way.Y, way.X);
        Hold(KeyMap.Act.Back, false);
        for (var frame = 0; frame < 30 && game.Driven is not null && MathF.Abs(Wrap(yaw - game.AimYaw)) > 0.05f; frame++)
        {
            Look(game, yaw, game.LookPitch);
            await Frames(1);
        }
        if (game.Driven is null)
            return;
        await Tap(KeyMap.Of(KeyMap.Act.Dash));
        Say($"dash ({why}), pressed {KeyMap.Name(KeyMap.Act.Dash)}");
        Queue("dash", "dash");
    }

    /// <summary>An ability where the bot aims it: the sight on the machine there first, then its key.</summary>
    private async Task UseAbility(Game game, Machine hero, int slot, NVector2 aim)
    {
        var world = game.World;
        var item = world.Wired!.Heroes[Game.Player].Loadout.Abilities.ElementAtOrDefault(slot);
        if (NVector2.Distance(aim, hero.Position) > 2)
        {
            var foe = world.Machines.Where(m => !m.IsDead && world.AreEnemies(Game.Player, m.Race) && NVector2.Distance(m.Position, aim) < 5)
                .OrderBy(m => NVector2.DistanceSquared(m.Position, aim)).FirstOrDefault();
            for (var frame = 0; frame < 45 && game.Driven is not null; frame++)
            {
                var point = foe is not null && game.CentreOf(foe.Id) is { } centre ? centre : ActorView.World(aim);
                LookAt(game, point);
                await Frames(1);
                var to = point - game.Eye;
                if (foe is not null && game.CrosshairSight == Crosshair.Sight.Enemy || MathF.Abs(Wrap(MathF.Atan2(to.Z, to.X) - game.AimYaw)) < 0.02f)
                    break;
            }
        }
        if (game.Driven is null)
            return;
        var act = slot == 0 ? KeyMap.Act.Ability1 : KeyMap.Act.Ability2;
        await Tap(KeyMap.Of(act));
        Say($"ability {slot + 1} {item?.Catalogue}: pressed {KeyMap.Name(act)}, aimed at {aim.X:0},{aim.Y:0}");
    }

    /// <summary>An order to the squad by its key; with none to order there is nothing to press.</summary>
    private async Task Order(Game game, KeyMap.Act act)
    {
        var squad = game.World.Escorts(Game.Player).Count();
        if (squad == 0)
            return;
        await Tap(KeyMap.Of(act));
        Say($"squad of {squad}: {act} by {KeyMap.Name(act)}");
    }

    /// <summary>At the pod: the squad holds where it is, and the hangar's key.</summary>
    private async Task OpenHangar(Game game, WiredRun run)
    {
        LetGo();
        if (_heldAt != run.Wave && game.World.Escorts(Game.Player).Any())
        {
            _heldAt = run.Wave;
            await Order(game, KeyMap.Act.SquadHold);
        }
        await Tap(KeyMap.Of(KeyMap.Act.Hangar));
        var page = await Until(() => game.HangarShown, 2);
        Say($"pressed {KeyMap.Name(KeyMap.Act.Hangar)} at the pod: {(page is null ? "no hangar" : "the hangar is up")}");
    }

    // ---- the keys ---------------------------------------------------------------------------------

    /// <summary>An action's key or button held down or let go, through the input as the system sends it.</summary>
    private void Hold(KeyMap.Act act, bool down)
    {
        if (KeyMap.Button(act) is { } button)
            HoldButton(button, down);
        else
            HoldKey(KeyMap.Of(act), down);
        if (!down)
            _pulled.Remove(act);
        else if (!_pulled.ContainsKey(act))
            _pulled[act] = Now;
    }

    private void HoldKey(Key key, bool down)
    {
        if (down ? _keys.Add(key) : _keys.Remove(key))
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = down });
        // A key the system let go of (the window lost the focus) is pressed again.
        else if (down && !Input.IsKeyPressed(key))
            Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
    }

    private void HoldButton(MouseButton button, bool down)
    {
        if (!(down ? _buttons.Add(button) : _buttons.Remove(button)))
            return;
        var mask = _buttons.Aggregate((MouseButtonMask)0, (all, b) => all | (MouseButtonMask)(1 << ((int)b - 1)));
        Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex = button, Pressed = down, ButtonMask = mask, Position = ScreenMiddle, GlobalPosition = ScreenMiddle });
    }

    /// <summary>Everything held is let go.</summary>
    private void LetGo()
    {
        foreach (var key in _keys.ToList())
            HoldKey(key, false);
        foreach (var button in _buttons.ToList())
            HoldButton(button, false);
        _pulled.Clear();
        (_forward, _stuck, _blocked) = (false, null, 0);
    }

    /// <summary>A key pressed and let go on the field.</summary>
    private async Task Tap(Key key)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Wait(0.08);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        await Frames(2);
    }
}
