using Godot;

namespace Machines.Game;

/// <summary>
/// One of the original's bitmap fonts (GuiBmpFont), e.g. <c>gui/menu/promtfnt.bmp</c>: a single row of characters,
/// one for each character code from 0, each as wide as the run of pixels before the next white one in the
/// bottom line, which itself is not drawn. Spaces are 7 pixels and characters 1 apart, as getFont's defaults.
/// </summary>
public sealed class ClassicFont
{
    private const int SpaceWidth = 7, Spacing = 1;

    private static readonly Dictionary<string, ClassicFont?> Fonts = [];

    private readonly Texture2D _texture;
    private readonly List<(int Offset, int Width)> _characters = [];

    private ClassicFont(Texture2D texture, Image image)
    {
        _texture = texture;
        Height = image.GetHeight() - 1;
        var bottom = image.GetHeight() - 1;
        var x = 0;
        while (x < image.GetWidth())
        {
            var start = x;
            while (x < image.GetWidth() && image.GetPixel(x, bottom) is not { R8: 255, G8: 255, B8: 255, A8: 255 })
                x++;
            _characters.Add((start, x - start));
            x++;
        }
    }

    /// <summary>The height of a line of it, in its own pixels.</summary>
    public int Height { get; }

    /// <summary>A font by its path under <c>gui/</c>; null without a game copy.</summary>
    public static ClassicFont? Get(string path)
    {
        if (Fonts.TryGetValue(path, out var cached))
            return cached;
        ClassicFont? font = null;
        if (ClassicGui.Bitmap(path) is { } texture && texture.GetImage() is { } image)
            font = new ClassicFont(texture, image);
        Fonts[path] = font;
        return font;
    }

    private int Width(char c) => c == ' ' ? SpaceWidth : c < _characters.Count ? _characters[c].Width : 0;

    /// <summary>How wide a text is in the font's own pixels.</summary>
    public int Measure(string text) => text.Sum(c => Width(c) is var w and > 0 ? w + Spacing : 0);

    /// <summary>
    /// Draws a text with its top left at <paramref name="at"/>, each font pixel <paramref name="scale"/> screen pixels,
    /// up to <paramref name="width"/> font pixels long (MachGuiMenuText's chopped lines never are).
    /// </summary>
    public void Draw(CanvasItem canvas, Vector2 at, string text, float scale, int width = int.MaxValue)
    {
        var x = 0;
        foreach (var c in text)
        {
            var w = Width(c);
            if (w == 0)
                continue;
            if (x + w > width)
                break;
            if (c != ' ')
            {
                var (offset, _) = _characters[c];
                canvas.DrawTextureRectRegion(_texture, new Rect2(at + new Vector2(x, 0) * scale, new Vector2(w + 1, Height) * scale),
                    new Rect2(offset, 0, w + 1, Height));
            }
            x += w + Spacing;
        }
    }

    /// <summary>A text broken into lines no wider than <paramref name="width"/>, at spaces (MachGuiMenuText::chopUpText).</summary>
    public List<string> Chop(string text, int width)
    {
        var lines = new List<string>();
        var line = "";
        foreach (var word in text.Split(' '))
        {
            var candidate = line.Length == 0 ? word : line + " " + word;
            if (line.Length > 0 && Measure(candidate) > width)
            {
                lines.Add(line);
                line = word;
            }
            else
                line = candidate;
        }
        if (line.Length > 0)
            lines.Add(line);
        return lines;
    }
}
