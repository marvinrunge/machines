using Godot;
using Machines.Sim.Story;
using Machines.Sim.World;

namespace Machines.Game;

// Package E's HUD (plan stufe7 §4 E, design-escape §8): the gyro, a small plate at the top right under the countdown with the
// ship's list ("LIST 9°", an arrow the way it leans and a horizon bar tilted by it; the HUD itself never tilts), and the
// rear arc, a red arc along the bottom edge for each of the pack behind the hero that sees it, brighter as it draws on it
// (StorySeeker.SightSince against its Draw), so that a player running away knows when to dodge. The countdown itself is the
// HUD's own (StoryHud.Holds / the objective).
public partial class StoryHud
{
    private void DrawEscape(StoryReadout now)
    {
        if (_game.World is not { Story: { } run } world || !run.Flags.Contains(StoryMarks.Escape) || now.Ending is not null)
            return;
        DrawGyro();
        if (world.HeroOf(Game.Me) is { IsDead: false } hero)
            DrawRearArc(world, run, hero);
    }

    /// <summary>The list as the sim leans the view (Game.Tilt): its degrees, the way, a horizon bar tilted by it.</summary>
    private void DrawGyro()
    {
        var tilt = _game.Tilt;
        var roll = tilt.Roll;
        if (Mathf.Abs(roll) < 0.5f && tilt.Sway <= 0)
            return;
        var box = new Rect2(_size.X - 190 * _u, 130 * _u, 170 * _u, 58 * _u);
        var colour = Mathf.Abs(roll) >= 9 ? HudInk.Danger : HudInk.Warning;
        _ink.Plate(box, HudInk.Plating, MenuTheme.Edge, 8);
        var way = roll > 0 ? "►" : roll < 0 ? "◄" : "";
        _ink.Say(box.Position + new Vector2(12, 20) * _u, Lang.T("LIST {0}°", Mathf.RoundToInt(Mathf.Abs(roll))) + " " + way, 14, colour);
        // The horizon: a bar through the middle, turned by the list and its sway, with the level as a faint line.
        var centre = box.Position + new Vector2(box.Size.X / 2, 40 * _u);
        var half = 60 * _u;
        DrawLine(centre - new Vector2(half, 0), centre + new Vector2(half, 0), new Color(MenuTheme.Dim, 0.5f), 1);
        var swing = tilt.Sway * Mathf.Sin(Mathf.Tau * (float)(_time / Mathf.Max(tilt.Period, 0.5f)));
        var angle = Mathf.DegToRad(roll + swing);
        var along = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * half;
        DrawLine(centre - along, centre + along, colour, 2.5f * _u);
        DrawCircle(centre, 3 * _u, colour);
    }

    /// <summary>A red arc along the bottom edge for each pack member behind the hero that sees it, the brighter the nearer it is to firing.</summary>
    private void DrawRearArc(GameWorld world, StoryRun run, Machine hero)
    {
        if (GetViewport().GetCamera3D() is not { } camera)
            return;
        var forward = -camera.GlobalBasis.Z;
        var heading = Mathf.Atan2(forward.X, -forward.Z);
        var centre = new Vector2(Left + (_size.X - Left) / 2, _size.Y + 260 * _u);
        var radius = 330 * _u;
        foreach (var member in world.PackAlive(run))
        {
            if (world.StorySeekerOf(member.Id) is not { Blind: false } seeker || !seeker.Sees.Contains(hero.Id))
                continue;
            // Its bearing from the hero, against the way the camera looks (0 ahead, positive to the right).
            var to = ActorView.World(member.Position) - ActorView.World(hero.Position);
            var bearing = Mathf.Wrap(Mathf.Atan2(to.X, -to.Z) - heading, -Mathf.Pi, Mathf.Pi);
            if (Mathf.Abs(bearing) < Mathf.Pi / 2)
                continue;
            // Straight behind at the arc's top in the middle, at the side out towards its ends.
            var behind = Mathf.Sign(bearing) * (Mathf.Pi - Mathf.Abs(bearing)) / (Mathf.Pi / 2);
            var at = -Mathf.Pi / 2 + behind * 0.75f;
            var drawn = seeker.SightSince.TryGetValue(hero.Id, out var since)
                ? Mathf.Clamp((world.Time - since) / Mathf.Max(seeker.Draw, 0.1f), 0, 1)
                : 0;
            var pulse = drawn >= 1 ? 0.75f + 0.25f * Mathf.Sin((float)_time * 18) : 1;
            var colour = new Color(HudInk.Danger, (0.35f + 0.65f * drawn) * pulse);
            DrawArc(centre, radius, at - 0.12f, at + 0.12f, 24, colour, (5 + 5 * drawn) * _u, true);
        }
    }
}
