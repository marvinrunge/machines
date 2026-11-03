"""The HD Knights' and Ballistas' weapons keep the original's shapes and pictures, but as worn metal that goes with the
Meshy hulls: this turns the original's HD textures into maps for tools/blender/meshy_rig.py's plan key
"forged", in classic/meshy/textures/forged-<kind>/ (albedo.jpg, normal.jpg, orm.jpg):

    blender -b --factory-startup -P tools/blender/weapon_metal.py -- <classic folder>

steel, dark and paint read mach_b's picture (its panels and ribs stay where the original's uvs look for them) as bare
steel, dark steel and worn red paint, the original's tints baked in; paint goes through tools/race-recolour.ps1 like a
Meshy map. grille reads fangrl_t, stripes the hazard stripes of stripes1, and front is a tile drawn here: one launcher
tube's steel plate, burnt round the hole, which meshy_rig cuts for real. platter reads whl_bt, the wheel picture the
Vortex's turntable shows."""
import os
import sys

import bpy
import numpy as np

FOLDER = "forged-"


def load(path):
    image = bpy.data.images.load(path)
    image.colorspace_settings.name = "Non-Color"
    width, height = image.size
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(height, width, 4)[::-1, :, :3]
    bpy.data.images.remove(image)
    return pixels


def save(path, pixels):
    height, width = pixels.shape[:2]
    image = bpy.data.images.new(os.path.basename(path), width, height, alpha=False)
    image.colorspace_settings.name = "Non-Color"
    # Blender keeps the rows bottom up.
    image.pixels.foreach_set(np.dstack([np.clip(pixels, 0, 1), np.ones((height, width))])[::-1].astype(np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "JPEG"
    bpy.context.scene.render.image_settings.quality = 93
    image.save()
    bpy.data.images.remove(image)


def blur(field, radius):
    """A box blur three times, wrapping round (the tiles repeat)."""
    for _ in range(3):
        for axis in (0, 1):
            total = np.cumsum(np.concatenate([np.take(field, range(-radius - 1, 0), axis=axis, mode="wrap"), field,
                                              np.take(field, range(0, radius), axis=axis, mode="wrap")], axis=axis), axis=axis)
            size = field.shape[axis]
            field = (np.take(total, range(2 * radius + 1, 2 * radius + 1 + size), axis=axis)
                     - np.take(total, range(0, size), axis=axis)) / (2 * radius + 1)
    return field


def noise(rng, shape, radius):
    field = blur(rng.random(shape), radius)
    return (field - field.min()) / max(field.max() - field.min(), 1e-9)


def smooth_step(edge0, edge1, x):
    t = np.clip((x - edge0) / (edge1 - edge0), 0, 1)
    return t * t * (3 - 2 * t)


def scratches(rng, shape, count):
    """Thin straight scratches, wrapping round the tile."""
    field = np.zeros(shape, dtype=np.float32)
    height, width = shape
    for _ in range(count):
        x, y = rng.random() * width, rng.random() * height
        angle, length = rng.random() * np.pi, 15 + rng.random() * 90
        steps = np.arange(0, length)
        rows = (y + steps * np.sin(angle)).astype(int) % height
        cols = (x + steps * np.cos(angle)).astype(int) % width
        field[rows, cols] = np.maximum(field[rows, cols], 0.4 + 0.6 * rng.random())
    return field


def wear(rng, shape, light):
    """How much of a surface is rubbed bare (0-1): at the picture's edges, in blotches, along scratches; and grime."""
    edges = np.hypot(np.roll(light, 1, 1) - np.roll(light, -1, 1), np.roll(light, 1, 0) - np.roll(light, -1, 0))
    blotch = smooth_step(0.62, 0.8, noise(rng, shape, 14)) * smooth_step(0.4, 0.7, noise(rng, shape, 3))
    rubbed = np.clip(smooth_step(0.15, 0.5, blur(edges, 1)) * (0.3 + 0.7 * noise(rng, shape, 6)) + blotch, 0, 1)
    rubbed = np.maximum(rubbed, 0.7 * scratches(rng, shape, 170))
    grime = noise(rng, shape, 22) * 0.55 + noise(rng, shape, 5) * 0.45
    return rubbed, grime


def relief(normal, height, strength):
    """<normal> (a map, 0-1) with the slopes of <height> added."""
    dx = (np.roll(height, -1, 1) - np.roll(height, 1, 1)) * strength
    dy = (np.roll(height, -1, 0) - np.roll(height, 1, 0)) * strength
    vector = normal * 2 - 1
    vector[..., 0] -= dx
    vector[..., 1] += dy
    vector /= np.linalg.norm(vector, axis=2, keepdims=True)
    return vector * 0.5 + 0.5


def steel(light, rng, low, high):
    """(colour, roughness, metal) of bare steel from the picture's light."""
    rubbed, grime = wear(rng, light.shape, light)
    level = low + (high - low) * light ** 0.85
    level = level * (0.72 + 0.4 * grime) + 0.22 * rubbed
    colour = level[..., None] * np.array([1.0, 0.97, 0.93])
    rust = smooth_step(0.72, 0.9, noise(rng, light.shape, 9)) * smooth_step(0.5, 0.2, light)
    colour = colour * (1 - 0.6 * rust[..., None]) + 0.6 * rust[..., None] * np.array([0.3, 0.16, 0.08])
    roughness = np.clip(0.34 + 0.34 * grime + 0.2 * rust - 0.14 * rubbed, 0, 1)
    return colour, roughness, np.clip(0.95 - 0.7 * rust, 0, 1)


def paint(light, rng):
    """Worn red paint over steel, as the Meshy hulls wear it."""
    rubbed, grime = wear(rng, light.shape, light)
    shade = (0.34 + 0.72 * light ** 0.8) * (0.74 + 0.42 * grime)
    red = shade[..., None] * np.array([0.78, 0.07, 0.05])
    bare = (0.2 + 0.32 * light)[..., None] * np.array([1.0, 0.97, 0.93])
    chipped = smooth_step(0.3, 0.6, rubbed)[..., None]
    colour = red * (1 - chipped) + bare * chipped
    return colour, np.clip(0.44 + 0.3 * grime - 0.1 * chipped[..., 0], 0, 1), 0.9 * chipped[..., 0]


def keep_colour(colour, picture):
    """The picture's own colours (the yellow of hazard stripes, brass) stay."""
    top, low = picture.max(axis=2), picture.min(axis=2)
    coloured = smooth_step(0.25, 0.5, (top - low) / np.maximum(top, 1e-6))[..., None] * smooth_step(0.15, 0.3, top)[..., None]
    return colour * (1 - coloured) + picture * 0.8 * coloured


def write(classic, kind, colour, normal, roughness, metal, occlusion=None):
    folder = os.path.join(classic, "meshy", "textures", FOLDER + kind)
    os.makedirs(folder, exist_ok=True)
    save(os.path.join(folder, "albedo.jpg"), colour)
    save(os.path.join(folder, "normal.jpg"), normal)
    occlusion = np.ones_like(roughness) if occlusion is None else occlusion
    save(os.path.join(folder, "orm.jpg"), np.dstack([occlusion, roughness, metal]))


def front_tile(rng):
    """One tube's plate: u and v of the tile run across the cell, the hole in the middle (see meshy_rig.real_holes)."""
    size = 512
    rows, cols = np.mgrid[0:size, 0:size]
    s, t = (cols + 0.5) / size - 0.5, (rows + 0.5) / size - 0.5
    r = np.hypot(s, t)
    seam = smooth_step(0.462, 0.5, np.maximum(np.abs(s), np.abs(t)))
    bolts = np.zeros((size, size))
    for bs in (-0.41, 0.41):
        for bt in (-0.41, 0.41):
            bolts = np.maximum(bolts, 1 - smooth_step(0.014, 0.026, np.hypot(s - bs, t - bt)))
    height = (1 - seam) * 0.8 + 0.5 * bolts + 0.25 * (1 - smooth_step(0.33, 0.37, r)) * smooth_step(0.3, 0.32, r)
    light = np.clip(0.5 + 0.3 * height, 0, 1)
    colour, roughness, metal = steel(light, rng, 0.04, 0.25)
    # Burnt round the hole, black inside it.
    burnt = (1 - smooth_step(0.3, 0.47, r))[..., None] * (0.6 + 0.4 * noise(rng, (size, size), 6))[..., None]
    colour = colour * (1 - 0.8 * burnt) + burnt * 0.8 * np.array([0.3, 0.14, 0.05]) * (0.4 + smooth_step(0.26, 0.4, r))[..., None]
    colour = colour * smooth_step(0.2, 0.3, r)[..., None]
    flat = np.tile(np.array([0.5, 0.5, 1.0], dtype=np.float32), (size, size, 1))
    # The tube's depth shows the tile's middle: soot, which shines back nothing.
    soot = smooth_step(0.2, 0.3, r)
    return colour, relief(flat, blur(height, 1), 4.0), 1 - soot * (1 - roughness), metal * soot, soot


def stripes_tile(picture, rng):
    light = picture.mean(axis=2)
    light = np.repeat(np.repeat(light, 2, 0), 2, 1) if light.shape[0] < 512 else light
    rubbed, grime = wear(rng, light.shape, light)
    yellow = np.array([0.82, 0.62, 0.06])[None, None, :] * (0.72 + 0.4 * grime)[..., None]
    black = np.array([0.035, 0.035, 0.04])[None, None, :] * (0.8 + 0.5 * grime)[..., None]
    bare = np.array([0.42, 0.41, 0.39])[None, None, :]
    mask = smooth_step(0.4, 0.6, light)[..., None]
    chipped = smooth_step(0.35, 0.65, rubbed)[..., None]
    colour = (yellow * mask + black * (1 - mask)) * (1 - chipped) + bare * chipped
    flat = np.tile(np.array([0.5, 0.5, 1.0], dtype=np.float32), (*light.shape, 1))
    return colour, relief(flat, blur(0.3 * chipped[..., 0], 1), 3.0), 0.45 + 0.3 * grime, 0.9 * chipped[..., 0]


def main():
    classic = os.path.abspath(sys.argv[sys.argv.index("--") + 1])
    textures = os.path.join(classic, "hd", "models", "texture4")
    picture = load(os.path.join(textures, "mach_b.png"))
    normal = load(os.path.join(textures, "mach_b_n.png"))
    light = picture.mean(axis=2)
    bumped = relief(normal, blur(light, 1), 2.0)
    rng = np.random.default_rng(23)
    colour, roughness, metal = steel(light, rng, 0.05, 0.5)
    write(classic, "steel", keep_colour(colour, picture), bumped, roughness, metal)
    colour, roughness, metal = steel(light, rng, 0.022, 0.17)
    write(classic, "dark", colour, bumped, roughness, metal)
    colour, roughness, metal = paint(light, rng)
    write(classic, "paint", colour, bumped, roughness, metal)
    picture = load(os.path.join(textures, "fangrl_t.png"))
    light = picture.mean(axis=2)
    colour, roughness, metal = steel(light, rng, 0.03, 0.4)
    write(classic, "grille", colour, relief(load(os.path.join(textures, "fangrl_t_n.png")), blur(light, 1), 3.0), roughness, metal)
    write(classic, "stripes", *stripes_tile(load(os.path.join(textures, "stripes1.png")), rng))
    write(classic, "front", *front_tile(rng))
    picture = load(os.path.join(textures, "whl_bt.png"))
    light = picture.mean(axis=2)
    colour, roughness, metal = steel(light, rng, 0.03, 0.45)
    write(classic, "platter", colour, relief(load(os.path.join(textures, "whl_bt_n.png")), blur(light, 1), 3.0), roughness, metal)


if __name__ == "__main__":
    main()
