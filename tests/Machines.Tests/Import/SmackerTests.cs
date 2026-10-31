using Machines.Import.Video;

namespace Machines.Tests.Import;

/// <summary>
/// Plays every Smacker flic in the player's own copy of the game (<c>flics</c>, found through
/// <c>classic/game_path.txt</c>) through <see cref="SmackerDecoder"/>. Skipped when there is no game copy.
/// </summary>
public class SmackerTests
{
    /// <summary>The game copy's <c>flics</c> folder, or null when it is missing.</summary>
    internal static string? FlicsFolder { get; } = FindFlics();

    private static string? FindFlics()
    {
        try
        {
            var repository = Path.GetDirectoryName(Path.GetDirectoryName(RepoPaths.OriginalDataFolder()))!;
            var gamePath = Path.Combine(repository, "classic", "game_path.txt");
            if (!File.Exists(gamePath))
                return null;
            var flics = Path.Combine(File.ReadAllText(gamePath).Trim(), "flics");
            return Directory.Exists(flics) ? flics : null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }

    public static IEnumerable<object[]> Flics => FlicsFolder is null
        ? []
        : Directory.EnumerateFiles(FlicsFolder, "*.smk", SearchOption.AllDirectories)
            .Select(f => Path.GetRelativePath(FlicsFolder, f).Replace('\\', '/'))
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(f => new object[] { f });

    [GameCopyTheory]
    [MemberData(nameof(Flics))]
    public void Flic_plays_to_the_end(string flic)
    {
        using var smk = SmackerDecoder.Open(Path.Combine(FlicsFolder!, flic));
        var audio = new long[7];
        var frames = 0;
        var pictured = false;
        while (smk.NextFrame())
        {
            frames++;
            foreach (var track in smk.AudioTracks)
                audio[track.Index] += track.Pcm.Length;
            pictured = pictured || !SingleColour(smk);
        }

        Assert.Equal(smk.FrameCount, frames);
        Assert.Equal(0, smk.DamagedFrames);
        Assert.True(pictured, "every frame is a single colour");
        // The sound runs as long as the picture; the first frames carry it ahead, but it all arrives in the end.
        var seconds = smk.FrameCount * smk.FrameDuration.TotalSeconds;
        foreach (var track in smk.AudioTracks)
        {
            var heard = audio[track.Index] / (double)track.BytesPerSecond;
            Assert.True(Math.Abs(heard - seconds) <= Math.Max(2 * smk.FrameDuration.TotalSeconds, 0.02 * seconds),
                $"track {track.Index}: {heard:F2} s of sound for {seconds:F2} s of picture");
            Assert.Equal(audio[track.Index], smk.ReadAllAudio(track.Index).Length);
        }
    }

    [GameCopyFact]
    public void Briefing_face_is_read_as_the_header_says()
    {
        using var smk = SmackerDecoder.Open(Path.Combine(FlicsFolder!, "gui", "tutba1b.smk"));

        Assert.Equal((188, 188, 138), (smk.Width, smk.Height, smk.FrameCount));
        Assert.Equal(TimeSpan.FromMilliseconds(50), smk.FrameDuration);
        Assert.False(smk.YInterlaced || smk.YDoubled || smk.HasRingFrame);
        var track = Assert.Single(smk.AudioTracks);
        Assert.Equal((0, 22050, 16, 2, true), (track.Index, track.Rate, track.Bits, track.Channels, track.Compressed));
    }

    [GameCopyFact]
    public void Rewind_plays_the_same_frames_again()
    {
        using var smk = SmackerDecoder.Open(Path.Combine(FlicsFolder!, "home.smk"));
        byte[] Play()
        {
            for (var i = 0; i < 40; i++)
                smk.NextFrame();
            var rgba = new byte[smk.Width * smk.DisplayHeight * 4];
            smk.CopyRgba(rgba);
            return rgba;
        }

        var first = Play();
        smk.Rewind();
        Assert.Equal(-1, smk.FrameIndex);
        Assert.Equal(first, Play());
        Assert.Equal(39, smk.FrameIndex);
    }

    [Fact]
    public void Other_files_are_refused()
    {
        var bytes = new byte[104];
        "SMK3"u8.CopyTo(bytes);
        Assert.Throws<InvalidDataException>(() => new SmackerDecoder(new MemoryStream(bytes)));
    }

    private static bool SingleColour(SmackerDecoder smk)
    {
        var pixels = smk.Pixels;
        var palette = smk.Palette;
        var first = pixels[0];
        foreach (var index in pixels)
        {
            if (index != first && !palette.Slice(3 * index, 3).SequenceEqual(palette.Slice(3 * first, 3)))
                return false;
        }
        return true;
    }
}

/// <summary>A test that needs the player's own copy of the game; skipped without it.</summary>
public sealed class GameCopyFactAttribute : FactAttribute
{
    public GameCopyFactAttribute()
    {
        if (SmackerTests.FlicsFolder is null)
            Skip = "no game copy (classic/game_path.txt)";
    }
}

/// <inheritdoc cref="GameCopyFactAttribute"/>
public sealed class GameCopyTheoryAttribute : TheoryAttribute
{
    public GameCopyTheoryAttribute()
    {
        if (SmackerTests.FlicsFolder is null)
            Skip = "no game copy (classic/game_path.txt)";
    }
}
