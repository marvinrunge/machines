"""Turns Poly Haven rock scans (CC0) into game-ready models: one object each (sets split into their pieces), the
polygons cut down to what a strategy view needs, exported as glTF with their colour, normal and roughness maps, and
a preview picture of each.

    blender --background --factory-startup --python tools/blender/rocks.py -- <downloads> <models out> <previews out> <triangles> <id>...

<downloads>/<id>/<id>.blend is the 1k download with its textures beside it (tools/blender/fetch-models.ps1).
"""
import math
import os
import sys

import bpy


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:]
    return args[0], args[1], args[2], int(args[3]), args[4:]


def clear():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def load(path):
    """Every mesh object in the downloaded .blend, linked into the scene."""
    with bpy.data.libraries.load(path, link=False) as (source, target):
        target.objects = list(source.objects)
    objects = [o for o in target.objects if o is not None and o.type == "MESH"]
    for o in objects:
        bpy.context.scene.collection.objects.link(o)
    return objects


def decimate(obj, triangles):
    """Collapses the mesh to about so many triangles, keeping its UVs."""
    faces = sum(len(p.vertices) - 2 for p in obj.data.polygons)
    if faces > triangles:
        modifier = obj.modifiers.new("Decimate", "DECIMATE")
        modifier.ratio = triangles / faces
        modifier.use_collapse_triangulate = True
        bpy.context.view_layer.objects.active = obj
        bpy.ops.object.modifier_apply(modifier=modifier.name)
    return sum(len(p.vertices) - 2 for p in obj.data.polygons)


def ground(obj):
    """Puts the object's lowest point on the ground at the origin, its middle over it, in metres as scanned."""
    bpy.context.view_layer.objects.active = obj
    obj.select_set(True)
    bpy.ops.object.transform_apply(location=False, rotation=True, scale=True)
    xs = [v.co.x for v in obj.data.vertices]
    ys = [v.co.y for v in obj.data.vertices]
    zs = [v.co.z for v in obj.data.vertices]
    shift = ((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, min(zs))
    for v in obj.data.vertices:
        v.co.x -= shift[0]
        v.co.y -= shift[1]
        v.co.z -= shift[2]
    obj.location = (0, 0, 0)
    return max(max(xs) - min(xs), max(ys) - min(ys)), max(zs) - min(zs)


def export(obj, path):
    bpy.ops.object.select_all(action="DESELECT")
    obj.select_set(True)
    bpy.context.view_layer.objects.active = obj
    bpy.ops.export_scene.gltf(filepath=path, use_selection=True, export_format="GLB", export_image_format="JPEG",
                              export_jpeg_quality=88, export_yup=True, export_apply=True)


def preview(obj, path, size):
    """A picture of the object on grey, lit from the upper left."""
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 48
    scene.cycles.use_denoising = True
    scene.render.resolution_x = scene.render.resolution_y = 320
    scene.render.film_transparent = False
    scene.view_settings.view_transform = "AgX"
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    world.node_tree.nodes["Background"].inputs["Color"].default_value = (0.42, 0.44, 0.46, 1)
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 0.8
    scene.world = world
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 4
    sun.rotation_euler = (math.radians(50), math.radians(-30), math.radians(30))
    scene.collection.objects.link(sun)
    cam = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    distance = max(size) * 1.9
    cam.location = (distance * 0.75, -distance, distance * 0.55)
    target = (0, 0, size[1] * 0.4)
    direction = [target[i] - cam.location[i] for i in range(3)]
    cam.rotation_euler = (math.atan2(math.hypot(direction[0], direction[1]), -direction[2]), 0, math.atan2(direction[0], -direction[1]) + math.pi)
    cam.rotation_euler[2] = math.atan2(-direction[0], direction[1])
    cam.data.lens = 50
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    for o in (sun, cam):
        bpy.data.objects.remove(o)


def main():
    downloads, models, previews, triangles, ids = arguments()
    os.makedirs(models, exist_ok=True)
    os.makedirs(previews, exist_ok=True)
    for id in ids:
        clear()
        objects = load(os.path.join(downloads, id, id + ".blend"))
        # Poly Haven ships levels of detail as objects of the same size: only the finest is wanted. Sets of different
        # rocks keep each one.
        def size(o):
            return tuple(round(d, 2) for d in o.dimensions)
        finest = {}
        for o in objects:
            key = size(o)
            if key not in finest or len(o.data.polygons) > len(finest[key].data.polygons):
                finest[key] = o
        for o in objects:
            if o not in finest.values():
                bpy.data.objects.remove(o)
        objects = list(finest.values())
        for i, obj in enumerate(objects):
            name = id if len(objects) == 1 else f"{id}_{i + 1}"
            before = sum(len(p.vertices) - 2 for p in obj.data.polygons)
            for other in objects:
                other.hide_render = other is not obj
            after = decimate(obj, triangles)
            width, height = ground(obj)
            export(obj, os.path.join(models, name + ".glb"))
            preview(obj, os.path.join(previews, name + ".png"), (width, height))
            print(f"rock {name}: {before} -> {after} triangles, {width:.2f} x {height:.2f} m")


main()
