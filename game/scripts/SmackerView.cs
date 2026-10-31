using Godot;
using Machines.Import.Video;

namespace Machines.Game;

/// <summary>
/// Plays one of the original's Smacker flics from the player's own copy of the game: a briefing face, a system,
/// planet or mission picture, or a cut-scene. Frames are decoded one by one as the clip's clock reaches them
/// (<see cref="SmackerDecoder"/>) into a single texture, shown at the picture's own aspect. The first sound track is
/// decoded whole on a worker thread and plays on the effects bus; the clip waits at its first frame until the sound
/// is ready (a second at most), then the picture follows the sound's clock.
///
/// In the looks other than the classic one, the film's HD version plays instead when it has been made
/// (<c>classic/films</c>, by tools/film-upscale: enlarged, with frames drawn in between, as Ogg Theora).
/// </summary>
public partial class SmackerView : TextureRect
{
    /// <summary>Most frames decoded in one engine frame when the clip has fallen behind.</summary>
    private const int CatchUpFrames = 4;

    /// <summary>How long the first frame waits for the sound before the clip starts without it.</summary>
    private const double SoundWait = 1;

    /// <summary>How far the sound may be from the picture's clock before it is moved to it.</summary>
    private const double SoundDrift = 0.5;

    private SmackerDecoder? _decoder;
    private VideoStreamPlayer? _film;
    private double _frameSeconds;
    private int _endFrame;
    private byte[] _rgba = [];
    private Image? _image;
    private ImageTexture? _texture;
    private Task<(byte[] Pcm, int Rate, int Bits, bool Stereo)>? _soundLoad;
    private AudioStreamPlayer? _sound;
    private double _clock, _waited, _latency;
    private bool _started, _ended, _paused, _closed;
    private bool _skippable;

    /// <param name="path">The <c>.smk</c> file, e.g. <c>&lt;game&gt;/flics/gui/tutba1b.smk</c>. A file that is missing or
    /// cannot be read leaves the view empty and raises <see cref="Finished"/> at once.</param>
    public SmackerView(string path)
    {
        FilePath = path;
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.KeepAspectCentered;
        MouseFilter = MouseFilterEnum.Ignore;
        Open();
    }

    public string FilePath { get; }

    /// <summary>
    /// The picture's size as the original shows it, in pixels (doubled lines included), also for the HD version; zero
    /// when the file cannot be read.
    /// </summary>
    public Vector2I VideoSize { get; private set; }

    /// <summary>Whether the HD version plays rather than the original.</summary>
    public bool IsHd => _film is not null;

    /// <summary>The clip's length in seconds.</summary>
    public double Duration { get; private set; }

    /// <summary>Starts again after the last frame, for the menus' pictures; such a clip never finishes by itself.</summary>
    public bool Loop { get; set; }

    /// <summary>A click on the picture or Escape ends the clip.</summary>
    public bool Skippable
    {
        get => _skippable;
        set
        {
            _skippable = value;
            MouseFilter = value ? MouseFilterEnum.Stop : MouseFilterEnum.Ignore;
        }
    }

    /// <summary>Holds the clip, sound included. It is also held while the view is not visible in the tree.</summary>
    public bool Paused { get; set; }

    /// <summary>The frame shown, from 0; -1 before the first.</summary>
    public int CurrentFrame => _film is null ? _decoder?.FrameIndex ?? -1 : _ended ? _endFrame : (int)(_film.StreamPosition / _frameSeconds);

    /// <summary>Whether the clip has ended: played through, skipped, or its file could not be read.</summary>
    public bool IsFinished => _ended;

    /// <summary>Raised once when a clip that does not loop ends, or any clip is skipped.</summary>
    public event Action? Finished;

    /// <summary>Ends the clip now, as a click would; the last frame shown stays.</summary>
    public void Skip() => End();

    public override void _EnterTree()
    {
        // Back in the tree after leaving it: the file was closed, so the clip starts over.
        if (_closed && !_ended)
        {
            _closed = false;
            Open();
        }
    }

    public override void _ExitTree()
    {
        _closed = _decoder is not null || _film is not null;
        _decoder?.Dispose();
        _decoder = null;
        _film?.QueueFree();
        _film = null;
        _soundLoad = null;
        _sound?.Stop();
    }

    public override void _Process(double delta)
    {
        if (_ended)
            return;
        if (_decoder is null && _film is null)
        {
            End();
            return;
        }
        var paused = Paused || !IsVisibleInTree();
        if (paused != _paused)
        {
            _paused = paused;
            if (_sound is not null)
                _sound.StreamPaused = paused;
            if (_film is not null)
                _film.Paused = paused;
        }
        if (paused)
            return;
        if (_film is not null)
        {
            // The HD film keeps its own clock and sound; the view shows its picture.
            if (!_started)
            {
                _started = true;
                _film.Play();
            }
            if (Texture != _film.GetVideoTexture())
                Texture = _film.GetVideoTexture();
            return;
        }

        try
        {
            if (_decoder!.FrameIndex < 0)
                Advance(0);
            TakeSound();
            if (!_started)
            {
                // The first frame waits for the sound, so that a briefing is heard from its first word.
                _waited += delta;
                if (_soundLoad is not null && _waited < SoundWait)
                    return;
                _started = true;
                _sound?.Play();
            }
            else
            {
                _clock += delta;
            }
            if (_sound is { Playing: true })
            {
                // The picture follows the sound as it reaches the ear; a sound far off (its device stalled, say)
                // is put back to the picture instead.
                var heard = _sound.GetPlaybackPosition() + AudioServer.GetTimeSinceLastMix() - _latency;
                if (Math.Abs(heard - _clock) < SoundDrift)
                    _clock = Math.Max(0, heard);
                else
                    _sound.Seek((float)_clock);
            }

            var frameSeconds = _decoder.FrameDuration.TotalSeconds;
            if (_clock < Duration)
            {
                Advance((int)(_clock / frameSeconds));
                return;
            }
            Advance(_decoder.FrameCount - 1);
            if (_decoder.FrameIndex < _decoder.FrameCount - 1)
                return;
            if (!Loop)
            {
                End();
                return;
            }
            _decoder.Rewind();
            _clock = 0;
            Advance(0);
            _sound?.Play();
        }
        catch (InvalidDataException e)
        {
            GD.PushWarning($"flic broken off: {FilePath}: {e.Message}");
            End();
        }
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (Skippable && !_ended && @event is InputEventMouseButton { Pressed: true })
        {
            AcceptEvent();
            End();
        }
    }

    public override void _Input(InputEvent @event)
    {
        // Before the menus see it: Escape here ends the clip, not the page.
        if (Skippable && !_ended && IsVisibleInTree() && @event.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            End();
        }
    }

    private void Open()
    {
        try
        {
            _decoder = SmackerDecoder.Open(FilePath);
        }
        catch (Exception e) when (e is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            GD.PushWarning($"flic not played: {FilePath}: {e.Message}");
            return;
        }
        VideoSize = new Vector2I(_decoder.Width, _decoder.DisplayHeight);
        _frameSeconds = _decoder.FrameDuration.TotalSeconds;
        Duration = _decoder.FrameCount * _frameSeconds;
        (_clock, _waited, _started) = (0, 0, false);
        _sound?.Stop();
        if (HdFilm(FilePath) is { } hd)
        {
            // The original's header still gives the size and length.
            _decoder.Dispose();
            _decoder = null;
            _film = new VideoStreamPlayer
            {
                Name = "Film",
                Stream = new VideoStreamTheora { File = hd },
                Bus = GameAudio.EffectsBus,
                Visible = false,
            };
            _film.Finished += () =>
            {
                if (Loop && !_ended)
                    _film?.Play();
                else
                    End();
            };
            AddChild(_film);
            return;
        }
        _rgba = new byte[_decoder.Width * _decoder.DisplayHeight * 4];
        if (_decoder.AudioTracks.FirstOrDefault() is { } track)
        {
            var (path, index, rate, bits, stereo) = (FilePath, track.Index, track.Rate, track.Bits, track.Channels == 2);
            _soundLoad = Task.Run(() =>
            {
                using var decoder = SmackerDecoder.Open(path);
                var pcm = decoder.ReadAllAudio(index);
                // Smacker's 8-bit samples are unsigned, Godot wants them signed.
                if (bits == 8)
                    for (var i = 0; i < pcm.Length; i++)
                        pcm[i] ^= 0x80;
                return (pcm, rate, bits, stereo);
            });
        }
    }

    /// <summary>Picks up the sound once the worker has decoded it; a clip already running joins it where it is.</summary>
    private void TakeSound()
    {
        if (_soundLoad is not { IsCompleted: true } load)
            return;
        _soundLoad = null;
        if (!load.IsCompletedSuccessfully)
        {
            GD.PushWarning($"flic sound not played: {FilePath}: {load.Exception?.InnerException?.Message}");
            return;
        }
        var (pcm, rate, bits, stereo) = load.Result;
        if (pcm.Length == 0)
            return;
        if (_sound is null)
        {
            _sound = new AudioStreamPlayer { Name = "Sound", Bus = GameAudio.EffectsBus };
            AddChild(_sound);
            _latency = AudioServer.GetOutputLatency();
        }
        _sound.Stream = new AudioStreamWav
        {
            Data = pcm,
            Format = bits == 8 ? AudioStreamWav.FormatEnum.Format8Bits : AudioStreamWav.FormatEnum.Format16Bits,
            MixRate = rate,
            Stereo = stereo,
        };
        if (_started && _clock < Duration)
            _sound.Play((float)_clock);
    }

    /// <summary>Decodes up to frame <paramref name="target"/> (a few frames at most) and shows the last one decoded.</summary>
    private void Advance(int target)
    {
        var decoder = _decoder!;
        var decoded = 0;
        while (decoder.FrameIndex < target && decoded < CatchUpFrames && decoder.NextFrame())
            decoded++;
        if (decoded == 0)
            return;
        decoder.CopyRgba(_rgba);
        if (_image is null || _image.GetWidth() != decoder.Width || _image.GetHeight() != decoder.DisplayHeight)
        {
            _image = Image.CreateFromData(decoder.Width, decoder.DisplayHeight, false, Image.Format.Rgba8, _rgba);
            _texture = ImageTexture.CreateFromImage(_image);
            Texture = _texture;
        }
        else
        {
            _image.SetData(decoder.Width, decoder.DisplayHeight, false, Image.Format.Rgba8, _rgba);
            _texture!.Update(_image);
        }
    }

    private void End()
    {
        if (_ended)
            return;
        _ended = true;
        _sound?.Stop();
        // Paused rather than stopped, so its last frame stays shown.
        if (_film is not null)
        {
            _endFrame = _film.IsPlaying() ? (int)(_film.StreamPosition / _frameSeconds) : (int)Math.Round(Duration / _frameSeconds) - 1;
            _film.Paused = true;
        }
        Finished?.Invoke();
    }

    /// <summary>
    /// The HD version of a flic under the game copy's <c>flics</c> folder (<c>classic/films/&lt;same path&gt;.ogv</c>),
    /// in the looks other than the classic one, when it has been made.
    /// </summary>
    private static string? HdFilm(string path)
    {
        if (GraphicsSettings.Look == Look.Classic)
            return null;
        var parts = System.IO.Path.GetFullPath(path).Split('/', '\\');
        var flics = Array.FindLastIndex(parts, p => p.Equals("flics", StringComparison.OrdinalIgnoreCase));
        if (flics < 0)
            return null;
        var film = System.IO.Path.ChangeExtension(System.IO.Path.Combine([Paths.Classic, "films", .. parts[(flics + 1)..]]), ".ogv");
        return File.Exists(film) ? film : null;
    }
}
