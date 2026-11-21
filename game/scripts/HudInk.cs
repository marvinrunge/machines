using Godot;

namespace Machines.Game;

/// <summary>
/// How the remake's own HUDs draw (Wired's, a battle's, the story's): text in the menus' face from its baseline, plates with
/// their corners cut and a sheen at the top, thin bars, glyphs with a glow, a key's cap, the diamond on a place in view and
/// the arrow towards one out of it, a prompt on a plate, and the band of a call across the view. Everything is in pixels of
/// a window 1080 high, times <see cref="U"/>; it draws on the canvas item it was made for, from that item's <c>_Draw</c>.
/// </summary>
public sealed class HudInk(CanvasItem canvas)
{
    // Colours of the remake's HUDs, beside the menus'.
    public static readonly Color Danger = new(1f, 0.26f, 0.16f), Warning = new(1f, 0.7f, 0.2f), Ice = new(0.48f, 0.8f, 1f), Gold = new(1f, 0.84f, 0.36f);
    public static readonly Color Plating = new(0.006f, 0.028f, 0.012f, 0.9f), Sheen = new(0.03f, 0.085f, 0.04f, 0.9f);

    /// <summary>How much larger than in a window 1080 high everything is drawn: the window's height and the options' HUD size.</summary>
    public float U { get; set; } = 1;

    /// <summary>The scale for a view of that size at the options' HUD size, never below 0.6 of the standard.</summary>
    public static float ScaleFor(Vector2 view) => Mathf.Max(view.Y / 1080f, 0.6f) * GameOptions.HudSize;

    public int Px(float size) => Math.Max(9, Mathf.RoundToInt(size * U));

    public float Width(string text, float size) => MenuTheme.Font.GetStringSize(text, HorizontalAlignment.Left, -1, Px(size)).X;

    /// <summary>Text from its baseline: <paramref name="align"/> says which part of it stands there, 0 its start, 0.5 its middle, 1 its end.</summary>
    public void Say(Vector2 at, string text, float size, Color colour, float align = 0, int outline = 0)
    {
        var px = Px(size);
        var font = MenuTheme.Font;
        var place = at - new Vector2(font.GetStringSize(text, HorizontalAlignment.Left, -1, px).X * align, 0);
        if (outline > 0)
            canvas.DrawStringOutline(font, place, text, HorizontalAlignment.Left, -1, px, Math.Max(2, Mathf.RoundToInt(outline * 2 * U)), new Color(0, 0, 0, colour.A * 0.85f));
        canvas.DrawString(font, place, text, HorizontalAlignment.Left, -1, px, colour);
    }

    /// <summary>A text cut with dots to a width in the HUD's pixels, as a hero's name can be long.</summary>
    public string Fit(string text, float size, float width)
    {
        if (Width(text, size) <= width * U)
            return text;
        var cut = text.Length;
        while (cut > 1 && Width(text[..cut] + "...", size) > width * U)
            cut--;
        return text[..cut].TrimEnd() + "...";
    }

    /// <summary>A rectangle's outline with its corners cut, by how much each: top left, top right, bottom right, bottom left.</summary>
    public Vector2[] Corners(Rect2 box, float a, float b, float c, float d)
    {
        var (p, e) = (box.Position, box.End);
        var (ca, cb, cc, cd) = (a * U, b * U, c * U, d * U);
        return
        [
            new(p.X + ca, p.Y), new(e.X - cb, p.Y), new(e.X, p.Y + cb), new(e.X, e.Y - cc),
            new(e.X - cc, e.Y), new(p.X + cd, e.Y), new(p.X, e.Y - cd), new(p.X, p.Y + ca),
        ];
    }

    /// <summary>A plate with two corners cut, top left and bottom right.</summary>
    public void Plate(Rect2 box, Color fill, Color edge, float cut) => Plate(Corners(box, cut, 0, cut, 0), fill, edge, 1.5f);

    public void Plate(Vector2[] points, Color fill, Color edge, float line)
    {
        // A plating with a little sheen at its top; a light of one colour stays that colour.
        var top = points.Min(p => p.Y);
        var height = Mathf.Max(points.Max(p => p.Y) - top, 1);
        var plain = fill != Plating;
        var colours = points.Select(p => plain ? fill : Sheen.Lerp(Plating, Mathf.Clamp((p.Y - top) / height * 1.6f, 0, 1))).ToArray();
        canvas.DrawPolygon(points, colours);
        canvas.DrawPolyline([.. points, points[0]], edge, Mathf.Max(line * U, 1), true);
    }

    /// <summary>A thin bar filled from its middle out by <paramref name="share"/>.</summary>
    public void Thin(Rect2 bar, float share, Color colour)
    {
        canvas.DrawRect(bar, new Color(colour, 0.18f));
        var width = bar.Size.X * Mathf.Clamp(share, 0, 1);
        canvas.DrawRect(new Rect2(bar.Position + new Vector2((bar.Size.X - width) / 2, 0), new Vector2(width, bar.Size.Y)), colour);
    }

    /// <summary>A bar filled from its left by <paramref name="share"/>, on a black ground.</summary>
    public void Bar(Rect2 bar, float share, Color colour)
    {
        canvas.DrawRect(bar, new Color(0, 0, 0, 0.8f));
        canvas.DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * Mathf.Clamp(share, 0, 1), bar.Size.Y)), colour);
    }

    /// <summary>One of <see cref="HangarGlyphs"/>' drawings in a box, with a glow.</summary>
    public void Glyph(float[][]? lines, Rect2 box, Color colour)
    {
        if (lines is null)
            return;
        var scale = box.Size / HangarGlyphs.Grid;
        foreach (var line in lines)
        {
            var points = new Vector2[line.Length / 2];
            for (var i = 0; i < points.Length; i++)
                points[i] = box.Position + new Vector2(line[i * 2], line[i * 2 + 1]) * scale;
            canvas.DrawPolyline(points, new Color(colour, 0.22f), 5 * U, true);
            canvas.DrawPolyline(points, colour, Mathf.Max(2 * U, 1), true);
        }
    }

    /// <summary>A key's cap on the top right corner of a box.</summary>
    public void Cap(Rect2 box, string? key)
    {
        if (key is null)
            return;
        var width = Mathf.Max(Width(key, 15) + 10 * U, 22 * U);
        var cap = new Rect2(box.Position + new Vector2(box.Size.X - width - 3 * U, 3 * U), new Vector2(width, 21 * U));
        canvas.DrawRect(cap, MenuTheme.Well);
        canvas.DrawRect(cap, MenuTheme.Edge, false, 1);
        Say(cap.Position + new Vector2(width / 2, 16 * U), key, 15, MenuTheme.Text, 0.5f);
    }

    /// <summary>The diamond on a place in view, <paramref name="r"/> pixels from its middle to a corner.</summary>
    public void Mark(Vector2 at, float r, Color tint) =>
        canvas.DrawPolyline([at + new Vector2(0, -r), at + new Vector2(r, 0), at + new Vector2(0, r), at + new Vector2(-r, 0), at + new Vector2(0, -r)], tint, 2 * U, true);

    /// <summary>An arrow whose tip stands at <paramref name="tip"/>, pointing <paramref name="way"/> (a unit vector), <paramref name="size"/> pixels long; returns its back.</summary>
    public Vector2 Arrow(Vector2 tip, Vector2 way, float size, Color tint)
    {
        var side = new Vector2(-way.Y, way.X);
        var back = tip - way * size;
        Vector2[] arrow = [tip, back + side * size * 0.6f, back + way * size * 0.3f, back - side * size * 0.6f];
        canvas.DrawColoredPolygon(arrow, tint);
        canvas.DrawPolyline([.. arrow, tip], new Color(Plating, 1), 2 * U, true);
        return back;
    }

    /// <summary>A pointer's word on a dark ground, so that it reads on sand as on snow, kept whole inside <paramref name="view"/>.</summary>
    public void Tag(Vector2 middle, string label, Color tint, Rect2 view)
    {
        var half = new Vector2(Width(label, 15) / 2 + 8 * U, 12 * U);
        // In a view narrower than the word (a run without a screen has a tiny one) it stands at the left edge.
        middle = new Vector2(Mathf.Clamp(middle.X, view.Position.X + half.X, Mathf.Max(view.Position.X + half.X, view.End.X - half.X)),
            Mathf.Clamp(middle.Y, view.Position.Y + half.Y, Mathf.Max(view.Position.Y + half.Y, view.End.Y - half.Y)));
        canvas.DrawRect(new Rect2(middle - half, half * 2), new Color(Plating, 0.72f));
        Say(middle + new Vector2(0, 5.5f * U), label, 15, new Color(tint, 1), 0.5f);
    }

    /// <summary>A line on a plate whose top stands at <paramref name="at"/>, centred there; returns the plate.</summary>
    public Rect2 Prompt(Vector2 at, string text, Color colour, float size)
    {
        var width = Width(text, size) + 40 * U;
        var height = (size + 20) * U;
        var box = new Rect2(at - new Vector2(width / 2, 0), new Vector2(width, height));
        Plate(box, new Color(Plating, Plating.A * colour.A), colour, 10);
        Say(box.GetCenter() + new Vector2(0, size * 0.36f * U), text, size, colour, 0.5f);
        return box;
    }

    /// <summary>
    /// A call across the view: a band dark across its middle and clear towards the ends, with a line in the call's colour at
    /// its top and foot, from <paramref name="middle"/> out by <paramref name="width"/> either way, its title and a line in it
    /// (cut to <paramref name="lineWidth"/> interface pixels). <paramref name="alpha"/> says how much of it shows.
    /// </summary>
    public void Call(float middle, float y, float width, string title, string? line, Color colour, float alpha, float lineWidth)
    {
        var height = (line is null ? 92 : 128) * U;
        var left = new Color(0, 0, 0, 0);
        var dark = new Color(0, 0, 0, 0.62f * alpha);
        canvas.DrawPolygon([new Vector2(middle - width, y), new Vector2(middle, y), new Vector2(middle, y + height), new Vector2(middle - width, y + height)], [left, dark, dark, left]);
        canvas.DrawPolygon([new Vector2(middle, y), new Vector2(middle + width, y), new Vector2(middle + width, y + height), new Vector2(middle, y + height)], [dark, left, left, dark]);
        var tint = new Color(colour, alpha);
        canvas.DrawLine(new Vector2(middle - width, y), new Vector2(middle + width, y), tint, 2 * U);
        canvas.DrawLine(new Vector2(middle - width, y + height), new Vector2(middle + width, y + height), tint, 2 * U);
        Say(new Vector2(middle, y + 66 * U), title, 58, new Color(Colors.White, alpha), 0.5f, outline: 2);
        if (line is not null)
            Say(new Vector2(middle, y + 106 * U), Fit(line, 25, lineWidth), 25, tint, 0.5f, outline: 2);
    }

    /// <summary>
    /// A row of LEDs set into a groove, lit from the left as far as <paramref name="share"/>, the last partly, in
    /// <paramref name="lit"/>, with a blip running along the lit ones at <paramref name="time"/> (as the loading screen's bar).
    /// </summary>
    public void Leds(Rect2 groove, float share, Color lit, float time, int count = 20)
    {
        var off = new Color(lit.R * 0.12f, lit.G * 0.12f, lit.B * 0.12f);
        canvas.DrawRect(groove, MenuTheme.Well);
        canvas.DrawRect(groove, MenuTheme.Edge, false, Mathf.Max(1.5f * U, 1));
        var inside = groove.Grow(-4 * U);
        var gap = 3 * U;
        var width = (inside.Size.X - gap * (count - 1)) / count;
        var on = Mathf.Clamp(share, 0, 1) * count;
        var blip = on >= 1 ? time * 18 % (on + 4) : -10;
        for (var i = 0; i < count; i++)
        {
            var led = new Rect2(inside.Position.X + i * (width + gap), inside.Position.Y, width, inside.Size.Y);
            var level = Mathf.Clamp(on - i, 0, 1);
            var hot = Mathf.Clamp(1 - Mathf.Abs(i - blip) / 2.5f, 0, 1) * level;
            if (level > 0)
                canvas.DrawRect(led.Grow(2 * U), new Color(lit, 0.16f * level + 0.2f * hot));
            canvas.DrawRect(led, off.Lerp(lit, level).Lerp(Colors.White, hot * 0.5f));
            canvas.DrawRect(new Rect2(led.Position + new Vector2(1, 1), new Vector2(led.Size.X - 2, led.Size.Y * 0.3f)), new Color(1, 1, 1, 0.04f + 0.22f * level));
        }
    }
}
