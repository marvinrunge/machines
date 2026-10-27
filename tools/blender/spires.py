"""Builds rock spires (the moons' spikes) in Blender from code: a tapering, leaning horn of rock, its surface broken
by noise and banded by strata, wrapped in a Poly Haven rock texture. Exported as glTF with a preview picture each.

    blender --background --factory-startup --python tools/blender/spires.py -- <textures> <rock layer> <models out> <previews out>

<textures>/<rock layer>/albedo.jpg and normal.jpg are a rock picture from Poly Haven (CC0), as tools/terrain-textures
fetches them into textures/polyhaven. Everything is seeded, so the same spires come out every time.
"""
import math
import os
import random
import sys

import bmesh
import bpy
from mathutils import Vector, noise

# How many metres of rock one repeat of the texture covers.
TILE = 4.0


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:]
    return args[0], args[1], args[2], args[3]


def rock_material(textures, layer):
    mat = bpy.data.materials.new("Rock_" + layer)
    mat.use_nodes = True
    tree = mat.node_tree
    bsdf = tree.nodes["Principled BSDF"]
    folder = os.path.join(textures, layer)
    colour = tree.nodes.new("ShaderNodeTexImage")
    colour.image = bpy.data.images.load(os.path.join(folder, "albedo.jpg"))
    tree.links.new(colour.outputs["Color"], bsdf.inputs["Base Color"])
    normal = tree.nodes.new("ShaderNodeTexImage")
    normal.image = bpy.data.images.load(os.path.join(folder, "normal.jpg"))
    normal.image.colorspace_settings.name = "Non-Color"
    bump = tree.nodes.new("ShaderNodeNormalMap")
    tree.links.new(normal.outputs["Color"], bump.inputs["Color"])
    tree.links.new(bump.outputs["Normal"], bsdf.inputs["Normal"])
    bsdf.inputs["Roughness"].default_value = 0.85
    return mat


def spire(rng, height, radius, lean, name, material, sides=14, rings=26):
    """
    A horn of rock: rings up a leaning, curling centre line, wide and flared at the foot and thinning to a blunt
    point, each ring's points pushed in and out by noise and by horizontal strata.
    """
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    heading = rng.uniform(0, math.tau)
    lean_dir = Vector((math.cos(heading), math.sin(heading), 0))
    curl_dir = Vector((-lean_dir.y, lean_dir.x, 0)) * rng.uniform(-0.3, 0.3)
    seed = Vector((rng.uniform(0, 100), rng.uniform(0, 100), rng.uniform(0, 100)))
    grid = []
    for i in range(rings + 1):
        t = i / rings
        # Sunk half a metre into the ground, so it sits on slopes.
        centre = (lean_dir + curl_dir * t) * lean * height * t * t + Vector((0, 0, height * t - 0.5))
        r = radius * ((1 - t) ** 0.85 * 0.92 + 0.08) + radius * 0.3 * (1 - t) ** 10
        band = 0.07 * math.sin(t * height / 1.7 + noise.noise(seed + Vector((t * 3, 0, 0))) * 2)
        ring = []
        for s in range(sides + 1):
            a = math.tau * (s % sides) / sides
            out = Vector((math.cos(a), math.sin(a), 0))
            probe = seed + out * 1.5 + Vector((0, 0, t * height / 6))
            push = 1 + 0.3 * noise.noise(probe) + 0.18 * abs(noise.noise(probe * 3.1)) - 0.09 + band
            ring.append((centre + out * r * push, a * r / TILE, centre.z / TILE))
        grid.append(ring)
    verts = [[bm.verts.new(p) for p, _, _ in ring[:-1]] for ring in grid]
    for i in range(rings):
        for s in range(sides):
            n = (s + 1) % sides
            f = bm.faces.new([verts[i][s], verts[i][n], verts[i + 1][n], verts[i + 1][s]])
            us = [grid[i][s][1], grid[i][s + 1][1], grid[i + 1][s + 1][1], grid[i + 1][s][1]]
            vs = [grid[i][s][2], grid[i][s + 1][2], grid[i + 1][s + 1][2], grid[i + 1][s][2]]
            for loop, u, v in zip(f.loops, us, vs):
                loop[uv].uv = (u, v)
    tip = bm.verts.new(grid[-1][0][0].lerp(grid[-1][sides // 2][0], 0.5) + Vector((0, 0, radius * 0.1)))
    for s in range(sides):
        f = bm.faces.new([verts[-1][s], verts[-1][(s + 1) % sides], tip])
        for loop in f.loops:
            loop[uv].uv = (s / sides, height / TILE)
    bmesh.ops.triangulate(bm, faces=bm.faces)
    bmesh.ops.recalc_face_normals(bm, faces=bm.faces)
    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    mesh.materials.append(material)
    for p in mesh.polygons:
        p.use_smooth = True
    obj = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(obj)
    return obj


def cluster(rng, height, name, material):
    """A main spire with two or three smaller ones leaning out from its foot, joined into one object."""
    parts = [spire(rng, height, height * 0.2, 0.18, name, material)]
    for k in range(rng.randint(2, 3)):
        a = rng.uniform(0, math.tau)
        small = spire(rng, height * rng.uniform(0.35, 0.6), height * 0.12, 0.3, f"{name}_{k}", material)
        small.location = (math.cos(a) * height * 0.16, math.sin(a) * height * 0.16, 0)
        parts.append(small)
    bpy.ops.object.select_all(action="DESELECT")
    for p in parts:
        p.select_set(True)
    bpy.context.view_layer.objects.active = parts[0]
    bpy.ops.object.join()
    return parts[0]


def export(obj, path):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
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
    centre = Vector((0, 0, obj.dimensions.z * 0.45))
    cam.location = centre + Vector((size * 1.3, -size * 1.7, size * 0.55))
    cam.rotation_euler = (centre - cam.location).to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(cam)


def main():
    textures, layer, models, previews = arguments()
    os.makedirs(models, exist_ok=True)
    os.makedirs(previews, exist_ok=True)
    bpy.ops.wm.read_factory_settings(use_empty=True)
    rock = rock_material(textures, layer)
    made = [
        spire(random.Random(1), 17, 3.6, 0.25, "spire_stub", rock),
        spire(random.Random(2), 28, 4.2, 0.35, "spire_horn", rock),
        cluster(random.Random(3), 26, "spire_cluster", rock),
    ]
    for obj in made:
        triangles = sum(len(p.vertices) - 2 for p in obj.data.polygons)
        export(obj, os.path.join(models, obj.name + ".glb"))
        preview(obj, os.path.join(previews, obj.name + ".png"))
        print(f"spire {obj.name}: {triangles} triangles, {obj.dimensions.x:.1f} x {obj.dimensions.y:.1f} x {obj.dimensions.z:.1f} m")


main()
