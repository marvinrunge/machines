using Godot;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Package S (plan stufe7 C20, design-stealth §9.1): the Thresher's sound on its own bus. Each plant of his feet (ActorView.Stealth
// finds them in his walk) is a Stomp: a sample of the near family (thresher_plant_1..3, heavy), the middle one (thresher_mid_1..3)
// or the far one (thresher_step_1..2), cross-faded by the distance (StoryStalker.Families), each picked at random and a little
// higher or lower each time, the same for a front and a back foot, with no low-pass: the families sound their distance and only
// the loudness falls (user 2026-11-27). Heard through the deck (StoryStalker.Occlusion and Mix: walls and grates take loudness),
// with a sub thump within 120 m, his hydraulic breath every second
// plant within 25 m and the grate under the listener rattling within 30 m. His states (StoryStalker.StateOf) have their own
// sounds: the halt and a falling ping repeating faster as he grows sure, the lens focusing and the servo ticks of his scan, the
// lock whine of the draw with the black-out's cut-outs, the wind-down when he lost his quarry, a long exhale when he calms.
// Package E calls Stomp for the hunt's gallop as well. MACHINES_STORY_STOMP_TEST (Game.StoryStealth) logs one line a plant.
public partial class StorySounds
{
    private const int StompVoices = 10;
    private readonly List<AudioStreamPlayer3D> _stompVoices = [];
    private AudioStreamPlayer? _sub;
    private int _plants;
    private StalkerState? _heardState;
    private double _pingAt, _clock;
    private bool _busTuned;

    /// <summary>Each plant's line for the stomp test, or null (Game.StoryStealth sets it while MACHINES_STORY_STOMP_TEST runs).</summary>
    public static Action<string>? StompLog { get; set; }

    /// <summary>
    /// One of the Thresher's plants at <paramref name="at"/> (a foot coming down), heard through the deck from the listener.
    /// Every foot of his walk sounds alike (<paramref name="weight"/> up to 1); above 1 (the hunt's gallop) louder.
    /// </summary>
    public void Stomp(Vector3 at, float weight = 1)
    {
        TuneThresherBus();
        var listener = audio.Listener;
        var distance = new NVector2(at.X - listener.X, at.Z - listener.Z).Length();
        if (distance > StoryStalker.Audible)
            return;
        var (walls, grates) = audio.Deck is { } deck ? StoryStalker.Occlusion(deck, new NVector2(at.X, at.Z), new NVector2(listener.X, listener.Z)) : (0, 0);
        var mix = StoryStalker.Mix(distance, walls, grates);
        var level = mix.Db + StoryStalker.WeightDb(weight);
        foreach (var (family, share) in new[] { ("thresher_plant", mix.Near), ("thresher_mid", mix.Mid), ("thresher_step", mix.Far) })
            if (share > 0.01f)
                StompVoice(family, at, level + Mathf.LinearToDb(share), Unfiltered);
        if (mix.Sub)
            SubThump(-4 - 20 * distance / StoryStalker.SubWithin + (walls > 0 ? -3 : 0));
        _plants++;
        if (distance <= StoryStalker.BreathWithin && _plants % 2 == 0)
            StompVoice("thresher_breath", at + Vector3.Up * 7, -8 + mix.Db, Unfiltered);
        if (mix.Rattle && GrateBy(listener) is { } grate)
            Play("grating_rattle", grate, 0, (float)GD.RandRange(0.9, 1.1));
        var line = FormattableString.Invariant($"stomp {distance:0.0} m walls {walls} grates {grates} gain {level:0.0} dB near {mix.Near:0.00} mid {mix.Mid:0.00} far {mix.Far:0.00}{(mix.Sub ? " sub" : "")}");
        audio.Trace($"{line}");
        StompLog?.Invoke(line);
    }

    /// <summary>The cutoff of Godot's distance filter that leaves a sound as it is (its highest).</summary>
    private const float Unfiltered = 20500;

    /// <summary>A plant's sample on the Thresher's bus, placed, at its level and distance filter cutoff (his own voices, so the story's others keep theirs).</summary>
    private void StompVoice(string id, Vector3 at, float db, float lowPass)
    {
        var sound = StoryAudio.Sfx.Resolve(id, count => _random.RandiRange(0, count - 1));
        if (sound is null || StoryAudio.SfxPath(sound) is not { } path || StoryAudio.Stream(path) is not { } stream)
        {
            audio.Trace($"sfx {id} missing");
            return;
        }
        var player = _stompVoices.FirstOrDefault(v => !v.Playing);
        if (player is null && _stompVoices.Count < StompVoices)
        {
            player = new AudioStreamPlayer3D
            {
                Name = $"Stomp{_stompVoices.Count}",
                Bus = StoryAudio.ThresherBus,
                UnitSize = StoryStalker.StompUnit,
                MaxDistance = StoryStalker.Audible,
                AttenuationModel = AudioStreamPlayer3D.AttenuationModelEnum.InverseDistance,
                PanningStrength = 1,
                AttenuationFilterDb = -24,
            };
            AddChild(player);
            _stompVoices.Add(player);
        }
        player ??= _stompVoices[0];
        if (player == _stompVoices[0] && player.Playing)
        {
            // The oldest goes to the back of the line.
            _stompVoices.RemoveAt(0);
            _stompVoices.Add(player);
        }
        player.Stream = stream;
        player.VolumeDb = db;
        player.AttenuationFilterCutoffHz = lowPass;
        player.PitchScale = Varied(1);
        player.GlobalPosition = at;
        player.Play();
    }

    /// <summary>The sub thump: not placed, felt more than heard, on the Thresher's bus.</summary>
    private void SubThump(float db)
    {
        if (StoryAudio.SfxPath("thresher_sub") is not { } path || StoryAudio.Stream(path) is not { } stream)
            return;
        if (_sub is null)
        {
            _sub = new AudioStreamPlayer { Name = "StompSub", Bus = StoryAudio.ThresherBus };
            AddChild(_sub);
        }
        _sub.Stream = stream;
        _sub.VolumeDb = db;
        _sub.PitchScale = Varied(1);
        _sub.Play();
    }

    /// <summary>A grate cell within 2 m of the listener (it rattles as a plant comes near), its middle; null without one.</summary>
    private Vector3? GrateBy(Vector3 listener)
    {
        if (audio.Deck is not { } deck)
            return null;
        for (var dy = -2; dy <= 2; dy++)
        {
            for (var dx = -2; dx <= 2; dx++)
            {
                var (x, y) = ((int)Mathf.Floor(listener.X) + dx, (int)Mathf.Floor(listener.Z) + dy);
                if (deck.Cell(x, y) == Machines.Import.Model.DeckCell.Grate)
                    return new Vector3(x + 0.5f, listener.Y, y + 0.5f);
            }
        }
        return null;
    }

    /// <summary>
    /// The Thresher's bus as design-stealth §9.1 has it, once a chapter: a long metallic hall (room 0.95, damping 0.15, wet 0.5,
    /// 60 ms predelay), two echoes off the steel at 140 and 310 ms, and a limiter on top.
    /// </summary>
    private void TuneThresherBus()
    {
        if (_busTuned)
            return;
        _busTuned = true;
        var bus = AudioServer.GetBusIndex(StoryAudio.ThresherBus);
        if (bus < 0)
            return;
        if (AudioServer.GetBusEffectCount(bus) > 0 && AudioServer.GetBusEffect(bus, 0) is AudioEffectReverb hall)
            (hall.RoomSize, hall.Damping, hall.Wet, hall.PredelayMsec) = (0.95f, 0.15f, 0.5f, 60);
        AudioServer.AddBusEffect(bus, new AudioEffectDelay
        {
            Dry = 1,
            Tap1Active = true,
            Tap1DelayMs = 140,
            Tap1LevelDb = -12,
            Tap1Pan = -0.3f,
            Tap2Active = true,
            Tap2DelayMs = 310,
            Tap2LevelDb = -16,
            Tap2Pan = 0.3f,
            FeedbackActive = false,
        });
        AudioServer.AddBusEffect(bus, new AudioEffectHardLimiter { CeilingDb = -1.5f, Release = 0.2f });
    }

    /// <summary>Each look of the story's sound (StorySounds.Look): the Thresher's bus tuned for the chapter.</summary>
    partial void LookThresher(Vector3 listener, int section) => TuneThresherBus();

    /// <summary>
    /// Each frame of section 2 (Game.StoryStealth): his state's sounds as it changes, and the ping repeating while he is wary,
    /// every 0.8 s at the first doubt down to every 0.3 s as he grows sure. Null: no Thresher under section 2's rule.
    /// </summary>
    public void FollowThresher(StalkerLook? look, double delta)
    {
        _clock += delta;
        if (look is not { } him)
        {
            _heardState = null;
            return;
        }
        var eye = ActorView.World(him.Position) + Vector3.Up * 9;
        if (_heardState != him.State)
        {
            var was = _heardState;
            _heardState = him.State;
            if (was is not null)
                StateSound(was.Value, him.State, eye);
            _pingAt = _clock + 0.5;
        }
        if (him.State == StalkerState.Wary && _clock >= _pingAt)
        {
            StompVoice("thresher_ping", eye, -2 + 4 * him.Suspicion, 6000);
            _pingAt = _clock + Mathf.Lerp(0.8f, 0.3f, Mathf.Clamp((him.Suspicion - 0.25f) / 0.75f, 0, 1));
        }
    }

    // What a change of his state sounds like (design-stealth §4's table).
    private void StateSound(StalkerState was, StalkerState now, Vector3 eye)
    {
        switch (now)
        {
            case StalkerState.Dwell:
                StompVoice("searchlight_servo", eye, -6, 6000);
                break;
            case StalkerState.Wary when was is StalkerState.Calm or StalkerState.Dwell:
                StompVoice("thresher_halt", eye, 0, 6000);
                StompVoice("thresher_ping", eye, -2, 6000);
                StompVoice("thresher_focus", eye, -4, 6000);
                StompVoice("thresher_scan", eye, -8, 6000);
                break;
            case StalkerState.Draw:
                StompVoice("thresher_lock", eye, 0, 8000);
                StompVoice("thresher_blackout", eye, -2, 8000);
                break;
            case StalkerState.Lost:
                StompVoice("thresher_winddown", eye, -2, 6000);
                break;
            case StalkerState.Calm when was is StalkerState.Wary or StalkerState.Lost:
                StompVoice("thresher_breath", eye, -4, 4000);
                break;
        }
        audio.Trace($"thresher {StoryStalker.Word(was)} -> {StoryStalker.Word(now)}");
    }
}
