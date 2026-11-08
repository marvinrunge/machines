using Godot;
using Machines.Import.Audio;
using Machines.Sim.Presentation;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The original sounds, played straight from the player's own copy of the game (its <c>sounds</c> folder) as
/// the definitions in <c>data/original/sounds</c> name them. Which sound goes with which weapon, explosion and
/// machine follows machphys (bolter.cpp, pulsewep.cpp, mulaunch.cpp, machine.cpp, objexplo.cpp, ...). Without a
/// game copy the game is silent.
/// </summary>
public partial class GameAudio : Node
{
    /// <summary>
    /// One-shot sounds playing at once, as a sound card's voices. When all are busy a new sound takes the voice of
    /// the least important one playing if its definition's priority is higher (explosions above weapons above
    /// engines), and is dropped otherwise.
    /// </summary>
    private const int Voices = 32;

    /// <summary>
    /// Heard as in a film: every sound from where it happens whatever the options say, and the engines, which in a
    /// match sit under the battle and fade within a few metres, heard as loud as weapons and three times as far.
    /// </summary>
    public static bool Film { get; set; }

    private readonly Dictionary<string, SoundDefinition> _sounds = [];
    private readonly Dictionary<string, VoiceMailDefinition> _voiceMail = [];
    private readonly Dictionary<string, AudioStreamWav?> _streams = [];
    private readonly List<AudioStreamPlayer3D> _players = [];
    private readonly Dictionary<AudioStreamPlayer3D, int> _priority = [];
    private readonly Dictionary<string, double> _lastPlayed = [];
    private readonly RandomNumberGenerator _random = new();
    private AudioStreamPlayer _voice = null!;
    private string? _gameFolder;

    /// <summary>
    /// The bus the game's own sounds play on (not the music), so that their volume is set apart; made on first
    /// use at the volume kept in <c>user://settings.cfg</c>.
    /// </summary>
    public static StringName EffectsBus
    {
        get
        {
            const string name = "Effects";
            if (AudioServer.GetBusIndex(name) < 0)
            {
                AudioServer.AddBus();
                var index = AudioServer.BusCount - 1;
                AudioServer.SetBusName(index, name);
                AudioServer.SetBusSend(index, "Master");
                AudioServer.SetBusVolumeDb(index, Mathf.LinearToDb(Mathf.Max(0.001f, SoundVolume)));
            }
            return name;
        }
    }

    /// <summary>The volume of the game's own sounds, 0 to 1 (the options' sound volume).</summary>
    public static float SoundVolume
    {
        get
        {
            var config = new ConfigFile();
            return config.Load("user://settings.cfg") == Error.Ok ? (float)(double)config.GetValue("audio", "sound_volume", 1.0) : 1;
        }
        set
        {
            var config = new ConfigFile();
            config.Load("user://settings.cfg");
            config.SetValue("audio", "sound_volume", (double)value);
            config.Save("user://settings.cfg");
            if (AudioServer.GetBusIndex("Effects") is var index and >= 0)
                AudioServer.SetBusVolumeDb(index, Mathf.LinearToDb(Mathf.Max(0.001f, value)));
        }
    }

    public static GameAudio Create(string? gameFolder)
    {
        var audio = new GameAudio { Name = "Audio", _gameFolder = gameFolder };
        var definitions = System.IO.Path.Combine(Paths.OriginalData, "sounds", "snddef64.dat");
        var voiceMail = System.IO.Path.Combine(Paths.OriginalData, "sounds", "vemail", "vemail.dat");
        if (gameFolder is not null && File.Exists(definitions))
        {
            foreach (var sound in SoundDefinitionParser.Load(definitions))
                audio._sounds[sound.Id] = sound;
            if (File.Exists(voiceMail))
                foreach (var mail in VoiceMailParser.Load(voiceMail))
                    audio._voiceMail[mail.Id] = mail;
        }
        audio._voice = new AudioStreamPlayer { Name = "VoiceMail", Bus = EffectsBus };
        audio._voice.Finished += audio.SpeakNext;
        audio.AddChild(audio._voice);
        return audio;
    }

    public bool Enabled => _sounds.Count > 0;

    /// <summary>
    /// The world falls silent (in the dark of the fog of war, as W4dSoundManager::setPlayingSoundsStatus did);
    /// every world sound is positional, so the view's 3D listener is switched off. Reports still speak.
    /// </summary>
    public bool Muted
    {
        set
        {
            if (IsInsideTree() && GetViewport().AudioListenerEnable3D == value)
                GetViewport().AudioListenerEnable3D = !value;
        }
    }

    // ---- one-shot sounds ------------------------------------------------------------------------

    /// <summary>Plays a sound effect once at a point of the world.</summary>
    public void Play(string? id, Vector3 at)
    {
        if (id is null || !_sounds.TryGetValue(id, out var sound) || Stream(sound.File) is not { } stream)
            return;
        // The same sound started within a few frames is heard once: a burst of 20 grunts firing is one bang.
        var now = FrameClock.Now;
        if (_lastPlayed.TryGetValue(id, out var last) && now - last < 0.06)
            return;
        _lastPlayed[id] = now;

        var player = _players.FirstOrDefault(p => !p.Playing);
        if (player is null && _players.Count >= Voices)
        {
            // Every voice busy: the least important sound playing gives way to a more important one.
            var least = _players.MinBy(p => _priority.GetValueOrDefault(p))!;
            if (_priority.GetValueOrDefault(least) >= sound.Priority)
                return;
            least.Stop();
            player = least;
        }
        if (player is null)
        {
            player = new AudioStreamPlayer3D { Bus = EffectsBus };
            AddChild(player);
            _players.Add(player);
        }
        _priority[player] = sound.Priority;
        Configure(player, sound, stream);
        player.GlobalPosition = at;
        player.Play();
        Trace(id);
    }

    /// <summary>For automated checks: MACHINES_AUDIO_TRACE=1 prints every sound and report played.</summary>
    private static void Trace(string id)
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_AUDIO_TRACE") == "1")
            GD.Print($"audio {id}");
    }

    /// <summary>
    /// A looping player for a machine's engine: its idle sound, or its movement sound while it moves
    /// (MachPhysMachine::getLocomotionSounds). Null when the game copy or the sounds are missing.
    /// </summary>
    public EngineSound? EngineFor(Machine machine)
    {
        var (move, idle) = SoundTable.LocomotionSounds(machine.Spec.Type, machine.Spec.SubType, machine.Stats.HardwareLevel);
        var flies = move == "SID_FLY_BLAST";
        var idleSound = idle is not null && _sounds.TryGetValue(idle, out var i) ? i : null;
        var moveSound = !flies && move is not null && _sounds.TryGetValue(move, out var m) ? m : idleSound;
        if (idleSound is null || moveSound is null)
            return null;
        var idleStream = Looping(idleSound.File);
        var moveStream = Looping(moveSound.File);
        if (idleStream is null || moveStream is null)
            return null;
        var player = new AudioStreamPlayer3D { Name = "Engine", Bus = EffectsBus };
        return new EngineSound(player, idleSound, idleStream, moveSound, moveStream)
        {
            OnThrust = flies ? MachineThrust(SoundTable.GliderSounds(machine.Spec.Type, machine.Stats.HardwareLevel)) : null,
        };
    }

    private Action<bool, Vector3> MachineThrust((string Blast, string Brake) sounds) =>
        (moving, at) => Play(moving ? sounds.Blast : sounds.Brake, at);

    public sealed class EngineSound(AudioStreamPlayer3D player, SoundDefinition idle, AudioStreamWav idleStream, SoundDefinition move, AudioStreamWav moveStream)
    {
        // A stop this short between the legs of a route is no landing.
        private const double ThrustGap = 1;

        private bool? _moving;
        private double _changed;

        public AudioStreamPlayer3D Player { get; } = player;

        /// <summary>A flying machine's thrust as it sets off (true) or brakes (false).</summary>
        public Action<bool, Vector3>? OnThrust { get; init; }

        public void Update(bool moving)
        {
            if (_moving == moving && Player.Playing)
                return;
            if (OnThrust is not null && _moving is not null && _moving != moving && FrameClock.Now - _changed > ThrustGap)
            {
                _changed = FrameClock.Now;
                OnThrust(moving, Player.GlobalPosition);
            }
            _moving = moving;
            Configure(Player, moving ? move : idle, moving ? moveStream : idleStream);
            // Engines sit under the battle: quieter than weapons.
            if (Film)
            {
                Player.VolumeDb += 6;
                Player.MaxDb = 6;
                Player.UnitSize *= 3;
                Player.MaxDistance *= 2;
                Player.AttenuationFilterDb = 0;
            }
            else
                Player.VolumeDb -= 6;
            Player.Play();
            Trace(moving ? move.Id : idle.Id);
        }
    }

    /// <summary>
    /// A sound that runs on where its node stands, as W4dSoundManager's sounds on an entity did: a building's hum, a
    /// constructor at work, a fire. Add <see cref="LoopSound.Player"/> to the node before playing.
    /// </summary>
    public LoopSound Loop(string name = "Loop") => new(this, new AudioStreamPlayer3D { Name = name, Bus = EffectsBus });

    public sealed class LoopSound(GameAudio audio, AudioStreamPlayer3D player)
    {
        private string? _id;
        private double _until;

        public AudioStreamPlayer3D Player { get; } = player;

        /// <summary>Plays this sound over and over, in place of the last; null stops it.</summary>
        public void Play(string? id)
        {
            if (id == _id)
                return;
            _id = id;
            Player.Stop();
            if (id is null || !audio._sounds.TryGetValue(id, out var sound) || audio.Looping(sound.File) is not { } stream)
                return;
            Configure(Player, sound, stream);
            Player.Play();
            Trace(id);
        }

        /// <summary>As <see cref="Play"/> for so many seconds (W4dSoundManager::playForDuration); played again it lasts longer.</summary>
        public void PlayFor(string id, float seconds)
        {
            Play(id);
            _until = FrameClock.Now + seconds;
            Player.GetTree().CreateTimer(seconds).Timeout += () =>
            {
                if (IsInstanceValid(Player) && FrameClock.Now >= _until - 0.05)
                    Play(null);
            };
        }
    }

    private static void Configure(AudioStreamPlayer3D player, SoundDefinition sound, AudioStream stream)
    {
        player.Stream = stream;
        player.VolumeDb = Mathf.LinearToDb(Mathf.Clamp(sound.MaxVolumePercent / 100f, 0.01f, 1f));
        // Full volume up to the definition's near distance, fading with distance, silent beyond the far one.
        player.UnitSize = Mathf.Max(4, sound.NearDistance);
        player.MaxDistance = Mathf.Max(sound.FarDistance, 50);
        player.AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance;
        // Without the options' 3D Sound only the distance tells where a sound is, as the original's 2D sounds did.
        player.PanningStrength = GameOptions.Sound3d || Film ? 1 : 0;
    }

    // ---- voice mail -----------------------------------------------------------------------------

    /// <summary>
    /// Speaks a report such as <c>VID_GRUNT_SELECTED</c>, picking one of its numbered variants. A report that
    /// arrives while another is playing is dropped, as the original's voice-mail queue did for minor ones; with
    /// <paramref name="queue"/> it waits its turn instead, as the mission scripts' mails did (MachLogVoiceMailManager
    /// keeps every mail of the full-function kind). <paramref name="speaker"/> is the machine reporting, if one is.
    /// </summary>
    public void Say(string id, bool queue = false, int speaker = 0)
    {
        if (_voice.Playing && !queue)
            return;
        var variants = _voiceMail.Keys.Where(k => k == id || k.Length == id.Length + 1 && k.StartsWith(id, StringComparison.Ordinal) && char.IsDigit(k[^1])).ToList();
        if (variants.Count == 0)
            return;
        var mail = _voiceMail[variants[_random.RandiRange(0, variants.Count - 1)]];
        if (_voice.Playing)
        {
            _waiting.Enqueue((mail.File, speaker));
            return;
        }
        _speaker = speaker;
        Speak(mail.File, mail.Id);
    }

    /// <summary>
    /// A machine died: what it still had to say is dropped, and if it was speaking a burst of static cuts it off
    /// (MachLogVoiceMailManager::postDeathMail).
    /// </summary>
    public void SpeakerLost(int actor)
    {
        if (_waiting.Any(w => w.Speaker == actor))
            _waiting = new(_waiting.Where(w => w.Speaker != actor));
        if (actor != _speaker || !_voice.Playing)
            return;
        _speaker = 0;
        _voice.Stop();
        var noise = $"VID_INTERFERENCE_{Math.Min(5, _random.RandiRange(0, 6))}";
        if (_voiceMail.TryGetValue(noise, out var mail))
            Speak(mail.File, mail.Id);
        else
            SpeakNext();
    }

    /// <summary>Speaks a recording of the game copy, e.g. a briefing's <c>Sounds/menus/tut01ob1.wav</c>, in place of what is speaking.</summary>
    public void SayFile(string file)
    {
        _waiting.Clear();
        Speak(file, file);
    }

    /// <summary>Stops what is speaking and what waits to be spoken.</summary>
    public void Hush()
    {
        _waiting.Clear();
        if (IsInstanceValid(_voice))
            _voice.Stop();
    }

    private Queue<(string File, int Speaker)> _waiting = [];
    private int _speaker;

    private void Speak(string file, string id)
    {
        if (!IsInsideTree() || Stream(file) is not { } stream)
            return;
        _voice.Stream = stream;
        _voice.Play();
        Trace(id);
    }

    /// <summary>The next mail waiting, once the voice is free.</summary>
    private void SpeakNext()
    {
        _speaker = 0;
        if (_waiting.TryDequeue(out var next))
        {
            _speaker = next.Speaker;
            Speak(next.File, next.File);
        }
    }

    /// <summary>A sound of the interface (MachGuiSoundManager), e.g. <c>gui/sounds/attackon.wav</c>: not placed, never muted.</summary>
    public void PlayGui(string file)
    {
        if (!IsInsideTree() || Stream(file) is not { } stream)
            return;
        // Several interface sounds may sound together (a panel click and the order it gives); the same one asked
        // for twice at once is heard once.
        var now = FrameClock.Now;
        if (_lastPlayed.TryGetValue(file, out var last) && now - last < 0.06)
            return;
        _lastPlayed[file] = now;
        var player = _gui.FirstOrDefault(p => !p.Playing);
        if (player is null)
        {
            if (_gui.Count >= GuiVoices)
                return;
            player = new AudioStreamPlayer { Name = $"Gui{_gui.Count}", Bus = EffectsBus };
            AddChild(player);
            _gui.Add(player);
        }
        player.Stream = stream;
        player.Play();
        Trace(file);
    }

    /// <summary>A sound effect by its id, not placed, for a screen with no world to hear it in (the hangar's guns on their turntable).</summary>
    public void PlayFlat(string? id)
    {
        if (id is not null && _sounds.TryGetValue(id, out var sound))
            PlayGui(sound.File);
    }

    private const int GuiVoices = 4;
    private readonly List<AudioStreamPlayer> _gui = [];

    // ---- loading ------------------------------------------------------------------------------

    private AudioStreamWav? Stream(string file)
    {
        if (_streams.TryGetValue(file, out var cached))
            return cached;
        AudioStreamWav? stream = null;
        if (_gameFolder is not null)
        {
            var path = System.IO.Path.Combine(_gameFolder, file.Replace('\\', '/'));
            // The game ships Waypoint.wav beside waypoint names in the code: match regardless of case.
            if (!File.Exists(path) && System.IO.Path.GetDirectoryName(path) is { } folder && Directory.Exists(folder))
                path = Directory.EnumerateFiles(folder).FirstOrDefault(f => System.IO.Path.GetFileName(f).Equals(System.IO.Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)) ?? path;
            if (File.Exists(path))
                stream = DecodeWav(File.ReadAllBytes(path));
            if (stream is null)
                GD.PushWarning($"sound not loaded: {path}");
        }
        _streams[file] = stream;
        return stream;
    }

    /// <summary>
    /// Reads an uncompressed PCM WAV (the game's are 8- or 16-bit, mostly 11 kHz mono). Only the <c>fmt </c> and
    /// <c>data</c> chunks are read: some files carry text chunks that Godot's own loader complains about.
    /// </summary>
    private static AudioStreamWav? DecodeWav(byte[] bytes)
    {
        if (bytes.Length < 12 || System.Text.Encoding.ASCII.GetString(bytes, 0, 4) != "RIFF" || System.Text.Encoding.ASCII.GetString(bytes, 8, 4) != "WAVE")
            return null;
        int channels = 0, rate = 0, bits = 0;
        byte[]? data = null;
        for (var at = 12; at + 8 <= bytes.Length;)
        {
            var id = System.Text.Encoding.ASCII.GetString(bytes, at, 4);
            var size = BitConverter.ToInt32(bytes, at + 4);
            var body = at + 8;
            size = Math.Clamp(size, 0, bytes.Length - body);
            if (id == "fmt " && size >= 16)
            {
                if (BitConverter.ToUInt16(bytes, body) != 1)
                    return null; // not PCM
                channels = BitConverter.ToUInt16(bytes, body + 2);
                rate = BitConverter.ToInt32(bytes, body + 4);
                bits = BitConverter.ToUInt16(bytes, body + 14);
            }
            else if (id == "data")
            {
                data = bytes.AsSpan(body, size).ToArray();
            }
            at = body + size + (size & 1);
        }
        if (data is null || rate <= 0 || channels is < 1 or > 2 || bits is not (8 or 16))
            return null;
        if (bits == 8)
        {
            // WAV stores 8-bit samples unsigned, Godot wants them signed.
            for (var i = 0; i < data.Length; i++)
                data[i] = (byte)(data[i] - 128);
        }
        return new AudioStreamWav
        {
            Data = data,
            Format = bits == 8 ? AudioStreamWav.FormatEnum.Format8Bits : AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = rate,
            Stereo = channels == 2,
        };
    }

    /// <summary>A second copy of the stream that loops over its whole length.</summary>
    private AudioStreamWav? Looping(string file)
    {
        var key = file + "#loop";
        if (_streams.TryGetValue(key, out var cached))
            return cached;
        AudioStreamWav? loop = null;
        if (Stream(file) is { } stream)
        {
            loop = (AudioStreamWav)stream.Duplicate();
            loop.LoopMode = AudioStreamWav.LoopModeEnum.Forward;
            loop.LoopBegin = 0;
            loop.LoopEnd = (int)(stream.GetLength() * stream.MixRate);
        }
        _streams[key] = loop;
        return loop;
    }
}
