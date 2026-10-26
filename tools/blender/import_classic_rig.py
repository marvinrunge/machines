"""Builds a classic-export machine (classic/models/<colour>/<id>.json) in Blender as a reference:
one empty per rig node, its original meshes as children, and every animation laid out on the
timeline one after another with a marker at each start. Original axes are z up, like Blender's."""
import json
import bpy
from mathutils import Matrix, Quaternion, Vector

FPS = 30


def matrix(v):
    x, y, z, p = Vector(v[0:3]), Vector(v[3:6]), Vector(v[6:9]), Vector(v[9:12])
    return Matrix(((x.x, y.x, z.x, p.x), (x.y, y.y, z.y, p.y), (x.z, y.z, z.z, p.z), (0, 0, 0, 1)))


def build(path, prefix="orig"):
    d = json.load(open(path))
    rig = d["rig"]
    col = bpy.data.collections.new(prefix)
    bpy.context.scene.collection.children.link(col)
    nodes = []
    for i, n in enumerate(rig["nodes"]):
        e = bpy.data.objects.new(f"{prefix}_{i:02d}", None)
        e.empty_display_size = 0.3
        e.empty_display_type = "ARROWS"
        e.rotation_mode = "QUATERNION"
        col.objects.link(e)
        nodes.append(e)
    for i, n in enumerate(rig["nodes"]):
        e = nodes[i]
        if 0 <= n["parent"] < len(nodes) and n["parent"] != i:
            e.parent = nodes[n["parent"]]
        e.matrix_parent_inverse = Matrix.Identity(4)
        e.matrix_basis = matrix(n["transform"])
        e["rest"] = n["transform"]
        for k, part in enumerate(n["parts"]):
            pos = part["positions"]
            verts = [pos[j:j + 3] for j in range(0, len(pos), 3)]
            faces = [(j, j + 1, j + 2) for j in range(0, len(verts) - 2, 3)]
            me = bpy.data.meshes.new(f"{prefix}_{i:02d}_{k}")
            me.from_pydata(verts, [], faces)
            ob = bpy.data.objects.new(me.name, me)
            ob.parent = e
            ob["texture"] = part.get("texture", "")
            col.objects.link(ob)

    bpy.context.scene.render.fps = FPS
    add_animations(nodes, rig.get("animations", {}), start=0)
    return nodes


def fcurves(obj):
    """An object's animation curves, in legacy and in slotted (Blender 4.4+) actions."""
    action = obj.animation_data.action if obj.animation_data else None
    if action is None:
        return []
    curves = list(getattr(action, "fcurves", []))
    if not curves and hasattr(action, "layers"):
        for layer in action.layers:
            for strip in layer.strips:
                for bag in strip.channelbags:
                    curves += list(bag.fcurves)
    return curves


def key(obj, frame):
    """
    Keys an object's transform as the game plays it: quaternions (no euler flips), each on the same side as the
    one keyed before it, so the turn between them is the short way.
    """
    obj.rotation_mode = "QUATERNION"
    before = obj.get("_last_quaternion")
    if before is not None and obj.rotation_quaternion.dot(Quaternion(before)) < 0:
        obj.rotation_quaternion = -obj.rotation_quaternion
    obj["_last_quaternion"] = list(obj.rotation_quaternion)
    obj.keyframe_insert("location", frame=frame)
    obj.keyframe_insert("rotation_quaternion", frame=frame)
    obj.keyframe_insert("scale", frame=frame)


def linear(objects):
    """Straight interpolation between samples, as the game's slerp: Bezier curves overshoot between them."""
    for obj in objects:
        for curve in fcurves(obj):
            for point in curve.keyframe_points:
                point.interpolation = "LINEAR"


def add_animations(nodes, animations, only=None, start=None):
    """
    Keys each animation (or those named in `only`) on the timeline from `start` (by default after the last
    marker), behind a marker of its name. A node whose pivot was moved (its "pivot_offset", see
    split_parts.move_pivot) keeps it. Between two cycles the pose moves straight from the end of one to the
    start of the next.
    """
    scene = bpy.context.scene
    if start is None:
        start = scene.frame_end + 10 if scene.timeline_markers else 0
    for name, anim in animations.items():
        if only is not None and name not in only:
            continue
        if not any(m.name == name and m.frame == start for m in scene.timeline_markers):
            scene.timeline_markers.new(name, frame=start)
        frames = 0
        for track in anim["tracks"]:
            e = nodes[track["node"]]
            offset = Vector(e.get("pivot_offset", (0, 0, 0)))
            samples = track["samples"]
            step = anim["duration"] * FPS / max(1, len(samples) - 1)
            for s, v in enumerate(samples):
                e.matrix_basis = matrix(v)
                e.location = e.location + offset
                key(e, start + s * step)
            frames = max(frames, round((len(samples) - 1) * step))
        start += frames + 10
    linear(nodes)
    for e in nodes:
        e.matrix_basis = matrix(e["rest"])
    scene.frame_start, scene.frame_end = 0, start - 10
