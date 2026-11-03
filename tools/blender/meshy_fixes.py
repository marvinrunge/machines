"""Small fixes by hand to single Meshy models, which tools/blender/meshy_rig.py then uses in their place: each fixed
model is written beside its download as <meshy folder>/<name>/fixed.glb (model.glb stays as Meshy made it).

    blender -b --factory-startup -P tools/blender/meshy_fixes.py -- <meshy folder> [name ...]

FIXES names per Meshy model what to do, all in the model's own axes (Blender's after the GLB import, z up), each
within a box (lowest and highest corner):
- ("flush", lo, hi, axis): a plate Meshy stood off the surface behind it (along +axis, a unit vector) is laid onto
  that surface: each of its corners moves along the axis until it is FLUSH_LIFT in front of it, so a plate on a
  slanted side slants with it.
- ("drop", lo, hi): the triangles wholly inside go (stray fragments).
- ("island", lo, hi): what hangs together and lies wholly inside goes (a badly made part, beside others that only
  reach into the box).
- ("mirror",): the model's +x half (seen from its front, the right side, with its front along -y) laid over the other,
  about the middle of its bounds, for a Meshy hull that came out lopsided. The texture stays: the mirrored half shows
  the same paint, its emblems mirrored too."""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

# How far a laid plate stands in front of the surface, metres of the Meshy model (about a metre and a half tall).
FLUSH_LIFT = 0.0015

FIXES = {
    # The emblem plate on the hull's side (its -y side) stood a hand's breadth off the slanted side behind it.
    "grunt-2": [("flush", (-0.045, -0.2, -0.012), (0.15, -0.19, 0.2), (0, 1, 0)),
                # The hollow ring where the weapon sits: the rig plan's "arm" brings the original's in its place.
                ("island", (0.01, -0.32, 0.13), (0.19, -0.19, 0.265))],
    "grunt-3": [("mirror",)],
    # Hoses across the joints of the arms, which no rigid part can bend with: they tore as the arms moved.
    "gorilla-5": [("island", lo, hi) for lo, hi in (
        ((-0.355, -0.12, 0.02), (-0.26, -0.03, 0.195)), ((0.26, -0.12, 0.02), (0.35, -0.03, 0.195)),
        ((0.255, 0.1, 0.02), (0.39, 0.22, 0.165)), ((-0.39, 0.1, 0.02), (-0.255, 0.22, 0.165)),
        ((0.325, 0.03, -0.22), (0.415, 0.115, -0.02)), ((-0.415, 0.03, -0.22), (-0.325, 0.115, -0.02)),
        ((-0.415, -0.075, -0.215), (-0.32, 0, -0.015)), ((0.32, -0.075, -0.215), (0.42, 0, -0.015)))],
    "behemoth-4": [("island", (0.04, -0.135, -0.06), (0.26, -0.08, 0.04)), ("island", (0.025, 0.065, 0.07), (0.185, 0.16, 0.135))],
    # A loose shard Meshy left floating behind the legs.
    "knight-5": [("drop", (-0.22, 0.19, -0.39), (-0.08, 0.46, -0.2))],
}


def inside(point, lo, hi):
    return all(lo[k] <= point[k] <= hi[k] for k in range(3))


def flush(bm, lo, hi, axis):
    """Returns the number of the plate's faces and the most a corner moved."""
    axis = Vector(axis).normalized()
    plate = [f for f in bm.faces if all(inside(v.co, lo, hi) for v in f.verts)]
    taken = set(plate)
    rest = [f for f in bm.faces if f not in taken]
    corners, polygons = [], []
    for f in rest:
        polygons.append(tuple(range(len(corners), len(corners) + len(f.verts))))
        corners += [v.co.copy() for v in f.verts]
    tree = BVHTree.FromPolygons(corners, polygons)
    verts = {v for f in plate for v in f.verts}
    # How far each corner lies in front of the plate's back (its thickness for the front's corners): the plate keeps it.
    back = max(v.co.dot(axis) for v in verts)
    shifts = {}
    for v in verts:
        hit = tree.ray_cast(v.co, axis)[0]
        if hit is not None:
            shifts[v] = (hit - v.co).dot(axis) - FLUSH_LIFT
    if not shifts:
        return len(plate), 0.0
    # Corners past the surface's edge move as the plate does on average.
    mean = sum(shifts.values()) / len(shifts)
    for v in verts:
        thickness = back - v.co.dot(axis)
        v.co += axis * (shifts.get(v, mean) - thickness)
    return len(plate), max(shifts.values())


def drop(bm, lo, hi):
    gone = [f for f in bm.faces if all(inside(v.co, lo, hi) for v in f.verts)]
    bmesh.ops.delete(bm, geom=gone, context="FACES")
    return len(gone)


def island(bm, lo, hi):
    """Hangs together by shared positions: Meshy's corners are not always welded."""
    parent = {}

    def find(k):
        while parent.setdefault(k, k) != k:
            parent[k] = parent[parent[k]]
            k = parent[k]
        return k

    def key(v):
        return tuple(round(c, 4) for c in v.co)

    for f in bm.faces:
        first = find(key(f.verts[0]))
        for v in f.verts[1:]:
            parent[find(key(v))] = first
    groups = {}
    for f in bm.faces:
        groups.setdefault(find(key(f.verts[0])), []).append(f)
    gone = [f for g in groups.values() if all(inside(v.co, lo, hi) for f in g for v in f.verts) for f in g]
    bmesh.ops.delete(bm, geom=gone, context="FACES")
    return len(gone)


def mirror(bm):
    """Returns the plane's x."""
    xs = [v.co.x for v in bm.verts]
    middle = (min(xs) + max(xs)) / 2
    bm.transform(Matrix.Translation((-middle, 0, 0)))
    bmesh.ops.bisect_plane(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], plane_co=(0, 0, 0), plane_no=(1, 0, 0),
                           clear_inner=True)
    bmesh.ops.mirror(bm, geom=bm.verts[:] + bm.edges[:] + bm.faces[:], matrix=Matrix.Identity(4), merge_dist=0.0005, axis="X")
    bm.transform(Matrix.Translation((middle, 0, 0)))
    return middle


def fix(meshy, name, steps):
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(meshy, name, "model.glb"))
    ob = next(o for o in bpy.context.scene.objects if o.type == "MESH")
    mesh = ob.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.transform(ob.matrix_world)
    for step in steps:
        if step[0] == "flush":
            count, most = flush(bm, *step[1:])
            print(f"{name}: {count} faces laid flush, moved up to {most:.4f}")
        elif step[0] == "drop":
            print(f"{name}: {drop(bm, *step[1:])} faces dropped")
        elif step[0] == "island":
            print(f"{name}: {island(bm, *step[1:])} faces of an island dropped")
        elif step[0] == "mirror":
            print(f"{name}: mirrored about x = {mirror(bm):.3f}, {len(bm.faces)} faces")
    bm.to_mesh(mesh)
    bm.free()
    ob.matrix_world.identity()
    if any(step[0] == "mirror" for step in steps):
        # The import's own normals are gone with the mirrored half: hard edges by their angle.
        for polygon in mesh.polygons:
            polygon.use_smooth = True
        mesh.set_sharp_from_angle(angle=math.radians(35))
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(meshy, name, "fixed.glb"), export_format="GLB", use_selection=True)


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    meshy, only = argv[0], argv[1:]
    for name, steps in FIXES.items():
        if not only or name in only:
            fix(meshy, name, steps)


if __name__ == "__main__":
    main()
