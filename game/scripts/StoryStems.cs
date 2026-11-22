using Godot;

namespace Machines.Game;

/// <summary>
/// For measuring the story's mix: MACHINES_STORY_STEMS=&lt;folder&gt; records what the music, the world and the voices put
/// on the story's mix bus, each on its own, into 32-bit float WAV files: <c>&lt;n&gt;-music-dry.wav</c> the music before the
/// duck under speech, <c>&lt;n&gt;-music.wav</c> after it, <c>&lt;n&gt;-world.wav</c>, <c>&lt;n&gt;-voice-dry.wav</c> the voices
/// before their compressor and <c>&lt;n&gt;-voice.wav</c> after it (n counts the chapters started in the run), and
/// <c>&lt;n&gt;-lines.tsv</c>, each line's start and end as a frame of those files. The files are taken before each bus's
/// fader: <c>&lt;n&gt;-lines.tsv</c> begins with the faders' levels. With the movie maker (<c>--write-movie</c>) the sound is
/// mixed frame by frame; in real time (also headless, or with <c>--audio-driver Dummy</c> to keep it silent) the taps are
/// drained every frame.
/// </summary>
public partial class StoryStems : Node
{
    private static int _chapters;

    private readonly List<(AudioEffectCapture Tap, System.IO.FileStream File, string Name)> _taps = [];
    private System.IO.StreamWriter _lines = null!;
    private long _frames;

    /// <summary>The folder asked for, or null when the switch is off.</summary>
    public static string? Folder => System.Environment.GetEnvironmentVariable("MACHINES_STORY_STEMS") is { Length: > 0 } f ? f : null;

    /// <summary>Puts the taps on the story's buses (StoryAudio.BuildBuses, once they are made).</summary>
    public static StoryStems? Tap(string folder)
    {
        System.IO.Directory.CreateDirectory(folder);
        var stems = new StoryStems { Name = "StoryStems" };
        var n = ++_chapters;
        var rate = (int)AudioServer.GetMixRate();
        void Add(string bus, int at, string name)
        {
            var index = AudioServer.GetBusIndex(bus);
            var tap = new AudioEffectCapture { BufferLength = 2 };
            AudioServer.AddBusEffect(index, tap, at);
            // Readable while it is written, so a long run can be measured as it goes.
            var file = new System.IO.FileStream(System.IO.Path.Combine(folder, $"{n}-{name}.wav"), System.IO.FileMode.Create,
                System.IO.FileAccess.ReadWrite, System.IO.FileShare.Read);
            Header(file, rate, 0);
            stems._taps.Add((tap, file, name));
        }
        Add(StoryAudio.MusicBus, 0, "music-dry");
        Add(StoryAudio.MusicBus, -1, "music");
        Add(StoryAudio.WorldBus, -1, "world");
        Add(StoryAudio.VoiceBus, 0, "voice-dry");
        Add(StoryAudio.VoiceBus, -1, "voice");
        stems._lines = new System.IO.StreamWriter(System.IO.Path.Combine(folder, $"{n}-lines.tsv"));
        string Db(string bus) => AudioServer.GetBusVolumeDb(AudioServer.GetBusIndex(bus)).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        stems._lines.WriteLine($"rate\t{rate}");
        stems._lines.WriteLine($"fader\tmusic\t{Db(StoryAudio.MusicBus)}\tworld\t{Db(StoryAudio.WorldBus)}\tvoice\t{Db(StoryAudio.VoiceBus)}");
        StoryAudio.LineStarted += stems.Started;
        StoryAudio.LineEnded += stems.Ended;
        return stems;
    }

    private void Started(StoryLineCue cue) => Mark("start", cue);

    private void Ended(StoryLineCue cue) => Mark("end", cue);

    private void Mark(string what, StoryLineCue cue)
    {
        Drain();
        _lines.WriteLine($"{what}\t{cue.Id}\t{cue.Channel}\t{_frames}\t{cue.Lang}");
        _lines.Flush();
    }

    public override void _Process(double delta) => Drain();

    private void Drain()
    {
        // The taps run together: take the frames all of them have, so the files stay in step.
        var count = _taps.Min(t => t.Tap.GetFramesAvailable());
        if (count <= 0)
            return;
        foreach (var (tap, file, _) in _taps)
        {
            var buffer = tap.GetBuffer(count);
            var bytes = new byte[buffer.Length * 8];
            for (var i = 0; i < buffer.Length; i++)
            {
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8), buffer[i].X);
                BitConverter.TryWriteBytes(bytes.AsSpan(i * 8 + 4), buffer[i].Y);
            }
            file.Write(bytes);
        }
        _frames += count;
    }

    public override void _ExitTree()
    {
        Drain();
        StoryAudio.LineStarted -= Started;
        StoryAudio.LineEnded -= Ended;
        foreach (var (_, file, _) in _taps)
        {
            file.Position = 0;
            Header(file, (int)AudioServer.GetMixRate(), _frames);
            file.Dispose();
        }
        _taps.Clear();
        _lines.Dispose();
    }

    // A RIFF header of stereo 32-bit float at the rate, for so many frames.
    private static void Header(System.IO.Stream file, int rate, long frames)
    {
        var data = (uint)Math.Min(frames * 8, uint.MaxValue - 64);
        using var w = new System.IO.BinaryWriter(file, System.Text.Encoding.ASCII, leaveOpen: true);
        w.Write("RIFF"u8);
        w.Write(36 + data);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)3);
        w.Write((short)2);
        w.Write(rate);
        w.Write(rate * 8);
        w.Write((short)8);
        w.Write((short)32);
        w.Write("data"u8);
        w.Write(data);
    }
}
