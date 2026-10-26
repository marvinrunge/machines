using System.Diagnostics;

namespace Machines.Game;

/// <summary>
/// Imports the original models and terrain from the player's own copy of the game, as tools/classic-export/run.sh
/// does: the exporter (shipped in <c>tools/classic-export</c>) loads <c>models.bin</c> and each planet's
/// <c>pdata/*.psb</c> with the original engine and writes them to <c>classic/</c>. Nothing from the game is
/// shipped with the remake; it only ever comes from the copy the player points at.
/// </summary>
public static class ClassicImport
{
    private static string ToolFolder => Path.Combine(Paths.Root, "tools", "classic-export");
    private static string Exporter => Path.Combine(ToolFolder, "classic_export.exe");

    /// <summary>The exporter is part of this build (release builds for Windows).</summary>
    public static bool Available => OperatingSystem.IsWindows() && File.Exists(Exporter) && File.Exists(Path.Combine(ToolFolder, "models.txt"));

    public static bool Imported => Directory.Exists(Path.Combine(Paths.Classic, "models"));

    /// <summary>
    /// What the exporter writes, raised when an import has to be redone: 2 added the original vertex normals
    /// (without them some terrain tiles and model parts are lit from behind), 3 the first-person eye on each
    /// machine's faceplate, 4 the turn-to-face sprites (eyes, lamps, leaves) and the materials' ambient and emissive
    /// colours, 5 the parts that move by themselves (rotors, bobbing hips, a weapon's energy ball) and their motion,
    /// buildings' moving parts and how they work, and the vertices' own materials (terrain tiles shade their corners
    /// with them; without, whole corner tiles came out dark).
    /// </summary>
    public const int Format = 5;

    private static string FormatFile => Path.Combine(Paths.Classic, "format.txt");

    /// <summary>The import was made by an older exporter and should be run again.</summary>
    public static bool Outdated => Imported
        && !(File.Exists(FormatFile) && int.TryParse(File.ReadAllText(FormatFile).Trim(), out var format) && format >= Format);

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
