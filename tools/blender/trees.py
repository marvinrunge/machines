"""Builds the remake's trees and bushes in Blender from code, light enough for a strategy game (one to three thousand
triangles), and exports each as glTF with a preview picture.

    blender --background --factory-startup --python tools/blender/trees.py -- <textures> <models out> <previews out>

<textures>/<name>/albedo.jpg and normal.jpg are bark pictures from Poly Haven (CC0). The leaves need no picture: each
leaf takes its colour from a small palette image by its UV, so the trees export cleanly and tint per leaf.
Everything is seeded, so the same trees come out every time.
"""
import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:]
    return args[0], args[1], args[2]


# ---- materials --------------------------------------------------------------------------------------------------

# The palette's columns: leaf greens dark to light, dry brown fronds, needles, snow, dry baobab leaves.
PALETTE = [
    (0.10, 0.20, 0.05), (0.16, 0.30, 0.07), (0.24, 0.40, 0.10), (0.34, 0.48, 0.14),
    (0.42, 0.33, 0.16), (0.55, 0.43, 0.22), (0.06, 0.14, 0.07), (0.10, 0.20, 0.10),
    (0.86, 0.90, 0.95), (0.95, 0.97, 1.00), (0.35, 0.40, 0.12), (0.46, 0.44, 0.16),
]


def palette_u(index):
    return (index + 0.5) / len(PALETTE)


def palette_material():
    image = bpy.data.images.new("leaf_palette", len(PALETTE), 1, alpha=False)
    pixels = []
    for r, g, b in PALETTE:
        pixels += [r, g, b, 1.0]
    image.pixels = pixels
    image.pack()
    mat = bpy.data.materials.new("Leaves")
    mat.use_nodes = True
    tree = mat.node_tree
    bsdf = tree.nodes["Principled BSDF"]
    texture = tree.nodes.new("ShaderNodeTexImage")
    texture.image = image
    texture.interpolation = "Closest"
    tree.links.new(texture.outputs["Color"], bsdf.inputs["Base Color"])
    bsdf.inputs["Roughness"].default_value = 0.7
    mat.use_backface_culling = False
    return mat


def bark_material(textures, name):
    mat = bpy.data.materials.new("Bark_" + name)
    mat.use_nodes = True
    tree = mat.node_tree
    bsdf = tree.nodes["Principled BSDF"]
    folder = os.path.join(textures, name)
    colour = tree.nodes.new("ShaderNodeTexImage")
    colour.image = bpy.data.images.load(os.path.join(folder, "albedo.jpg"))
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


# ---- geometry -----------------------------------------------------------------------------------------------------

class Builder:
    """Collects the tree's triangles into one mesh with two material slots (0 bark, 1 leaves)."""

    def __init__(self):
        self.bm = bmesh.new()
        self.uv = self.bm.loops.layers.uv.new("UVMap")

    def face(self, points, uvs, material):
        verts = [self.bm.verts.new(p) for p in points]
        f = self.bm.faces.new(verts)
        f.material_index = material
        for loop, uv in zip(f.loops, uvs):
            loop[self.uv].uv = uv
        return f

    def tube(self, path, radii, sides, material=0, uv_scale=(1.0, 1.0), cap=False):
        """A tube along points with a radius at each, its bark texture wrapped round and up it."""
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
            ring = []
            for s in range(sides + 1):
                a = math.tau * s / sides
                ring.append((p + (frame_x * math.cos(a) + frame_y * math.sin(a)) * radii[i], s / sides, length))
            rings.append(ring)
        for i in range(len(rings) - 1):
            for s in range(sides):
                a, b, c, d = rings[i][s], rings[i][s + 1], rings[i + 1][s + 1], rings[i + 1][s]
                self.face([a[0], b[0], c[0], d[0]],
                          [(a[1] * uv_scale[0], a[2] * uv_scale[1]), (b[1] * uv_scale[0], b[2] * uv_scale[1]),
                           (c[1] * uv_scale[0], c[2] * uv_scale[1]), (d[1] * uv_scale[0], d[2] * uv_scale[1])], material)
        if cap:
            top = rings[-1]
            centre = sum((v[0] for v in top[:-1]), Vector()) / sides
            for s in range(sides):
                self.face([top[s][0], top[s + 1][0], centre], [(0, 0), (1, 0), (0.5, 0.5)], material)

    def leaf(self, base, along, side, length, width, colour, droop=0.0):
        """A leaf or leaflet: a slim quad (two triangles) from base along a direction, bending down at its tip."""
        along = Vector(along).normalized()
        side = Vector(side).normalized()
        tip = Vector(base) + along * length + Vector((0, 0, -droop * length))
        middle = Vector(base) + along * length * 0.5 + Vector((0, 0, -droop * length * 0.3))
        u = palette_u(colour)
        self.face([Vector(base) - side * width * 0.2, middle - side * width, tip, middle + side * width, Vector(base) + side * width * 0.2],
                  [(u, 0.5)] * 5, 1)

    def foliage(self, centre, radius, count, colours, rng, size=0.35):
        """A clump of leaves: many small leaves facing every way in a squashed ball, denser and darker inside."""
        for _ in range(count):
            direction = Vector((rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 0.7))).normalized()
            depth = rng.random() ** 0.5
            at = Vector(centre) + direction * radius * depth * Vector((1, 1, 0.7))
            along = Vector((rng.gauss(0, 1), rng.gauss(0, 1), rng.gauss(0, 1))).normalized()
            side = along.cross(direction).normalized() if along.cross(direction).length > 0.01 else along.orthogonal()
            colour = colours[min(len(colours) - 1, int(depth * len(colours)))]
            self.leaf(at, along, side, size * rng.uniform(0.7, 1.3), size * 0.35, colour, droop=0.1)

    def blob(self, centre, radius, colour, rng, subdivisions=1):
        """A lumpy ball of leaves (bushes, baobab crowns)."""
        temp = bmesh.new()
        bmesh.ops.create_icosphere(temp, subdivisions=subdivisions, radius=radius)
        u = palette_u(colour)
        for v in temp.verts:
            v.co *= 0.75 + 0.5 * rng.random()
            v.co.z *= 0.75
        mapping = {}
        for v in temp.verts:
            mapping[v] = self.bm.verts.new(v.co + Vector(centre))
        for f in temp.faces:
            nf = self.bm.faces.new([mapping[v] for v in f.verts])
            nf.material_index = 1
            for loop in nf.loops:
                loop[self.uv].uv = (u, 0.5)
        temp.free()

    def finish(self, name, materials):
        mesh = bpy.data.meshes.new(name)
        bmesh.ops.recalc_face_normals(self.bm, faces=self.bm.faces)
        self.bm.to_mesh(mesh)
        self.bm.free()
        for m in materials:
            mesh.materials.append(m)
        obj = bpy.data.objects.new(name, mesh)
        bpy.context.scene.collection.objects.link(obj)
        for p in mesh.polygons:
            p.use_smooth = p.material_index == 0
        return obj


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

def palm(rng, height, lean, fronds, dry, name, bark, leaves):
    """A palm: a slender ringed trunk leaning and curving up, a crown of arching fronds with hanging leaflets."""
    b = Builder()
    heading = rng.uniform(0, math.tau)
    lean_dir = Vector((math.cos(heading), math.sin(heading), 0))
    trunk = []
    radii = []
    steps = 28
    for i in range(steps + 1):
        t = i / steps
        trunk.append(lean_dir * lean * height * t * t + Vector((0, 0, height * t)))
        # Swollen at the foot, thinner up the trunk, ringed every few decimetres.
        r = 0.2 * (1.25 - 0.45 * t) + (0.08 * (1 - t) ** 6)
        radii.append(r * (1 + 0.05 * math.sin(t * height / 0.32 * math.tau)))
    b.tube(trunk, radii, 10, uv_scale=(1, 0.8))
    top = trunk[-1]
    for k in range(fronds):
        a = math.tau * k / fronds + rng.uniform(-0.2, 0.2)
        out = Vector((math.cos(a), math.sin(a), 0))
        is_dry = k < dry
        rise = rng.uniform(0.6, 1.1) if not is_dry else rng.uniform(-0.4, 0.0)
        length = rng.uniform(3.2, 4.4) * (0.85 if is_dry else 1)
        spine = curve(top, out + Vector((0, 0, rise)), length, 14, (0, 0, -0.55 if not is_dry else -0.8))
        b.tube(spine, [0.05 * (1 - i / len(spine)) + 0.012 for i in range(len(spine))], 4, uv_scale=(1, 1))
        for i in range(1, len(spine) - 1):
            s = i / (len(spine) - 1)
            p = spine[i]
            forward = (spine[i + 1] - spine[i - 1]).normalized()
            side = forward.cross(Vector((0, 0, 1))).normalized()
            leaflet = 0.25 + 0.95 * math.sin(math.pi * s) ** 0.7
            colour = rng.choice([4, 5]) if is_dry else rng.choice([1, 2, 2, 3])
            for sign in (-1, 1):
                direction = side * sign + forward * 0.6 + Vector((0, 0, -0.25))
                b.leaf(p, direction, forward, leaflet, 0.07, colour, droop=0.35 if not is_dry else 0.6)
    return b.finish(name, [bark, leaves])


def fir(rng, height, snow, name, bark, leaves, slim=1.0):
    """A fir: a straight trunk under tiers of drooping, saw-edged boughs, smaller towards the top; snow on top."""
    b = Builder()
    b.tube([Vector((0, 0, 0)), Vector((0, 0, height))], [0.22, 0.04], 8, uv_scale=(1, 0.6))
    tiers = 15
    for k in range(tiers):
        t = 0.12 + 0.88 * k / tiers
        z = height * t
        reach = height * 0.32 * slim * (1 - t) ** 0.9 + 0.25
        boughs = 7 + int(3 * (1 - t))
        for j in range(boughs):
            a = math.tau * (j + rng.random() * 0.4) / boughs + k * 0.7
            out = Vector((math.cos(a), math.sin(a), 0))
            side = Vector((-math.sin(a), math.cos(a), 0))
            root = Vector((0, 0, z))
            tip = root + out * reach + Vector((0, 0, -reach * 0.45))
            # A bough: a flat jagged spray from the trunk to its tip, wider in the middle.
            points = 6
            left, right = [], []
            for i in range(points + 1):
                s = i / points
                centre = root.lerp(tip, s) + Vector((0, 0, math.sin(math.pi * s) * reach * 0.12))
                width = reach * 0.42 * math.sin(math.pi * min(1.0, s * 1.1)) * (1.0 if i % 2 else 0.65)
                left.append(centre - side * width)
                right.append(centre + side * width)
            colour = rng.choice([6, 7, 7])
            for i in range(points):
                quad = [left[i], left[i + 1], right[i + 1], right[i]]
                top_colour = colour
                if snow and rng.random() < 0.75:
                    top_colour = rng.choice([8, 9])
                b.face(quad, [(palette_u(top_colour), 0.5)] * 4, 1)
                # The underside stays green: a second face, a little lower, turned the other way.
                under = [q + Vector((0, 0, -0.04)) for q in reversed(quad)]
                b.face(under, [(palette_u(colour), 0.5)] * 4, 1)
    return b.finish(name, [bark, leaves])


def baobab(rng, height, leafy, name, bark, leaves):
    """A baobab: a fat bottle trunk, a crown of thick crooked branches, and a few clumps of leaves (or none)."""
    b = Builder()
    profile = []
    radii = []
    for i in range(13):
        t = i / 12
        profile.append(Vector((0, 0, height * 0.62 * t)))
        radii.append(1.15 * (1 - 0.45 * t) + 0.25 * math.sin(t * math.pi) + 0.2 * (1 - t) ** 4)
    b.tube(profile, radii, 16, uv_scale=(2, 0.35))
    top = profile[-1]
    branches = rng.randint(6, 8)
    for k in range(branches):
        a = math.tau * k / branches + rng.uniform(-0.3, 0.3)
        out = Vector((math.cos(a), math.sin(a), 0))
        length = rng.uniform(2.5, 3.6)
        path = curve(top + out * 0.4, out * 0.8 + Vector((0, 0, 0.9)), length, 6, (rng.uniform(-0.2, 0.2), rng.uniform(-0.2, 0.2), -0.05))
        b.tube(path, [0.38 * (1 - i / len(path)) + 0.08 for i in range(len(path))], 8, uv_scale=(1, 0.5))
        for twig in range(2):
            ta = a + rng.uniform(-0.9, 0.9)
            tout = Vector((math.cos(ta), math.sin(ta), 0))
            tpath = curve(path[-1], tout + Vector((0, 0, 0.7)), rng.uniform(1.0, 1.8), 4, (0, 0, -0.1))
            b.tube(tpath, [0.1 * (1 - i / len(tpath)) + 0.03 for i in range(len(tpath))], 5, uv_scale=(1, 0.5))
            if leafy:
                b.foliage(tpath[-1] + Vector((0, 0, 0.25)), rng.uniform(0.8, 1.1), 55, [1, 10, 2, 11], rng, size=0.32)
    return b.finish(name, [bark, leaves])


def dead_tree(rng, height, name, bark, leaves):
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
    return b.finish(name, [bark, leaves])


def bush(rng, size, colour, name, bark, leaves):
    """A bush: a few lumpy balls of leaves, the lower ones wider."""
    b = Builder()
    # A few twigs from the ground, and clumps of leaves on them.
    for _ in range(rng.randint(5, 7)):
        a = rng.uniform(0, math.tau)
        out = Vector((math.cos(a), math.sin(a), 0))
        r = size * rng.uniform(0.32, 0.48)
        centre = out * size * rng.uniform(0.1, 0.45) + Vector((0, 0, r * rng.uniform(0.7, 1.3)))
        b.tube([Vector((0, 0, 0)), centre * 0.6, centre], [0.04, 0.03, 0.015], 4, uv_scale=(1, 1))
        b.foliage(centre, r, 70, colour, rng, size=0.22)
    return b.finish(name, [bark, leaves])


# ---- export and preview ----------------------------------------------------------------------------------------

def export(obj, path):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_image_format="JPEG",
                              export_jpeg_quality=88, export_yup=True)


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
    textures, models, previews = arguments()
    os.makedirs(models, exist_ok=True)
    os.makedirs(previews, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    leaves = palette_material()
    palm_bark = bark_material(textures, "palm_bark")
    # A baobab's bark is smooth and pale, greyish pink: the plane tree's comes closest.
    tree_bark = bark_material(textures, "bark_platanus")
    dead_bark = bark_material(textures, "jolcham_oak_bark_01")
    made = [
        palm(random.Random(1), 7.5, 0.12, 11, 2, "palm_a", palm_bark, leaves),
        palm(random.Random(2), 9.5, 0.22, 12, 3, "palm_b", palm_bark, leaves),
        palm(random.Random(3), 5.5, 0.35, 9, 1, "palm_c", palm_bark, leaves),
        # A young palm, its fronds still near the ground (the original's low palm03).
        palm(random.Random(11), 0.9, 0.0, 12, 2, "palm_shrub", palm_bark, leaves),
        fir(random.Random(4), 10, False, "fir", dead_bark, leaves),
        fir(random.Random(5), 11, True, "fir_snow", dead_bark, leaves),
        fir(random.Random(12), 12, True, "fir_slim_snow", dead_bark, leaves, slim=0.6),
        baobab(random.Random(6), 10, True, "baobab_leafy", tree_bark, leaves),
        baobab(random.Random(7), 10, False, "baobab_bare", tree_bark, leaves),
        dead_tree(random.Random(8), 7, "dead_tree", dead_bark, leaves),
        bush(random.Random(9), 1.8, [0, 1, 2, 3], "bush_green", dead_bark, leaves),
        bush(random.Random(10), 1.4, [4, 10, 11, 5], "bush_dry", dead_bark, leaves),
    ]
    for obj in made:
        triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        export(obj, os.path.join(models, obj.name + ".glb"))
        preview(obj, os.path.join(previews, obj.name + ".png"))
        print(f"tree {obj.name}: {triangles} triangles, {obj.dimensions.x:.1f} x {obj.dimensions.y:.1f} x {obj.dimensions.z:.1f} m")


main()
