using Machines.Game;
using Xunit;

namespace Machines.Tests.Audio;

/// <summary>
/// Which music plays when, by the soundtrack chosen (SoundtrackChoice): the original's from the player's MP3s, the
/// remake's own, or both; what each remake track is meant for (TrackMoods, from titel.txt); and that the remake's
/// tracks and their titel.txt come with the game, imported.
/// </summary>
public class SoundtrackTests
{
    // The original soundtrack as its release has it: Track 00 (intro) to Track 10.
    private static readonly int[] AllOriginals = Enumerable.Range(0, 11).ToArray();
    private static readonly int[] NoOriginals = [];

    private static readonly string[] Names =
    [
        "deep_drift_v1", "deep_drift_v2", "circuitry_v1", "circuitry_v2", "glitching_dark_v1", "glitching_dark_v2",
        "iron_gears_v1", "iron_gears_v2", "sovereign_circuit_v1", "static_dread_v1", "static_dread_v2", "warm_ticks",
    ];

    private const string Marked = """
        # a comment
        deep_drift_v1 = menu
        warm_ticks = normal
        deep_drift_v2 = normal
        circuitry_v1 = schlacht
        iron_gears_v1 = normal, battle
        sovereign_circuit_v1 = schlacht, boss
        """;

    // Every track listed but the ones Marked leaves out count as normal.
    private static readonly TrackMoods AllRemakes = TrackMoods.Read(Marked, Names);
    private static readonly TrackMoods NoRemakes = TrackMoods.None;

    private static int First(int count) => 0;

    [Fact]
    public void A_soundtrack_not_chosen_follows_the_look()
    {
        Assert.Equal(Soundtrack.Original, SoundtrackChoice.Effective(null, classicLook: true));
        Assert.Equal(Soundtrack.Remake, SoundtrackChoice.Effective(null, classicLook: false));
    }

    [Theory]
    [InlineData(Soundtrack.Original, true)]
    [InlineData(Soundtrack.Original, false)]
    [InlineData(Soundtrack.Both, true)]
    [InlineData(Soundtrack.Remake, true)]
    public void A_chosen_soundtrack_sticks_whatever_the_look(Soundtrack chosen, bool classicLook) =>
        Assert.Equal(chosen, SoundtrackChoice.Effective(chosen, classicLook));

    [Theory]
    [InlineData("Original", Soundtrack.Original)]
    [InlineData("both", Soundtrack.Both)]
    [InlineData("REMAKE", Soundtrack.Remake)]
    [InlineData("", null)]
    [InlineData(null, null)]
    [InlineData("Loud", null)]
    [InlineData("7", null)]
    public void The_kept_choice_reads_back_or_counts_as_none(string? kept, Soundtrack? expected) =>
        Assert.Equal(expected, SoundtrackChoice.Parse(kept));

    [Fact]
    public void The_original_plays_its_intro_then_its_menu_tune_as_before()
    {
        Assert.Equal(Tune.Original(0), SoundtrackChoice.Menu(Soundtrack.Original, false, AllOriginals, AllRemakes));
        Assert.Equal(Tune.Original(1), SoundtrackChoice.Menu(Soundtrack.Original, true, AllOriginals, AllRemakes));
        Assert.Null(SoundtrackChoice.Menu(Soundtrack.Original, false, NoOriginals, AllRemakes));
    }

    [Fact]
    public void The_remake_plays_deep_drift_v1_over_the_menus_without_the_original_intro()
    {
        Assert.Equal(Tune.Made("deep_drift_v1"), SoundtrackChoice.Menu(Soundtrack.Remake, false, AllOriginals, AllRemakes));
        Assert.Equal(Tune.Made("deep_drift_v1"), SoundtrackChoice.Menu(Soundtrack.Remake, true, NoOriginals, AllRemakes));
        // Nothing of the original, even where the remake's tune were missing.
        Assert.Null(SoundtrackChoice.Menu(Soundtrack.Remake, true, AllOriginals, NoRemakes));
    }

    [Fact]
    public void Both_keep_the_original_intro_then_play_the_remake_menu_tune()
    {
        Assert.Equal(Tune.Original(0), SoundtrackChoice.Menu(Soundtrack.Both, false, AllOriginals, AllRemakes));
        Assert.Equal(Tune.Made("deep_drift_v1"), SoundtrackChoice.Menu(Soundtrack.Both, true, AllOriginals, AllRemakes));
        Assert.Equal(Tune.Made("deep_drift_v1"), SoundtrackChoice.Menu(Soundtrack.Both, false, NoOriginals, AllRemakes));
        Assert.Equal(Tune.Original(1), SoundtrackChoice.Menu(Soundtrack.Both, true, AllOriginals, NoRemakes));
    }

    [Fact]
    public void The_first_track_marked_menu_is_the_menu_tune()
    {
        var moods = TrackMoods.Read("warm_ticks = normal\ncircuitry_v1 = menu\ndeep_drift_v1 = menu, normal", Names);
        Assert.Equal("circuitry_v1", moods.Menu);
        Assert.Equal(Tune.Made("circuitry_v1"), SoundtrackChoice.Menu(Soundtrack.Remake, true, NoOriginals, moods));
        // A track marked menu and normal plays in a match as well; one marked menu alone does not.
        Assert.Contains("deep_drift_v1", moods.Pool(Mood.Normal));
        Assert.DoesNotContain("circuitry_v1", moods.Pool(Mood.Normal));
    }

    [Fact]
    public void Without_a_menu_mark_the_remake_menu_falls_back()
    {
        // The file leaves deep drift v1 out: it stays the menu tune.
        var left = TrackMoods.Read("warm_ticks = normal", Names);
        Assert.Equal("deep_drift_v1", left.Menu);
        // The file gives it to matches: the menus play the first normal track.
        var given = TrackMoods.Read("warm_ticks = normal\ndeep_drift_v1 = normal", ["warm_ticks", "deep_drift_v1"]);
        Assert.Null(given.Menu);
        Assert.Equal(Tune.Made("warm_ticks"), SoundtrackChoice.Menu(Soundtrack.Remake, true, NoOriginals, given));
        Assert.Equal(Tune.Original(1), SoundtrackChoice.Menu(Soundtrack.Both, true, AllOriginals, given));
    }

    [Fact]
    public void The_original_starts_a_match_on_the_scenario_track_or_track_09()
    {
        // sceninfo's MUSIC is the CD's track number, one more than the release's.
        Assert.Equal(Tune.Original(5), SoundtrackChoice.Match(Soundtrack.Original, Mood.Normal, 6, AllOriginals, AllRemakes, First));
        Assert.Equal(Tune.Original(9), SoundtrackChoice.Match(Soundtrack.Original, Mood.Normal, null, AllOriginals, AllRemakes, First));
        // A battle too: the original knows no moods.
        Assert.Equal(Tune.Original(5), SoundtrackChoice.Match(Soundtrack.Original, Mood.Battle, 6, AllOriginals, AllRemakes, First));
        // A missing first track: the shuffle begins, with the original's tracks only.
        Assert.Equal(Tune.Original(4), SoundtrackChoice.Match(Soundtrack.Original, Mood.Normal, 20, AllOriginals, AllRemakes, First));
        Assert.Null(SoundtrackChoice.Match(Soundtrack.Original, Mood.Normal, 6, NoOriginals, AllRemakes, First));
    }

    [Fact]
    public void Both_start_a_match_on_the_scenario_track_and_the_remake_at_random()
    {
        Assert.Equal(Tune.Original(5), SoundtrackChoice.Match(Soundtrack.Both, Mood.Normal, 6, AllOriginals, AllRemakes, First));
        // Without the original's MP3s, the remake's tracks.
        Assert.True(SoundtrackChoice.Match(Soundtrack.Both, Mood.Normal, 6, NoOriginals, AllRemakes, First) is { IsRemake: true });
        var picked = SoundtrackChoice.Match(Soundtrack.Remake, Mood.Normal, 6, AllOriginals, AllRemakes, n => n - 1);
        Assert.True(picked is { IsRemake: true });
        Assert.NotEqual(Tune.Made("deep_drift_v1"), picked);
    }

    [Fact]
    public void A_battle_plays_the_tracks_marked_schlacht()
    {
        var remake = SoundtrackChoice.InGame(Soundtrack.Remake, Mood.Battle, AllOriginals, AllRemakes);
        Assert.Equal(new[] { "circuitry_v1", "iron_gears_v1", "sovereign_circuit_v1" }.Select(Tune.Made), remake);
        // Both play the marked tracks alone in a battle, not the scenario's track: the original's carry no marks.
        Assert.Equal(remake, SoundtrackChoice.InGame(Soundtrack.Both, Mood.Battle, AllOriginals, AllRemakes));
        Assert.Equal(Tune.Made("circuitry_v1"), SoundtrackChoice.Match(Soundtrack.Both, Mood.Battle, 6, AllOriginals, AllRemakes, First));
        // Without remake tracks, the original's.
        Assert.Equal(Enumerable.Range(4, 7).Select(Tune.Original), SoundtrackChoice.InGame(Soundtrack.Both, Mood.Battle, AllOriginals, NoRemakes));
    }

    [Fact]
    public void A_boss_plays_the_remake_tracks_marked_boss_alone()
    {
        Assert.Equal(new[] { Tune.Made("sovereign_circuit_v1") }, SoundtrackChoice.InGame(Soundtrack.Remake, Mood.Boss, AllOriginals, AllRemakes));
        Assert.Equal(new[] { Tune.Made("sovereign_circuit_v1") }, SoundtrackChoice.InGame(Soundtrack.Both, Mood.Boss, AllOriginals, AllRemakes));
        // The original's music goes on as it was.
        Assert.Equal(SoundtrackChoice.InGame(Soundtrack.Original, Mood.Normal, AllOriginals, AllRemakes),
            SoundtrackChoice.InGame(Soundtrack.Original, Mood.Boss, AllOriginals, AllRemakes));
        // Both without remake tracks: the original's.
        Assert.Equal(Enumerable.Range(4, 7).Select(Tune.Original), SoundtrackChoice.InGame(Soundtrack.Both, Mood.Boss, AllOriginals, NoRemakes));
    }

    [Fact]
    public void An_empty_pool_falls_back_to_normal_and_normal_to_all_but_the_menu_tune()
    {
        var plain = TrackMoods.Read("deep_drift_v1 = menu\nwarm_ticks = schlacht", ["deep_drift_v1", "warm_ticks", "circuitry_v1"]);
        // circuitry_v1 is left out, so normal: battle has warm_ticks, boss falls back to normal.
        Assert.Equal(new[] { "warm_ticks" }, plain.Pool(Mood.Battle));
        Assert.Equal(new[] { "circuitry_v1" }, plain.Pool(Mood.Boss));
        var none = TrackMoods.Read("deep_drift_v1 = menu\nwarm_ticks = boss\ncircuitry_v1 = schlacht", ["deep_drift_v1", "warm_ticks", "circuitry_v1"]);
        Assert.Equal(new[] { "warm_ticks", "circuitry_v1" }, none.Pool(Mood.Normal));
        Assert.Equal(new[] { "warm_ticks" }, none.Pool(Mood.Boss));
    }

    [Fact]
    public void A_match_shuffles_the_tracks_of_its_soundtrack_never_the_remake_menu_tune()
    {
        var original = SoundtrackChoice.InGame(Soundtrack.Original, Mood.Normal, AllOriginals, AllRemakes);
        Assert.Equal(Enumerable.Range(4, 7).Select(Tune.Original), original);

        var remake = SoundtrackChoice.InGame(Soundtrack.Remake, Mood.Normal, AllOriginals, AllRemakes);
        Assert.All(remake, t => Assert.True(t.IsRemake));
        Assert.DoesNotContain(Tune.Made("deep_drift_v1"), remake);
        // Marked normal, and the tracks the file leaves out; not those marked only for battle or boss.
        Assert.Contains(Tune.Made("deep_drift_v2"), remake);
        Assert.Contains(Tune.Made("iron_gears_v1"), remake);
        Assert.Contains(Tune.Made("static_dread_v2"), remake);
        Assert.DoesNotContain(Tune.Made("circuitry_v1"), remake);
        Assert.DoesNotContain(Tune.Made("sovereign_circuit_v1"), remake);

        var both = SoundtrackChoice.InGame(Soundtrack.Both, Mood.Normal, AllOriginals, AllRemakes);
        Assert.Equal(original.Concat(remake), both);
    }

    [Theory]
    [InlineData(Soundtrack.Original, Mood.Normal)]
    [InlineData(Soundtrack.Both, Mood.Normal)]
    [InlineData(Soundtrack.Remake, Mood.Normal)]
    [InlineData(Soundtrack.Remake, Mood.Battle)]
    [InlineData(Soundtrack.Both, Mood.Battle)]
    public void The_next_track_is_never_the_one_that_just_played(Soundtrack mode, Mood mood)
    {
        var tunes = SoundtrackChoice.InGame(mode, mood, AllOriginals, AllRemakes);
        var random = new Random(7);
        Tune? playing = null;
        var heard = new HashSet<Tune>();
        for (var i = 0; i < 500; i++)
        {
            var next = SoundtrackChoice.Next(mode, mood, playing, AllOriginals, AllRemakes, random.Next);
            Assert.NotNull(next);
            Assert.NotEqual(playing, next);
            Assert.Contains(next.Value, tunes);
            heard.Add(next.Value);
            playing = next;
        }
        // Every track of the soundtrack comes up.
        Assert.Equal(tunes.Count, heard.Count);
    }

    [Fact]
    public void A_single_track_plays_again_and_none_gives_silence()
    {
        Assert.Equal(Tune.Original(4), SoundtrackChoice.Next(Soundtrack.Original, Mood.Normal, Tune.Original(4), [4], NoRemakes, First));
        Assert.Equal(Tune.Made("sovereign_circuit_v1"),
            SoundtrackChoice.Next(Soundtrack.Remake, Mood.Boss, Tune.Made("sovereign_circuit_v1"), NoOriginals, AllRemakes, First));
        Assert.Null(SoundtrackChoice.Next(Soundtrack.Original, Mood.Normal, null, [0, 1, 2, 3], AllRemakes, First));
        Assert.Null(SoundtrackChoice.Next(Soundtrack.Remake, Mood.Normal, null, AllOriginals, TrackMoods.Read("deep_drift_v1 = menu", ["deep_drift_v1"]), First));
    }

    [Fact]
    public void The_end_plays_the_original_tunes_unless_the_remake_alone_plays()
    {
        Assert.Equal(Tune.Original(2), SoundtrackChoice.End(Soundtrack.Original, true, AllOriginals));
        Assert.Equal(Tune.Original(3), SoundtrackChoice.End(Soundtrack.Original, false, AllOriginals));
        Assert.Equal(Tune.Original(2), SoundtrackChoice.End(Soundtrack.Both, true, AllOriginals));
        Assert.Null(SoundtrackChoice.End(Soundtrack.Both, true, NoOriginals));
        Assert.Null(SoundtrackChoice.End(Soundtrack.Remake, true, AllOriginals));
        Assert.Null(SoundtrackChoice.End(Soundtrack.Remake, false, AllOriginals));
    }

    [Fact]
    public void The_file_takes_english_words_any_case_and_ogg_names()
    {
        var moods = TrackMoods.Read("Circuitry_V1.ogg = Battle , BOSS\nwarm_ticks=menu;normal", Names);
        Assert.Equal(new HashSet<Mood> { Mood.Battle, Mood.Boss }, moods.Of("circuitry_v1"));
        Assert.Equal(new HashSet<Mood> { Mood.Menu, Mood.Normal }, moods.Of("warm_ticks"));
        Assert.Equal("warm_ticks", moods.Menu);
    }

    [Fact]
    public void A_wrong_line_is_warned_about_and_never_stops_the_music()
    {
        var text = """
            deep_drift_v1 = menu
            warm_ticks = hektisch
            circuitry_v1 schlacht
            no_such_song = boss
            iron_gears_v1 =
            iron_gears_v1 = boss
            """;
        var moods = TrackMoods.Read(text, Names);
        // An unknown mark or none counts as normal; a line without "=" or for a track there is not is left out.
        Assert.Equal(new HashSet<Mood> { Mood.Normal }, moods.Of("warm_ticks"));
        Assert.Equal(new HashSet<Mood> { Mood.Normal }, moods.Of("circuitry_v1"));
        Assert.Equal(new HashSet<Mood> { Mood.Normal, Mood.Boss }, moods.Of("iron_gears_v1"));
        Assert.Equal(Names.Length, moods.Tracks.Count);
        Assert.Contains(moods.Warnings, w => w.Contains("line 2") && w.Contains("hektisch"));
        Assert.Contains(moods.Warnings, w => w.Contains("line 3") && w.Contains("="));
        Assert.Contains(moods.Warnings, w => w.Contains("line 4") && w.Contains("no_such_song"));
        Assert.Contains(moods.Warnings, w => w.Contains("line 5"));
        Assert.Contains(moods.Warnings, w => w.Contains("line 6") && w.Contains("twice"));
        Assert.Contains(moods.Warnings, w => w.Contains("does not list static_dread_v1"));

        // Nothing readable at all, or no file: every track plays, deep drift v1 over the menus.
        foreach (var broken in new[] { "\0\0ÿ garbage ===,,,", null })
        {
            var fallback = TrackMoods.Read(broken, Names);
            Assert.Equal("deep_drift_v1", fallback.Menu);
            Assert.Equal(Names.Length - 1, fallback.Pool(Mood.Normal).Count);
            Assert.NotEmpty(fallback.Warnings);
        }
    }

    [Fact]
    public void The_shipped_titel_txt_lists_every_track_there_is_and_no_other()
    {
        var folder = RemakeFolder();
        var files = Directory.GetFiles(folder, "*.ogg").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToArray();
        var text = File.ReadAllText(Path.Combine(folder, "titel.txt"));
        var listed = text.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#'))
            .Select(l => l[..l.IndexOf('=')].Trim()).Order().ToArray();
        Assert.Equal(files, listed);
        var moods = TrackMoods.Read(text, files);
        Assert.Empty(moods.Warnings);
        Assert.Equal("deep_drift_v1", moods.Menu);
        // Every kind of match has tracks of its own marked.
        Assert.All(new[] { Mood.Normal, Mood.Battle, Mood.Boss }, mood => Assert.Contains(files, f => moods.Of(f).Contains(mood)));
    }

    [Fact]
    public void The_remake_tracks_come_with_the_game_imported()
    {
        var folder = RemakeFolder();
        var files = Directory.GetFiles(folder, "*.ogg").Select(f => Path.GetFileNameWithoutExtension(f)).Order().ToArray();
        Assert.Equal(Names.Order(), files);
        // Godot loads them in an exported build only from their import; its file is committed beside each.
        Assert.All(files, name => Assert.True(File.Exists(Path.Combine(folder, name + ".ogg.import")), $"{name}.ogg.import missing"));
        Assert.Empty(Directory.GetFiles(folder, "*.mp3"));
        // The export takes titel.txt, which is no resource, only by its include filter.
        var presets = File.ReadAllText(Path.Combine(folder, "..", "..", "..", "export_presets.cfg"));
        Assert.Contains("assets/music/remake/titel.txt", presets);
    }

    private static string RemakeFolder()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "game", "assets", "music", "remake");
            if (Directory.Exists(candidate))
                return candidate;
        }
        throw new DirectoryNotFoundException($"game/assets/music/remake not found above {AppContext.BaseDirectory}");
    }
}
