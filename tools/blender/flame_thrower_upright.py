"""Grunt 2's flame thrower as the original built it: Meshy laid the gas tank on its side across the hull, the original
stands it upright behind, beside the barrel's end, a hose between them. This takes the Meshy model apart (tank, barrel
with nozzle, hose; the thin wire between tank and barrel goes), stands the tank up where the original has it, lays a
new ribbed hose from the barrel's end into the tank's side and writes it as a Meshy model of its own, which the rig
plan names instead:

    blender -b --factory-startup -P tools/blender/flame_thrower_upright.py -- <meshy folder>

reads <meshy folder>/grunt-flame-thrower-2/model.glb and writes <meshy folder>/grunt-flame-thrower-2-upright/model.glb.
All in the Meshy model's axes (Blender's, z up): the nozzle points along -y, the tank lies on the -x side of the
barrel (towards the hull)."""
import math
import os
import sys

import bmesh
import bpy
from mathutils import Matrix, Vector

SOURCE, TARGET = "grunt-flame-thrower-2", "grunt-flame-thrower-2-upright"
# Where the tank stands, in the barrel's terms (the barrel's axis at x = BARREL_X, z = 0, its back end at
# y = BARREL_BACK): its middle as far back as the barrel's end, as in the original, and its foot as far below the
# barrel (on the hull's deck); 1.15 times the Meshy tank's size. The original's tank stands 1.33 of its units (0.43
# here) towards the hull, but its barrel stands off the hull and the remake's sits on the hull's mount, so the tank
# stands nearer, behind the hull's middle.
BARREL_X, BARREL_BACK = 0.14, 0.2
TANK_MIDDLE = Vector((-0.14, 0.205, -0.126))
TANK_GROWTH = 1.15
HOSE_RADIUS = 0.024
HOSE_RIBS = 22
HOSE_RINGS, HOSE_SIDES = 120, 12


def kind(middle):
    """Which piece a Meshy triangle (its middle) belongs to."""
    if middle.y > 0.215 or (-0.06 < middle.x < 0.1 and middle.y > 0.15):
        return "hose"
    if middle.x < -0.03:
        return "tank"
    # Beside the barrel's thin back half lies the wire's end, a lever towards the tank.
    if middle.x > (0.075 if middle.y > 0 else 0.03):
        return "barrel"
    return "wire"


def bezier(points, t):
    a, b, c, d = points
    s = 1 - t
    return a * s ** 3 + b * 3 * s * s * t + c * 3 * s * t * t + d * t ** 3


def hose(bm, uv_layer, path, rubber_uv):
    """A ribbed tube along a cubic Bezier: its radius swells at each rib, its colour one spot of the hose's rubber."""
    centres = [bezier(path, i / HOSE_RINGS) for i in range(HOSE_RINGS + 1)]
    rings = []
    up = Vector((0, 0, 1))
    for i, centre in enumerate(centres):
        ahead = (centres[min(i + 1, HOSE_RINGS)] - centres[max(i - 1, 0)]).normalized()
        side = ahead.cross(up)
        side = side.normalized() if side.length > 1e-6 else Vector((1, 0, 0))
        lift = side.cross(ahead).normalized()
        swell = 1 + 0.16 * abs(math.sin(math.pi * HOSE_RIBS * i / HOSE_RINGS)) ** 0.5
        ring = []
        for k in range(HOSE_SIDES):
            angle = 2 * math.pi * k / HOSE_SIDES
            ring.append(bm.verts.new(centre + (side * math.cos(angle) + lift * math.sin(angle)) * HOSE_RADIUS * swell))
        rings.append(ring)
    for a, b in zip(rings, rings[1:]):
        for k in range(HOSE_SIDES):
            face = bm.faces.new((a[k], a[(k + 1) % HOSE_SIDES], b[(k + 1) % HOSE_SIDES], b[k]))
            face.smooth = True
            for loop in face.loops:
                loop[uv_layer].uv = rubber_uv


def main():
    meshy = sys.argv[sys.argv.index("--") + 1]
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=os.path.join(meshy, SOURCE, "model.glb"))
    ob = next(o for o in bpy.context.scene.objects if o.type == "MESH")
    mesh = ob.data
    bm = bmesh.new()
    bm.from_mesh(mesh)
    bm.transform(ob.matrix_world)
    uv_layer = bm.loops.layers.uv.active
    kinds = {f: kind(f.calc_center_median()) for f in bm.faces}
    # The rubber's colour: the uv in the middle of the hose's biggest triangle.
    biggest = max((f for f in bm.faces if kinds[f] == "hose"), key=lambda f: f.calc_area())
    rubber_uv = sum((l[uv_layer].uv for l in biggest.loops), Vector((0, 0))) / len(biggest.loops)
    # Every corner belongs to one piece only.
    bmesh.ops.split_edges(bm, edges=[e for e in bm.edges if len({kinds[f] for f in e.link_faces}) > 1])
    bmesh.ops.delete(bm, geom=[f for f in bm.faces if kinds[f] in ("wire", "hose")], context="FACES")
    bm.faces.ensure_lookup_table()
    tank = [f for f in bm.faces if kinds.get(f) == "tank"]
    verts = {v for f in tank for v in f.verts}
    lo = Vector([min(v.co[k] for v in verts) for k in range(3)])
    hi = Vector([max(v.co[k] for v in verts) for k in range(3)])
    middle = (lo + hi) / 2
    # Its long axis (y) up, its front end on top, the filler cap looking back.
    stand = Matrix.Translation(TANK_MIDDLE) @ Matrix.Rotation(-math.pi / 2, 4, "X") @ Matrix.Scale(TANK_GROWTH, 4) @ Matrix.Translation(-middle)
    for v in verts:
        v.co = stand @ v.co
    radius = (hi.x - lo.x) / 2 * TANK_GROWTH
    # The hose: out of the barrel's back end, round behind, into the tank's side that faces the barrel.
    start = Vector((BARREL_X, BARREL_BACK - 0.01, 0.01))
    towards = Vector((math.cos(math.radians(35)), math.sin(math.radians(35)), 0))
    end = Vector((TANK_MIDDLE.x, TANK_MIDDLE.y, 0.02)) + towards * (radius * 0.85)
    path = [start, start + Vector((0, 0.16, 0.03)), end + towards * 0.14 + Vector((0, 0, 0.03)), end]
    hose(bm, uv_layer, path, rubber_uv)
    bm.to_mesh(mesh)
    bm.free()
    ob.matrix_world = Matrix.Identity(4)
    for polygon in mesh.polygons:
        polygon.use_smooth = True
    # The import's own normals would not turn with the tank: shaded anew, hard edges kept.
    if mesh.has_custom_normals:
        bpy.context.view_layer.objects.active = ob
        ob.select_set(True)
        bpy.ops.mesh.customdata_custom_splitnormals_clear()
    mesh.set_sharp_from_angle(angle=math.radians(35))
    ob.select_set(True)
    folder = os.path.join(meshy, TARGET)
    os.makedirs(folder, exist_ok=True)
    bpy.ops.export_scene.gltf(filepath=os.path.join(folder, "model.glb"), export_format="GLB", use_selection=True)
    print("written", os.path.join(folder, "model.glb"))


if __name__ == "__main__":
    main()
