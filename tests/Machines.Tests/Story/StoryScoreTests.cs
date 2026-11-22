using Machines.Game;

namespace Machines.Tests.Story;

/// <summary>The prologue's score (game/scripts/StoryAudioPlan.cs, StoryScore): its pieces, the order they play in, the break-in and the escape.</summary>
public class StoryScoreTests
{
    private static string Music(string file)
    {
        var data = RepoPaths.OriginalDataFolder();
        return Path.Combine(Path.GetFullPath(Path.Combine(data, "..", "..")), "game", "assets", "story", "music", file);
    }

    /// <summary>An Ogg Vorbis file's length in seconds: the last page's granule position over the identification header's rate.</summary>
    private static double OggSeconds(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var vorbis = bytes.AsSpan().IndexOf("\u0001vorbis"u8);
        var rate = BitConverter.ToInt32(bytes, vorbis + 12);
        var last = bytes.AsSpan().LastIndexOf("OggS"u8);
        return BitConverter.ToInt64(bytes, last + 6) / (double)rate;
    }

    [Fact]
    public void ThePiecesAreThereAndPlayOnce()
    {
        Assert.Equal(["lost_in_the_void", "lost_vessel", "drift_into_the_black", "lost_in_the_dark_calm"], StoryScore.Playlist.Select(p => p.Name));
        foreach (var piece in StoryScore.Pieces)
        {
            var file = Music(piece.Name + ".ogg");
            Assert.True(File.Exists(file), file);
            Assert.InRange(OggSeconds(file), piece.Seconds - 0.05, piece.Seconds + 0.05);
            // Imported without the loop flag: the playlist goes on to the next piece itself.
            Assert.Contains("loop=false", File.ReadAllText(file + ".import"));
        }
    }

    [Fact]
    public void ThePlaylistGoesRoundAgain()
    {
        Assert.Equal(1, StoryScore.Next(0));
        Assert.Equal(3, StoryScore.Next(2));
        Assert.Equal(0, StoryScore.Next(3));
    }

    [Fact]
    public void TheBreakInPlaysTheLoudPartUntilTheTrioIsOutOfTheChamber()
    {
        var score = new StoryScore();
        Assert.Equal(StoryScorePart.Playlist, score.Follow(1, null));
        Assert.Equal(StoryScorePart.Playlist, score.Follow(3, null));
        Assert.True(score.BreakIn());
        Assert.Equal(StoryScorePart.Caught, score.Follow(4, null));
        // Out into Die Enge while it still plays: it plays on.
        Assert.Equal(StoryScorePart.Caught, score.Follow(5, null));
        score.CaughtEnded();
        Assert.Equal(StoryScorePart.Playlist, score.Follow(5, null));

        // Ended while still in the chamber: the playlist waits for section 5.
        var slow = new StoryScore();
        slow.BreakIn();
        slow.CaughtEnded();
        Assert.Equal(StoryScorePart.Caught, slow.Follow(4, null));
        Assert.Equal(StoryScorePart.Playlist, slow.Follow(5, null));
    }

    [Fact]
    public void TheEscapeStartsWithTheCountdownAndOutlastsTheLaunch()
    {
        var score = new StoryScore();
        Assert.Equal(StoryScorePart.Playlist, score.Follow(6, null));
        Assert.Equal(StoryScorePart.Escape, score.Follow(7, 180));
        // The launch stops the countdown: the piece plays on into the pod's drop.
        Assert.Equal(StoryScorePart.Escape, score.Follow(8, null));
        Assert.False(score.BreakIn());
        score.Stop();
        Assert.Equal(StoryScorePart.Silence, score.Follow(8, null));
        Assert.Equal(StoryScorePart.Silence, score.Follow(7, 100));
    }

    [Theory]
    [InlineData(180, 0)]
    [InlineData(179.9f, 0)]
    [InlineData(150, 29.9)]
    [InlineData(60, 119.9)]
    [InlineData(0.5f, 178.9)]
    public void TheEscapesPieceEndsAsTheCountdownRunsOut(float left, double offset) =>
        Assert.Equal(offset, StoryScore.EscapeOffset(left, StoryScore.Escape.Seconds), 3);

    [Fact]
    public void ACheckpointGoesOnWithThePieceThatPlayed()
    {
        // A new start always from the first piece.
        Assert.Equal((0, 0.0), StoryScore.Start(0, (2, 40)));
        // A checkpoint after a fall: where the last match left it.
        Assert.Equal((2, 40.0), StoryScore.Start(3, (2, 40)));
        Assert.Equal((1, 12.5), StoryScore.Start(1, (1, 12.5)));
        // Without one: the piece a run usually hears there.
        Assert.Equal((0, 0.0), StoryScore.Start(1, null));
        Assert.Equal((1, 0.0), StoryScore.Start(2, null));
        Assert.Equal((2, 0.0), StoryScore.Start(5, null));
        Assert.Equal((2, 0.0), StoryScore.Start(3, (9, 10)));
    }

    [Fact]
    public void ThePlaylistDrawsBackWhenHuntedAndAfterTheCore()
    {
        Assert.Equal(0, StoryScore.PlaylistDb(2, 0));
        Assert.Equal(StoryScore.UneasyDb, StoryScore.PlaylistDb(2, 1));
        Assert.Equal(StoryScore.HuntedDb, StoryScore.PlaylistDb(3, 2));
        Assert.Equal(StoryScore.AfterCaughtDb, StoryScore.PlaylistDb(5, 0));
        Assert.Equal(StoryScore.AfterCaughtDb + StoryScore.HuntedDb, StoryScore.PlaylistDb(6, 2));
    }
}
