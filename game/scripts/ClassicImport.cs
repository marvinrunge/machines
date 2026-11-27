using System.Diagnostics;

namespace Machines.Game;

/// <summary>
/// Imports the original models and terrain from the player's own copy of the game, as tools/classic-export/run.sh
/// does: the exporter (shipped in <c>tools/classic-export</c>) loads <c>models.bin</c> and each planet's
/// <c>pdata/*.psb</c> with the original engine and writes them to <c>classic/</c>. Nothing from the game is
/// shipped with the remake; it only ever comes from the copy the player points at. A download that carries the
/// asset pack (<see cref="AssetPack"/>, <c>tools/release/build-complete.sh</c>) opens it with the copy instead of
/// exporting and upscaling, so that every look is there as it was made, byte for byte.
/// </summary>
public static class ClassicImport
{
    private static string ToolFolder => Path.Combine(Paths.Root, "tools", "classic-export");
    private static string Exporter => Path.Combine(ToolFolder, "classic_export.exe");

    /// <summary>The exporter is part of this build (release builds for Windows), or the asset pack is.</summary>
    public static bool Available => Packed || (OperatingSystem.IsWindows() && File.Exists(Exporter) && File.Exists(Path.Combine(ToolFolder, "models.txt")));

    /// <summary>The pack of every look's assets beside the game (<see cref="AssetPack"/>).</summary>
    public static string PackFolder => Path.Combine(Paths.Root, AssetPack.FolderName);

    /// <summary>This download carries the asset pack.</summary>
    public static bool Packed => File.Exists(Path.Combine(PackFolder, AssetPack.ManifestName));

    /// <summary>
    /// The pack beside the game has not been opened into <c>classic/</c> yet (a first start, or an update that brought a
    /// new pack): the import, or the copy imported before, opens it.
    /// </summary>
    public static bool PackWaiting => Packed && AssetPack.Read(PackFolder) is { } pack && AssetPack.Opened(Paths.Classic) != pack.Id;

    public static bool Imported => Directory.Exists(Path.Combine(Paths.Classic, "models"));

    /// <summary>
    /// What the exporter writes, raised when an import has to be redone: 2 added the original vertex normals
    /// (without them some terrain tiles and model parts are lit from behind), 3 the first-person eye on each
    /// machine's faceplate, 4 the turn-to-face sprites (eyes, lamps, leaves) and the materials' ambient and emissive
    /// colours, 5 the parts that move by themselves (rotors, bobbing hips, a weapon's energy ball) and their motion,
    /// buildings' moving parts and how they work, and the vertices' own materials (terrain tiles shade their corners
    /// with them; without, whole corner tiles came out dark), 6 the weapons' parts that move as they fire (the
    /// recoil, the autocannon's barrels), buildings' interiors as parts of their own, towers' turning heads, the
    /// support weapons' and loading effects' meshes, and each planet's artefacts (trees, rocks, ruins), 7 the silo's
    /// plans for opening and closing, 8 the pod's ion top, the locator's cycle, rig lights and more effects, 9 the
    /// bolters' muzzle flashes, 10 the light patch on the ground and the lights the rounds carry.
    /// </summary>
    public const int Format = 10;

    private static string FormatFile => Path.Combine(Paths.Classic, "format.txt");

    /// <summary>The import was made by an older exporter and should be run again.</summary>
    public static bool Outdated => Imported
        && !(File.Exists(FormatFile) && int.TryParse(File.ReadAllText(FormatFile).Trim(), out var format) && format >= Format);

    /// <summary>Where a copy unpacked from the CDs goes: beside the game, as an installed copy would be elsewhere.</summary>
    public static string DiscCopy => Path.Combine(Paths.Root, "game-copy");

    /// <summary>The copy the last import came from, while it is still there.</summary>
    public static string? CurrentCopy
    {
        get
        {
            var file = Path.Combine(Paths.Classic, "game_path.txt");
            if (!File.Exists(file))
                return null;
            var folder = File.ReadAllText(file).Trim();
            if (!Path.IsPathRooted(folder))
                folder = Path.GetFullPath(Path.Combine(Paths.Root, folder));
            return Directory.Exists(folder) ? folder : null;
        }
    }

    /// <summary>The copy came from the CDs but CD 2's films are not in it yet.</summary>
    public static bool FilmsMissing => CurrentCopy is { } copy
        && Path.GetFullPath(copy).Equals(Path.GetFullPath(DiscCopy), StringComparison.OrdinalIgnoreCase)
        && !GameDisc.HasFilms(copy);

    /// <returns>null when <paramref name="source"/> (a folder, a drive or a disc image) can be imported from, otherwise why not.</returns>
    public static string? WhyNotSource(string source)
    {
        using var disc = GameDisc.Open(source);
        if (disc is null)
            return File.Exists(source) ? Lang.T("That file is no disc image of a Machines CD.") : WhyNot(source);
        if (GameDisc.Which(disc) == GameDisc.Kind.Two && CurrentCopy is null)
            return Lang.T("This is CD 2, with the campaign's films. Import from CD 1 first.");
        return null;
    }

    /// <summary>
    /// Imports from a folder, a drive or a disc image: CD 1 is unpacked into <see cref="DiscCopy"/> first, with CD 2's
    /// films where CD 2 is found too, and imported from there; CD 2 alone adds its films to the copy imported before.
    /// Returns an error or null.
    /// </summary>
    public static string? FromSource(string source, Action<string> progress)
    {
        try
        {
            using var disc = GameDisc.Open(source);
            if (disc is null)
                return Run(source, progress);
            if (GameDisc.Which(disc) == GameDisc.Kind.Two)
            {
                AddFilms(disc, CurrentCopy!, progress);
                progress(Lang.T("Done."));
                return null;
            }
            var copy = DiscCopy;
            GameDisc.Install(disc, copy, (done, total) => progress(Lang.T("Copying the game from the CD: {0} of {1} files", done, total)));
            if (GameDisc.Second(source) is { } second)
            {
                using var films = GameDisc.Open(second);
                if (films is not null)
                    AddFilms(films, copy, progress);
            }
            return Run(copy, progress);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Lang.T("Reading the CD failed: {0}", exception.Message);
        }
    }

    private static void AddFilms(IDiscFiles disc, string copy, Action<string> progress) =>
        GameDisc.AddFilms(disc, copy, (done, total) => progress(Lang.T("Copying the films from CD 2: {0} of {1}", done, total)));

    /// <returns>null when the folder holds the game, otherwise why not.</returns>
    public static string? WhyNot(string gameFolder)
    {
        if (!File.Exists(Path.Combine(gameFolder, "models.bin")))
            return "That folder has no models.bin. Pick the folder Machines is installed in.";
        if (!Directory.Exists(Path.Combine(gameFolder, "pdata")))
            return "That folder has no pdata folder. Pick the folder Machines is installed in.";
        return null;
    }

    /// <summary>Runs the whole import; <paramref name="progress"/> gets a line per step. Returns an error or null.</summary>
    public static string? Run(string gameFolder, Action<string> progress)
    {
        if (Packed)
            return Open(gameFolder, progress);
        var data = Paths.OriginalData;
        var sounds = Path.Combine(data, "sounds", "snddef64.dat");
        Directory.CreateDirectory(Paths.Classic);

        progress("Exporting machines and buildings…");
        var models = Path.Combine(Paths.Classic, "models");
        Export([gameFolder, Path.Combine(ToolFolder, "models.txt"), models, sounds], "models.log");
        if (!Directory.Exists(models) || Directory.GetFiles(models, "*.json", SearchOption.AllDirectories).Length == 0)
            return "The model export failed; see classic/models.log.";

        // The exporter creates only the last folder of its output path.
        Directory.CreateDirectory(Path.Combine(Paths.Classic, "planets"));
        var planets = Directory.GetFiles(Path.Combine(data, "models", "planet"), "*.psf", SearchOption.AllDirectories)
            .Where(psf => File.Exists(Path.Combine(gameFolder, "pdata", Path.GetFileNameWithoutExtension(psf) + ".psb")))
            .ToList();
        for (var i = 0; i < planets.Count; i++)
        {
            var name = Path.GetFileNameWithoutExtension(planets[i]);
            progress($"Exporting terrain {i + 1}/{planets.Count}: {name}");
            Export(["--planet", gameFolder, planets[i], Path.Combine(Paths.Classic, "planets", name), sounds], "planets.log");
        }

        File.WriteAllText(Path.Combine(Paths.Classic, "game_path.txt"), Slashes(gameFolder));
        File.WriteAllText(FormatFile, Format.ToString());

        // The remake's sharper textures, made from the same copy.
        if (HdTextures.Run(gameFolder, progress) is { } error)
            return error;
        progress("Done.");
        return null;
    }

    /// <summary>
    /// Opens the asset pack with the game copy in <paramref name="gameFolder"/> into <c>classic/</c>: the copy must be
    /// the game's (<see cref="GameDisc.Check"/>), and its files are the key.
    /// </summary>
    private static string? Open(string gameFolder, Action<string> progress)
    {
        if (GameDisc.Check(gameFolder) != GameDisc.Proof.Owned)
            return Lang.T("That copy's files are not those of the Machines CD, so it cannot open the remake's assets.");
        try
        {
            Directory.CreateDirectory(Paths.Classic);
            AssetPack.Install(PackFolder, Paths.Classic, AssetPack.Key(gameFolder),
                (done, total) => progress(Lang.T("Unlocking the remake's assets: {0} of {1}", done, total)));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return Lang.T("Unlocking the remake's assets failed: {0}", exception.Message);
        }
        File.WriteAllText(Path.Combine(Paths.Classic, "game_path.txt"), Slashes(gameFolder));
        progress(Lang.T("Done."));
        return null;
    }

    private static void Export(string[] arguments, string log)
    {
        var start = new ProcessStartInfo(Exporter)
        {
            WorkingDirectory = ToolFolder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        // The engine's path class wants forward slashes.
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument.StartsWith("--", StringComparison.Ordinal) ? argument : Slashes(argument));

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var errors = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromMinutes(5)))
            process.Kill(entireProcessTree: true);
        File.AppendAllText(Path.Combine(Paths.Classic, log), output.Result + errors.Result);
    }

    private static string Slashes(string path) => Path.GetFullPath(path).Replace('\\', '/');
}
