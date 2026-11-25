using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// Package S's HUD (design-stealth §9.4): while section 2's rule holds, an eye at the edge of the view on the side the
// Thresher stands, opening as he grows suspicious of this player's hero (amber while he doubts, red as he draws and fires);
// IM LICHT while the hero stands in his beam, whether or not he has noticed yet; ERFASST while he draws on it; SCHLEICHEN
// on the signature's plate while the hero creeps; and his last place on the motion tracker fading over two seconds once he
// stands still (the tracker shows only what moves).
public partial class StoryHud
{
    private System.Numerics.Vector2? _stalkerLastSeen;
    private double _stalkerStoodAt;

    /// <summary>How long the tracker keeps his last place once he stopped moving, seconds.</summary>
    private const double GhostFor = 2;

    private void DrawStealth(StoryReadout now)
    {
        if (_game.World is not { } world || StoryStalker.Look(world) is not { } him || world.Find<Machine>(him.Id) is not { } thresher)
        {
            _stalkerLastSeen = null;
            return;
        }
        var hero = world.HeroOf(Game.Me);
        if (Shows(BootPart.Tracker))
        {
            DrawStalkerGhost(now, him, hero);
            if (hero is not null && world.DriveInput.TryGetValue(hero.Id, out var input) && input.Slow)
                DrawCreeping();
        }
        if (hero is null || !Driving || now.Ending is not null)
            return;
        DrawEye(world, him, hero);
        var middle = Left + (_size.X - Left) / 2;
        if (him.State == StalkerState.Draw && him.Quarry == hero.Id || him.State == StalkerState.Fire && him.Quarry == hero.Id)
        {
            var beat = 0.6f + 0.4f * Mathf.Sin((float)_time * 18);
            _ink.Say(new Vector2(middle, _size.Y * 0.3f), Lang.T("LOCKED ON"), 30, new Color(HudInk.Danger, beat), 0.5f, outline: 2);
        }
        else if (StoryStalker.InLight(world, thresher, hero))
        {
            var beat = 0.7f + 0.3f * Mathf.Sin((float)_time * 6);
            _ink.Say(new Vector2(middle, _size.Y * 0.3f), Lang.T("IN THE LIGHT"), 24, new Color(Colors.White, beat), 0.5f, outline: 2);
        }
    }

    // The eye at the edge of the view, on his side: 0 ahead is the top edge, to the right the right edge.
    private void DrawEye(GameWorld world, StalkerLook him, Machine hero)
    {
        var suspicion = Mathf.Max(world.StorySuspicionOf(hero.Id), StoryStalker.SuspicionOf(world, him, hero));
        if (suspicion < 0.02f && him.State is StalkerState.Calm or StalkerState.Dwell)
            return;
        var camera = GetViewport().GetCamera3D();
        var bearing = camera is not null
            ? Bearing(camera, new TrackerContact(him.Position, 0, 0))
            : 0;
        var centre = new Vector2(Left + (_size.X - Left) / 2, _size.Y / 2);
        var reach = new Vector2((_size.X - Left) / 2 - 60 * _u, _size.Y / 2 - 70 * _u);
        var way = new Vector2(Mathf.Sin(bearing), -Mathf.Cos(bearing));
        // Out along the bearing to the box round the view's middle.
        var scale = Mathf.Min(reach.X / Mathf.Max(Mathf.Abs(way.X), 0.001f), reach.Y / Mathf.Max(Mathf.Abs(way.Y), 0.001f));
        var at = centre + way * scale;
        var colour = him.State is StalkerState.Draw or StalkerState.Fire ? HudInk.Danger : suspicion >= StorySight.WaryAt || him.State == StalkerState.Wary ? HudInk.Warning : MenuTheme.Glow;
        var open = Mathf.Clamp(0.15f + 0.85f * suspicion, 0.15f, 1);
        var width = 34 * _u;
        var height = 16 * _u * open;
        // The lids: two arcs meeting at the corners, the iris filling with his suspicion.
        var points = new Vector2[25];
        for (var i = 0; i <= 12; i++)
        {
            var t = i / 12f;
            var x = Mathf.Lerp(-width, width, t);
            var y = height * Mathf.Sin(Mathf.Pi * t);
            points[i] = at + new Vector2(x, -y);
            if (i < 12)
                points[24 - i] = at + new Vector2(x, y);
        }
        points[12] = at + new Vector2(width, 0);
        DrawColoredPolygon(points, new Color(0, 0, 0, 0.55f));
        DrawPolyline([.. points, points[0]], colour, Mathf.Max(1.5f, 2 * _u), true);
        var iris = Mathf.Min(height, 12 * _u);
        if (iris > 1)
        {
            DrawCircle(at, iris, new Color(colour, 0.35f + 0.65f * suspicion));
            DrawCircle(at, iris * 0.4f, new Color(0, 0, 0, 0.8f));
        }
        // A tick toward him from the eye's middle.
        DrawLine(at + way * (width + 4 * _u), at + way * (width + 14 * _u), colour, Mathf.Max(1.5f, 2 * _u), true);
    }

    // His last place on the tracker, fading over two seconds once he stands (Contacts holds only what moves).
    private void DrawStalkerGhost(StoryReadout now, StalkerLook him, Machine? hero)
    {
        if (now.Contacts.Any(c => System.Numerics.Vector2.Distance(c.Position, him.Position) < 1))
        {
            _stalkerLastSeen = him.Position;
            _stalkerStoodAt = _time;
            return;
        }
        if (_stalkerLastSeen is not { } last || hero is null || _time - _stalkerStoodAt > GhostFor)
            return;
        var distance = System.Numerics.Vector2.Distance(hero.Position, last);
        if (distance > StoryReadout.TrackerRange)
            return;
        var camera = GetViewport().GetCamera3D();
        var angle = camera is not null ? Bearing(camera, new TrackerContact(last, 0, distance)) : 0;
        var radius = TrackerRadius * _u;
        var at = TrackerCentre + new Vector2(Mathf.Sin(angle), -Mathf.Cos(angle)) * Mathf.Clamp(distance / StoryReadout.TrackerRange, 0, 1) * radius;
        var fade = 1 - (float)((_time - _stalkerStoodAt) / GhostFor);
        DrawArc(at, 6 * _u, 0, Mathf.Tau, 16, new Color(MenuTheme.Glow, 0.7f * fade), Mathf.Max(1, 1.5f * _u), true);
    }

    // SCHLEICHEN on the signature's plate, above its right end.
    private void DrawCreeping()
    {
        var tracker = TrackerCentre;
        var right = tracker.X + (TrackerRadius + 10) * _u;
        var top = tracker.Y - (TrackerRadius + 18) * _u - 40 * _u;
        _ink.Say(new Vector2(right - 10 * _u, top + 16 * _u), Lang.T("CREEPING"), 12, MenuTheme.Glow, 1);
    }
}
