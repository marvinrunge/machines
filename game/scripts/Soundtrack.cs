namespace Machines.Game;

/// <summary>
/// Which music plays (the player's choice in the options, kept as <c>[audio] soundtrack</c>): the original's tracks
/// from the player's own MP3s, the remake's own tracks that come with the game, or both. Until the player has chosen,
/// it follows the look: the classic look plays the original, every other look the remake's (<see cref="SoundtrackChoice.Follow"/>).
/// </summary>
public enum Soundtrack
{
    /// <summary>The original soundtrack only, as the original played its CD (it needs the player's MP3s).</summary>
    Original,
    /// <summary>The remake's menu tune; in a match the scenario's own original track first, then old and new at random.</summary>
    Both,
    /// <summary>The remake's own tracks only.</summary>
    Remake,
}

/// <summary>
/// What a remake track is meant for, as the player marks it in <c>titel.txt</c> (<see cref="TrackMoods"/>): the menus,
/// ordinary matches, the Battle mode, or a boss fight. The original's tracks have none: they play as the original played them.
/// </summary>
public enum Mood
{
    Menu,
    Normal,
    Battle,
    Boss,
}

/// <summary>A tune: one of the original's tracks by its number, or one of the remake's by its name.</summary>
public readonly record struct Tune(int Track, string? Remake)
{
    public static Tune Original(int track) => new(track, null);

    public static Tune Made(string name) => new(-1, name);

    public bool IsRemake => Remake is not null;

    public override string ToString() => Remake ?? $"Track {Track:00}";
}

/// <summary>
/// The remake's tracks and what each is meant for, read from <c>res://assets/music/remake/titel.txt</c>, a file the
/// player edits by hand: lines of <c>name = tags</c> (the file name without .ogg; tags menu, normal, schlacht or battle,
/// boss, comma-separated), <c>#</c> starting a comment. A track the file leaves out, or marks with nothing it knows,
/// counts as normal, and each such line is told in <see cref="Warnings"/>; nothing in the file can stop the music.
/// </summary>
public sealed class TrackMoods
{
    /// <summary>The menu tune when the file marks none: the player's pick ("deep drift v1 is menu music").</summary>
    public const string DefaultMenu = "deep_drift_v1";

    private static readonly Dictionary<string, Mood> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["menu"] = Mood.Menu,
        ["normal"] = Mood.Normal,
        ["schlacht"] = Mood.Battle,
        ["battle"] = Mood.Battle,
        ["boss"] = Mood.Boss,
    };

    private readonly Dictionary<string, HashSet<Mood>> _moods;

    private TrackMoods(List<string> tracks, Dictionary<string, HashSet<Mood>> moods, List<string> warnings)
    {
        Tracks = tracks;
        _moods = moods;
        Warnings = warnings;
    }

    /// <summary>Every remake track there is, in the file's order, those it leaves out after them by name.</summary>
    public IReadOnlyList<string> Tracks { get; }

    /// <summary>What the file got wrong, a line each, for the log.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>No remake tracks at all.</summary>
    public static TrackMoods None { get; } = Read(null, []);

    /// <summary>What <paramref name="track"/> is meant for (nothing for a track there is not).</summary>
    public IReadOnlySet<Mood> Of(string track) => _moods.TryGetValue(track, out var moods) ? moods : new HashSet<Mood>();

    /// <summary>The menu tune: the first track marked menu, or null without one.</summary>
    public string? Menu => Tracks.FirstOrDefault(t => _moods[t].Contains(Mood.Menu));

    /// <summary>
    /// The tracks for <paramref name="mood"/> in a match, in the file's order. Without any marked so, a battle or a boss
    /// takes the normal ones, and normal play every track but the menu's.
    /// </summary>
    public IReadOnlyList<string> Pool(Mood mood)
    {
        var marked = Tracks.Where(t => _moods[t].Contains(mood)).ToList();
        if (mood == Mood.Menu || marked.Count > 0)
            return marked;
        if (mood != Mood.Normal)
            return Pool(Mood.Normal);
        var menu = Menu;
        return Tracks.Where(t => t != menu).ToList();
    }

    /// <summary>
    /// Reads the file's <paramref name="text"/> (null for a file that is not there) over the tracks that are there,
    /// <paramref name="present"/> (file names without .ogg). Where the file marks no menu tune and leaves
    /// <see cref="DefaultMenu"/> out (or is missing), that is the menu tune.
    /// </summary>
    public static TrackMoods Read(string? text, IEnumerable<string> present)
    {
        var there = new HashSet<string>(present.Select(p => p.ToLowerInvariant()));
        var tracks = new List<string>();
        var moods = new Dictionary<string, HashSet<Mood>>();
        var warnings = new List<string>();
        if (text is null && there.Count > 0)
            warnings.Add("titel.txt is missing: every track counts as normal");
        var lines = (text ?? "").Replace("\r\n", "\n").Split('\n');
        for (var at = 0; at < lines.Length; at++)
        {
            var line = lines[at].Trim().TrimStart('﻿');
            if (line.Length == 0 || line.StartsWith('#'))
                continue;
            var where = $"titel.txt line {at + 1} \"{line}\"";
            var equals = line.IndexOf('=');
            if (equals < 0)
            {
                warnings.Add($"{where}: no \"=\" between the name and its marks, left out");
                continue;
            }
            var name = line[..equals].Trim().ToLowerInvariant();
            if (name.EndsWith(".ogg", StringComparison.Ordinal))
                name = name[..^4].TrimEnd();
            if (!there.Contains(name))
            {
                warnings.Add($"{where}: there is no {name}.ogg, left out");
                continue;
            }
            if (!moods.TryGetValue(name, out var marks))
            {
                moods[name] = marks = [];
                tracks.Add(name);
            }
            else
                warnings.Add($"{where}: {name} is listed twice, its marks are joined");
            foreach (var word in line[(equals + 1)..].Split(',', ';').Select(w => w.Trim()).Where(w => w.Length > 0))
            {
                if (Words.TryGetValue(word, out var mood))
                    marks.Add(mood);
                else
                    warnings.Add($"{where}: unknown mark \"{word}\" (menu, normal, schlacht, boss), left out");
            }
            if (marks.Count == 0)
            {
                warnings.Add($"{where}: no mark it knows, counts as normal");
                marks.Add(Mood.Normal);
            }
        }
        var noMenu = !moods.Values.Any(m => m.Contains(Mood.Menu));
        foreach (var name in there.Order(StringComparer.Ordinal).Where(n => !moods.ContainsKey(n)))
        {
            // Left out of a file that marks no menu tune, the player's pick stays it.
            var mood = noMenu && name == DefaultMenu ? Mood.Menu : Mood.Normal;
            if (text is not null)
                warnings.Add($"titel.txt does not list {name}: it counts as {mood.ToString().ToLowerInvariant()}");
            moods[name] = [mood];
            tracks.Add(name);
        }
        return new TrackMoods(tracks, moods, warnings);
    }
}

/// <summary>
/// What plays when, for each <see cref="Soundtrack"/>, apart from Godot so that it can be tested (see <c>Music</c>,
/// which plays it). The original's tracks are numbered as the soundtrack's release numbers them (startup.hpp's Music
/// values plus one); the remake's by their file names under <c>res://assets/music/remake</c>, each meant for a
/// <see cref="Mood"/> (<see cref="TrackMoods"/>).
/// </summary>
public static class SoundtrackChoice
{
    // Track numbers of the original soundtrack; the first is the intro film's.
    public const int Intro = 0, MenuTune = 1, VictoryTune = 2, DefeatTune = 3, FirstInGame = 4, DefaultInGame = 9;

    /// <summary>The soundtrack a look brings until the player chooses one: the original with the classic look, else the remake's.</summary>
    public static Soundtrack Follow(bool classicLook) => classicLook ? Soundtrack.Original : Soundtrack.Remake;

    /// <summary>The soundtrack that plays: the player's choice, or the look's (<see cref="Follow"/>) while there is none.</summary>
    public static Soundtrack Effective(Soundtrack? chosen, bool classicLook) => chosen ?? Follow(classicLook);

    /// <summary>The choice as kept in the settings, or null for one not made (or not understood).</summary>
    public static Soundtrack? Parse(string? kept) =>
        Enum.TryParse<Soundtrack>(kept, ignoreCase: true, out var choice) && Enum.IsDefined(choice) ? choice : null;

    /// <summary>
    /// Over the menus: the original's intro tune the first time (where the original plays and no intro film has played
    /// with its own sound), then the menu tune: the remake's unless only the original plays, the original's where the
    /// remake has none; the remake alone with no menu tune marked plays its first normal track.
    /// </summary>
    public static Tune? Menu(Soundtrack mode, bool introPlayed, IReadOnlyCollection<int> originals, TrackMoods remakes)
    {
        if (!introPlayed && mode != Soundtrack.Remake && originals.Contains(Intro))
            return Tune.Original(Intro);
        if (mode != Soundtrack.Original && remakes.Menu is { } menu)
            return Tune.Made(menu);
        if (mode != Soundtrack.Remake)
            return originals.Contains(MenuTune) ? Tune.Original(MenuTune) : null;
        return remakes.Pool(Mood.Normal) is [var first, ..] ? Tune.Made(first) : null;
    }

    /// <summary>
    /// A match begins: the original plays the scenario's own track (sceninfo's MUSIC, <paramref name="scenarioMusic"/>,
    /// the CD's track number) or its default in-game track, and so does Both first in an ordinary match; the remake's
    /// starts at random among the tracks for the <paramref name="mood"/>. Where the first is missing, the shuffle begins
    /// (<see cref="Next"/>).
    /// </summary>
    public static Tune? Match(Soundtrack mode, Mood mood, int? scenarioMusic, IReadOnlyCollection<int> originals, TrackMoods remakes, Func<int, int> random)
    {
        if (mode == Soundtrack.Original || mode == Soundtrack.Both && mood == Mood.Normal)
        {
            var first = scenarioMusic is { } m ? m - 1 : DefaultInGame;
            if (originals.Contains(first))
                return Tune.Original(first);
        }
        return Next(mode, mood, null, originals, remakes, random);
    }

    /// <summary>
    /// The tracks a match shuffles: the original's in-game tracks (whatever the mood: the original knows none), the
    /// remake's for the mood (<see cref="TrackMoods.Pool"/>), or both together in an ordinary match; a battle or a boss fight plays
    /// the remake's tracks marked for it alone, as only they carry marks.
    /// </summary>
    public static List<Tune> InGame(Soundtrack mode, Mood mood, IReadOnlyCollection<int> originals, TrackMoods remakes)
    {
        var tunes = new List<Tune>();
        if (mode != Soundtrack.Original)
            tunes.AddRange(remakes.Pool(mood).Select(Tune.Made));
        if (mode == Soundtrack.Original || mode == Soundtrack.Both && (mood == Mood.Normal || tunes.Count == 0))
            tunes.InsertRange(0, originals.Where(t => t >= FirstInGame).Order().Select(Tune.Original));
        return tunes;
    }

    /// <summary>
    /// The next track of a match at random (<paramref name="random"/> gives 0 to its argument less one), never the one
    /// that just played (<paramref name="after"/>) unless it is the only one; null without any.
    /// </summary>
    public static Tune? Next(Soundtrack mode, Mood mood, Tune? after, IReadOnlyCollection<int> originals, TrackMoods remakes, Func<int, int> random)
    {
        var tunes = InGame(mode, mood, originals, remakes);
        var choices = tunes.Where(t => t != after).ToList();
        if (choices.Count == 0)
            return tunes.Count > 0 ? tunes[0] : null;
        return choices[Math.Clamp(random(choices.Count), 0, choices.Count - 1)];
    }

    /// <summary>
    /// The match is over: the original's victory or defeat tune, where the original plays and the player has it; the
    /// remake has none of its own, so with its music alone the match's track fades out (null).
    /// </summary>
    public static Tune? End(Soundtrack mode, bool won, IReadOnlyCollection<int> originals)
    {
        var tune = won ? VictoryTune : DefeatTune;
        return mode != Soundtrack.Remake && originals.Contains(tune) ? Tune.Original(tune) : null;
    }
}
