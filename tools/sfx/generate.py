"""Generates the story's sound effects and music with ElevenLabs and turns them into game-ready Ogg files.

    python tools/sfx/generate.py                 # sound effects of sfx.json -> game/assets/story/sfx/
    python tools/sfx/generate.py --music         # music cues of music.json -> game/assets/story/music/
    python tools/sfx/generate.py --only amb_fire,pa_gong [--force] [--dry-run]

Only entries that are missing or changed are generated: each entry's request (prompt, seconds, loop,
influence, model) is hashed; the raw download is kept in tools/sfx/.cache/<id>-<hash>.mp3 (not
committed), so changing only gain or stereo reprocesses without a new request. --force regenerates.

Processing: decode, mono unless stereo, then
  one-shot: trim silence at both ends, 3 ms fade-in, 30 ms fade-out;
  loop:     equal-power crossfade of the end into the start (sfx: the last `xf` seconds; music: the loop
            length is chosen where the material after it matches the start best, skipping any
            fade-in/fade-out), so the file wraps without a seam;
normalised to the reference loudness + gain (measured with EBU R128), limited to -1.5 dBFS only where a
peak would pass it, encoded Ogg Vorbis q5, with a Godot .import next to it. Manifest: manifest.json.

The key comes from ELEVENLABS_API_KEY (process env, else the Windows user env); it is never printed.
When the API refuses for permission or quota reasons the run stops, keeps what is done and exits 2.
"""
import argparse
import array
import hashlib
import json
import math
import os
import re
import subprocess
import sys
import tempfile
import urllib.error
import urllib.request

HERE = os.path.dirname(os.path.abspath(__file__))
REPO = os.path.abspath(os.path.join(HERE, "..", ".."))
sys.path.insert(0, HERE)
sys.path.insert(0, os.path.join(REPO, "tools", "voice"))
import godot_import  # noqa: E402
from robot import FFMPEG  # noqa: E402

CACHE = os.path.join(HERE, ".cache")
API = "https://api.elevenlabs.io"
PROC_VERSION = 3
PEAK_LIMIT = -1.5


class ApiError(Exception):
    def __init__(self, status, body):
        super().__init__("HTTP %s: %s" % (status, body))
        self.status, self.body = status, body

    @property
    def fatal(self):
        b = self.body.lower()
        return self.status in (401, 402, 403, 429) or any(
            w in b for w in ("quota", "credits", "permission", "not allowed", "subscription", "unauthorized"))


def api_key():
    k = os.environ.get("ELEVENLABS_API_KEY")
    if not k and os.name == "nt":
        k = subprocess.run(["powershell", "-NoProfile", "-Command",
                            "[Environment]::GetEnvironmentVariable('ELEVENLABS_API_KEY','User')"],
                           capture_output=True, text=True).stdout.strip()
    if not k:
        sys.exit("ELEVENLABS_API_KEY is not set")
    return k


def post(path, body, out, key, timeout=600):
    req = urllib.request.Request(API + path, data=json.dumps(body).encode("utf-8"), method="POST",
                                 headers={"xi-api-key": key, "Content-Type": "application/json",
                                          "Accept": "audio/mpeg"})
    try:
        with urllib.request.urlopen(req, timeout=timeout) as r:
            data = r.read()
            cost = r.headers.get("character-cost")
    except urllib.error.HTTPError as e:
        raise ApiError(e.code, e.read().decode("utf-8", "replace")[:600])
    if len(data) < 1000:
        raise ApiError(200, "suspiciously small response (%d bytes)" % len(data))
    with open(out, "wb") as f:
        f.write(data)
    return cost


def ff(args):
    r = subprocess.run([FFMPEG, "-hide_banner", "-nostdin"] + args, capture_output=True,
                       text=True, encoding="utf-8", errors="replace")
    if r.returncode != 0:
        raise RuntimeError("ffmpeg failed:\n" + r.stderr[-2000:])
    return r.stderr


def wav_seconds(path):
    """Length of a PCM (16 bit) or float WAV from its fmt and data chunks."""
    with open(path, "rb") as f:
        data = f.read()
    rate = int.from_bytes(data[24:28], "little")
    block = int.from_bytes(data[32:34], "little")
    i = 12
    while i + 8 <= len(data):
        cid, size = data[i:i + 4], int.from_bytes(data[i + 4:i + 8], "little")
        if cid == b"data":
            return min(size, len(data) - i - 8) / block / rate
        i += 8 + size + (size & 1)
    return 0.0


def loudness(path):
    err = ff(["-i", path, "-af", "ebur128=framelog=quiet,volumedetect", "-f", "null", "-"])
    i = re.search(r"Summary:.*?I:\s*(-?[\d.]+|-inf) LUFS", err, re.S)
    pk = re.search(r"max_volume:\s*(-?[\d.]+) dB", err)
    return (float(i.group(1)) if i and i.group(1) != "-inf" else -99.0,
            float(pk.group(1)) if pk else -99.0)


def envelope(path, hop=0.01):
    """Log energy per 10 ms frame (mono, 11 kHz)."""
    raw = subprocess.run([FFMPEG, "-hide_banner", "-nostdin", "-i", path, "-ac", "1", "-ar", "11025",
                          "-f", "s16le", "-"], capture_output=True).stdout
    a = array.array("h")
    a.frombytes(raw[: len(raw) // 2 * 2])
    n = int(11025 * hop)
    env = []
    for i in range(0, len(a) - n, n):
        s = sum(x * x for x in a[i:i + n]) / n
        env.append(10 * math.log10(s + 1.0))
    return env, hop


def loop_points(path, xf, search):
    """(start, length) of the loop body in seconds. The crossfade uses [start+length, +xf)."""
    total = wav_seconds(path)
    if not search:
        return 0.0, total - xf
    env, hop = envelope(path)
    med = sorted(env)[len(env) // 2]
    ok = [i for i, e in enumerate(env) if e > med - 8]
    s0 = ok[0] if ok else 0
    e0 = ok[-1] if ok else len(env) - 1           # past this the piece fades out
    xfn = int(xf / hop)
    avail = e0 - s0 - xfn
    w = min(int(6 / hop), avail // 3)
    flux = [max(0.0, env[i] - env[i - 1]) if i else 0.0 for i in range(len(env))]
    ref = flux[s0:s0 + w]
    ref_m = sum(ref) / w
    ref_c = [x - ref_m for x in ref]
    ref_n = math.sqrt(sum(x * x for x in ref_c)) or 1.0
    best, best_n = -2.0, avail
    for n in range(int(avail * 0.6), avail - w + xfn + 1):
        seg = flux[s0 + n:s0 + n + w]
        if len(seg) < w:
            break
        m = sum(seg) / w
        num = sum(r * (x - m) for r, x in zip(ref_c, seg))
        den = math.sqrt(sum((x - m) ** 2 for x in seg)) or 1.0
        c = num / (ref_n * den)
        if c > best:
            best, best_n = c, n
    return s0 * hop, best_n * hop


def process(raw, dst, loop, stereo, lufs, xf=1.0, search=False):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with tempfile.TemporaryDirectory(prefix="sfx-") as tmp:
        dec = os.path.join(tmp, "dec.wav")
        ff(["-y", "-i", raw, "-ac", "2" if stereo else "1", "-ar", "44100", "-c:a", "pcm_s16le", dec])
        mid = os.path.join(tmp, "mid.wav")
        info = {}
        if loop:
            total = wav_seconds(dec)
            xf = min(xf, total * 0.1)
            s, n = loop_points(dec, xf, search)
            # In samples, so the pieces meet exactly: out = fadein(a[S:S+X]) + fadeout(a[S+N:S+N+X]),
            # then a[S+X:S+N]. The last sample is a[S+N-1] and the first is (almost) a[S+N]: no seam.
            S, N, X = round(s * 44100), round(n * 44100), round(xf * 44100)
            g = ("[0:a]asplit=3[a][b][c];"
                 "[a]atrim=start_sample={S}:end_sample={SX},asetpts=PTS-STARTPTS,afade=t=in:ns={X}:curve=qsin[hi];"
                 "[b]atrim=start_sample={SX}:end_sample={SN},asetpts=PTS-STARTPTS[body];"
                 "[c]atrim=start_sample={SN}:end_sample={SNX},asetpts=PTS-STARTPTS,"
                 "afade=t=out:ns={X}:curve=qsin[to];"
                 "[hi][to]amix=inputs=2:normalize=0:duration=first[x];"
                 "[x][body]concat=n=2:v=0:a=1[out]").format(S=S, SX=S + X, SN=S + N, SNX=S + N + X, X=X)
            ff(["-y", "-i", dec, "-filter_complex", g, "-map", "[out]", "-c:a", "pcm_f32le", mid])
            info.update(loop_start=round(s, 3), crossfade=round(xf, 3))
        else:
            ff(["-y", "-i", dec, "-af",
                "silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.02,areverse,"
                "silenceremove=start_periods=1:start_threshold=-55dB:start_silence=0.05,"
                "afade=t=in:d=0.03,areverse,afade=t=in:d=0.003", "-c:a", "pcm_f32le", mid])
        i0, pk0 = loudness(mid)
        gain = (lufs - i0) if i0 > -60 else (-6.0 - pk0)   # too short to gate: peak to -6 dBFS
        chain = "volume=%.2fdB" % gain
        if pk0 + gain > PEAK_LIMIT:
            chain += ",alimiter=limit=%.4f:level=false:attack=3:release=60:latency=1" % (10 ** (PEAK_LIMIT / 20))
        ff(["-y", "-i", mid, "-af", chain, "-c:a", "libvorbis", "-q:a", "5", "-map_metadata", "-1", dst])
        info.update(seconds=round(wav_seconds(mid), 3), gain_db=round(gain, 2),
                    limited=pk0 + gain > PEAK_LIMIT)
    godot_import.write(dst, loop)
    return info


def sha(obj):
    return hashlib.sha1(json.dumps(obj, sort_keys=True, ensure_ascii=False).encode("utf-8")).hexdigest()[:12]


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--music", action="store_true")
    ap.add_argument("--only")
    ap.add_argument("--force", action="store_true")
    ap.add_argument("--dry-run", action="store_true", help="list what would be requested")
    a = ap.parse_args()
    music = a.music
    with open(os.path.join(HERE, "music.json" if music else "sfx.json"), encoding="utf-8") as f:
        spec = json.load(f)
    d = spec["defaults"]
    entries = spec["cues" if music else "sounds"]
    out_dir = os.path.join(REPO, "game", "assets", "story", "music" if music else "sfx")
    man_path = os.path.join(out_dir, "manifest.json")
    man = {"files": {}}
    if os.path.isfile(man_path):
        with open(man_path, encoding="utf-8") as f:
            man = json.load(f)
    only = set(a.only.split(",")) if a.only else None
    os.makedirs(CACHE, exist_ok=True)
    key = None
    done, failed, stopped = [], [], None
    for e in entries:
        if only and e["id"] not in only:
            continue
        model = e.get("model", d["model"])
        if music:
            loop, stereo = True, True
            req = {"prompt": e["prompt"], "music_length_ms": int(e["seconds"] * 1000), "model_id": model,
                   "force_instrumental": True}
            path = "/v1/music"
            lufs = d["lufs"] + e.get("gain", 0)
        else:
            loop = bool(e.get("loop", False))
            stereo = bool(e.get("stereo", loop))
            req = {"text": e["prompt"], "duration_seconds": e["seconds"],
                   "prompt_influence": e.get("influence", d["influence"]), "model_id": model}
            if loop:
                req["loop"] = True
            path = "/v1/sound-generation"
            lufs = (d["loop_lufs"] if loop else d["oneshot_lufs"]) + e.get("gain", 0)
        gen_hash = sha([path, req])
        xf = e.get("crossfade", d.get("crossfade", 1.0))
        proc_hash = sha([gen_hash, PROC_VERSION, loop, stereo, lufs, xf])
        dst = os.path.join(out_dir, e["id"] + ".ogg")
        old = man["files"].get(e["id"], {})
        if not a.force and old.get("hash") == proc_hash and os.path.isfile(dst):
            continue
        raw = os.path.join(CACHE, "%s-%s.mp3" % (e["id"], gen_hash))
        if a.dry_run:
            print("would %s %s" % ("process" if os.path.isfile(raw) and not a.force else "generate", e["id"]))
            continue
        cost = None
        if a.force or not os.path.isfile(raw):
            key = key or api_key()
            try:
                try:
                    cost = post(path, req, raw, key)
                except ApiError as err:
                    if err.status >= 500:
                        cost = post(path, req, raw, key)   # one retry on a server error
                    else:
                        raise
            except ApiError as err:
                print("%-20s FAILED %s" % (e["id"], err))
                failed.append({"id": e["id"], "status": err.status, "error": err.body})
                if err.fatal:
                    stopped = err
                    break
                continue
        info = process(raw, dst, loop, stereo, lufs, xf, search=music)
        man["files"][e["id"]] = dict(file=e["id"] + ".ogg", loop=loop, stereo=stereo, target_lufs=lufs,
                                     source="elevenlabs", model=model, prompt=e["prompt"],
                                     requested_seconds=e["seconds"], gen_hash=gen_hash, hash=proc_hash, **info)
        done.append(e["id"])
        print("%-20s %6.2f s gain %+.1f dB%s%s" % (e["id"], info["seconds"], info["gain_db"],
                                                     " (limited)" if info["limited"] else "",
                                                     ("  cost %s" % cost) if cost else ""))
        with open(man_path, "w", encoding="utf-8", newline="\n") as f:   # keep progress if a later one stops
            man["about"] = ("Generated with ElevenLabs (%s) by tools/sfx/generate.py from tools/sfx/%s. "
                            "seconds: file length; loop files wrap seamlessly." %
                            ("music" if music else "sound generation", "music.json" if music else "sfx.json"))
            man["files"] = dict(sorted(man["files"].items()))
            json.dump(man, f, ensure_ascii=False, indent=1)
            f.write("\n")
    print("\n%d processed, %d failed" % (len(done), len(failed)))
    if failed:
        with open(os.path.join(CACHE, "last-errors-%s.json" % ("music" if music else "sfx")), "w",
                  encoding="utf-8") as f:
            json.dump(failed, f, indent=1)
    if stopped:
        print("STOPPED: the API refused (%s). Tool and list kept; rerun when the key or quota allows." % stopped)
        sys.exit(2)


if __name__ == "__main__":
    main()
