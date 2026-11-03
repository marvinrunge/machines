"""The tyres' tread as a texture: the remade tyres are plain turned bodies, and their blocks and grooves come from this
tile's normal map (modelled blocks cost 1280 triangles a tyre, nearly all of a wheeled machine). It writes albedo.png,
normal.png and orm.png (occlusion, roughness, metal) to classic/remake/textures/tyre, drawn from a height field:

    blender -b --factory-startup -P tools/blender/tyre_texture.py -- <classic folder>

tools/blender/upgrade_classic.py runs it too. Round the tyre (u) the tile holds one block pitch, TREAD_LUGS of them go
round; across (v) the tread takes the tile from the top down to TREAD_SHARE, in rows of blocks, each row set half a
pitch round from the next. Below it lies plain rubber for the shoulders and sides."""
import os
import sys

import bpy
import numpy as np

SIZE = 256
TREAD_SHARE = 0.8
ROWS = (0, 0.24, 0.5, 0.76, 1)
# The share of the pitch a block takes round the tyre.
BLOCK = 0.68
# The grooves' width (shares of the tile round and of the tread across) and how steep the blocks' sides look.
GROOVE_ROUND, GROOVE_ACROSS = 0.05, 0.03
STRENGTH = 5.0
# Rubber: the tyres' dark grey (linear), a little darker in the grooves and paler where the blocks wear.
RUBBER = 0.0152
ROUGHNESS = 0.85


def smooth_step(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0, 1)
    return t * t * (3 - 2 * t)


def build():
    rng = np.random.default_rng(11)
    rows, cols = np.mgrid[0:SIZE, 0:SIZE]
    u = (cols + 0.5) / SIZE
    v = (rows + 0.5) / SIZE / TREAD_SHARE
    # Distance to the nearest groove, round the tyre and across it, for the blocks of each row.
    height = np.zeros((SIZE, SIZE))
    for row, (top, bottom) in enumerate(zip(ROWS, ROWS[1:])):
        inside = (v >= top) & (v < bottom)
        shift = 0.5 * (row % 2)
        phase = (u + shift) % 1
        round_edge = np.where(phase < BLOCK, np.minimum(phase, BLOCK - phase), 0)
        across_edge = np.minimum(v - top, bottom - v)
        block = smooth_step(GROOVE_ROUND * 0.4, GROOVE_ROUND, round_edge) * smooth_step(GROOVE_ACROSS * 0.4, GROOVE_ACROSS, across_edge)
        height = np.where(inside, block, height)
    height = np.where(v >= 1, 1.0, height)
    # A faint grain, so the rubber does not look poured.
    grain = rng.random((SIZE, SIZE))
    for _ in range(2):
        grain = (grain + np.roll(grain, 1, 0) + np.roll(grain, -1, 0) + np.roll(grain, 1, 1) + np.roll(grain, -1, 1)) / 5
    grain = (grain - grain.mean()) / max(grain.std(), 1e-9)
    relief = height + 0.012 * grain
    dx = (np.roll(relief, -1, axis=1) - np.roll(relief, 1, axis=1)) * SIZE / 2 / 100
    drow = (np.roll(relief, -1, axis=0) - np.roll(relief, 1, axis=0)) * SIZE / 2 / 100
    normal = np.dstack([-dx * STRENGTH, drow * STRENGTH, np.ones_like(height)])
    normal /= np.linalg.norm(normal, axis=2, keepdims=True)
    occlusion = 0.45 + 0.55 * height
    shade = RUBBER * (0.8 + 0.2 * height) * (1 + 0.08 * grain)
    colour = np.dstack([shade, shade, shade])
    orm = np.dstack([occlusion, np.full_like(height, ROUGHNESS) + 0.01 * grain, np.zeros_like(height)])
    return colour, normal * 0.5 + 0.5, orm


def save(path, pixels, srgb):
    image = bpy.data.images.new(os.path.basename(path), SIZE, SIZE, alpha=False)
    if not srgb:
        image.colorspace_settings.name = "Non-Color"
    data = pixels
    if srgb:
        # The colours above are linear; the PNG keeps sRGB.
        data = np.where(data <= 0.0031308, data * 12.92, 1.055 * np.power(np.clip(data, 0, None), 1 / 2.4) - 0.055)
    rgba = np.dstack([np.clip(data, 0, 1), np.ones((SIZE, SIZE))])
    # Blender keeps the rows bottom up.
    image.pixels.foreach_set(rgba[::-1].astype(np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)


def write(classic):
    folder = os.path.join(classic, "remake", "textures", "tyre")
    os.makedirs(folder, exist_ok=True)
    colour, normal, orm = build()
    save(os.path.join(folder, "albedo.png"), colour, True)
    save(os.path.join(folder, "normal.png"), normal, False)
    save(os.path.join(folder, "orm.png"), orm, False)


if __name__ == "__main__":
    write(os.path.abspath(sys.argv[sys.argv.index("--") + 1]))
