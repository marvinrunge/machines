"""Builds the remake's trees and bushes in Blender from code: semi-realistic and light enough for a strategy game (one
to three thousand triangles). Trunks and branches are tubes in Poly Haven bark pictures (CC0); the foliage is cards,
bent strips of a few triangles each showing a whole twig, frond or bough baked by tools/blender/cards.py from
ambientCG's leaf atlases (CC0), as games draw their trees. The cards' normals point out of the crown, so it is lit as
one round mass rather than as flat strips. Each tree is exported as glTF with a preview picture.

    blender --background --factory-startup --python tools/blender/trees.py -- <barks> <cards> <models out> <previews out>

<barks>/<name>/albedo.jpg and normal.jpg are the bark pictures; <cards>/<name>.png the baked cards. Everything is
seeded, so the same trees come out every time.
"""
import math
import os
import random
import sys

import bmesh
import numpy as np
import bpy
from mathutils import Vector

UP = Vector((0, 0, 1))


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:]
    return args[0], args[1], args[2], args[3]


# ---- materials --------------------------------------------------------------------------------------------------

def bark_material(textures, name, recolour=None):
    """A bark picture with its relief; recoloured (an sRGB colour) it keeps its pattern around that colour instead of
    its own, with a little less contrast (a pale baobab from a darker bark)."""
    mat = bpy.data.materials.new("Bark_" + name)
    mat.use_nodes = True
    tree = mat.node_tree
    bsdf = tree.nodes["Principled BSDF"]
    folder = os.path.join(textures, name)
    colour = tree.nodes.new("ShaderNodeTexImage")
    colour.image = bpy.data.images.load(os.path.join(folder, "albedo.jpg"))
    if recolour is not None:
        source = colour.image
        w, h = source.size
        px = np.empty(w * h * 4, dtype=np.float32)
        source.pixels.foreach_get(px)
        px = px.reshape(-1, 4)
        mean = px[:, :3].mean(axis=0)
        px[:, :3] = np.clip(np.array(recolour) * (1 + (px[:, :3] / mean - 1) * 0.75), 0, 1)
        image = bpy.data.images.new(f"{name}_recoloured", w, h)
        image.pixels.foreach_set(px.ravel())
        image.file_format = "JPEG"
        image.pack()
        colour.image = image
    tree.links.new(colour.outputs["Color"], bsdf.inputs["Base Color"])
    normal_path = os.path.join(folder, "normal.jpg")
    if os.path.exists(normal_path):
        normal = tree.nodes.new("ShaderNodeTexImage")
        normal.image = bpy.data.images.load(normal_path)
        normal.image.colorspace_settings.name = "Non-Color"
        bump = tree.nodes.new("ShaderNodeNormalMap")
        tree.links.new(normal.outputs["Color"], bump.inputs["Color"])
        tree.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    bsdf.inputs["Roughness"].default_value = 0.9
    return mat


def card_material(cards, name):
    """A card's picture, cut out where it is clear (glTF alpha mask: the alpha through a 'greater than' node), and
    seen from both sides."""
    mat = bpy.data.materials.new("Leaves_" + name)
    mat.use_nodes = True
    tree = mat.node_tree
    bsdf = tree.nodes["Principled BSDF"]
    picture = tree.nodes.new("ShaderNodeTexImage")
    picture.image = bpy.data.images.load(os.path.join(cards, name + ".png"))
    picture.image.alpha_mode = "STRAIGHT"
    tree.links.new(picture.outputs["Color"], bsdf.inputs["Base Color"])
    cut = tree.nodes.new("ShaderNodeMath")
    cut.operation = "GREATER_THAN"
    cut.inputs[1].default_value = 0.5
    tree.links.new(picture.outputs["Alpha"], cut.inputs[0])
    tree.links.new(cut.outputs["Value"], bsdf.inputs["Alpha"])
    bsdf.inputs["Roughness"].default_value = 0.75
    mat.use_backface_culling = False
    return mat


# ---- geometry -----------------------------------------------------------------------------------------------------

class Builder:
    """Collects a tree's faces into one mesh: material 0 is the bark, the others its cards. Each card face remembers
    the middle of its crown, from which its normals point outwards."""

    def __init__(self):
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")
        # Each card face's crown, kept on the face itself so it lasts through triangulating.
        self.crown = [self.bm.faces.layers.float.new(f"crown_{axis}") for axis in "xyz"]
        self.has_crown = self.bm.faces.layers.int.new("has_crown")

    def face(self, points, uvs, material, crown=None):
        verts = [self.bm.verts.new(p) for p in points]
        f = self.bm.faces.new(verts)
        f.material_index = material
        for loop, uv in zip(f.loops, uvs):
            loop[self.uv].uv = uv
        if crown is not None:
            for layer, value in zip(self.crown, crown):
                f[layer] = value
            f[self.has_crown] = 1
        return f

    def tube(self, path, radii, sides, uv_scale=(1.0, 1.0)):
        """A tube along points with a radius at each, its bark wrapped round and up it."""
        rings = []
        length = 0.0
        frame_x = None
        for i, p in enumerate(path):
            p = Vector(p)
            forward = (Vector(path[min(i + 1, len(path) - 1)]) - Vector(path[max(i - 1, 0)])).normalized()
            if frame_x is None:
                frame_x = forward.orthogonal().normalized()
            frame_x = (frame_x - forward * frame_x.dot(forward)).normalized()
            frame_y = forward.cross(frame_x)
            if i > 0:
                length += (p - Vector(path[i - 1])).length
            rings.append([(p + (frame_x * math.cos(math.tau * s / sides) + frame_y * math.sin(math.tau * s / sides)) * radii[i], s / sides, length)
                          for s in range(sides + 1)])
        for i in range(len(rings) - 1):
            for s in range(sides):
                a, b, c, d = rings[i][s], rings[i][s + 1], rings[i + 1][s + 1], rings[i + 1][s]
                self.face([a[0], b[0], c[0], d[0]],
                          [(a[1] * uv_scale[0], a[2] * uv_scale[1]), (b[1] * uv_scale[0], b[2] * uv_scale[1]),
                           (c[1] * uv_scale[0], c[2] * uv_scale[1]), (d[1] * uv_scale[0], d[2] * uv_scale[1])], 0)

    def card(self, spine, side, width, material, crown, fold=0.0):
        """A card along a spine of points (its base first), as wide as given, its picture from bottom (base) to top
        (tip). With a fold its two halves rise from the spine like a shallow V (a frond's leaflets)."""
        side = Vector(side).normalized()
        total = sum((Vector(b) - Vector(a)).length for a, b in zip(spine, spine[1:]))
        along = 0.0
        rows = []
        for i, p in enumerate(spine):
            p = Vector(p)
            if i > 0:
                along += (p - Vector(spine[i - 1])).length
            forward = (Vector(spine[min(i + 1, len(spine) - 1)]) - Vector(spine[max(i - 1, 0)])).normalized()
            lift = forward.cross(side).normalized()
            if lift.z < 0:
                lift = -lift
            half = width / 2
            left = p - (side * math.cos(fold) - lift * math.sin(fold)) * half
            right = p + (side * math.cos(fold) + lift * math.sin(fold)) * half
            rows.append((left, p, right, along / total))
        for (l0, c0, r0, v0), (l1, c1, r1, v1) in zip(rows, rows[1:]):
            if fold:
                self.face([l0, c0, c1, l1], [(0, v0), (0.5, v0), (0.5, v1), (0, v1)], material, crown)
                self.face([c0, r0, r1, c1], [(0.5, v0), (1, v0), (1, v1), (0.5, v1)], material, crown)
            else:
                self.face([l0, r0, r1, l1], [(0, v0), (1, v0), (1, v1), (0, v1)], material, crown)


def curve(start, direction, length, steps, bend):
    """Points from start along a direction that turns by bend (a vector added per unit length)."""
    points = [Vector(start)]
    d = Vector(direction).normalized()
    step = length / steps
    for _ in range(steps):
        d = (d + Vector(bend) * step).normalized()
        points.append(points[-1] + d * step)
    return points


# ---- the trees ----------------------------------------------------------------------------------------------------

def palm(rng, height, lean, fronds, dry, name, bark, green, withered):
    """A palm: a slender ringed trunk leaning and curving up; a crown of arching fronds, a few young ones standing up
    in the middle and the dead ones hanging down the trunk."""
    b = Builder()
    heading = rng.uniform(0, math.tau)
    lean_dir = Vector((math.cos(heading), math.sin(heading), 0))
    trunk, radii = [], []
    steps = 28
    for i in range(steps + 1):
        t = i / steps
        trunk.append(lean_dir * lean * height * t * t + Vector((0, 0, height * t)))
        r = 0.2 * (1.25 - 0.45 * t) + (0.08 * (1 - t) ** 6)
        radii.append(r * (1 + 0.05 * math.sin(t * height / 0.32 * math.tau)))
    b.tube(trunk, radii, 10, uv_scale=(1, 0.8))
    top = trunk[-1]
    crown = top + Vector((0, 0, 0.4))
    for k in range(fronds + dry):
        a = math.tau * k / (fronds + dry) + rng.uniform(-0.25, 0.25)
        out = Vector((math.cos(a), math.sin(a), 0))
        is_dry = k >= fronds
        young = not is_dry and k % 4 == 0
        rise = rng.uniform(1.4, 2.2) if young else rng.uniform(0.25, 0.6) if not is_dry else rng.uniform(-1.6, -0.9)
        length = rng.uniform(3.4, 4.6) * (0.7 if young else 0.85 if is_dry else 1)
        spine = curve(top, out + Vector((0, 0, rise)), length, 8, (0, 0, -0.42 if not is_dry else -0.1))
        side = out.cross(UP)
        # Its leaflets hang from the midrib on both sides: the two halves fold down.
        b.card(spine, side, length * 0.6, 2 if is_dry else 1, crown, fold=0.0 if is_dry else math.radians(-35))
    return b, [bark, green, withered]


def fir(rng, height, slim, name, bark, boughs):
    """A fir: a straight trunk under tiers of drooping boughs, shorter towards the top, and a leader at the tip."""
    b = Builder()
    b.tube([Vector((0, 0, -0.3)), Vector((0, 0, height * 0.97))], [0.26, 0.04], 8, uv_scale=(1, 0.6))
    tiers = 26
    for k in range(tiers):
        t = 0.1 + 0.86 * k / tiers
        z = height * t
        reach = height * 0.3 * slim * (1 - t) ** 0.95 + 0.45
        count = 8 if t < 0.7 else 6
        for j in range(count):
            a = math.tau * (j + rng.uniform(-0.25, 0.25)) / count + k * 1.3
            out = Vector((math.cos(a), math.sin(a), 0))
            root = Vector((0, 0, z)) + out * 0.1
            # Low boughs reach out and droop; high ones lift a little.
            spine = curve(root, out + Vector((0, 0, 0.15 - 0.45 * (1 - t))), reach, 3, (0, 0, -0.35 / max(reach, 0.5)))
            b.card(spine, out.cross(UP), reach * 0.85, 1, Vector((0, 0, z)), fold=math.radians(12))
    # The leader: two crossed cards standing up at the tip.
    for a in (0.0, math.pi / 2):
        side = Vector((math.cos(a), math.sin(a), 0))
        spine = [Vector((0, 0, height * 0.88)), Vector((0, 0, height * 0.94)), Vector((0, 0, height * 1.02))]
        b.card(spine, side, height * 0.08, 1, Vector((0, 0, height * 0.9)))
    return b, [bark, boughs]


def baobab(rng, height, leafy, name, bark, twigs):
    """A baobab: a fat bottle trunk, a crown of thick crooked branches, and clusters of leafy twigs at their ends."""
    b = Builder()
    profile, radii = [], []
    for i in range(13):
        t = i / 12
        profile.append(Vector((0, 0, height * 0.62 * t)))
        radii.append(1.15 * (1 - 0.45 * t) + 0.25 * math.sin(t * math.pi) + 0.2 * (1 - t) ** 4)
    b.tube(profile, radii, 16, uv_scale=(2, 0.35))
    top = profile[-1]
    for k in range(rng.randint(6, 8)):
        a = math.tau * k / 7 + rng.uniform(-0.3, 0.3)
        out = Vector((math.cos(a), math.sin(a), 0))
        path = curve(top + out * 0.4, out * 0.8 + Vector((0, 0, 0.9)), rng.uniform(2.5, 3.6), 6,
                     (rng.uniform(-0.2, 0.2), rng.uniform(-0.2, 0.2), -0.05))
        b.tube(path, [0.38 * (1 - i / len(path)) + 0.08 for i in range(len(path))], 8, uv_scale=(1, 0.5))
        for _ in range(2):
            ta = a + rng.uniform(-0.9, 0.9)
            tout = Vector((math.cos(ta), math.sin(ta), 0))
            tpath = curve(path[-1], tout + Vector((0, 0, 0.7)), rng.uniform(1.0, 1.8), 4, (0, 0, -0.1))
            b.tube(tpath, [0.1 * (1 - i / len(tpath)) + 0.03 for i in range(len(tpath))], 5, uv_scale=(1, 0.5))
            if leafy:
                cluster(b, rng, tpath[-1], 9, 1.8, 1)
    return b, [bark, twigs]


def cluster(b, rng, at, count, size, material):
    """Leafy twigs spreading every way but down from a point, their crown a little above it."""
    crown = Vector(at) + Vector((0, 0, size * 0.3))
    for _ in range(count):
        a = rng.uniform(0, math.tau)
        rise = rng.uniform(-0.2, 1.2)
        out = Vector((math.cos(a), math.sin(a), rise)).normalized()
        length = size * rng.uniform(0.75, 1.1)
        spine = curve(at, out, length, 2, (0, 0, -0.25))
        side = out.cross(UP) if abs(out.z) < 0.95 else Vector((1, 0, 0))
        b.card(spine, side.normalized(), length * 0.7, material, crown)


def dead_tree(rng, height, name, bark):
    """A dead tree: a twisted trunk and bare branches forking twice."""
    b = Builder()
    trunk = curve((0, 0, 0), (rng.uniform(-0.2, 0.2), rng.uniform(-0.2, 0.2), 1), height * 0.6, 8, (rng.uniform(-0.1, 0.1), rng.uniform(-0.1, 0.1), 0))
    b.tube(trunk, [0.32 * (1 - 0.5 * i / len(trunk)) for i in range(len(trunk))], 8, uv_scale=(1, 0.6))

    def branch(start, direction, length, radius, depth):
        path = curve(start, direction, length, 5, (rng.uniform(-0.3, 0.3), rng.uniform(-0.3, 0.3), -0.05))
        b.tube(path, [radius * (1 - 0.7 * i / len(path)) for i in range(len(path))], 5, uv_scale=(1, 0.6))
        if depth > 0:
            for _ in range(2):
                d = (Vector(direction) + Vector((rng.uniform(-0.8, 0.8), rng.uniform(-0.8, 0.8), rng.uniform(0.0, 0.5)))).normalized()
                branch(path[-1], d, length * 0.65, radius * 0.5, depth - 1)

    for k in range(4):
        a = math.tau * k / 4 + rng.uniform(-0.4, 0.4)
        start = trunk[int(len(trunk) * rng.uniform(0.55, 0.95))]
        branch(start, (math.cos(a), math.sin(a), 0.8), height * 0.3, 0.13, 2)
    return b, [bark]


def bush(rng, size, count, name, bark, twigs):
    """A bush: a few thin stems, and leafy twigs rising and spreading from low down all round."""
    b = Builder()
    for _ in range(5):
        a = rng.uniform(0, math.tau)
        tip = Vector((math.cos(a), math.sin(a), 0)) * size * 0.3 + Vector((0, 0, size * 0.5))
        b.tube([Vector((0, 0, -0.05)), tip * 0.5, tip], [0.035, 0.025, 0.012], 4)
    crown = Vector((0, 0, size * 0.4))
    for _ in range(count):
        a = rng.uniform(0, math.tau)
        rise = rng.uniform(0.6, 2.4)
        out = Vector((math.cos(a), math.sin(a), rise)).normalized()
        at = Vector((math.cos(a), math.sin(a), 0)) * size * rng.uniform(0.0, 0.2) + Vector((0, 0, size * rng.uniform(0.0, 0.25)))
        length = size * rng.uniform(0.6, 0.95)
        spine = curve(at, out, length, 2, (0, 0, -0.3))
        b.card(spine, out.cross(UP).normalized(), length * 0.75, 1, crown)
    return b, [bark, twigs]


# ---- export and preview ----------------------------------------------------------------------------------------

def finish(builder, name, materials):
    """The builder's mesh as an object: smooth, the cards' normals turned out of their crowns."""
    bm = builder.bm
    mesh = bpy.data.meshes.new(name)
    bmesh.ops.triangulate(bm, faces=list(bm.faces))
    crowns = [Vector([f[layer] for layer in builder.crown]) if f[builder.has_crown] else None for f in bm.faces]
    bm.to_mesh(mesh)
    bm.free()
    for m in materials:
        mesh.materials.append(m)
    for p in mesh.polygons:
        p.use_smooth = True
    normals = []
    for p, crown in zip(mesh.polygons, crowns):
        own = Vector(p.normal) if p.normal.z >= 0 else -Vector(p.normal)
        for loop in p.loop_indices:
            if crown is None:
                normals.append((0.0, 0.0, 0.0))
                continue
            out = mesh.vertices[mesh.loops[loop].vertex_index].co - crown
            out = out.normalized() if out.length > 1e-4 else own
            normals.append(tuple(own.lerp(out, 0.65).normalized()))
    mesh.normals_split_custom_set(normals)
    for attribute in ("crown_x", "crown_y", "crown_z", "has_crown"):
        if attribute in mesh.attributes:
            mesh.attributes.remove(mesh.attributes[attribute])
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def export(obj, path):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    # The cards keep their PNG with its alpha, the barks their JPEG.
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_image_format="AUTO",
                              export_yup=True)


def preview(obj, path):
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 48
    scene.cycles.use_denoising = True
    scene.render.resolution_x = scene.render.resolution_y = 360
    scene.view_settings.view_transform = "AgX"
    if scene.world is None:
        world = bpy.data.worlds.new("World")
        world.use_nodes = True
        world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.42, 0.44, 0.46, 1)
        world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
        scene.world = world
        sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
        sun.data.energy = 4
        sun.rotation_euler = (math.radians(45), math.radians(-30), math.radians(30))
        scene.collection.objects.link(sun)
    for o in scene.objects:
        if o.type == "MESH":
            o.hide_render = o is not obj
    size = max(obj.dimensions)
    cam = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    cam.data.lens = 50
    centre = Vector((0, 0, obj.dimensions.z * 0.45))
    cam.location = centre + Vector((size * 1.3, -size * 1.7, size * 0.55))
    cam.rotation_euler = (centre - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)


def main():
    barks, cards, models, previews = arguments()
    os.makedirs(models, exist_ok=True)
    os.makedirs(previews, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    palm_bark = bark_material(barks, "palm_bark")
    # A baobab's bark is smooth and pale, greyish pink: the plane tree's pattern in that colour.
    tree_bark = bark_material(barks, "bark_platanus", recolour=(0.66, 0.58, 0.54))
    dead_bark = bark_material(barks, "jolcham_oak_bark_01")
    frond, frond_dry = card_material(cards, "frond"), card_material(cards, "frond_dry")
    bough, bough_snow = card_material(cards, "bough"), card_material(cards, "bough_snow")
    twig_green, twig_dry = card_material(cards, "twig_green"), card_material(cards, "twig_dry")
    made = [
        ("palm_a", palm(random.Random(1), 7.5, 0.12, 14, 3, "palm_a", palm_bark, frond, frond_dry)),
        ("palm_b", palm(random.Random(2), 9.5, 0.22, 15, 4, "palm_b", palm_bark, frond, frond_dry)),
        ("palm_c", palm(random.Random(3), 5.5, 0.35, 12, 2, "palm_c", palm_bark, frond, frond_dry)),
        # A young palm, its fronds still near the ground (the original's low palm03).
        ("palm_shrub", palm(random.Random(11), 0.9, 0.0, 12, 2, "palm_shrub", palm_bark, frond, frond_dry)),
        ("fir", fir(random.Random(4), 10, 1.0, "fir", dead_bark, bough)),
        ("fir_snow", fir(random.Random(5), 11, 1.0, "fir_snow", dead_bark, bough_snow)),
        ("fir_slim_snow", fir(random.Random(12), 12, 0.6, "fir_slim_snow", dead_bark, bough_snow)),
        ("baobab_leafy", baobab(random.Random(6), 10, True, "baobab_leafy", tree_bark, twig_green)),
        ("baobab_bare", baobab(random.Random(7), 10, False, "baobab_bare", tree_bark, twig_green)),
        ("dead_tree", dead_tree(random.Random(8), 7, "dead_tree", dead_bark)),
        ("bush_green", bush(random.Random(9), 1.8, 52, "bush_green", dead_bark, twig_green)),
        ("bush_dry", bush(random.Random(10), 1.4, 34, "bush_dry", dead_bark, twig_dry)),
    ]
    for name, (builder, materials) in made:
        obj = finish(builder, name, materials)
        triangles = len(obj.data.polygons)
        export(obj, os.path.join(models, name + ".glb"))
        preview(obj, os.path.join(previews, name + ".png"))
        print(f"tree {name}: {triangles} triangles, {obj.dimensions.x:.1f} x {obj.dimensions.y:.1f} x {obj.dimensions.z:.1f} m")


main()
