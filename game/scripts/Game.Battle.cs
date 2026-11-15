using Godot;
using Machines.Import.Model;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

// A battle in the match: what the world does is called out, a player whose hero is out for good looks on through a
// team-mate (Tab goes on to the next), and the end goes into the battle account once. In any other match World.Battle is
// null and none of this does anything. Battle is the remake's own addition.
public partial class Game
{
    /// <summary>Seconds from the end of a battle to its end screen: the world stands still for the last of them.</summary>
    private const int BattleHold = 5;

    // The hero the view follows from behind, while the player's own is out of the fight or a bot drives it; where the
    // camera is on its way to, and whether the player left that view for the field.
    private int? _watched;
    private Vector3? _watchFrom;
    private bool _battleRecorded;
    private double _battleRetryAt;
    private readonly BattleCaptureCalls _captureCalls = new();

    /// <summary>
    /// A battle in which a bot plays the player's own race too (MACHINES_BATTLE with bots=4): the view looks on through the
    /// hero, nothing is driven and nothing goes into the account.
    /// </summary>
    public bool BattleWatch => World.Battle is not null && !Online && !Watching
        && MatchRequest.Current.Starts.Players.GetValueOrDefault(Player) == Controller.Ai;

    /// <summary>The machine the view follows in a battle, while it stands.</summary>
    public int? WatchedHero => _watched is { } id && World.Find<Machine>(id) is { IsDead: false } ? id : null;

    /// <summary>The name of the hero the view follows, while it stands.</summary>
    public string? WatchedName => WatchedHero is { } id && World.Find<Machine>(id) is { } machine
        ? World.Battle is { } run ? BattleReadout.HeroName(run, machine.Race) : machine.Race.ToString()
        : null;

    /// <summary>The heroes the view can follow: the team-mates standing, and in a battle a bot plays for the player its own hero first.</summary>
    public IReadOnlyList<Race> Watchable => WatchCandidates().Select(m => m.Race).ToList();

    /// <summary>What the battle that just ended opened: the chassis that came within reach of research.</summary>
    public IReadOnlyList<string> BattleOpened { get; private set; } = [];

    /// <summary>Whether this battle's result is in the account, or waits for the file; false for one not counted here.</summary>
    public bool BattleCounted => _battleRecorded;

    /// <summary>For automated checks: the Wired HUD with its battle readout.</summary>
    internal WiredHud? BattleHud => _hud.Wired;

    private List<Machine> WatchCandidates()
    {
        if (World.Battle is not { } run)
            return [];
        var team = run.TeamOf(Player);
        if (team < 0)
            return [];
        var races = run.Teams[team].Order().Where(race => race != Player).ToList();
        if (BattleWatch)
            races.Insert(0, Player);
        var mates = races.Select(World.HeroOf).OfType<Machine>().ToList();
        // Once it is over, or none of the team stands and none has a body to come (with victory by taking the pod the
        // battle goes on until the other team has taken it), the view may go to a hero of the other team still standing.
        var teamOut = run.Teams[team].All(race => World.HeroOf(race) is null && World.Wired?.Heroes.GetValueOrDefault(race)?.RespawnAt is null);
        return mates.Count > 0 || !run.Over && !teamOut ? mates : run.Teams.SelectMany(t => t.Order()).Select(World.HeroOf).OfType<Machine>().ToList();
    }

    /// <summary>
    /// The view goes behind a hero of the team: the one asked for, or the first standing. False when none stands.
    /// </summary>
    private bool StartWatching(int? prefer = null)
    {
        var candidates = WatchCandidates();
        if (candidates.Count == 0 || Cinematic.Asked is not null)
            return false;
        var target = candidates.FirstOrDefault(m => m.Id == prefer) ?? candidates[0];
        if (_watched is null && _driven is null && !_heroDown)
            _firstPersonFromGround = !ZenithCamera;
        _heroDown = false;
        _watched = target.Id;
        _watchFrom = null;
        _watchLook = null;
        _rig.Active = false;
        _firstPersonCamera.Current = true;
        if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT") is not { Length: > 0 } && System.Environment.GetEnvironmentVariable("MACHINES_BATTLE_SHOTS") is not { Length: > 0 })
            Input.MouseMode = Input.MouseModeEnum.Captured;
        _hud.FirstPerson = true;
        _hud.HeroDown();
        _hud.Radar.Driven = target;
        _screen.Enter();
        return true;
    }

    /// <summary>Back to the field from looking on, over the hero that was followed.</summary>
    private void StopWatching()
    {
        var was = WatchedHero is { } id ? World.Find<Machine>(id) : null;
        _watched = null;
        _watchFrom = null;
        _watchLook = null;
        _rig.Active = true;
        _rig.Camera.Current = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _hud.FirstPerson = false;
        _hud.Radar.Driven = null;
        _screen.Leave();
        UseZenithCamera(!_firstPersonFromGround);
        if (was is not null)
            LookAtActor(was);
    }

    /// <summary>Tab: the next hero of the team the view can follow.</summary>
    private void WatchNext()
    {
        var candidates = WatchCandidates();
        if (candidates.Count < 2)
            return;
        var at = candidates.FindIndex(m => m.Id == _watched);
        var next = candidates[(at + 1) % candidates.Count];
        _audio.PlayGui("gui/sounds/weapchng.wav");
        StartWatching(next.Id);
    }

    /// <summary>
    /// Every frame of a battle the view follows its hero from behind and above, looking where it looks; when that one falls
    /// it goes on to the next standing, or waits where it is until a body comes.
    /// </summary>
    private void FollowWatch(double delta)
    {
        // A vortex or an ion strike right beside the camera that looks on is drawn lighter (GroundBlast.Onlooker).
        GroundBlast.Onlooker = _watched is not null;
        if (_watched is not { } id)
            return;
        if (World.Find<Machine>(id) is not { IsDead: false } machine)
        {
            if (WatchCandidates().FirstOrDefault() is not { } next)
                return;
            _watched = next.Id;
            _hud.Radar.Driven = next;
            _screen.Enter();
            machine = next;
        }
        if (!_views.TryGetValue(machine.Id, out var view))
            return;
        var yaw = view.Yaw - view.UpperBodyNow - Mathf.Pi / 2;
        var ahead = new Vector3(-Mathf.Sin(yaw), 0, -Mathf.Cos(yaw));
        var centre = view.Centre;
        var (want, look) = WatchSpot(machine, centre, ahead);
        // On its way the camera goes round the hero, not through it (or through the pod beside it) on a straight line.
        var t = 1 - Mathf.Exp(-(float)delta * 5);
        // A hero far off (one that came back at its pod, the next one followed) is cut to: on the way round it the camera
        // passed through the pod and showed nothing but its wall.
        if (_watchFrom is { } far && far.DistanceTo(want) > 60)
            (_watchFrom, _watchLook) = (null, null);
        var from = _watchFrom is { } was ? RoundTo(was, want, centre, t) : want;
        from.Y = Mathf.Max(from.Y, GroundAbout(from) + 2);
        look = _watchLook is { } looked ? looked.Lerp(look, t) : look;
        _watchFrom = from;
        _watchLook = look;
        // Wherever the camera is on its way, the hero stays in the middle third of the picture.
        look = Framed(from, look, centre);
        _firstPersonCamera.GlobalPosition = from;
        if (!from.IsEqualApprox(look))
            _firstPersonCamera.LookAt(look, Vector3.Up);
        _firstPersonCamera.Basis *= CameraRig.ShakeAt(from);
        _hud.Radar.Driven = machine;
        _hud.Radar.Head = view.UpperBodyNow;
    }

    /// <summary>
    /// How far behind the watched hero the camera may stand: it stops a metre short of the first building on the way (a hero
    /// at its pod had the camera inside the pod's walls), never nearer than four metres; what it loses it gains in height.
    /// </summary>
    private float Arm(Vector3 centre, Vector3 back, float length)
    {
        var from = new System.Numerics.Vector2(centre.X, centre.Z);
        var way = new System.Numerics.Vector2(back.X, back.Z);
        var near = World.Constructions.Where(c => System.Numerics.Vector2.Distance(c.Position, from) < length + c.Radius + 2).ToList();
        if (near.Count == 0)
            return length;
        for (var step = 0.5f; step <= length; step += 0.5f)
        {
            var at = from + way * step;
            if (near.Any(c => at.X > c.Footprint.Min.X - 1 && at.X < c.Footprint.Max.X + 1 && at.Y > c.Footprint.Min.Y - 1 && at.Y < c.Footprint.Max.Y + 1))
                return Mathf.Max(4, step - 1);
        }
        return length;
    }

    // The places round the hero the camera may stand at: swung from straight behind (0) to straight in front (pi), set off
    // to either side while looking the same way, each also high up looking down, and each also nearer; which one it stood
    // at last, so that it does not hop between two about as good.
    private static readonly float[] WatchSwings = [0, 0.6f, -0.6f, 1.1f, -1.1f, 1.7f, -1.7f, 2.4f, -2.4f, Mathf.Pi];
    private static readonly float[] WatchAside = [0, 5, -5];
    private static int WatchPlaces => WatchSwings.Length * WatchAside.Length * 4;
    private int _watchPick;
    private Vector3? _watchLook;

    // With the ground between the camera and the hero, the camera climbs: how far out it stays (a share of its reach) and how
    // much higher it goes, in this order, until the hero is in sight.
    private static readonly (float Reach, float Rise)[] WatchClimb =
        [(1, 0), (1, 2), (1, 4), (0.7f, 0), (0.7f, 2), (0.7f, 4), (1, 7), (0.45f, 0), (0.45f, 3), (0.7f, 8), (1, 11), (0.45f, 7), (0.7f, 13), (0.45f, 12), (1, 18)];

    /// <summary>
    /// Where the camera stands behind the watched hero and what it looks at. Behind and above it, looking where it looks; but
    /// a hero 7 to 11 m from a pod had the pod's wall over half the picture whether the camera stood behind it or beside it
    /// (taking the enemy pod it looks at the pod; a guard has it at its back), and a hill between the camera and a hero on a
    /// slope hid it. So each place climbs over the ground until the hero is in sight, and when a building is near or the
    /// ground was in the way, each place round the hero is tried, buildings by casting rays through the picture it would
    /// see: the one where buildings cover least and the hero is in clear sight wins, the nearer to straight behind and the
    /// less climbed the better, and the camera keeps its place unless another is clearly better. Whatever it looks at, the
    /// hero stays in the middle third of the picture (<see cref="Framed"/>).
    /// </summary>
    private (Vector3 At, Vector3 Look) WatchSpot(Machine machine, Vector3 centre, Vector3 ahead)
    {
        // A small hero (a grunt, 1.5 m) is followed from nearer and lower: from 12 m behind it was a speck at the foot of
        // the picture, and a ramp beside it filled the rest.
        var full = 3 + machine.Radius * 3.5f;
        var lift = 1.5f + machine.Radius * 1.6f;
        var walls = WallsNear(centre, full + 50);
        (float Swing, float Aside, bool High, bool Near) Of(int pick) =>
            (WatchSwings[pick / (WatchAside.Length * 4)], WatchAside[pick / 4 % WatchAside.Length], pick % 2 == 1, pick / 2 % 2 == 1);
        (Vector3 At, Vector3 Look, float Climbed, bool Hidden) Place(int pick)
        {
            var (swing, aside, high, near) = Of(pick);
            var facing = ahead.Rotated(Vector3.Up, swing);
            var side = facing.Cross(Vector3.Up) * aside;
            // With a building in the way, nearer and higher, but never so high that it looks down on the hero more steeply
            // than about 35 degrees (beside a pod it looked straight down on the hero's back, with no horizon); high up a
            // good deal higher still.
            var arm = Arm(centre + side, -facing, near ? full * 0.75f : full);
            var up = lift + (full - arm) * 0.6f;
            if (arm < full)
                up = Mathf.Min(up, Mathf.Max(lift * arm / full, arm * 0.7f));
            var offset = side - facing * arm + Vector3.Up * (up + (high ? 4 + machine.Radius * 0.6f : 0));
            var (at, climbed, hidden) = (centre + offset, 0f, true);
            foreach (var (reach, rise) in WatchClimb)
            {
                // Nearer, it is lower too: a small hero beside a ramp was looked down on from as high as from further out.
                at = centre + new Vector3(offset.X * reach, offset.Y * Mathf.Min(1, reach + 0.25f) + rise, offset.Z * reach);
                at.Y = Mathf.Max(at.Y, GroundAbout(at) + 2);
                climbed = rise + (1 - reach) * 10;
                if (!GroundHides(at, centre, machine.Radius))
                {
                    hidden = false;
                    break;
                }
            }
            // Straight behind it looks where the hero looks; else past the hero the way the camera faces; high up a little
            // past the hero. Framed turns it towards the hero as far as the middle third of the picture asks.
            var look = pick == 0 ? centre + ahead * 10 + Vector3.Up * 1.5f
                : high ? centre + facing * 6 + Vector3.Up
                : centre + side * 0.4f + facing * 8 + Vector3.Up * 1.5f;
            return (at, Framed(at, look, centre), climbed, hidden);
        }
        var behind = Place(0);
        if (walls.Count == 0 && behind.Climbed == 0)
        {
            _watchPick = 0;
            return (behind.At, behind.Look);
        }
        // A building may stand in the picture, up to about a seventh of it, at little cost (a pod beside the hero tells where
        // it is); more weighs heavily. A building or the ground between the camera and the hero rules the place out. Looking
        // down on the hero more steeply than about 26 degrees costs, more than about 38 degrees as much as a place that does
        // not see the hero: the picture became ground or the hero's back from above with no horizon, and an overhead place
        // beat any with the pod in the picture; a pod wall at the side is the lesser evil. Standing much nearer than the arm
        // costs too, as the hero then fills the picture, and right at its body as much as not seeing it (the camera stood
        // inside a gorilla pressed against the enemy pod).
        float Cost(int pick, (Vector3 At, Vector3 Look, float Climbed, bool Hidden) place)
        {
            var (swing, aside, high, near) = Of(pick);
            var cover = walls.Count == 0 ? 0 : WallCover(walls, place.At, place.Look);
            var walled = cover <= 0.15f ? cover * 0.25f : 0.04f + (cover - 0.15f) * 2;
            var hidden = place.Hidden || walls.Count > 0 && SightBlocked(walls, place.At, centre, machine.Radius) ? 1 : 0;
            var across = new Vector2(place.At.X - centre.X, place.At.Z - centre.Z).Length();
            var steep = Mathf.Atan2(place.At.Y - centre.Y, across);
            var steepness = Mathf.Max(0, steep - WatchSteep) * 1.5f + (steep > WatchSteepest ? 1 : 0);
            var away = place.At.DistanceTo(centre);
            var close = Mathf.Max(0, full * 0.6f - away) / full + (away < machine.Radius * 1.5f + 3 ? 1 : 0);
            return walled + hidden + place.Climbed * 0.012f + steepness + close + Mathf.Abs(swing) / Mathf.Pi * 0.15f
                + (aside != 0 ? 0.03f : 0) + (high ? 0.06f : 0) + (near ? 0.06f : 0);
        }
        var behindCost = Cost(0, behind);
        // Behind the hero with no wall worth the name in the picture and the hero in sight: there it stays, as in the open.
        if (behindCost < 0.12f && (_watchPick == 0 || behindCost < 0.05f))
        {
            _watchPick = 0;
            return (behind.At, behind.Look);
        }
        var places = Enumerable.Range(0, WatchPlaces).Select(pick => pick == 0 ? behind : Place(pick)).ToList();
        var costs = places.Select((place, pick) => pick == 0 ? behindCost : Cost(pick, place)).ToList();
        var best = costs.IndexOf(costs.Min());
        if (_watchPick < costs.Count && costs[_watchPick] <= costs[best] + 0.08f)
            best = _watchPick;
        _watchPick = best;
        return (places[best].At, places[best].Look);
    }

    /// <summary>
    /// What the camera at <paramref name="at"/> looks at instead of <paramref name="look"/>, turned only as far towards the
    /// hero at <paramref name="centre"/> as keeps it within the middle third of the picture: a camera raised over a wall or a
    /// hill and still looking far ahead had the hero at the bottom edge and the picture mostly ground. It never looks down
    /// more steeply than <see cref="WatchMostDown"/>, so that the horizon stays at the top of the picture; from a place that
    /// looks down on the hero more steeply than that the hero sits lower than the middle third (<see cref="WatchSpot"/> makes
    /// such places dear).
    /// </summary>
    private Vector3 Framed(Vector3 at, Vector3 look, Vector3 centre)
    {
        var (way, hero) = (look - at, centre - at);
        if (way.LengthSquared() < 1e-4f || hero.LengthSquared() < 1e-4f)
            return look;
        var most = Mathf.Atan(Mathf.Tan(Mathf.DegToRad(_firstPersonCamera.Fov) / 2) / 3);
        var (d, h) = (way.Normalized(), hero.Normalized());
        var off = d.AngleTo(h);
        var framed = off <= most ? d : h.Slerp(d, most / off);
        var level = new Vector3(framed.X, 0, framed.Z);
        if (Mathf.Asin(Mathf.Clamp(-framed.Y, -1, 1)) > WatchMostDown)
        {
            if (level.LengthSquared() < 1e-6f)
                level = new Vector3(hero.X, 0, hero.Z).LengthSquared() > 1e-6f ? new Vector3(hero.X, 0, hero.Z) : Vector3.Forward;
            framed = level.Normalized() * Mathf.Cos(WatchMostDown) + Vector3.Down * Mathf.Sin(WatchMostDown);
        }
        else if (off <= most)
            return look;
        return at + framed * way.Length();
    }

    /// <summary>How steeply the watch camera may look down at most, in radians (about 29 degrees): the top of its 70 degree picture stays above the horizon.</summary>
    private const float WatchMostDown = 0.5f;

    /// <summary>From how steep a place looking down on the watched hero costs, and from how steep it costs as much as one that does not see it (about 26 and 38 degrees).</summary>
    private const float WatchSteep = 0.45f, WatchSteepest = 0.66f;

    /// <summary>
    /// The highest ground within 3 m of a point: on a steep slope the ground drawn beside the camera filled the picture
    /// with a blur of earth when the camera stood only 2 m over the ground right under it.
    /// </summary>
    private static float GroundAbout(Vector3 at)
    {
        if (HeightField.Current is not { } ground)
            return 0;
        var most = ground.At(at.X, at.Z);
        for (var k = 0; k < 8; k++)
            most = Mathf.Max(most, ground.At(at.X + Mathf.Cos(k * Mathf.Pi / 4) * 3, at.Z + Mathf.Sin(k * Mathf.Pi / 4) * 3));
        return most;
    }

    /// <summary>
    /// Whether the ground stands between the camera and the hero's middle, by its height at every metre along the line (the
    /// last of it, within the hero's breadth, left out).
    /// </summary>
    private static bool GroundHides(Vector3 at, Vector3 centre, float radius)
    {
        if (HeightField.Current is not { } ground)
            return false;
        var way = centre - at;
        var length = way.Length() - radius * 0.8f;
        for (var step = 1f; step < length; step += 1)
        {
            var point = at + way * (step / way.Length());
            if (point.Y < ground.At(point.X, point.Z) + 0.8f)
                return true;
        }
        return false;
    }

    /// <summary>The buildings standing near a point, as boxes in the world (their transform and their model's bounds).</summary>
    private List<(Transform3D Inverse, Aabb Box)> WallsNear(Vector3 centre, float reach)
    {
        var from = new System.Numerics.Vector2(centre.X, centre.Z);
        var walls = new List<(Transform3D, Aabb)>();
        foreach (var c in World.Constructions)
        {
            if (System.Numerics.Vector2.Distance(c.Position, from) > reach + c.Radius || !_views.TryGetValue(c.Id, out var view) || !view.Visible)
                continue;
            walls.Add((view.GlobalTransform.AffineInverse(), view.Bounds));
        }
        return walls;
    }

    /// <summary>The share of the picture from <paramref name="at"/> looking at <paramref name="look"/> that buildings within 50 m fill, by a grid of rays.</summary>
    private float WallCover(List<(Transform3D Inverse, Aabb Box)> walls, Vector3 at, Vector3 look)
    {
        if (at.IsEqualApprox(look))
            return 1;
        var basis = Transform3D.Identity.LookingAt(look - at, Vector3.Up).Basis;
        var size = GetViewport().GetVisibleRect().Size;
        var tall = Mathf.Tan(Mathf.DegToRad(_firstPersonCamera.Fov) / 2);
        var wide = tall * size.X / Mathf.Max(1, size.Y);
        const int Across = 8, Down = 5;
        var hits = 0;
        for (var i = 0; i < Across; i++)
        {
            for (var j = 0; j < Down; j++)
            {
                var ray = basis * new Vector3(((i + 0.5f) / Across * 2 - 1) * wide, (1 - (j + 0.5f) / Down * 2) * tall, -1);
                if (walls.Any(w => DomeEntry(w.Inverse, w.Box, at, ray.Normalized()) is < 50))
                    hits++;
            }
        }
        return hits / (float)(Across * Down);
    }

    /// <summary>Whether a building stands between the camera and the hero's middle.</summary>
    private static bool SightBlocked(List<(Transform3D Inverse, Aabb Box)> walls, Vector3 at, Vector3 centre, float radius)
    {
        var way = centre - at;
        var length = way.Length();
        return length > 0.01f && walls.Any(w => DomeEntry(w.Inverse, w.Box, at, way / length) is { } hit && hit < length - radius);
    }

    /// <summary>
    /// Where a ray enters a building (0 from inside), in metres along it, or null; the building's transform given inverted.
    /// It is taken as a dome standing on the floor of its model's box and filling it: a pod is one, and its box's empty top
    /// corners made it look a third bigger than it is.
    /// </summary>
    private static float? DomeEntry(Transform3D inverse, Aabb box, Vector3 from, Vector3 direction)
    {
        var (floor, reach) = (new Vector3(box.GetCenter().X, box.Position.Y, box.GetCenter().Z), new Vector3(box.Size.X / 2, box.Size.Y, box.Size.Z / 2));
        if (reach.X < 0.01f || reach.Y < 0.01f || reach.Z < 0.01f)
            return null;
        // In the dome's own space, where it is half a unit ball, the ray keeps its parameter: the map is affine.
        var o = (inverse * from - floor) / reach;
        var d = inverse.Basis * direction / reach;
        var (a, b, c) = (d.Dot(d), 2 * o.Dot(d), o.Dot(o) - 1);
        var disc = b * b - 4 * a * c;
        if (a < 1e-12f || disc < 0)
            return null;
        var root = Mathf.Sqrt(disc);
        var (enter, leave) = ((-b - root) / (2 * a), (-b + root) / (2 * a));
        if (leave < 0)
            return null;
        enter = Mathf.Max(enter, 0);
        if (o.Y + d.Y * enter >= 0)
            return enter;
        // It enters the ball below the floor: it meets the dome only by coming up through the floor inside it.
        if (d.Y <= 0)
            return null;
        var up = -o.Y / d.Y;
        return up >= enter && up <= leave ? up : null;
    }

    /// <summary>A step from where the camera was towards where it wants to be, round <paramref name="centre"/>: angle, reach and height each eased.</summary>
    private static Vector3 RoundTo(Vector3 was, Vector3 want, Vector3 centre, float t)
    {
        var (a, b) = (was - centre, want - centre);
        var (ra, rb) = (new Vector2(a.X, a.Z), new Vector2(b.X, b.Z));
        if (ra.Length() < 0.5f || rb.Length() < 0.5f)
            return was.Lerp(want, t);
        var angle = Mathf.LerpAngle(ra.Angle(), rb.Angle(), t);
        var reach = Mathf.Lerp(ra.Length(), rb.Length(), t);
        return centre + new Vector3(Mathf.Cos(angle) * reach, Mathf.Lerp(a.Y, b.Y, t), Mathf.Sin(angle) * reach);
    }

    /// <summary>While looking on: Tab goes on to the next, Esc and the first-person key back to the field, the menu and pause keys as ever.</summary>
    private void WatchInput(InputEvent @event)
    {
        if (@event is not InputEventKey { Pressed: true, Echo: false } key)
            return;
        if (MatchOver)
        {
            if (key.Keycode is Key.Escape or Key.Space)
                _endSkipped = true;
            return;
        }
        if (key.Keycode == Key.Tab)
            WatchNext();
        else if (key.Keycode == Key.Escape || KeyMap.Is(key, KeyMap.Act.FirstPerson))
            StopWatching();
        else if (KeyMap.Is(key, KeyMap.Act.Menu))
            OpenMenu();
        else if (KeyMap.Is(key, KeyMap.Act.Pause))
            TogglePause();
    }

    /// <summary>What the world did in a battle, called out, floated up and on the message line.</summary>
    private void ShowBattleEvents(WiredHud hud)
    {
        if (World.Battle is not { } run)
            return;
        var own = run.TeamOf(Player);
        string Name(Race race) => BattleReadout.HeroName(run, race);
        foreach (var e in World.Events)
        {
            switch (e.Kind)
            {
                case GameEventKind.BattleEnded when Enum.TryParse<BattleEnd>(e.Detail, out var reason):
                    var outcome = run.Won(Player) ? BattleOutcome.Won : run.Lost(Player) ? BattleOutcome.Lost : BattleOutcome.Draw;
                    hud.BattleEnds(outcome, reason);
                    _audio.PlayGui(outcome == BattleOutcome.Won ? "gui/sounds/taskmach.wav" : "gui/sounds/hpcritic.wav");
                    break;
                case GameEventKind.BattleCaptureProgress when e.Other is { } team && _captureCalls.Call(team, e.Detail, World.Time):
                    var ours = team == own;
                    if (e.Detail == "0")
                        hud.CaptureBegins(ours);
                    _audio.PlayGui(ours ? "gui/sounds/listclik.wav" : "gui/sounds/hpcritic.wav");
                    break;
                case GameEventKind.BattleCaptureReset when e.Other == (int)Player && e.Detail == "hit":
                    _hud.Message(Lang.T("Hit: your share of the capture is lost"));
                    break;
                case GameEventKind.BattleHeroKilled when Enum.TryParse<Race>(e.Detail, out var fallen):
                    var killer = e.Other is { } by ? (Race)by : (Race?)null;
                    _hud.Message(killer is { } k ? Lang.T("{0} destroyed {1}", Name(k), Name(fallen)) : Lang.T("{0} fell", Name(fallen)));
                    if (killer == Player)
                    {
                        hud.Float(ActorView.World(e.Position) + Vector3.Up * 3, Lang.T("Hero destroyed"));
                        _audio.PlayGui("gui/sounds/taskmach.wav");
                    }
                    break;
            }
        }
        _captureCalls.Seen(run, World.Time);
    }

    /// <summary>
    /// A battle that has come to its end goes into the player's account once: XP on the chassis it was fought on, free XP and
    /// credits, and what came within reach is said once it is written. A recording, and a battle a bot played for the player,
    /// change nothing.
    /// </summary>
    private void RecordBattle()
    {
        if (_battleRecorded || Watching || BattleWatch || World.Battle is not { Over: true } run || BattleNote.Of(run, Player) is not { } note)
            return;
        // Rejoining a match whose battle had ended before this player took the race back: the session that saw the end
        // counted it then, and one that left before the end does not count it now.
        if (MatchRequest.Current.ResumeFrom is { } from && run.Result!.EndedAt * GameWorld.TicksPerSecond < from)
            return;
        _battleRecorded = true;
        if (BattleAccounts.Ledger.Tell(note) is { } opened)
            ShowWithinReach(opened);
        else
            _hud.Message(Lang.T("The battle is not in the profile yet: the file cannot be written just now."));
    }

    /// <summary>A profile held at the battle's end is tried again every so often.</summary>
    private void RetryBattle(double now)
    {
        if (!BattleAccounts.Ledger.Waiting || now < _battleRetryAt)
            return;
        _battleRetryAt = now + WiredRetry;
        if (BattleAccounts.Ledger.Write() is { } opened)
        {
            _hud.Message(Lang.T("The battle is in the profile."));
            ShowWithinReach(opened, late: true);
        }
    }

    /// <summary>
    /// What came within reach goes to the end screen; a line on the message bar says it only when the profile was written
    /// late (over the victory banner it only stood in the way).
    /// </summary>
    private void ShowWithinReach(IReadOnlyList<string> opened, bool late = false)
    {
        BattleOpened = opened;
        if (late && opened.Count > 0)
            _hud.Message(Lang.T("Within reach: {0}", string.Join(", ", opened.Select(HangarText.Name))));
    }

    /// <summary>The same battle once more, from a new seed: the same heroes and bots on the same map.</summary>
    public void BattleAgain()
    {
        SaveReplay();
        MatchRequest.Current = MatchRequest.Current with { Saved = null, Seed = null, ResumeFrom = null };
        GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
    }
}
