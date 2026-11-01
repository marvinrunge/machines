using Godot;
using Machines.Import.Model;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// The thirty-second trailer on hilly (MACHINES_CINEMATIC=trailer, data/maps/trailer.scn).
public partial class Cinematic
{
    // Where the takes change: on the strong beats of the original's intro music (Track 00), which starts with the
    // film: a bar every 1.845 s up to 7.4 s, and again from 10.5 s. The title comes on the beat at 27.24 s.
    private static readonly float[] Cuts = [0, 3.69f, 7.38f, 10.51f, 14.2f, 18.01f, 19.85f, 21.69f, 25.39f, 30];

    // The missile launcher is held unloaded until it fires on the cut to its take.
    private const float Launch = 10.45f;

    // The cast: who is filmed and ordered about.
    private List<int> _horde = [], _line = [], _builders = [];
    private int _knight, _launcher;
    private int? _missile;

    /// <summary>Picks out the players of data/maps/trailer.scn and when they get their orders.</summary>
    private void TrailerCast()
    {
        _horde = _world.Machines.Where(m => m.Race == Race.Blue).Select(m => m.Id).ToList();
        _line = Of(Race.Red, "AGGRESSOR");
        _builders = Of(Race.Red, "CONSTRUCTOR");
        _knight = Nearest(_world.Machines.Where(m => m.Race == Race.Red && m.IsType("AGGRESSOR", "KNIGHT")), 1160, 740);
        _launcher = Building(Race.Red, "MISSILE_EMPLACEMENT", 1235, 700, "LAUNCHER");

        // Two pairs of constructors start a turret each before the line; the horde comes down the valley.
        At(0, () =>
        {
            Place(_builders.Take(2).ToList(), 1112, 690);
            Place(_builders.Skip(2).ToList(), 1172, 690);
        });
        At(1, () => _world.Issue(new MoveCommand(Race.Blue, _horde, new NVector2(1165, 700))));
        // The launcher is held unloaded, and fires at the horde as its take begins.
        var launch = (int)MathF.Round(Launch * GameWorld.TicksPerSecond);
        _everyTick.Add(() =>
        {
            if (_world.Tick < launch)
                foreach (var weapon in Launcher()?.Weapons ?? [])
                    weapon.Reload = weapon.Spec.ReloadTime;
        });
        At(Launch, () =>
        {
            foreach (var weapon in Launcher()?.Weapons ?? [])
                weapon.Reload = 0;
        });
    }

    private Construction? Launcher() => _world.FindTarget(_launcher) as Construction;

    private void Place(List<int> constructors, float x, float y)
    {
        var centre = new NVector2(x, y);
        var spot = Enumerable.Range(0, 400).Select(i => centre + new NVector2(i % 20 - 10, i / 20 - 10) * 3)
            .OrderBy(p => NVector2.Distance(p, centre))
            .FirstOrDefault(p => _world.WhyCannotPlace(Race.Red, "MISSILE_EMPLACEMENT", "TURRET", 2, p, 0) is null, centre);
        _world.Issue(new PlaceConstructionCommand(Race.Red, constructors, "MISSILE_EMPLACEMENT", "TURRET", 2, spot, 0));
    }

    /// <summary>
    /// Thirty seconds, cut on the music's beats: low over the river to the camp; through the camp, where constructors
    /// raise turrets; the reapers pouring down the valley; after a missile of the long-range launcher to where it
    /// strikes; behind a knight of the red line as it turns to face them; the clash, over a reaper's shoulder, the
    /// clash slowed down; and up into the sky over the battle as the title comes.
    /// </summary>
    private Shot[] Trailer()
    {
        TrailerCast();
        _titleAt = 27.24f;
        _fadeIn = 1.5f;
        _fadeOut = true;
        float Take(int i) => Cuts[i + 1] - Cuts[i];
        return
        [
        new("river", Take(0), u =>
        {
            // Low over the water and across the bank towards the pod.
            var e = Ease(u);
            var eye = Ground(Mathf.Lerp(1150, 1162, e), Mathf.Lerp(150, 390, u), Mathf.Lerp(3, 8, e));
            return new Frame(eye, Ground(1185, 480, Mathf.Lerp(-12, 8, e)), 38, Blur: 1);
        }),
        new("camp", Take(1), u =>
        {
            // Through the camp between the pod and the factory, out to where the turrets rise.
            var e = Ease(u);
            var eye = Ground(Mathf.Lerp(1112, 1128, e), Mathf.Lerp(455, 630, e), Mathf.Lerp(4.5f, 5.5f, e));
            return new Frame(eye, Ground(Mathf.Lerp(1125, 1142, e), 700, 2), 44, Blur: 1.3f);
        }),
        new("horde", Take(2), u =>
        {
            // Low beside the mouth of the valley as the reapers pour down past it.
            var horde = Follow("horde", Centre(_horde));
            return new Frame(Ground(1200 - 6 * u, 915 - 25 * u, 1.6f), horde + new Vector3(0, 2, 0), 40, Blur: 1.2f);
        }),
        new("missile", Take(3), Missile),
        new("line", Take(4), u =>
        {
            var knight = Follow("knight", Position(_knight));
            var eye = knight + new Vector3(-3, 3.5f, -13 + u);
            var horde = Follow("horde", Centre(_horde));
            // The knight looks ahead, then turns its upper body to the horde as the focus moves over to it.
            Steer(_knight, u < 0.3f ? 0 : Toward(_knight, horde));
            var pull = Mathf.SmoothStep(0.45f, 0.8f, u);
            var focus = Mathf.Lerp(eye.DistanceTo(knight + Vector3.Up * 2.5f), eye.DistanceTo(horde), pull);
            return new Frame(eye, knight + new Vector3(-4, 2.5f, 30), 34, focus, 1.8f);
        }),
        new("clash", Take(5), u =>
        {
            var clash = Follow("clash", Clash());
            return new Frame(Ground(1235 - 10 * u, 775, 7), clash + new Vector3(0, 3, 0), 40, Blur: 1.4f);
        }),
        new("runner", Take(6), u =>
        {
            // Over the shoulder of a reaper running at the red line.
            var line = Centre(_line) ?? Ground(1160, 750);
            var picked = Pick("runner", () => _horde.Where(id => Position(id) is { } p && p.DistanceTo(line) > 25)
                .OrderBy(id => Position(id)!.Value.DistanceTo(line)).Cast<int?>().FirstOrDefault());
            var runner = Follow("runner", picked is { } id ? Position(id) : null);
            var ahead = new Vector3(line.X - runner.X, 0, line.Z - runner.Z).Normalized();
            var side = ahead.Cross(Vector3.Up);
            var eye = Lift(runner - ahead * 9 + side * 2.5f, 3.3f);
            return new Frame(eye, runner + ahead * 25 + Vector3.Up, 42, eye.DistanceTo(runner), 1.6f);
        }),
        new("slow", Take(7), u =>
        {
            var clash = Follow("clash", Clash());
            var angle = -0.6f + 1.0f * u;
            return new Frame(Around(clash, 22, angle, 4.5f), clash + new Vector3(0, 2.5f, 0), 45, Blur: 1.8f);
        }, u => 1 - 0.7f * Mathf.SmoothStep(0, 0.15f, u) * (1 - Mathf.SmoothStep(0.85f, 1, u))),
        new("rise", Take(8), u =>
        {
            var clash = Hold("rise", Clash());
            var e = Ease(u);
            var eye = clash + new Vector3(-20, 6, -35).Lerp(new Vector3(-60, 110, -230), e);
            return new Frame(eye, clash + new Vector3(0, 2, 60 * e), Mathf.Lerp(42, 48, e), Blur: Mathf.Lerp(1, 0.4f, e));
        }),
        ];
    }

    /// <summary>
    /// Beside the launcher as it fires, then after its missile: the camera lags as it takes off, closes in behind it
    /// and glides on to where it strikes.
    /// </summary>
    private Frame Missile(float u)
    {
        var launcher = Position(_launcher) ?? Ground(1235, 700);
        var horde = Hold("horde", Centre(_horde));
        var ahead = Flat(horde - launcher);
        var side = ahead.Cross(Vector3.Up);
        var round = _missile is { } id ? _world.Projectiles.FirstOrDefault(p => p.Id == id) : null;
        if (_missile is null && (round = _world.Projectiles.Where(p => p.Shooter == _launcher).MinBy(p => p.Id)) is not null)
            _missile = round.Id;
        if (round is not null && _round(round.Id)?.GlobalPosition is { } at)
        {
            var heading = Flat(new Vector3(round.Direction.X, 0, round.Direction.Y));
            _followed["struck"] = at;
            _followed["heading"] = heading;
            var chase = Mathf.Lerp(1.5f, 14, Mathf.SmoothStep(0.05f, 0.45f, u));
            // Beside and over its smoke trail, which would hide everything from right behind it.
            var eye = Above(Follow("eye", at - heading * 9 + side * 4.5f + Vector3.Up * 3.5f, chase), 1.2f);
            var target = Follow("aim", at + heading * 30, 10);
            return new Frame(eye, target, 46, eye.DistanceTo(at), 1.1f);
        }
        if (_followed.TryGetValue("struck", out var struck))
        {
            // It has struck: on to a stop short of the blast, looking at it.
            var heading = _followed["heading"];
            var eye = Above(Follow("eye", struck - heading * 16 + side * 4.5f + Vector3.Up * 5, 2.5f), 1.2f);
            var target = Follow("aim", struck + Vector3.Up * 2, 4);
            return new Frame(eye, target, 46, eye.DistanceTo(struck), 1.1f);
        }
        // Not fired yet: beside and behind the launcher, looking where it will fire.
        var start = Follow("eye", launcher - ahead * 16 + side * 5 + Vector3.Up * 7);
        return new Frame(start, Follow("aim", launcher + ahead * 40), 46, start.DistanceTo(launcher), 1.1f);
    }

    // Where the armies meet: the middle of the reapers nearest the red line, or between the two.
    private Vector3? Clash()
    {
        var line = Centre(_line) ?? Ground(1160, 750);
        var near = _horde.Select(Position).OfType<Vector3>().Where(p => p.DistanceTo(line) < 90).ToList();
        if (near.Count >= 3)
            return near.Aggregate(Vector3.Zero, (sum, p) => sum + p) / near.Count;
        return Centre(_horde) is { } horde ? (horde + line) / 2 : line;
    }
}
