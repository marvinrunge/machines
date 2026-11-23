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
        Assert.Equal(["silent_circuit_v1", "silent_circuit_v2"], StoryScore.Stealth.Select(p => p.Name));
        Assert.Equal(["lost_in_the_void", "lost_vessel", "drift_into_the_black", "lost_in_the_dark_calm"], StoryScore.Playlist.Select(p => p.Name));
        Assert.Equal(["static_dread_v2"], StoryScore.Waves.Select(p => p.Name));
        Assert.Equal("static_dread_v1", StoryScore.Boss.Name);
        Assert.Equal("circuitry_v1", StoryScore.Escape.Name);
        foreach (var piece in StoryScore.Pieces)
        {
            var file = Music(piece.Name + ".ogg");
            Assert.True(File.Exists(file), file);
            Assert.InRange(OggSeconds(file), piece.Seconds - 0.05, piece.Seconds + 0.05);
            // Imported without the loop flag: the playlist goes on to the next piece itself, a hold is set by the code.
            Assert.Contains("loop=false", File.ReadAllText(file + ".import"));
        }
    }

    [Fact]
    public void ThePlaylistGoesRoundAgain()
    {
        Assert.Equal(1, StoryScore.Next(StoryList.Background, 0));
        Assert.Equal(3, StoryScore.Next(StoryList.Background, 2));
        Assert.Equal(0, StoryScore.Next(StoryList.Background, 3));
        Assert.Equal(1, StoryScore.Next(StoryList.Stealth, 0));
        Assert.Equal(0, StoryScore.Next(StoryList.Stealth, 1));
        Assert.Equal(0, StoryScore.Next(StoryList.Waves, 0));
    }

    [Fact]
    public void SilentCircuitPlaysUntilTheBreakInAndTheOldPlaylistAfterIt()
    {
        var score = new StoryScore();
        Assert.Equal(StoryList.Stealth, score.List);
        score.Follow(6, null);
        Assert.Equal(StoryList.Stealth, score.List);
        score.BreakIn();
        Assert.Equal(StoryList.Background, score.List);
        // A chapter begun after the break-in has no stealth left; one begun at the antechamber has.
        Assert.Equal(StoryList.Background, new StoryScore(StoryScore.BrokenInFrom).List);
        Assert.Equal(StoryList.Stealth, new StoryScore(6).List);
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
    public void TheEscapeBeginsAtTheNotgangAndOutlastsTheLaunch()
    {
        var score = new StoryScore();
        Assert.Equal(StoryScorePart.Playlist, score.Follow(6, null));
        // C0: the playlist goes for good, before the countdown runs.
        Assert.Equal(StoryScorePart.Escape, score.Follow(7, null));
        Assert.Equal(StoryScorePart.Escape, score.Follow(7, 240));
        // The launch stops the countdown: the piece plays on into the pod's drop.
        Assert.Equal(StoryScorePart.Escape, score.Follow(8, null));
        Assert.False(score.BreakIn());
        Assert.False(score.BossIn());
        score.Stop();
        Assert.Equal(StoryScorePart.Silence, score.Follow(8, null));
        Assert.Equal(StoryScorePart.Silence, score.Follow(7, 100));
    }

    [Fact]
    public void TheEscapesPieceStartsWithTheLiftsBurst()
    {
        var score = new StoryScore();
        Assert.Null(score.EscapeFrom(12, 240, 100));
        score.Follow(7, 240, 100);
        // The 6 s before the burst are silence.
        Assert.Null(score.EscapeFrom(12, 240, 100));
        Assert.Null(score.EscapeFrom(12, 236, 104));
        score.Burst(106);
        Assert.Equal(0, score.EscapeFrom(12, 234, 106)!.Value, 3);
        // Looked at a moment late: as far in as it is late.
        Assert.Equal(0.25, score.EscapeFrom(12, 234, 106.25)!.Value, 3);
    }

    [Theory]
    [InlineData(0, 240)]
    [InlineData(12, 240)]
    [InlineData(12, 300)]
    public void WithoutTheBurstsCueTheEscapesPieceStartsByTheCountdown(int checkpoint, float from)
    {
        var score = new StoryScore();
        score.Follow(7, from);
        Assert.Null(score.EscapeFrom(checkpoint, from, 0));
        // It waits the burst's 6 s and 2 more for its cue, then starts 2 s in (Easy's 300 s alike).
        Assert.Null(score.EscapeFrom(checkpoint, from - 7.9f, 0));
        Assert.Equal(2, score.EscapeFrom(checkpoint, from - 8, 0)!.Value, 3);
    }

    [Theory]
    [InlineData(13, 170, 64)]
    [InlineData(14, 120, 114)]
    [InlineData(13, 230, 64)]
    public void ARetryStartsTheEscapesPieceWhereARunHearsItThere(int checkpoint, float left, double at)
    {
        var score = new StoryScore();
        score.Follow(7, left);
        Assert.Equal(at, score.EscapeFrom(checkpoint, left, 0)!.Value, 3);
        Assert.Equal(at + 10, score.EscapeFrom(checkpoint, left - 10, 0)!.Value, 3);
        // 234 - the checkpoint's seconds left on Normal.
        Assert.Equal(234 - (checkpoint == 13 ? 170 : 120), StoryScore.RetryAt[checkpoint]);
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 100)]
    [InlineData(182.4, 182.4)]
    [InlineData(182.433, 138.219)]
    [InlineData(190, 145.786)]
    [InlineData(182.433 + 44.214 * 3 + 1, 139.219)]
    public void TheEscapeLoopsItsHoldPastItsEnd(double at, double played) =>
        Assert.Equal(played, StoryScore.IntoHold(at), 3);

    /// <summary>A hold's ends lie on bar lines of the grid measured from its file, and Godot's beat loop ends it at the hold's end.</summary>
    private static void AssertHoldOnBars(StoryPiece piece, double bpm, double first, int bars, double bpmFrom, double bpmTo)
    {
        var beat = 60 / bpm;
        foreach (var bar in new[] { piece.HoldFrom, piece.HoldTo })
        {
            var beats = (bar - first) / beat;
            Assert.Equal(0, Math.Round(beats) % 4);
            Assert.InRange(Math.Abs(beats - Math.Round(beats)) * beat, 0, 0.01);
        }
        Assert.Equal(bars * 4 * beat, piece.HoldTo - piece.HoldFrom, 2);
        Assert.True(piece.HoldTo < piece.Seconds - 1);
        Assert.Equal(piece.HoldTo, piece.HoldBeats * 60 / piece.HoldBpm, 6);
        Assert.InRange(piece.HoldBpm, bpmFrom, bpmTo);
    }

    [Fact]
    public void TheEscapesHoldIsThirtyTwoBarsInThePiecesLastThird()
    {
        // Measured: 173.7 BPM, the first beat at 0.049 s, bars of four (see the music README).
        Assert.InRange(StoryScore.HoldFrom, StoryScore.Escape.Seconds * 2 / 3, StoryScore.Escape.Seconds);
        AssertHoldOnBars(StoryScore.Escape, 173.7, 0.0494, 32, 173.5, 173.9);
        Assert.Equal(StoryScore.Escape.HoldBpm, StoryScore.HoldBpm);
    }

    [Fact]
    public void TheBossLoopsTheBodyOfStaticDread()
    {
        // Measured: 70 BPM, the first beat at 0.056 s; bars 8 to 48, after the intro and before the break-down.
        AssertHoldOnBars(StoryScore.Boss, 70, 0.056, 40, 69.9, 70.1);
        Assert.InRange(StoryScore.Boss.HoldFrom, 20, 40);
        Assert.False(StoryScore.Caught.Holds);
        Assert.All(StoryScore.Waves.Concat(StoryScore.Stealth).Concat(StoryScore.Playlist), p => Assert.False(p.Holds));
        // Past the hold's end the boss's piece goes back to its body.
        Assert.Equal(StoryScore.Boss.HoldFrom + 1, StoryScore.Boss.IntoHold(StoryScore.Boss.HoldTo + 1), 3);
    }

    [Fact]
    public void TheArmouryHoldsItsLevelInWavesAndDropsInPauses()
    {
        var score = new StoryScore();
        Assert.Equal(StoryScore.AfterCaughtDb + StoryScore.HuntedDb, StoryScore.PlaylistDb(6, 2, armoury: score.Armoury));
        score.Wave();
        Assert.Equal(StoryArmouryMusic.Wave, score.Armoury);
        // Static Dread v2 in place of the playlist while the waves run, pauses and all.
        Assert.Equal(StoryList.Waves, score.List);
        // The threat's dips are off while a wave is alive.
        Assert.Equal(StoryScore.AfterCaughtDb, StoryScore.PlaylistDb(6, 2, armoury: score.Armoury));
        score.Pause();
        Assert.Equal(StoryList.Waves, score.List);
        Assert.Equal(StoryScore.PauseDb, StoryScore.PlaylistDb(6, 2, armoury: score.Armoury));
        Assert.Equal(-10, StoryScore.PauseDb);
    }

    [Fact]
    public void TheBossPlaysStaticDreadAndItsFallIsTwoSecondsOfSilence()
    {
        var score = new StoryScore(9);
        score.Wave();
        Assert.True(score.BossIn());
        Assert.False(score.BossIn());
        Assert.Equal(StoryScorePart.Caught, score.Follow(6, null, 10));
        // A wave's cue during the boss does not take his level away.
        score.Wave();
        Assert.Equal(StoryArmouryMusic.Boss, score.Armoury);
        Assert.Equal(StoryScore.AfterCaughtDb, StoryScore.PlaylistDb(6, 2, armoury: score.Armoury));
        // His piece loops until he falls: no playlist under the fight however long it lasts.
        Assert.Equal(StoryScorePart.Caught, score.Follow(6, null, 400));
        Assert.True(score.BossDown(400));
        Assert.False(score.BossDown(400));
        Assert.Equal(StoryScorePart.Rest, score.Follow(6, null, 401.9));
        Assert.Equal(StoryScorePart.Playlist, score.Follow(6, null, 402));
        Assert.Equal(StoryArmouryMusic.None, score.Armoury);
        // After him the old playlist, not the waves' piece.
        Assert.Equal(StoryList.Background, score.List);
        // Falling at once: the rest all the same.
        var quick = new StoryScore();
        quick.BossIn();
        Assert.True(quick.BossDown(20));
        Assert.Equal(StoryScorePart.Rest, quick.Follow(6, null, 21));
        Assert.Equal(StoryScorePart.Playlist, quick.Follow(6, null, 22));
    }

    [Fact]
    public void TheBossNeedsTheArmoury()
    {
        Assert.False(new StoryScore().BossDown(0));
        var escaping = new StoryScore();
        escaping.Follow(7, 200);
        Assert.False(escaping.BossIn());
    }

    [Theory]
    [InlineData(null, null, false, StoryDuck.FarDb)]
    [InlineData(200f, "calm", false, StoryDuck.FarDb)]
    [InlineData(100f, "calm", false, StoryDuck.StepsDb)]
    [InlineData(100f, "lost", false, StoryDuck.StepsDb)]
    [InlineData(100f, "dwell", false, StoryDuck.FarDb)]
    [InlineData(100f, "calm", true, StoryDuck.FarDb)]
    [InlineData(39f, "calm", false, StoryDuck.NearDb)]
    [InlineData(39f, "calm", true, StoryDuck.NearDb)]
    [InlineData(100f, "wary", false, StoryDuck.NearDb)]
    [InlineData(300f, "wary", false, StoryDuck.NearDb)]
    [InlineData(80f, "draw", false, StoryDuck.LockDb)]
    [InlineData(30f, "fire", true, StoryDuck.LockDb)]
    public void TheThresherDucksThePlaylist(float? metres, string? state, bool sheltered, float db) =>
        Assert.Equal(db, StoryDuck.Raw(metres, state, sheltered));

    [Fact]
    public void TheDuckGoesDownAtOnceAndComesBackAfterEightSeconds()
    {
        var duck = new StoryDuck();
        Assert.Equal(StoryDuck.FarDb, duck.Update(200, "calm", false, 0));
        Assert.Equal(StoryDuck.StepsDb, duck.Update(100, "calm", false, 1));
        Assert.Equal(StoryDuck.NearDb, duck.Update(30, "calm", false, 2));
        Assert.True(duck.Filter);
        Assert.Equal(StoryDuck.LockDb, duck.Update(30, "fire", false, 3));
        // He loses the trio: the lock's level holds 8 s after it last held.
        Assert.Equal(StoryDuck.LockDb, duck.Update(150, "lost", false, 4));
        Assert.Equal(StoryDuck.LockDb, duck.Update(150, "lost", false, 10.9));
        Assert.Equal(StoryDuck.FarDb, duck.Update(150, "lost", false, 11));
        Assert.False(duck.Filter);
        // Down over 2 s (at least 3 dB a second), up over 6 s.
        Assert.Equal(9, StoryDuck.Speed(StoryDuck.FarDb, StoryDuck.LockDb), 3);
        Assert.Equal(3, StoryDuck.Speed(-6, -10), 3);
        Assert.Equal(3, StoryDuck.Speed(StoryDuck.LockDb, StoryDuck.FarDb), 3);
    }

    [Fact]
    public void TheDuckReplacesTheThreatsDipsInSectionTwo()
    {
        Assert.Equal(StoryDuck.NearDb, StoryScore.PlaylistDb(2, 2, duck: StoryDuck.NearDb));
        Assert.Equal(StoryDuck.FarDb + StoryScore.BootDb, StoryScore.PlaylistDb(2, 0, booting: true, duck: StoryDuck.FarDb));
    }

    [Fact]
    public void DuctsAndTheSideRoomsShelterTheLocator()
    {
        var repo = Path.GetFullPath(Path.Combine(RepoPaths.OriginalDataFolder(), "..", ".."));
        var deck = Machines.Import.Planets.DeckParser.Load(Path.Combine(repo, "data", "planets", "prolog", "prolog.deck"));
        foreach (var room in new[] { "R5", "R7", "A1", "V1a", "V1b", "V1c" })
            Assert.True(StoryDuck.Sheltered(deck.Zone(room)), room);
        foreach (var room in new[] { "R4", "R6", "WL", "S" })
            Assert.False(StoryDuck.Sheltered(deck.Zone(room)), room);
        Assert.False(StoryDuck.Sheltered(null));
    }

    [Theory]
    [InlineData("thresher near 35.5", StoryMusicCueKind.ThresherNear, 35.5f, 0f, null)]
    [InlineData("thresher state wary", StoryMusicCueKind.ThresherState, 0f, 0f, "wary")]
    [InlineData("thresher state hunt", StoryMusicCueKind.ThresherState, 0f, 0f, "calm")]
    [InlineData("wave 3 alive", StoryMusicCueKind.WaveAlive, 3f, 0f, null)]
    [InlineData("wave pause", StoryMusicCueKind.WavePause, 0f, 0f, null)]
    [InlineData("boss in", StoryMusicCueKind.BossIn, 0f, 0f, null)]
    [InlineData("boss down", StoryMusicCueKind.BossDown, 0f, 0f, null)]
    [InlineData("score escape 234", StoryMusicCueKind.ScoreEscape, 234f, 0f, null)]
    [InlineData("hush 1.5 -20", StoryMusicCueKind.Hush, 1.5f, -20f, null)]
    [InlineData("hush 1 -120", StoryMusicCueKind.Hush, 1f, -80f, null)]
    [InlineData("launch", StoryMusicCueKind.Launch, 0f, 0f, null)]
    public void TheMusicReadsItsCues(string words, StoryMusicCueKind kind, float number, float db, string? state) =>
        Assert.Equal(new StoryMusicCue(kind, number, db, state), StoryMusicCue.Parse(words.Split(' ')));

    [Theory]
    [InlineData("thresher near far")]
    [InlineData("wave alive")]
    [InlineData("hush 1.5")]
    [InlineData("boss half")]
    [InlineData("door shut H")]
    [InlineData("score escape")]
    public void OtherCuesAreLeftToTheirSections(string words) =>
        Assert.Null(StoryMusicCue.Parse(words.Split(' ')));

    [Fact]
    public void ACheckpointGoesOnWithThePieceThatPlayed()
    {
        // A new start always from the first piece.
        Assert.Equal((0, 0.0), StoryScore.Start(StoryList.Stealth, 0, (1, 40)));
        // A checkpoint after a fall: where the last match left it.
        Assert.Equal((1, 40.0), StoryScore.Start(StoryList.Stealth, 3, (1, 40)));
        Assert.Equal((2, 40.0), StoryScore.Start(StoryList.Background, 8, (2, 40)));
        Assert.Equal((0, 12.5), StoryScore.Start(StoryList.Waves, 9, (0, 12.5)));
        // Without one: the piece a run usually hears there (stage 7's checkpoints: 1–5 section 2, 6 the antechamber, 7–14 on).
        for (var checkpoint = 1; checkpoint <= 5; checkpoint++)
            Assert.Equal((0, 0.0), StoryScore.Start(StoryList.Stealth, checkpoint, null));
        Assert.Equal((1, 0.0), StoryScore.Start(StoryList.Stealth, 6, null));
        for (var checkpoint = 7; checkpoint <= 14; checkpoint++)
            Assert.Equal((0, 0.0), StoryScore.Start(StoryList.Background, checkpoint, null));
        Assert.Equal((0, 0.0), StoryScore.Start(StoryList.Background, 7, (9, 10)));
        Assert.Equal((0, 0.0), StoryScore.Start(StoryList.Waves, 9, (1, 10)));
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
