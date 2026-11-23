using Godot;

namespace Machines.Game;

/// <summary>
/// MACHINES_STORY_AUDIO_TEST=1 (with MACHINES_STORY=prolog and MACHINES_MAP=prolog, which skips the menus): once the chapter is shown, a short sequence through every part
/// of the story's sound — the bay's ambience and the calm music, a PA announcement with its gong and loudspeakers, a radio
/// line that waits for it, a clamp in the world, the threat rising with the Thresher's steps, the silence before a scare,
/// "Gefunden." beside the Locator, a radio line while hiding (muffled world), the deck's hall and the alarm, a fragment
/// of the PA cut off with a crackle, the update's whisper and the music falling silent. Each sound is traced
/// (<see cref="StoryAudio.Trace"/>), the trace is written to MACHINES_STORY_AUDIO_TEST_LOG (default
/// <c>user://story-audio-test.log</c>), and the game quits; with <c>--write-movie</c> the movie has it all.
/// </summary>
public partial class StoryAudioTest(StoryAudio audio) : Node
{
    private readonly List<string> _log = [];
    private readonly List<(double At, string What, Action Do)> _steps = [];
    private double? _start;
    private int _next;

    public void Note(string line) => _log.Add(line);

    public override void _Ready()
    {
        Name = "StoryAudioTest";
        // MACHINES_STORY_AUDIO_TEST=music: stage 7's music rules instead (StoryMusic.Rehearsal).
        if (System.Environment.GetEnvironmentVariable("MACHINES_STORY_AUDIO_TEST") == "music")
        {
            At(audio.MusicCues.Rehearse(At), "end", Finish);
            _steps.Sort((a, b) => a.At.CompareTo(b.At));
            return;
        }
        Vector3 Ahead(float metres, float right = 0)
        {
            var camera = GetViewport()?.GetCamera3D();
            var forward = camera is null ? Vector3.Forward : -camera.GlobalBasis.Z with { Y = 0 };
            var side = camera is null ? Vector3.Right : camera.GlobalBasis.X with { Y = 0 };
            return audio.Listener + forward.Normalized() * metres + side.Normalized() * right;
        }
        At(0.0, "bay ambience, calm music", () =>
        {
            StoryAudio.SetAmbience("R1");
            // The section's music: the score's playlist where its files are there, else the cue's stand-in.
            StoryAudio.Cue(null);
            StoryAudio.SetThreat(0);
        });
        At(1.0, "PA line", () => StoryAudio.Say("P_ANN_WAKE_1"));
        At(1.5, "radio line behind it", () => StoryAudio.Say("P_IKE_WAKE"));
        At(2.5, "clamp in the world", () => StoryAudio.Play("clamp_release", Ahead(3, 2)));
        At(12.0, "uneasy", () => StoryAudio.SetThreat(1));
        At(12.5, "Thresher steps", () => StoryAudio.Play("thresher_step", Ahead(18, -6)));
        At(13.4, "Thresher steps", () => StoryAudio.Play("thresher_step", Ahead(16, -5)));
        At(14.0, "hunted", () => StoryAudio.SetThreat(2));
        At(15.0, "silence before a scare", () => StoryAudio.Hush(1.5f));
        At(16.5, "Gefunden.", () =>
        {
            StoryAudio.Sting("sting_found");
            StoryAudio.Say("P_ANN_FOUND");
        });
        At(18.5, "hiding", () => StoryAudio.SetMuffled("hiding", true));
        At(19.0, "radio while hiding", () => StoryAudio.Say("P_BOB_CONTACT_L"));
        At(21.5, "out of hiding, deck A, alarm", () =>
        {
            StoryAudio.SetMuffled("hiding", false);
            StoryAudio.SetAmbience("R4");
            StoryAudio.SetLayer("amb_alarm_klaxon", true, -12);
            StoryAudio.SetThreat(1);
            // Erwischt: the score's loud part over the cut playlist.
            StoryAudio.Caught();
        });
        At(23.0, "PA fragment", () => StoryAudio.Say("P_ANN_C5"));
        At(24.2, "PA cut off", () => StoryAudio.CutVoice());
        At(25.5, "whisper", () => StoryAudio.Say("P_WHISPER_1"));
        At(30.5, "music off, alarm off", () =>
        {
            StoryAudio.Cue("silence");
            StoryAudio.SetLayer("amb_alarm_klaxon", false);
        });
        At(33.5, "end", Finish);
    }

    private void At(double seconds, string what, Action action) => _steps.Add((seconds, what, action));

    public override void _Process(double delta)
    {
        if (_start is null)
        {
            if (!audio.Shown())
                return;
            _start = audio.Clock;
            audio.Trace($"test start");
        }
        while (_next < _steps.Count && audio.Clock - _start >= _steps[_next].At)
        {
            var (_, what, action) = _steps[_next++];
            if (what.Length > 0)
                audio.Trace($"test {what}");
            action();
        }
    }

    private void Finish()
    {
        var path = System.Environment.GetEnvironmentVariable("MACHINES_STORY_AUDIO_TEST_LOG") is { Length: > 0 } asked ? asked : "user://story-audio-test.log";
        var real = path.StartsWith("user://", StringComparison.Ordinal) || path.StartsWith("res://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
        System.IO.File.WriteAllLines(real, _log);
        GD.Print($"story audio test: {_log.Count} lines written to {real}");
        // Every story sound stops, and the mixer lets go of them, before the game quits.
        foreach (var player in audio.FindChildren("*", nameof(AudioStreamPlayer), true, false).OfType<AudioStreamPlayer>())
            player.Stop();
        foreach (var player in audio.FindChildren("*", nameof(AudioStreamPlayer3D), true, false).OfType<AudioStreamPlayer3D>())
            player.Stop();
        GetTree().CreateTimer(0.3).Timeout += () => GetTree().Quit();
    }
}
