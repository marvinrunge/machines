"""The HD machines' tread links: one tile of four links that repeats along the belts meshy_rig.py lays round each
track (the Meshy textures are atlases of a whole model and cannot scroll). It writes classic/meshy/textures/tread/
albedo.png, normal.png and orm.png (occlusion, roughness, metal), drawn from a height field:

    blender -b --factory-startup -P tools/blender/tread_texture.py -- <classic folder>

Across (u) the tile holds the belt's three faces: 0-0.5 the tread that meets the ground (link plates, a grouser bar
each, four bolts), 0.5-0.75 the edge (side plates with their hinge pins), 0.75-1 the running face the wheels roll on
(a guide horn per link). Down (v) it holds LINKS links, so the belt's length must be a whole number of tiles; v runs
from the image's top, as the Meshy maps do. The paint is worn dark steel: bright where wheels and ground rub, rust and
dust in the grooves, a little different from link to link."""
import os
import sys

import bpy
import numpy as np

LINKS = 4
WIDTH, HEIGHT = 512, 512
# How steep the height field's slopes look in the normal map.
STRENGTH = 6.0
TREAD, EDGE = 0.5, 0.75


def periodic_noise(rng, shape, blur):
    """Noise that tiles both ways, blurred by <blur> pixels."""
    field = rng.random(shape)
    for _ in range(3):
        for axis in (0, 1):
            total = np.zeros_like(field)
            for shift in range(-blur, blur + 1):
                total += np.roll(field, shift, axis=axis)
            field = total / (2 * blur + 1)
    field -= field.min()
    return field / max(field.max(), 1e-9)


def smooth_step(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0, 1)
    return t * t * (3 - 2 * t)


def band(x, lo, hi, soft):
    """1 between lo and hi, falling off over <soft> on both sides."""
    return smooth_step(lo - soft, lo, x) * (1 - smooth_step(hi, hi + soft, x))


def build():
    rng = np.random.default_rng(7)
    rows, cols = np.mgrid[0:HEIGHT, 0:WIDTH]
    u = (cols + 0.5) / WIDTH
    v = (rows + 0.5) / HEIGHT
    link = np.floor(v * LINKS)
    t = v * LINKS - link
    variation = rng.random(LINKS)[link.astype(int)]
    height = np.zeros((HEIGHT, WIDTH))
    # Where paint and dirt go: 1 in a groove, 0 on a face.
    groove = np.zeros((HEIGHT, WIDTH))

    # The tread: plates with a hinge gap at each link's ends, a grouser bar across, four bolts.
    s = u / TREAD
    tread = u < TREAD
    plate = band(t, 0.07, 0.93, 0.025) * band(s, 0.03, 0.97, 0.02)
    grouser = band(t, 0.38, 0.62, 0.05) * band(s, 0.06, 0.94, 0.03)
    bolts = np.zeros_like(u)
    for bs in (0.22, 0.78):
        for bt in (0.2, 0.8):
            d = np.hypot((s - bs) * 2.2, t - bt)
            bolts = np.maximum(bolts, 1 - smooth_step(0.035, 0.06, d))
    tread_height = 0.35 * plate + 0.55 * grouser + 0.15 * bolts * plate
    height = np.where(tread, tread_height, height)
    groove = np.where(tread, 1 - plate, groove)

    # The edge: side plates overlapping at each link's end, the hinge pin's round head between them.
    e = (u - TREAD) / (EDGE - TREAD)
    edge = (u >= TREAD) & (u < EDGE)
    side_plate = band(t, 0.05, 0.95, 0.03) * band(e, 0.08, 0.92, 0.04)
    pin = 1 - smooth_step(0.09, 0.13, np.hypot((e - 0.5) * 0.9, np.minimum(t, 1 - t)))
    edge_height = 0.4 * side_plate + 0.5 * pin
    height = np.where(edge, edge_height, height)
    groove = np.where(edge, (1 - side_plate) * (1 - pin), groove)

    # The running face: smooth pads, a guide horn in the middle of each link.
    r = (u - EDGE) / (1 - EDGE)
    running = u >= EDGE
    pad = band(t, 0.06, 0.94, 0.03)
    horn = band(t, 0.3, 0.7, 0.06) * band(r, 0.4, 0.6, 0.04)
    height = np.where(running, 0.3 * pad + 0.6 * horn, height)
    groove = np.where(running, 1 - pad, groove)

    # Cast steel is never smooth; each link wears a little differently.
    grain = periodic_noise(rng, (HEIGHT, WIDTH), 1)
    blotch = periodic_noise(rng, (HEIGHT, WIDTH), 6)
    height = height + 0.03 * grain + 0.02 * blotch * variation

    # Normals from the slopes (image x to the right, y up, as the Meshy and glTF maps).
    dx = (np.roll(height, -1, axis=1) - np.roll(height, 1, axis=1)) * WIDTH / 2 / 100
    drow = (np.roll(height, -1, axis=0) - np.roll(height, 1, axis=0)) * HEIGHT / 2 / 100
    normal = np.dstack([-dx * STRENGTH, drow * STRENGTH, np.ones_like(height)])
    normal /= np.linalg.norm(normal, axis=2, keepdims=True)

    # Occlusion: lower than the surroundings is darker.
    around = height.copy()
    for _ in range(4):
        around = (around + np.roll(around, 3, 0) + np.roll(around, -3, 0) + np.roll(around, 3, 1) + np.roll(around, -3, 1)) / 5
    occlusion = np.clip(1 - 2.5 * np.maximum(around - height, 0), 0.35, 1)

    # Worn where it rubs: the grouser tops and the running pads; the slopes catch the edge wear.
    slope = np.hypot(dx, drow)
    top = smooth_step(0.75, 0.95, height / max(height.max(), 1e-9))
    rubbed = np.clip(np.where(running, 0.3 * pad * (1 - horn) + 0.25 * horn, 0.7 * top) + 0.6 * smooth_step(0.6, 1.4, slope) * blotch, 0, 1)
    rust = np.clip(groove * (0.5 + 0.5 * blotch) + 0.25 * smooth_step(0.55, 0.9, blotch) * variation, 0, 1)
    dust = np.clip(0.6 * groove + 0.3 * smooth_step(0.4, 0.8, periodic_noise(rng, (HEIGHT, WIDTH), 10)), 0, 1)

    steel = np.array([0.065, 0.062, 0.058])
    bright = np.array([0.26, 0.25, 0.23])
    rusty = np.array([0.13, 0.055, 0.025])
    dusty = np.array([0.21, 0.17, 0.12])
    colour = steel * (0.85 + 0.3 * grain[..., None])
    colour = colour * (1 - rubbed[..., None]) + bright * rubbed[..., None]
    colour = colour * (1 - 0.7 * rust[..., None]) + rusty * 0.7 * rust[..., None]
    colour = colour * (1 - 0.5 * dust[..., None]) + dusty * 0.5 * dust[..., None]
    colour *= occlusion[..., None] ** 0.5

    roughness = np.clip(0.72 - 0.35 * rubbed + 0.2 * rust + 0.1 * dust, 0.25, 1)
    metal = np.clip(0.9 - 0.6 * rust - 0.5 * dust, 0.1, 1)
    orm = np.dstack([occlusion, roughness, metal])
    return colour, normal * 0.5 + 0.5, orm


def save(path, pixels, srgb):
    image = bpy.data.images.new(os.path.basename(path), WIDTH, HEIGHT, alpha=False)
    if not srgb:
        image.colorspace_settings.name = "Non-Color"
    data = pixels
    if srgb:
        # The colours above are linear; the PNG keeps sRGB.
        data = np.where(data <= 0.0031308, data * 12.92, 1.055 * np.power(np.clip(data, 0, None), 1 / 2.4) - 0.055)
    rgba = np.dstack([np.clip(data, 0, 1), np.ones((HEIGHT, WIDTH))])
    # Blender keeps the rows bottom up.
    image.pixels.foreach_set(rgba[::-1].astype(np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()


def main():
    classic = sys.argv[sys.argv.index("--") + 1]
    folder = os.path.join(os.path.abspath(classic), "meshy", "textures", "tread")
    os.makedirs(folder, exist_ok=True)
    colour, normal, orm = build()
    save(os.path.join(folder, "albedo.png"), colour, True)
    save(os.path.join(folder, "normal.png"), normal, False)
    save(os.path.join(folder, "orm.png"), orm, False)
    print("tread maps in", folder)


if __name__ == "__main__":
    main()
