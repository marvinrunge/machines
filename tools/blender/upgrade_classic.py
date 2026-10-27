"""Stage 1 of the model upgrade: the classic exports (classic/models/<race>/<id>.json) with rounded edges and
baked ambient occlusion, written in the same format to classic/remake/models/<race>/<id>.json. The game uses
them in the remake look (ClassicAssets). Silhouettes, rigs, animations, sprites and weapons stay the original's.

    blender -b --factory-startup -P tools/blender/upgrade_classic.py -- <classic folder> [model id ...]

Each rig node becomes one object in its rest pose, all of them at once, so the occlusion sees the whole machine.
Per node:
- Triangles are turned to face the way the original's vertex normals point, then welded.
- Edges where the original shaded flat (its normals differ across the edge) and that fold by more than
  SHARP_ANGLE get a small bevel; its normals are hardened, so the faces stay flat and the bevel catches the light.
  Edges the original shaded smoothly stay smooth.
- Cycles bakes ambient occlusion into the vertices (`ao`, one value per vertex, 1 is open).
The model's own parts (its mesh without the rig) are rebuilt from the nodes' in the rest pose."""
import json
import math
import os
import sys

import bmesh
from mathutils import kdtree
import bpy
from mathutils import Matrix, Vector

SHARP_ANGLE = math.radians(28)
# The bevel as a share of the model's size (its bounding box diagonal), within these limits in metres.
BEVEL_SHARE = 0.012
BEVEL_MIN, BEVEL_MAX = 0.015, 0.25
BEVEL_SEGMENTS = 2
# A point whose faces' corner angles add up to less than this is a spike tip and keeps its sharp point (a flat
# surface has 360°, a box corner 270°).
TIP_ANGLE = math.radians(150)
# Occlusion: the distance rays look as a share of the model's size, and the samples per vertex.
AO_SHARE = 0.2
AO_SAMPLES = 128
# How dark full occlusion gets; the game's own SSAO adds to it.
AO_FLOOR = 0.12


def matrix(v):
    x, y, z, p = Vector(v[0:3]), Vector(v[3:6]), Vector(v[6:9]), Vector(v[9:12])
    return Matrix(((x.x, y.x, z.x, p.x), (x.y, y.y, z.y, p.y), (x.z, y.z, z.z, p.z), (0, 0, 0, 1)))


def part_key(part):
    return (part.get("texture", ""), tuple(part.get("diffuse", ())), tuple(part.get("ambient", ())), tuple(part.get("emissive", ())))


def clear_scene():
    for ob in list(bpy.data.objects):
        bpy.data.objects.remove(ob, do_unlink=True)
    for me in list(bpy.data.meshes):
        bpy.data.meshes.remove(me)


def world_transforms(nodes):
    world = {}

    def of(i):
        if i not in world:
            n = nodes[i]
            m = matrix(n["transform"])
            p = n["parent"]
            world[i] = of(p) @ m if 0 <= p < len(nodes) and p != i else m
        return world[i]

    return [of(i) for i in range(len(nodes))]


def build_node(name, parts, world):
    """One object for a node's parts (one material index per part), triangles facing their original normals."""
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("uv")
    smooth_normal = bm.loops.layers.float_vector.new("orig_normal")
    weight = bm.edges.layers.float.new("bevel_weight_edge")
    # Two-sided plates (wheels, fins) are two triangles back to back, often with a different look on each side
    # (a wheel's hub outside, black inside). Welding would drop the second one, and the occlusion bake would find
    # each side covered by the other, so the second side goes to an object of its own that stays out of the bake
    # and takes its twin's occlusion.
    tb = bmesh.new()
    twin_uv = tb.loops.layers.uv.new("uv")
    seen = set()
    for index, part in enumerate(parts):
        pos, nrm, uvs = part["positions"], part.get("normals", []), part["uvs"]
        has_normals = len(nrm) == len(pos)
        for t in range(0, len(pos) // 3 - 2, 3):
            corners = [Vector(pos[(t + k) * 3:(t + k) * 3 + 3]) for k in range(3)]
            normals = [Vector(nrm[(t + k) * 3:(t + k) * 3 + 3]) if has_normals else Vector() for k in range(3)]
            texcoords = [uvs[(t + k) * 2:(t + k) * 2 + 2] for k in range(3)]
            front = (corners[1] - corners[0]).cross(corners[2] - corners[0])
            if front.length < 1e-10:
                continue
            total = normals[0] + normals[1] + normals[2]
            if total.length < 0.1:
                normals = [front.normalized()] * 3
            elif front.dot(total) < 0:
                corners, normals, texcoords = corners[::-1], normals[::-1], texcoords[::-1]
            key = frozenset(tuple(round(c, 4) for c in corner) for corner in corners)
            if key in seen:
                face = tb.faces.new([tb.verts.new(c) for c in corners])
                face.material_index = index
                for loop, t in zip(face.loops, texcoords):
                    loop[twin_uv].uv = t
                continue
            seen.add(key)
            face = bm.faces.new([bm.verts.new(c) for c in corners])
            face.material_index = index
            for loop, n, t in zip(face.loops, normals, texcoords):
                loop[uv].uv = t
                loop[smooth_normal] = n.normalized() if n.length > 1e-6 else face.normal
    twin_ob = None
    if tb.faces:
        twin_mesh = bpy.data.meshes.new(name + "_twins")
        tb.to_mesh(twin_mesh)
        twin_ob = bpy.data.objects.new(twin_mesh.name, twin_mesh)
        twin_ob.matrix_world = world
    tb.free()
    bmesh.ops.remove_doubles(bm, verts=bm.verts, dist=1e-4)
    bm.normal_update()

    # Spikes (the tips of legs, claws and antennas): the faces meeting there leave little of the full turn round
    # the point. A bevel would cut the tip off and shorten the spike, and legs would float above the ground.
    tips = {v for v in bm.verts if v.link_loops and sum(loop.calc_angle() for loop in v.link_loops) < TIP_ANGLE}

    # Edges to bevel: two faces, folding sharply, shaded flat in the original, and not running into a spike.
    sharp = []
    for edge in bm.edges:
        edge.smooth = True
        if len(edge.link_faces) != 2:
            edge.smooth = False
            continue
        a, b = edge.link_faces
        if a.normal.angle(b.normal, 0) < SHARP_ANGLE or a.normal.dot(b.normal) < -0.999:
            continue
        flat = False
        for v in edge.verts:
            na = next(l[smooth_normal] for l in a.loops if l.vert == v)
            nb = next(l[smooth_normal] for l in b.loops if l.vert == v)
            if na.angle(nb, 0) > math.radians(5):
                flat = True
        if flat:
            edge.smooth = False
            if not any(v in tips for v in edge.verts):
                sharp.append(edge)
    for face in bm.faces:
        face.smooth = True
    for edge in sharp:
        edge[weight] = 1.0

    mesh = bpy.data.meshes.new(name)
    bm.to_mesh(mesh)
    bm.free()
    ob = bpy.data.objects.new(name, mesh)
    ob.matrix_world = world
    bpy.context.scene.collection.objects.link(ob)
    return ob, twin_ob, len(sharp)


def share_occlusion(twin, ob):
    """The twin faces take the occlusion of the nearest corners of the object they were split from."""
    source = ob.data
    ao = source.color_attributes["ao"]
    tree = kdtree.KDTree(len(source.loops))
    for loop in source.loops:
        tree.insert(source.vertices[loop.vertex_index].co, loop.index)
    tree.balance()
    target = twin.data.color_attributes.new("ao", "FLOAT_COLOR", "CORNER")
    for loop in twin.data.loops:
        _, index, _ = tree.find(twin.data.vertices[loop.vertex_index].co)
        target.data[loop.index].color = ao.data[index].color


def bevel(ob, width):
    mod = ob.modifiers.new("bevel", "BEVEL")
    mod.width = width
    mod.segments = BEVEL_SEGMENTS
    mod.limit_method = "WEIGHT"
    mod.harden_normals = True
    mod.use_clamp_overlap = True
    mod.loop_slide = True
    tri = ob.modifiers.new("tri", "TRIANGULATE")
    tri.keep_custom_normals = True
    depsgraph = bpy.context.evaluated_depsgraph_get()
    evaluated = ob.evaluated_get(depsgraph)
    mesh = bpy.data.meshes.new_from_object(evaluated, preserve_all_data_layers=True, depsgraph=depsgraph)
    ob.modifiers.clear()
    old = ob.data
    ob.data = mesh
    bpy.data.meshes.remove(old)


def bake_ao(objects, distance):
    scene = bpy.context.scene
    try:
        scene.render.engine = "CYCLES"
    except TypeError as error:
        raise SystemExit(f"no Cycles: {error}")
    scene.cycles.samples = AO_SAMPLES
    scene.cycles.device = "CPU"
    scene.world = scene.world or bpy.data.worlds.new("world")
    scene.world.light_settings.distance = distance
    bpy.ops.object.select_all(action="DESELECT")
    for ob in objects:
        attr = ob.data.color_attributes.new("ao", "FLOAT_COLOR", "CORNER")
        ob.data.color_attributes.active_color = attr
        ob.select_set(True)
    bpy.context.view_layer.objects.active = objects[0]
    scene.render.bake.target = "VERTEX_COLORS"
    bpy.ops.object.bake(type="AO", target="VERTEX_COLORS")


def export_parts(ob, parts, space):
    """The object's triangles as the original's parts again, in the given space (node local or model)."""
    mesh = ob.data
    uv = mesh.uv_layers[0].data
    ao = mesh.color_attributes.get("ao")
    normals = mesh.corner_normals
    out = [dict(p, positions=[], normals=[], uvs=[], indices=[], ao=[]) for p in parts]
    to_space = space @ ob.matrix_world
    rotate = to_space.to_3x3().inverted().transposed()
    for poly in mesh.polygons:
        target = out[poly.material_index]
        for li in poly.loop_indices:
            loop = mesh.loops[li]
            p = to_space @ mesh.vertices[loop.vertex_index].co
            n = (rotate @ normals[li].vector).normalized()
            target["positions"] += [round(c, 5) for c in p]
            target["normals"] += [round(c, 4) for c in n]
            target["uvs"] += [round(c, 5) for c in uv[li].uv]
            occlusion = ao.data[li].color[0] if ao else 1.0
            target["ao"].append(round(AO_FLOOR + (1 - AO_FLOOR) * occlusion, 3))
            target["indices"].append(len(target["indices"]))
    return out


def joined(exports):
    """Parts exported from several objects, joined part by part."""
    parts = exports[0]
    for other in exports[1:]:
        for part, more in zip(parts, other):
            for field in ("positions", "normals", "uvs", "ao"):
                part[field] += more[field]
            part["indices"] = list(range(len(part["ao"])))
    return parts


def upgrade(source, target):
    clear_scene()
    model = json.load(open(source))
    rig = model.get("rig")
    if not rig:
        return None
    nodes = rig["nodes"]
    world = world_transforms(nodes)
    objects = []
    twins = {}
    sharp_edges = 0
    for i, node in enumerate(nodes):
        if not node["parts"]:
            objects.append(None)
            continue
        ob, twin, count = build_node(f"n{i:02d}", node["parts"], world[i])
        sharp_edges += count
        objects.append(ob)
        if twin:
            twins[i] = twin
    present = [ob for ob in objects if ob]
    if not present:
        return None

    corners = [ob.matrix_world @ v.co for ob in present for v in ob.data.vertices]
    low = Vector((min(c[k] for c in corners) for k in range(3)))
    high = Vector((max(c[k] for c in corners) for k in range(3)))
    size = (high - low).length
    width = min(BEVEL_MAX, max(BEVEL_MIN, size * BEVEL_SHARE))
    # No part's lowest point may rise: feet, tracks and a building's base stand on the ground with it (the lowest
    # part of a machine can be the shadow plate the game leaves out, so every part is measured).
    def bottom(ob):
        return min((ob.matrix_world @ v.co).z for v in ob.data.vertices)

    bottoms = {ob: bottom(ob) for ob in present}
    for ob in present:
        bevel(ob, width)
    lifted, lifted_part = max((bottom(ob) - z, ob.name) for ob, z in bottoms.items())
    bake_ao(present, size * AO_SHARE)
    for i, twin in twins.items():
        share_occlusion(twin, objects[i])

    # The nodes' own parts, in their own space.
    new_parts = {}
    for i, (node, ob) in enumerate(zip(nodes, objects)):
        if ob:
            pieces = [ob] + ([twins[i]] if i in twins else [])
            originals = node["parts"]
            node["parts"] = joined([export_parts(o, originals, world[i].inverted()) for o in pieces])
            new_parts[i] = joined([export_parts(o, originals, Matrix.Identity(4)) for o in pieces])
    # The model's parts: every node part with the same texture and colours, in the model's space.
    rebuilt = []
    for part in model.get("parts", []):
        merged = dict(part, positions=[], normals=[], uvs=[], indices=[], ao=[])
        for i, pieces in new_parts.items():
            for piece in pieces:
                if part_key(piece) == part_key(part):
                    for field in ("positions", "normals", "uvs", "ao"):
                        merged[field] += piece[field]
        merged["indices"] = list(range(len(merged["ao"])))
        rebuilt.append(merged if merged["positions"] else part)
    model["parts"] = rebuilt
    model["upgrade"] = {"bevel": round(width, 4), "sharpEdges": sharp_edges, "lifted": round(lifted, 4), "liftedPart": lifted_part}
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w") as f:
        json.dump(model, f, separators=(",", ":"))
    return round(width, 3), sharp_edges, round(lifted, 3), lifted_part


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    classic = argv[0] if argv else os.path.join(os.path.dirname(__file__), "..", "..", "classic")
    only = set(argv[1:])
    for race in ("red", "blue", "green", "yellow"):
        folder = os.path.join(classic, "models", race)
        for name in sorted(os.listdir(folder)):
            if not name.endswith(".json") or (only and name[:-5] not in only):
                continue
            result = upgrade(os.path.join(folder, name), os.path.join(classic, "remake", "models", race, name))
            print(f"{race}/{name[:-5]}: {result}", flush=True)


if __name__ == "__main__":
    main()
