using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

// Package W's cues in the view (plan stufe7 §4 W, design-waves §8): the lockdown's bolts and klaxon, gate N's grind, strain
// and burst, the heavy Grunt's rev, crash and stun, the strips, mounts and mends, the power dip at the end; the salvage
// kept in the host's story save as it happens (StoryProgress.AddSalvaged). The music's cues (wave <n> alive, wave pause,
// boss in, boss down) are package M's. What lasts (the workbench's holds, the boss's motor and scrape, the lift's hum) is
// held each tick by Game.StoryArmoury.Follow; a looping sound is never played here as a one-shot, where it would not stop.
public partial class Game
{
    /// <summary>The armoury's doors the lockdown bolts, west, east and the corridor's.</summary>
    private const string LockdownDoors = "RTU";

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
                LockdownBolts();
                // The Notabschaltung comes on: the room's lamps dip and catch again (DeckView.Armoury), the relays clacking.
                StoryAudio.Play("thresher_blackout", at);
                DipLights(at, 1.4f, 0.2f);
                return true;
            case ["wave", var wave, "alive"] when int.TryParse(wave, out var batch) && batch < StoryWaves.Plan.Count:
                // A batch at its doors: the ship lets go of their bolts (gate N grinds open by its own cue, the boss bursts it).
                foreach (var door in StoryWaves.DoorsOf(batch).Where(d => d != 'N'))
                    if (World.Deck is { } deck && DeckDoorSpots.Middle(deck, door) is { } middle)
                        StoryAudio.Play("clamp_release", ActorView.World(middle) + Vector3.Up * 2);
                return true;
            case ["wave", "early", _]:
                StoryAudio.Play("bar_tick");
                return true;
            case ["barricade", "hit", _]:
                return true;
            case ["gate", "N", "lights"]:
                // The lift's hum behind the gate is held while the bar runs out (Game.StoryArmoury.Follow).
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
                // Its motor is held while it stands (Game.StoryArmoury.Follow).
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
                // Then the stun: pressure hissing out of the open radiator, sparks, the motor sputtering.
                GetTree().CreateTimer(0.45).Timeout += () => StoryAudio.Play("boss_stun", at);
                CameraRig.Shake(at, 8, 0.7f);
                _effects.SparkShower(at + Vector3.Up * 3, Vector3.Up, 70);
                _effects.ChargeDust(at, 40);
                return true;
            case ["boss", "stop"]:
                _effects.ChargeDust(at, 12);
                return true;
            case ["boss", "recover"]:
                // The motor comes back up.
                StoryAudio.Play("restart_whine", at, 0, 1.3f);
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
                // The seal snaps; the gun leaves the heap in sparks (Game.StoryArmoury.Follow).
                StoryAudio.Play("strip_snap", at);
                return true;
            case ["mounted", _]:
                StoryAudio.Play("mount_clunk", at);
                return true;
            case ["mended", _]:
                // Whole again: the Grunt flexes its servos (the welding hiss was held while it was mended).
                StoryAudio.Play("loc_servo", at);
                return true;
            case ["salvaged", var part]:
                StoryAudio.Play("strip_snap", at);
                StoryAudio.Play("clamp_release", at);
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
                // Section six loses its power: the room goes near dark, relays clacking, a drive winding down, and catches again.
                StoryAudio.Play("thresher_blackout", at);
                StoryAudio.Play("shutdown_whine", at, 0, 0.7f);
                DipLights(at, 3.2f, 0.04f);
                return true;
        }
        return false;
    }

    /// <summary>
    /// The lockdown's three bolts, one door after another. A door still standing open as the room seals has no leaves yet, so
    /// its "door locked" cue comes from nowhere (Then.LockDoors finds its middle by its leaves): its bolt is heard here.
    /// </summary>
    private void LockdownBolts()
    {
        if (World.Deck is not { } deck)
            return;
        var delay = 0.0;
        foreach (var door in LockdownDoors)
        {
            if (World.DoorClosed(door) || DeckDoorSpots.Middle(deck, door) is not { } middle)
                continue;
            var place = ActorView.World(middle) + Vector3.Up * 2;
            delay += 0.3;
            GetTree().CreateTimer(delay).Timeout += () => StoryAudio.Play("lockdown_bolt", place);
        }
    }

    /// <summary>The deck's lit light groups of the room at <paramref name="at"/> and the rooms touching it dip and flicker back.</summary>
    private void DipLights(Vector3 at, float seconds, float floor)
    {
        if (_deckView is null || World.Deck is not { } deck)
            return;
        var point = new System.Numerics.Vector2(at.X, at.Z);
        var room = deck.Zones.FirstOrDefault(z => z.Tags.Contains("room") && z.Contains(point));
        if (room is null)
            return;
        // Rooms touching it (a few metres of wall between) dip with it: the armoury's ramp and its corridor.
        static bool Near(Machines.Import.Model.DeckZone a, Machines.Import.Model.DeckZone b, float gap) =>
            a.Min.X - gap <= b.Max.X && b.Min.X - gap <= a.Max.X && a.Min.Y - gap <= b.Max.Y && b.Min.Y - gap <= a.Max.Y;
        var groups = deck.Lights.Where(l => deck.Zone(l.Zone) is { } zone && (zone == room || Near(zone, room, 6))).Select(l => l.Name);
        _deckView.Dip(groups, seconds, floor);
    }
}
