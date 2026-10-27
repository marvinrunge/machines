using System.Diagnostics;
using System.Text.RegularExpressions;
using Godot;

namespace Machines.Game;

/// <summary>
/// Sharper textures for the remake look, made from the player's own copy of the game: every original texture the
/// imported models and planets use, enlarged four times by Real-ESRGAN (<c>tools/texture-upscale</c>), with a
/// normal map and a roughness map derived from each. They are written to <c>classic/hd</c> and, like the rest of
/// <c>classic/</c>, never shipped. Without the upscaler (or a GPU it can use) the textures are enlarged with
/// Lanczos filtering instead, and still get their maps.
/// </summary>
public static partial class HdTextures
{
    /// <summary>What <see cref="Run"/> writes, raised when the textures have to be made again.</summary>
    public const int Format = 1;

    private const int Scale = 4;

    private static string ToolFolder => Path.Combine(Paths.Root, "tools", "texture-upscale", "bin");
    private static string Upscaler => Path.Combine(ToolFolder, "realesrgan-ncnn-vulkan.exe");
    private static string Folder => Path.Combine(Paths.Classic, "hd");
    private static string FormatFile => Path.Combine(Folder, "format.txt");

    /// <summary>The upscaler is part of this build (see tools/texture-upscale/fetch.sh).</summary>
    public static bool UpscalerAvailable => OperatingSystem.IsWindows() && File.Exists(Upscaler);

    /// <summary>The HD textures are made, by this version.</summary>
    public static bool Made => File.Exists(FormatFile)
        && int.TryParse(File.ReadAllText(FormatFile).Trim(), out var format) && format >= Format;

    /// <summary>The enlarged texture for an original one (its path in the game folder), whether made or not.</summary>
    public static string AlbedoPath(string relative) => Path.Combine(Folder, Key(relative) + ".png");

    /// <summary>Its normal map (OpenGL convention: green points up the texture).</summary>
    public static string NormalPath(string relative) => Path.Combine(Folder, Key(relative) + "_n.png");

    /// <summary>Its roughness map (grey).</summary>
    public static string RoughnessPath(string relative) => Path.Combine(Folder, Key(relative) + "_r.png");

    /// <summary>Its metal mask (grey, white is bare metal), for machines and buildings; see <see cref="MakeMetalMap"/>.</summary>
    public static string MetalPath(string relative) => Path.Combine(Folder, Key(relative) + "_m.png");

    /// <summary>
    /// The metal mask of a model texture, made from its enlarged picture the first time it is asked for: grey,
    /// lighter parts are bare metal (struts, barrels, plating), coloured paint and the dark parts (rubber, black
    /// paint, shadow painted in) are not. False when the HD texture has not been made.
    /// </summary>
    public static bool MakeMetalMap(string relative)
    {
        var path = MetalPath(relative);
        if (File.Exists(path))
            return true;
        var albedoPath = AlbedoPath(relative);
        if (!File.Exists(albedoPath) || Image.LoadFromFile(albedoPath) is not { } albedo)
            return false;
        albedo.Convert(Image.Format.Rgba8);
        var (w, h) = (albedo.GetWidth(), albedo.GetHeight());
        var rgba = albedo.GetData();
        var metal = new byte[w * h];
        for (var i = 0; i < metal.Length; i++)
        {
            var (r, g, b) = (rgba[i * 4] / 255f, rgba[i * 4 + 1] / 255f, rgba[i * 4 + 2] / 255f);
            var high = Mathf.Max(r, Mathf.Max(g, b));
            var saturation = high > 0.01f ? (high - Mathf.Min(r, Mathf.Min(g, b))) / high : 0;
            var amount = Mathf.SmoothStep(0.32f, 0.12f, saturation) * Mathf.SmoothStep(0.22f, 0.5f, high);
            metal[i] = (byte)Mathf.RoundToInt(amount * 255);
        }
        try
        {
            Save(Image.CreateFromData(w, h, false, Image.Format.L8, metal), path);
            return true;
        }
        catch (IOException error)
        {
            GD.PushWarning(error.Message);
            return false;
        }
    }

    private static string Key(string relative)
    {
        var key = relative.Replace('\\', '/').ToLowerInvariant();
        return key.EndsWith(".bmp", StringComparison.Ordinal) ? key[..^4] : key;
    }

    /// <summary>
    /// Real-ESRGAN's models, chosen by comparison: the video model keeps the grain of sand and rock on the
    /// planets (the others smooth it into plastic), the anime model draws the machines' painted panels and
    /// lines crisply.
    /// </summary>
    private static string ModelFor(bool planet) => planet ? "realesr-animevideov3" : "realesrgan-x4plus-anime";

    /// <summary>One texture to enlarge.</summary>
    private sealed class Job
    {
        public required string Relative { get; init; }
        public required bool Planet { get; init; }
        public required int Width { get; init; }
        public required int Height { get; init; }

        /// <summary>Original pixels added round each edge (wrapped from the far side), cut off again afterwards.</summary>
        public required int Pad { get; init; }

        /// <summary>See-through textures: the coverage, 0 to 1, at original size.</summary>
        public float[]? Alpha { get; init; }

        public string Staged => $"{Index:D5}.png";
        public int Index { get; set; }
    }

    /// <summary>Makes every HD texture; <paramref name="progress"/> gets a line per step. Returns an error or null.</summary>
    public static string? Run(string gameFolder, Action<string> progress)
    {
        progress("Sharpening textures: finding them…");
        var work = Path.Combine(Paths.Classic, "hd-work");
        if (Directory.Exists(work))
            Directory.Delete(work, recursive: true);
        var jobs = new List<Job>();
        foreach (var relative in UsedTextures(gameFolder))
            if (Stage(gameFolder, relative, work) is { } job)
            {
                job.Index = jobs.Count;
                jobs.Add(job);
                SaveStaged(gameFolder, job, work);
            }
        if (jobs.Count == 0)
            return "No textures found to sharpen. Import the original models first.";

        // Planets and models go through the upscaler separately, each with its own model.
        var done = 0;
        foreach (var planet in new[] { true, false })
        {
            var batch = jobs.Where(j => j.Planet == planet).ToList();
            var input = Path.Combine(work, planet ? "planets" : "models");
            var output = input + "-x4";
            Directory.CreateDirectory(output);
            if (UpscalerAvailable)
                Upscale(input, output, ModelFor(planet), batch.Count, count => progress($"Sharpening textures {done + count}/{jobs.Count}…"));
            done += batch.Count;
        }

        progress("Sharpening textures: normal and roughness maps…");
        Directory.CreateDirectory(Folder);
        var finished = 0;
        var fallbacks = 0;
        Parallel.ForEach(jobs, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount / 2) }, job =>
        {
            if (!Finish(job, work))
                Interlocked.Increment(ref fallbacks);
            var count = Interlocked.Increment(ref finished);
            if (count % 50 == 0)
                progress($"Sharpening textures: maps {count}/{jobs.Count}…");
        });

        Directory.Delete(work, recursive: true);
        File.WriteAllText(FormatFile, Format.ToString());
        GD.Print($"HD textures: {jobs.Count} made, {fallbacks} with Lanczos instead of Real-ESRGAN");
        return null;
    }

    [GeneratedRegex("\"texture\":\"([^\"]+)\"")]
    private static partial Regex TextureReference();

    /// <summary>
    /// The textures the imported models and planets name, their race variants (see
    /// <see cref="ClassicAssets.RaceVariants"/>) and, for see-through ones, the colour and alpha maps beside them.
    /// </summary>
    private static IEnumerable<string> UsedTextures(string gameFolder)
    {
        var named = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var json in Directory.EnumerateFiles(Paths.Classic, "*.json", SearchOption.AllDirectories))
            foreach (Match match in TextureReference().Matches(File.ReadAllText(json)))
                named.Add(match.Groups[1].Value);
        var all = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var texture in named)
            foreach (var variant in ClassicAssets.RaceVariants(texture).Prepend(texture))
                if (File.Exists(Path.Combine(gameFolder, variant)))
                    all.Add(variant);
        return all;
    }

    /// <summary>
    /// Reads a texture as the game shows it (see ClassicAssets.LoadTexture): see-through ones get their coverage
    /// from the alpha map beside them or from the magenta key. Returns null for what cannot be read.
    /// </summary>
    private static Job? Stage(string gameFolder, string relative, string work)
    {
        var colour = Read(gameFolder, relative, out var alpha);
        if (colour is null)
            return null;
        var (width, height) = (colour.GetWidth(), colour.GetHeight());
        return new Job
        {
            Relative = relative,
            Planet = Key(relative).StartsWith("models/planet/", StringComparison.Ordinal),
            Width = width,
            Height = height,
            Pad = Math.Clamp(Math.Min(width, height) / 4, 1, 8),
            Alpha = alpha,
        };
    }

    private static Image? Read(string gameFolder, string relative, out float[]? alpha)
    {
        alpha = null;
        var path = Path.Combine(gameFolder, relative);
        var name = Path.GetFileName(relative).ToLowerInvariant();
        var transparent = name.EndsWith("_t.bmp", StringComparison.Ordinal) || name.EndsWith("_bt.bmp", StringComparison.Ordinal);
        if (transparent)
        {
            string Sibling(char letter) => path[..^5] + letter + path[^4..];
            if (File.Exists(Sibling('c')) && File.Exists(Sibling('a'))
                && ClassicAssets.DecodeBmp(File.ReadAllBytes(Sibling('c'))) is { } soft
                && ClassicAssets.DecodeBmp(File.ReadAllBytes(Sibling('a'))) is { } map && soft.GetSize() == map.GetSize())
            {
                var mapData = map.GetData();
                alpha = new float[soft.GetWidth() * soft.GetHeight()];
                for (var i = 0; i < alpha.Length; i++)
                    alpha[i] = mapData[i * 4] / 255f;
                return soft;
            }
        }
        if (!File.Exists(path) || ClassicAssets.DecodeBmp(File.ReadAllBytes(path)) is not { } image)
            return null;
        if (transparent)
        {
            // Magenta is cut out. The upscaler would smear it into a pink fringe, so the cut-out pixels take the
            // colour of their neighbours first and the coverage is enlarged on its own.
            var data = image.GetData();
            alpha = new float[image.GetWidth() * image.GetHeight()];
            for (var i = 0; i < alpha.Length; i++)
                alpha[i] = data[i * 4] == 255 && data[i * 4 + 1] == 0 && data[i * 4 + 2] == 255 ? 0 : 1;
            FillCutOut(data, alpha, image.GetWidth(), image.GetHeight());
            image = Image.CreateFromData(image.GetWidth(), image.GetHeight(), false, Image.Format.Rgba8, data);
        }
        return image;
    }

    /// <summary>Gives cut-out pixels the average colour of their covered neighbours, growing inwards pass by pass.</summary>
    private static void FillCutOut(byte[] rgba, float[] alpha, int width, int height)
    {
        var filled = alpha.Select(a => a > 0).ToArray();
        for (var pass = 0; pass < width + height && filled.Contains(false); pass++)
        {
            var next = (bool[])filled.Clone();
            for (var y = 0; y < height; y++)
                for (var x = 0; x < width; x++)
                {
                    var i = y * width + x;
                    if (filled[i])
                        continue;
                    int r = 0, g = 0, b = 0, n = 0;
                    for (var dy = -1; dy <= 1; dy++)
                        for (var dx = -1; dx <= 1; dx++)
                        {
                            var (nx, ny) = (x + dx, y + dy);
                            if (nx < 0 || ny < 0 || nx >= width || ny >= height || !filled[ny * width + nx])
                                continue;
                            var j = (ny * width + nx) * 4;
                            (r, g, b, n) = (r + rgba[j], g + rgba[j + 1], b + rgba[j + 2], n + 1);
                        }
                    if (n == 0)
                        continue;
                    (rgba[i * 4], rgba[i * 4 + 1], rgba[i * 4 + 2]) = ((byte)(r / n), (byte)(g / n), (byte)(b / n));
                    next[i] = true;
                }
            filled = next;
        }
    }

    /// <summary>
    /// Writes the texture for the upscaler with its edges wrapped round: planet tiles lie beside copies of
    /// themselves and tread textures repeat, so their enlarged edges must meet without a seam.
    /// </summary>
    private static void SaveStaged(string gameFolder, Job job, string work)
    {
        var image = Read(gameFolder, job.Relative, out _)!;
        var source = image.GetData();
        var (w, h, pad) = (job.Width, job.Height, job.Pad);
        var padded = new byte[(w + 2 * pad) * (h + 2 * pad) * 4];
        for (var y = 0; y < h + 2 * pad; y++)
            for (var x = 0; x < w + 2 * pad; x++)
            {
                var from = (Wrap(y - pad, h) * w + Wrap(x - pad, w)) * 4;
                var to = (y * (w + 2 * pad) + x) * 4;
                (padded[to], padded[to + 1], padded[to + 2], padded[to + 3]) = (source[from], source[from + 1], source[from + 2], 255);
            }
        var folder = Path.Combine(work, job.Planet ? "planets" : "models");
        Directory.CreateDirectory(folder);
        Save(Image.CreateFromData(w + 2 * pad, h + 2 * pad, false, Image.Format.Rgba8, padded), Path.Combine(folder, job.Staged));
    }

    private static int Wrap(int i, int n) => ((i % n) + n) % n;

    /// <summary>Runs Real-ESRGAN over a folder, reporting how many files it has written so far.</summary>
    private static void Upscale(string input, string output, string model, int expected, Action<int> progress)
    {
        var start = new ProcessStartInfo(Upscaler)
        {
            WorkingDirectory = ToolFolder,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-i", input, "-o", output, "-n", model, "-s", Scale.ToString(), "-f", "png" })
            start.ArgumentList.Add(argument);
        // Overlay layers (Steam, Epic, Twitch) make the upscaler's older Vulkan setup fail to find the GPUs.
        start.Environment["VK_LOADER_LAYERS_DISABLE"] = "~implicit~";

        using var process = Process.Start(start)!;
        var errors = process.StandardError.ReadToEndAsync();
        _ = process.StandardOutput.ReadToEndAsync();
        var deadline = DateTime.UtcNow + TimeSpan.FromMinutes(30);
        while (!process.WaitForExit(500))
        {
            progress(Directory.GetFiles(output, "*.png").Length);
            if (DateTime.UtcNow > deadline)
            {
                process.Kill(entireProcessTree: true);
                break;
            }
        }
        File.AppendAllText(Path.Combine(Paths.Classic, "hd.log"), $"{model} on {input}: exit {process.ExitCode}, {Directory.GetFiles(output, "*.png").Length}/{expected}\n" + errors.Result);
    }

    /// <summary>
    /// Cuts the wrapped edges off the enlarged texture (or enlarges it with Lanczos when the upscaler left none),
    /// puts its coverage back, and writes it with its normal and roughness maps. False when Lanczos stood in.
    /// </summary>
    private static bool Finish(Job job, string work)
    {
        var folder = Path.Combine(work, job.Planet ? "planets" : "models");
        var upscaled = Path.Combine(folder + "-x4", job.Staged);
        var image = Image.LoadFromFile(Path.Combine(folder, job.Staged));
        var (paddedWidth, paddedHeight) = (image.GetWidth() * Scale, image.GetHeight() * Scale);
        var enlargedByUpscaler = File.Exists(upscaled) ? Image.LoadFromFile(upscaled) : null;
        var fromUpscaler = enlargedByUpscaler is not null
            && enlargedByUpscaler.GetWidth() == paddedWidth && enlargedByUpscaler.GetHeight() == paddedHeight;
        if (fromUpscaler)
            image = enlargedByUpscaler!;
        else
            image.Resize(paddedWidth, paddedHeight, Image.Interpolation.Lanczos);
        image.Convert(Image.Format.Rgba8);
        var (w, h) = (job.Width * Scale, job.Height * Scale);
        image = image.GetRegion(new Rect2I(job.Pad * Scale, job.Pad * Scale, w, h));
        var rgba = image.GetData();

        if (job.Alpha is { } alpha)
        {
            var coverage = new byte[job.Width * job.Height];
            for (var i = 0; i < coverage.Length; i++)
                coverage[i] = (byte)Mathf.RoundToInt(alpha[i] * 255);
            var mask = Image.CreateFromData(job.Width, job.Height, false, Image.Format.L8, coverage);
            mask.Resize(w, h, Image.Interpolation.Cubic);
            var enlarged = mask.GetData();
            for (var i = 0; i < w * h; i++)
                rgba[i * 4 + 3] = enlarged[i];
        }
        Directory.CreateDirectory(Path.GetDirectoryName(AlbedoPath(job.Relative))!);
        Save(Image.CreateFromData(w, h, false, Image.Format.Rgba8, rgba), AlbedoPath(job.Relative));

        var name = Path.GetFileName(job.Relative).ToLowerInvariant();
        if (!WithoutMaps.Any(name.StartsWith))
            SaveMaps(job, rgba, w, h);
        return fromUpscaler;
    }

    /// <summary>Skies, suns and moons are never lit; water has a shader of its own.</summary>
    private static readonly string[] WithoutMaps = ["sky", "sun", "moon", "watr", "watfal", "watflw"];

    /// <summary>
    /// The normal and roughness maps, guessed from the texture's brightness: fine detail (the brightness minus its
    /// blur over a few original pixels) is read as height, so panel lines, rivets and cracks sink in and lit
    /// edges stand out, while the large light and shade the artists painted in stays flat. Darker and recessed
    /// spots are rougher. Planet tiles wrap round at their edges, model textures do not.
    /// </summary>
    private static void SaveMaps(Job job, byte[] rgba, int w, int h)
    {
        var wrap = job.Planet;
        var luminance = new float[w * h];
        for (var i = 0; i < luminance.Length; i++)
            luminance[i] = (0.299f * rgba[i * 4] + 0.587f * rgba[i * 4 + 1] + 0.114f * rgba[i * 4 + 2]) / 255f;
        var fine = Blur(luminance, w, h, 1, wrap, 1);
        var coarse = Blur(luminance, w, h, Scale * 2, wrap, 3);
        var height = new float[w * h];
        for (var i = 0; i < height.Length; i++)
            height[i] = fine[i] - coarse[i];

        // The heights with a one-pixel border, wrapped or repeated, so the slopes need no edge checks.
        var (pw, ph) = (w + 2, h + 2);
        var bordered = new float[pw * ph];
        for (var y = 0; y < ph; y++)
            for (var x = 0; x < pw; x++)
            {
                var (sx, sy) = wrap ? (Wrap(x - 1, w), Wrap(y - 1, h)) : (Math.Clamp(x - 1, 0, w - 1), Math.Clamp(y - 1, 0, h - 1));
                bordered[y * pw + x] = height[sy * w + sx];
            }
        // Slopes per original pixel, so the strength does not depend on how far the texture was enlarged.
        var strength = (job.Planet ? PlanetBump : ModelBump) * Scale;
        var normals = new byte[w * h * 3];
        var roughness = new byte[w * h];
        for (var y = 0; y < h; y++)
            for (var x = 0; x < w; x++)
            {
                var c = (y + 1) * pw + x + 1;
                var (up, down) = (c - pw, c + pw);
                var dx = (bordered[up + 1] + 2 * bordered[c + 1] + bordered[down + 1] - bordered[up - 1] - 2 * bordered[c - 1] - bordered[down - 1]) / 8;
                var dy = (bordered[down - 1] + 2 * bordered[down] + bordered[down + 1] - bordered[up - 1] - 2 * bordered[up] - bordered[up + 1]) / 8;
                // Rising to the right tilts the surface left; rising down the texture tilts it up (green up).
                var normal = new Vector3(-dx * strength, dy * strength, 1).Normalized();
                var i = y * w + x;
                normals[i * 3] = (byte)Mathf.RoundToInt((normal.X * 0.5f + 0.5f) * 255);
                normals[i * 3 + 1] = (byte)Mathf.RoundToInt((normal.Y * 0.5f + 0.5f) * 255);
                normals[i * 3 + 2] = (byte)Mathf.RoundToInt((normal.Z * 0.5f + 0.5f) * 255);
                var rough = job.Planet
                    ? 0.88f + (0.5f - coarse[i]) * 0.2f
                    : 0.55f + (0.45f - coarse[i]) * 0.35f + Mathf.Clamp(-height[i] * 2, 0, 0.3f);
                roughness[i] = (byte)Mathf.RoundToInt(Mathf.Clamp(rough, job.Planet ? 0.6f : 0.25f, 1) * 255);
            }
        Save(Image.CreateFromData(w, h, false, Image.Format.Rgb8, normals), NormalPath(job.Relative));
        Save(Image.CreateFromData(w, h, false, Image.Format.L8, roughness), RoughnessPath(job.Relative));
    }

    private static void Save(Image image, string path)
    {
        if (image.SavePng(path) is not Error.Ok and var error)
            throw new IOException($"Could not write {path}: {error}");
    }

    /// <summary>How strongly brightness detail becomes slope: the machines' painted panels, the planets' ground.</summary>
    private const float ModelBump = 3f;
    private const float PlanetBump = 2f;

    /// <summary>A box blur of the given radius, repeated (three passes come close to a Gaussian).</summary>
    private static float[] Blur(float[] source, int w, int h, int radius, bool wrap, int passes)
    {
        var current = source;
        for (var pass = 0; pass < passes; pass++)
        {
            current = BlurLine(current, w, h, radius, wrap, horizontal: true);
            current = BlurLine(current, w, h, radius, wrap, horizontal: false);
        }
        return current;
    }

    private static float[] BlurLine(float[] source, int w, int h, int radius, bool wrap, bool horizontal)
    {
        var result = new float[source.Length];
        var (length, lines) = horizontal ? (w, h) : (h, w);
        var weight = 1f / (2 * radius + 1);
        for (var line = 0; line < lines; line++)
        {
            float Sample(int i)
            {
                i = wrap ? Wrap(i, length) : Math.Clamp(i, 0, length - 1);
                return horizontal ? source[line * w + i] : source[i * w + line];
            }
            var sum = 0f;
            for (var i = -radius; i <= radius; i++)
                sum += Sample(i);
            for (var i = 0; i < length; i++)
            {
                result[horizontal ? line * w + i : i * w + line] = sum * weight;
                sum += Sample(i + radius + 1) - Sample(i - radius);
            }
        }
        return result;
    }
}
