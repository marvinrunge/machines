"""Checks the story's audio files without listening: length, loudness, peaks, clipping, loop seams.

    python tools/sfx/check.py [--md tools/sfx/REPORT-table.md] [dirs...]

Default dirs: game/assets/story/{voice/de,voice/en,sfx,music}. For each .ogg:
  seconds, integrated loudness (EBU R128, LUFS), true peak (dBTP), sample peak (dBFS),
  clipped samples (|x| >= 0.999), and for loops (manifest loop=true) the seam:
  RMS of the last vs the first 50 ms (dB) and the sample jump across the wrap
  compared with the 99.9th percentile sample-to-sample step ("jump x"; worst channel).
Warns when: sample peak > -0.5 dBFS, any clipped sample, loudness off by > 3 LU from the
manifest target, a loop seam whose RMS differs by > 6 dB or whose jump is over 1.5x the 99.9th percentile step.
"""
import argparse
import array
import glob
import json
import math
import os
import re
import subprocess
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, os.path.join(REPO, "tools", "voice"))
from robot import FFMPEG  # noqa: E402

STORY = os.path.join(REPO, "game", "assets", "story")


def ff(args):
    return subprocess.run([FFMPEG, "-hide_banner", "-nostdin"] + args, capture_output=True,
                          text=True, encoding="utf-8", errors="replace").stderr


def measure(path):
    err = ff(["-i", path, "-af", "ebur128=peak=true+sample,volumedetect", "-f", "null", "-"])
    out = {}
    m = re.search(r"Summary:.*?I:\s*(-?[\d.]+|-inf) LUFS.*?True peak:\s*Peak:\s*(-?[\d.]+|-inf) dBFS", err, re.S)
    if m:
        out["lufs"] = float(m.group(1)) if m.group(1) != "-inf" else -99.0
        out["tp"] = float(m.group(2)) if m.group(2) != "-inf" else -99.0
    m = re.search(r"max_volume:\s*(-?[\d.]+) dB", err)
    out["peak"] = float(m.group(1)) if m else -99.0
    t = re.findall(r"time=(\d+):(\d+):(\d+\.\d+)", err)
    if t:
        h, mi, s = t[-1]
        out["seconds"] = int(h) * 3600 + int(mi) * 60 + float(s)
    return out


def pcm(path):
    raw = subprocess.run([FFMPEG, "-hide_banner", "-nostdin", "-i", path, "-f", "f32le", "-"],
                         capture_output=True).stdout
    a = array.array("f")
    a.frombytes(raw[: len(raw) // 4 * 4])
    return a


def rms(xs):
    return math.sqrt(sum(x * x for x in xs) / max(1, len(xs))) if xs else 0.0


def db(x):
    return 20 * math.log10(x) if x > 1e-9 else -180.0


def seam(a, ch):
    """Loop seam per channel, worst channel: RMS of the last vs first 50 ms, and the sample jump across
    the wrap relative to the 99.9th percentile of the file's own sample-to-sample steps (a click is >> 1)."""
    worst = {"seam_db": 0.0, "jump_x": 0.0}
    for c in range(ch):
        x = a[c::ch]
        n = 2205  # 50 ms at 44.1 kHz (close enough at 48 kHz)
        sdb = db(rms(x[-n:])) - db(rms(x[:n]))
        steps = sorted(abs(x[i + 1] - x[i]) for i in range(0, len(x) - 1, max(1, len(x) // 50000)))
        p999 = steps[int(len(steps) * 0.999)] if steps else 0.0
        jx = abs(x[0] - x[-1]) / p999 if p999 > 1e-7 else 0.0
        if abs(sdb) > abs(worst["seam_db"]):
            worst["seam_db"] = round(sdb, 1)
        worst["jump_x"] = max(worst["jump_x"], round(jx, 2))
    return worst


def clipped(a):
    return sum(1 for x in a if abs(x) >= 0.999)


def channels(path):
    err = ff(["-i", path])
    return 2 if re.search(r"Audio:.*stereo", err) else 1


def manifests():
    info = {}
    for sub in ("sfx", "music"):
        p = os.path.join(STORY, sub, "manifest.json")
        if os.path.isfile(p):
            with open(p, encoding="utf-8") as f:
                for k, v in json.load(f).get("files", {}).items():
                    info[os.path.normcase(os.path.join(STORY, sub, v["file"]))] = v
    return info


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("dirs", nargs="*")
    ap.add_argument("--md", help="write a markdown table here")
    ap.add_argument("--json", help="write the raw results here")
    a = ap.parse_args()
    dirs = a.dirs or [os.path.join(STORY, d) for d in ("voice/de", "voice/en", "sfx", "music")]
    info = manifests()
    rows, warns = [], []
    for d in dirs:
        for p in sorted(glob.glob(os.path.join(d, "*.ogg"))):
            meta = info.get(os.path.normcase(os.path.abspath(p)), {})
            r = {"file": os.path.relpath(p, STORY).replace("\\", "/")}
            r.update(measure(p))
            s = pcm(p)
            r["clipped"] = clipped(s)
            loop = bool(meta.get("loop"))
            r["loop"] = loop
            if loop:
                r.update(seam(s, channels(p)))
            target = meta.get("target_lufs", -18.0 if "voice/" in r["file"] else None)
            w = []
            if r.get("peak", -99) > -0.5:
                w.append("peak %.1f dBFS" % r["peak"])
            if r["clipped"]:
                w.append("%d clipped samples" % r["clipped"])
            if target is not None and r.get("lufs", -99) > -70 and r.get("seconds", 0) >= 1.0 \
                    and abs(r["lufs"] - target) > 3:
                w.append("loudness %.1f vs target %.1f" % (r["lufs"], target))
            if loop and (abs(r["seam_db"]) > 6 or r["jump_x"] > 1.5):
                w.append("loop seam %+.1f dB, jump %.1fx" % (r["seam_db"], r["jump_x"]))
            r["warn"] = "; ".join(w)
            if w:
                warns.append("%s: %s" % (r["file"], r["warn"]))
            rows.append(r)
            print("%-40s %6.2fs %6.1f LUFS tp %5.1f pk %5.1f clip %d %s %s" % (
                r["file"], r.get("seconds", 0), r.get("lufs", -99), r.get("tp", -99), r.get("peak", -99),
                r["clipped"], ("seam %+.1f dB jump %.1fx" % (r["seam_db"], r["jump_x"])) if loop else "",
                ("WARN " + r["warn"]) if r["warn"] else ""))
    print("\n%d files, %d with warnings" % (len(rows), len(warns)))
    for w in warns:
        print("  " + w)
    if a.json:
        with open(a.json, "w", encoding="utf-8") as f:
            json.dump(rows, f, indent=1)
    if a.md:
        with open(a.md, "w", encoding="utf-8", newline="\n") as f:
            f.write("| File | s | LUFS | dBTP | peak dBFS | clipped | loop seam | warning |\n|---|---|---|---|---|---|---|---|\n")
            for r in rows:
                f.write("| %s | %.2f | %.1f | %.1f | %.1f | %d | %s | %s |\n" % (
                    r["file"], r.get("seconds", 0), r.get("lufs", -99), r.get("tp", -99), r.get("peak", -99),
                    r["clipped"], ("%+.1f dB, %.1fx" % (r["seam_db"], r["jump_x"])) if r["loop"] else "",
                    r["warn"]))


if __name__ == "__main__":
    main()
