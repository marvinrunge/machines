using Godot;

namespace Machines.Game;

/// <summary>
/// The music's own check, MACHINES_STORY_AUDIO_TEST=music (with MACHINES_STORY=prolog and MACHINES_MAP=prolog): stage 7's
/// rules one after the other, fed as the director's cues would come (<see cref="StoryMusic.Hear"/>), the section and the
/// countdown set as if the trio were there. Once a second it traces what plays, the playlist's level, the low-pass and the
/// hush, so the log shows each rule at work: the duck under the Thresher (far, steps, near with the low-pass, locked on,
/// 8 s and back over 6 s), a staged silence, the armoury (threat dips, a wave, a pause, the boss's loud part, its fall's
/// rest and the playlist back), the Notgang (playlist out), Pressure from the burst, J1's hush, the hold's loop and the
/// launch's fade.
/// </summary>
public partial class StoryMusic
{
    private (float Metres, string State)? _rehearsedThresher;

    /// <summary>The check's steps, handed to the audio test's timeline; its end comes at the returned second.</summary>
    public double Rehearse(Action<double, string, Action> at)
    {
        void Hear(string words) => HearHere(words.Split(' '));
        void Thresher(float metres, string state)
        {
            _rehearsedThresher = (metres, state);
            Hear($"thresher state {state}");
            Hear(FormattableString.Invariant($"thresher near {metres}"));
        }
        at(0.0, "section 2, the Thresher far", () =>
        {
            SectionOverride = 2;
            Cue(null);
            SetThreat(0);
            Thresher(200, "calm");
        });
        at(4.0, "his steps audible (90 m)", () => Thresher(90, "calm"));
        at(8.0, "near (30 m)", () => Thresher(30, "calm"));
        at(11.0, "locked on", () => Thresher(30, "draw"));
        at(12.0, "firing", () => Thresher(30, "fire"));
        at(14.0, "lost, 150 m (8 s hold, then back over 6 s)", () => Thresher(150, "lost"));
        at(24.0, "staged silence (hush 1.5 s -60 dB)", () => Hear("hush 1.5 -60"));
        at(27.0, "section 6, hunted (threat dips)", () =>
        {
            _rehearsedThresher = null;
            SectionOverride = 6;
            SetThreat(2);
        });
        at(30.0, "wave 1 alive (no dips)", () => Hear("wave 1 alive"));
        at(34.0, "wave pause (-10 dB)", () => Hear("wave pause"));
        at(39.0, "wave 5, boss in (loud part)", () =>
        {
            Hear("wave 5 alive");
            Hear("boss in");
        });
        at(47.0, "boss down (2 s rest, playlist back)", () => Hear("boss down"));
        at(54.0, "section 7, the Notgang (playlist out)", () =>
        {
            SetThreat(null);
            SectionOverride = 7;
            CountdownOverride = 240;
        });
        at(57.0, "lift burst (score escape 234)", () =>
        {
            CountdownOverride = 234;
            Hear("score escape 234");
        });
        at(60.0, "J1 (hush 1.5 s -20 dB)", () => Hear("hush 1.5 -20"));
        at(63.0, "Pressure 4 s before the hold's end", () => EscapeAt(StoryScore.HoldTo - 4));
        at(71.0, "launch", () => Hear("launch"));
        at(72.0, "the pod's cutscene: music out over 6 s", () => Out(6));
        for (var second = 0; second < 80; second++)
            at(second + 0.5, "", RehearsalLook);
        return 80;
    }

    private void RehearsalLook()
    {
        // The Thresher's distance comes as the view would send it, more often than it lapses.
        if (_rehearsedThresher is { } t)
            HearHere(["thresher", "near", t.Metres.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        var list = _list?.Player is { } player ? FormattableString.Invariant($"{player.VolumeDb:0.0} dB") : "-";
        var playing = ScorePlaying is { } p ? FormattableString.Invariant($"{p.Piece} {p.At:0.00} s") : "nothing";
        audio.Trace($"check {playing}, playlist {list}, low-pass {_lowPass?.CutoffHz ?? 0:0} Hz, hush {_hushAmp?.VolumeDb ?? 0:0.0} dB");
    }
}
