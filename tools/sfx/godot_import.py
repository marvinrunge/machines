"""Writes Godot's <file>.ogg.import next to a new .ogg, like the committed rain sounds have, so a
fresh clone or a headless run finds the stream without opening the editor first. An existing
.import keeps its uid; only the loop flag is updated. Godot fills in the rest on its next import."""
import hashlib
import os
import re
import secrets

REPO = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), "..", ".."))
GAME = os.path.join(REPO, "game")


def _uid_text(n):
    # Godot's ResourceUID::id_to_text as 4.7 writes it: base 34, digits a-y then 0-8 (Godot rewrote every "z" this
    # wrote as "0" on its first import; no uid in the repo has a "z" or a "9").
    chars = "abcdefghijklmnopqrstuvwxy012345678"
    s = ""
    while True:
        s = chars[n % 34] + s
        n //= 34
        if n == 0:
            break
    return "uid://" + s


def write(ogg_path, loop=False):
    res = "res://" + os.path.relpath(os.path.abspath(ogg_path), GAME).replace("\\", "/")
    name = os.path.basename(ogg_path)
    dest = "res://.godot/imported/%s-%s.oggvorbisstr" % (name, hashlib.md5(res.encode("utf-8")).hexdigest())
    imp = ogg_path + ".import"
    uid = None
    if os.path.isfile(imp):
        with open(imp, encoding="utf-8") as f:
            m = re.search(r'uid="(uid://[a-z0-9]+)"', f.read())
            uid = m.group(1) if m else None
    uid = uid or _uid_text(secrets.randbits(62) | 1)
    text = """[remap]

importer="oggvorbisstr"
type="AudioStreamOggVorbis"
uid="{uid}"
path="{dest}"

[deps]

source_file="{res}"
dest_files=["{dest}"]

[params]

loop={loop}
loop_offset=0
bpm=0
beat_count=0
bar_beats=4
""".format(uid=uid, dest=dest, res=res, loop="true" if loop else "false")
    with open(imp, "w", encoding="utf-8", newline="\n") as f:
        f.write(text)
