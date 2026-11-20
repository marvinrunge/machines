# Story music

No music has been made for the story yet (ElevenLabs refused music generation with the current key).
Until it has, `StoryMusic` (game/scripts/StoryMusic.cs) plays a stand-in for each cue: ambience loops of
`../sfx` pitched down into a drone, with layers and pulses that come in by threat level
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
