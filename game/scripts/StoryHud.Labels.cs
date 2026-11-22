using Godot;

namespace Machines.Game;

/// <summary>
/// The labels over the others of the trio in the view (not in the original): each one's name and how far, and under it what
/// it is about as its card says (follows, waits, on the way, distracts, down), in the trio's ice blue (gold drawing the
/// hunters off, red down, grey still switched off). They fade as a Grunt comes near in plain view and shrink with distance;
/// off the screen a Grunt far off or down gets an arrow at the edge, inside the objective's (<see cref="GruntLabels"/>). In
/// co-op the other players' heroes have the same. None while the boot runs, a hack's board is up, a cutscene plays, the
/// hero is down or the chapter is over.
/// </summary>
public partial class StoryHud
{
    // The labels drawn so far this frame, so that none covers another.
    private readonly List<Rect2> _labelled = [];

    /// <summary>Whether the labels are up at all now.</summary>
    private bool LabelsUp(StoryReadout now) =>
        Driving && now.Ending is null && !Booting && !_game.HackShown && !_game.InCutscene && _game.World.HeroOf(Game.Me) is not null;

    private void DrawLabels(StoryReadout now)
    {
        if (!LabelsUp(now) || GetViewport().GetCamera3D() is not { } camera || _game.World.HeroOf(Game.Me) is not { } hero)
            return;
        // The nearest first: a label further off that would cover it stands over it instead.
        _labelled.Clear();
        foreach (var grunt in now.Grunts.OrderBy(g => g.Distance ?? float.MaxValue))
        {
            if (grunt.Position is not { } position || grunt.Distance is not { } distance)
                continue;
            var view = grunt.Actor is { } actor ? _game.ViewOf(actor) : null;
            var place = view is not null && IsInstanceValid(view)
                ? view.GlobalPosition + Vector3.Up * (view.TopOverFeet + 0.25f)
                : ActorView.World(position) + Vector3.Up * 2.6f;
            Label(camera, grunt, place, distance, GruntLabels.PlainView(_game.World, hero.Position, position));
        }
    }

    /// <summary>A Grunt's colour on its label, as its card has it.</summary>
    private static Color LabelColour(GruntReadout grunt) => grunt.Doing switch
    {
        GruntDoing.Down => HudInk.Danger,
        GruntDoing.Dormant => MenuTheme.Dim,
        GruntDoing.Distracting => HudInk.Gold,
        _ => grunt.Health < 0.3f ? HudInk.Danger : HudInk.Ice,
    };

    /// <summary>One Grunt's label over it where it is in view, or its arrow at the edge where it is not.</summary>
    private void Label(Camera3D camera, GruntReadout grunt, Vector3 place, float distance, bool plainView)
    {
        var colour = LabelColour(grunt);
        var name = grunt.Name.Caps();
        var far = $"{Mathf.RoundToInt(distance)} m";
        var margin = 90 * _u;
        var view = new Rect2(new Vector2(Left + 16 * _u, margin * 0.9f), new Vector2(_size.X - Left - 32 * _u, _size.Y - margin * 0.9f - 250 * _u));
        var front = !camera.IsPositionBehind(place);
        var at = front ? camera.UnprojectPosition(place) : Vector2.Zero;
        if (front && view.HasPoint(at))
        {
            var alpha = GruntLabels.Alpha(distance, plainView, grunt.Doing);
            if (alpha > 0.02f)
                Tag(at, name, far, grunt, colour, GruntLabels.Scale(distance), alpha);
            return;
        }
        if (!GruntLabels.Arrow(distance, grunt.Doing))
            return;
        // At the edge, a little inside the objective's arrows, so the two never sit on each other.
        var area = new Rect2(new Vector2(Left + margin, margin * 1.6f), new Vector2(_size.X - Left - margin * 2, _size.Y * 0.68f - margin * 1.6f)).Grow(-30 * _u);
        var local = camera.GlobalTransform.AffineInverse() * place;
        var angle = MathF.Atan2(local.X, -local.Z);
        var way = new Vector2(MathF.Sin(angle), -MathF.Cos(angle));
        var tip = area.GetCenter() + way * area.Size / 2;
        var tint = new Color(colour, grunt.Doing == GruntDoing.Down ? 0.7f + 0.3f * MathF.Sin((float)_time * 6) : 0.8f);
        var back = _ink.Arrow(tip, way, 15 * _u, tint);
        var label = $"{name}  {far}";
        _ink.Tag(back - way * (_ink.Width(label, 15) / 2 + 14 * _u) * new Vector2(1, 0.4f), label, tint, new Rect2(Left, 0, _size.X - Left, _size.Y));
    }

    /// <summary>
    /// The label on a Grunt in view: a small chevron over its head and a plate over that, the name and the distance on its
    /// first line, the card's sign and word on the second (a person's hero says only when it is down).
    /// </summary>
    private void Tag(Vector2 at, string name, string far, GruntReadout grunt, Color colour, float scale, float alpha)
    {
        var k = scale;
        var state = !grunt.Person || grunt.Doing == GruntDoing.Down ? Lang.T(DoingWord(grunt.Doing)).Caps() : null;
        const float nameSize = 15, farSize = 12, stateSize = 11, pad = 8, sign = 16;
        var top = _ink.Width(name, nameSize * k) + 8 * _u * k + _ink.Width(far, farSize * k);
        var under = state is null ? 0 : sign * _u * k + _ink.Width(state, stateSize * k);
        var size = new Vector2(Mathf.Max(top, under) + 2 * pad * _u * k, (state is null ? 24 : 39) * _u * k);
        // The chevron points down at the Grunt; the plate stands over it, kept whole on the screen.
        var r = 6 * _u * k;
        var tint = new Color(colour, alpha);
        DrawColoredPolygon([at, at + new Vector2(-r, -r * 1.1f), at + new Vector2(r, -r * 1.1f)], new Color(colour, 0.85f * alpha));
        var screen = new Rect2(Left, 0, _size.X - Left, _size.Y);
        var corner = at - new Vector2(size.X / 2, size.Y + r * 1.1f + 4 * _u * k);
        corner = new Vector2(Mathf.Clamp(corner.X, screen.Position.X, Mathf.Max(screen.Position.X, screen.End.X - size.X)), Mathf.Max(corner.Y, 0));
        var box = new Rect2(corner, size);
        for (var tries = 0; tries < 3 && _labelled.FirstOrDefault(other => other.Grow(2 * _u).Intersects(box)) is { Size.X: > 0 } covered; tries++)
            box.Position = new Vector2(box.Position.X, covered.Position.Y - size.Y - 3 * _u);
        _labelled.Add(box);
        _ink.Plate(box, new Color(HudInk.Plating, 0.72f * alpha), new Color(colour, 0.5f * alpha), 6 * k);
        var x = box.Position.X + pad * _u * k;
        _ink.Say(new Vector2(x, box.Position.Y + 17 * _u * k), name, nameSize * k, new Color(grunt.Doing == GruntDoing.Dormant ? MenuTheme.Dim : MenuTheme.Text, alpha));
        _ink.Say(new Vector2(box.End.X - pad * _u * k, box.Position.Y + 17 * _u * k), far, farSize * k, new Color(MenuTheme.Dim, alpha), 1);
        if (state is null)
            return;
        DrawDoing(grunt.Doing, new Vector2(x + sign / 2 * _u * k, box.Position.Y + 28 * _u * k), 0.7f * k, alpha);
        _ink.Say(new Vector2(x + sign * _u * k, box.Position.Y + 32 * _u * k), state, stateSize * k, tint);
    }
}
