using Godot;

namespace Machines.Game;

/// <summary>
/// The original's mouse cursors (MachInGameCursors2d): bitmaps from <c>gui/cursor/large</c> of the player's game
/// copy, animated at ten frames a second, magenta cut out, held at their centre (the arrow and the edge
/// arrows at their points), and scaled up as the control panel is. Without a game copy the system cursor stays.
/// </summary>
public partial class ClassicCursor : Node
{
    // Name, frames and hot spot (null: the middle), as loadCursors set them up.
    private static readonly Dictionary<string, (int Frames, Vector2I? Origin)> Cursors = new()
    {
        ["arrow"] = (1, new Vector2I(0, 0)),
        ["select"] = (1, null),
        ["noentry"] = (1, null),
        ["move"] = (4, null),
        ["attack"] = (2, null),
        ["enter"] = (6, null),
        ["entapc"] = (6, null),
        ["follow"] = (4, null),
        ["patrol"] = (6, null),
        ["heal"] = (6, null),
        ["repair"] = (1, null),
        ["capture"] = (8, null),
        ["jconst"] = (11, null),
        ["const"] = (11, null),
        ["deconst"] = (9, null),
        ["recycle"] = (4, null),
        ["locat"] = (5, new Vector2I(8, 14)),
        ["deploy"] = (2, null),
        ["assembl"] = (4, null),
        ["pickup"] = (2, null),
        ["putdn"] = (2, null),
        ["scrln"] = (1, new Vector2I(6, 0)),
        ["scrls"] = (1, new Vector2I(6, 13)),
        ["scrle"] = (1, new Vector2I(13, 6)),
        ["scrlw"] = (1, new Vector2I(0, 6)),
        ["scrlne"] = (1, new Vector2I(14, 0)),
        ["scrlnw"] = (1, new Vector2I(0, 0)),
        ["scrlse"] = (1, new Vector2I(14, 14)),
        ["scrlsw"] = (1, new Vector2I(0, 14)),
    };

    private readonly Dictionary<(string, int, int), (Texture2D Texture, Vector2 Hotspot)?> _frames = [];
    private (string Kind, int Frame, int Scale) _shown;
    private double _time;

    /// <summary>Which cursor shows, by its file name (<c>move</c>, <c>attack</c>, ...).</summary>
    public string Kind { get; set; } = "arrow";

    public override void _Process(double delta)
    {
        if (!ClassicGui.Available || !Cursors.TryGetValue(Kind, out var cursor))
            return;
        _time += delta;
        var frame = (int)(_time * 10) % cursor.Frames;
        // The original ran at 640 by 480; its cursors grow with the screen as the panel does.
        var scale = Math.Max(1, (int)Math.Round(GetViewport().GetVisibleRect().Size.Y / 480f));
        if (_shown == (Kind, frame, scale))
            return;
        _shown = (Kind, frame, scale);
        if (Frame(Kind, frame, scale, cursor) is { } shown)
            Input.SetCustomMouseCursor(shown.Texture, Input.CursorShape.Arrow, shown.Hotspot);
    }

    public override void _ExitTree() => Input.SetCustomMouseCursor(null);

    /// <summary>
    /// Doubles a pixel picture the Scale2x (EPX) way: each pixel becomes four, and a corner takes its neighbours'
    /// colour where they agree, so diagonal edges come out smooth instead of stepped.
    /// </summary>
    public static Image Scale2x(Image source)
    {
        var (w, h) = (source.GetWidth(), source.GetHeight());
        var result = Image.CreateEmpty(w * 2, h * 2, false, Image.Format.Rgba8);
        Color At(int x, int y) => source.GetPixel(Math.Clamp(x, 0, w - 1), Math.Clamp(y, 0, h - 1));
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var (p, a, b, c, d) = (At(x, y), At(x, y - 1), At(x + 1, y), At(x - 1, y), At(x, y + 1));
                result.SetPixel(2 * x, 2 * y, c == a && c != d && a != b ? a : p);
                result.SetPixel(2 * x + 1, 2 * y, a == b && a != c && b != d ? b : p);
                result.SetPixel(2 * x, 2 * y + 1, d == c && d != b && c != a ? c : p);
                result.SetPixel(2 * x + 1, 2 * y + 1, b == d && b != a && d != c ? d : p);
            }
        }
        return result;
    }

    private (Texture2D Texture, Vector2 Hotspot)? Frame(string kind, int frame, int scale, (int Frames, Vector2I? Origin) cursor)
    {
        if (_frames.TryGetValue((kind, frame, scale), out var cached))
            return cached;
        var name = cursor.Frames > 1 ? $"cursor/large/{kind}{frame + 1}.bmp" : $"cursor/large/{kind}.bmp";
        (Texture2D, Vector2)? result = null;
        if (ClassicGui.Bitmap(name)?.GetImage() is { } image)
        {
            var origin = cursor.Origin ?? new Vector2I(image.GetWidth() / 2, image.GetHeight() / 2);
            var (width, height) = (image.GetWidth() * scale, image.GetHeight() * scale);
            for (var grown = 1; grown < scale; grown *= 2)
                image = Scale2x(image);
            if (image.GetWidth() != width)
                image.Resize(width, height, Image.Interpolation.Lanczos);
            result = (ImageTexture.CreateFromImage(image), origin * scale);
        }
        _frames[(kind, frame, scale)] = result;
        return result;
    }
}
