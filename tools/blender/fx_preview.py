"""A contact sheet of the effect models, each original next to its remake (tools/blender/fx_models.py):

    blender -b --factory-startup -P tools/blender/fx_preview.py -- <classic folder> <out.png> <name>[:<machine id>] ...

Each row is one model: the original (classic/models/fx/<name>.json, with its HD texture) and the remake
(classic/remake/fx/<name>.json) from the same three-quarter view, then the remake from the side and from behind.
A machine id after the name (a red export, e.g. AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7)
adds a last tile with the remake beside that machine at the same scale, the HD one where there is one. Without a
remake the row shows the original from all three sides. Rendered with EEVEE in a grey studio light like the game's.
"""
import json
import math
import os
import sys

import bpy
import numpy as np
from mathutils import Vector

TILE = 360
LABEL = 22


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:]
    return os.path.abspath(args[0]), os.path.abspath(args[1]), args[2:]


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    engines = [i.identifier for i in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    scene.render.engine = next(n for n in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE") if n in engines)
    scene.render.resolution_x = scene.render.resolution_y = TILE
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    world = bpy.data.worlds.new("studio")
    world.use_nodes = True
    background = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    background.inputs[0].default_value = (0.55, 0.56, 0.58, 1)
    background.inputs[1].default_value = 0.8
    scene.world = world
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.2
    sun.rotation_euler = (math.radians(50), 0, math.radians(35))
    scene.collection.objects.link(sun)
    fill = bpy.data.objects.new("fill", bpy.data.lights.new("fill", "SUN"))
    fill.data.energy = 0.8
    fill.rotation_euler = (math.radians(-60), 0, math.radians(200))
    scene.collection.objects.link(fill)
    return scene


def image(path, colour=True):
    if not path or not os.path.isfile(path):
        return None
    loaded = bpy.data.images.load(path, check_existing=True)
    if not colour:
        loaded.colorspace_settings.name = "Non-Color"
    return loaded


def material_for(part, classic):
    """The part's look: its baked maps (remakes, Meshy machines), or its texture tinted as the original lit it."""
    material = bpy.data.materials.new("part")
    material.use_nodes = True
    tree = material.node_tree
    bsdf = next(n for n in tree.nodes if n.type == "BSDF_PRINCIPLED")
    out = next(n for n in tree.nodes if n.type == "OUTPUT_MATERIAL")

    def texture(img):
        node = tree.nodes.new("ShaderNodeTexImage")
        node.image = img
        return node

    maps = part.get("maps")
    if maps:
        # Map paths are relative to classic/.
        root = classic
        albedo = image(os.path.join(root, maps.get("albedo", "")))
        if albedo:
            tree.links.new(texture(albedo).outputs["Color"], bsdf.inputs["Base Color"])
        normal = image(os.path.join(root, maps.get("normal", "")), colour=False)
        if normal:
            node = tree.nodes.new("ShaderNodeNormalMap")
            tree.links.new(texture(normal).outputs["Color"], node.inputs["Color"])
            tree.links.new(node.outputs["Normal"], bsdf.inputs["Normal"])
        orm = image(os.path.join(root, maps.get("orm", "")), colour=False)
        if orm:
            split = tree.nodes.new("ShaderNodeSeparateColor")
            tree.links.new(texture(orm).outputs["Color"], split.inputs["Color"])
            tree.links.new(split.outputs[1], bsdf.inputs["Roughness"])
            tree.links.new(split.outputs[2], bsdf.inputs["Metallic"])
        emission = image(os.path.join(root, maps.get("emission", "")))
        if emission:
            tree.links.new(texture(emission).outputs["Color"], bsdf.inputs["Emission Color"])
            bsdf.inputs["Emission Strength"].default_value = part.get("emissionEnergy", 2.0)
        return material

    diffuse = part.get("diffuse", [1, 1, 1, 1])
    emissive = part.get("emissive", [0, 0, 0])
    name = part.get("texture", "")
    hd = image(os.path.join(classic, "hd", os.path.splitext(name)[0] + ".png")) if name else None
    glowing = any(emissive) and sum(diffuse[:3]) < 0.01
    if hd:
        node = texture(hd)
        if glowing:
            # Unlit in the original: the texture in the emissive colour.
            emit = tree.nodes.new("ShaderNodeEmission")
            tree.links.new(node.outputs["Color"], emit.inputs["Color"])
            mix = tree.nodes.new("ShaderNodeMixShader")
            clear = tree.nodes.new("ShaderNodeBsdfTransparent")
            tree.links.new(node.outputs["Alpha"], mix.inputs[0])
            tree.links.new(clear.outputs[0], mix.inputs[1])
            tree.links.new(emit.outputs[0], mix.inputs[2])
            tree.links.new(mix.outputs[0], out.inputs["Surface"])
        else:
            tint = tree.nodes.new("ShaderNodeMix")
            tint.data_type = "RGBA"
            tint.blend_type = "MULTIPLY"
            tint.inputs[0].default_value = 1
            tint.inputs[6].default_value = (*diffuse[:3], 1)
            tree.links.new(node.outputs["Color"], tint.inputs[7])
            tree.links.new(tint.outputs[2], bsdf.inputs["Base Color"])
            tree.links.new(node.outputs["Alpha"], bsdf.inputs["Alpha"])
    else:
        bsdf.inputs["Base Color"].default_value = (*(emissive if glowing else diffuse[:3]), 1)
        if glowing:
            bsdf.inputs["Emission Color"].default_value = (*emissive, 1)
            bsdf.inputs["Emission Strength"].default_value = 1
    if len(diffuse) > 3 and diffuse[3] < 1:
        bsdf.inputs["Alpha"].default_value = diffuse[3]
    return material


def load(path, classic, name="model"):
    """A model in the export format as one object, z up as exported."""
    model = json.load(open(path))
    verts, faces, uvs, loop_normals, mats, materials = [], [], [], [], [], []
    for part in model["parts"]:
        p = part["positions"]
        count = len(p) // 3
        if count < 3:
            continue
        u = part.get("uvs", [])
        n = part.get("normals", [])
        base = len(verts)
        verts += [tuple(p[i:i + 3]) for i in range(0, count * 3, 3)]
        uvs += [(u[i], 1 - u[i + 1]) for i in range(0, len(u), 2)] if len(u) == count * 2 else [(0, 0)] * count
        loop_normals += [tuple(n[i:i + 3]) for i in range(0, count * 3, 3)] if len(n) == count * 3 else [None] * count
        faces += [(base + t, base + t + 1, base + t + 2) for t in range(0, count - 2, 3)]
        mats += [len(materials)] * (count // 3)
        materials.append(material_for(part, classic))
    mesh = bpy.data.meshes.new(name)
    mesh.from_pydata(verts, [], faces)
    layer = mesh.uv_layers.new()
    for poly in mesh.polygons:
        poly.material_index = mats[poly.index]
        for li in poly.loop_indices:
            layer.data[li].uv = uvs[mesh.loops[li].vertex_index]
    if all(v is not None for v in loop_normals):
        mesh.normals_split_custom_set([loop_normals[mesh.loops[li].vertex_index] for li in range(len(mesh.loops))])
    for m in materials:
        mesh.materials.append(m)
    ob = bpy.data.objects.new(name, mesh)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def bounds(objects):
    corners = [o.matrix_world @ Vector(c) for o in objects for c in o.bound_box]
    lo = Vector([min(c[i] for c in corners) for i in range(3)])
    hi = Vector([max(c[i] for c in corners) for i in range(3)])
    return lo, hi


def shoot(scene, objects, direction, label, path):
    """Renders the objects from a direction, framed, with a label at the bottom."""
    lo, hi = bounds(objects)
    centre, size = (lo + hi) / 2, max((hi - lo).length, 0.2)
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = size * 1.15
    cam.data.clip_end = size * 20
    scene.collection.objects.link(cam)
    scene.camera = cam
    cam.location = centre + direction.normalized() * size * 4
    cam.rotation_euler = (centre - cam.location).to_track_quat("-Z", "Y").to_euler()
    text = bpy.data.curves.new("label", "FONT")
    text.body = label
    text.size = cam.data.ortho_scale * 0.055
    words = bpy.data.objects.new("label", text)
    words.parent = cam
    words.location = (-cam.data.ortho_scale * 0.47, -cam.data.ortho_scale * 0.47, -size)
    ink = bpy.data.materials.new("ink")
    ink.use_nodes = True
    bsdf = next(n for n in ink.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0, 0, 0, 1)
    bsdf.inputs["Emission Color"].default_value = (1, 1, 1, 1)
    bsdf.inputs["Emission Strength"].default_value = 1
    text.materials.append(ink)
    scene.collection.objects.link(words)
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    bpy.data.objects.remove(words)
    bpy.data.objects.remove(cam)


def machine_path(classic, machine):
    for folder in ("meshy/models/red", "remake/models/red", "models/red"):
        path = os.path.join(classic, folder, machine + ".json")
        if os.path.exists(path):
            return path
    return None


VIEWS = {
    "3/4": Vector((1, -1.1, 0.8)),
    "side": Vector((0, -1, 0.05)),
    "back": Vector((-1, -0.35, 0.3)),
}


def row(scene, classic, entry, folder):
    name, _, machine = entry.partition(":")
    original = os.path.join(classic, "models", "fx", name + ".json")
    remade = os.path.join(classic, "remake", "fx", name + ".json")
    tiles = []

    def tile(objects, view, label):
        path = os.path.join(folder, f"{name}-{len(tiles)}.png")
        shoot(scene, objects, VIEWS[view], label, path)
        tiles.append(path)
        for o in objects:
            o.hide_render = True

    if os.path.exists(original):
        ob = load(original, classic, name)
        tri = len(ob.data.polygons)
        if os.path.exists(remade):
            tile([ob], "3/4", f"{name} original {tri}")
        else:
            for view in VIEWS:
                ob.hide_render = False
                tile([ob], view, f"{name} original {view} {tri}")
    if os.path.exists(remade):
        ob = load(remade, classic, name + "_new")
        tri = len(ob.data.polygons)
        for view in VIEWS:
            ob.hide_render = False
            tile([ob], view, f"{name} new {view} {tri}")
        if machine and (path := machine_path(classic, machine)):
            unit = load(path, classic, "machine")
            lo, hi = bounds([unit])
            ob.hide_render = False
            ob.location = (lo.x, hi.y + 1.5, (lo.z + hi.z) / 2)
            tile([unit, ob], "3/4", f"{name} beside {machine.split('_')[2].lower()}")
    return tiles


def sheet(rows, out):
    """The tiles in a grid, a row per model."""
    width = max(len(r) for r in rows) * TILE
    pixels = np.full((len(rows) * TILE, width, 4), 0.3, dtype=np.float32)
    pixels[..., 3] = 1
    for y, tiles in enumerate(rows):
        for x, path in enumerate(tiles):
            img = bpy.data.images.load(path)
            data = np.array(img.pixels[:], dtype=np.float32).reshape(img.size[1], img.size[0], 4)
            top = (len(rows) - 1 - y) * TILE
            pixels[top:top + TILE, x * TILE:(x + 1) * TILE] = data
            bpy.data.images.remove(img)
    result = bpy.data.images.new("sheet", width, len(rows) * TILE, alpha=True)
    result.pixels = pixels.ravel()
    result.filepath_raw = out
    result.file_format = "PNG"
    result.save()


def main():
    classic, out, entries = arguments()
    folder = os.path.join(os.path.dirname(out), os.path.splitext(os.path.basename(out))[0] + "-tiles")
    os.makedirs(folder, exist_ok=True)
    rows = []
    for entry in entries:
        scene = reset()
        rows.append(row(scene, classic, entry, folder))
        print("rendered", entry, flush=True)
    sheet([r for r in rows if r], out)
    print("sheet", out, flush=True)


if __name__ == "__main__":
    main()
