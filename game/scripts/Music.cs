using System.Text.RegularExpressions;
using Godot;

namespace Machines.Game;

/// <summary>
/// The music, by the player's <see cref="Soundtrack"/> (what plays when is <see cref="SoundtrackChoice"/>'s).
/// The original soundtrack plays as the original played its CD (MachGuiStartupScreens::updateCdAudio): the menu
/// tune over the menus, the scenario's own track (its <c>MUSIC</c> in sceninfo.dat) when a match begins and then
/// the in-game tracks at random, and a short victory or defeat tune at the end. Its tracks are the MP3s of the
/// soundtrack the player owns, numbered as its release numbers them ("Track 01 - Menu.mp3" ...), which are the
/// CD's track numbers less one. The folder is picked in the menu and kept in <c>user://settings.cfg</c>; until
/// one is picked, a <c>music</c> folder beside the game serves. The remake's own tracks come with the game
/// (<c>res://assets/music/remake</c>, loudness matched to the original's), each marked in its <c>titel.txt</c> for
/// the menus, ordinary matches, the Battle mode or Wired's boss fights (<see cref="TrackMoods"/>): its menu tune over
/// the menus, the others at random in a match by its kind, the boss tracks while a boss stands on the map. They are all
/// kept at the level of the original's in-game tracks, and a remake track over the menus plays <see cref="MenuDb"/>
/// quieter, as the original's menu tune is, so that the file may mark any of them for anything.
/// MACHINES_SOUNDTRACK=Original|Both|Remake sets the soundtrack for one run, unsaved.
/// </summary>
public partial class Music : Node
{
    private const string Settings = "user://settings.cfg";
    private const string RemakeFolder = "res://assets/music/remake";
    private const string MoodsFile = RemakeFolder + "/titel.txt";
    private const float FadeSeconds = 0.8f;
    // A boss coming or going changes the music more gently than a new screen.
    private const float MoodFadeSeconds = 2.5f;
    // The original's menu tune is about 6 LU quieter than its in-game tracks (-16.3 against -9.5 LUFS); the remake's
    // tracks are all kept at the in-game level (-10.1), so over the menus one plays this much quieter.
    private const float MenuDb = -6f;

    private enum Phase
    {
        Menu,
        Match,
        End,
    }

    private readonly record struct Wanted(Phase Phase, int? ScenarioMusic = null, bool Won = false, Mood Mood = Mood.Normal);

    private static Music? _instance;
    private static bool _introPlayed;
    private static Soundtrack? _asked = SoundtrackChoice.Parse(System.Environment.GetEnvironmentVariable("MACHINES_SOUNDTRACK"));

    private readonly Dictionary<int, string> _tracks = [];
    private readonly HashSet<string> _remakes = [];
    private string? _moodsText;
    private TrackMoods _moods = TrackMoods.None;
    private readonly RandomNumberGenerator _random = new();
    private AudioStreamPlayer _player = null!;
    private Wanted? _wanted;
    // The wanted phase has just begun: a match starts on its first track rather than going on with the one playing.
    private bool _fresh;
    // A boss stands on the map (Wired's boss waves): an ordinary match plays its boss tracks meanwhile.
    private bool _boss;
    private Tune? _playing;
    // How far below the music's volume the tune playing plays (see Gain).
    private float _gainDb;
    private Tween? _fade;

    /// <summary>The menus: the original's intro tune the first time where it plays, then the menu tune over and over.</summary>
    public static void Menu(Node from) => Want(from, new Wanted(Phase.Menu));

    /// <summary>The intro film has played with its own sound (<see cref="StartupFilms"/>): the menus start with the menu tune.</summary>
    public static void IntroFilmPlayed() => _introPlayed = true;

    /// <summary>A film with its own sound is to play over the menus: the tune fades out until <see cref="Menu"/> asks for it again.</summary>
    public static void Silence()
    {
        if (_instance is null || !IsInstanceValid(_instance) || !_instance._player.Playing)
            return;
        var music = _instance;
        (music._wanted, music._playing) = (null, null);
        music.FadeOut(null, FadeSeconds);
    }

    /// <summary>
    /// A match on a scenario whose catalogue entry asks for <paramref name="scenarioMusic"/> (sceninfo's MUSIC); a
    /// <paramref name="battle"/> (the Battle mode) plays the remake's tracks marked for it.
    /// </summary>
    public static void Match(Node from, int? scenarioMusic, bool battle = false) =>
        Want(from, new Wanted(Phase.Match, scenarioMusic, Mood: battle ? Mood.Battle : Mood.Normal));

    /// <summary>
    /// Whether a boss stands on the map now (asked every frame of a match): an ordinary match fades to the remake's boss
    /// tracks while one does and back to its own when none is left. The original's music goes on as it was.
    /// </summary>
    public static void Boss(bool standing)
    {
        if (_instance is null || !IsInstanceValid(_instance) || _instance._boss == standing)
            return;
        _instance._boss = standing;
        if (_instance._wanted is { Phase: Phase.Match, Mood: Mood.Normal } && _instance.IsInsideTree())
            _instance.Apply(MoodFadeSeconds);
    }

    /// <summary>The match is over: the victory or defeat tune, once (with the remake's music alone the track fades out).</summary>
    public static void End(Node from, bool won) => Want(from, new Wanted(Phase.End, Won: won));

    /// <summary>The soundtrack the player chose, or null while they have not (then it follows the look).</summary>
    public static Soundtrack? Chosen => _asked ?? SoundtrackChoice.Parse((string)Setting("soundtrack", ""));

    /// <summary>
    /// The soundtrack that plays: the one chosen, else the look's (the classic look the original's, every other the
    /// remake's). Choosing one keeps it and takes at once, fading to the tune it plays now.
    /// </summary>
    public static Soundtrack Mode
    {
        get => SoundtrackChoice.Effective(Chosen, GraphicsSettings.Look == Look.Classic);
        set
        {
            _asked = null;
            Store("soundtrack", value.ToString());
            Refresh();
        }
    }

    /// <summary>Something the soundtrack depends on has changed (the look, while none is chosen): the right tune fades in.</summary>
    public static void Refresh()
    {
        if (_instance is not null && IsInstanceValid(_instance) && _instance.IsInsideTree())
            _instance.Apply(FadeSeconds);
    }

    /// <summary>The folder holding the original soundtrack's MP3s, or null.</summary>
    public static string? Folder
    {
        get => (string)Setting("music_folder", "") is { Length: > 0 } folder ? folder
            : TrackCount(Beside) > 0 ? Beside : null;
        set
        {
            Store("music_folder", value ?? "");
            if (_instance is not null && IsInstanceValid(_instance))
            {
                _instance.FindTracks();
                // A track of the old folder starts again from the new one, a match's on its first.
                if (_instance._playing is { IsRemake: false })
                {
                    _instance._playing = null;
                    _instance._fresh = _instance._wanted is { Phase: Phase.Match };
                }
                Refresh();
            }
        }
    }

    /// <summary>Music volume, 0 to 1.</summary>
    public static float Volume
    {
        get => (float)Setting("music_volume", 0.7);
        set
        {
            Store("music_volume", value);
            if (_instance is not null && IsInstanceValid(_instance) && _instance._fade is null && _instance.IsInsideTree())
                _instance._player.VolumeDb = _instance.Level;
        }
    }

    /// <summary>The soundtrack packed beside the game, when no folder has been picked.</summary>
    private static string Beside => System.IO.Path.Combine(Paths.Root, "music");

    /// <summary>How many tracks the folder holds (0 without one).</summary>
    public static int TrackCount(string? folder) => Tracks(folder).Count;

    private static void Want(Node from, Wanted wanted)
    {
        if (_instance is null || !IsInstanceValid(_instance))
        {
            // The player lives at the root, so it plays on when the menu and the match replace each other.
            _instance = new Music { Name = "Music" };
            _instance.FindTracks();
            from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        }
        _instance._wanted = wanted;
        _instance._fresh = true;
        _instance._boss = false;
        if (_instance.IsInsideTree())
            _instance.Apply(FadeSeconds);
    }

    public override void _Ready()
    {
        _player = new AudioStreamPlayer { Name = "Player", VolumeDb = Level };
        _player.Finished += OnFinished;
        AddChild(_player);
        Apply(FadeSeconds);
    }

    private void FindTracks()
    {
        _tracks.Clear();
        foreach (var (number, path) in Tracks(Folder))
            _tracks[number] = path;
        _remakes.Clear();
        // An exported build keeps each track's import (name.ogg.import) in place of the file itself.
        foreach (var file in DirAccess.GetFilesAt(RemakeFolder))
            if (Regex.Match(file, @"^(.+)\.ogg(\.import|\.remap)?$") is { Success: true } match && ResourceLoader.Exists(RemakeFile(match.Groups[1].Value)))
                _remakes.Add(match.Groups[1].Value);
        _moodsText = Godot.FileAccess.FileExists(MoodsFile) ? Godot.FileAccess.GetFileAsString(MoodsFile) : null;
        _moods = TrackMoods.Read(_moodsText, _remakes);
        foreach (var warning in _moods.Warnings)
            GD.PushWarning($"music: {warning}");
    }

    private static string RemakeFile(string name) => $"{RemakeFolder}/{name}.ogg";

    private static Dictionary<int, string> Tracks(string? folder)
    {
        var tracks = new Dictionary<int, string>();
        if (folder is null || !Directory.Exists(folder))
            return tracks;
        foreach (var file in Directory.GetFiles(folder, "*.mp3"))
            if (Regex.Match(System.IO.Path.GetFileName(file), @"(?i)track\s*(\d+)") is { Success: true } match)
                tracks.TryAdd(int.Parse(match.Groups[1].Value), file);
        return tracks;
    }

    private int Random(int count) => _random.RandiRange(0, count - 1);

    /// <summary>What a match plays for now: a boss's tracks over an ordinary match while one stands, else the match's own.</summary>
    private Mood MoodOf(Wanted wanted) => wanted.Mood == Mood.Normal && _boss ? Mood.Boss : wanted.Mood;

    /// <summary>
    /// The tune the wanted phase calls for under the soundtrack now: a match starts on its first track when it begins,
    /// and later goes on with the one playing where the soundtrack and the mood still have it.
    /// </summary>
    private Tune? Choose(Wanted wanted)
    {
        var mode = Mode;
        var mood = MoodOf(wanted);
        var originals = _tracks.Keys;
        return wanted.Phase switch
        {
            Phase.Menu => SoundtrackChoice.Menu(mode, _introPlayed, originals, _moods),
            Phase.Match when _fresh => SoundtrackChoice.Match(mode, mood, wanted.ScenarioMusic, originals, _moods, Random),
            Phase.Match when _playing is { } tune && SoundtrackChoice.InGame(mode, mood, originals, _moods).Contains(tune) => tune,
            Phase.Match => SoundtrackChoice.Next(mode, mood, _playing, originals, _moods, Random),
            _ => SoundtrackChoice.End(mode, wanted.Won, originals),
        };
    }

    /// <summary>Starts the wanted tune unless it is already playing, fading out the one before (or only that, for none).</summary>
    private void Apply(float fade)
    {
        if (_wanted is not { } wanted || _player is null)
            return;
        var tune = Choose(wanted);
        _fresh = false;
        if (tune == _playing && (tune is not null || !_player.Playing))
        {
            // The tune plays on into the new phase (the menu tune where a match picks it first, or back): from now on
            // it plays as this phase plays it, once or over and over, at its level. A fade under way starts it so anyway.
            if (tune is { } going && _fade is null && _player.Stream is { } stream)
            {
                Looped(stream, Loops(going));
                if (System.Environment.GetEnvironmentVariable("MACHINES_AUDIO_TRACE") == "1")
                    GD.Print($"music {going} plays on ({(Loops(going) ? "looped" : "once")}, {Gain(going):0} dB)");
                if (!Mathf.IsEqualApprox(_gainDb, Gain(going)))
                {
                    _gainDb = Gain(going);
                    _fade = CreateTween();
                    _fade.TweenProperty(_player, "volume_db", Level, fade);
                    _fade.TweenCallback(Callable.From(() => _fade = null));
                }
            }
            return;
        }
        Play(tune, fade);
    }

    /// <summary>Whether <paramref name="tune"/> plays over and over: a menu tune does, the intro and a match's tracks once.</summary>
    private bool Loops(Tune tune) => _wanted is { Phase: Phase.Menu } && tune != Tune.Original(SoundtrackChoice.Intro);

    /// <summary>How far below the music's volume <paramref name="tune"/> plays: a remake track over the menus by <see cref="MenuDb"/>.</summary>
    private float Gain(Tune tune) => tune.IsRemake && _wanted is { Phase: Phase.Menu } ? MenuDb : 0f;

    /// <summary>The player's level for the tune playing: the music's volume with its <see cref="Gain"/>.</summary>
    private float Level => Mathf.LinearToDb(Volume) + _gainDb;

    private void Play(Tune? tune, float fade)
    {
        _playing = tune;
        // Once a tune has played over the menus the intro is over: a soundtrack chosen later starts on its menu tune.
        if (tune is not null && _wanted is { Phase: Phase.Menu })
            _introPlayed = true;
        if (_player.Playing)
            FadeOut(tune is { } next ? () => Start(next) : null, fade);
        else if (tune is { } next)
            Start(next);
    }

    /// <summary>Fades the tune playing out over <paramref name="seconds"/>, then stops it and does <paramref name="then"/>.</summary>
    private void FadeOut(Action? then, float seconds)
    {
        _fade?.Kill();
        _fade = CreateTween();
        _fade.TweenProperty(_player, "volume_db", -40f, seconds);
        _fade.TweenCallback(Callable.From(() =>
        {
            _fade = null;
            _player.Stop();
            _player.VolumeDb = Level;
            then?.Invoke();
        }));
    }

    private void Start(Tune tune)
    {
        _fade?.Kill();
        _fade = null;
        _player.Stop();
        var loop = Loops(tune);
        _gainDb = Gain(tune);
        _player.VolumeDb = Level;
        string? file;
        AudioStream? stream;
        if (tune.Remake is { } name)
        {
            file = RemakeFile(name);
            stream = ResourceLoader.Load<AudioStreamOggVorbis>(file) is { } ogg ? Looped(ogg, loop) : null;
        }
        else
        {
            file = _tracks.GetValueOrDefault(tune.Track);
            stream = file is not null && AudioStreamMP3.LoadFromFile(file) is { } mp3 ? Looped(mp3, loop) : null;
        }
        if (stream is null)
        {
            // A tune that will not load is left out from now on, and another is chosen in its place.
            if (tune.Remake is { } missing)
            {
                _remakes.Remove(missing);
                _moods = TrackMoods.Read(_moodsText, _remakes);
            }
            else
                _tracks.Remove(tune.Track);
            _playing = null;
            Apply(FadeSeconds);
            return;
        }
        _player.Stream = stream;
        _player.Play();
        if (System.Environment.GetEnvironmentVariable("MACHINES_AUDIO_TRACE") == "1")
            GD.Print($"music {System.IO.Path.GetFileName(file)} ({Mode.ToString().ToLowerInvariant()}" +
                $"{(_wanted is { } wanted ? $", {(wanted.Phase == Phase.Match ? MoodOf(wanted).ToString() : wanted.Phase.ToString()).ToLowerInvariant()}" : "")}" +
                $"{(loop ? ", looped" : "")}{(_gainDb != 0 ? $", {_gainDb:0} dB" : "")})");
    }

    private static AudioStream Looped(AudioStream stream, bool loop)
    {
        switch (stream)
        {
            case AudioStreamOggVorbis ogg: ogg.Loop = loop; break;
            case AudioStreamMP3 mp3: mp3.Loop = loop; break;
        }
        return stream;
    }

    private void OnFinished()
    {
        // The intro gives way to the menu tune; a match's track to the next at random.
        if (_wanted is { Phase: Phase.Menu })
            Apply(FadeSeconds);
        else if (_wanted is { Phase: Phase.Match } wanted && _playing is { } track)
            Play(SoundtrackChoice.Next(Mode, MoodOf(wanted), track, _tracks.Keys, _moods, Random), FadeSeconds);
    }

    private static Variant Setting(string key, Variant fallback)
    {
        var config = new ConfigFile();
        return config.Load(Settings) == Error.Ok ? config.GetValue("audio", key, fallback) : fallback;
    }

    private static void Store(string key, Variant value)
    {
        var config = new ConfigFile();
        config.Load(Settings);
        config.SetValue("audio", key, value);
        config.Save(Settings);
    }
}
