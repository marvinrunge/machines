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
/// place of the cues and their stand-ins: the stealth's pieces from the start, the loud part at the break-in
/// (<see cref="Caught"/>), the old playlist after it, the armoury's fight piece while its waves run, the boss's piece, the
/// escape's piece from the lift's burst. Where each list stood is kept for the session, so that a checkpoint loaded after a
/// fall goes on with the piece that played.</para>
/// <para>Stage 7 (package M) reads the director's cues (<see cref="Hear"/>): the Thresher's distance and state duck the
/// stealth's music in section 2 (<see cref="StoryDuck"/>, with a low-pass on the Music bus); the armoury's waves and pauses
/// bring in Static Dread v2 and set its level, the boss Static Dread v1; <c>score escape</c> anchors Circuitry v1, which
/// loops its last section until the launch; <c>hush</c> dips the music for a moment, never stopping it.</para>
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
        // The duck's low-pass and the hushes, first on the Music bus (before the speech duck and the stems' taps); the bus goes with the chapter.
        if (AudioServer.GetBusIndex(StoryAudio.MusicBus) is var bus and >= 0)
        {
            _lowPass = new AudioEffectLowPassFilter { CutoffHz = StoryDuck.OpenHz, Resonance = 0.5f };
            _hushAmp = new AudioEffectAmplify { VolumeDb = 0 };
            AudioServer.AddBusEffect(bus, _hushAmp, 0);
            AudioServer.AddBusEffect(bus, _lowPass, 0);
        }
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
        section = SectionOverride ?? section;
        countdownLeft = CountdownOverride ?? countdownLeft;
        _thresherSeen = thresherDistance;
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

    // ---- the director's cues (stage 7) -----------------------------------------------------------

    /// <summary>For the music's check (MACHINES_STORY_AUDIO_TEST=music): the section and the countdown it plays as if they were.</summary>
    public int? SectionOverride { get; set; }
    public float? CountdownOverride { get; set; }

    private AudioEffectLowPassFilter? _lowPass;
    private AudioEffectAmplify? _hushAmp;
    private Tween? _hushTween, _filterTween;
    private bool _filterOn;
    private readonly StoryDuck _duck = new();
    private float? _thresherSeen;
    private (float Metres, double At)? _thresherHeard;
    private string? _thresherState;
    private bool _launched;

    /// <summary>A <c>thresher near</c> cue counts over the view's own distance for so long (seconds).</summary>
    private const double HeardFor = 1.5;

    /// <summary>
    /// A director's cue (its words, as Game.StoryCue has them) or a view's own word for the music: true when the music read
    /// it (<see cref="StoryMusicCue.Parse"/>: <c>thresher near|state</c>, <c>wave n alive</c>, <c>wave pause</c>, <c>boss in|down</c>,
    /// <c>score escape s</c>, <c>hush s dB</c>, <c>launch</c>). Every other cue is left to its section.
    /// </summary>
    public static bool Hear(IReadOnlyList<string> words) => StoryAudio.Current?.MusicCues.HearHere(words) ?? false;

    public bool HearHere(IReadOnlyList<string> words)
    {
        if (StoryMusicCue.Parse(words) is not { } cue)
            return false;
        var clock = audio.Clock;
        switch (cue.Kind)
        {
            case StoryMusicCueKind.ThresherNear:
                _thresherHeard = (cue.Number, clock);
                return true;
            case StoryMusicCueKind.ThresherState:
                if (cue.State != _thresherState)
                    audio.Trace($"thresher {cue.State}");
                _thresherState = cue.State;
                return true;
            case StoryMusicCueKind.Hush:
                HushMusic(cue.Number, cue.Db);
                return true;
            case StoryMusicCueKind.Launch:
                // The hold lets go: should the cutscene's fade not cover it, the piece plays out to its end.
                _launched = true;
                if (_escape?.Player.Stream is AudioStreamOggVorbis escape)
                    escape.Loop = false;
                audio.Trace($"score launch");
                return true;
        }
        if (_pinned is not null || Score() is not { } score)
            return true;
        switch (cue.Kind)
        {
            case StoryMusicCueKind.WaveAlive:
                score.Wave();
                audio.Trace($"score wave {cue.Number:0} alive");
                break;
            case StoryMusicCueKind.WavePause:
                score.Pause();
                audio.Trace($"score wave pause");
                break;
            case StoryMusicCueKind.BossIn:
                if (score.BossIn())
                    Loud("boss", StoryScore.Boss);
                break;
            case StoryMusicCueKind.BossDown:
                if (score.BossDown(clock))
                {
                    // The fall: the loud part (or the playlist) cut, 2 s of silence, then the playlist comes back by itself.
                    Remember();
                    Leave(ref _list, StoryScore.CutFade);
                    Leave(ref _caught, StoryScore.CutFade);
                    audio.Trace($"score boss down, rest {StoryScore.BossRest:0} s");
                }
                break;
            case StoryMusicCueKind.ScoreEscape:
                score.Burst(clock);
                audio.Trace($"score burst with {cue.Number:0.0} s left");
                break;
        }
        return true;
    }

    /// <summary>The music dips by <paramref name="db"/> for <paramref name="seconds"/> and comes back (J1, the bridge, the stealth's staged silences).</summary>
    private void HushMusic(float seconds, float db)
    {
        if (_hushAmp is null)
            return;
        _hushTween?.Kill();
        _hushTween = CreateTween();
        _hushTween.TweenProperty(_hushAmp, "volume_db", Mathf.Max(db, -80f), 0.12);
        _hushTween.TweenInterval(Mathf.Max(0, seconds - 0.12f));
        _hushTween.TweenProperty(_hushAmp, "volume_db", 0f, 0.4);
        audio.Trace($"music hush {seconds:0.0#} s {db:0} dB");
    }

    /// <summary>The playlist's level under the Thresher in section 2 (null elsewhere), and the low-pass with it.</summary>
    private float? Duck(int section)
    {
        if (section != 2)
        {
            Filter(false);
            return null;
        }
        var clock = audio.Clock;
        var metres = _thresherHeard is { } heard && clock - heard.At <= HeardFor ? heard.Metres : _thresherSeen;
        var listener = audio.Listener;
        var sheltered = StoryDuck.Sheltered(StorySoundscape.RoomAt(audio.Deck, new(listener.X, listener.Z)));
        var was = _duck.Db;
        var db = _duck.Update(metres, _thresherState, sheltered, clock);
        if (db != was)
            audio.Trace($"duck {db:0} dB{(_duck.Filter ? " low-pass" : "")} ({(metres is { } m ? $"{m:0} m" : "none")}, {_thresherState ?? "calm"}{(sheltered ? ", sheltered" : "")})");
        Filter(_duck.Filter);
        return db;
    }

    /// <summary>The Music bus's low-pass closes to 1.2 kHz over 2 s, or opens over 6 s.</summary>
    private void Filter(bool on)
    {
        if (on == _filterOn || _lowPass is null)
            return;
        _filterOn = on;
        _filterTween?.Kill();
        _filterTween = CreateTween();
        _filterTween.TweenProperty(_lowPass, "cutoff_hz", on ? StoryDuck.FilterHz : StoryDuck.OpenHz, on ? StoryDuck.DownOver : StoryDuck.BackOver)
            .SetTrans(Tween.TransitionType.Expo).SetEase(on ? Tween.EaseType.Out : Tween.EaseType.In);
        audio.Trace($"music low-pass {(on ? "on" : "off")}");
    }

    // ---- the score (StoryScore) -------------------------------------------------------------------

    /// <summary>Where each of the prologue's lists stood when its chapter was last played in this session: for a checkpoint after a fall.</summary>
    private static readonly Dictionary<StoryList, (string Chapter, int Track, double At)> _remembered = [];

    private StoryScore? _score;
    private bool? _hasScore;
    private bool _scored;
    private int _track = -1;
    private StoryList _listKind;
    private readonly HashSet<StoryList> _listsPlayed = [];
    private Fader? _list, _caught, _escape;
    private StoryPiece? _caughtPiece;
    private readonly List<Fader> _leaving = [];
    private float _listDb;

    /// <summary>The playlist's level changes (the threat's dips) at so many dB a second.</summary>
    private const float ListSpeed = 3;

    /// <summary>The piece of the score playing now and where it stands, for the checks: (name, seconds) or null.</summary>
    public (string Piece, double At)? ScorePlaying =>
        (_escape ?? _caught ?? _list) is { Player: { Playing: true } player } fader ? (fader.Piece, player.GetPlaybackPosition()) : null;

    /// <summary>The chapter's score while its files are there; null for the cues and their stand-ins.</summary>
    private StoryScore? Score()
    {
        _hasScore ??= audio.Chapter == Machines.Sim.Story.StoryOptions.Prologue && StoryScore.Pieces.All(p => StoryAudio.Exists(Piece(p)));
        return _hasScore == true ? _score ??= new StoryScore(audio.StartCheckpoint) : null;
    }

    private static string Piece(StoryPiece piece) => $"{StoryAudio.MusicFolder}/{piece.Name}.ogg";

    /// <summary>The break-in: the stealth's music is cut and the loud part of Lost In The Dark plays once from its entry.</summary>
    public void Caught()
    {
        if (_pinned is not null || Score() is not { } score || !score.BreakIn())
            return;
        Loud("caught", StoryScore.Caught);
    }

    /// <summary>
    /// A piece over the cut playlist: the loud part of Lost In The Dark from its entry at the break-in (once), Static Dread v1
    /// as the armoury's boss bursts in (looping its body until he falls).
    /// </summary>
    private void Loud(string why, StoryPiece piece)
    {
        StopCue();
        Remember();
        Leave(ref _list, StoryScore.CutFade);
        Leave(ref _caught, StoryScore.CutFade);
        _caughtPiece = piece;
        _caught = Play(piece, 0, 0, 0, ListSpeed, hold: piece.Holds);
        _scored = true;
        audio.Trace($"score {why} {piece.Name}{(piece.Holds ? FormattableString.Invariant($", hold {piece.HoldFrom:0.000}-{piece.HoldTo:0.000} s") : "")}");
    }

    private void LookScore(StoryScore score, int section, float? countdownLeft)
    {
        // Not before the chapter shows: the playlist's first piece is not spent behind the loading screen.
        if (!_scored && !audio.Shown())
            return;
        StopCue();
        _scored = true;
        var duck = Duck(section);
        switch (score.Follow(section, countdownLeft, audio.Clock))
        {
            case StoryScorePart.Playlist:
                var db = StoryScore.PlaylistDb(section, _threat.Level, audio.Booting(), score.Armoury, duck);
                if (db != _listDb && _list is not null)
                    audio.Trace($"score level {db:0.#} dB{(score.Armoury != StoryArmouryMusic.None ? $" ({score.Armoury.ToString().ToLowerInvariant()})" : "")}");
                var from = _listDb;
                _listDb = db;
                // Another list's turn (the armoury's waves begin or are over): the one playing gives way over the overlap.
                if (_list is not null && _listKind != score.List)
                {
                    Remember();
                    Leave(ref _list, StoryScore.Overlap);
                }
                if (_list is null)
                    ResumeList(score.List);
                else if (_list.Target != _listDb)
                    _list.To(_listDb, duck is not null ? StoryDuck.Speed(from, _listDb) : Math.Max(ListSpeed, _list.Speed));
                break;
            case StoryScorePart.Escape:
                // The Notgang: the playlist fades out for good; the escape's piece waits for the lift's burst.
                if (_list is not null || _caught is not null)
                {
                    Remember();
                    Leave(ref _list, StoryScore.EscapeFade);
                    Leave(ref _caught, StoryScore.EscapeFade);
                    audio.Trace($"score playlist out, waiting for the burst");
                }
                if (_escape is null && score.EscapeFrom(audio.StartCheckpoint, countdownLeft, audio.Clock) is { } at)
                    StartEscape(at, countdownLeft);
                break;
        }
    }

    /// <summary>Circuitry v1 from <paramref name="at"/> seconds in, looping its hold (bars 100 to 132) until the launch lets it go.</summary>
    private void StartEscape(double at, float? left)
    {
        _escape = Play(StoryScore.Escape, at, 0, 0, ListSpeed, hold: !_launched);
        audio.Trace($"score escape {StoryScore.Escape.Name} from {at:0.0} s{(left is { } l ? FormattableString.Invariant($" with {l:0.0} s left") : "")}, hold {StoryScore.HoldFrom:0.000}-{StoryScore.HoldTo:0.000} s");
    }

    /// <summary>For the music's check: the escape's piece from <paramref name="at"/> seconds in at once, as a retry would start it.</summary>
    public void EscapeAt(double at)
    {
        Leave(ref _escape, 0.05);
        StartEscape(StoryScore.IntoHold(at), null);
    }

    /// <summary>
    /// A list (<paramref name="list"/>): in this match where it was cut, in a new one where the chapter's last match left it
    /// (or where its checkpoint has it). A piece begun from its start comes in by its own fade, one gone on with fades in.
    /// </summary>
    private void ResumeList(StoryList list)
    {
        var was = _remembered.TryGetValue(list, out var r) && r.Chapter == audio.Chapter ? (r.Track, r.At) : ((int Track, double At)?)null;
        var resumed = _listsPlayed.Contains(list);
        var (track, at) = resumed && was is { } here ? here : StoryScore.Start(list, audio.StartCheckpoint, was);
        var pieces = StoryScore.Of(list);
        if (at > Length(pieces[track]) - StoryScore.Overlap - 1)
            (track, at) = (StoryScore.Next(list, track), 0);
        _listKind = list;
        _listsPlayed.Add(list);
        _track = track;
        var piece = pieces[track];
        var fadeIn = at > 0;
        _list = Play(piece, at, fadeIn ? Silent : _listDb, _listDb, fadeIn ? -Silent / (float)StoryScore.ResumeFade : ListSpeed);
        _remembered[list] = (audio.Chapter, track, at);
        audio.Trace($"score {list.ToString().ToLowerInvariant()} {piece.Name} from {at:0.0} s");
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
        if (_escape is { } escape)
        {
            escape.Step(delta);
            if (!_launched)
                KeepInHold(escape, StoryScore.Escape);
        }
        if (_caught is { } caught)
        {
            caught.Step(delta);
            if (_caughtPiece is { Holds: true } held)
                KeepInHold(caught, held);
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
        _remembered[_listKind] = (audio.Chapter, _track, position);
        // The next piece comes in under the last seconds of this one (each begins and ends in its own fade); after the
        // last, the first again.
        if (position < length - StoryScore.Overlap)
            return;
        list.Leaving = true;
        _leaving.Add(list);
        _track = StoryScore.Next(_listKind, _track);
        var next = StoryScore.Of(_listKind)[_track];
        _list = Play(next, 0, _listDb, _listDb, ListSpeed);
        _remembered[_listKind] = (audio.Chapter, _track, 0);
        audio.Trace($"score {_listKind.ToString().ToLowerInvariant()} {next.Name}");
    }

    /// <summary>Godot's beat loop jumps back at a hold's end; should it not (a stream without it), the jump is made here.</summary>
    private void KeepInHold(Fader fader, StoryPiece piece)
    {
        if (fader.Player.Playing && fader.Player.GetPlaybackPosition() is var at && at > piece.HoldTo + 0.1)
        {
            fader.Player.Seek((float)piece.IntoHold(at));
            audio.Trace($"score hold seek from {at:0.00} s");
        }
    }

    /// <summary>Where the playing list stands now, kept for a checkpoint.</summary>
    private void Remember()
    {
        if (_list is { Player: { Playing: true } player })
            _remembered[_listKind] = (audio.Chapter, _track, player.GetPlaybackPosition());
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

    private Fader Play(StoryPiece piece, double at, float fromDb, float toDb, float speed, bool hold = false)
    {
        var stream = StoryAudio.Stream(Piece(piece))!;
        // Played once: the playlist goes on to the next piece itself. A piece held (the boss's, the escape's) loops its hold
        // by Godot's beat loop: at HoldBeats beats of HoldBpm (the hold's end) it goes back to LoopOffset (the hold's start).
        if (stream is AudioStreamOggVorbis ogg)
        {
            ogg.Loop = hold && piece.Holds;
            if (ogg.Loop)
            {
                ogg.Bpm = piece.HoldBpm;
                ogg.BeatCount = piece.HoldBeats;
                ogg.BarBeats = 4;
                ogg.LoopOffset = piece.HoldFrom;
            }
        }
        var player = new AudioStreamPlayer { Name = $"Score-{piece.Name}", Stream = stream, Bus = StoryAudio.MusicBus, VolumeDb = fromDb };
        AddChild(player);
        player.Play((float)at);
        var fader = new Fader(player) { Piece = piece.Name };
        fader.To(toDb, speed);
        return fader;
    }

    private static double Length(StoryPiece piece) => StoryAudio.Stream(Piece(piece))?.GetLength() is { } length and > 0 ? length : piece.Seconds;

    /// <summary>A score player moving to its level at so many dB a second (the playlist's dips are slow, its cut fast).</summary>
    private sealed class Fader(AudioStreamPlayer player)
    {
        public AudioStreamPlayer Player { get; } = player;
        public string Piece { get; init; } = "";
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
