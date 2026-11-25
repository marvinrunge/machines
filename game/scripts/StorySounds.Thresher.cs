using Godot;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

// Package S (plan stufe7 C20, design-stealth §9.1): the Thresher's sound on its own bus. Each plant of his feet (ActorView.Stealth
// finds them in his walk) is a Stomp: a near sample (thresher_plant_front_1..4 for a front foot, _rear_1..2 for a back one)
// cross-faded with a far one (thresher_step_1..3) by the distance, heard through the deck (StoryStalker.Occlusion: a wall or a
// high stack -6 dB and an 800 Hz low-pass, a grate -2 dB), with a sub thump within 120 m, his hydraulic breath every second
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
    /// One of the Thresher's plants at <paramref name="at"/> (a foot coming down): <paramref name="weight"/> 1 a heavy front
    /// foot, about 0.6 a lighter back one; above 1 (the hunt's gallop) louder still. Heard through the deck from the listener.
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
        var heavy = weight >= 0.8f;
        var level = mix.Db + 6 * Mathf.Log(Mathf.Max(0.2f, weight)) / Mathf.Log(2);
        var lowPass = mix.LowPassHz < StoryStalker.OpenLowPass ? mix.LowPassHz : 3000;
        if (mix.Near > 0.01f)
            StompVoice(heavy ? "thresher_plant_front" : "thresher_plant_rear", at, level + Mathf.LinearToDb(mix.Near), lowPass);
        if (mix.Near < 0.99f)
            StompVoice("thresher_step", at, level + Mathf.LinearToDb(1 - mix.Near) + (heavy ? 0 : -3), lowPass);
        if (mix.Sub)
            SubThump(-4 - 20 * distance / StoryStalker.SubWithin + (walls > 0 ? -3 : 0) + (heavy ? 0 : -4));
        _plants++;
        if (distance <= StoryStalker.BreathWithin && _plants % 2 == 0)
            StompVoice("thresher_breath", at + Vector3.Up * 7, -8 + mix.Db, lowPass);
        if (mix.Rattle && GrateBy(listener) is { } grate)
            Play("grating_rattle", grate, 0, (float)GD.RandRange(0.9, 1.1));
        var line = FormattableString.Invariant($"stomp {(heavy ? "front" : "rear")} {distance:0.0} m walls {walls} grates {grates} gain {level:0.0} dB low-pass {mix.LowPassHz:0} Hz near {mix.Near:0.00}{(mix.Sub ? " sub" : "")}");
        audio.Trace($"{line}");
        StompLog?.Invoke(line);
    }

    /// <summary>A plant's sample on the Thresher's bus, placed, at its level and low-pass (his own voices, so the story's others keep theirs).</summary>
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
                UnitSize = 15,
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
        player.PitchScale = (float)GD.RandRange(0.94, 1.04);
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
