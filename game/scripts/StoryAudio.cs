using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// The sound of a story chapter (plan 5.4): its own buses, the lines (<see cref="StoryVoice"/>), the rooms' ambience and
/// the story's sound effects (<see cref="StorySounds"/>) and the music by threat (<see cref="StoryMusic"/>). It lives only in
/// a story match (Game.SetUpStoryAudio) and touches nothing of the sim: what it plays is the view's. The director reaches
/// it through the static calls (<see cref="Say"/>, <see cref="Play"/>, <see cref="SetAmbience"/>, <see cref="SetThreat"/>
/// ...), which do nothing outside a story, so its events can be routed here as they come.
/// <para>Buses, made when the chapter starts and taken away when it ends, right after Master so that each sends to one on
/// its left: <c>Music</c> (ducked under speech), <c>World</c> (the room's reverb, a low-pass when hiding, damaged or the
/// update intrudes, ducked under speech; the game's <c>Effects</c> bus sends into it meanwhile), <c>Voice</c> (speech;
/// the ducking's sidechain), <c>Announcer</c> (the PA: band-pass and hall, into Voice) and <c>Radio</c> (band-pass and
/// lo-fi, into Voice).</para>
/// <para>MACHINES_AUDIO_TRACE=1 prints each story sound as <c>audio story-&lt;kind&gt; &lt;id&gt; ... @&lt;seconds&gt;</c>;
/// MACHINES_STORY_AUDIO_TEST=1 (with MACHINES_STORY=prolog, and MACHINES_MAP=prolog so the menus are skipped) plays a
/// short sequence once the chapter is shown (a PA line, a radio line, sounds, ambience,
/// threat, hush, muffle, a cut), writes the trace to MACHINES_STORY_AUDIO_TEST_LOG (default user://story-audio-test.log)
/// and quits; with <c>--write-movie</c> the movie has its sound.</para>
/// </summary>
public partial class StoryAudio : Node
{
    public const string MusicBus = "Music", WorldBus = "World", VoiceBus = "Voice", AnnouncerBus = "Announcer", RadioBus = "Radio";

    // Left to right after Master; each sends to one on its left, and the ducking's sidechain (Voice) is mixed before the
    // buses it ducks (Godot mixes the buses from the last to the first).
    private static readonly string[] Buses = [MusicBus, WorldBus, VoiceBus, AnnouncerBus, RadioBus];

    public const string SfxFolder = "res://assets/story/sfx", VoiceFolder = "res://assets/story/voice", MusicFolder = "res://assets/story/music";

    /// <summary>The chapter's sound, while a story is played; null otherwise.</summary>
    public static StoryAudio? Current { get; private set; }

    /// <summary>A line starts (its voice, or only its subtitle when it has none): for the subtitles. Unsubscribe when done.</summary>
    public static event Action<StoryLineCue>? LineStarted;

    /// <summary>A line has ended, cut off or not.</summary>
    public static event Action<StoryLineCue>? LineEnded;

    /// <summary>"Schreckmomente mildern": the stingers play much quieter. Follows the options' "Soften scares".</summary>
    public static bool MildScares => GameOptions.SoftScares;

    private static readonly Dictionary<string, AudioStream?> Streams = [];
    private static StorySfxManifest? _sfx;
    private static StoryVoiceManifest? _voices;

    // What the view hands in (Game.SetUpStoryAudio): where the Thresher is, how suspected the trio is, the section, the
    // Locator's health (0 to 1), whether the chapter is shown yet and whether it is over.
    public Deck? Deck { get; init; }
    public Func<Vector3, float?> ThresherDistance { get; init; } = _ => null;
    public Func<float> Suspicion { get; init; } = () => 0;
    public Func<int> Section { get; init; } = () => 1;
    public Func<float> LocatorHealth { get; init; } = () => 1;
    public Func<bool> Shown { get; init; } = () => true;
    public Func<bool> Over { get; init; } = () => false;

    private StoryVoice _voice = null!;
    private StorySounds _sounds = null!;
    private StoryMusic _music = null!;
    private double _clock;
    private double _nextLook;
    private readonly HashSet<string> _muffled = new(StringComparer.Ordinal);
    private Tween? _muffle, _hush;
    private bool _ended;
    private StoryAudioTest? _test;

    /// <summary>Seconds since the chapter's sound began (the trace's clock).</summary>
    public double Clock => _clock;

    public StoryVoice Voice => _voice;
    public StorySounds Sounds => _sounds;
    public StoryMusic MusicCues => _music;

    public override void _Ready()
    {
        Name = "StoryAudio";
        Current = this;
        BuildBuses();
        _voice = new StoryVoice(this) { Name = "StoryVoice" };
        _sounds = new StorySounds(this) { Name = "StorySounds" };
        _music = new StoryMusic(this) { Name = "StoryMusic" };
        AddChild(_voice);
        AddChild(_sounds);
        AddChild(_music);
        if (System.Environment.GetEnvironmentVariable("MACHINES_STORY_AUDIO_TEST") is { Length: > 0 } test && test != "0")
        {
            _test = new StoryAudioTest(this);
            AddChild(_test);
        }
    }

    public override void _ExitTree()
    {
        // A chapter started again may have made its own buses already: they stay.
        if (Current != this)
            return;
        Current = null;
        RemoveBuses();
        // The chapter's streams go with it (the next one preloads its own).
        Streams.Clear();
    }

    public override void _Process(double delta)
    {
        _clock += delta;
        if (_ended)
            return;
        if (Over())
        {
            // The chapter has ended: what speaks is cut, the music falls silent, the rooms go on.
            _ended = true;
            _voice.Cut(clear: true, crackle: false);
            _music.Cue("silence");
            return;
        }
        if (_clock < _nextLook)
            return;
        _nextLook = _clock + 0.25;
        var listener = Listener;
        _music.Look(ThresherDistance(listener), Suspicion(), Section());
        _sounds.Look(listener, Section());
        SetMuffledHere("damaged", LocatorHealth() < 0.25f);
    }

    /// <summary>Where the story is heard from: the camera.</summary>
    public Vector3 Listener => GetViewport()?.GetCamera3D() is { } camera ? camera.GlobalPosition : Vector3.Zero;

    // ---- the director's calls -------------------------------------------------------------------

    /// <summary>Speaks a line of the line table (tools/voice/lines.json) by its id, in its turn; without its voice file only its subtitle comes.</summary>
    public static void Say(string lineId) => Current?._voice.Say(lineId);

    /// <summary>Plays a story sound effect by its id (game/assets/story/sfx), at a point of the world or, without one, not placed.</summary>
    public static void Play(string sfxId, Vector3? at = null) => Current?._sounds.Play(sfxId, at);

    /// <summary>The ambience of a room of the deck (R4, V1b ...) or a section ("section:3"); null follows the room the listener is in.</summary>
    public static void SetAmbience(string? zone) => Current?._sounds.SetAmbience(zone);

    /// <summary>A loop over the room's ambience (the alarm's klaxon, fire), on or off.</summary>
    public static void SetLayer(string sfxId, bool on, float db = -6) => Current?._sounds.SetLayer(sfxId, on, db);

    /// <summary>The music's threat level, 0 calm to 2 hunted; null leaves it to the Thresher's distance and the suspicion.</summary>
    public static void SetThreat(int? level) => Current?._music.SetThreat(level);

    /// <summary>The music's cue (wartung, jagd, gegenwehr, absturz, silence); null follows the section.</summary>
    public static void Cue(string? cue) => Current?._music.Cue(cue);

    /// <summary>A stinger (sting_dread, sting_found, sting_alarm) over the music; quiet with "Schreckmomente mildern".</summary>
    public static void Sting(string sfxId) => Current?._music.Sting(sfxId);

    /// <summary>The silence before a scare: the world and the music drop by 30 dB for so many seconds.</summary>
    public static void Hush(float seconds) => Current?.HushHere(seconds);

    /// <summary>The world heard as through a wall (hiding, damaged, the update intruding): on while any reason holds.</summary>
    public static void SetMuffled(string reason, bool on) => Current?.SetMuffledHere(reason, on);

    /// <summary>What is speaking is cut off with a crackle (the PA dying); with <paramref name="clear"/> nothing waiting is said either.</summary>
    public static void CutVoice(bool clear = true) => Current?._voice.Cut(clear, crackle: true);

    // ---- buses --------------------------------------------------------------------------------

    private AudioEffectAmplify _worldHush = null!, _musicHush = null!;
    private AudioEffectLowPassFilter _worldMuffle = null!;
    private AudioEffectReverb _worldReverb = null!;

    private void BuildBuses()
    {
        _ = GameAudio.EffectsBus;
        RemoveBuses();
        for (var i = Buses.Length - 1; i >= 0; i--)
        {
            AudioServer.AddBus(1);
            AudioServer.SetBusName(1, Buses[i]);
        }
        int Index(string name) => AudioServer.GetBusIndex(name);
        AudioServer.SetBusSend(Index(MusicBus), "Master");
        AudioServer.SetBusSend(Index(WorldBus), "Master");
        AudioServer.SetBusSend(Index(VoiceBus), "Master");
        AudioServer.SetBusSend(Index(AnnouncerBus), VoiceBus);
        AudioServer.SetBusSend(Index(RadioBus), VoiceBus);
        AudioServer.SetBusSend(Index(GameAudio.EffectsBus), WorldBus);
        AudioServer.SetBusVolumeDb(Index(MusicBus), Mathf.LinearToDb(Mathf.Max(0.001f, global::Machines.Game.Music.Volume)));
        AudioServer.SetBusVolumeDb(Index(VoiceBus), Mathf.LinearToDb(Mathf.Max(0.001f, GameAudio.SoundVolume)));

        // Speech pushes the world and the music down by itself.
        AudioEffectCompressor Duck() => new() { Sidechain = VoiceBus, Threshold = -28, Ratio = 4, AttackUs = 20000, ReleaseMs = 450 };

        var world = Index(WorldBus);
        _worldReverb = new AudioEffectReverb { RoomSize = StorySoundscape.Room.RoomSize, Damping = StorySoundscape.Room.Damping, Wet = StorySoundscape.Room.Wet, Dry = 1, PredelayMsec = StorySoundscape.Room.PredelayMs, Spread = 0.8f, Hipass = 0.1f };
        _worldMuffle = new AudioEffectLowPassFilter { CutoffHz = Open };
        _worldHush = new AudioEffectAmplify { VolumeDb = 0 };
        AudioServer.AddBusEffect(world, _worldReverb);
        AudioServer.AddBusEffect(world, _worldMuffle);
        AudioServer.AddBusEffect(world, Duck());
        AudioServer.AddBusEffect(world, _worldHush);

        var music = Index(MusicBus);
        _musicHush = new AudioEffectAmplify { VolumeDb = 0 };
        AudioServer.AddBusEffect(music, Duck());
        AudioServer.AddBusEffect(music, _musicHush);

        // The PA: a loudspeaker's narrow band and the hall of the ship (the recordings carry a little hall already).
        var announcer = Index(AnnouncerBus);
        AudioServer.AddBusEffect(announcer, new AudioEffectHighPassFilter { CutoffHz = 280 });
        AudioServer.AddBusEffect(announcer, new AudioEffectLowPassFilter { CutoffHz = 4200 });
        AudioServer.AddBusEffect(announcer, new AudioEffectReverb { RoomSize = 0.78f, Damping = 0.35f, Wet = 0.14f, Dry = 1, PredelayMsec = 60, Spread = 1 });

        // The radio: a narrower band and a crushed signal.
        var radio = Index(RadioBus);
        AudioServer.AddBusEffect(radio, new AudioEffectHighPassFilter { CutoffHz = 450 });
        AudioServer.AddBusEffect(radio, new AudioEffectLowPassFilter { CutoffHz = 3000 });
        AudioServer.AddBusEffect(radio, new AudioEffectDistortion { Mode = AudioEffectDistortion.ModeEnum.Lofi, Drive = 0.25f, PreGain = 0, PostGain = -3, KeepHfHz = 16000 });
    }

    // The story's buses go when the chapter ends: the game's effects send to Master again, as in every other match.
    private static void RemoveBuses()
    {
        if (AudioServer.GetBusIndex(GameAudio.EffectsBus) is var effects and >= 0)
            AudioServer.SetBusSend(effects, "Master");
        foreach (var name in Buses.Reverse())
            if (AudioServer.GetBusIndex(name) is var index and > 0)
                AudioServer.RemoveBus(index);
    }

    /// <summary>The music bus's level follows the options' music volume (Music.Volume).</summary>
    public static void MusicVolumeChanged(float volume)
    {
        if (AudioServer.GetBusIndex(MusicBus) is var index and > 0)
            AudioServer.SetBusVolumeDb(index, Mathf.LinearToDb(Mathf.Max(0.001f, volume)));
    }

    /// <summary>The voice bus's level follows the options' sound volume (GameAudio.SoundVolume).</summary>
    public static void SoundVolumeChanged(float volume)
    {
        if (AudioServer.GetBusIndex(VoiceBus) is var index and > 0)
            AudioServer.SetBusVolumeDb(index, Mathf.LinearToDb(Mathf.Max(0.001f, volume)));
    }

    /// <summary>The world's reverb becomes the room's, over a second.</summary>
    public void SetReverb(StoryReverb reverb)
    {
        var tween = CreateTween().SetParallel();
        tween.TweenProperty(_worldReverb, "room_size", reverb.RoomSize, 1);
        tween.TweenProperty(_worldReverb, "damping", reverb.Damping, 1);
        tween.TweenProperty(_worldReverb, "wet", reverb.Wet, 1);
        tween.TweenProperty(_worldReverb, "predelay_msec", reverb.PredelayMs, 1);
        Trace($"reverb {reverb.Name}");
    }

    private const float Open = 20000, Muffled = 900;

    private void SetMuffledHere(string reason, bool on)
    {
        if (!(on ? _muffled.Add(reason) : _muffled.Remove(reason)))
            return;
        var target = _muffled.Count > 0 ? Muffled : Open;
        _muffle?.Kill();
        _muffle = CreateTween();
        _muffle.TweenProperty(_worldMuffle, "cutoff_hz", target, on ? 0.35 : 0.8).SetTrans(Tween.TransitionType.Expo);
        Trace($"muffle {reason} {(on ? "on" : "off")}");
    }

    private void HushHere(float seconds)
    {
        _hush?.Kill();
        _hush = CreateTween();
        _hush.SetParallel();
        _hush.TweenProperty(_worldHush, "volume_db", -30f, 0.15);
        _hush.TweenProperty(_musicHush, "volume_db", -30f, 0.15);
        _hush.Chain().TweenInterval(Mathf.Max(0, seconds - 0.15f));
        _hush.Chain().TweenProperty(_worldHush, "volume_db", 0f, 0.05);
        _hush.TweenProperty(_musicHush, "volume_db", 0f, 0.05);
        Trace($"hush {seconds:0.0#}");
    }

    // ---- streams ------------------------------------------------------------------------------

    /// <summary>The story's sound effects as tools/sfx wrote them.</summary>
    public static StorySfxManifest Sfx => _sfx ??= Manifest($"{SfxFolder}/manifest.json", StorySfxManifest.Parse, StorySfxManifest.Empty);

    /// <summary>The voice files as tools/voice wrote them.</summary>
    public static StoryVoiceManifest VoiceFiles => _voices ??= Manifest($"{VoiceFolder}/manifest.json", StoryVoiceManifest.Parse, StoryVoiceManifest.Empty);

    private static T Manifest<T>(string path, Func<string, T> parse, T empty)
    {
        try
        {
            return Godot.FileAccess.FileExists(path) ? parse(Godot.FileAccess.GetFileAsString(path)) : empty;
        }
        catch (System.Text.Json.JsonException e)
        {
            GD.PushWarning($"{path}: {e.Message}");
            return empty;
        }
    }

    /// <summary>A story stream by its res:// path, loaded once; null when there is no such file.</summary>
    public static AudioStream? Stream(string path)
    {
        if (Streams.TryGetValue(path, out var cached))
            return cached;
        var start = HitchLog.Start;
        var stream = ResourceLoader.Exists(path) ? ResourceLoader.Load<AudioStream>(path) : null;
        Streams[path] = stream;
        HitchLog.Loaded($"story sound {path}", start);
        return stream;
    }

    /// <summary>Whether a story stream is there, without loading it.</summary>
    public static bool Exists(string path) => Streams.TryGetValue(path, out var cached) ? cached is not null : ResourceLoader.Exists(path);

    /// <summary>The res:// path of a story sound effect, or null.</summary>
    public static string? SfxPath(string id) => Sfx.File(id) is { } file ? $"{SfxFolder}/{file}" : null;

    /// <summary>
    /// Every stream a chapter may play, for <see cref="Warmup"/>: the sound effects, the voices in the player's language
    /// (and English, which stands in for a missing one) and the music files there are.
    /// </summary>
    public static IEnumerable<string> Files()
    {
        foreach (var id in Sfx.Ids)
            if (SfxPath(id) is { } path)
                yield return path;
        var langs = Lang.Code == "en" ? new[] { "en" } : [Lang.Code, "en"];
        foreach (var line in VoiceFiles.Lines)
            foreach (var lang in langs)
                if (VoiceFiles.Find(line, lang) is { } file && file.Lang == lang)
                    yield return $"{VoiceFolder}/{file.File}";
        foreach (var cue in StorySoundscape.Cues)
            foreach (var part in new[] { cue, cue + "_1", cue + "_2" })
                if (ResourceLoader.Exists($"{MusicFolder}/{part}.ogg"))
                    yield return $"{MusicFolder}/{part}.ogg";
    }

    /// <summary>Loads a story stream now (<see cref="Warmup"/>), so that it plays at once when its time comes.</summary>
    public static void Preload(string path) => Stream(path);

    // ---- trace ----------------------------------------------------------------------------------

    private static readonly bool Tracing = System.Environment.GetEnvironmentVariable("MACHINES_AUDIO_TRACE") == "1";

    /// <summary>For automated checks: MACHINES_AUDIO_TRACE=1 prints each story sound as <c>audio story-...</c> with the chapter's clock.</summary>
    public void Trace(FormattableString what)
    {
        // Numbers with a point whatever the system's language, so the checks can read them.
        var line = FormattableString.Invariant($"audio story-{FormattableString.Invariant(what)} @{_clock:0.00}");
        if (Tracing || _test is not null)
            GD.Print(line);
        _test?.Note(line);
    }

    internal static void Raise(StoryLineCue cue, bool started)
    {
        try
        {
            (started ? LineStarted : LineEnded)?.Invoke(cue);
        }
        catch (Exception e)
        {
            GD.PushError($"story line {cue.Id}: {e}");
        }
    }
}

/// <summary>A line as the subtitles see it: its id, where it is heard, its length, whether a voice speaks it and in which language.</summary>
public sealed record StoryLineCue(string Id, StoryVoiceChannel Channel, double Seconds, bool Voiced, string Lang);
