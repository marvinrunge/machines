using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// Package W's cues in the view (plan stufe7 §4 W, design-waves §8): the lockdown's bolts and klaxon, gate N's grind, strain
// and burst, the heavy Grunt's rev, crash and stun, the strips, mounts and mends, the power dip at the end; the salvage
// kept in the host's story save as it happens (StoryProgress.AddSalvaged). The music's cues (wave <n> alive, wave pause,
// boss in, boss down) are package M's.
public partial class Game
{
    /// <summary>A director's cue of this package's section (Game.StoryCue forwards what it does not know); false: not its cue.</summary>
    private bool StoryCueArmoury(string[] words, Vector3 at)
    {
        ActorView.ArmouryWorld = World;
        switch (words)
        {
            case ["armoury", "hint", var hint]:
                // Strip, mount and mend are the Locator's to do: its player alone is told.
                if (World.Story is { } run && Me == run.Locator)
                    _hud.Story?.Hint(hint);
                return true;
            case ["lockdown"]:
                StoryAudio.Play("lockdown_klaxon", at);
                _screen.Interference(0.15f, 0.4f);
                return true;
            case ["wave", "early", _]:
                StoryAudio.Play("bar_tick");
                return true;
            case ["barricade", "hit", _]:
                return true;
            case ["gate", "N", "lights"]:
                StoryAudio.Play("lift_hum", at);
                return true;
            case ["gate", "N", "open"]:
                StoryAudio.Play("gate_grind", at);
                return true;
            case ["gate", "N", "strain", var step]:
                StoryAudio.Play(step switch { "1" => "gate_strain_1", "2" => "gate_strain_2", _ => "gate_strain_3" }, at);
                CameraRig.Shake(at, 2 + 1.5f * (step[0] - '0'), 0.6f);
                _effects.SparkShower(at + Vector3.Up * 4, Vector3.Down, 30);
                return true;
            case ["gate", "N", "burst"]:
                StoryAudio.Play("gate_burst", at);
                CameraRig.Shake(at, 10, 1);
                _effects.Explosion(at + Vector3.Up * 3, 8);
                return true;
            case ["boss", "in"]:
                StoryAudio.Play("boss_idle", at);
                return true;
            case ["boss", "rev", _]:
                StoryAudio.Play("boss_rev", at);
                _effects.ChargeDust(at, 18);
                return true;
            case ["boss", "run"]:
                _effects.ChargeDust(at, 30);
                return true;
            case ["boss", "hit", _]:
                StoryAudio.Play("rack_plough", at);
                CameraRig.Shake(at, 6, 0.5f);
                return true;
            case ["boss", "crash"]:
                StoryAudio.Play("boss_crash", at);
                CameraRig.Shake(at, 8, 0.7f);
                _effects.SparkShower(at + Vector3.Up * 3, Vector3.Up, 70);
                _effects.ChargeDust(at, 40);
                return true;
            case ["boss", "stop"]:
                _effects.ChargeDust(at, 12);
                return true;
            case ["boss", "recover"]:
                StoryAudio.Play("boss_stun", at);
                return true;
            case ["boss", "smoke"]:
                _effects.SparkShower(at + Vector3.Up * 6, Vector3.Up, 50);
                return true;
            case ["boss", "down"]:
                // A harmless burst at the wreck (no harm: the sim's boss is gone already).
                _effects.Explosion(at + Vector3.Up * 3, 10);
                CameraRig.Shake(at, 6, 0.8f);
                return true;
            case ["wreck", ..]:
                return true;
            case ["stripped", _, _]:
                StoryAudio.Play("strip_snap", at);
                _effects.SparkShower(at + Vector3.Up, Vector3.Up, 20);
                return true;
            case ["mounted", _]:
                StoryAudio.Play("mount_clunk", at);
                return true;
            case ["mended", _]:
                StoryAudio.Play("repair_hum", at);
                return true;
            case ["salvaged", var part]:
                StoryAudio.Play("strip_work", at);
                // The host keeps what was stripped at once, not only at a checkpoint (a guest's strip counts for the host's save).
                if (World.Story is { } story && Me == story.Locator && !Watching)
                {
                    var progress = StoryProgress.Load();
                    if (progress.AddSalvaged(part))
                        progress.Save();
                }
                return true;
            case ["power", "dip"]:
                StoryAudio.Play("pa_cutoff", at);
                _screen.Interference(0.25f, 0.6f);
                return true;
        }
        return false;
    }
}
