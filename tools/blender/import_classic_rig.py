"""Builds a classic-export machine (classic/models/<colour>/<id>.json) in Blender as a reference:
one empty per rig node, its original meshes as children, and every animation laid out on the
timeline one after another with a marker at each start. Original axes are z up, like Blender's."""
import json
import bpy
from mathutils import Matrix, Vector

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
    add_animations(nodes, rig.get("animations", {}))
    return nodes


def add_animations(nodes, animations, only=None):
    """
    Keys each animation (or those named in `only`) on the timeline after the last marker, behind a marker of
    its name. A node whose pivot was moved (its "pivot_offset", see split_parts.move_pivot) keeps it.
    """
    from mathutils import Vector
    scene = bpy.context.scene
    start = scene.frame_end + 10 if scene.timeline_markers else 0
    for name, anim in animations.items():
        if only is not None and name not in only:
            continue
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
                f = start + s * step
                e.keyframe_insert("location", frame=f)
                e.keyframe_insert("rotation_euler", frame=f)
                e.keyframe_insert("scale", frame=f)
            frames = max(frames, round((len(samples) - 1) * step))
        start += frames + 10
    for e in nodes:
        e.matrix_basis = matrix(e["rest"])
    scene.frame_start, scene.frame_end = 0, start - 10
