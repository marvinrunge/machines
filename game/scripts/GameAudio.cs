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
    /// <summary>One-shot sounds playing at once; more are dropped, as a sound card's voices ran out.</summary>
    private const int Voices = 32;

    private readonly Dictionary<string, SoundDefinition> _sounds = [];
    private readonly Dictionary<string, VoiceMailDefinition> _voiceMail = [];
    private readonly Dictionary<string, AudioStreamWav?> _streams = [];
    private readonly List<AudioStreamPlayer3D> _players = [];
    private readonly Dictionary<string, double> _lastPlayed = [];
    private readonly RandomNumberGenerator _random = new();
    private AudioStreamPlayer _voice = null!;
    private string? _gameFolder;

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
        audio._voice = new AudioStreamPlayer { Name = "VoiceMail" };
        audio.AddChild(audio._voice);
        return audio;
    }

    public bool Enabled => _sounds.Count > 0;

    // ---- one-shot sounds ------------------------------------------------------------------------

    /// <summary>Plays a sound effect once at a point of the world.</summary>
    public void Play(string? id, Vector3 at)
    {
        if (id is null || !_sounds.TryGetValue(id, out var sound) || Stream(sound.File) is not { } stream)
            return;
        // The same sound started within a few frames is heard once: a burst of 20 grunts firing is one bang.
        var now = Time.GetTicksMsec() / 1000.0;
        if (_lastPlayed.TryGetValue(id, out var last) && now - last < 0.06)
            return;
        _lastPlayed[id] = now;

        var player = _players.FirstOrDefault(p => !p.Playing);
        if (player is null)
        {
            if (_players.Count >= Voices)
                return;
            player = new AudioStreamPlayer3D();
            AddChild(player);
            _players.Add(player);
        }
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
        var idleSound = idle is not null && _sounds.TryGetValue(idle, out var i) ? i : null;
        var moveSound = move is not null && _sounds.TryGetValue(move, out var m) ? m : idleSound;
        if (idleSound is null || moveSound is null)
            return null;
        var idleStream = Looping(idleSound.File);
        var moveStream = Looping(moveSound.File);
        if (idleStream is null || moveStream is null)
            return null;
        var player = new AudioStreamPlayer3D { Name = "Engine" };
        return new EngineSound(player, idleSound, idleStream, moveSound, moveStream);
    }

    public sealed class EngineSound(AudioStreamPlayer3D player, SoundDefinition idle, AudioStreamWav idleStream, SoundDefinition move, AudioStreamWav moveStream)
    {
        private bool? _moving;

        public AudioStreamPlayer3D Player { get; } = player;

        public void Update(bool moving)
        {
            if (_moving == moving && Player.Playing)
                return;
            _moving = moving;
            Configure(Player, moving ? move : idle, moving ? moveStream : idleStream);
            // Engines sit under the battle: quieter than weapons.
            Player.VolumeDb -= 6;
            Player.Play();
            Trace(moving ? move.Id : idle.Id);
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
    }

    // ---- voice mail -----------------------------------------------------------------------------

    /// <summary>
    /// Speaks a report such as <c>VID_GRUNT_SELECTED</c>, picking one of its numbered variants. A report that
    /// arrives while another is playing is dropped, as the original's voice-mail queue did for minor ones.
    /// </summary>
    public void Say(string id)
    {
        if (_voice.Playing)
            return;
        var variants = _voiceMail.Keys.Where(k => k == id || k.Length == id.Length + 1 && k.StartsWith(id, StringComparison.Ordinal) && char.IsDigit(k[^1])).ToList();
        if (variants.Count == 0)
            return;
        var mail = _voiceMail[variants[_random.RandiRange(0, variants.Count - 1)]];
        if (Stream(mail.File) is not { } stream)
            return;
        _voice.Stream = stream;
        _voice.Play();
        Trace(mail.Id);
    }

    // ---- loading ------------------------------------------------------------------------------

    private AudioStreamWav? Stream(string file)
    {
        if (_streams.TryGetValue(file, out var cached))
            return cached;
        AudioStreamWav? stream = null;
        if (_gameFolder is not null)
        {
            var path = System.IO.Path.Combine(_gameFolder, file.Replace('\\', '/'));
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
