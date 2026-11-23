"""Google voices for the prologue's robots: the lines of the speakers cast in tools/voice/voices.json
(Ike, Bob, L-7) spoken by Google Cloud Text-to-Speech (Chirp 3 HD), passed through the line's robot.py
preset and written to game/assets/story/voice/<lang>/<id>.ogg, marked "source": "google" in the manifest.

    python tools/voice/google_tts.py [--speaker ike,bob,l7] [--only ID[,ID...]] [--lang de|en] [--force] [--dry-run]
    python tools/voice/google_tts.py --fresh --only ID[,ID...] [--lang de|en]
    python tools/voice/google_tts.py --sampler [--out DIR]
    python tools/voice/google_tts.py --say VOICE "text" out.wav [--preset ike]

Order of precedence: a recording in tools/voice/recordings/<lang>/<id>.wav (robot.py --batch) wins over a
Google line, which wins over the SAPI stand-in (placeholders.py). Lines with a recording are skipped here.

The cast file voices.json names per speaker the voice in each language, its speakingRate and how long a
"…" inside a line pauses; a line of lines.json may set its own speakingRate per language ("rate": {"en": 0.8}). The text goes to Google as markup: a "…" becomes a [pause long] tag, and the
pause it leaves is then set to exactly the cast's length. Names are said as tools/voice/pronounce.json has them
(German: Ike "Eik", L-7 "Ell sieben"); only the text sent to Google changes, not the subtitles. A word broken off with "-" ("kor- …",
"Schalte a-") is spoken as its fragment and cut off sharply; lines marked "cut": true lose their reverb
tail as before. The robot presets are applied as they are (they were tuned for human recordings, and the
Chirp voices are human-like); the SAPI pre-pitching of placeholders.py does not apply.

Every answer from Google is kept in the main checkout's tools/voice/cache/google (git-ignored, shared by
every worktree, so it outlives them) under a hash of the text, the voice and the settings, so reruns and
preset changes cost no characters, and a line keeps its take (Chirp gives a different one on every call).
Reruns redo only lines whose text, voice, settings or preset changed (hash in the manifest); --force redoes
them all from the cache; --fresh drops the cached answers of the --only lines and asks Google for new takes.

--sampler voices a few lines of each speaker with each candidate voice of the cast file, robot-filtered,
with a spoken label before each voice, into <out>/<speaker>-stimmen.mp3, and the cast voice with and
without the filter into <out>/<speaker>-roh-gegen-robot.mp3 (out: the main checkout's
renders/prologue/stimmen). --say voices one text with any voice, for trying.

Auth: the gcloud login (`gcloud auth print-access-token`) and its project, like tools/story-book/voice.py.
Chirp 3 HD has 1 M free characters a month; the run prints how many characters went to Google.
"""
import argparse
import base64
import concurrent.futures as cf
import hashlib
import io
import json
import math
import os
import re
import shutil
import struct
import subprocess
import sys
import tempfile
import threading
import time
import urllib.error
import urllib.request
import wave

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import robot  # noqa: E402
import pronounce  # noqa: E402

CAST = os.path.join(robot.HERE, "voices.json")
MAIN = robot.REPO.split(os.sep + ".claude" + os.sep)[0]
CACHE = os.path.join(MAIN, "tools", "voice", "cache", "google")  # the main checkout's: worktrees come and go
SAMPLER_OUT = os.path.join(MAIN, "renders", "prologue", "stimmen")
RATE = robot.RATE  # 48 kHz from Google, as the presets expect
VERSION = 3  # bump when the text preparation or the pause/cut shaping changes
API = "https://texttospeech.googleapis.com/v1/text:synthesize"
PAUSE_TAG = "[pause long]"
CUT_MS = 70  # a broken-off word loses this much of its end
GAIN_DB = -6.0  # Chirp 3 HD renders some voices up to full scale and clips; this leaves headroom


# ---------------------------------------------------------------- cast

def load_cast():
    with open(CAST, encoding="utf-8") as f:
        return json.load(f)


def cast_voice(cast, speaker, lang):
    s = cast["speakers"].get(speaker)
    return s and s.get(lang)


def line_rate(line, lang, spk):
    """The line's speakingRate in lang: its own ("rate": {"en": 0.8} in lines.json), else its speaker's."""
    own = line.get("rate")
    return own.get(lang, spk.get("rate", 1.0)) if isinstance(own, dict) else spk.get("rate", 1.0)


# ---------------------------------------------------------------- Google

_auth = {"token": None, "at": 0.0, "project": None}
_auth_lock = threading.Lock()
_chars = {"sent": 0}
_chars_lock = threading.Lock()


def _gcloud():
    return shutil.which("gcloud") or shutil.which("gcloud.cmd") or sys.exit("gcloud not found on PATH")


def auth():
    with _auth_lock:
        if not _auth["token"] or time.time() - _auth["at"] > 1800:
            r = subprocess.run([_gcloud(), "auth", "print-access-token"], capture_output=True, text=True)
            if r.returncode:
                sys.exit("gcloud auth print-access-token failed: " + r.stderr.strip()[-500:])
            p = subprocess.run([_gcloud(), "config", "get-value", "project"], capture_output=True, text=True)
            _auth.update(token=r.stdout.strip(), at=time.time(), project=p.stdout.strip())
        return _auth["token"], _auth["project"]


def cache_path(markup, voice, rate, field="markup"):
    key = json.dumps(["chirp3-hd", voice, round(rate, 3), RATE, "LINEAR16", GAIN_DB, markup]
                     + ([] if field == "markup" else [field]), ensure_ascii=False)
    return os.path.join(CACHE, hashlib.sha1(key.encode("utf-8")).hexdigest() + ".wav")


def make_cache():
    """The cache folder, ignored by git on its own (a "*" .gitignore inside it), so the main checkout never offers the
    takes for a commit, even on a branch whose tools/voice/.gitignore does not list cache/ yet."""
    os.makedirs(CACHE, exist_ok=True)
    ignore = os.path.join(os.path.dirname(CACHE), ".gitignore")
    if not os.path.isfile(ignore):
        with open(ignore, "w", encoding="utf-8", newline="\n") as f:
            f.write("# Google voice answers (google_tts.py): kept here, never committed\n*\n")


def synth(markup, voice, rate=1.0, field="markup"):
    """16-bit mono PCM at RATE for the markup text (or plain text with field="text"), from the cache or from
    Google."""
    path = cache_path(markup, voice, rate, field)
    if os.path.isfile(path):
        return read_wav(path)
    token, project = auth()
    body = {"input": {field: markup},
            "voice": {"languageCode": voice[:5], "name": voice},
            "audioConfig": {"audioEncoding": "LINEAR16", "sampleRateHertz": RATE, "speakingRate": rate,
                            "volumeGainDb": GAIN_DB}}
    data = json.dumps(body, ensure_ascii=False).encode("utf-8")
    for attempt in range(6):
        req = urllib.request.Request(API, data=data, headers={
            "Authorization": "Bearer " + token, "x-goog-user-project": project,
            "Content-Type": "application/json; charset=utf-8"})
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                audio = base64.b64decode(json.load(r)["audioContent"])
            break
        except urllib.error.HTTPError as e:
            msg = e.read().decode("utf-8", "replace")
            if e.code in (429, 500, 503) and attempt < 5:
                time.sleep(4 * (attempt + 1))
                continue
            raise RuntimeError("Google TTS %d for %s: %s\nText: %s" % (e.code, voice, msg[:400], markup[:120]))
        except urllib.error.URLError:
            if attempt == 5:
                raise
            time.sleep(4 * (attempt + 1))
    with _chars_lock:
        _chars["sent"] += len(markup)
    pcm = pcm_of(audio)
    make_cache()
    write_wav(path + ".tmp", pcm)
    os.replace(path + ".tmp", path)
    return pcm


# ---------------------------------------------------------------- PCM helpers

def pcm_of(wav_bytes):
    try:
        with wave.open(io.BytesIO(wav_bytes)) as w:
            if w.getframerate() != RATE or w.getnchannels() != 1 or w.getsampwidth() != 2:
                raise RuntimeError("unexpected format %d Hz, %d ch" % (w.getframerate(), w.getnchannels()))
            return w.readframes(w.getnframes())
    except wave.Error:
        return wav_bytes[44:]


def read_wav(path):
    with wave.open(path) as w:
        return w.readframes(w.getnframes())


def write_wav(path, pcm):
    with wave.open(path, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm)


def silence(sec):
    return b"\0\0" * int(round(RATE * sec))


FRAME = RATE // 100  # 10 ms


def frame_db(pcm):
    """RMS level in dBFS of every 10 ms frame."""
    n = len(pcm) // 2
    samples = struct.unpack("<%dh" % n, pcm[:n * 2])
    out = []
    for i in range(0, n, FRAME):
        f = samples[i:i + FRAME]
        ms = sum(x * x for x in f) / max(1, len(f))
        out.append(10 * math.log10(ms / (32768.0 ** 2)) if ms > 0 else -120.0)
    return out


def voiced(dbs):
    top = max(dbs) if dbs else -120.0
    floor = max(top - 40.0, -60.0)
    return [d > floor for d in dbs]


def smoothed(dbs, width=9):
    """Frame levels averaged (as energy) over `width` frames: breath and room tone in a pause stay low, the dips
    inside a word are filled."""
    lin = [10 ** (d / 10) for d in dbs]
    h, out = width // 2, []
    for i in range(len(lin)):
        w = lin[max(0, i - h):i + h + 1]
        out.append(10 * math.log10(max(1e-12, sum(w) / len(w))))
    return out


def speech_ends(sm, top, run=5):
    """First and last frame of the speech: where the 90 ms average holds within 45 dB of the loudest for `run`
    frames (a click before the speech does not count; a quiet last word does). None when there is no speech."""
    edge = [d > top - 45 for d in sm]
    runs = [i for i in range(len(edge) - run + 1) if all(edge[i:i + run])]
    return (runs[0], runs[-1] + run - 1) if runs else None


def trim(pcm, margin=0.03):
    """The speech of a take without the silence around it (keeping `margin` seconds)."""
    sm = smoothed(frame_db(pcm))
    ends = sm and speech_ends(sm, max(sm))
    if not ends:
        return pcm
    m = int(margin * 100)
    a, b = max(0, ends[0] - m), min(len(sm), ends[1] + 1 + m)
    return pcm[a * FRAME * 2:b * FRAME * 2]


def shape_pauses(pcm, count, seconds):
    """Set the `count` longest silent gaps inside the speech to `seconds` each (the "…" pauses, which the
    [pause long] tags leave as the longest gaps, about 1.2 to 1.5 s). A gap is where the 90 ms average stays
    30 dB under the loudest; the speech's ends are where it is within 45 dB (a quiet last word counts).
    Returns (pcm, [old lengths of the gaps changed])."""
    if count <= 0:
        return pcm, []
    sm = smoothed(frame_db(pcm))
    if not sm:
        return pcm, []
    top = max(sm)
    loud = [d > top - 30 for d in sm]
    ends = speech_ends(sm, top)
    if not ends:
        return pcm, []
    first, last = ends
    gaps, start = [], None
    for i in range(first, last + 1):
        if not loud[i] and start is None:
            start = i
        elif loud[i] and start is not None:
            gaps.append((start, i))
            start = None
    gaps = sorted(g for g in sorted(gaps, key=lambda g: g[1] - g[0], reverse=True)[:count]
                  if (g[1] - g[0]) / 100.0 >= 0.35)  # shorter ones are not a pause the tag made: left alone
    keep = 4  # frames of the original kept at both sides of a gap (the voice's own decay and breath-in)
    out, pos, old = [], 0, []
    for a, b in gaps:
        old.append((b - a) / 100.0)
        out.append(pcm[pos:(a + keep) * FRAME * 2])
        out.append(silence(max(0.0, seconds - 2 * keep / 100.0)))
        pos = (b - keep) * FRAME * 2
    out.append(pcm[pos:])
    return b"".join(out), old


def cap_gaps(pcm, longest):
    """Shorten every silent gap inside the speech that is longer than `longest` seconds to that length (the same
    30 dB rule as shape_pauses). Sadachbia's German leaves 1.2 to 1.7 s between sentences ("Begrenzer raus, L-7!
    … Lauf!"), too slow for Ike; the "…" pauses are set before this and are shorter than the cap. Returns (pcm,
    [old lengths of the gaps shortened])."""
    sm = smoothed(frame_db(pcm))
    ends = sm and speech_ends(sm, max(sm))
    if not ends:
        return pcm, []
    top = max(sm)
    loud = [d > top - 30 for d in sm]
    gaps, start = [], None
    for i in range(ends[0], ends[1] + 1):
        if not loud[i] and start is None:
            start = i
        elif loud[i] and start is not None:
            if (i - start) / 100.0 > longest:
                gaps.append((start, i))
            start = None
    keep = 4
    out, pos, old = [], 0, []
    for a, b in gaps:
        old.append((b - a) / 100.0)
        out.append(pcm[pos:(a + keep) * FRAME * 2])
        out.append(silence(max(0.0, longest - 2 * keep / 100.0)))
        pos = (b - keep) * FRAME * 2
    out.append(pcm[pos:])
    return b"".join(out), old


def chop_end(pcm, ms=CUT_MS):
    """Cut a broken-off word: end the audio `ms` before the last voiced frame ends."""
    v = voiced(frame_db(pcm))
    if True not in v:
        return pcm
    last = len(v) - v[::-1].index(True)  # frame after the last voiced one
    end = max(FRAME, last * FRAME - int(RATE * ms / 1000))
    return pcm[:end * 2]


# ---------------------------------------------------------------- text

def prepare(text, lang):
    """Line text -> (markup for Google, number of "…" pauses in it, whether it ends in a broken-off word). Names are
    said as pronounce.json has them (German "Ike" -> "Eik", "L-7" -> "Ell sieben"); the markup is part of the cache
    key and the line's hash, so a changed pronunciation gives the line a new take."""
    t = text.replace("„", "").replace("“", "").replace("”", "").replace('"', "")
    t = pronounce.spoken(t, lang).replace("L-7", "L 7")  # names as pronounce.json says them
    broken = bool(re.search(r"\w-\s*…?\s*$", t))
    t = re.sub(r"(\w)-(?=\s*…|\s*$)", r"\1", t)   # "kor- …" -> "kor …", "Schalte a-" -> "Schalte a"
    t = re.sub(r"\s*…\s*$", "", t.strip())       # a trailing "…" is the end of the line
    t = re.sub(r"^…\s*", "", t)                  # a leading one goes on from the line before ("… und der Thresher."): no pause
    pauses = t.count("…")
    t = re.sub(r"\s*…\s*", " %s " % PAUSE_TAG, t)
    return re.sub(r"\s+", " ", t).strip(), pauses, broken


def raw_take(line, lang, voice, spk):
    """The Google take of a line, pauses set and a broken end cut, as PCM. Also returns the markup and what
    became of the pauses: the old lengths of the gaps set, or "split" when the tags left too few of them and the
    parts between the "…" were voiced one by one and joined with the pause."""
    markup, pauses, broken = prepare(line[lang], lang)
    rate, pause = line_rate(line, lang, spk), spk.get("pause", 0.6)
    pcm = synth(markup, voice, rate)
    pcm, gaps = shape_pauses(pcm, pauses, pause)
    if len(gaps) < pauses:
        # Chirp sometimes runs short fragments together despite the tags ("Ich bin … kurz … weg …"): try the
        # plain text with "...", then voice the parts one by one (a lone word needs its full stop, or it can
        # come back silent)
        pcm, gaps = shape_pauses(synth(markup.replace(" %s " % PAUSE_TAG, "... "), voice, rate, "text"),
                                 pauses, pause)
    if len(gaps) < pauses:
        joined = [silence(0.1)]
        for k, part in enumerate(p.strip() for p in markup.split(PAUSE_TAG)):
            if k:
                joined.append(silence(pause))
            joined.append(trim(synth(part if re.search(r"[.!?,;:]$", part) else part + ".", voice, rate)))
        pcm, gaps = b"".join(joined + [silence(0.1)]), "split"
    if spk.get("gap"):
        pcm, _ = cap_gaps(pcm, spk["gap"])  # no silence between sentences longer than the cast's "gap"
    if broken or line.get("cut"):
        pcm = chop_end(pcm)
    return pcm, markup, gaps


def requests(line, lang):
    """Every text raw_take may send for the line: (text, field) of the markup, and with "…" inside also of the plain
    text and of each part (the fallbacks when Chirp runs fragments together)."""
    markup, pauses, _ = prepare(line[lang], lang)
    out = [(markup, "markup")]
    if pauses:
        out.append((markup.replace(" %s " % PAUSE_TAG, "... "), "text"))
        out += [(p if re.search(r"[.!?,;:]$", p) else p + ".", "markup") for p in (q.strip() for q in markup.split(PAUSE_TAG))]
    return out


def line_hash(line, lang, voice, spk):
    markup, pauses, broken = prepare(line[lang], lang)
    preset = robot.preset_of(line)
    return hashlib.sha1(json.dumps([VERSION, markup, voice, line_rate(line, lang, spk), GAIN_DB, spk.get("pause", 0.6), preset,
                                    line.get("cut", False), broken, robot.graph(preset, 1.0, line.get("cut", False))]
                                   + ([spk["gap"]] if spk.get("gap") else []),
                                   ensure_ascii=False).encode("utf-8")).hexdigest()[:12]


# ---------------------------------------------------------------- the game's lines

def batch(a):
    cast = load_cast()
    speakers = set(a.speaker.split(",")) if a.speaker else set(cast["speakers"])
    only = set(a.only.split(",")) if a.only else None
    langs = [a.lang] if a.lang else ["de", "en"]
    man = robot.load_manifest()
    todo, kept = [], 0
    for line in robot.load_lines():
        if line["speaker"] not in speakers or line["speaker"] not in cast["speakers"] \
                or (only and line["id"] not in only):
            continue
        spk = cast["speakers"][line["speaker"]]
        for lang in langs:
            voice = spk.get(lang)
            if not voice:
                continue
            old = man["lines"].get(line["id"], {}).get(lang, {})
            if robot.is_recording(old) or os.path.isfile(os.path.join(robot.RECORDINGS, lang, line["id"] + ".wav")):
                kept += 1
                continue  # the user's recording wins
            h = line_hash(line, lang, voice, spk)
            dst = os.path.join(robot.VOICE_OUT, lang, line["id"] + ".ogg")
            if not (a.force or a.fresh) and old.get("source") == "google" and old.get("hash") == h and os.path.isfile(dst):
                continue
            todo.append((line, lang, voice, spk, h, dst))
    if a.dry_run:
        new_chars = worst = 0
        for line, lang, voice, spk, h, dst in todo:
            rate = line_rate(line, lang, spk)
            markup = prepare(line[lang], lang)[0]
            cached = not a.fresh and os.path.isfile(cache_path(markup, voice, rate))
            new_chars += 0 if cached else len(markup)
            # the fallbacks for a "…" inside the line (plain text, then part by part) are billed too when they run (a
            # cached take needs none it has not already fetched)
            worst += 0 if cached else sum(len(t) for t, field in requests(line, lang)
                                          if a.fresh or not os.path.isfile(cache_path(t, voice, rate, field)))
            print("%-16s %s %-28s %s %s" % (line["id"], lang, voice, "cached" if cached else "NEW   ", markup))
        print("%d line(s) to voice, %d characters not cached yet (up to %d if the pause fallbacks run); %d with a "
              "recording kept" % (len(todo), new_chars, worst, kept))
        return
    if a.fresh:
        # new takes: forget what Google said for these lines before
        for line, lang, voice, spk, h, dst in todo:
            for t, field in requests(line, lang):
                path = cache_path(t, voice, line_rate(line, lang, spk), field)
                if os.path.isfile(path):
                    os.remove(path)
    if not todo:
        print("all Google lines up to date (%d with a recording kept)" % kept)
        return
    # fetch in parallel (Google), then filter in parallel (ffmpeg)
    with cf.ThreadPoolExecutor(4) as ex:
        list(ex.map(lambda t: synth(prepare(t[0][t[1]], t[1])[0], t[2], line_rate(t[0], t[1], t[3])), todo))

    def render(t):
        line, lang, voice, spk, h, dst = t
        pcm, markup, gaps = raw_take(line, lang, voice, spk)
        with tempfile.TemporaryDirectory(prefix="voice-g-") as tmp:
            src = os.path.join(tmp, "take.wav")
            write_wav(src, pcm)
            secs, _ = robot.process(robot.preset_of(line), src, dst, 1.0, line.get("cut", False))
        return secs, gaps

    with cf.ThreadPoolExecutor(4) as ex:
        results = list(ex.map(render, todo))
    for (line, lang, voice, spk, h, dst), (secs, gaps) in zip(todo, results):
        man["lines"].setdefault(line["id"], {})[lang] = {
            "file": "%s/%s.ogg" % (lang, line["id"]), "seconds": round(secs, 3), "placeholder": False,
            "preset": robot.preset_of(line), "source": "google", "voice": voice, "hash": h}
        print("%-16s %s %-28s %5.2f s%s" % (line["id"], lang, voice, secs,
                                            "  pauses: %s" % (gaps if gaps == "split" else "%s s set to %.1f s" % (gaps, spk.get("pause", 0.6))) if gaps else ""))
    robot.save_manifest(man)
    print("%d line(s) voiced, %d characters sent to Google; %d with a recording kept"
          % (len(todo), _chars["sent"], kept))


# ---------------------------------------------------------------- sampler

NUMBERS = ["eins", "zwei", "drei", "vier", "fünf", "sechs"]


def piece(tmp, name, preset, pcm, cut=False):
    """PCM through a preset at -18 LUFS, back as PCM (16-bit WAV in between)."""
    src = os.path.join(tmp, name + ".in.wav")
    dst = os.path.join(tmp, name + ".out.wav")
    write_wav(src, pcm)
    robot.process(preset, src, dst, 1.0, cut)
    return read_wav(dst)


def label(tmp, cast, text):
    markup = text
    return piece(tmp, "label-" + hashlib.sha1(text.encode("utf-8")).hexdigest()[:8], "plain",
                 synth(markup, cast["label_voice"]))


def check(name, pcm):
    """Length, loudest frame and sample peak of a piece; complains about silent or clipped ones."""
    n = len(pcm) // 2
    samples = struct.unpack("<%dh" % n, pcm)
    peak = max(abs(x) for x in samples) if n else 0
    full = sum(1 for x in samples if abs(x) >= 32700)
    dbs = frame_db(pcm)
    top = max(dbs) if dbs else -120
    pk = 20 * math.log10(peak / 32768.0) if peak else -120
    flag = ""
    if n / RATE < 0.3 or top < -40:
        flag = "  SILENT?"
    elif full > 2:
        flag = "  CLIPPED (%d samples at full scale)" % full
    return "%-34s %5.2f s, loudest 10 ms %6.1f dBFS, peak %5.1f dBFS%s" % (name, n / RATE, top, pk, flag), flag


def write_mp3(tmp, pcm, mp3, title):
    src = os.path.join(tmp, "mix.wav")
    write_wav(src, pcm)
    i0, _ = robot.measure(src, "[0:a]anull[out]")
    gain = -18.0 - i0
    # limited at -2.5 dBFS so the MP3's own overshoot (true peak) stays below -1 dBFS; once more if the
    # limiter took loudness away
    i1, _ = robot.measure(src, "[0:a]anull[out]", "volume=%.2fdB,%s" % (gain, robot.limiter(-2.5)))
    gain = min(gain + 1.5, gain + (-18.0 - i1))
    os.makedirs(os.path.dirname(mp3), exist_ok=True)
    robot.run(["-y", "-i", src, "-af", "volume=%.2fdB,%saresample=44100" % (gain, robot.limiter(-2.5)),
               "-c:a", "libmp3lame", "-b:a", "160k", "-metadata", "title=" + title,
               "-metadata", "album=Machines Prolog: Stimmen", mp3])
    err = robot.run(["-i", mp3, "-af", "ebur128=framelog=quiet:peak=true,volumedetect", "-f", "null", "-"])
    lufs = re.search(r"Summary:.*?I:\s*(-?[\d.]+) LUFS", err, re.S)
    tp = re.search(r"True peak:\s*Peak:\s*(-?[\d.]+) dBFS", err, re.S)
    sp = re.search(r"max_volume:\s*(-?[\d.]+) dB", err)
    return robot.duration(mp3), float(lufs.group(1)), float(tp.group(1)) if tp else None, float(sp.group(1))


def sampler(a):
    cast = load_cast()
    lines = {l["id"]: l for l in robot.load_lines()}
    out = a.out or SAMPLER_OUT
    speakers = a.speaker.split(",") if a.speaker else list(cast["speakers"])
    report, problems = [], []
    with tempfile.TemporaryDirectory(prefix="voice-sampler-") as tmp:
        for sp in speakers:
            spk = cast["speakers"][sp]
            mix = [silence(0.5)]
            for n, cand in enumerate(spk["candidates"]):
                voice = "de-DE-Chirp3-HD-" + cand
                if n:
                    mix.append(silence(1.5))
                mix += [label(tmp, cast, "Stimme %s: %s." % (NUMBERS[n], cand)), silence(0.6)]
                for k, lid in enumerate(spk["sampler"]):
                    line = lines[lid]
                    pcm, markup, gaps = raw_take(line, "de", voice, spk)
                    msg, flag = check("%s %s raw" % (cand, lid), pcm)
                    report.append(msg + ("  pauses %s" % gaps if gaps else ""))
                    done = piece(tmp, "%s-%s-%s" % (sp, cand, lid), robot.preset_of(line), pcm, line.get("cut", False))
                    msg2, flag2 = check("%s %s %s" % (cand, lid, robot.preset_of(line)), done)
                    report.append(msg2)
                    if flag or flag2:
                        problems.append(msg + " / " + msg2)
                    if k:
                        mix.append(silence(0.6))
                    mix.append(done)
            mix.append(silence(1.0))
            mp3 = os.path.join(out, "%s-stimmen.mp3" % sp)
            res = write_mp3(tmp, b"".join(mix), mp3, "%s: Stimmen zur Auswahl" % sp)
            report.append("%s: %.1f s, %.1f LUFS, true peak %s dBFS, sample peak %.1f dBFS" % ((mp3,) + res))
            # the cast voice without and with the filter
            line = lines[spk["sampler"][0]]
            pcm, _, _ = raw_take(line, "de", spk["de"], spk)
            mix = [silence(0.5), label(tmp, cast, "Ohne Filter."), silence(0.6),
                   piece(tmp, sp + "-roh", "plain", pcm), silence(1.5),
                   label(tmp, cast, "Mit Filter."), silence(0.6),
                   piece(tmp, sp + "-robot", robot.preset_of(line), pcm, line.get("cut", False)), silence(1.0)]
            mp3 = os.path.join(out, "%s-roh-gegen-robot.mp3" % sp)
            res = write_mp3(tmp, b"".join(mix), mp3, "%s: ohne und mit Filter" % sp)
            report.append("%s: %.1f s, %.1f LUFS, true peak %s dBFS, sample peak %.1f dBFS" % ((mp3,) + res))
    print("\n".join(report))
    if problems:
        print("PROBLEMS:\n" + "\n".join(problems))
    print("%d characters sent to Google" % _chars["sent"])


# ---------------------------------------------------------------- main

def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--speaker", help="comma-separated speakers of voices.json (default: all of them)")
    ap.add_argument("--only", help="comma-separated line ids")
    ap.add_argument("--lang", choices=["de", "en"])
    ap.add_argument("--force", action="store_true", help="redo every line (from the cache where it can)")
    ap.add_argument("--fresh", action="store_true", help="new takes from Google for the --only lines (their cache dropped)")
    ap.add_argument("--dry-run", action="store_true", help="only list what would be voiced and what it costs")
    ap.add_argument("--sampler", action="store_true", help="voice the candidate voices for the user to pick")
    ap.add_argument("--out", help="folder for --sampler (default: the main checkout's renders/prologue/stimmen)")
    ap.add_argument("--say", nargs=3, metavar=("VOICE", "TEXT", "OUT"), help="voice one text with any voice")
    ap.add_argument("--preset", default="plain", help="robot preset for --say (default: plain)")
    a = ap.parse_args()
    if a.fresh and not a.only:
        ap.error("--fresh needs --only: every line would get a new take")
    if a.say:
        voice, text, out = a.say
        markup, pauses, broken = prepare(text, voice[:2])
        pcm, gaps = shape_pauses(synth(markup, voice), pauses, 0.8)
        if broken:
            pcm = chop_end(pcm)
        with tempfile.TemporaryDirectory(prefix="voice-say-") as tmp:
            src = os.path.join(tmp, "take.wav")
            write_wav(src, pcm)
            secs, _ = robot.process(a.preset, src, out, 1.0, broken)
        print("%s: %.2f s, %d characters sent to Google" % (out, secs, _chars["sent"]))
        return
    if a.sampler:
        sampler(a)
        return
    batch(a)


if __name__ == "__main__":
    main()
