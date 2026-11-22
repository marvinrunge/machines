# Story voices

The prologue's spoken lines (`lines.json`) are turned into robot voices by `robot.py`, an ffmpeg
filter chain. Each line's voice comes from one of three sources, the first one there is winning:

1. **a recording** by people (`tools/voice/recordings`, `robot.py --batch`),
2. **a Google voice** (Chirp 3 HD, `google_tts.py`) for the speakers cast in `voices.json`: Ike (Sadachbia), Bob
   (Alnilam), L-7 (Iapetus), picked by ear from the sampler,
3. **a SAPI stand-in** spoken by Windows' own voice (`placeholders.py`) for everyone else (the ship's PA,
   the automatic voice, the whisper).

All three go through the same preset of the line, and the subtitles always work.

## Names

`pronounce.json` says how names are spoken: whole-word replacements applied only to the text a speech engine gets
(Google and SAPI alike), never to the subtitles. In German Ike is said "Eik" and L-7 "Ell sieben" (also "Ikes",
"L-7s"); in English L-7 goes to the engine as "L seven" and Ike stays "Ike". A new name or form goes into the table;
`google_tts.py` and `placeholders.py` then voice again exactly the lines whose spoken text changed. A recording of a
line should say the names the same way.

Output: `game/assets/story/voice/<lang>/<id>.ogg` (Ogg Vorbis, mono, 48 kHz, -18 LUFS, peaks
at most -1.5 dBFS) and `game/assets/story/voice/manifest.json` (per line and language: file,
seconds, preset and `source`: `recording` (`placeholder: false`), `google` (`placeholder: false`,
with the `voice` name) or `sapi` (`placeholder: true`)).

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

   Every recording found replaces the Google or SAPI voice of its line; lines without a recording keep theirs.
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
| `l7` | the Locator's thoughts (`P_L7_`) | two semitones up (x1.089, formants kept), a monotone buzz, a faint 95 Hz ring modulation, a short metallic comb and a breath of whisper under the voice, light crush, close and dry |
| `plain` | comparisons, spoken labels | no robot: only trimmed and levelled |

The game adds the room on top later (announcer bus, 3D speakers), so the presets stay fairly
dry apart from the PA's hall.

In the game the voices go through a gentle compressor with 4 dB of makeup on the story's `Voice` bus, and speech ducks
the music and the world (`StoryAudio.BuildBuses`). To check the mix, `MACHINES_STORY_STEMS=<folder>` records the music
(before and after the duck), the world and the voices of a story run on their own, with each line's start and end
(`StoryStems`), for measuring each line against the music in the speech band.

## Google voices

`python tools/voice/google_tts.py` voices every line of the speakers cast in `voices.json` with Google
Cloud Text-to-Speech (Chirp 3 HD) in German and English, runs it through the line's preset (as it is: the
presets were made for human voices, and Chirp voices are human-like; no SAPI pre-pitching) and writes the
Ogg files with `"source": "google"`. It uses the gcloud login and its project (`gcloud auth
print-access-token`, as `tools/story-book/voice.py` does) and only the Text-to-Speech API; the text goes to
Google. Chirp 3 HD has 1 M free characters a month; all of Ike's, Bob's and L-7's lines in both languages
are about 3.7 k.

```
python tools/voice/google_tts.py --dry-run                 # what would be voiced, and how many characters are new
python tools/voice/google_tts.py                           # all cast speakers, both languages
python tools/voice/google_tts.py --speaker l7 --lang de --only P_L7_BOOT_VOICE
python tools/voice/google_tts.py --force                   # redo all (from the cache: free)
python tools/voice/google_tts.py --fresh --only P_IKE_WAKE --lang de   # a new take of a line (billed)
python tools/voice/google_tts.py --sampler                 # candidate voices for picking by ear
python tools/voice/google_tts.py --say de-DE-Chirp3-HD-Puck "Wen hauen wir um?" try.ogg --preset ike
```

- **Cast** (`voices.json`): per speaker the voice per language, `rate` (speakingRate; Chirp 3 HD has no
  pitch setting, the presets pitch), `pause` (seconds a "…" inside a line lasts: 0.8 for L-7, 0.6 for Ike
  and Bob), the sampler's `candidates` and `sampler` lines. A line in `lines.json` can set its own
  speakingRate per language, `"rate": {"en": 0.8}` (L-7's English `P_L7_BOOT_GAP`, which Chirp spoke faster than
  its other thoughts). Ike's `gap` (0.7) is the longest silence left between his sentences: Sadachbia's German
  otherwise waits up to 1.7 s, too slow for him. A preset variant (`bob-radio`) uses its
  speaker's voice. A speaker taken out of the cast goes back to SAPI with `placeholders.py --force --only …`.
- **Pauses**: a "…" goes to Google as a `[pause long]` markup tag (about 1.2-1.5 s); the gap it leaves is
  then set to the cast's `pause`. When Chirp runs short fragments together anyway (Ike's "Ich bin … kurz …
  weg …"), the plain text with "..." is tried, then the parts are voiced one by one and joined with the pause.
- **Broken-off words** ("kor- …", "Schalte a-") are spoken as the fragment and cut 70 ms before the end of
  their sound; lines marked `"cut": true` lose the preset's tail as before.
- **Cache**: every answer from Google stays in the main checkout's `tools/voice/cache/google` (not
  committed: `tools/voice/cache/` holds its own `.gitignore` with `*`, which the tool writes, so it stays
  ignored on any branch; worktrees use the main checkout's too, so it outlives them), keyed by text, voice and settings,
  so reruns cost nothing. Chirp gives a different take on every call, so the cache is also what keeps a line
  the same: do not delete it. For a new take of a line run `--fresh --only ID [--lang xx]`, which drops that
  line's cached answers and asks Google again (deleting a cache file alone does nothing: the line is up to
  date by its hash and is skipped). Changing a line's text, voice or rate also gives a new take.
- **Characters**: `--dry-run` counts the characters of each line's markup not cached yet, and in brackets
  the worst case: for a line with a "…" inside, the plain-text and part-by-part fallbacks are billed too
  when Chirp runs the fragments together (up to about 2 to 3 times that line's characters).
- Requests ask for -6 dB (`volumeGainDb`): some Chirp voices otherwise come back clipped at full scale.
- Reruns redo only lines whose text, voice, settings or preset changed (hash in the manifest); lines with a
  recording (in the manifest or in `recordings/`) are skipped.

`--sampler` writes, for each cast speaker, `<speaker>-stimmen.mp3` (each candidate voice, announced
unfiltered by de-DE-Chirp3-HD-Orus, "Stimme eins: Fenrir.", then the speaker's `sampler` lines in German
through its preset, 0.6 s apart, 1.5 s between voices) and `<speaker>-roh-gegen-robot.mp3` (the cast voice's
first sampler line without and with the filter), at about -18 LUFS, to the main checkout's
`renders/prologue/stimmen` (`--out` for another folder; not committed).

## Placeholders

`python tools/voice/placeholders.py` speaks every voiced line with the SAPI voices installed
(here Hedda, de-DE, and Zira, en-US, both female; the robots are pitched down per speaker before the
preset) through `sapi.ps1`, filters it and writes the Ogg files with `placeholder: true`. Reruns redo
only lines whose text, preset or tuning changed; recorded lines are never touched, nor Google lines of a
speaker still cast in `voices.json`.
