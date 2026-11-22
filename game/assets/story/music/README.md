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
| `lost_in_the_dark_loud.ogg` | 4 Lost In The Dark, 157.195–219.10 s (the entry's transient at 157.211 s), 15 ms fade-in, its own fade-out | 61.90 s | -14 LUFS | the break-in (Erwischt), once |
| `pressure_at_sector_nine.ogg` | 5 Pressure At Sector Nine, 0–179.90 s | 179.90 s | -15 LUFS | the escape, from the crash countdown |

The playlist runs from the start in that order (a 2 s overlap, round again after the last), is not restarted by sections or
checkpoints, and draws back a little while the trio is hunted (-1.5 dB uneasy, -4 dB hunted) and after the core (-3 dB).
The break-in cuts it (0.3 s) for the loud part; it goes on where it was once the loud part has ended and the trio is out
of the core chamber. Pressure starts with the countdown at `length - seconds left` (0 at 180 s, 29.9 s at a checkpoint's
150 s), so its last note falls just before zero; the launch keeps it, and it fades out over the pod cutscene's first 6 s.

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
