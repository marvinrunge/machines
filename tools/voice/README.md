# Story voices

The prologue's spoken lines (`lines.json`) are recorded by people and turned into robot voices
by `robot.py`, an ffmpeg filter chain. Until a line is recorded, a placeholder spoken by
Windows' own SAPI voice (through the same filter) stands in, and the subtitles always work.

Output: `game/assets/story/voice/<lang>/<id>.ogg` (Ogg Vorbis, mono, 48 kHz, -18 LUFS, peaks
at most -1.5 dBFS) and `game/assets/story/voice/manifest.json` (per line and language: file,
seconds, preset, and `placeholder: true` for SAPI lines, `false` for recordings).

## Recording lines

1. Record each line on its own, in a quiet room, close to the microphone (a hand's width),
   without effects. Speak naturally in the speaker's character; the filter makes the robot.
   - **ship**: calm, friendly, a little slow, like a station announcement.
   - **auto**: flat and even, no emotion at all, every word the same weight.
   - **ike**: brash and quick, a grin in the voice.
   - **bob**: short, dry, a soldier reporting.
   - **whisper**: whispered or breathy, slow, close to the microphone.
   - **l7**: the Locator thinking: quiet, close, precise, a little lost; thin and a little hoarse.
     The pauses "…" are about 0.8 s.
   - Lines marked `"cut": true` (`P_ANN_C5` to `P_ANN_C8`) break off mid-word: stop sharply.
2. Save as **WAV, 48 kHz, mono (or stereo), 16 or 24 bit**, named by the line id, one folder per
   language:

   ```
   tools/voice/recordings/de/P_ANN_WAKE_1.wav
   tools/voice/recordings/en/P_ANN_WAKE_1.wav
   tools/voice/recordings/de/P_IKE_WAKE.wav
   ```

   Silence before and after is trimmed automatically; leave about half a second. Peaks should
   stay below about -3 dBFS when recording (no red light); the level is normalised afterwards.
   The `recordings/` folder is not committed (raw takes are large and private): keep a backup.
3. Run the batch (Python from the Google Cloud SDK on this machine; plain `python` works too):

   ```
   python tools/voice/robot.py --batch            # both languages
   python tools/voice/robot.py --batch --lang de --only P_IKE_WAKE,P_BOB_WAKE
   ```

   Every recording found replaces its placeholder; lines without a recording keep theirs.
   A recording is processed again when the file changes (its time stamp) or with `--force`.
4. Commit the changed `.ogg` files and `manifest.json`.

To try a preset on any file: `python tools/voice/robot.py ike take3.wav test.ogg [--pitch 0.95] [--cut]`
(`--pitch` shifts on top of the preset, for example 0.9 for a high natural voice that should sound
lower). `python tools/voice/robot.py --list` names the presets.

## Presets

| Preset | Used by | Sound |
|---|---|---|
| `ship` | the PA | band-pass 300-3400 Hz, compressor, faint 173 Hz ring-mod shimmer, chorus, hall |
| `ship-dry` | `P_ANN_FOUND` | the same voice from a speaker right beside you: no hall, small room |
| `ship-old` | `P_ANN_CRASH` | an old, damaged recording: narrow band, tape wobble, crush, hiss |
| `auto` | the automatic voice | half monotone vocoder buzz (phase-zeroed FFT), bitcrush, 380-3000 Hz |
| `ike` | Ike | pitch x1.08, 62 Hz ring modulation, soft-clip grit, exciter, short slap |
| `bob` | Bob | pitch x0.86, metallic comb filter (3/6/9 ms), presence peak, 85 ms echo |
| `bob-radio` | `P_BOB_GLITCH` | bob over a breaking radio: telephone band, crush, dropouts, hiss |
| `whisper` | the update | pitch x0.82, whispered (random-phase FFT), reversed reverb swelling into the words |
| `l7` | the Locator's thoughts (`P_L7_`) | pitch x0.97, a faint monotone buzz and a breath of whisper under the voice, light crush, close and dry |

The game adds the room on top later (announcer bus, 3D speakers), so the presets stay fairly
dry apart from the PA's hall.

## Placeholders

`python tools/voice/placeholders.py` speaks every voiced line with the SAPI voices installed
(here Hedda, de-DE, and Zira, en-US, both female; the robots are pitched down per speaker before the
preset) through `sapi.ps1`, filters it and writes the Ogg files with `placeholder: true`. Reruns redo
only lines whose text, preset or tuning changed; recorded lines are never touched.
