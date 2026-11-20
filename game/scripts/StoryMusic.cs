using Godot;

namespace Machines.Game;

/// <summary>
/// The story's music (plan 5.3): a cue per stretch of the chapter (<c>wartung</c> in sections 1–3, <c>jagd</c> in 4–5,
/// <c>gegenwehr</c> in 6, <c>absturz</c> in 7, or what the director names) and a threat level, 0 calm to 2 hunted, from
/// the Thresher's distance and the suspicion on the trio (<see cref="StoryThreat"/>), or the director's. Layers fade in
/// and out with the level over two seconds; a new cue cross-fades over three. No music has been made yet: a cue is played
/// by its stand-in (<see cref="StorySoundscape.MusicStandIn"/>, ambience loops pitched into a drone and pulses), until
/// <c>res://assets/story/music/&lt;cue&gt;.ogg</c> exists; then that file plays, with its stems <c>&lt;cue&gt;_1.ogg</c> and
/// <c>&lt;cue&gt;_2.ogg</c> (if there are any) kept in step with it and brought in at threat 1 and 2. Everything plays on the
/// Music bus, which speech ducks.
/// </summary>
public partial class StoryMusic(StoryAudio audio) : Node
{
    private const float Silent = -60, LevelFade = 2, CueFade = 3;

    private readonly StoryThreat _threat = new();
    private string? _pinned;
    private string? _cue;
    private CueView? _playing;
    private AudioStreamPlayer _sting = null!;

    public int Threat => _threat.Level;

    public string? CueName => _cue;

    public override void _Ready()
    {
        _sting = new AudioStreamPlayer { Name = "Sting", Bus = StoryAudio.MusicBus };
        AddChild(_sting);
    }

    public override void _Process(double delta) => _playing?.Process(audio.Clock, delta);

    /// <summary>The level the director asks for, or null for the Thresher's distance and the suspicion.</summary>
    public void SetThreat(int? level)
    {
        _threat.Override = level is { } l ? Math.Clamp(l, 0, 2) : null;
        // Back to the Thresher's distance at the next look; the director's level at once.
        if (level is not null && _threat.Update(null, 0, audio.Clock))
            Level();
    }

    /// <summary>The cue the director names, or null to follow the section.</summary>
    public void Cue(string? cue)
    {
        _pinned = cue;
        if (cue is not null)
            Start(cue);
    }

    /// <summary>Four times a second: the threat from the Thresher and the suspicion, and the section's cue.</summary>
    public void Look(float? thresherDistance, float suspicion, int section)
    {
        if (_pinned is null)
            Start(section switch
            {
                <= 3 => "wartung",
                4 or 5 => "jagd",
                6 => "gegenwehr",
                _ => "absturz",
            });
        if (_threat.Update(thresherDistance, suspicion, audio.Clock))
            Level();
    }

    public void Sting(string id)
    {
        if (StoryAudio.SfxPath(id) is not { } path || StoryAudio.Stream(path) is not { } stream)
        {
            audio.Trace($"sting {id} missing");
            return;
        }
        _sting.Stream = stream;
        _sting.VolumeDb = StoryAudio.MildScares ? -14 : 0;
        _sting.Play();
        audio.Trace($"sting {id}{(StoryAudio.MildScares ? " mild" : "")}");
    }

    private void Start(string cue)
    {
        if (cue == _cue)
            return;
        _cue = cue;
        _playing?.Stop(CueFade);
        _playing = cue == "silence" ? null : CueView.Create(this, audio, cue, _threat.Level);
        audio.Trace($"music {cue} threat {_threat.Level} {(_playing?.Real == true ? "file" : cue == "silence" ? "off" : "stand-in")}");
    }

    private void Level()
    {
        _playing?.Level(_threat.Level);
        audio.Trace($"threat {_threat.Level}{(_threat.Override is not null ? " set" : "")}");
    }

    /// <summary>A cue playing: its file and stems, or its stand-in's layers.</summary>
    private sealed class CueView
    {
        private readonly Node _root = new();
        private readonly List<(StoryLayer Layer, AudioStreamPlayer Player)> _layers = [];
        private readonly List<(StoryLayer Layer, AudioStreamPlayer Player, double Next)> _pulses = [];
        private AudioStreamSynchronized? _stems;
        private AudioStreamPlayer? _file;
        private float[] _stemDb = [], _stemTarget = [];
        private int _level;
        private bool _stopping;

        public bool Real => _file is not null;

        public static CueView Create(StoryMusic music, StoryAudio audio, string cue, int level)
        {
            var view = new CueView { _level = level };
            view._root.Name = $"Cue-{cue}";
            music.AddChild(view._root);
            var file = $"{StoryAudio.MusicFolder}/{cue}.ogg";
            if (StoryAudio.Exists(file) && StoryAudio.Stream(file) is { } main)
            {
                // The music's stems in step with it, each with its own volume.
                var parts = new List<AudioStream> { main };
                foreach (var stem in new[] { 1, 2 })
                    if (StoryAudio.Exists($"{StoryAudio.MusicFolder}/{cue}_{stem}.ogg") && StoryAudio.Stream($"{StoryAudio.MusicFolder}/{cue}_{stem}.ogg") is { } part)
                        parts.Add(part);
                foreach (var part in parts.OfType<AudioStreamOggVorbis>())
                    part.Loop = true;
                view._stems = new AudioStreamSynchronized { StreamCount = parts.Count };
                for (var i = 0; i < parts.Count; i++)
                    view._stems.SetSyncStream(i, parts[i]);
                view._stemDb = [.. parts.Select((_, i) => i <= level ? 0f : Silent)];
                view._stemTarget = [.. view._stemDb];
                for (var i = 0; i < parts.Count; i++)
                    view._stems.SetSyncStreamVolume(i, view._stemDb[i]);
                view._file = new AudioStreamPlayer { Name = "Music", Stream = view._stems, Bus = StoryAudio.MusicBus, VolumeDb = Silent };
                view._root.AddChild(view._file);
                view._file.Play();
                StorySounds.FadeTo(view._file, 0, CueFade);
                return view;
            }
            foreach (var layer in StorySoundscape.MusicStandIn(cue))
            {
                if (layer.Every > 0)
                {
                    if (StoryAudio.SfxPath(layer.Sound) is not { } path || StoryAudio.Stream(path) is not { } hit)
                        continue;
                    var player = new AudioStreamPlayer { Name = $"Pulse-{layer.Sound}", Stream = hit, Bus = StoryAudio.MusicBus, VolumeDb = layer.Db, PitchScale = layer.Pitch };
                    view._root.AddChild(player);
                    view._pulses.Add((layer, player, audio.Clock + layer.Every));
                }
                else if (audio.Sounds.Loop(layer with { Db = layer.Threat <= level ? layer.Db : Silent }, $"Layer-{layer.Sound}", StoryAudio.MusicBus, view._root) is { } loop)
                {
                    view._layers.Add((layer, loop));
                }
            }
            return view;
        }

        public void Level(int level)
        {
            _level = level;
            foreach (var (layer, player) in _layers)
                StorySounds.FadeTo(player, layer.Threat <= level ? layer.Db : Silent, LevelFade);
            for (var i = 0; i < _stemTarget.Length; i++)
                _stemTarget[i] = i <= level ? 0 : Silent;
        }

        public void Process(double now, double delta)
        {
            if (_stopping)
                return;
            for (var i = 0; i < _pulses.Count; i++)
            {
                var (layer, player, next) = _pulses[i];
                if (now < next)
                    continue;
                if (layer.Threat <= _level)
                    player.Play();
                _pulses[i] = (layer, player, next + layer.Every);
            }
            if (_stems is null)
                return;
            // The stems move towards their level, 60 dB over LevelFade seconds.
            var step = (float)(-Silent / LevelFade * delta);
            for (var i = 0; i < _stemDb.Length; i++)
            {
                if (_stemDb[i] == _stemTarget[i])
                    continue;
                _stemDb[i] = _stemDb[i] < _stemTarget[i] ? Mathf.Min(_stemTarget[i], _stemDb[i] + step) : Mathf.Max(_stemTarget[i], _stemDb[i] - step);
                _stems.SetSyncStreamVolume(i, _stemDb[i]);
            }
        }

        public void Stop(float seconds)
        {
            _stopping = true;
            foreach (var player in _root.GetChildren().OfType<AudioStreamPlayer>())
            {
                var tween = player.CreateTween();
                tween.TweenProperty(player, "volume_db", Silent, seconds);
            }
            var end = _root.CreateTween();
            end.TweenInterval(seconds);
            end.TweenCallback(Callable.From(_root.QueueFree));
        }
    }
}
