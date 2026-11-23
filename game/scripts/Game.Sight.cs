using Godot;
using Machines.Net;
using Machines.Sim.World;
using Machines.Sim.Survival;
using NVector2 = System.Numerics.Vector2;
using NVector3 = System.Numerics.Vector3;

namespace Machines.Game;

// Wired's sighted driver (the Modern layout in a run or a battle): every command says where the view looks, and the world
// turns the gun there at its own rate (GameWorld.AimGuns). The view draws two marks: the gun sight where the gun will point
// once the commands on their way are carried out (GunPrediction), with the original's bitmap, its colour, its sounds and
// the spread marks; and a dot in the middle where the view looks, with the bars of what it rests on. Held triggers wait for
// the gun unless the options say otherwise. MACHINES_SIGHT=truth|log|flick (comma-separated) draws the world's own gun,
// logs the prediction beside the world's gun tick by tick, or takes the wheel and flicks the view about.
public partial class Game
{
    /// <summary>Whether the driven machine is sighted: Wired, the Modern layout, and a heading to send.</summary>
    private bool Sighted => World.Wired is not null && FreeLook && float.IsFinite(_aimYaw);

    /// <summary>The heading a command carries: where the view looks while sighted, else none.</summary>
    private float? SentYaw => Sighted ? _aimYaw : null;

    // The commands on their way, by the tick each is carried out on, with the hull turn it makes and the yaw it carries: a
    // hull following the view counts the turns, or it would overshoot, and the gun sight replays them all.
    private readonly List<GunPrediction.Due> _turnsDue = [];

    // The gun as it will stand once a command sent now is carried out, and the heading the gun sight is drawn along, which
    // follows it a little faster than the gun turns; for which machine.
    private GunPrediction.Gun _gun;
    private float _gunDrawn;
    private int _gunDrawnFor = -1;

    // What the gun sight and the dot rest on, and the held triggers that wait for the gun.
    private Crosshair.Sight _dotSight;
    private readonly HashSet<KeyMap.Act> _waiting = [];

    private static readonly string[] SightModes = (System.Environment.GetEnvironmentVariable("MACHINES_SIGHT") ?? "")
        .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

    private static bool SightTruth => SightModes.Contains("truth");
    private static bool SightLog => SightModes.Contains("log");
    private static bool SightFlicks => SightModes.Contains("flick");

    /// <summary>Forgets the commands on their way and the drawn gun: a new machine is driven, or none.</summary>
    private void ForgetGun()
    {
        _turnsDue.Clear();
        _gunDrawnFor = -1;
        _waiting.Clear();
    }

    /// <summary>The tick a command queued now is carried out on: the session's next turn, at least the input delay ahead.</summary>
    private long NextDue => Math.Max(World.Tick + _session.InputDelay, _session.NextLocalTurn);

    /// <summary>Ticks a command not landed yet stays past its stamp online: a turn later than this was marked as lagging long before.</summary>
    private const int LateGrace = 40;

    /// <summary>
    /// The commands the world has carried out leave; online one not landed yet stays a while, as it may still land later
    /// (<see cref="GunPrediction.Drop"/>).
    /// </summary>
    private void DropDue() => GunPrediction.Drop(_turnsDue, World.Tick, _session.Mode == SessionMode.Bundles ? LateGrace : 0);

    /// <summary>
    /// Online, the relay placed this player's turn stamped <paramref name="stamp"/> on the bundle of <paramref name="tick"/>
    /// (<see cref="LockstepSession.Landed"/>): a late turn's hull turn is carried out there, not where it was stamped, and the
    /// prediction follows it (P2 design §4.4), so the gun and hull turn foreseen are not off by the turns that came late.
    /// </summary>
    private void Landed(long stamp, long tick) => GunPrediction.Land(_turnsDue, stamp, tick);

    /// <summary>
    /// How a hull without an upper body turns to where the view looks, as fast as it can, counting the turns already on
    /// their way so as to stop there: the command's turn before its sign is put right, and the most a tick turns.
    /// </summary>
    private float FollowTurn(Machine machine, out float step)
    {
        step = HullTurnRate(machine, Input.IsKeyPressed(Key.Ctrl)) * GameWorld.Dt;
        DropDue();
        var coming = GunPrediction.Turning(_turnsDue, World.Tick, Sighted ? NextDue : long.MinValue);
        var behind = Mathf.Wrap(_aimYaw - machine.Facing - coming, -Mathf.Pi, Mathf.Pi);
        // Guns that angle in their arc keep the hull still while the view lies within it, and past it the hull turns on
        // the spot only until the view is back within it, a little inside the edge (GameWorld.GunArc.cs).
        if (GunArc(machine) is > 0 and var arc)
            behind = GameWorld.BeyondArc(behind, arc - Mathf.Min(ArcMargin, arc * 0.2f));
        return -Mathf.Clamp(behind / step, -1, 1);
    }

    /// <summary>How far inside its arc's edge a hull turning after the view leaves the view, radians (a degree), so the guns stay on it.</summary>
    private const float ArcMargin = Mathf.Pi / 180;

    /// <summary>
    /// The arc of the driven machine's narrowest weapon that angles, radians, while it is sighted (<see cref="GameWorld.GunArcOf(Machine)"/>):
    /// within it the hull stays and the guns angle towards the view. 0 for a machine with an upper body, unsighted, or a match without the arcs.
    /// </summary>
    private float GunArc(Machine machine) => Sighted ? World.GunArcOf(machine) : 0;

    /// <summary>The hull turn a command's <paramref name="turn"/> makes in the world, radians, worked out as the world does.</summary>
    private float HullTurnFor(Machine machine, float turn, bool slow, bool hasYaw) =>
        GameWorld.HullTurnOf(Math.Clamp(turn, -1, 1), World.Options.OriginalMotion ? World.DrivenTurnRate(machine, slow, hasYaw)
            : World.Data.Parm.General.FirstPersonMaxFastTurnRate);

    /// <summary>
    /// The gun as it will stand after the commands on their way and <paramref name="next"/>; with <paramref name="truth"/>
    /// the world's own gun as it stands.
    /// </summary>
    private GunPrediction.Gun PredictGun(Machine machine, GunPrediction.Due? next, bool truth = false)
    {
        if (truth)
        {
            var heading = World.GunHeading(machine);
            return new GunPrediction.Gun(machine.Facing, World.HeadOf(machine.Id), heading,
                MathF.IEEERemainder(heading - _aimYaw, 2 * MathF.PI), World.OnSight(machine.Id));
        }
        DropDue();
        (float, float)? carried = World.DriveInput.TryGetValue(machine.Id, out var input)
            ? (HullTurnFor(machine, input.Turn, input.Slow, World.HasYaw(machine.Id)), World.PlayerYaws.TryGetValue(machine.Id, out var yaw) ? yaw : float.NaN)
            : null;
        return GunPrediction.Predict(machine.Facing, World.HeadOf(machine.Id), GameWorld.HeadStep(machine), World.Busy(machine),
            machine.Inside is not null, World.Tick, _turnsDue, tick => World.Battle?.InOpening(tick * GameWorld.Dt) == true, next, carried, GunArc(machine));
    }

    /// <summary>
    /// A command with the view's heading goes out: it joins those on their way with the hull turn it makes (its
    /// <paramref name="turn"/> as sent), and the gun is foreseen as it will stand once it is carried out.
    /// </summary>
    private GunPrediction.Gun SendingGun(Machine machine, float turn)
    {
        var due = NextDue;
        DropDue();
        GunPrediction.Add(_turnsDue, new GunPrediction.Due(due, HullTurnFor(machine, turn, Input.IsKeyPressed(Key.Ctrl), true), _aimYaw));
        var gun = PredictGun(machine, null);
        if (SightLog)
            GD.Print($"sight: tick {due} predicted on {gun.On} gun {gun.Heading:0.00000}");
        return gun;
    }

    /// <summary>The turn the next command will carry, as <see cref="UpdateDriving"/> works it out (before its sign is put right).</summary>
    private float NextTurn(Machine machine) =>
        ScreenOver ? 0 : machine.CanTurnUpperBody || GunArc(machine) > 0 && KeyTurn(machine) != 0 ? KeyTurn(machine) : FollowTurn(machine, out _);

    /// <summary>
    /// The held weapons whose bits go into the command, with "Fire waits for the gun" (<see cref="GunPrediction.Gate"/>);
    /// a held trigger none of whose weapons goes waits, and the weapons panel shows it dimmed.
    /// </summary>
    private int WaitForGun(Machine machine, int held, GunPrediction.Gun gun)
    {
        _waiting.Clear();
        var now = Time.GetTicksMsec() / 1000.0;
        var enemy = _hud.Crosshair.Aim == Crosshair.Sight.Enemy;
        var hullLag = MathF.IEEERemainder(gun.Facing - _aimYaw, 2 * MathF.PI);
        var places = WeaponTriggers.Places(machine.Weapons);
        var (sent, going) = (0, new HashSet<KeyMap.Act>());
        for (var i = 0; i < machine.Weapons.Count && i < 31; i++)
        {
            if ((held & (1 << i)) == 0)
                continue;
            var weapon = machine.Weapons[i];
            var triggers = WeaponTriggers.Of(weapon.Mounting, places);
            var pressed = triggers.Max(_hands.PressedAt);
            // A weapon that angles is on while the view lies within its own arc, which may be wider than the narrowest's.
            var on = World.GunArcOf(machine, weapon.Spec) is > 0 and var arc
                ? machine.Inside is null && MathF.Abs(GameWorld.BeyondArc(hullLag, arc)) <= WiredBalance.GunPlay
                : gun.On;
            var goes = GunPrediction.Gate(GameOptions.FireWaitsForGun, weapon.Spec.TypeName, on, enemy, now - pressed, hullLag);
            if (goes)
                sent |= 1 << i;
            foreach (var trigger in triggers.Where(_hands.Down))
                (goes ? going : _waiting).Add(Trigger(trigger));
        }
        _waiting.ExceptWith(going);
        return sent;
    }

    /// <summary>
    /// Each frame: the gun foreseen with the command this frame would send, and the heading the gun sight is drawn along,
    /// which moves towards it at a quarter more than the gun's rate and takes it once within 0.05° or on the sight. The
    /// driven model's upper body points there.
    /// </summary>
    private void TurnDrawnGun(Machine machine, ActorView view)
    {
        var next = new GunPrediction.Due(NextDue, HullTurnFor(machine, Mathf.Clamp(-NextTurn(machine), -1, 1), Input.IsKeyPressed(Key.Ctrl), true), _aimYaw);
        _gun = PredictGun(machine, next, SightTruth);
        if (_gunDrawnFor != machine.Id)
            (_gunDrawn, _gunDrawnFor) = (_gun.Heading, machine.Id);
        var rest = MathF.IEEERemainder(_gun.Heading - _gunDrawn, 2 * MathF.PI);
        var rate = GameWorld.HeadStep(machine) is > 0 and var step ? step / GameWorld.Dt : HullTurnRate(machine, false);
        var most = 1.25f * rate * (float)GetProcessDeltaTime();
        _gunDrawn = _gun.On || MathF.Abs(rest) <= Mathf.DegToRad(0.05f) ? _gun.Heading : _gunDrawn + Mathf.Clamp(rest, -most, most);
        view.GunBearing = _gunDrawn;
        // Guns that angle in their arc stand towards the drawn gun from the hull as it is drawn (its yaw is the facing's negative).
        var arc = GunArc(machine);
        view.GunSwing = arc > 0 ? Mathf.Clamp(Mathf.Wrap(_gunDrawn + view.Yaw, -Mathf.Pi, Mathf.Pi), -arc, arc) : 0;
    }

    /// <summary>
    /// The two marks: the dot in the middle with the bars and brackets of what it rests on, and the gun sight where the drawn
    /// gun points, coloured by what lies on the line of the gun as foreseen (through the camera, at the dot's distance).
    /// Turned off the screen, the sight stands at its edge; while the towers are up only the dot is drawn.
    /// </summary>
    private void ShowGunSight(Machine machine, ActorView view)
    {
        var cross = _hud.Crosshair;
        var size = GetViewport().GetVisibleRect().Size;
        var camera = _firstPersonCamera;
        var reach = machine.Weapons.Select(w => w.Spec.Range).DefaultIfEmpty(0).Max();
        var (seen, met, _, _) = SightPoint(machine);
        var looked = met is not null && InShadow(met) ? null : met;
        cross.Dot = true;
        cross.SightHidden = _towersUp;
        cross.Located = looked is not null && LocatedFoe(looked);
        cross.Target = looked is null ? null
            : (looked.HitPoints / (float)Math.Max(1, looked.MaxHitPoints), looked.Armour / (float)Math.Max(1, looked.MaxArmour));
        cross.Heading = Mathf.RadToDeg(_aimYaw);
        _dotSight = Kind(looked);

        // What the gun's line meets: the dot's point turned about the hero's middle by the gun's lag, seen through the camera.
        var onGun = met;
        var dot = new NVector2(seen.X, seen.Z);
        if (!_gun.On)
        {
            var gunPoint = GameWorld.GunPoint(machine.Position, _gun.Lag, dot);
            var point = new Vector3(gunPoint.X, seen.Y, gunPoint.Y);
            onGun = camera.IsPositionBehind(point) ? null : SightAt(camera.UnprojectPosition(point), reach).Actor;
        }
        if (onGun is not null && InShadow(onGun))
            onGun = null;

        // Where the drawn gun points, from where the model stands in the picture.
        if (_gun.On)
            (cross.At, cross.Clamped) = (null, false);
        else
        {
            var lag = MathF.IEEERemainder(_gunDrawn - _aimYaw, 2 * MathF.PI);
            var drawnPoint = GameWorld.GunPoint(new NVector2(view.GlobalPosition.X, view.GlobalPosition.Z), lag, dot);
            var point = new Vector3(drawnPoint.X, seen.Y, drawnPoint.Y);
            var inset = 0.06f * size.Y;
            if (camera.IsPositionBehind(point))
                (cross.At, cross.Clamped) = (new Vector2(lag > 0 ? size.X - inset : inset, size.Y / 2), true);
            else
            {
                var at = camera.UnprojectPosition(point);
                var edge = OntoScreen(at, size, inset);
                (cross.At, cross.Clamped) = (edge ?? at, edge is not null);
            }
        }
        ShowArc(machine, view, seen, size);
        cross.Show(Kind(onGun), _gun.On);
    }

    /// <summary>
    /// What the sighted driver is told of the hull and the gun beside the two marks: the edges of the guns' arc (the view
    /// past them turns the hull), where the ground stops the gun's line before the point looked at, and from behind the hero
    /// how wide the rounds' cone shows at the point.
    /// </summary>
    private void ShowArc(Machine machine, ActorView view, Vector3 seen, Vector2 size)
    {
        var cross = _hud.Crosshair;
        var camera = _firstPersonCamera;
        var hero = new NVector2(view.GlobalPosition.X, view.GlobalPosition.Z);
        var dot = new NVector2(seen.X, seen.Z);
        var facing = -view.Yaw;
        // The arc's edges at the dot's distance (at least 15 m), where the camera sees them.
        cross.ArcEdges = null;
        if (GunArc(machine) is > 0 and var arc && !_towersUp)
        {
            var away = MathF.Max(15, NVector2.Distance(hero, dot));
            float? Edge(float side)
            {
                var bearing = facing + side * arc;
                var point = new Vector3(hero.X + MathF.Cos(bearing) * away, seen.Y, hero.Y + MathF.Sin(bearing) * away);
                if (camera.IsPositionBehind(point))
                    return null;
                var x = camera.UnprojectPosition(point).X;
                return x > 0 && x < size.X ? x : null;
            }
            cross.ArcEdges = (Edge(1), Edge(-1));
        }
        // The ground between the gun and where it sends its rounds stops them there (GameWorld.Projectiles): marked, as from
        // behind the hero the view sees over crests its gun cannot clear.
        cross.BlockedAt = null;
        if (World.Terrain is { } terrain && !_towersUp)
        {
            var aim = _gun.On ? dot : GameWorld.GunPoint(machine.Position, _gun.Lag, dot);
            var from = World.AimPoint(machine);
            var to = new NVector3(aim.X, aim.Y, seen.Y);
            if (terrain.FirstBlocked(from, to, step: 1.5f, ends: 0) is { } share)
            {
                var hit = NVector3.Lerp(from, to, share);
                var point = new Vector3(hit.X, hit.Z, hit.Y);
                if (!camera.IsPositionBehind(point) && point.DistanceTo(seen) > 2)
                    cross.BlockedAt = camera.UnprojectPosition(point);
            }
        }
        // The rounds' cone opens at the hero; from behind it the camera sees it from further away, narrower.
        cross.SpreadShare = _chaseSpot is { } spot
            ? ChaseCamera.SpreadShare(view.Centre.DistanceTo(seen), spot.Camera.DistanceTo(seen))
            : 1;
    }

    /// <summary>What a sight resting on an actor shows: an enemy, a friend, or nothing.</summary>
    private Crosshair.Sight Kind(Actor? actor) =>
        actor is null ? Crosshair.Sight.None : World.AreEnemies(Player, actor.Race) ? Crosshair.Sight.Enemy : Crosshair.Sight.Friend;

    /// <summary>A point off the screen (less an inset all round) brought onto its edge along the way from the middle; null for one on it.</summary>
    private static Vector2? OntoScreen(Vector2 at, Vector2 size, float inset)
    {
        var middle = size / 2;
        var way = at - middle;
        var room = middle - new Vector2(inset, inset);
        var share = Mathf.Max(Mathf.Abs(way.X) / room.X, Mathf.Abs(way.Y) / room.Y);
        return share <= 1 ? null : middle + way / share;
    }

    // MACHINES_SIGHT's script: when the view last flicked, and which way the next goes.
    private bool _sightHooked;
    private double _flickClock;
    private int _flickWay = 1;

    /// <summary>
    /// MACHINES_SIGHT: with log, the world's gun is printed for every tick stepped; with flick, the player's hero is taken
    /// over as soon as it can be, the view flicks 120° every 3 s, one way and then the other, and between the flicks it
    /// follows the nearest enemy within the weapons' reach.
    /// </summary>
    private void UpdateSightScript(double delta)
    {
        if (!_sightHooked && SightModes.Length > 0)
        {
            _sightHooked = true;
            GD.Print($"sight: modes {string.Join(",", SightModes)}, input delay {_session.InputDelay}");
            if (SightLog)
            {
                Ticked += () =>
                {
                    if (_driven is { } driven && World.HasYaw(driven.Id))
                        GD.Print($"sight: tick {World.Tick - 1} sim on {World.OnSight(driven.Id)} gun {World.GunHeading(driven):0.00000}");
                };
            }
        }
        if (!SightFlicks || Watching || MatchOver)
            return;
        if (_driven is null)
        {
            if (World.HeroOf(Me) is { IsDead: false } hero && !HangarOpen && !_heroDown)
            {
                Select([hero.Id]);
                EnterFirstPerson();
            }
            return;
        }
        if (!Sighted)
            return;
        _flickClock += delta;
        if (_flickClock >= 3)
        {
            _flickClock = 0;
            _aimYaw = Mathf.Wrap(_aimYaw + _flickWay * Mathf.DegToRad(120), -Mathf.Pi, Mathf.Pi);
            _flickWay = -_flickWay;
            GD.Print($"sight: flick at tick {World.Tick}");
            return;
        }
        if (_flickClock < 1.5)
            return;
        var machine = _driven;
        var reach = machine.Weapons.Select(w => w.Spec.Range).DefaultIfEmpty(0).Max();
        var foe = World.Machines.Where(m => !m.IsDead && World.AreEnemies(Player, m.Race) && m.DistanceTo(machine) <= reach)
            .OrderBy(m => NVector2.DistanceSquared(m.Position, machine.Position)).ThenBy(m => m.Id).FirstOrDefault();
        if (foe is null)
            return;
        var way = foe.Position - machine.Position;
        var turn = MathF.IEEERemainder(MathF.Atan2(way.Y, way.X) - _aimYaw, 2 * MathF.PI);
        _aimYaw = Mathf.Wrap(_aimYaw + Mathf.Clamp(turn, -4 * (float)delta, 4 * (float)delta), -Mathf.Pi, Mathf.Pi);
    }
}
