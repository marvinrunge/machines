using System.Buffers.Binary;
using System.Diagnostics;
using System.IO.Compression;
using Machines.Import.Video;

// Makes HD versions of the original's Smacker films from the player's own copy of the game, for the looks other
// than the classic one (game/scripts/SmackerView.cs plays them in place of the originals):
//   1. each frame is enlarged by Real-ESRGAN's video model (tools/texture-upscale/bin): small menu films four
//      times, the 632×272 cut-scenes three times, the 640×480 ones twice;
//   2. RIFE (tools/film-upscale/bin) draws the frames in between, to 50-60 a second;
//   3. FFmpeg writes them with the first sound track as Ogg Theora and Vorbis, which Godot plays.
// Results go to classic/films/<path under flics>.ogv (git-ignored, never shared); films already made are kept.
//
//   dotnet run -c Release --project tools/film-upscale [-- [--game <folder>] [name ...]]
//
// Names pick films by file name without extension (animatic, gui/tutba1b, ...); without names every film is made.

const int Version = 1;
const int Encoders = 3;

var repository = FindRepository();
var gameFolder = Option("--game") ?? ReadGamePath(repository)
    ?? throw new InvalidOperationException("no game folder: pass --game or import the game first (classic/game_path.txt)");
var names = args.Where((a, i) => !a.StartsWith("--", StringComparison.Ordinal) && (i == 0 || args[i - 1] != "--game")).ToHashSet(StringComparer.OrdinalIgnoreCase);
var flics = Path.Combine(gameFolder, "flics");
var output = Path.Combine(repository, "classic", "films");
var work = Path.Combine(output, ".work");
var upscaler = Path.Combine(repository, "tools", "texture-upscale", "bin", "realesrgan-ncnn-vulkan.exe");
var rife = Path.Combine(repository, "tools", "film-upscale", "bin", "rife", "rife-ncnn-vulkan.exe");
var ffmpeg = Path.Combine(repository, "tools", "film-upscale", "bin", "ffmpeg", "ffmpeg.exe");
foreach (var tool in new[] { upscaler, rife, ffmpeg })
    if (!File.Exists(tool))
        throw new FileNotFoundException("tool missing: run tools/texture-upscale/fetch.sh and tools/film-upscale/fetch.sh", tool);

// Films made by an older version of this tool are made again.
Directory.CreateDirectory(output);
var versionFile = Path.Combine(output, "version.txt");
if (!File.Exists(versionFile) || File.ReadAllText(versionFile).Trim() != Version.ToString())
{
    foreach (var old in Directory.EnumerateFiles(output, "*.ogv", SearchOption.AllDirectories))
        File.Delete(old);
    File.WriteAllText(versionFile, Version.ToString());
}
if (Directory.Exists(work))
    Directory.Delete(work, true);

var films = Directory.EnumerateFiles(flics, "*.smk", SearchOption.AllDirectories)
    .Select(f => Path.GetRelativePath(flics, f).Replace('\\', '/'))
    .Where(f => names.Count == 0 || names.Contains(Path.ChangeExtension(f, null)) || names.Contains(Path.GetFileNameWithoutExtension(f)))
    .Order(StringComparer.OrdinalIgnoreCase)
    .ToList();
var total = Stopwatch.StartNew();
var encoding = new List<Task>();
foreach (var film in films)
{
    var target = Path.Combine(output, Path.ChangeExtension(film, ".ogv"));
    if (File.Exists(target))
        continue;
    var watch = Stopwatch.StartNew();
    var folder = Path.Combine(work, Path.ChangeExtension(film, null).Replace('/', '_'));
    var job = Prepare(Path.Combine(flics, film), folder);
    Console.WriteLine($"{film}: {job.Count} frames ×{job.Scale} ×{job.Between} ready in {watch.Elapsed.TotalSeconds:F0} s, encoding");
    // The GPU goes on with the next film while the processor encodes this one.
    encoding.RemoveAll(t => t.IsCompleted);
    if (encoding.Count >= Encoders)
        Task.WaitAny([.. encoding]);
    encoding.Add(Task.Run(() =>
    {
        Encode(job, target);
        Directory.Delete(folder, true);
        Console.WriteLine($"{film}: written");
    }));
}
Task.WaitAll([.. encoding]);
if (Directory.Exists(work))
    Directory.Delete(work, true);
Console.WriteLine($"{films.Count} films in {total.Elapsed.TotalMinutes:F1} min");

// Frames, sound, enlarging and in-between frames of one film, in its own folder.
Job Prepare(string path, string folder)
{
    var source = Path.Combine(folder, "source");
    var enlarged = Path.Combine(folder, "enlarged");
    var smooth = Path.Combine(folder, "smooth");
    Directory.CreateDirectory(source);
    Directory.CreateDirectory(enlarged);
    using var smk = SmackerDecoder.Open(path);
    var scale = smk.Width <= 320 ? 4 : smk.DisplayHeight <= 300 ? 3 : 2;
    var between = Math.Max(1, (int)Math.Round(60 * smk.FrameDuration.TotalSeconds));

    string? sound = null;
    if (smk.AudioTracks.FirstOrDefault() is { } track && smk.ReadAllAudio(track.Index) is { Length: > 0 } pcm && !Silent(pcm, track.Bits))
    {
        sound = Path.Combine(folder, "sound.wav");
        WriteWav(sound, pcm, track);
    }
    var rgba = new byte[smk.Width * smk.DisplayHeight * 4];
    while (smk.NextFrame())
    {
        smk.CopyRgba(rgba);
        WritePng(Path.Combine(source, $"{smk.FrameIndex + 1:D8}.png"), smk.Width, smk.DisplayHeight, rgba);
    }

    Run(upscaler, ["-i", source, "-o", enlarged, "-n", "realesr-animevideov3", "-s", scale.ToString(), "-m", Path.Combine(Path.GetDirectoryName(upscaler)!, "models"), "-f", "png"]);
    Directory.Delete(source, true);
    var frames = enlarged;
    if (between > 1)
    {
        Directory.CreateDirectory(smooth);
        Run(rife, ["-i", enlarged, "-o", smooth, "-m", Path.Combine(Path.GetDirectoryName(rife)!, "rife-v4.6"), "-n", (smk.FrameCount * between).ToString(), "-f", "%08d.png"]);
        Directory.Delete(enlarged, true);
        frames = smooth;
    }
    return new Job(frames, sound, smk.FrameCount, scale, between, $"{between * 10_000_000}/{smk.FrameDuration.Ticks}");
}

void Encode(Job job, string target)
{
    Directory.CreateDirectory(Path.GetDirectoryName(target)!);
    var partial = target + ".part";
    List<string> arguments = ["-hide_banner", "-loglevel", "error", "-y", "-framerate", job.Rate, "-i", Path.Combine(job.Folder, "%08d.png")];
    if (job.Sound is not null)
        arguments.AddRange(["-i", job.Sound]);
    arguments.AddRange(["-c:v", "libtheora", "-q:v", "8", "-pix_fmt", "yuv420p"]);
    arguments.AddRange(job.Sound is not null ? ["-c:a", "libvorbis", "-q:a", "6"] : ["-an"]);
    arguments.AddRange(["-f", "ogg", partial]);
    Run(ffmpeg, arguments);
    File.Move(partial, target, true);
}

static void Run(string program, IEnumerable<string> arguments)
{
    var start = new ProcessStartInfo(program) { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true };
    foreach (var argument in arguments)
        start.ArgumentList.Add(argument);
    // Steam's, Epic's and Twitch's Vulkan overlays keep the 2022 builds from finding the GPU.
    start.Environment["VK_LOADER_LAYERS_DISABLE"] = "~implicit~";
    using var process = Process.Start(start)!;
    var errors = process.StandardError.ReadToEndAsync();
    process.StandardOutput.ReadToEnd();
    process.WaitForExit();
    if (process.ExitCode != 0)
        throw new InvalidOperationException($"{Path.GetFileName(program)} failed ({process.ExitCode}): {errors.Result}");
}

// The menu pictures carry 8-bit tracks that hold one value throughout.
static bool Silent(byte[] pcm, int bits) =>
    bits == 8 ? pcm.AsSpan().IndexOfAnyExcept(pcm[0]) < 0
        : pcm.Length < 2 || System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(pcm.AsSpan(0, pcm.Length & ~1)).IndexOfAnyExcept(BitConverter.ToInt16(pcm, 0)) < 0;

static void WriteWav(string path, byte[] pcm, SmackerAudioTrack track)
{
    using var wav = new BinaryWriter(File.Create(path));
    wav.Write("RIFF"u8);
    wav.Write(36 + pcm.Length);
    wav.Write("WAVEfmt "u8);
    wav.Write(16);
    wav.Write((short)1);
    wav.Write((short)track.Channels);
    wav.Write(track.Rate);
    wav.Write(track.BytesPerSecond);
    wav.Write((short)(track.Channels * track.Bits / 8));
    wav.Write((short)track.Bits);
    wav.Write("data"u8);
    wav.Write(pcm.Length);
    wav.Write(pcm);
}

static void WritePng(string path, int width, int height, byte[] rgba)
{
    var pixels = new MemoryStream();
    using (var deflate = new ZLibStream(pixels, CompressionLevel.Fastest, true))
    {
        var line = new byte[1 + width * 3];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
                rgba.AsSpan((y * width + x) * 4, 3).CopyTo(line.AsSpan(1 + x * 3));
            deflate.Write(line);
        }
    }
    var header = new byte[13];
    BinaryPrimitives.WriteInt32BigEndian(header, width);
    BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), height);
    header[8] = 8; // bits per channel
    header[9] = 2; // RGB
    using var file = File.Create(path);
    file.Write([0x89, (byte)'P', (byte)'N', (byte)'G', 0x0D, 0x0A, 0x1A, 0x0A]);
    WriteChunk(file, "IHDR"u8, header);
    WriteChunk(file, "IDAT"u8, pixels.ToArray());
    WriteChunk(file, "IEND"u8, []);
}

static void WriteChunk(Stream file, ReadOnlySpan<byte> type, byte[] data)
{
    Span<byte> word = stackalloc byte[4];
    BinaryPrimitives.WriteInt32BigEndian(word, data.Length);
    file.Write(word);
    file.Write(type);
    file.Write(data);
    BinaryPrimitives.WriteUInt32BigEndian(word, ~Crc(Crc(~0u, type), data));
    file.Write(word);
}

// PNG's CRC-32 (zlib's), carried on from crc.
static uint Crc(uint crc, ReadOnlySpan<byte> data)
{
    foreach (var b in data)
    {
        crc ^= b;
        for (var bit = 0; bit < 8; bit++)
            crc = (crc & 1) != 0 ? 0xEDB88320u ^ crc >> 1 : crc >> 1;
    }
    return crc;
}

string? Option(string name) => Array.IndexOf(args, name) is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

static string? ReadGamePath(string repository)
{
    var file = Path.Combine(repository, "classic", "game_path.txt");
    return File.Exists(file) && File.ReadAllText(file).Trim() is { Length: > 0 } folder && Directory.Exists(folder) ? folder : null;
}

static string FindRepository()
{
    for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        if (File.Exists(Path.Combine(dir.FullName, "Machines.sln")))
            return dir.FullName;
    throw new DirectoryNotFoundException("repository not found above " + AppContext.BaseDirectory);
}

// Frames ready to encode: their folder, the sound file if any, the source's frame count, the enlargement, the frames
// drawn per source frame, and the frame rate as a fraction.
record Job(string Folder, string? Sound, int Count, int Scale, int Between, string Rate);
