"""Tones down the red paint of the story's ship props (game/assets/story/props/*.glb).

Meshy painted the deck's kit (floors, walls, ceilings, doors, ducts, pillars) and the set pieces in a loud signal red.
This greys that red out in each prop's base-colour texture, inside the .glb (Godot extracts the textures from it on
import, as <prop>_0.jpg ...; those files are not committed): a pixel whose hue is red keeps only KEEP of its colour
around its own luminance, so the paint reads as gunmetal with a slight red tint. Other hues (the yellow-black hazard
stripes, orange, green or blue screen light) stay as they are; the toning fades out between RED_FULL and RED_NONE
degrees of hue from pure red. The normal and metal-roughness textures are left alone, and so are the lamps (LEFT):
their red is the story's alarm. The set pieces in NEUTRAL lose their red entirely (keep 0), and on them the toning
reaches further towards purple (full to PURPLE_FULL, gone at PURPLE_NONE degrees), where Meshy's maroon shades off, so
they wear the plain grey of the rebuilt deck kit. Props no longer Meshy's (their .json sidecar names another source,
like the deck kit rebuilt in Blender) are never touched.

It always works from the originals as committed in ORIGINALS (read with git show), so it can be run again with another
KEEP without toning twice. After running it the .glb files have to be imported again. Not with --import in the main
checkout (it rewrites other .import files there): a scratch project (a project.godot with config_version=5 and
assets/story/props/ holding the changed .glb files with their .glb.import and <prop>_N.jpg.import files) imports them
with godot --headless --import; its .godot/imported/* and the extracted <prop>_0.jpg(.import) files are then copied into
game/.godot/imported and game/assets/story/props. The paths, and so the imported file names, are the same.

    uv run --with pillow --with numpy python tools/deck/tone_props.py [keep] [prop ...]

Naming props tones only those.
"""
import io
import json
import struct
import subprocess
import sys
from pathlib import Path

import numpy as np
from PIL import Image

# How much of a red pixel's colour is kept (0 = plain grey, 1 = as Meshy painted it).
KEEP = 0.15
# Hue distance from pure red (degrees) toned fully, and where the toning has faded out (orange-yellow stays).
RED_FULL = 16.0
RED_NONE = 30.0
# The commit holding Meshy's original props.
ORIGINALS = "907fa38"
# Props left as they are: their red is a lamp's.
LEFT = {"alarm_beacon", "lamp_emergency"}
# Set pieces toned to plain grey, to stand beside the rebuilt deck kit.
NEUTRAL = {"barrel", "cable_hang", "clamp_terminal", "core_console", "crate_large", "crate_small", "debris_beam",
           "debris_panel", "generator", "junction_box", "pipe_run", "pod_cradle", "repair_station", "wall_screen"}
# On those, the hue distance on the purple side of red toned fully, and where it has faded out (blue stays).
PURPLE_FULL = 50.0
PURPLE_NONE = 75.0

ROOT = Path(__file__).resolve().parents[2]
FOLDER = "game/assets/story/props"


def toned(rgb: np.ndarray, keep: float, purple: tuple[float, float] = (RED_FULL, RED_NONE)) -> np.ndarray:
    a = rgb.astype(np.float32) / 255.0
    r, g, b = a[..., 0], a[..., 1], a[..., 2]
    mx, mn = a.max(-1), a.min(-1)
    delta = mx - mn
    safe = np.maximum(delta, 1e-6)
    hue = np.where(mx == r, ((g - b) / safe) % 6.0, np.where(mx == g, (b - r) / safe + 2.0, (r - g) / safe + 4.0)) * 60.0
    full, none = purple
    weight = np.where(hue <= 180.0, np.clip((RED_NONE - hue) / (RED_NONE - RED_FULL), 0.0, 1.0),
                      np.clip((none - (360.0 - hue)) / (none - full), 0.0, 1.0))
    weight = np.where(delta > 1e-6, weight, 0.0)
    luma = (0.2126 * r + 0.7152 * g + 0.0722 * b)[..., None]
    factor = (1.0 - weight * (1.0 - keep))[..., None]
    out = luma + (a - luma) * factor
    return (np.clip(out, 0.0, 1.0) * 255.0 + 0.5).astype(np.uint8)


def chunks(glb: bytes) -> tuple[dict, bytes]:
    magic, version, _ = struct.unpack_from("<III", glb, 0)
    assert magic == 0x46546C67 and version == 2, "not a glTF 2 binary"
    length, kind = struct.unpack_from("<II", glb, 12)
    assert kind == 0x4E4F534A
    doc = json.loads(glb[20 : 20 + length])
    at = 20 + length
    length, kind = struct.unpack_from("<II", glb, at)
    assert kind == 0x004E4942
    return doc, glb[at + 8 : at + 8 + length]


def packed(doc: dict, views: list[bytes]) -> bytes:
    binary = bytearray()
    for view, data in zip(doc["bufferViews"], views):
        binary += b"\0" * (-len(binary) % 4)
        view["byteOffset"] = len(binary)
        view["byteLength"] = len(data)
        binary += data
    binary += b"\0" * (-len(binary) % 4)
    doc["buffers"] = [{"byteLength": len(binary)}]
    text = json.dumps(doc, separators=(",", ":")).encode()
    text += b" " * (-len(text) % 4)
    total = 12 + 8 + len(text) + 8 + len(binary)
    return struct.pack("<III", 0x46546C67, 2, total) + struct.pack("<II", len(text), 0x4E4F534A) + text \
        + struct.pack("<II", len(binary), 0x004E4942) + bytes(binary)


def tone_glb(glb: bytes, keep: float, purple: tuple[float, float] = (RED_FULL, RED_NONE)) -> bytes:
    doc, binary = chunks(glb)
    assert all(v.get("buffer", 0) == 0 for v in doc["bufferViews"]) and len(doc["buffers"]) == 1
    views = [binary[v.get("byteOffset", 0) : v.get("byteOffset", 0) + v["byteLength"]] for v in doc["bufferViews"]]
    bases = {doc["textures"][m["pbrMetallicRoughness"]["baseColorTexture"]["index"]]["source"]
             for m in doc.get("materials", []) if "baseColorTexture" in m.get("pbrMetallicRoughness", {})}
    for source in sorted(bases):
        image = doc["images"][source]
        view = image["bufferView"]
        picture = Image.open(io.BytesIO(views[view]))
        alpha = picture.getchannel("A") if picture.mode in ("RGBA", "LA") else None
        out = Image.fromarray(toned(np.asarray(picture.convert("RGB")), keep, purple))
        buffer = io.BytesIO()
        if image["mimeType"] == "image/png":
            if alpha is not None:
                out.putalpha(alpha)
            out.save(buffer, "PNG", optimize=True)
        else:
            out.save(buffer, "JPEG", quality=95, subsampling=0)
        views[view] = buffer.getvalue()
    return packed(doc, views)


def meshy(path: str) -> bool:
    """Whether the prop is still Meshy's, by its sidecar (the rebuilt deck kit is not)."""
    sidecar = (ROOT / path).with_suffix(".json")
    return not sidecar.exists() or json.loads(sidecar.read_text(encoding="utf-8")).get("source", "").startswith("Meshy")


def main() -> None:
    keep = float(sys.argv[1]) if len(sys.argv) > 1 else KEEP
    only = set(sys.argv[2:])
    names = subprocess.run(["git", "ls-tree", "--name-only", ORIGINALS, FOLDER + "/"], cwd=ROOT, check=True,
                           capture_output=True, text=True).stdout.split()
    for path in sorted(n for n in names if n.endswith(".glb")):
        stem = Path(path).stem
        if stem in LEFT or only and stem not in only or not meshy(path):
            continue
        glb = subprocess.run(["git", "show", f"{ORIGINALS}:{path}"], cwd=ROOT, check=True, capture_output=True).stdout
        neutral = stem in NEUTRAL
        (ROOT / path).write_bytes(tone_glb(glb, 0.0 if neutral else keep, (PURPLE_FULL, PURPLE_NONE) if neutral else (RED_FULL, RED_NONE)))
        print(path, "grey" if neutral else keep)


if __name__ == "__main__":
    main()
