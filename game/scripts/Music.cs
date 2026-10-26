using System.Text.RegularExpressions;
using Godot;

namespace Machines.Game;

/// <summary>
/// The original soundtrack, played as the original played its CD (MachGuiStartupScreens::updateCdAudio): the menu
/// tune over the menus, the scenario's own track (its <c>MUSIC</c> in sceninfo.dat) when a match begins and then
/// the in-game tracks at random, and a short victory or defeat tune at the end. The tracks are the MP3s of the
/// soundtrack the player owns, numbered as its release numbers them ("Track 01 - Menu.mp3" ...), which are the
/// CD's track numbers less one. The folder is picked in the menu and kept in <c>user://settings.cfg</c>;
/// without it the game has no music.
/// </summary>
public partial class Music : Node
{
    // Track numbers (startup.hpp's Music values plus one); the first is the intro film's.
    private const int Intro = 0, MenuTune = 1, VictoryTune = 2, DefeatTune = 3, FirstInGame = 4, DefaultInGame = 9;

    private const string Settings = "user://settings.cfg";
    private const float FadeSeconds = 0.8f;

    private static Music? _instance;
    private static bool _introPlayed;

    private readonly Dictionary<int, string> _tracks = [];
    private readonly RandomNumberGenerator _random = new();
    private AudioStreamPlayer _player = null!;
    private (int Track, bool Loop, bool Shuffle)? _wanted;
    private int? _playing;
    private Tween? _fade;

    /// <summary>The menus: the intro tune the first time, then the menu tune over and over.</summary>
    public static void Menu(Node from) => Want(from, (_introPlayed ? MenuTune : Intro, _introPlayed, false));

    /// <summary>A match on a scenario whose catalogue entry asks for <paramref name="scenarioMusic"/> (sceninfo's MUSIC).</summary>
    public static void Match(Node from, int? scenarioMusic) => Want(from, (scenarioMusic is { } m ? m - 1 : DefaultInGame, false, true));

    /// <summary>The match is over: the victory or defeat tune, once.</summary>
    public static void End(Node from, bool won) => Want(from, (won ? VictoryTune : DefeatTune, false, false));

    /// <summary>The folder holding the soundtrack's MP3s, or null.</summary>
    public static string? Folder
    {
        get => (string)Setting("music_folder", "") is { Length: > 0 } folder ? folder : null;
        set
        {
            Store("music_folder", value ?? "");
            if (_instance is not null && IsInstanceValid(_instance))
            {
                _instance.FindTracks();
                _instance._playing = null;
                _instance.Apply();
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
                _instance._player.VolumeDb = Mathf.LinearToDb(value);
        }
    }

    /// <summary>How many tracks the folder holds (0 without one).</summary>
    public static int TrackCount(string? folder) => Tracks(folder).Count;

    private static void Want(Node from, (int Track, bool Loop, bool Shuffle) tune)
    {
        if (_instance is null || !IsInstanceValid(_instance))
        {
            // The player lives at the root, so it plays on when the menu and the match replace each other.
            _instance = new Music { Name = "Music" };
            _instance.FindTracks();
            from.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        }
        _instance._wanted = tune;
        if (_instance.IsInsideTree())
            _instance.Apply();
    }

    public override void _Ready()
    {
        _player = new AudioStreamPlayer { Name = "Player", VolumeDb = Mathf.LinearToDb(Volume) };
        _player.Finished += OnFinished;
        AddChild(_player);
        Apply();
    }

    private void FindTracks()
    {
        _tracks.Clear();
        foreach (var (number, path) in Tracks(Folder))
            _tracks[number] = path;
    }

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

    /// <summary>Starts the wanted tune unless it is already playing, fading out the one before.</summary>
    private void Apply()
    {
        if (_wanted is not { } tune || _player is null || tune.Track == _playing)
            return;
        _playing = tune.Track;
        if (tune.Track == Intro)
            _introPlayed = true;
        _fade?.Kill();
        if (_player.Playing)
        {
            _fade = CreateTween();
            _fade.TweenProperty(_player, "volume_db", -40f, FadeSeconds);
            _fade.TweenCallback(Callable.From(() =>
            {
                _fade = null;
                Start(tune.Track, tune.Loop);
            }));
        }
        else
        {
            Start(tune.Track, tune.Loop);
        }
    }

    private void Start(int track, bool loop)
    {
        _player.Stop();
        _player.VolumeDb = Mathf.LinearToDb(Volume);
        if (!_tracks.TryGetValue(track, out var path) || AudioStreamMP3.LoadFromFile(path) is not { } stream)
        {
            // A missing tune: the intro gives way to the menu, a match's first track to the others.
            if (track == Intro)
                Menu(this);
            else if (_wanted is { Shuffle: true })
                Shuffle(track);
            return;
        }
        stream.Loop = loop;
        _player.Stream = stream;
        _player.Play();
        if (System.Environment.GetEnvironmentVariable("MACHINES_AUDIO_TRACE") == "1")
            GD.Print($"music {System.IO.Path.GetFileName(path)}");
    }

    private void OnFinished()
    {
        if (_playing == Intro)
            Menu(this);
        else if (_wanted is { Shuffle: true } && _playing is { } track)
            Shuffle(track);
    }

    /// <summary>The in-game tracks at random after one another, as the CD's random play did.</summary>
    private void Shuffle(int after)
    {
        var choices = _tracks.Keys.Where(t => t >= FirstInGame && t != after).ToList();
        if (choices.Count == 0)
            return;
        var next = choices[_random.RandiRange(0, choices.Count - 1)];
        _playing = next;
        Start(next, false);
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
