using Godot;

namespace Machines.Game;

/// <summary>
/// The chip every menu panel and button is (tools/blender/menu-art.py, chip.png): a dark package round a green screen,
/// with pins down its left and right sides. The texture is a nine-slice at twice the menu's scale: its corners are
/// drawn as they are and its edges repeated a pin at a time, so a taller chip gets more pins, never stretched ones.
/// The screen is drawn here, a green gradient that lights up under the mouse as the original's buttons did
/// (gui/menu/glow.bmp).
/// </summary>
public static class ChipArt
{
    // In pixels of the texture: the body's edge lies Inset in from the texture's, the screen Frame in from the body's.
    private const float Size = 256, Inset = 40, Frame = 16, Corner = 64, Pitch = 32;

    private static Texture2D? _texture, _light;

    private static Texture2D Texture => _texture ??= GD.Load<Texture2D>("res://assets/menu/chip.png");

    private static Texture2D Light => _light ??= new GradientTexture2D
    {
        Width = 128,
        Height = 128,
        Fill = GradientTexture2D.FillEnum.Radial,
        FillFrom = new Vector2(0.5f, 0.5f),
        FillTo = new Vector2(1f, 0.5f),
        Gradient = new Gradient
        {
            Offsets = [0f, 0.3f, 0.75f, 1f],
            Colors = [new Color(0.86f, 1f, 0.72f), new Color(0.38f, 0.98f, 0.24f), new Color(0.1f, 0.55f, 0.08f, 0.85f), new Color(0.05f, 0.3f, 0.05f, 0.6f)],
        },
    };

    /// <summary>How far in from the chip's edge its screen begins, in menu pixels at the given art scale.</summary>
    public static float FrameWidth(float scale) => Frame * scale;

    /// <summary>
    /// Draws a chip whose body fills <paramref name="body"/>; its pins and shadow reach out beyond it. Scale is menu
    /// pixels per texture pixel, light how far its screen is lit (0 to 1), power how far it is switched on.
    /// </summary>
    public static void Draw(CanvasItem item, Rect2 body, float scale, float light = 0, float power = 1, bool dim = false) =>
        Draw(item.GetCanvasItem(), body, scale, light, power, dim);

    /// <summary>The same onto a canvas item by its id, as a <see cref="StyleBox"/> draws (<see cref="ChipStyle"/>).</summary>
    public static void Draw(Rid item, Rect2 body, float scale, float light = 0, float power = 1, bool dim = false)
    {
        DrawScreen(item, Screen(body, scale), power, dim);
        Glow(item, body, scale, light * power);
        DrawFrame(item, body.Grow(Inset * scale), scale);
    }

    /// <summary>Only the green light on a chip's screen, over the chip.</summary>
    public static void Glow(Rid item, Rect2 body, float scale, float light)
    {
        if (light > 0.001f)
            RenderingServer.CanvasItemAddTextureRect(item, Screen(body, scale), Light.GetRid(), false, new Color(1, 1, 1, light));
    }

    private static Rect2 Screen(Rect2 body, float scale) => body.Grow(-(Frame - 3) * scale);

    private static void DrawScreen(Rid item, Rect2 r, float power, bool dim)
    {
        Color Shade(float red, float green, float blue)
        {
            var c = new Color(red, green, blue).Lerp(new Color(0.012f, 0.03f, 0.014f), 1 - power);
            return dim ? c.Lerp(new Color(c.V, c.V, c.V) * 0.8f, 0.6f) : c;
        }
        RenderingServer.CanvasItemAddPolygon(item,
            [r.Position, new Vector2(r.End.X, r.Position.Y), r.End, new Vector2(r.Position.X, r.End.Y)],
            [Shade(0.07f, 0.2f, 0.08f), Shade(0.05f, 0.15f, 0.06f), Shade(0.022f, 0.075f, 0.03f), Shade(0.04f, 0.12f, 0.05f)]);
        // A faint reflection across the glass from the top left.
        RenderingServer.CanvasItemAddPolygon(item,
            [r.Position, new Vector2(r.Position.X + r.Size.X * 0.6f, r.Position.Y), new Vector2(r.Position.X, r.Position.Y + r.Size.Y * 0.9f)],
            [new Color(0.7f, 1f, 0.7f, 0.06f * power), new Color(0.7f, 1f, 0.7f, 0), new Color(0.7f, 1f, 0.7f, 0)]);
    }

    private static void DrawFrame(Rid item, Rect2 outer, float scale)
    {
        var texture = Texture.GetRid();
        var c = Corner * scale;
        var middle = outer.Size - new Vector2(2 * c, 2 * c);
        var across = Math.Max(1, (int)MathF.Round(middle.X / (Pitch * scale)));
        var down = Math.Max(1, (int)MathF.Round(middle.Y / (Pitch * scale)));
        var piece = new Vector2(middle.X / across, middle.Y / down);
        var (x0, y0, x1, y1) = (outer.Position.X, outer.Position.Y, outer.End.X - c, outer.End.Y - c);
        const float far = Size - Corner;

        void Put(float x, float y, float w, float h, float sx, float sy, float sw, float sh) =>
            RenderingServer.CanvasItemAddTextureRectRegion(item, new Rect2(x, y, w, h), texture, new Rect2(sx, sy, sw, sh));

        Put(x0, y0, c, c, 0, 0, Corner, Corner);
        Put(x1, y0, c, c, far, 0, Corner, Corner);
        Put(x0, y1, c, c, 0, far, Corner, Corner);
        Put(x1, y1, c, c, far, far, Corner, Corner);
        for (var i = 0; i < across; i++)
        {
            Put(x0 + c + i * piece.X, y0, piece.X, c, Corner, 0, Pitch, Corner);
            Put(x0 + c + i * piece.X, y1, piece.X, c, Corner, far, Pitch, Corner);
        }
        for (var i = 0; i < down; i++)
        {
            Put(x0, y0 + c + i * piece.Y, c, piece.Y, 0, Corner, Corner, Pitch);
            Put(x1, y0 + c + i * piece.Y, c, piece.Y, far, Corner, Corner, Pitch);
        }
        Put(x0 + c, y0 + c, middle.X, middle.Y, Corner, Corner, Size - 2 * Corner, Size - 2 * Corner);
    }
}

/// <summary>
/// The chip as a <see cref="StyleBox"/>, so the theme can put it under Godot's own controls (plain buttons, panels):
/// lit for a button under the mouse or pressed, greyed when disabled, and only the light for the keyboard's focus,
/// which Godot draws over the button's own box.
/// </summary>
public partial class ChipStyle : StyleBox
{
    public float ArtScale { get; set; } = 0.42f;
    public float Light { get; set; }
    public bool Dim { get; set; }
    public bool GlowOnly { get; set; }

    /// <summary>Content margins that keep what the box holds on its screen, this far in from the frame.</summary>
    public ChipStyle WithPadding(float across, float down)
    {
        var frame = ChipArt.FrameWidth(ArtScale);
        ContentMarginLeft = ContentMarginRight = frame + across;
        ContentMarginTop = ContentMarginBottom = frame + down;
        return this;
    }

    public override void _Draw(Rid toCanvasItem, Rect2 rect)
    {
        if (GlowOnly)
            ChipArt.Glow(toCanvasItem, rect, ArtScale, Light);
        else
            ChipArt.Draw(toCanvasItem, rect, ArtScale, Light, dim: Dim);
    }
}
