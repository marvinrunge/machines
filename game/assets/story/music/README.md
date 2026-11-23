# Story music

## The prologue's score

The prologue plays the user's own pieces (`StoryScore` in game/scripts/StoryAudioPlan.cs, played by
game/scripts/StoryMusic.cs). Made from their MP3s (44.1 kHz stereo) with ffmpeg: Ogg Vorbis at quality 5, silence at the ends
trimmed, each levelled by a static gain (a limiter at -1 dBFS for safety) to one loudness. The MP3s are not in the repository.

| File | From | Length | Loudness | Plays |
|---|---|---|---|---|
| `lost_in_the_void.ogg` | 1 Lost In The Void, 0.30–147.95 s | 147.65 s | -19 LUFS | playlist 1 |
| `lost_vessel.ogg` | 2 Lost Vessel, 1.10–149.62 s, its loud middle ridden down 6 dB (30–34 s down, 117–123 s up) | 148.52 s | -19 LUFS | playlist 2 |
| `drift_into_the_black.ogg` | 3 Drift Into The Black, 0.60–239.62 s | 239.02 s | -19 LUFS | playlist 3 |
| `lost_in_the_dark_calm.ogg` | 4 Lost In The Dark, 1.00–157.20 s, faded out over its last 2.5 s | 156.20 s | -19 LUFS | playlist 4 |
| `lost_in_the_dark_loud.ogg` | 4 Lost In The Dark, 157.195–219.10 s (the entry's transient at 157.211 s), 15 ms fade-in, its own fade-out | 61.90 s | -14 LUFS | the break-in (Erwischt), and again for the armoury's boss |
| `pressure_at_sector_nine.ogg` | 5 Pressure At Sector Nine, 0–179.90 s | 179.90 s | -15 LUFS | the escape, from the lift's burst, its last 16 bars looped until the launch |

The playlist runs from the start in that order (a 2 s overlap, round again after the last), is not restarted by sections or
checkpoints, and draws back a little while the trio is hunted (-1.5 dB uneasy, -4 dB hunted) and after the core (-3 dB).
The break-in cuts it (0.3 s) for the loud part; it goes on where it was once the loud part has ended and the trio is out
of the core chamber.

### Stage 7

- **Under the Thresher (section 2)** the playlist is ducked, never cut (`StoryDuck`): -6 dB with him beyond 120 m or the
  Locator in a duct, R5, R7 or the Leitstand (A1); -10 dB while his steps are heard; -18 dB and a 1.2 kHz low-pass on the
  Music bus within 40 m or while he is wary, reached in 2 s; -24 dB while he draws or fires. A deeper level comes at once;
  the playlist comes back 8 s after the danger last held, over 6 s. These levels replace the threat's dips there.
- **The armoury (section 6):** while a wave is alive the playlist holds its after-core level (-3 dB) without the threat's
  dips; in the pauses it drops to -10 dB, and the lift's hum and the batch bar's tick carry the quiet. As the boss bursts
  through gate N the loud part of Lost In The Dark plays once more from its entry (question 7: reused, no new piece); if it
  ends before he falls, the playlist comes back under the fight. His fall cuts the music to 2 s of silence, then the
  playlist comes back where it was.
- **The escape (section 7):** at the Notgang (C0) the playlist fades out over 2 s and never comes back. Pressure starts
  from its beginning when the lift bursts (cue `score escape`, T-234 of 240), plays through and loops its hold (below)
  until the launch; the pod's cutscene fades it out over its first 6 s as before. Without the burst's cue it starts by the
  countdown, 6 s after the countdown began (it waits 2 s more for the cue, then starts 2 s in). A retry from Galerie (13)
  starts it 64 s in, from Bucht (14) 114 s in (where a run usually hears it there: 234 minus the checkpoint's seconds
  left), both past their end wrapped into the hold. The Notgang checkpoint (12) waits for the burst again.
- **Hushes** dip the music for a moment and bring it back, never stop it: J1 (1.5 s, -20 dB), the bridge (1 s, -20 dB),
  and the stealth's two staged silences (the window's pause 1.5 s, the 2 s after H slams; -60 dB is silence).
- The cue stand-ins no longer play fake Thresher steps: steps that are not his would ruin finding him by ear.

The music reads these cues (`StoryMusic.Hear`, the words as `Game.StoryCue` has them; every other cue is left to its
section):

| Cue | Sent by | Does |
|---|---|---|
| `thresher near <m>` | S | his distance; counts over the view's own distance for 1.5 s, so it is sent at least once a second |
| `thresher state <calm\|dwell\|wary\|draw\|fire\|lost>` | S | his state for the duck (`dwell` is no steps; unknown words read as calm) |
| `wave <n> alive` / `wave pause` | W | the armoury's levels |
| `boss in` / `boss down` | W | the loud part again / 2 s of silence, then the playlist |
| `score escape <s>` | E | Pressure's anchor, the lift's burst with s seconds left |
| `hush <s> <dB>` | S, E | the music dips by dB for s seconds |
| `launch` | E | the hold lets go (should the cutscene's fade not cover it, the piece plays out to its end) |

### Pressure's hold (loop points)

Measured from `pressure_at_sector_nine.ogg`, not by ear (no listening was possible when it was built): librosa's beat
tracker gives **140.0 BPM**, a straight grid with its first beat at **0.531 s** (beats within 4 ms of the grid over the
loud part, 30–154 s); the strongest bar lines by novelty (MFCC, chroma and level over two bars either side) are bar 64
(110.245 s) and bar 88 (**151.388 s**, where the loud section gives way to the outro, -21 dB against -16 dB), so the
phrases are 8 bars from bar 64 on. The hold is **bars 72–88, 123.960–151.388 s (16 bars, 27.43 s)**: it starts in the
piece's last third (from 119.9 s) and ends on the outro's bar line, so the outro plays only if the launch comes exactly
then, and otherwise the launch's fade covers it.

**Please check it by ear:** play the piece from about 140 s and listen to the jump from 151.388 s back to 123.960 s
(`MACHINES_STORY_AUDIO_TEST=music` does this at its 63rd second). If it bumps, the other candidate is the last 8 bars,
**137.674–151.388 s** (bar 80; its bar before the jump sounds a little closer, 0.85 against 0.73 spectral match, but it
repeats twice as often). Change `StoryScore.HoldFrom` (and keep `HoldTo` on a bar line: 0.531 + 1.714286 × bar).

How it loops: Godot's beat loop on the Ogg stream, no extra file and no `AudioStreamInteractive` (an interactive
stream's clips start at their beginning, so a clip from 123.96 s would need a cut file): `StoryMusic` sets `loop`,
`bpm` 139.905 and `beat_count` 353 (so 353 beats end exactly at 151.388 s; the measured 140.0 would put beat 353 at
151.288 s) and `loop_offset` 123.960 on the stream while it plays the escape. The `.import` keeps `loop=false`; the
playlist and the loud part are played once. Should a stream ever not loop by beats, `StoryMusic` seeks back itself once
the position passes the hold's end.

### Mix check (stage 7)

`MACHINES_STORY_AUDIO_TEST=music` with `MACHINES_STORY_STEMS` (headless, 80 s through every rule): the music stem peaks at
-3.7 dBFS (the loud part about -6, Pressure about -4, before the music fader at -3.1 dB), the world stem at -1.0 dBFS; no
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
