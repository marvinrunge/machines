"""Whitens the backdrop of Gemini prop pictures for Meshy: every near-white pixel connected to the picture's border
becomes pure white; the object, its pale parts inside the outline and the raw picture stay untouched. Runs in Blender
for numpy and the image reader:

    blender -b --factory-startup --python tools/meshy/whiten.py -- [--force] <props dir or input-raw.png> ...

For each <name>/input-raw.png it writes <name>/input.png beside it (skipped when input.png is newer, unless --force)."""
import struct
import sys
import zlib
from pathlib import Path

import bpy
import numpy as np

TOLERANCE = 26   # how far below the backdrop's own brightness a pixel may be and still count as backdrop
CHROMA = 18      # how coloured a backdrop pixel may be (max - min channel)


def read(path):
    image = bpy.data.images.load(str(path))
    w, h = image.size
    flat = np.empty(w * h * 4, dtype=np.float32)
    image.pixels.foreach_get(flat)
    bpy.data.images.remove(image)
    # Blender's rows run bottom-up; byte images give their stored values divided by 255.
    return np.flipud(np.round(flat.reshape(h, w, 4)[:, :, :3] * 255).astype(np.uint8))


def write_png(path, rgb):
    h, w, _ = rgb.shape
    rows = b"".join(b"\x00" + rgb[y].tobytes() for y in range(h))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) +
                     chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b""))


def backdrop_mask(rgb):
    h, w, _ = rgb.shape
    border = np.concatenate([rgb[:4].reshape(-1, 3), rgb[-4:].reshape(-1, 3),
                             rgb[:, :4].reshape(-1, 3), rgb[:, -4:].reshape(-1, 3)])
    level = int(np.median(border.min(axis=1)))
    lo, hi = rgb.min(axis=2).astype(np.int16), rgb.max(axis=2).astype(np.int16)
    candidate = (lo >= level - TOLERANCE) & (hi - lo <= CHROMA)
    reached = np.zeros_like(candidate)
    reached[0, :], reached[-1, :], reached[:, 0], reached[:, -1] = (
        candidate[0, :], candidate[-1, :], candidate[:, 0], candidate[:, -1])
    # Flood fill by repeated 8-neighbour growth inside the candidate pixels; checked for change every 16 steps.
    count = -1
    while True:
        for _ in range(16):
            grown = reached.copy()
            grown[1:] |= reached[:-1]
            grown[:-1] |= reached[1:]
            grown[:, 1:] |= reached[:, :-1]
            grown[:, :-1] |= reached[:, 1:]
            grown[1:, 1:] |= reached[:-1, :-1]
            grown[:-1, :-1] |= reached[1:, 1:]
            grown[1:, :-1] |= reached[:-1, 1:]
            grown[:-1, 1:] |= reached[1:, :-1]
            reached = grown & candidate
        now = int(reached.sum())
        if now == count:
            return reached, level
        count = now


def whiten(raw, force):
    out = raw.with_name("input.png")
    if out.is_file() and not force and out.stat().st_mtime > raw.stat().st_mtime:
        print(f"{raw.parent.name}: input.png is current, skipped", flush=True)
        return
    rgb = read(raw)
    mask, level = backdrop_mask(rgb)
    white = rgb.copy()
    white[mask] = 255
    write_png(out, white)
    print(f"{raw.parent.name}: backdrop level {level}, {100 * mask.mean():.0f} % whitened", flush=True)


def main(args):
    force = "--force" in args
    for arg in (a for a in args if a != "--force"):
        path = Path(arg).resolve()
        raws = sorted(path.glob("*/input-raw.png")) if path.is_dir() else [path]
        for raw in raws:
            whiten(raw, force)


main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
