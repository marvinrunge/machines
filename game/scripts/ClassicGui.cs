using Godot;

namespace Machines.Game;

/// <summary>
/// The original interface's icons, read from the player's game copy (its <c>gui</c> folder). As the
/// original renderer did, magenta is see-through. File names are matched regardless of case: the game ships
/// <c>move.BMP</c> beside <c>attack.bmp</c>.
/// </summary>
public static class ClassicGui
{
    private static string? _root;
    private static readonly Dictionary<string, Texture2D?> Bitmaps = [];
    private static readonly Dictionary<string, Dictionary<string, string>> Folders = [];

    /// <summary>Points the loader at a game copy; null leaves the interface without original bitmaps.</summary>
    public static void Use(string? gameFolder)
    {
        var root = gameFolder is null ? null : Path.Combine(gameFolder, "gui");
        if (root == _root)
            return;
        _root = root is not null && Directory.Exists(root) ? root : null;
        Bitmaps.Clear();
        Folders.Clear();
    }

    public static bool Available => _root is not null;

    /// <summary>A bitmap by its path under <c>gui/</c>, e.g. <c>commands/move.bmp</c>; null when missing.</summary>
    public static Texture2D? Bitmap(string path, bool blackIsClear = false)
    {
        if (_root is null)
            return null;
        var key = path.ToLowerInvariant() + (blackIsClear ? "|black" : "");
        if (Bitmaps.TryGetValue(key, out var cached))
            return cached;
        Texture2D? texture = null;
        if (Find(path.ToLowerInvariant()) is { } file && ClassicAssets.DecodeBmp(File.ReadAllBytes(file)) is { } image)
        {
            KeyOut(image, blackIsClear);
            texture = ImageTexture.CreateFromImage(image);
        }
        Bitmaps[key] = texture;
        return texture;
    }


    private static string? Find(string relative)
    {
        var folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? "";
        if (!Folders.TryGetValue(folder, out var files))
        {
            files = [];
            var directory = _root!;
            foreach (var part in folder.Split('/', StringSplitOptions.RemoveEmptyEntries))
            {
                var match = Directory.Exists(directory)
                    ? Directory.EnumerateDirectories(directory).FirstOrDefault(d => Path.GetFileName(d).Equals(part, StringComparison.OrdinalIgnoreCase))
                    : null;
                if (match is null)
                {
                    directory = "";
                    break;
                }
                directory = match;
            }
            if (directory.Length > 0)
                foreach (var file in Directory.EnumerateFiles(directory))
                    files[Path.GetFileName(file).ToLowerInvariant()] = file;
            Folders[folder] = files;
        }
        return files.GetValueOrDefault(Path.GetFileName(relative));
    }

    private static void KeyOut(Image image, bool black)
    {
        for (var y = 0; y < image.GetHeight(); y++)
            for (var x = 0; x < image.GetWidth(); x++)
            {
                var c = image.GetPixel(x, y);
                if (c.R8 == 255 && c.G8 == 0 && c.B8 == 255 || black && c.R8 == 0 && c.G8 == 0 && c.B8 == 0)
                    image.SetPixel(x, y, Colors.Transparent);
            }
    }
}
