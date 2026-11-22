"""Robot-voice filter for the story's voice lines.

Turns a human voice recording into one of the story's robot voices with ffmpeg,
loudness-normalises it to -18 LUFS (sample peaks at most -1.5 dBFS, measured on the decoded Ogg; the
true peak between samples can come out a little higher, up to about -0.8 dBTP) and writes Ogg Vorbis.

    python tools/voice/robot.py <preset> <in.wav> <out.ogg> [--pitch 1.0] [--cut]
    python tools/voice/robot.py --batch [--lang de|en] [--only ID[,ID...]] [--force]
    python tools/voice/robot.py --list

--batch reads the user's recordings tools/voice/recordings/<lang>/<id>.wav for every
voiced line of tools/voice/lines.json, writes game/assets/story/voice/<lang>/<id>.ogg
and marks them "placeholder": false, "source": "recording" in game/assets/story/voice/manifest.json.
A recording replaces a Google voice (google_tts.py) and a SAPI placeholder (placeholders.py);
lines without a recording keep theirs.

<out> may end in .wav (16-bit PCM) instead of .ogg.

Presets (speaker names from lines.json, plus per-line variants):
  ship      calm friendly PA: band-pass 300-3400 Hz, faint ring-mod shimmer, chorus, hall
  ship-dry  the same voice from a speaker right next to you: no hall, small room
  ship-old  distorted old recording: narrow band, tape wobble, crush, hiss
  auto      flat automatic voice: monotone vocoder buzz, bitcrush, narrow band
  ike       brash robot: slight pitch-up, ring modulation, soft-clip grit
  bob       terse robot: lower, metallic comb filter, short echo
  bob-radio bob over a glitching radio: telephone band, dropouts, hiss
  whisper   the update: pitched down, whispered (random phase), reversed reverb swell
  l7        the Locator's thoughts: close, dry, thin and a little hoarse, robotized, two semitones up
  plain     no robot: only trimmed and levelled (for comparisons and spoken labels)

ffmpeg: FFMPEG env var, else tools/film-upscale/bin/ffmpeg/ffmpeg.exe in this checkout,
else the main checkout's copy, else ffmpeg on PATH.
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
LINES = os.path.join(HERE, "lines.json")
RECORDINGS = os.path.join(HERE, "recordings")
VOICE_OUT = os.path.join(REPO, "game", "assets", "story", "voice")
MANIFEST = os.path.join(VOICE_OUT, "manifest.json")
sys.path.insert(0, os.path.join(REPO, "tools", "sfx"))
import godot_import  # noqa: E402

TARGET_LUFS = -18.0
TARGET_TP = -1.5
RATE = 48000


def find_ffmpeg():
    cands = [os.environ.get("FFMPEG"),
             os.path.join(REPO, "tools", "film-upscale", "bin", "ffmpeg", "ffmpeg.exe"),
             "C:/Projekte/machines/tools/film-upscale/bin/ffmpeg/ffmpeg.exe",
             shutil.which("ffmpeg")]
    for c in cands:
        if c and os.path.isfile(c):
            return c
    sys.exit("ffmpeg not found; set FFMPEG to its path")


FFMPEG = find_ffmpeg()

# Shared front end: mono, 48 kHz, rumble cut, silence trimmed at both ends.
PRE = ("aformat=sample_fmts=fltp:channel_layouts=mono,aresample=%d,highpass=f=70,"
       "silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.04,areverse,"
       "silenceremove=start_periods=1:start_threshold=-50dB:start_silence=0.06,areverse" % RATE)

ROBOTIZE = "afftfilt=real='hypot(re,im)*sin(0)':imag='hypot(re,im)*cos(0)':win_size={w}:overlap=0.75"
WHISPERIZE = ("afftfilt=real='hypot(re,im)*cos((random(0)*2-1)*2*3.14)'"
              ":imag='hypot(re,im)*sin((random(1)*2-1)*2*3.14)':win_size=256:overlap=0.8")
HALL = "aecho=0.85:0.55:43|71|109|157|229|311:0.32|0.27|0.21|0.16|0.12|0.08"


def pitch(p):
    return "anull" if abs(p - 1.0) < 1e-3 else "rubberband=pitch=%.4f" % p


# L-7 two semitones over the Iapetus voice's own pitch (0.97 before 2026-11-22), its formants kept where they are, so it
# sounds higher and smaller without turning into a cartoon.
L7_PITCH = 0.97 * 2 ** (2 / 12)


def pitch_formant(p):
    return "anull" if abs(p - 1.0) < 1e-3 else "rubberband=pitch=%.4f:formant=preserved:pitchq=quality" % p


def graph(preset, p=1.0, cut=False):
    """filter_complex for a preset; input [0:a], output [out]."""
    def tail(d):
        return "" if cut else "apad=pad_dur=%.2f," % d
    if preset in ("ship", "ship-dry", "ship-old"):
        if preset == "ship":
            room = tail(1.4) + HALL + ",lowpass=f=5000"
            band = "highpass=f=300,lowpass=f=3400"
            extra = ""
        elif preset == "ship-dry":
            room = "aecho=0.8:0.35:9|14:0.2|0.12"
            band = "highpass=f=260,lowpass=f=4200"
            extra = ""
        else:
            room = tail(1.0) + HALL
            band = "highpass=f=500,lowpass=f=2700"
            extra = (",vibrato=f=0.7:d=0.06,tremolo=f=9:d=0.25,"
                     "acrusher=bits=7:mode=log:mix=0.35:aa=1")
        g = ("[0:a]{pre},{pit},acompressor=threshold=-24dB:ratio=3:attack=5:release=120,asplit=2[d][r];"
             "[r]aeval=exprs='val(0)*sin(2*PI*173*t)':channel_layout=mono,volume=0.16[rm];"
             "[d][rm]amix=inputs=2:normalize=0,"
             "chorus=0.7:0.9:22|37:0.22|0.18:0.3|0.25:1.3|1.9,{band},"
             "equalizer=f=1800:t=q:w=1:g=3{extra},{room}[v]").format(
                 pre=PRE, pit=pitch(p), band=band, extra=extra, room=room)
        if preset == "ship-old":
            g = g[:-3] + ",dynaudnorm=f=200:g=5:p=0.7[v]"
            g += (";anoisesrc=c=pink:a=0.006:seed=7,highpass=f=400,lowpass=f=3000[n];"
                  "[v][n]amix=inputs=2:duration=first:normalize=0[out]")
        else:
            g = g[:-3] + "[out]"
        return g
    if preset == "auto":
        return ("[0:a]{pre},{pit},acompressor=threshold=-26dB:ratio=6:attack=3:release=80,asplit=2[d][r];"
                "[r]{rob}[rb];[d][rb]amix=inputs=2:weights='0.45 0.8':normalize=0,"
                "highpass=f=380,lowpass=f=3000,acrusher=bits=8:mode=log:mix=0.5:samples=2:aa=1,"
                "equalizer=f=1200:t=q:w=1.2:g=4,{tail}aecho=0.8:0.25:23|51:0.18|0.1[out]").format(
                    pre=PRE, pit=pitch(p), rob=ROBOTIZE.format(w=1024),
                    tail=tail(0.25))
    if preset == "ike":
        return ("[0:a]{pre},{pit},acompressor=threshold=-22dB:ratio=4:attack=4:release=90,asplit=2[d][r];"
                "[r]aeval=exprs='val(0)*sin(2*PI*62*t)':channel_layout=mono[rm];"
                "[d][rm]amix=inputs=2:weights='0.75 0.55':normalize=0,"
                "volume=2.2,asoftclip=type=tanh,highpass=f=140,lowpass=f=6500,"
                "aexciter=amount=1.5:drive=6:freq=3000,{tail}aecho=0.8:0.3:17:0.25[out]").format(
                    pre=PRE, pit=pitch(1.08 * p), tail=tail(0.2))
    if preset in ("bob", "bob-radio"):
        g = ("[0:a]{pre},{pit},acompressor=threshold=-24dB:ratio=4:attack=4:release=100,"
             "aecho=0.9:0.9:3.1|6.2|9.3:0.5|0.32|0.2,highpass=f=180,lowpass=f=4800,"
             "equalizer=f=2400:t=q:w=3:g=5,{tail}aecho=0.85:0.5:85:0.22").format(
                 pre=PRE, pit=pitch(0.86 * p), tail=tail(0.3))
        if preset == "bob":
            return g + "[out]"
        return (g + ",highpass=f=550,lowpass=f=2600,acrusher=bits=7:mode=log:mix=0.6:samples=3:aa=1,"
                "aeval=exprs='val(0)*gt(sin(2*PI*3.3*t)+sin(2*PI*7.9*t)+sin(2*PI*13.1*t),-1.9)'"
                ":channel_layout=mono,dynaudnorm=f=200:g=5:p=0.7,asoftclip=type=hard[v];"
                "anoisesrc=c=white:a=0.008:seed=3,highpass=f=800,lowpass=f=3500[n];"
                "[v][n]amix=inputs=2:duration=first:normalize=0[out]")
    if preset == "whisper":
        return ("[0:a]{pre},{pit},{wh},highpass=f=220,lowpass=f=7500,adelay=1400:all=1,"
                "apad=pad_dur=1.2,asplit=2[d][r];"
                "[r]areverse,aecho=0.8:0.88:61|127|211|337|503|701:0.5|0.42|0.34|0.26|0.19|0.13,"
                "areverse,lowpass=f=5000,volume=0.8[rv];"
                "[d]aecho=0.8:0.4:97|173:0.25|0.15[dw];"
                "[dw][rv]amix=inputs=2:normalize=0[out]").format(
                    pre=PRE, pit=pitch(0.82 * p), wh=WHISPERIZE)
    if preset == "l7":
        # The Locator's thoughts: close and dry, thin and a little hoarse (a breath of the whisper under it), robotized
        # (a monotone buzz, a faint 95 Hz ring modulation, a short metallic comb and a touch of crush), pitched up two
        # semitones with its formants kept, no radio band, no hall.
        return ("[0:a]{pre},{pit},acompressor=threshold=-24dB:ratio=3:attack=5:release=100,asplit=4[d][r][w][m];"
                "[r]{rob}[rb];[w]{wh},highpass=f=900[wb];"
                "[m]aeval=exprs='val(0)*sin(2*PI*95*t)':channel_layout=mono[rm];"
                "[d][rb][wb][rm]amix=inputs=4:weights='0.8 0.3 0.14 0.12':normalize=0,"
                "aecho=0.9:0.9:4.3|8.6:0.2|0.1,"
                "highpass=f=170,lowpass=f=7200,equalizer=f=3200:t=q:w=1.5:g=3,equalizer=f=250:t=q:w=1:g=-3,"
                "acrusher=bits=9:mode=log:mix=0.18:aa=1,{tail}aecho=0.8:0.22:11:0.14[out]").format(
                    pre=PRE, pit=pitch_formant(L7_PITCH * p), rob=ROBOTIZE.format(w=512), wh=WHISPERIZE, tail=tail(0.2))
    if preset == "plain":
        return "[0:a]{pre},{pit}[out]".format(pre=PRE, pit=pitch(p))
    raise SystemExit("unknown preset %r (see --list)" % preset)


PRESETS = ["ship", "ship-dry", "ship-old", "auto", "ike", "bob", "bob-radio", "whisper", "l7", "plain"]


def run(args):
    r = subprocess.run([FFMPEG, "-hide_banner", "-nostdin"] + args,
                       capture_output=True, text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        raise RuntimeError("ffmpeg failed:\n" + r.stderr[-3000:])
    return r.stderr


def duration(path):
    err = subprocess.run([FFMPEG, "-hide_banner", "-nostdin", "-i", path, "-f", "null", "-"],
                         capture_output=True, text=True, encoding="utf-8", errors="replace").stderr
    t = re.findall(r"time=(\d+):(\d+):(\d+\.\d+)", err)
    if not t:
        return 0.0
    h, m, s = t[-1]
    return int(h) * 3600 + int(m) * 60 + float(s)


def measure(src, g, post=""):
    """Integrated loudness (LUFS) and sample peak (dBFS) of the graph's output."""
    err = run(["-i", src, "-filter_complex", g + ";[out]" + post + "ebur128=framelog=quiet,volumedetect[m]",
               "-map", "[m]", "-f", "null", "-"])
    i = re.search(r"Summary:.*?I:\s*(-?[\d.]+|-inf) LUFS", err, re.S)
    pk = re.search(r"max_volume:\s*(-?[\d.]+) dB", err)
    lufs = float(i.group(1)) if i and i.group(1) != "-inf" else -99.0
    return lufs, float(pk.group(1)) if pk else -99.0


def limiter(tp=TARGET_TP):
    return "alimiter=limit=%.4f:level=false:attack=2:release=40:latency=1," % (10 ** (tp / 20))


def process(preset, src, dst, p=1.0, cut=False, lufs=TARGET_LUFS):
    """Filter src through preset into dst (Ogg Vorbis, or 16-bit WAV for a .wav dst) at `lufs` LUFS, peaks limited
    to -1.5 dBFS.
    Returns (seconds, {"input_i": loudness after the filter, "gain_db": gain applied})."""
    g = graph(preset, p, cut)
    i0, pk0 = measure(src, g)
    gated = i0 > -60  # a line shorter than 0.4 s cannot be gated: peak-normalise it to -3 dBFS
    gain = (lufs - i0) if gated else (-3.0 - pk0)
    first = gain
    for _ in range(3):
        if not gated:
            break
        i1, _ = measure(src, g, "volume=%.2fdB,%s" % (gain, limiter()))
        if abs(i1 - lufs) < 0.4:
            break
        gain = min(first + 6.0, gain + (lufs - i1))  # a peaky voice: push into the limiter, but not far
    end = "afade=t=in:d=0.004,areverse,afade=t=in:d=0.006,areverse"  # no clicks at either end
    os.makedirs(os.path.dirname(os.path.abspath(dst)), exist_ok=True)
    wav = dst.lower().endswith(".wav")
    codec = ["-c:a", "pcm_s16le"] if wav else ["-c:a", "libvorbis", "-q:a", "5"]
    tp = TARGET_TP
    for _ in range(4):
        run(["-y", "-i", src, "-filter_complex",
             g + ";[out]volume=%.2fdB,%saresample=%d,%s[f]" % (gain, limiter(tp), RATE, end),
             "-map", "[f]", "-ac", "1"] + codec + ["-map_metadata", "-1", dst])
        if wav:
            break
        # Vorbis overshoots the limiter a little (often 0.1-0.2 dB, on some voices 1 dB): measure the decoded
        # file and limit lower until it stays at TARGET_TP
        pk = re.search(r"max_volume:\s*(-?[\d.]+) dB", run(["-i", dst, "-af", "volumedetect", "-f", "null", "-"]))
        if not pk or float(pk.group(1)) <= TARGET_TP:
            break
        tp -= float(pk.group(1)) - TARGET_TP + 0.1
    if not wav and os.path.abspath(dst).startswith(os.path.join(REPO, "game") + os.sep):
        godot_import.write(dst)
    return duration(dst), {"input_i": i0, "gain_db": round(gain, 2)}


def load_lines():
    with open(LINES, encoding="utf-8") as f:
        return json.load(f)["lines"]


def preset_of(line):
    return line.get("preset") or line["speaker"]


def is_recording(entry):
    """A manifest entry made from the user's own recording (robot.py --batch), not from a voice generator."""
    return bool(entry) and not entry.get("placeholder", True) and entry.get("source") not in ("google", "sapi")


def load_manifest():
    if os.path.isfile(MANIFEST):
        with open(MANIFEST, encoding="utf-8") as f:
            return json.load(f)
    return {"about": "", "lines": {}}


def save_manifest(man):
    man["about"] = ("Voice files per line id and language, each through a robot-voice preset of tools/voice/robot.py. "
                    "source: recording (the user's own, placeholder=false), google (Google Chirp 3 HD voice named in "
                    "'voice', tools/voice/google_tts.py, placeholder=false) or sapi (Windows SAPI stand-in, "
                    "placeholder=true). A recording wins over a Google voice, which wins over SAPI "
                    "(tools/voice/README.md). seconds: length of the Ogg file.")
    man["lines"] = dict(sorted(man["lines"].items()))
    with open(MANIFEST, "w", encoding="utf-8", newline="\n") as f:
        json.dump(man, f, ensure_ascii=False, indent=1)
        f.write("\n")


def batch(langs, only, force):
    man = load_manifest()
    done = 0
    for line in load_lines():
        if line["speaker"] == "hud" or (only and line["id"] not in only):
            continue
        for lang in langs:
            src = os.path.join(RECORDINGS, lang, line["id"] + ".wav")
            if not os.path.isfile(src):
                continue
            dst = os.path.join(VOICE_OUT, lang, line["id"] + ".ogg")
            entry = man["lines"].setdefault(line["id"], {})
            rec_mtime = int(os.path.getmtime(src))
            old = entry.get(lang, {})
            if not force and is_recording(old) and old.get("source_mtime") == rec_mtime \
                    and old.get("preset") == preset_of(line) and os.path.isfile(dst):
                continue
            secs, _ = process(preset_of(line), src, dst, cut=line.get("cut", False))
            entry[lang] = {"file": "%s/%s.ogg" % (lang, line["id"]), "seconds": round(secs, 3),
                           "placeholder": False, "preset": preset_of(line), "source": "recording",
                           "source_mtime": rec_mtime}
            done += 1
            print("%-16s %s %.2f s" % (line["id"], lang, secs))
    save_manifest(man)
    print("%d recording(s) processed" % done)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("preset", nargs="?")
    ap.add_argument("src", nargs="?")
    ap.add_argument("dst", nargs="?")
    ap.add_argument("--pitch", type=float, default=1.0, help="extra pitch factor on top of the preset")
    ap.add_argument("--cut", action="store_true", help="no reverb tail: the line breaks off")
    ap.add_argument("--batch", action="store_true")
    ap.add_argument("--lang", choices=["de", "en"])
    ap.add_argument("--only", help="comma-separated line ids")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--list", action="store_true")
    a = ap.parse_args()
    if a.list:
        print("\n".join(PRESETS))
        return
    if a.batch:
        batch([a.lang] if a.lang else ["de", "en"], set(a.only.split(",")) if a.only else None, a.force)
        return
    if not (a.preset and a.src and a.dst):
        ap.error("need <preset> <in.wav> <out.ogg>, or --batch")
    secs, m = process(a.preset, a.src, a.dst, a.pitch, a.cut)
    print("%s: %.2f s (input %s LUFS)" % (a.dst, secs, m.get("input_i") if m else "?"))


if __name__ == "__main__":
    main()
