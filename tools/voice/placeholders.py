"""Placeholder voices: every voiced line of lines.json in German and English, spoken by
Windows' SAPI voices (sapi.ps1) and passed through robot.py, into
game/assets/story/voice/<lang>/<id>.ogg, marked "placeholder": true in the manifest.

    python tools/voice/placeholders.py [--force] [--only ID[,ID...]]

Lines the user has recorded are never overwritten, nor lines voiced by Google (google_tts.py) for a speaker
still cast in voices.json: a recording wins over a Google voice, which wins over SAPI.
Reruns redo only lines whose text, preset or tuning changed (hash in the manifest).
"""
import argparse
import hashlib
import json
import os
import re
import subprocess
import sys
import tempfile

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import robot  # noqa: E402
import google_tts  # noqa: E402

CULTURE = {"de": "de-DE", "en": "en-US"}
# SAPI only has female voices here (Hedda, Zira): pre-pitch per speaker so the robots differ.
TUNING = {  # speaker: (extra pitch, SAPI rate -10..10)
    "ship": (1.0, -1),
    "auto": (0.94, 0),
    "ike": (0.80, 1),
    "bob": (0.74, -1),
    "whisper": (0.96, -3),
    "l7": (0.90, 0),
}
VERSION = 3  # 2: measured gain + limiter instead of loudnorm; 3: limiter without delay


def tts_text(text):
    t = text.replace("„", "").replace("“", "").replace('"', "")
    t = t.replace("L-7", "L 7")
    t = re.sub(r"(\w)-\s*…", r"\1", t)   # broken-off words: "kor- …" -> "kor"
    t = t.replace("…", ",")
    return re.sub(r"\s+", " ", t).strip(" ,")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--only")
    a = ap.parse_args()
    only = set(a.only.split(",")) if a.only else None
    man = robot.load_manifest()
    cast = google_tts.load_cast()
    todo = []
    for line in robot.load_lines():
        if line["speaker"] == "hud" or (only and line["id"] not in only):
            continue
        pitch, rate = TUNING[line["speaker"]]
        for lang in ("de", "en"):
            old = man["lines"].get(line["id"], {}).get(lang, {})
            if robot.is_recording(old):
                continue  # the user's recording
            if old.get("source") == "google" and google_tts.cast_voice(cast, line["speaker"], lang):
                continue  # a Google voice
            h = hashlib.sha1(json.dumps([VERSION, line[lang], robot.preset_of(line), pitch, rate,
                                         line.get("cut", False), robot.graph(robot.preset_of(line), pitch,
                                                                              line.get("cut", False))],
                                        ensure_ascii=False).encode("utf-8")).hexdigest()[:12]
            dst = os.path.join(robot.VOICE_OUT, lang, line["id"] + ".ogg")
            if not a.force and old.get("hash") == h and os.path.isfile(dst):
                continue
            todo.append((line, lang, pitch, rate, h, dst))
    if not todo:
        print("all placeholders up to date")
        return
    with tempfile.TemporaryDirectory(prefix="voice-ph-") as tmp:
        jobs = []
        for i, (line, lang, pitch, rate, h, dst) in enumerate(todo):
            jobs.append({"text": tts_text(line[lang]), "culture": CULTURE[lang], "rate": rate,
                         "out": os.path.join(tmp, "%03d.wav" % i).replace("\\", "/")})
        jf = os.path.join(tmp, "jobs.json")
        with open(jf, "w", encoding="utf-8") as f:
            json.dump(jobs, f, ensure_ascii=False)
        subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File",
                        os.path.join(robot.HERE, "sapi.ps1"), "-Jobs", jf], check=True,
                       stdout=subprocess.DEVNULL)
        for (line, lang, pitch, rate, h, dst), job in zip(todo, jobs):
            secs, _ = robot.process(robot.preset_of(line), job["out"], dst, pitch, line.get("cut", False))
            man["lines"].setdefault(line["id"], {})[lang] = {
                "file": "%s/%s.ogg" % (lang, line["id"]), "seconds": round(secs, 3), "placeholder": True,
                "preset": robot.preset_of(line), "source": "sapi", "hash": h}
            print("%-16s %s %.2f s" % (line["id"], lang, secs))
    robot.save_manifest(man)


if __name__ == "__main__":
    main()
