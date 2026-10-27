"""Bakes the foliage cards the trees are built from (tools/blender/trees.py): pictures of a leafy twig, a dry twig, a
palm frond (green and withered) and a fir bough (green and snowed on), each composed in Blender from the single leaves
of ambientCG's leaf atlases (CC0) and rendered from straight above on a clear background. A tree then shows a whole
twig on a strip of a few triangles, as games draw their trees.

    blender --background --factory-startup --python tools/blender/cards.py -- <atlases> <cards out>

<atlases>/<id>/<id>_1K-PNG_Color.png and _Opacity.png are the leaf sets as tools/blender/build-nature.sh fetches
them. Each card's base is at the middle of its bottom edge and its tip at the top. Everything is seeded, so the same
cards come out every time.
"""
import math
import os
import random
import sys

import bpy
import numpy as np
from mathutils import Vector

SIZE = 1024


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:]
    return args[0], args[1]


# ---- the atlases --------------------------------------------------------------------------------------------------

class Atlas:
    """A leaf set: its colour and opacity pictures, and where each leaf lies in them (UV boxes, smallest first)."""

    def __init__(self, folder, id):
        self.colour = bpy.data.images.load(os.path.join(folder, id, f"{id}_1K-PNG_Color.png"))
        self.opacity = bpy.data.images.load(os.path.join(folder, id, f"{id}_1K-PNG_Opacity.png"))
        self.opacity.colorspace_settings.name = "Non-Color"
        self.leaves = find_leaves(self.opacity)
        self.material = None


def find_leaves(image, step=4):
    """The boxes of the separate leaves in an opacity picture: its clear and solid parts read at a quarter of its
    size, the solid ones joined where they touch."""
    w, h = image.size
    pixels = np.empty(w * h * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    solid = pixels.reshape(h, w, 4)[::step, ::step, 0] > 0.5
    rows, cols = solid.shape
    seen = np.zeros_like(solid)
    boxes = []
    for y in range(rows):
        for x in range(cols):
            if not solid[y, x] or seen[y, x]:
                continue
            stack, ys, xs = [(y, x)], [], []
            seen[y, x] = True
            while stack:
                cy, cx = stack.pop()
                ys.append(cy)
                xs.append(cx)
                for ny, nx in ((cy + 1, cx), (cy - 1, cx), (cy, cx + 1), (cy, cx - 1)):
                    if 0 <= ny < rows and 0 <= nx < cols and solid[ny, nx] and not seen[ny, nx]:
                        seen[ny, nx] = True
                        stack.append((ny, nx))
            if len(ys) < rows * cols * 0.002:
                continue
            pad = 1
            boxes.append(((min(xs) - pad) / cols, (min(ys) - pad) / rows, (max(xs) + 1 + pad) / cols, (max(ys) + 1 + pad) / rows))
    return sorted(boxes, key=lambda b: (b[2] - b[0]) * (b[3] - b[1]))


def leaf_material(atlas, name, tint=(1, 1, 1), snow=0.0):
    """Unlit: the atlas's colour (tinted, and white in patches where snow lies) where its opacity says."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    nodes, links = mat.node_tree.nodes, mat.node_tree.links
    nodes.clear()
    out = nodes.new("ShaderNodeOutputMaterial")
    colour = nodes.new("ShaderNodeTexImage")
    colour.image = atlas.colour
    alpha = nodes.new("ShaderNodeTexImage")
    alpha.image = atlas.opacity
    tinted = nodes.new("ShaderNodeMix")
    tinted.data_type = "RGBA"
    tinted.blend_type = "MULTIPLY"
    tinted.inputs["Factor"].default_value = 1.0
    links.new(colour.outputs["Color"], socket(tinted.inputs, "A"))
    socket(tinted.inputs, "B").default_value = (*tint, 1)
    shown = socket(tinted.outputs, "Result")
    if snow > 0:
        # Snow lies in soft patches over the needles, thickest along the middle.
        noise = nodes.new("ShaderNodeTexNoise")
        noise.inputs["Scale"].default_value = 9.0
        noise.inputs["Detail"].default_value = 6.0
        ramp = nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].position = 0.5 - snow * 0.25
        ramp.color_ramp.elements[1].position = 0.62 - snow * 0.25
        links.new(noise.outputs["Fac"], ramp.inputs["Fac"])
        white = nodes.new("ShaderNodeMix")
        white.data_type = "RGBA"
        links.new(ramp.outputs["Color"], white.inputs["Factor"])
        links.new(shown, socket(white.inputs, "A"))
        socket(white.inputs, "B").default_value = (0.92, 0.94, 0.97, 1)
        shown = socket(white.outputs, "Result")
    emit = nodes.new("ShaderNodeEmission")
    links.new(shown, emit.inputs["Color"])
    clear = nodes.new("ShaderNodeBsdfTransparent")
    mix = nodes.new("ShaderNodeMixShader")
    links.new(alpha.outputs["Color"], mix.inputs["Fac"])
    links.new(clear.outputs["BSDF"], mix.inputs[1])
    links.new(emit.outputs["Emission"], mix.inputs[2])
    links.new(mix.outputs["Shader"], out.inputs["Surface"])
    return mat


def socket(sockets, name):
    """A colour socket by its name (a mix node has a float, a vector and a colour socket of each name)."""
    return next(s for s in sockets if s.name == name and s.type == "RGBA")


def stem_material(colour):
    mat = bpy.data.materials.new("Stem")
    mat.use_nodes = True
    nodes = mat.node_tree.nodes
    nodes.clear()
    out = nodes.new("ShaderNodeOutputMaterial")
    emit = nodes.new("ShaderNodeEmission")
    emit.inputs["Color"].default_value = (*colour, 1)
    mat.node_tree.links.new(emit.outputs["Emission"], out.inputs["Surface"])
    return mat


# ---- composing a card ---------------------------------------------------------------------------------------------

class Card:
    """The quads of one card, in card units: x from 0 to 1 across, y from 0 (base) to 1 (tip)."""

    def __init__(self):
        self.verts, self.faces, self.uvs, self.mats = [], [], [], []
        self.materials = []
        self.depth = 0.0

    def material(self, mat):
        if mat not in self.materials:
            self.materials.append(mat)
        return self.materials.index(mat)

    def quad(self, corners, uvs, mat):
        # Each later quad a little higher, so the later leaves lie over the earlier ones.
        self.depth += 0.0005
        base = len(self.verts)
        self.verts += [(c[0], c[1], self.depth) for c in corners]
        self.faces.append((base, base + 1, base + 2, base + 3))
        self.uvs.append(uvs)
        self.mats.append(self.material(mat))

    def leaf(self, atlas, box, base, angle, length, width_scale=1.0, upright=True, mat=None):
        """A leaf of the atlas from base pointing at angle (radians from straight up), its width by its picture's
        shape. Upright leaves have their stalk at the bottom of their box; the others (sprigs) at its right."""
        u0, v0, u1, v1 = box
        if upright:
            aspect = (u1 - u0) / (v1 - v0)
            uv = [(u0, v0), (u1, v0), (u1, v1), (u0, v1)]
        else:
            aspect = (v1 - v0) / (u1 - u0)
            uv = [(u1, v1), (u1, v0), (u0, v0), (u0, v1)]
        width = length * aspect * width_scale
        up = Vector((math.sin(angle), math.cos(angle)))
        side = Vector((up.y, -up.x))
        b = Vector(base)
        corners = [b - side * width / 2, b + side * width / 2, b + side * width / 2 + up * length, b - side * width / 2 + up * length]
        self.quad(corners, uv, mat or atlas.material)

    def stem(self, points, width, mat):
        for a, b in zip(points, points[1:]):
            a, b = Vector(a), Vector(b)
            d = (b - a).normalized()
            side = Vector((d.y, -d.x)) * width / 2
            self.quad([a - side, a + side, b + side, b - side], [(0, 0)] * 4, mat)

    def render(self, path, height=SIZE):
        """The card from above on a clear background, its colours carried out into the clear parts (so blurring the
        picture far off blends no black into the leaves' edges)."""
        mesh = bpy.data.meshes.new("card")
        mesh.from_pydata(self.verts, [], self.faces)
        layer = mesh.uv_layers.new(name="UVMap")
        for f, poly in enumerate(mesh.polygons):
            poly.material_index = self.mats[f]
            for k, loop in enumerate(poly.loop_indices):
                layer.data[loop].uv = self.uvs[f][k]
        for m in self.materials:
            mesh.materials.append(m)
        obj = bpy.data.objects.new("card", mesh)
        scene = bpy.context.scene
        scene.collection.objects.link(obj)
        cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
        cam.data.type = "ORTHO"
        aspect = height / SIZE
        cam.data.ortho_scale = max(1.0, aspect)
        cam.location = (0.5, aspect / 2, 5)
        scene.collection.objects.link(cam)
        scene.camera = cam
        scene.render.engine = "CYCLES"
        scene.cycles.samples = 24
        scene.cycles.use_denoising = False
        scene.render.film_transparent = True
        scene.render.resolution_x = SIZE
        scene.render.resolution_y = height
        scene.render.image_settings.file_format = "PNG"
        scene.render.image_settings.color_mode = "RGBA"
        scene.view_settings.view_transform = "Standard"
        scene.render.filepath = path
        bpy.ops.render.render(write_still=True)
        bleed(path)


def bleed(path, steps=12):
    """Spreads the leaves' colours out into the clear pixels around them, the alpha untouched."""
    image = bpy.data.images.load(path)
    w, h = image.size
    px = np.empty(w * h * 4, dtype=np.float32)
    image.pixels.foreach_get(px)
    px = px.reshape(h, w, 4)
    rgb, alpha = px[:, :, :3].copy(), px[:, :, 3]
    known = alpha > 0.5
    for _ in range(steps):
        total = np.zeros_like(rgb)
        count = np.zeros((h, w), dtype=np.float32)
        for dy, dx in ((1, 0), (-1, 0), (0, 1), (0, -1)):
            shifted = np.roll(np.roll(known, dy, 0), dx, 1)
            total += np.roll(np.roll(rgb * known[:, :, None], dy, 0), dx, 1)
            count += shifted
        grow = (~known) & (count > 0)
        rgb[grow] = total[grow] / count[grow][:, None]
        known = known | grow
    rgb[~known] = rgb[known].mean(axis=0) if known.any() else 0
    px[:, :, :3] = rgb
    image.pixels.foreach_set(px.ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()


# ---- the cards ----------------------------------------------------------------------------------------------------

def twig(rng, atlas, stem, leaves=26, spread=1.0):
    """A leafy twig: a stem rising and forking once, leaves alternating along both, and one at each tip."""
    card = Card()
    main = [(0.5, 0.02)]
    for i in range(1, 7):
        main.append((0.5 + rng.uniform(-0.03, 0.03), 0.02 + 0.13 * i))
    fork = [main[3]]
    for i in range(1, 4):
        fork.append((main[3][0] + 0.09 * i * spread, main[3][1] + 0.08 * i))
    card.stem(main, 0.012, stem)
    card.stem(fork, 0.009, stem)
    for branch in (main, fork):
        for k in range(leaves // 2):
            s = (k + 0.5) / (leaves // 2)
            i = min(int(s * (len(branch) - 1)), len(branch) - 2)
            t = s * (len(branch) - 1) - i
            at = Vector(branch[i]).lerp(Vector(branch[i + 1]), t)
            side = 1 if k % 2 else -1
            size = rng.uniform(0.17, 0.25) * (1.15 - 0.4 * s) * spread
            card.leaf(atlas, rng.choice(atlas.leaves), at, side * rng.uniform(0.6, 1.1), size)
        tip = branch[-1]
        card.leaf(atlas, rng.choice(atlas.leaves), tip, rng.uniform(-0.2, 0.2), 0.2 * spread)
    return card


def frond(rng, atlas, stem, leaflets=48):
    """A palm frond seen from above: a midrib and pairs of long, narrow leaflets, longest at the middle."""
    card = Card()
    rib = [(0.5, 0.0), (0.5, 1.0)]
    card.stem(rib, 0.018, stem)
    for k in range(leaflets):
        s = 0.06 + 0.92 * k / leaflets
        reach = 0.42 * math.sin(math.pi * min(1.0, s * 1.08)) ** 0.8 + 0.04
        for sign in (-1, 1):
            card.leaf(atlas, rng.choice(atlas.leaves[-6:]), (0.5, s), sign * rng.uniform(0.7, 0.95), reach, width_scale=0.15)
    return card


def bough(rng, atlas, mat):
    """A fir bough: the longest sprig up the middle and shorter ones fanning out from its sides."""
    card = Card()
    sprigs = sorted(atlas.leaves, key=lambda b: b[2] - b[0])
    card.leaf(atlas, sprigs[-1], (0.5, 0.0), 0.0, 0.98, upright=False, mat=mat)
    for k in range(6):
        s = 0.12 + 0.13 * k
        sign = 1 if k % 2 else -1
        card.leaf(atlas, rng.choice(sprigs[:-1]), (0.5, s), sign * rng.uniform(0.45, 0.7), 0.55 - 0.05 * k, upright=False, mat=mat)
    return card


def main():
    atlases, out = arguments()
    os.makedirs(out, exist_ok=True)
    jobs = [
        ("twig_green", "LeafSet024", lambda a, r: twig(r, a, stem_material((0.16, 0.11, 0.06)))),
        ("twig_dry", "LeafSet030", lambda a, r: twig(r, a, stem_material((0.2, 0.14, 0.08)), leaves=18, spread=0.9)),
        ("frond", "LeafSet022", lambda a, r: frond(r, a, stem_material((0.32, 0.3, 0.12)))),
        ("frond_dry", "LeafSet022", lambda a, r: frond(r, a, stem_material((0.35, 0.27, 0.15)), leaflets=36)),
        ("bough", "LeafSet019", lambda a, r: bough(r, a, a.material)),
        ("bough_snow", "LeafSet019", lambda a, r: bough(r, a, leaf_material(a, "snowy", snow=0.3))),
    ]
    for seed, (name, id, make) in enumerate(jobs):
        bpy.ops.wm.read_factory_settings(use_empty=True)
        atlas = Atlas(atlases, id)
        tint = (0.6, 0.38, 0.17) if name == "frond_dry" else (1, 1, 1)
        atlas.material = leaf_material(atlas, id, tint=tint)
        card = make(atlas, random.Random(seed + 1))
        card.render(os.path.join(out, name + ".png"))
        print(f"card {name}: {len(atlas.leaves)} leaves in {id}, {len(card.faces)} quads")


main()
