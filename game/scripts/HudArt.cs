using Godot;

namespace Machines.Game;

/// <summary>
/// The first-person HUD's painted art (weapon panels, the radar's casing and its parts, machine and tower pictures) at
/// the exact size in screen pixels the layout draws it, so it is drawn one texel to one pixel. Where
/// <c>classic/remake/gui</c> holds a four-times enlargement of a bitmap (made from the player's game copy by
/// <c>tools/hud-sharp/build.js</c>, by its path under <c>gui/</c> in lower case, as PNG), that is shrunk to the size
/// with Lanczos; without it the original bitmap is blown up as ever, by whole pixels or by Scale2x. A scale that is not
/// a whole number (a HUD size other than the standard, <see cref="HudScale"/>) gives each bitmap the whole number of
/// pixels nearest to it, so it still lies a texel to a pixel. What the first moments in first person show (the radar
/// switching on, the weapon panels dropping) is shrunk beforehand on a worker thread (<see cref="Prepare"/>), as each of
/// the radar's frames takes some 30 ms.
/// </summary>
public static class HudArt
{
    /// <summary>How a bitmap without an enlargement is blown up.</summary>
    public enum Fallback { Nearest, Scale2x }

    private static readonly Dictionary<(string, int, int, Fallback), Texture2D?> Cache = [];

    // Enlargements shrunk ahead on a worker thread, by path and size, until a texture is made of them.
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<(string, int, int), Image> Prepared = new();

    // The enlargements are this many times the original's size (tools/hud-sharp/build.js).
    private const int Enlargement = 4;

    /// <summary>Where the enlargements lie.</summary>
    public static string Folder => Path.Combine(Paths.Classic, "remake", "gui");

    /// <summary>Forgets the textures made, as when the game copy changes.</summary>
    public static void Clear()
    {
        Cache.Clear();
        Prepared.Clear();
        _prepared = -1;
    }

    /// <summary>Screen pixels a length of the original's pixels takes at a scale: the nearest whole number.</summary>
    public static int Size(int pixels, float scale) => Mathf.RoundToInt(pixels * scale);

    // The scale the art was last made ready for.
    private static float _prepared = -1;

    /// <summary>
    /// Shrinks the enlargements of the radar's casing, start-up frames and bars and of the weapon panels, their frames and
    /// icons to a scale on a worker thread, so that first person begins without a hitch; does nothing without them, or
    /// when they were made ready for that scale already. What was made ready for another scale and not yet used is let go.
    /// </summary>
    public static void Prepare(float scale)
    {
        if (Mathf.IsEqualApprox(scale, _prepared))
            return;
        if (_prepared > 0)
            Prepared.Clear();
        _prepared = scale;
        if (!Directory.Exists(Folder))
            return;
        var folders = new[] { "fstpersn/radar", "fstpersn/weapon" };
        _ = Task.Run(() =>
        {
            foreach (var folder in folders)
            {
                var directory = Path.Combine(Folder, folder);
                if (!Directory.Exists(directory))
                    continue;
                foreach (var file in Directory.EnumerateFiles(directory, "*.png"))
                {
                    var path = $"{folder}/{Path.GetFileNameWithoutExtension(file)}.bmp";
                    var image = new Image();
                    if (image.Load(file) != Error.Ok || image.IsEmpty() || image.GetWidth() % Enlargement != 0 || image.GetHeight() % Enlargement != 0)
                        continue;
                    var (width, height) = (Size(image.GetWidth() / Enlargement, scale), Size(image.GetHeight() / Enlargement, scale));
                    if (Prepared.ContainsKey((path, width, height)))
                        continue;
                    image.Convert(Image.Format.Rgba8);
                    image.Resize(width, height, Image.Interpolation.Lanczos);
                    Prepared[(path, width, height)] = image;
                }
            }
        });
    }

    /// <summary>
    /// A bitmap by its path under <c>gui/</c> at a scale of screen pixels to each of its own, the nearest whole number of
    /// pixels wide and high; null when the game copy has no such bitmap. Without an enlargement a whole scale blows it
    /// up by whole pixels (or Scale2x), any other by Scale2x and then Lanczos to the size.
    /// </summary>
    public static Texture2D? Get(string path, float scale, Fallback fallback = Fallback.Nearest)
    {
        if (ClassicGui.Bitmap(path) is not { } original)
            return null;
        var (width, height) = (Size(original.GetWidth(), scale), Size(original.GetHeight(), scale));
        var key = (path.ToLowerInvariant(), width, height, fallback);
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var image = Enlarged(path, width, height);
        if (image is null)
        {
            image = original.GetImage();
            if (fallback == Fallback.Scale2x || scale % 1 != 0)
            {
                for (var grown = 1; grown < scale; grown *= 2)
                    image = ClassicCursor.Scale2x(image);
                if (image.GetWidth() != width || image.GetHeight() != height)
                    image.Resize(width, height, Image.Interpolation.Lanczos);
            }
            else if (scale > 1)
                image.Resize(width, height, Image.Interpolation.Nearest);
        }
        return Cache[key] = ImageTexture.CreateFromImage(image);
    }

    /// <summary>A bitmap's enlargement shrunk to a size in screen pixels; null without one.</summary>
    public static Texture2D? Sized(string path, int width, int height)
    {
        var key = (path.ToLowerInvariant(), width, height, (Fallback)(-1));
        if (Cache.TryGetValue(key, out var cached))
            return cached;
        var image = Enlarged(path, width, height);
        return Cache[key] = image is null ? null : ImageTexture.CreateFromImage(image);
    }

    private static Image? Enlarged(string path, int width, int height)
    {
        if (Prepared.TryRemove((path.ToLowerInvariant(), width, height), out var ready))
            return ready;
        var file = Path.Combine(Folder, Path.ChangeExtension(path.ToLowerInvariant(), ".png"));
        if (width <= 0 || height <= 0 || !File.Exists(file))
            return null;
        var image = new Image();
        if (image.Load(file) != Error.Ok || image.IsEmpty())
            return null;
        image.Convert(Image.Format.Rgba8);
        if (image.GetWidth() != width || image.GetHeight() != height)
            image.Resize(width, height, Image.Interpolation.Lanczos);
        return image;
    }
}
