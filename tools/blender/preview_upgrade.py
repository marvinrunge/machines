"""Renders a classic model before and after upgrade_classic.py, side by side, with its HD textures:

    blender -b --factory-startup -P tools/blender/preview_upgrade.py -- <classic folder> <out.png> <model id> [race]

The left half is the classic export with its own normals, the right half the upgrade with its bevels and
baked occlusion."""
import json
import math
import os
import sys

import bpy
from mathutils import Matrix, Vector

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from upgrade_classic import clear_scene, world_transforms  # noqa: E402


def material(name, texture, diffuse, emissive, classic, cache):
    key = (texture, tuple(diffuse), tuple(emissive), name.startswith("after"))
    if key in cache:
        return cache[key]
    mat = bpy.data.materials.new(name)
    tree = mat.node_tree if mat.node_tree else None
    if tree is None:
        mat.use_nodes = True
        tree = mat.node_tree
    nodes, links = tree.nodes, tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Roughness"].default_value = 0.55
    bsdf.inputs["Metallic"].default_value = 0.2
    colour = nodes.new("ShaderNodeRGB")
    colour.outputs[0].default_value = (*diffuse[:3], 1)
    mix = nodes.new("ShaderNodeMix")
    mix.data_type = "RGBA"
    mix.blend_type = "MULTIPLY"
    mix.inputs["Factor"].default_value = 1
    links.new(colour.outputs[0], mix.inputs["A"])
    mix.inputs["B"].default_value = (1, 1, 1, 1)
    image = None
    if texture:
        stem = os.path.splitext(os.path.basename(texture))[0].lower()
        hd = os.path.join(classic, "hd", "models", "texture4", stem + ".png")
        game = open(os.path.join(classic, "game_path.txt")).read().strip()
        for path in (hd, os.path.join(game, texture)):
            if os.path.exists(path):
                image = bpy.data.images.load(path, check_existing=True)
                break
    if image:
        tex = nodes.new("ShaderNodeTexImage")
        tex.image = image
        links.new(tex.outputs["Color"], mix.inputs["B"])
    out = mix.outputs["Result"]
    if name.startswith("after"):
        ao = nodes.new("ShaderNodeVertexColor")
        ao.layer_name = "ao"
        mul = nodes.new("ShaderNodeMix")
        mul.data_type = "RGBA"
        mul.blend_type = "MULTIPLY"
        mul.inputs["Factor"].default_value = 1
        links.new(out, mul.inputs["A"])
        links.new(ao.outputs["Color"], mul.inputs["B"])
        out = mul.outputs["Result"]
    links.new(out, bsdf.inputs["Base Color"])
    if max(emissive[:3]) > 0.01:
        links.new(out, bsdf.inputs["Emission Color"])
        bsdf.inputs["Emission Strength"].default_value = 1.5
    cache[key] = mat
    return mat


def build(path, prefix, offset, classic, cache):
    model = json.load(open(path))
    nodes = model["rig"]["nodes"]
    world = world_transforms(nodes)
    for i, node in enumerate(nodes):
        for k, part in enumerate(node["parts"]):
            pos, nrm, uvs = part["positions"], part.get("normals", []), part["uvs"]
            count = len(pos) // 3
            if count < 3:
                continue
            verts = [pos[j * 3:j * 3 + 3] for j in range(count)]
            mesh = bpy.data.meshes.new(f"{prefix}_{i}_{k}")
            mesh.from_pydata(verts, [], [(t, t + 1, t + 2) for t in range(0, count - 2, 3)])
            layer = mesh.uv_layers.new(name="uv")
            for li, loop in enumerate(mesh.loops):
                layer.data[li].uv = uvs[loop.vertex_index * 2:loop.vertex_index * 2 + 2]
            if "ao" in part:
                ao = mesh.color_attributes.new("ao", "FLOAT_COLOR", "POINT")
                for vi, value in enumerate(part["ao"]):
                    ao.data[vi].color = (value, value, value, 1)
            if len(nrm) == len(pos):
                normals = [Vector(nrm[j * 3:j * 3 + 3]).normalized() for j in range(count)]
                # Faces turned against their normals are flipped, as the game does.
                for poly in mesh.polygons:
                    if poly.normal.dot(sum((normals[v] for v in poly.vertices), Vector())) < 0:
                        poly.flip()
                mesh.update()
                mesh.normals_split_custom_set([normals[loop.vertex_index] for loop in mesh.loops])
            mat = material(prefix, part.get("texture", ""), part.get("diffuse", [1, 1, 1, 1]), part.get("emissive", [0, 0, 0]), classic, cache)
            mesh.materials.append(mat)
            ob = bpy.data.objects.new(mesh.name, mesh)
            ob.matrix_world = Matrix.Translation(offset) @ world[i]
            bpy.context.scene.collection.objects.link(ob)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    classic, out, model = os.path.abspath(argv[0]), os.path.abspath(argv[1]), argv[2]
    race = argv[3] if len(argv) > 3 else "red"
    # A close look (zoom above 1) renders each model on its own, <out>-before.png and <out>-after.png.
    zoom = float(argv[4]) if len(argv) > 4 else 1.0
    clear_scene()
    cache = {}
    before = os.path.join(classic, "models", race, model + ".json")
    after = os.path.join(classic, "remake", "models", race, model + ".json")
    build(before, "before", Vector((0, 0, 0)), classic, cache)
    objs = list(bpy.context.scene.objects)
    corners = [ob.matrix_world @ Vector(c) for ob in objs for c in ob.bound_box]
    low = Vector((min(c[k] for c in corners) for k in range(3)))
    high = Vector((max(c[k] for c in corners) for k in range(3)))
    size = high - low
    gap = max(size.x, size.y) * 1.4
    build(after, "after", Vector((0, -gap, 0)), classic, cache)

    scene = bpy.context.scene
    for engine in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT"):
        try:
            scene.render.engine = engine
            break
        except TypeError:
            pass
    scene.render.resolution_x, scene.render.resolution_y = 1600, 800
    world = bpy.data.worlds.new("w")
    scene.world = world
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs["Color"].default_value = (0.55, 0.6, 0.68, 1)
    bg.inputs["Strength"].default_value = 0.6
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(50), 0, math.radians(-35))
    scene.collection.objects.link(sun)
    centre = (low + high) / 2 + Vector((0, -gap / 2, 0))
    cam = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = max(gap * 2.1, size.z * 2.4)
    direction = Vector((1.0, -0.35, 0.55)).normalized()
    cam.location = centre + direction * size.length * 4
    cam.rotation_euler = (-direction).to_track_quat("-Z", "Y").to_euler()
    cam.data.clip_end = size.length * 20
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.view_settings.view_transform = "AgX" if "AgX" in [i.identifier for i in bpy.types.ColorManagedViewSettings.bl_rna.properties["view_transform"].enum_items] else "Standard"
    if zoom <= 1:
        scene.render.filepath = out
        bpy.ops.render.render(write_still=True)
        return
    scene.render.resolution_x = 1000
    cam.data.ortho_scale = size.length / zoom
    for name, shift in (("before", 0), ("after", -gap)):
        cam.location = (low + high) / 2 + Vector((0, shift, size.z * 0.15)) + direction * size.length * 4
        scene.render.filepath = os.path.splitext(out)[0] + f"-{name}.png"
        bpy.ops.render.render(write_still=True)


if __name__ == "__main__":
    main()
