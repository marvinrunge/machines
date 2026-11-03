"""Calms the glowing parts of a Meshy texture: Meshy painted the Reaper's plasma rifles' green glow as a busy pattern
of bright slats and white flares, where the original (and the Overhauled look) has one even, glowing green. In each
named texture folder (classic/meshy/textures/<name>) this lays an even green over the glow, flattens the normal map
there and writes emission.png, which tools/blender/meshy_rig.py hands the game as the glow's map:

    blender -b --factory-startup -P tools/blender/calm_glow.py -- <classic folder> <texture folder name> ...

The maps as Meshy made them stay as albedo-meshy.jpg and normal-meshy.jpg, and each run starts from them; while they
are there, meshy_rig leaves albedo.jpg and normal.jpg as they are. Then tools/race-recolour.ps1 -Only <name> paints
the races' albedos anew."""
import os
import shutil
import sys

import bpy
import numpy as np

# The glow: green enough and bright enough; the white flares inside it count too.
HUE = (80 / 360, 170 / 360)
SATURATION, VALUE, FLARE = 0.3, 0.35, 0.8
# The even green (sRGB, 0-1) and how much of the glow's own light and shade stays on it.
GREEN = np.array([0.30, 0.86, 0.34])
SHADE = 0.25
GLOW = 0.85


def load(path):
    image = bpy.data.images.load(path)
    width, height = image.size
    pixels = np.array(image.pixels[:], dtype=np.float32).reshape(height, width, 4)[..., :3]
    bpy.data.images.remove(image)
    return pixels


def save(path, pixels, quality=95):
    height, width = pixels.shape[:2]
    image = bpy.data.images.new(os.path.basename(path), width, height, alpha=False)
    image.colorspace_settings.name = "Non-Color"
    image.pixels.foreach_set(np.dstack([np.clip(pixels, 0, 1), np.ones((height, width))]).astype(np.float32).ravel())
    image.filepath_raw = path
    image.file_format = "JPEG" if path.endswith(".jpg") else "PNG"
    bpy.context.scene.render.image_settings.quality = quality
    image.save()
    bpy.data.images.remove(image)


def blur(field, radius):
    """A box blur, three times (about a Gaussian), of a 2D field."""
    for _ in range(3):
        for axis in (0, 1):
            pad = [(0, 0), (0, 0)]
            pad[axis] = (radius + 1, radius)
            padded = np.pad(field, pad, mode="edge")
            total = np.cumsum(padded, axis=axis)
            field = (np.take(total, range(2 * radius + 1, total.shape[axis]), axis=axis)
                     - np.take(total, range(0, total.shape[axis] - 2 * radius - 1), axis=axis)) / (2 * radius + 1)
    return field


def hsv(rgb):
    top, low = rgb.max(axis=2), rgb.min(axis=2)
    span = np.maximum(top - low, 1e-6)
    r, g, b = rgb[..., 0], rgb[..., 1], rgb[..., 2]
    hue = np.where(top == r, (g - b) / span % 6, np.where(top == g, (b - r) / span + 2, (r - g) / span + 4)) / 6
    return hue, (top - low) / np.maximum(top, 1e-6), top


def calm(folder):
    for name in ("albedo", "normal"):
        original = os.path.join(folder, f"{name}-meshy.jpg")
        if not os.path.exists(original):
            shutil.copyfile(os.path.join(folder, f"{name}.jpg"), original)
    albedo = load(os.path.join(folder, "albedo-meshy.jpg"))
    normal = load(os.path.join(folder, "normal-meshy.jpg"))
    hue, saturation, value = hsv(albedo)
    green = (hue > HUE[0]) & (hue < HUE[1]) & (saturation > SATURATION) & (value > VALUE)
    # The flares: pale and bright, grown out from the green as far as they reach.
    flare = (value > FLARE) & ((saturation < SATURATION) | ((hue > 40 / 360) & (hue < HUE[1])))
    glow = green
    for _ in range(60):
        grown = glow | (flare & (blur(glow.astype(np.float32), 1) > 0.01))
        if (grown == glow).all():
            break
        glow = grown
    soft = np.clip(blur(glow.astype(np.float32), 2) * 1.5, 0, 1)[..., None]
    light = albedo.mean(axis=2)
    lit = blur(np.where(glow, light, 0), 24) / np.maximum(blur(glow.astype(np.float32), 24), 1e-3)
    mean = light[glow].mean() if glow.any() else 0.5
    even = GREEN[None, None, :] * (1 + SHADE * (lit - mean))[..., None]
    save(os.path.join(folder, "albedo.jpg"), albedo * (1 - soft) + even * soft)
    flat = np.array([0.5, 0.5, 1.0])[None, None, :]
    save(os.path.join(folder, "normal.jpg"), normal * (1 - soft) + flat * soft)
    save(os.path.join(folder, "emission.png"), even * soft * GLOW)
    print(f"{folder}: {glow.mean():.1%} of the texture glows")


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    classic, names = argv[0], argv[1:]
    for name in names:
        calm(os.path.join(os.path.abspath(classic), "meshy", "textures", name))


if __name__ == "__main__":
    main()
