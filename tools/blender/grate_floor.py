"""Builds the story's floor grate, kit_floor_grate, as real bars one can see through (headless Blender 5.2):

    blender -b --factory-startup --python tools/blender/grate_floor.py -- [--look out.png]

Meshy made the grate as flat slabs with the bars only painted on, so the floor under it never showed. This one is a 4 x 4 m
tile like kit_floor (0.3 m deep, origin at the bottom centre, its top where the machines stand): a frame round the edge,
bearing bars every 12 cm across it, cross bars every 50 cm and three joists under them. It wears kit_floor's own material
(the kit's painted steel, its normal and metal-roughness maps): the bars take a plain grey stretch of that picture and the
frame its dark plate, so it needs no texture of its own. Writes game/assets/story/props/kit_floor_grate.glb and its sidecar
.json, as prop_pack.py does for the Meshy props. --look renders it over a red floor 1.2 m below (how DeckView lays it) to
check the gaps."""
import json
import sys
from pathlib import Path

import bmesh
import bpy
from mathutils import Vector

sys.path.insert(0, str(Path(__file__).resolve().parent))
import prop_pack  # noqa: E402  (Blender's own Python; the helpers live beside this script)

OUT = prop_pack.OUT
SIZE = 4.0
DEPTH = 0.3
FRAME = 0.12          # the frame's width round the edge
BAR_PITCH = 0.12      # bearing bars, centre to centre
BAR_WIDTH = 0.02
BAR_DEPTH = 0.05      # shallow, so the gaps stay open to a camera looking down at a slant
CROSS_PITCH = 0.50    # cross bars, set into the bearing bars' tops
CROSS_WIDTH = 0.02
CROSS_DEPTH = 0.03
JOIST_WIDTH = 0.06    # under the bars, across them
JOISTS = (-1.0, 0.0, 1.0)

# Where on kit_floor's 1024 px picture (x, y from the top-left) each part takes its colour.
STEEL = (560, 470, 760, 540)   # plain grey steel
PLATE = (150, 820, 350, 1000)  # the dark floor plate


def region_uv(region, s, t):
    x0, y0, x1, y1 = region
    return Vector(((x0 + (x1 - x0) * s) / 1024, 1 - (y0 + (y1 - y0) * t) / 1024))


def box(bm, uv, lo, hi, region):
    """A closed box from lo to hi; each face shows the whole region of the picture, stretched to it."""
    lo, hi = Vector(lo), Vector(hi)
    corners = [Vector((hi.x if i & 1 else lo.x, hi.y if i & 2 else lo.y, hi.z if i & 4 else lo.z)) for i in range(8)]
    verts = [bm.verts.new(c) for c in corners]
    quads = [(0, 2, 3, 1), (4, 5, 7, 6), (0, 1, 5, 4), (2, 6, 7, 3), (0, 4, 6, 2), (1, 3, 7, 5)]
    for quad in quads:
        face = bm.faces.new([verts[i] for i in quad])
        for loop, (s, t) in zip(face.loops, ((0, 0), (1, 0), (1, 1), (0, 1))):
            loop[uv].uv = region_uv(region, s, t)


def build(material):
    me = bpy.data.meshes.new("kit_floor_grate")
    bm = bmesh.new()
    uv = bm.loops.layers.uv.new("UVMap")
    h, inner = SIZE / 2, SIZE / 2 - FRAME
    # The frame: two long sides along x, two short ones between them.
    box(bm, uv, (-h, -h, 0), (h, -inner, DEPTH), PLATE)
    box(bm, uv, (-h, inner, 0), (h, h, DEPTH), PLATE)
    box(bm, uv, (-h, -inner, 0), (-inner, inner, DEPTH), PLATE)
    box(bm, uv, (inner, -inner, 0), (h, inner, DEPTH), PLATE)
    # Bearing bars along x, from frame to frame.
    count = int((2 * inner) / BAR_PITCH)
    first = -(count - 1) * BAR_PITCH / 2
    for i in range(count):
        y = first + i * BAR_PITCH
        box(bm, uv, (-inner, y - BAR_WIDTH / 2, DEPTH - BAR_DEPTH), (inner, y + BAR_WIDTH / 2, DEPTH), STEEL)
    # Cross bars along y, flush with the top.
    cross = int((2 * inner) / CROSS_PITCH)
    first = -(cross - 1) * CROSS_PITCH / 2
    for i in range(cross):
        x = first + i * CROSS_PITCH
        box(bm, uv, (x - CROSS_WIDTH / 2, -inner, DEPTH - CROSS_DEPTH), (x + CROSS_WIDTH / 2, inner, DEPTH), STEEL)
    # Joists under the bearing bars, frame to frame.
    for x in JOISTS:
        box(bm, uv, (x - JOIST_WIDTH / 2, -inner, 0), (x + JOIST_WIDTH / 2, inner, DEPTH - BAR_DEPTH), PLATE)
    bm.to_mesh(me)
    bm.free()
    me.materials.append(material)
    ob = bpy.data.objects.new("kit_floor_grate", me)
    bpy.context.scene.collection.objects.link(ob)
    return ob


def look(path, grate):
    """A render from above at a slant over a red floor 1.2 m below, to see the gaps."""
    bpy.ops.mesh.primitive_plane_add(size=8, location=(0, 0, -1.2 + DEPTH))
    red = bpy.data.materials.new("under")
    red.use_nodes = True
    bsdf = next(n for n in red.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.6, 0.05, 0.03, 1)
    bpy.context.object.data.materials.append(red)
    cam = prop_pack.setup_render(900)
    prop_pack.aim(cam, Vector((0, 0, 0.15)), Vector((0.6, -1.0, 1.1)), 3.0)
    prop_pack.shoot(Path(path))


def main(argv):
    shot = argv[argv.index("--look") + 1] if "--look" in argv else None
    old = json.loads((OUT / "kit_floor_grate.json").read_text("utf-8"))
    prop_pack.reset()
    floor = prop_pack.import_glb(OUT / "kit_floor.glb")
    material = floor.data.materials[0]
    material.name = "kit_floor_grate"
    bpy.data.objects.remove(floor, do_unlink=True)
    ob = build(material)
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    glb = OUT / "kit_floor_grate.glb"
    bpy.ops.export_scene.gltf(filepath=str(glb), export_format="GLB", use_selection=True, export_yup=True,
                              export_apply=True, export_image_format="AUTO", export_cameras=False,
                              export_lights=False, export_animations=False)
    prop = {"name": old["name"], "id": old["id"], "kind": old["kind"], "size_m": old["size_m"]}
    images = sorted({tuple(n.image.size) for n in material.node_tree.nodes if n.type == "TEX_IMAGE" and n.image})
    extra = {"source": "Blender, tools/blender/grate_floor.py: bars built to size, kit_floor's material",
             "islands": len(ob.data.polygons) // 6, "textures": [list(t) for t in images], "glb_bytes": glb.stat().st_size,
             "note": "Rebuilt in Blender: Meshy's grate was flat slabs with painted bars (it could not be seen through)."}
    car = prop_pack.sidecar(ob, prop, extra)
    (OUT / "kit_floor_grate.json").write_text(json.dumps(car, indent=2) + "\n", "utf-8")
    print(f"kit_floor_grate: tris {car['tris']}, {car['glb_bytes'] // 1024} KB, bounds {car['bounds']}", flush=True)
    if shot:
        look(shot, ob)


main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
