using Godot;

namespace Machines.Game;

/// <summary>
/// The story's music (plan 5.3): a cue per stretch of the chapter (<c>wartung</c> in sections 1–3, <c>jagd</c> in 4–5,
/// <c>gegenwehr</c> in 6, <c>absturz</c> in 7, or what the director names) and a threat level, 0 calm to 2 hunted, from
/// the Thresher's distance and the suspicion on the trio (<see cref="StoryThreat"/>), or the director's. Layers fade in
/// and out with the level over two seconds; a new cue cross-fades over three. No cue's music has been made: a cue is played
/// by its stand-in (<see cref="StorySoundscape.MusicStandIn"/>, ambience loops pitched into a drone and pulses), until
/// <c>res://assets/story/music/&lt;cue&gt;.ogg</c> exists; then that file plays, with its stems <c>&lt;cue&gt;_1.ogg</c> and
/// <c>&lt;cue&gt;_2.ogg</c> (if there are any) kept in step with it and brought in at threat 1 and 2. Everything plays on the
/// Music bus, which speech ducks.
/// <para>The prologue has its score (<see cref="StoryScore"/>, the user's pieces): while its files are there it plays in
/// place of the cues and their stand-ins, its playlist from the start, the loud part at the break-in (<see cref="Caught"/>),
/// the escape's piece with the crash countdown. Where the playlist stood is kept for the session, so that a checkpoint
/// loaded after a fall goes on with the piece that played.</para>
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

    public override void _Process(double delta)
    {
        _playing?.Process(audio.Clock, delta);
        ProcessScore(delta);
    }

    public override void _ExitTree()
    {
        // Every player stops before the tree lets go of them (a stream left playing at quit leaks its playback).
        foreach (var player in FindChildren("*", nameof(AudioStreamPlayer), true, false).OfType<AudioStreamPlayer>())
            player.Stop();
    }

    /// <summary>The music fades out over <paramref name="seconds"/> and stays silent (the chapter's last cutscene).</summary>
    public void Out(float seconds)
    {
        _pinned = "silence";
        if (_scored)
            StopScore(seconds);
        _score?.Stop();
        _cue = "silence";
        _playing?.Stop(seconds);
        _playing = null;
        audio.Trace($"music out {seconds:0.#} s");
    }

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
        if (cue is null)
            return;
        // A cue named (the chapter's end: silence) takes the score's place; with silence it is over for good.
        if (_scored)
        {
            StopScore(CueFade);
            if (cue == "silence")
                _score?.Stop();
        }
        Start(cue);
    }

    /// <summary>Four times a second: the threat from the Thresher and the suspicion, and the section's cue.</summary>
    public void Look(float? thresherDistance, float suspicion, int section, float? countdownLeft)
    {
        if (_threat.Update(thresherDistance, suspicion, audio.Clock))
            Level();
        if (_pinned is null && Score() is { } score)
        {
            LookScore(score, section, countdownLeft);
            return;
        }
        if (_pinned is null)
            Start(section switch
            {
                <= 3 => "wartung",
                4 or 5 => "jagd",
                6 => "gegenwehr",
                _ => "absturz",
            });
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

    // ---- the score (StoryScore) -------------------------------------------------------------------

    /// <summary>Where the prologue's playlist stood when its chapter was last played in this session: for a checkpoint after a fall.</summary>
    private static (string Chapter, int Track, double At)? _remembered;

    private StoryScore? _score;
    private bool? _hasScore;
    private bool _scored;
    private int _track = -1;
    private Fader? _list, _caught, _escape;
    private readonly List<Fader> _leaving = [];
    private float _listDb;

    /// <summary>The playlist's level changes (the threat's dips) at so many dB a second.</summary>
    private const float ListSpeed = 3;

    /// <summary>The piece of the score playing now and where it stands, for the checks: (name, seconds) or null.</summary>
    public (string Piece, double At)? ScorePlaying =>
        (_escape ?? _caught ?? _list) is { Player: { Playing: true } player } ? (player.Name.ToString()["Score-".Length..], player.GetPlaybackPosition()) : null;

    /// <summary>The chapter's score while its files are there; null for the cues and their stand-ins.</summary>
    private StoryScore? Score()
    {
        _hasScore ??= audio.Chapter == Machines.Sim.Story.StoryOptions.Prologue && StoryScore.Pieces.All(p => StoryAudio.Exists(Piece(p)));
        return _hasScore == true ? _score ??= new StoryScore() : null;
    }

    private static string Piece(StoryPiece piece) => $"{StoryAudio.MusicFolder}/{piece.Name}.ogg";

    /// <summary>The break-in: the playlist is cut and the loud part of Lost In The Dark plays once from its entry.</summary>
    public void Caught()
    {
        if (_pinned is not null || Score() is not { } score || !score.BreakIn())
            return;
        StopCue();
        Remember();
        Leave(ref _list, StoryScore.CutFade);
        Leave(ref _caught, StoryScore.CutFade);
        _caught = Play(StoryScore.Caught, 0, 0, 0, ListSpeed);
        _scored = true;
        audio.Trace($"score caught {StoryScore.Caught.Name}");
    }

    private void LookScore(StoryScore score, int section, float? countdownLeft)
    {
        // Not before the chapter shows: the playlist's first piece is not spent behind the loading screen.
        if (!_scored && !audio.Shown())
            return;
        StopCue();
        _scored = true;
        switch (score.Follow(section, countdownLeft))
        {
            case StoryScorePart.Playlist:
                _listDb = StoryScore.PlaylistDb(section, _threat.Level, audio.Booting());
                if (_list is null)
                    ResumeList();
                else if (_list.Target != _listDb)
                    _list.To(_listDb, Math.Max(ListSpeed, _list.Speed));
                break;
            case StoryScorePart.Escape when _escape is null && countdownLeft is { } left:
                Remember();
                Leave(ref _list, StoryScore.EscapeFade);
                Leave(ref _caught, StoryScore.EscapeFade);
                var at = StoryScore.EscapeOffset(left, Length(StoryScore.Escape));
                _escape = Play(StoryScore.Escape, at, 0, 0, ListSpeed);
                audio.Trace($"score escape {StoryScore.Escape.Name} from {at:0.0} s with {left:0.0} s left");
                break;
        }
    }

    /// <summary>
    /// The playlist: in this match where the break-in cut it, in a new one where the chapter's last match left it (or where
    /// its checkpoint has it). A piece begun from its start comes in by its own fade, one gone on with fades in.
    /// </summary>
    private void ResumeList()
    {
        var was = _remembered is { } r && r.Chapter == audio.Chapter ? (r.Track, r.At) : ((int Track, double At)?)null;
        var resumed = _track >= 0;
        var (track, at) = resumed && was is { } here ? here : StoryScore.Start(audio.StartCheckpoint, was);
        if (at > Length(StoryScore.Playlist[track]) - StoryScore.Overlap - 1)
            (track, at) = (StoryScore.Next(track), 0);
        _track = track;
        var piece = StoryScore.Playlist[track];
        var fadeIn = at > 0 || resumed;
        _list = Play(piece, at, fadeIn ? Silent : _listDb, _listDb, fadeIn ? -Silent / (float)StoryScore.ResumeFade : ListSpeed);
        _remembered = (audio.Chapter, track, at);
        audio.Trace($"score playlist {piece.Name} from {at:0.0} s");
    }

    private void ProcessScore(double delta)
    {
        for (var i = _leaving.Count - 1; i >= 0; i--)
        {
            var fader = _leaving[i];
            if (fader.Step(delta) || !fader.Player.Playing)
            {
                fader.Player.Stop();
                fader.Player.QueueFree();
                _leaving.RemoveAt(i);
            }
        }
        _escape?.Step(delta);
        if (_caught is { } caught)
        {
            caught.Step(delta);
            if (!caught.Player.Playing)
            {
                _score?.CaughtEnded();
                caught.Player.QueueFree();
                _caught = null;
                audio.Trace($"score caught ended");
            }
        }
        if (_list is not { } list)
            return;
        list.Step(delta);
        var length = list.Player.Stream.GetLength();
        var position = list.Player.Playing ? list.Player.GetPlaybackPosition() : length;
        _remembered = (audio.Chapter, _track, position);
        // The next piece comes in under the last seconds of this one (each begins and ends in its own fade); after the
        // last, the first again.
        if (position < length - StoryScore.Overlap)
            return;
        list.Leaving = true;
        _leaving.Add(list);
        _track = StoryScore.Next(_track);
        _list = Play(StoryScore.Playlist[_track], 0, _listDb, _listDb, ListSpeed);
        _remembered = (audio.Chapter, _track, 0);
        audio.Trace($"score playlist {StoryScore.Playlist[_track].Name}");
    }

    /// <summary>Where the playlist stands now, kept for a checkpoint.</summary>
    private void Remember()
    {
        if (_list is { Player: { Playing: true } player })
            _remembered = (audio.Chapter, _track, player.GetPlaybackPosition());
    }

    private void StopScore(float seconds)
    {
        Remember();
        Leave(ref _list, seconds);
        Leave(ref _caught, seconds);
        Leave(ref _escape, seconds);
        _scored = false;
        audio.Trace($"score off");
    }

    /// <summary>A cue's stand-in playing (since a cue the director named was let go of) gives way to the score.</summary>
    private void StopCue()
    {
        if (_cue is null)
            return;
        _playing?.Stop(CueFade);
        _playing = null;
        _cue = null;
    }

    /// <summary>A score player fades out over so many seconds and goes.</summary>
    private void Leave(ref Fader? fader, double seconds)
    {
        if (fader is null)
            return;
        fader.To(Silent, -Silent / (float)Math.Max(seconds, 0.01));
        fader.Leaving = true;
        _leaving.Add(fader);
        fader = null;
    }

    private Fader Play(StoryPiece piece, double at, float fromDb, float toDb, float speed)
    {
        var stream = StoryAudio.Stream(Piece(piece))!;
        // Played once: the playlist goes on to the next piece itself.
        if (stream is AudioStreamOggVorbis ogg)
            ogg.Loop = false;
        var player = new AudioStreamPlayer { Name = $"Score-{piece.Name}", Stream = stream, Bus = StoryAudio.MusicBus, VolumeDb = fromDb };
        AddChild(player);
        player.Play((float)at);
        var fader = new Fader(player);
        fader.To(toDb, speed);
        return fader;
    }

    private static double Length(StoryPiece piece) => StoryAudio.Stream(Piece(piece))?.GetLength() is { } length and > 0 ? length : piece.Seconds;

    /// <summary>A score player moving to its level at so many dB a second (the playlist's dips are slow, its cut fast).</summary>
    private sealed class Fader(AudioStreamPlayer player)
    {
        public AudioStreamPlayer Player { get; } = player;
        public float Target { get; private set; } = player.VolumeDb;
        public float Speed { get; private set; } = ListSpeed;
        public bool Leaving { get; set; }

        public void To(float db, float speed)
        {
            Target = db;
            Speed = speed;
        }

        /// <summary>One frame towards the level; true once a leaving player is silent.</summary>
        public bool Step(double delta)
        {
            var db = Player.VolumeDb;
            if (db != Target)
            {
                var step = (float)(Speed * delta);
                Player.VolumeDb = db < Target ? Mathf.Min(Target, db + step) : Mathf.Max(Target, db - step);
            }
            return Leaving && Player.VolumeDb <= Silent;
        }
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
