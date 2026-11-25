# Story music

## The prologue's score

The prologue plays the user's own pieces (`StoryScore` in game/scripts/StoryAudioPlan.cs, played by
game/scripts/StoryMusic.cs). Made from their MP3s (44.1 kHz stereo) with ffmpeg: Ogg Vorbis at quality 5, silence at the ends
trimmed, each levelled by a static gain (a limiter at -1 dBFS for safety) to one loudness. The MP3s are not in the repository.

| File | From | Length | Loudness | Plays |
|---|---|---|---|---|
| `silent_circuit_v1.ogg` | Silent_Circuit_v1, 0.30–156.30 s | 156.00 s | -19 LUFS | the stealth 1, from the start |
| `silent_circuit_v2.ogg` | Silent_Circuit_v2, 0.30–168.32 s | 168.02 s | -19 LUFS | the stealth 2 |
| `lost_in_the_void.ogg` | 1 Lost In The Void, 0.30–147.95 s | 147.65 s | -19 LUFS | playlist 1 (after the break-in) |
| `lost_vessel.ogg` | 2 Lost Vessel, 1.10–149.62 s, its loud middle ridden down 6 dB (30–34 s down, 117–123 s up) | 148.52 s | -19 LUFS | playlist 2 |
| `drift_into_the_black.ogg` | 3 Drift Into The Black, 0.60–239.62 s | 239.02 s | -19 LUFS | playlist 3 |
| `lost_in_the_dark_calm.ogg` | 4 Lost In The Dark, 1.00–157.20 s, faded out over its last 2.5 s | 156.20 s | -19 LUFS | playlist 4 |
| `lost_in_the_dark_loud.ogg` | 4 Lost In The Dark, 157.195–219.10 s (the entry's transient at 157.211 s), 15 ms fade-in, its own fade-out | 61.90 s | -14 LUFS | the break-in (Erwischt) |
| `static_dread_v2.ogg` | Static_Dread_v2, 0.50 s to its end | 229.01 s | -15 LUFS | the armoury's waves and their pauses |
| `static_dread_v1.ogg` | Static_Dread_v1, 0.35 s to its end | 183.92 s | -14 LUFS | the armoury's boss, its body looped until he falls |
| `circuitry_v1.ogg` | Circuitry_v1, 0.35 s to its end | 187.00 s | -15 LUFS | the escape, from the lift's burst, its last 32 bars looped until the launch |

The new pieces (2026-11-24) were cut and levelled the same way (static gains of -3.2 to -4.7 dB, no sample near the limiter;
peaks -3.7 to -5.4 dBFS). Pressure At Sector Nine, the escape's piece before, is gone from the score and the folder.

The stealth's two pieces run from the start, one after the other (a 2 s overlap, round again after the second), not
restarted by sections or checkpoints, until the break-in cuts them (0.3 s) for the loud part. Once that has ended and the
trio is out of the core chamber, the old playlist follows from its start in its order, Lost In The Void to Lost In The
Dark (calm), round again after the last. Both draw back a little while the trio is hunted (-1.5 dB uneasy, -4 dB hunted),
the playlist also after the core (-3 dB). A chapter begun at a checkpoint from Erwischt (7) on starts with the playlist;
one begun at the antechamber (6) with Silent Circuit v2; each list goes on where it was when a checkpoint is loaded after
a fall.

### Stage 7

- **Under the Thresher (section 2)** Silent Circuit is ducked, never cut (`StoryDuck`): -6 dB with him beyond 120 m or the
  Locator in a duct, R5, R7 or the Leitstand (A1); -10 dB while his steps are heard; -18 dB and a 1.2 kHz low-pass on the
  Music bus within 40 m or while he is wary, reached in 2 s; -24 dB while he draws or fires. A deeper level comes at once;
  the music comes back 8 s after the danger last held, over 6 s. These levels replace the threat's dips there.
- **The armoury (section 6):** with the first wave's cue (or a pause's) Static Dread v2 takes the playlist's place (the
  playlist gives way over 2 s; Static Dread starts from its beginning, round again should the waves outlast it). While a
  wave is alive it plays at the after-core level (-3 dB) without the threat's dips; in the pauses it drops to -10 dB, and
  the lift's hum and the batch bar's tick carry the quiet. As the boss bursts through gate N it is cut (0.3 s) for
  Static Dread v1, which plays from its beginning and loops its body (below) until he falls. His fall cuts the music to
  2 s of silence, then the old playlist comes back where it was.
- **The escape (section 7):** at the Notgang (C0) the playlist fades out over 2 s and never comes back. Circuitry v1 starts
  from its beginning when the lift bursts (cue `score escape`, T-234 of 240), plays through and loops its hold (below)
  until the launch; the pod's cutscene fades it out over its first 6 s as before. Without the burst's cue it starts by the
  countdown, 6 s after the countdown began (it waits 2 s more for the cue, then starts 2 s in). A retry from Galerie (13)
  starts it 64 s in, from Bucht (14) 114 s in (where a run usually hears it there: 234 minus the checkpoint's seconds
  left), both past their end wrapped into the hold. The Notgang checkpoint (12) waits for the burst again.
- **Hushes** dip the music for a moment and bring it back, never stop it: J1 (1.5 s, -20 dB), the bridge (1 s, -20 dB),
  and the stealth's two staged silences (the window's pause 1.5 s, the 2 s after H slams; -60 dB is silence).
- The cue stand-ins no longer play fake Thresher steps: steps that are not his would ruin finding him by ear.

The music reads these cues (`StoryMusic.Hear`, called first in `Game.StoryCue` with its words, so every cue of a real game reaches it; every other cue is left to its
section):

| Cue | Sent by | Does |
|---|---|---|
| `thresher near <m>` | S | his distance; counts over the view's own distance for 1.5 s, so it is sent at least once a second |
| `thresher state <calm\|dwell\|wary\|draw\|fire\|lost>` | S | his state for the duck (`dwell` is no steps; unknown words read as calm) |
| `wave <n> alive` / `wave pause` | W | Static Dread v2 and its levels |
| `boss in` / `boss down` | W | Static Dread v1 / 2 s of silence, then the playlist |
| `score escape <s>` | E | Circuitry's anchor, the lift's burst with s seconds left |
| `hush <s> <dB>` | S, E | the music dips by dB for s seconds |
| `launch` | E | the hold lets go (should the cutscene's fade not cover it, the piece plays out to its end) |

### The holds (loop points)

Measured from the files, not by ear (no listening was possible when they were built, and no librosa on this machine): an
onset envelope (log energy and its high-passed twin, rises only), its autocorrelation for the tempo, the beat grid's
phase by the sum of onsets on it, the downbeat by the low band's rises, then the level of each bar for the sections. Times
are in the trimmed `.ogg`.

- **Circuitry v1 (the escape): 173.7 BPM**, the first beat at 0.049 s, bars of 1.3817 s. The level of each bar shows its
  sections: a drop at bar 52 (72 s), a break at bars 85–89 (118–125 s), a dip at bar 126 (174 s), and the fade from bar
  132 (**182.433 s**, -12 dB to -25 dB and down). The hold is **bars 100–132, 138.219–182.433 s (32 bars, 44.21 s)**: it
  starts in the piece's last third and ends on the fade's bar line, so the fade plays only if the launch comes exactly
  then. From the burst the piece reaches the hold's end after 182 s; a run's last 52 s at most loop it, little more than
  once. Godot's beat loop: `bpm` 173.65 and `beat_count` 528 (528 beats end exactly at 182.433 s), `loop_offset` 138.219.
- **Static Dread v1 (the boss): 70 BPM** (or 140 counted in halves), the first beat at 0.056 s, bars of 3.4286 s. Its intro
  runs to bar 8 (27.485 s), where the loud body comes in; it breaks down at bar 48 (164.627 s) before its end. The hold is
  **bars 8–48, 27.485–164.627 s (40 bars, 137.14 s)**, both ends on loud bars; a boss fight rarely reaches it. Godot's beat
  loop: `bpm` 69.98 and `beat_count` 192, `loop_offset` 27.485.

**Please check them by ear:** `MACHINES_STORY_AUDIO_TEST=music` plays Circuitry from 178.4 s at its 63rd second and jumps
at 182.433 s back to 138.219 s about four seconds later. If it bumps, keep both ends on bar lines
(0.0494 + 1.381693 × bar for Circuitry, 0.056 + 3.428571 × bar for Static Dread v1) and change `StoryScore.Escape` or
`StoryScore.Boss` (`HoldBeats` = the hold's end over the beat, 0.345423 s or 0.857143 s, rounded).

How it loops: Godot's beat loop on the Ogg stream, no extra file and no `AudioStreamInteractive` (an interactive
stream's clips start at their beginning, so a clip from the hold's start would need a cut file): `StoryMusic` sets `loop`,
`bpm`, `beat_count` and `loop_offset` from the piece (`StoryPiece.HoldFrom`, `HoldTo`, `HoldBeats`) while it plays a held
piece. The `.import` keeps `loop=false`; the lists and the loud part are played once. Should a stream ever not loop by
beats, `StoryMusic` seeks back itself once the position passes the hold's end.

### Mix check (stage 7)

`MACHINES_STORY_AUDIO_TEST=music` with `MACHINES_STORY_STEMS` (headless, 80 s through every rule): the music stem peaks at
-3.7 dBFS (the loud part about -6, Pressure about -4, before the music fader at -3.1 dB; measured with Pressure, before the 2026-11-24 pieces, whose files peak no higher), the world stem at -1.0 dBFS; no
music or world sample reaches full scale. The voice stem has one transient at +0.1 dBFS (6 samples at the start of
P_L7_BOOT_EYES, before the StoryMix bus's hard limiter at -1 dBFS, which catches it): a voice file's level, not the
music's.

## The cues and their stand-ins

Other chapters (and the prologue, should its score be missing) use cues: `StoryMusic` plays a stand-in for each cue,
ambience loops of `../sfx` pitched down into a drone, with layers and pulses that come in by threat level
(`StorySoundscape.MusicStandIn` in game/scripts/StoryAudioPlan.cs).

A real piece takes a cue's place as soon as its file is here, no code change needed:

| File | Plays |
|---|---|
| `<cue>.ogg` | the cue's base, always (looped) |
| `<cue>_1.ogg` | a stem in step with the base, faded in at threat 1 (uneasy) and up |
| `<cue>_2.ogg` | a stem in step with the base, faded in at threat 2 (hunted) |

Cues: `wartung` (sections 1-3), `jagd` (4-5), `gegenwehr` (6), `absturz` (7); `silence` has no file.
Stems must have the base's length and tempo (they play as one `AudioStreamSynchronized`). Ogg Vorbis,
stereo, 44.1 or 48 kHz, about -18 LUFS; commit the `.import` file Godot writes (set `loop=true` there or
leave it to the code, which loops them).
