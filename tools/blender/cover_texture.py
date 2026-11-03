"""A track's side plate painted from nothing, for the Meshy models whose own came out a smear: tools/blender/meshy_rig.py
calls write() with the plate's outline (the plan's "covers") and shows it across the plate, so its steel rim and rivets
follow the outline. Worn red paint in panels, as the Meshy hulls wear it, in classic/meshy/textures/<name>/; the emblem
is printed from the original's picture, and tools/race-recolour.ps1 paints the other races' as it does a Meshy map."""
import math
import os

import numpy as np

import weapon_metal as metal

WIDTH = 1024
# The steel rim's width, the rivets' radius and how far apart they stand, as shares of the plate's height.
RIM = 0.075
RIVET = 0.013
RIVET_STEP = 0.2


def inside_and_depth(outline, xs, ys):
    """Which points lie inside the loop <outline> and how far from its edge each is."""
    inside = np.zeros(xs.shape, dtype=bool)
    depth = np.full(xs.shape, 1e9)
    for (ax, ay), (bx, by) in zip(outline, outline[1:] + outline[:1]):
        crosses = (ay > ys) != (by > ys)
        with np.errstate(divide="ignore", invalid="ignore"):
            inside ^= crosses & (xs < ax + (ys - ay) * (bx - ax) / (by - ay))
        dx, dy = bx - ax, by - ay
        t = np.clip(((xs - ax) * dx + (ys - ay) * dy) / max(dx * dx + dy * dy, 1e-12), 0, 1)
        depth = np.minimum(depth, np.hypot(xs - ax - t * dx, ys - ay - t * dy))
    return inside, depth


def rivets(outline, step, inset):
    """Points along the loop, <inset> inside it, <step> apart."""
    area = sum(a[0] * b[1] - b[0] * a[1] for a, b in zip(outline, outline[1:] + outline[:1]))
    turn = 1 if area > 0 else -1
    points, left = [], 0.0
    for (ax, ay), (bx, by) in zip(outline, outline[1:] + outline[:1]):
        length = math.hypot(bx - ax, by - ay)
        if length < 1e-9:
            continue
        nx, ny = -(by - ay) / length * turn, (bx - ax) / length * turn
        at = left
        while at < length:
            points.append((ax + (bx - ax) * at / length + nx * inset, ay + (by - ay) * at / length + ny * inset))
            at += step
        left = at - length
    return points


def picture_emblem(path, shape):
    """The round middle of the picture, as (colour, how much of it shows) on <shape> pixels."""
    picture = metal.load(path)
    height, width = picture.shape[:2]
    rows = (np.arange(shape[0]) + 0.5) / shape[0]
    cols = (np.arange(shape[1]) + 0.5) / shape[1]
    # The white oval of the original's plate: where it stands in the picture.
    u = 0.5 + (cols[None, :] - 0.5) * 0.72
    v = 0.52 + (rows[:, None] - 0.5) * 0.8
    colour = picture[np.clip((v * height).astype(int), 0, height - 1), np.clip((u * width).astype(int), 0, width - 1)]
    r = np.hypot(cols[None, :] - 0.5, rows[:, None] - 0.5) * 2
    return colour, 1 - metal.smooth_step(0.93, 0.99, r)


def write(classic, name, outline, emblem=None, band="green"):
    """Paints the plate of <outline> (x along, y up, in metres); returns its maps, relative to classic."""
    rng = np.random.default_rng(sum(map(ord, name)))
    lo_x, hi_x = min(p[0] for p in outline), max(p[0] for p in outline)
    lo_y, hi_y = min(p[1] for p in outline), max(p[1] for p in outline)
    span, tall = hi_x - lo_x, hi_y - lo_y
    height = int(WIDTH * tall / span)
    rows, cols = np.mgrid[0:height, 0:WIDTH]
    xs = lo_x + (cols + 0.5) / WIDTH * span
    ys = hi_y - (rows + 0.5) / height * tall
    inside, depth = inside_and_depth(outline, xs, ys)
    depth = np.where(inside, depth, 0)
    rim = RIM * tall
    on_rim = 1 - metal.smooth_step(rim * 0.92, rim * 1.08, depth)

    relief = 0.55 + 0.45 * on_rim - 0.5 * np.exp(-((depth - rim * 1.14) / (rim * 0.09)) ** 2)
    # Panels: seams across the plate, the middle panel wider.
    for share in (0.27, 0.73):
        x = lo_x + span * share
        relief -= 0.45 * np.exp(-((xs - x) / (0.006 * span)) ** 2) * (1 - on_rim)
    middle_y = (lo_y + hi_y) / 2
    relief -= 0.45 * np.exp(-((ys - (lo_y + 0.27 * tall)) / (0.008 * tall)) ** 2) * (1 - on_rim)

    studs = np.zeros_like(relief)
    for x, y in rivets(outline, RIVET_STEP * tall, rim * 0.5):
        studs = np.maximum(studs, 1 - metal.smooth_step(RIVET * tall * 0.6, RIVET * tall, np.hypot(xs - x, ys - y)))
    for share in (0.27, 0.73):
        for y_share in np.arange(0.36, 0.9, 0.13):
            x, y = lo_x + span * share + 0.02 * span, lo_y + tall * y_share
            studs = np.maximum(studs, (1 - metal.smooth_step(RIVET * tall * 0.5, RIVET * tall * 0.8, np.hypot(xs - x, ys - y))) * (1 - on_rim))
    relief = relief + 0.6 * studs

    light = np.clip(0.42 + 0.5 * metal.blur(relief - 0.5, 1), 0, 1)
    colour, roughness, shine = metal.paint(light, rng)
    bare, bare_roughness, bare_shine = metal.steel(light, rng, 0.1, 0.5)

    # What the plate wears low down.
    low = lo_y + 0.27 * tall
    stripes = (ys < low) & (on_rim < 0.5)
    if band == "green":
        wave = np.sin((ys - lo_y) / (0.012 * tall))
        green = np.array([0.05, 0.3, 0.07])[None, None, :] * (0.7 + 0.5 * light[..., None])
        black = np.array([0.02, 0.02, 0.02])[None, None, :]
        colour = np.where(stripes[..., None], np.where(wave[..., None] > 0.1, green, black), colour)
    elif band == "hazard":
        wave = np.sin((xs + ys) / (0.05 * tall)) > 0
        yellow = np.array([0.8, 0.6, 0.06])[None, None, :] * (0.7 + 0.5 * light[..., None])
        black = np.array([0.03, 0.03, 0.03])[None, None, :]
        colour = np.where(stripes[..., None], np.where(wave[..., None], yellow, black), colour)

    if emblem:
        size = int(0.56 * height)
        top, left = int(height * 0.1), WIDTH // 2 - size // 2
        picture, shown = picture_emblem(os.path.join(classic, "hd", "models", "texture4", emblem + ".png"), (size, size))
        worn, grime = metal.wear(rng, (size, size), picture.mean(axis=2))
        shown = shown * (1 - 0.8 * metal.smooth_step(0.5, 0.8, worn))
        area = (slice(top, top + size), slice(left, left + size))
        picture = picture * (0.72 + 0.34 * grime)[..., None]
        colour[area] = colour[area] * (1 - shown[..., None]) + picture * shown[..., None]

    on_rim = on_rim[..., None]
    colour = colour * (1 - on_rim) + bare * on_rim
    roughness = roughness * (1 - on_rim[..., 0]) + bare_roughness * on_rim[..., 0]
    shine = shine * (1 - on_rim[..., 0]) + bare_shine * on_rim[..., 0]
    flat = np.tile(np.array([0.5, 0.5, 1.0], dtype=np.float32), (height, WIDTH, 1))
    normal = metal.relief(flat, metal.blur(relief, 1), 5.0)

    folder = os.path.join(classic, "meshy", "textures", name)
    os.makedirs(folder, exist_ok=True)
    metal.save(os.path.join(folder, "albedo.jpg"), colour)
    metal.save(os.path.join(folder, "normal.jpg"), normal)
    metal.save(os.path.join(folder, "orm.jpg"), np.dstack([np.ones_like(roughness), roughness, shine]))
    return {key: f"meshy/textures/{name}/{key}.jpg" for key in ("albedo", "normal", "orm")}
