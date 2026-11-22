"""Reads the story book (docs/story/buch/*.md) aloud as German MP3s.

    python tools/story-book/voice.py [--voice de-DE-Chirp3-HD-Charon] [--only 03] [--sample FILE.txt OUT.mp3]
    python tools/story-book/voice.py --dry-run     counts the paragraphs and characters that are not voiced yet

Backends:
  google (default)  Google Cloud Text-to-Speech, Chirp 3 HD voices. Uses the gcloud login and its project
                    (`gcloud auth print-access-token`). Chirp 3 HD has 1 M free characters a month; the book is
                    about 300 k. The text goes to Google.
  edge              Microsoft Edge online voices through the `edge-tts` package (pip install edge-tts),
                    e.g. --backend edge --voice de-DE-ChristophNeural. The text goes to Microsoft.

Each paragraph is one request. Results are cached by the hash of the spoken text in renders/story-book/cache, so a
re-run only voices paragraphs whose spoken text changed. PRONOUNCE says how names are spoken (Ike -> Eik,
L-7 -> Ell sieben); the book text itself keeps the names. Output: renders/story-book/audio/<chapter>.mp3, one long file
Machines-Story-komplett.mp3 and a playlist Machines-Story.m3u. Needs ffmpeg (tools/film-upscale/bin/ffmpeg or PATH).
"""
import argparse, base64, concurrent.futures as cf, hashlib, json, os, re, shutil, subprocess, sys, time, urllib.request, urllib.error, wave

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
BOOK = os.path.join(ROOT, "docs", "story", "buch")
OUT = os.path.join(ROOT, "renders", "story-book")
RATE = 24000


def ffmpeg():
    local = os.path.join(ROOT, "tools", "film-upscale", "bin", "ffmpeg", "ffmpeg.exe")
    if os.path.exists(local):
        return local
    main = os.path.join(ROOT.split(os.sep + ".claude" + os.sep)[0], "tools", "film-upscale", "bin", "ffmpeg", "ffmpeg.exe")
    if os.path.exists(main):
        return main
    return shutil.which("ffmpeg") or sys.exit("ffmpeg not found")


# ---------------------------------------------------------------- text

# How names are said (user's wish of 2026-11-22: Ike always "Eik", L-7 always "Ell sieben"). Only the text sent to
# the voice changes; the book, the EPUB and the Markdown keep "Ike" and "L-7". Every form that occurs in the book:
PRONOUNCE = [
    (r"\bL-7s\b", "Ell Siebens"),   # genitive: "L-7s Kern", "wie L-7s"
    (r"\bL-7\b", "Ell sieben"),
    (r"\bIKE\b", "Eik"),            # painted on the Goliath in the finale
    (r"\bIkes\b", "Eiks"),          # genitive: "Ikes Räder"
    (r"\bIke\b", "Eik"),            # also Mara Ikeda's nickname; "Ikeda" itself stays
]


def pronounce(text):
    for pat, said in PRONOUNCE:
        text = re.sub(pat, said, text)
    return text


def speakable(md):
    """Markdown chapter -> list of (kind, text): kind is 'title', 'para' or 'break'."""
    out = []
    for block in re.split(r"\n\s*\n", md.strip()):
        b = block.strip()
        if not b or b.startswith("<!--"):
            continue
        if re.fullmatch(r"(\*\s*){3}|-{3,}|\*{3,}", b):
            out.append(("break", ""))
            continue
        kind = "para"
        if b.startswith("#"):
            kind = "title"
            b = b.lstrip("#").strip()
        b = re.sub(r"^>\s?", "", b, flags=re.M)          # quotes
        b = re.sub(r"\[([^\]]*)\]\([^)]*\)", r"\1", b)    # links
        b = re.sub(r"[*_`]", "", b)                       # emphasis
        b = re.sub(r"\s*\n\s*", " ", b)
        b = b.replace(" %", " Prozent").replace("%", " Prozent")
        b = pronounce(b)
        b = b.replace("W-417", "W 4 1 7").replace("S-1", "S 1")
        b = b.replace("…", "...")
        # HUD lines and light signals are in capitals; read them as words, not letter by letter
        b = re.sub(r"\b([A-ZÄÖÜ]{3,})\b", lambda m: m.group(1) if m.group(1) == "HUD" else m.group(1).capitalize(), b)
        if kind == "title":
            b = b.replace(":", ".")
        out.append((kind, b))
    return out


def chunks(text, limit=1400):
    """Split long paragraphs at sentence ends so every request stays small."""
    if len(text.encode("utf-8")) <= limit:
        return [text]
    parts, cur = [], ""
    for s in re.split(r"(?<=[.!?“])\s+", text):
        if cur and len((cur + " " + s).encode("utf-8")) > limit:
            parts.append(cur)
            cur = s
        else:
            cur = (cur + " " + s).strip()
    if cur:
        parts.append(cur)
    return parts


# ---------------------------------------------------------------- backends

_token = {"t": None, "at": 0}


def google_token():
    if not _token["t"] or time.time() - _token["at"] > 1800:
        gc = shutil.which("gcloud") or shutil.which("gcloud.cmd")
        r = subprocess.run([gc, "auth", "print-access-token"], capture_output=True, text=True, shell=False)
        if r.returncode:
            sys.exit("gcloud auth print-access-token failed: " + r.stderr)
        _token["t"], _token["at"] = r.stdout.strip(), time.time()
        p = subprocess.run([gc, "config", "get-value", "project"], capture_output=True, text=True)
        _token["project"] = p.stdout.strip()
    return _token["t"], _token["project"]


def google_tts(text, voice, speed):
    token, project = google_token()
    body = {"input": {"text": text},
            "voice": {"languageCode": voice[:5], "name": voice},
            "audioConfig": {"audioEncoding": "LINEAR16", "sampleRateHertz": RATE, "speakingRate": speed}}
    req = urllib.request.Request("https://texttospeech.googleapis.com/v1/text:synthesize",
                                 data=json.dumps(body).encode("utf-8"),
                                 headers={"Authorization": "Bearer " + token, "x-goog-user-project": project,
                                          "Content-Type": "application/json; charset=utf-8"})
    for attempt in range(6):
        try:
            with urllib.request.urlopen(req, timeout=120) as r:
                wav = base64.b64decode(json.load(r)["audioContent"])
                return wav[44:]  # LINEAR16 comes with a WAV header
        except urllib.error.HTTPError as e:
            msg = e.read().decode("utf-8", "replace")
            if e.code in (429, 500, 503) and attempt < 5:
                time.sleep(4 * (attempt + 1))
                continue
            raise RuntimeError(f"Google TTS {e.code}: {msg}\nText: {text[:120]}")
        except urllib.error.URLError:
            if attempt < 5:
                time.sleep(4 * (attempt + 1))
                continue
            raise


def edge_tts(text, voice, speed, tmpdir):
    import asyncio, edge_tts as et
    pct = int(round((speed - 1) * 100))
    mp3 = os.path.join(tmpdir, hashlib.sha1(text.encode()).hexdigest() + ".mp3")
    asyncio.run(et.Communicate(text, voice, rate=f"{pct:+d}%").save(mp3))
    pcm = subprocess.run([ffmpeg(), "-loglevel", "error", "-i", mp3, "-f", "s16le", "-ac", "1", "-ar", str(RATE), "-"],
                         capture_output=True).stdout
    os.remove(mp3)
    return pcm


# ---------------------------------------------------------------- voicing

def silence(sec):
    return b"\0\0" * int(RATE * sec)


def cache_path(text, args, cache):
    """The cache is keyed on the spoken text, so a paragraph is voiced again exactly when what is said changes."""
    key = hashlib.sha1(f"{args.backend}|{args.voice}|{args.speed}|{text}".encode("utf-8")).hexdigest()
    return os.path.join(cache, key + ".pcm")


def missing(items, args, cache):
    """(paragraphs, pieces, characters) that are not in the cache yet and would be sent to the voice."""
    paras, pieces, chars = set(), 0, 0
    for i, (kind, text) in enumerate(items):
        if kind != "break":
            for c in chunks(text):
                if not os.path.exists(cache_path(c, args, cache)):
                    paras.add(i)
                    pieces += 1
                    chars += len(c)
    return len(paras), pieces, chars


def voice_piece(text, args, cache):
    path = cache_path(text, args, cache)
    if os.path.exists(path):
        return open(path, "rb").read()
    if args.backend == "google":
        pcm = google_tts(text, args.voice, args.speed)
    else:
        pcm = edge_tts(text, args.voice, args.speed, cache)
    with open(path + ".tmp", "wb") as f:
        f.write(pcm)
    os.replace(path + ".tmp", path)
    return pcm


def voice_items(items, args, cache):
    jobs = []  # (index, text)
    for i, (kind, text) in enumerate(items):
        if kind != "break":
            for c in chunks(text):
                jobs.append((i, kind, c))
    with cf.ThreadPoolExecutor(args.threads) as ex:
        pcms = list(ex.map(lambda j: voice_piece(j[2], args, cache), jobs))
    audio = [silence(0.6)]
    last = None
    for (i, kind, _), pcm in zip(jobs, pcms):
        if last is not None and i != last:
            prev_kind = items[last][0]
            gap = 1.6 if prev_kind == "title" else 0.75
            if any(items[k][0] == "break" for k in range(last + 1, i)):
                gap = 2.2
            audio.append(silence(gap))
        elif last == i:
            audio.append(silence(0.25))
        audio.append(pcm)
        last = i
    audio.append(silence(2.5))
    return b"".join(audio), sum(len(j[2]) for j in jobs)


def write_mp3(pcm, mp3, title, track=None, album="Machines – Keiner trägt alles (Hörbuch)"):
    wav = mp3[:-4] + ".wav"
    with wave.open(wav, "wb") as w:
        w.setnchannels(1)
        w.setsampwidth(2)
        w.setframerate(RATE)
        w.writeframes(pcm)
    meta = ["-metadata", f"title={title}", "-metadata", f"album={album}", "-metadata", "artist=Machines Remake",
            "-metadata", "genre=Hörbuch"]
    if track:
        meta += ["-metadata", f"track={track}"]
    subprocess.run([ffmpeg(), "-y", "-loglevel", "error", "-i", wav, "-af", "loudnorm=I=-18:TP=-1.5:LRA=11",
                    "-ar", "44100", "-c:a", "libmp3lame", "-b:a", "96k", *meta, mp3], check=True)
    os.remove(wav)
    return len(pcm) / 2 / RATE


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--backend", default="google", choices=["google", "edge"])
    ap.add_argument("--voice", default="de-DE-Chirp3-HD-Orus")
    ap.add_argument("--speed", type=float, default=1.0)
    ap.add_argument("--threads", type=int, default=4)
    ap.add_argument("--only", help="voice only chapters whose file name starts with this")
    ap.add_argument("--sample", nargs=2, metavar=("TEXT", "MP3"), help="voice one text file, for trying voices")
    ap.add_argument("--no-full", action="store_true", help="skip the one long file")
    ap.add_argument("--dry-run", action="store_true", help="only count what is not cached yet and would be billed")
    args = ap.parse_args()

    cache = os.path.join(OUT, "cache")
    os.makedirs(cache, exist_ok=True)
    if args.sample:
        items = speakable(open(args.sample[0], encoding="utf-8").read())
        pcm, n = voice_items(items, args, cache)
        sec = write_mp3(pcm, args.sample[1], os.path.basename(args.sample[1]))
        print(f"{args.sample[1]}: {sec:.0f} s, {n} characters")
        return

    audio_dir = os.path.join(OUT, "audio")
    os.makedirs(audio_dir, exist_ok=True)
    files = sorted(f for f in os.listdir(BOOK) if re.match(r"\d\d-.*\.md$", f))
    if args.dry_run:
        tp = tc = 0
        for f in files:
            if args.only and not f.startswith(args.only):
                continue
            paras, pieces, chars = missing(speakable(open(os.path.join(BOOK, f), encoding="utf-8").read()), args, cache)
            tp, tc = tp + paras, tc + chars
            print(f"{f}: {paras} paragraphs ({pieces} pieces), {chars} characters to voice")
        print(f"total: {tp} paragraphs, {tc} characters to voice")
        return
    total_chars, total_sec, playlist, parts = 0, 0.0, ["#EXTM3U"], []
    for n, f in enumerate(files, 1):
        md = open(os.path.join(BOOK, f), encoding="utf-8").read()
        title = re.search(r"^#\s+(.+)$", md, re.M).group(1).strip()
        mp3 = os.path.join(audio_dir, f[:-3] + ".mp3")
        if args.only and not f.startswith(args.only):
            if os.path.exists(mp3):
                parts.append(mp3)
            continue
        pcm, chars = voice_items(speakable(md), args, cache)
        sec = write_mp3(pcm, mp3, title, track=f"{n}/{len(files)}")
        total_chars += chars
        total_sec += sec
        parts.append(mp3)
        playlist += [f"#EXTINF:{int(sec)},{title}", os.path.basename(mp3)]
        print(f"{f}: {sec/60:.1f} min, {chars} characters", flush=True)
    with open(os.path.join(audio_dir, "Machines-Story.m3u"), "w", encoding="utf-8") as pl:
        pl.write("\n".join(playlist) + "\n")
    if not args.no_full and not args.only:
        lst = os.path.join(audio_dir, "parts.txt")
        with open(lst, "w", encoding="utf-8") as l:
            l.writelines(f"file '{p.replace(os.sep, '/')}'\n" for p in parts)
        subprocess.run([ffmpeg(), "-y", "-loglevel", "error", "-f", "concat", "-safe", "0", "-i", lst, "-c", "copy",
                        "-metadata", "title=Machines – Keiner trägt alles (komplett)",
                        "-metadata", "album=Machines – Keiner trägt alles (Hörbuch)",
                        os.path.join(audio_dir, "Machines-Story-komplett.mp3")], check=True)
        os.remove(lst)
    print(f"total: {total_sec/3600:.2f} h, {total_chars} characters")


if __name__ == "__main__":
    main()
