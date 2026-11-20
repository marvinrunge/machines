"""Makes Meshy's raw prop models game-ready for the story mode (headless Blender 5.2):

    blender -b --factory-startup --python tools/blender/prop_pack.py -- [--views | --sheet] [name ...]

Default: packs every prop of tools/meshy/props-prologue.json that has renders/meshy/props/<name>/model.glb (or only the
named ones) into game/assets/story/props/<name>.glb plus a sidecar <name>.json. Per prop, as tools/meshy/props-fit.json
says:
  - drops tiny loose scraps (islands far smaller than the model, and small islands floating free of everything else);
  - turns the front to -Y in Blender, which is +Z in the GLB (yaw, and "flip" for parts shown upside down);
  - scales to size_m [width, depth, height]: kit modules non-uniformly to their exact tile box, then snaps the vertices
    near the box faces the module meets its neighbours with ("tile") flat onto those faces, so copies tile on the 1 m
    grid; set pieces uniformly to their largest planned dimension;
  - puts the origin at the bottom centre;
  - decimates only when Meshy overshot target_polycount by more than 30 %;
  - repacks the textures to 1K (2K for the big set pieces) as JPEG, the format Meshy sends them in.
The sidecar holds, in the GLB's own frame (metres, y up, the front facing +Z): bounds, height, tris, and the footprint,
the convex hull at floor level (vertices below 0.5 m) grown out to at most 8 points, as [x, z] pairs.

--views renders each raw model.glb from the four sides into renders/meshy/props/<name>/raw-views.png (front -Y first),
to find the yaw. --sheet renders the packed props into renders/meshy/props/sheet-*.png (three views each) and a seam
test of tiled kit modules into sheet-tiling.png."""
import json
import math
import struct
import sys
import zlib
from pathlib import Path

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector
from mathutils.kdtree import KDTree

REPO = Path(__file__).resolve().parents[2]
PLAN = REPO / "tools" / "meshy" / "props-prologue.json"
FIT = REPO / "tools" / "meshy" / "props-fit.json"
RAW = REPO / "renders" / "meshy" / "props"
OUT = REPO / "game" / "assets" / "story" / "props"
FOOT_HEIGHT = 0.5
TEX_BIG = 2048
TEX_SMALL = 1024


# ---------------------------------------------------------------------------------------------------------- scene

def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)


def import_glb(path):
    before = set(bpy.data.objects)
    bpy.ops.import_scene.gltf(filepath=str(path))
    meshes = [o for o in bpy.data.objects if o not in before and o.type == "MESH"]
    for o in [o for o in bpy.data.objects if o not in before and o.type != "MESH"]:
        bpy.data.objects.remove(o, do_unlink=True)
    bpy.ops.object.select_all(action="DESELECT")
    for o in meshes:
        o.select_set(True)
        o.parent = None
    bpy.context.view_layer.objects.active = meshes[0]
    if len(meshes) > 1:
        bpy.ops.object.join()
    ob = bpy.context.view_layer.objects.active
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)
    return ob


def verts_np(me):
    co = np.empty(len(me.vertices) * 3, dtype=np.float64)
    me.vertices.foreach_get("co", co)
    return co.reshape(-1, 3)


def set_verts(me, co):
    me.vertices.foreach_set("co", co.astype(np.float32).ravel())
    me.update()


def tri_count(me):
    me.calc_loop_triangles()
    return len(me.loop_triangles)


# ------------------------------------------------------------------------------------------------------- scraps

def islands(me, co):
    """Connected pieces by position: Meshy splits vertices at UV seams, so vertices at the same spot count as one."""
    size = float(np.linalg.norm(co.max(0) - co.min(0))) or 1.0
    keys = np.round(co / (size * 1e-5)).astype(np.int64)
    _, spot = np.unique(keys, axis=0, return_inverse=True)
    spot = spot.ravel()
    parent = np.arange(spot.max() + 1)

    def find(a):
        while parent[a] != a:
            parent[a] = parent[parent[a]]
            a = parent[a]
        return a

    for poly in me.polygons:
        s = [spot[v] for v in poly.vertices]
        r0 = find(s[0])
        for x in s[1:]:
            r = find(x)
            if r != r0:
                parent[r] = r0
    roots = np.array([find(s) for s in range(len(parent))])
    return roots[spot]  # island id per vertex


def drop_scraps(ob):
    me = ob.data
    co = verts_np(me)
    diag = float(np.linalg.norm(co.max(0) - co.min(0)))
    label = islands(me, co)
    ids, counts = np.unique(label, return_counts=True)
    kd = KDTree(len(co))
    for i, c in enumerate(co):
        kd.insert(c, i)
    kd.balance()
    drop = set()
    for isl in ids:
        idx = np.nonzero(label == isl)[0]
        part = co[idx]
        d = float(np.linalg.norm(part.max(0) - part.min(0)))
        if d < 0.004 * diag:
            drop.add(isl)  # dust
            continue
        if d < 0.05 * diag:
            # Small piece: keep it when it touches something else (bolts, plates), drop it when it floats.
            reach = 0.015 * diag
            touching = False
            for i in idx[:: max(1, len(idx) // 24)]:
                for _, j, _ in kd.find_range(co[i], reach):
                    if label[j] != isl:
                        touching = True
                        break
                if touching:
                    break
            if not touching:
                drop.add(isl)
    if drop:
        bm = bmesh.new()
        bm.from_mesh(me)
        bm.verts.ensure_lookup_table()
        dead = [bm.verts[i] for i in np.nonzero(np.isin(label, list(drop)))[0]]
        bmesh.ops.delete(bm, geom=dead, context="VERTS")
        bm.to_mesh(me)
        bm.free()
        me.update()
    return len(drop), len(ids)


# ------------------------------------------------------------------------------------------------------ fitting

def bounds(co):
    return co.min(0), co.max(0)


def place(ob, matrix):
    """Applies an affine matrix through Blender's own transform apply, which also turns Meshy's custom normals
    correctly under a non-uniform scale (writing vertex positions directly would leave them pointing the old way)."""
    ob.matrix_world = matrix
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.transform_apply(location=True, rotation=True, scale=True)


def fit(ob, prop, cfg):
    me = ob.data
    # Turn: yaw about z (degrees, counter-clockwise from above), then an optional flip upside down about x.
    rot = Matrix.Rotation(math.radians(cfg.get("yaw", 0)), 4, "Z")
    if cfg.get("flip"):
        rot = Matrix.Rotation(math.pi, 4, "X") @ rot
    place(ob, rot)
    co = verts_np(me)
    lo, hi = bounds(co)
    w, d, h = cfg.get("size_m", prop["size_m"])
    info = {}
    if prop["kind"] == "kit" and not cfg.get("uniform"):
        span = np.maximum(hi - lo, 1e-6)
        target = np.array([w, d, h], dtype=np.float64)
        scale = target / span
        place(ob, Matrix.Translation((-w / 2, -d / 2, 0)) @ Matrix.Diagonal((*scale, 1.0)) @
              Matrix.Translation(Vector(-lo)))
        info["scale"] = [round(float(x), 4) for x in scale]
        co = verts_np(me)
        snapped = {}
        box_lo, box_hi = np.array([-w / 2, -d / 2, 0.0]), np.array([w / 2, d / 2, h])
        for face in cfg.get("tile", []):
            axis = "xyz".index(face[1])
            dim = target[axis]
            tol = min(cfg.get("snap_max", 0.12), cfg.get("snap_share", 0.04) * dim)
            plane = box_hi[axis] if face[0] == "+" else box_lo[axis]
            near = np.abs(co[:, axis] - plane) <= tol
            co[near, axis] = plane
            snapped[face] = int(near.sum())
        set_verts(me, co)
        info["snapped"] = snapped
    else:
        span = hi - lo
        s = max(w, d, h) / float(span.max())
        if cfg.get("fit_height"):
            s = cfg["fit_height"] / float(span[2])
        centre = Vector(((lo[0] + hi[0]) / 2, (lo[1] + hi[1]) / 2, lo[2]))
        rise = 0.0
        if cfg.get("lift"):
            rise = cfg["lift"] - span[2] * s if cfg.get("lift_top") else cfg["lift"]
        place(ob, Matrix.Translation((0, 0, rise)) @ Matrix.Scale(s, 4) @ Matrix.Translation(-centre))
        info["scale"] = round(s, 4)
    return info


def clear_boxes(ob, boxes):
    """Deletes the faces whose centre lies inside any of the boxes [x0, y0, z0, x1, y1, z1] (fitted metres, Blender
    axes): Meshy sometimes closes part of an opening that robots must pass through."""
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    dead = []
    for f in bm.faces:
        c = f.calc_center_median()
        if any(b[0] < c.x < b[3] and b[1] < c.y < b[4] and b[2] < c.z < b[5] for b in boxes):
            dead.append(f)
    bmesh.ops.delete(bm, geom=dead, context="FACES")
    bm.to_mesh(ob.data)
    bm.free()
    ob.data.update()
    return len(dead)


def add_ring(ob, ring):
    """A plain dark steel floor ring (the clamp's base, made here instead of by Meshy)."""
    bm = bmesh.new()
    seg = ring.get("segments", 32)
    r_out, r_in, hgt = ring["radius"], ring["radius"] - ring["width"], ring["height"]
    res = bmesh.ops.create_circle(bm, cap_ends=False, segments=seg, radius=r_out)
    outer = res["verts"]
    res = bmesh.ops.create_circle(bm, cap_ends=False, segments=seg, radius=r_in)
    inner = res["verts"]
    bot = [*outer, *inner]
    top_o = [bm.verts.new(v.co + Vector((0, 0, hgt))) for v in outer]
    top_i = [bm.verts.new(v.co + Vector((0, 0, hgt))) for v in inner]
    for i in range(seg):
        j = (i + 1) % seg
        bm.faces.new((outer[i], outer[j], top_o[j], top_o[i]))
        bm.faces.new((top_i[i], top_i[j], inner[j], inner[i]))
        bm.faces.new((top_o[i], top_o[j], top_i[j], top_i[i]))
        bm.faces.new((inner[i], inner[j], outer[j], outer[i]))
    me = bpy.data.meshes.new("ring")
    bm.to_mesh(me)
    bm.free()
    mat = bpy.data.materials.new("ring_steel")
    mat.use_nodes = True
    bsdf = next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (0.06, 0.06, 0.065, 1)
    bsdf.inputs["Metallic"].default_value = 0.6
    bsdf.inputs["Roughness"].default_value = 0.6
    me.materials.append(mat)
    ring_ob = bpy.data.objects.new("ring", me)
    bpy.context.scene.collection.objects.link(ring_ob)
    bpy.ops.object.select_all(action="DESELECT")
    ring_ob.select_set(True)
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.join()
    return bpy.context.view_layer.objects.active


def decimate(ob, target):
    tris = tri_count(ob.data)
    if tris <= target * 1.3:
        return None
    mod = ob.modifiers.new("dec", "DECIMATE")
    mod.ratio = target / tris
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.modifier_apply(modifier=mod.name)
    return tris


def darken_white(img, level=0.8, chroma=0.22):
    """Meshy paints the white backdrop seen through holes (grate gaps, a door's viewing slit) onto the surface it
    closes them with: those near-white, nearly grey texels become a dark void instead."""
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    px = px.reshape(-1, 4)
    rgb = px[:, :3]
    white = (rgb.mean(axis=1) > level) & (rgb.max(axis=1) - rgb.min(axis=1) < chroma)
    rgb[white] = 0.035
    img.pixels.foreach_set(px.ravel())
    img.update()
    return int(white.sum())


def repack_textures(size, scratch, dark_white=False):
    """Scales every image to size and re-saves it (as JPEG, quality 88 for colour and 92 for the data maps) so the exporter embeds the new
    bytes: Blender does not mark a scaled packed image dirty and would otherwise export the old 2K picture."""
    scratch.mkdir(parents=True, exist_ok=True)
    out = {}
    for mat in bpy.data.materials:
        if not mat.use_nodes:
            continue
        for node in mat.node_tree.nodes:
            if node.type != "TEX_IMAGE" or node.image is None:
                continue
            img = node.image
            if img.name in out:
                node.image = out[img.name]
                continue
            colour = img.colorspace_settings.name == "sRGB"
            w, h = img.size
            if max(w, h) > size:
                img.scale(size, size)
            if dark_white and colour:
                darken_white(img, *([] if dark_white is True else dark_white))
            ext, fmt = ".jpg", "JPEG"  # Meshy sends every map as JPEG already: PNG would only grow the file
            path = scratch / f"{len(out)}{ext}"
            img.file_format = fmt
            img.save(filepath=str(path), quality=88 if colour else 92)
            new = bpy.data.images.load(str(path))
            new.colorspace_settings.name = img.colorspace_settings.name
            new.pack()
            new.filepath = ""
            node.image = new
            out[img.name] = new
    return sorted({tuple(i.size) for i in out.values()})


# ------------------------------------------------------------------------------------------------------ sidecar

def hull2d(pts):
    pts = sorted(set(map(tuple, np.round(pts, 4))))
    if len(pts) < 3:
        return pts

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in pts:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(pts):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def line_cross(p1, p2, p3, p4):
    d = (p1[0] - p2[0]) * (p3[1] - p4[1]) - (p1[1] - p2[1]) * (p3[0] - p4[0])
    if abs(d) < 1e-12:
        return None
    a = p1[0] * p2[1] - p1[1] * p2[0]
    b = p3[0] * p4[1] - p3[1] * p4[0]
    return ((a * (p3[0] - p4[0]) - (p1[0] - p2[0]) * b) / d, (a * (p3[1] - p4[1]) - (p1[1] - p2[1]) * b) / d)


def tri_area(a, b, c):
    return abs((b[0] - a[0]) * (c[1] - a[1]) - (c[0] - a[0]) * (b[1] - a[1])) / 2


def grow_to(poly, k):
    """Cuts a convex polygon (counter-clockwise) down to k corners while still containing it: each step replaces one
    edge by extending its two neighbours to where they meet, choosing the edge that adds the least area."""
    poly = list(poly)
    while len(poly) > k:
        n, best = len(poly), None
        for i in range(n):
            a, b = poly[i], poly[(i + 1) % n]
            p, q = poly[i - 1], poly[(i + 2) % n]
            x = line_cross(p, a, b, q)
            if x is None:
                continue
            # The meeting point must lie beyond a (on ray p->a) and beyond b (on ray q->b).
            if (x[0] - a[0]) * (a[0] - p[0]) + (x[1] - a[1]) * (a[1] - p[1]) < 0:
                continue
            if (x[0] - b[0]) * (b[0] - q[0]) + (x[1] - b[1]) * (b[1] - q[1]) < 0:
                continue
            area = tri_area(a, x, b)
            if best is None or area < best[0]:
                best = (area, i, x)
        if best is None:
            # No edge can be grown out (a triangle-ish shape): drop the corner that loses the least area.
            i = min(range(n), key=lambda i: tri_area(poly[i - 1], poly[i], poly[(i + 1) % n]))
            poly.pop(i)
            continue
        _, i, x = best
        a_i, b_i = i, (i + 1) % n
        poly[a_i] = x
        poly.pop(b_i)
    return poly


def sidecar(ob, prop, extra):
    me = ob.data
    co = verts_np(me)
    lo, hi = bounds(co)
    low = co[co[:, 2] <= lo[2] + FOOT_HEIGHT]
    if len(low) < 3:
        low = co
    hull = hull2d(low[:, :2])
    hull = grow_to(hull, 8)
    # Blender (x, y, z) is (x, z, -y) in the GLB: footprint pairs are [x, z_gltf] = [x, -y].
    foot = [[round(float(x), 3), round(float(-y), 3)] for x, y in hull]
    foot.reverse()  # keep it counter-clockwise seen from above in the y-up frame
    return {
        "name": prop["name"], "id": prop["id"], "kind": prop["kind"], "size_m": extra.pop("size_m", prop["size_m"]),
        "frame": "GLB frame: metres, y up, the front faces +Z, origin at the bottom centre",
        "bounds": {"min": [round(float(lo[0]), 3), round(float(lo[2]), 3), round(float(-hi[1]), 3)],
                   "max": [round(float(hi[0]), 3), round(float(hi[2]), 3), round(float(-lo[1]), 3)]},
        "height": round(float(hi[2] - lo[2]), 3),
        "tris": tri_count(me),
        "footprint": foot, "footprint_below_m": FOOT_HEIGHT,
        **extra,
    }


# --------------------------------------------------------------------------------------------------------- pack

def pack(prop, cfg, scratch):
    raw = RAW / prop["name"] / "model.glb"
    reset()
    ob = import_glb(raw)
    tris_raw = tri_count(ob.data)
    dropped, total = drop_scraps(ob)
    info = fit(ob, prop, cfg)
    if cfg.get("clear"):
        info["cleared_faces"] = clear_boxes(ob, cfg["clear"])
    if cfg.get("ring"):
        ob = add_ring(ob, cfg["ring"])
    dec = decimate(ob, prop["target_polycount"] + (cfg.get("ring", {}).get("segments", 32) * 8 if cfg.get("ring") else 0))
    ob.name = prop["name"]
    ob.data.name = prop["name"]
    size = TEX_BIG if cfg.get("tex") == "2k" else TEX_SMALL
    texs = repack_textures(size, scratch / prop["name"], cfg.get("dark_white", False))
    OUT.mkdir(parents=True, exist_ok=True)
    glb = OUT / f"{prop['name']}.glb"
    bpy.ops.export_scene.gltf(filepath=str(glb), export_format="GLB", use_selection=False, export_yup=True,
                              export_apply=True, export_image_format="AUTO", export_cameras=False,
                              export_lights=False, export_animations=False)
    extra = {"source": "Meshy meshy-t2.5 smart-topology, renders/meshy/props/%s/model.glb" % prop["name"],
             "target_polycount": prop["target_polycount"], "tris_raw": tris_raw,
             "decimated_from": dec, "islands": total, "dropped_scraps": dropped,
             "yaw": cfg.get("yaw", 0), "flip": bool(cfg.get("flip")), "fit": info,
             "textures": [list(t) for t in texs], "glb_bytes": glb.stat().st_size,
             "size_m": cfg.get("size_m", prop["size_m"])}
    if cfg.get("note"):
        extra["note"] = cfg["note"]
    car = sidecar(ob, prop, extra)
    (OUT / f"{prop['name']}.json").write_text(json.dumps(car, indent=2) + "\n", "utf-8")
    print(f"{prop['name']}: tris {tris_raw} -> {car['tris']}, dropped {dropped}/{total} islands, "
          f"{car['glb_bytes'] // 1024} KB, tex {texs}", flush=True)


# ------------------------------------------------------------------------------------------------------ renders

def write_png(path, rgb):
    h, w, _ = rgb.shape
    rows = b"".join(b"\x00" + rgb[y].tobytes() for y in range(h))

    def chunk(kind, data):
        return struct.pack(">I", len(data)) + kind + data + struct.pack(">I", zlib.crc32(kind + data) & 0xffffffff)

    path.write_bytes(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", struct.pack(">IIBBBBB", w, h, 8, 2, 0, 0, 0)) +
                     chunk(b"IDAT", zlib.compress(rows, 6)) + chunk(b"IEND", b""))


def read_render(path):
    img = bpy.data.images.load(str(path))
    w, h = img.size
    px = np.empty(w * h * 4, dtype=np.float32)
    img.pixels.foreach_get(px)
    bpy.data.images.remove(img)
    return np.flipud((np.clip(px.reshape(h, w, 4)[:, :, :3], 0, 1) * 255).astype(np.uint8))


def setup_render(res):
    """EEVEE with a grey sky and a sun over the camera's shoulder: quick, and it shows the real PBR maps."""
    sc = bpy.context.scene
    engines = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    sc.render.engine = next(e for e in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE") if e in engines)
    try:
        sc.eevee.taa_render_samples = 16
    except AttributeError:
        pass
    sc.render.resolution_x = sc.render.resolution_y = res
    sc.render.film_transparent = False
    sc.render.image_settings.file_format = "PNG"
    try:
        sc.view_settings.view_transform = "Standard"
    except TypeError:
        pass
    world = sc.world or bpy.data.worlds.new("w")
    sc.world = world
    world.use_nodes = True
    bg = next(n for n in world.node_tree.nodes if n.type == "BACKGROUND")
    bg.inputs[0].default_value = (0.42, 0.43, 0.46, 1)
    bg.inputs[1].default_value = 1.6
    sun = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    sun.data.energy = 3.5
    sun.rotation_euler = (math.radians(50), math.radians(10), math.radians(-35))
    sc.collection.objects.link(sun)
    cam_data = bpy.data.cameras.new("cam")
    cam_data.type = "ORTHO"
    cam = bpy.data.objects.new("cam", cam_data)
    sc.collection.objects.link(cam)
    sc.camera = cam
    return cam


def label(cam, text, ortho):
    """A text label in the picture's lower left corner, carried by the camera."""
    cu = bpy.data.curves.new("label", "FONT")
    cu.body = text
    cu.size = ortho * 0.055
    t = bpy.data.objects.new("label", cu)
    bpy.context.scene.collection.objects.link(t)
    t.parent = cam
    t.location = (-ortho * 0.48, -ortho * 0.48, -cam.data.clip_start - 0.5 * ortho)
    mat = bpy.data.materials.new("label")
    mat.use_nodes = True
    next(n for n in mat.node_tree.nodes if n.type == "BSDF_PRINCIPLED").inputs["Base Color"].default_value = (0, 0, 0, 1)
    cu.materials.append(mat)
    return t


def aim(cam, centre, direction, extent):
    direction = Vector(direction).normalized()
    cam.location = Vector(centre) + direction * extent * 4
    cam.rotation_euler = (-direction).to_track_quat("-Z", "Y").to_euler()
    cam.data.ortho_scale = extent * 1.25
    cam.data.clip_start = 0.01 * extent
    cam.data.clip_end = extent * 10


def shoot(path):
    bpy.context.scene.render.filepath = str(path)
    bpy.ops.render.render(write_still=True)
    return read_render(path)


def mesh_extent(objs):
    pts = np.concatenate([np.array([o.matrix_world @ Vector(c) for c in o.bound_box]) for o in objs])
    lo, hi = pts.min(0), pts.max(0)
    return (lo + hi) / 2, float(np.linalg.norm(hi - lo)), lo, hi


def views(names, scratch):
    for name in names:
        raw = RAW / name / "model.glb"
        if not raw.is_file():
            continue
        reset()
        ob = import_glb(raw)
        cam = setup_render(400)
        centre, ext, _, _ = mesh_extent([ob])
        tiles = []
        for k, d in enumerate([(0, -1, 0.5), (1, 0, 0.5), (0, 1, 0.5), (-1, 0, 0.5), (0.001, -0.3, 1)]):
            aim(cam, centre, d, ext)
            tiles.append(shoot(scratch / f"v{k}.png"))
        write_png(RAW / name / "raw-views.png", np.concatenate(tiles, axis=1))
        print(f"{name}: raw-views.png", flush=True)


def sheet(props, scratch):
    done = [p for p in props if (OUT / f"{p['name']}.glb").is_file()]
    rows = []
    for p in done:
        reset()
        ob = import_glb(OUT / f"{p['name']}.glb")
        cam = setup_render(360)
        centre, ext, lo, hi = mesh_extent([ob])
        car = json.loads((OUT / f"{p['name']}.json").read_text("utf-8"))
        tiles = []
        # Front 3/4 (the front is -Y), back 3/4, straight front.
        for k, d in enumerate([(-0.7, -1, 0.6), (0.8, 1, 0.5), (0, -1, 0.05)]):
            aim(cam, centre, d, ext)
            lab = None
            if k == 0:
                lab = label(cam, f"{p['id']} {p['name']}  {car['tris']} tris", ext * 1.25)
                lab2 = label(cam, "%.2f x %.2f x %.2f m" % tuple(hi - lo), ext * 1.25)
                lab2.location.y += ext * 1.25 * 0.07
            tiles.append(shoot(scratch / f"s{k}.png"))
            if lab:
                bpy.data.objects.remove(lab, do_unlink=True)
                bpy.data.objects.remove(lab2, do_unlink=True)
        rows.append(np.concatenate(tiles, axis=1))
    per = 6
    for n in range(0, len(rows), per):
        chunk = rows[n:n + per]
        # two columns of props per sheet
        pairs = []
        for i in range(0, len(chunk), 2):
            left = chunk[i]
            right = chunk[i + 1] if i + 1 < len(chunk) else np.full_like(left, 200)
            pairs.append(np.concatenate([left, np.full((left.shape[0], 8, 3), 255, np.uint8), right], axis=1))
        img = np.concatenate([np.concatenate([r, np.full((8, r.shape[1], 3), 255, np.uint8)]) for r in pairs])
        out = RAW / f"sheet-{n // per + 1}.png"
        write_png(out, img)
        print(out, flush=True)
    tiling(scratch)


def tiling(scratch):
    """Seam test: a room corner of 3 x 3 floor tiles (a grate in the middle) with a row of tall walls behind it, low
    walls on its left and a pillar in the corner; beside it two ceiling panels and two grate walls in a row, two duct
    segments end to end, and a door frame between walls with its two leaves closed. Writes sheet-tiling.png: an
    overview and three close-ups (floor seams, wall seams, duct joint)."""
    reset()
    placed = []

    def put(name, x, y, z=0.0, yaw=0.0):
        path = OUT / f"{name}.glb"
        if not path.is_file():
            return
        ob = import_glb(path)
        ob.rotation_mode = "XYZ"  # the glTF importer leaves objects in quaternion mode
        ob.rotation_euler = (0, 0, math.radians(yaw))
        ob.location = (x, y, z)
        placed.append(ob)

    # Room corner: floor from x -6..6, y -6..6; walls stand on the floor's level 0 behind its back edge.
    for i in range(3):
        for j in range(3):
            put("kit_floor_grate" if (i, j) == (1, 1) else "kit_floor", (i - 1) * 4, (j - 1) * 4)
    for i in range(3):
        put("kit_wall", (i - 1) * 4, 6.5, 0.0, 0)  # front (-Y) faces the floor
    for j in range(3):
        put("kit_wall_low", -6.5, (j - 1) * 4, 0.0, 90)  # front turned to +X, facing the floor
    put("kit_pillar", -6.5, 6.5, 0.0)
    # Ceiling panels (as hung, seen from below in the overview's second view) and grate walls in a row.
    for i in range(2):
        put("kit_ceiling", 12 + i * 4, -2, 0.0)
        put("kit_grate_wall", 12 + i * 4, 4, 0.0)
    # Two duct segments end to end along y, then a door frame with its two leaves between two walls.
    for j in range(2):
        put("kit_duct", 23, -2 + j * 4, 0.0)
    put("kit_door_frame", 0, -16, 0.0)
    put("kit_door_leaf", -1.75, -16, 0.0)
    put("kit_door_leaf", 1.75, -16, 0.0, 180)
    put("kit_wall", -5.5, -16, 0.0)
    put("kit_wall", 5.5, -16, 0.0)
    cam = setup_render(1400)
    centre, ext, _, _ = mesh_extent(placed)
    imgs = []
    aim(cam, centre, (0.8, -1.2, 0.9), ext * 0.75)
    imgs.append(shoot(scratch / "t0.png"))
    aim(cam, (2, 2, 0.3), (0.2, -0.6, 1), 5)  # floor seams around the grate
    imgs.append(shoot(scratch / "t1.png"))
    aim(cam, (0, 6, 4), (0.15, -1, 0.2), 10)  # the tall wall row from the floor side
    imgs.append(shoot(scratch / "t2.png"))
    aim(cam, (23, 0, 1.5), (1, -0.3, 0.4), 5)  # duct joint
    imgs.append(shoot(scratch / "t3.png"))
    top = np.concatenate(imgs[:2], axis=1)
    bottom = np.concatenate(imgs[2:], axis=1)
    write_png(RAW / "sheet-tiling.png", np.concatenate([top, bottom]))
    print(RAW / "sheet-tiling.png", flush=True)


def main(argv):
    mode, names, scratch = "pack", [], None
    args = iter(argv)
    for arg in args:
        if arg == "--views":
            mode = "views"
        elif arg == "--sheet":
            mode = "sheet"
        elif arg == "--tiling":
            mode = "tiling"
        elif arg == "--scratch":
            scratch = Path(next(args))
        else:
            names.append(arg)
    scratch = scratch or (RAW / "_work")
    scratch.mkdir(parents=True, exist_ok=True)
    plan = json.loads(PLAN.read_text("utf-8"))
    fits = json.loads(FIT.read_text("utf-8")) if FIT.is_file() else {}
    defaults = fits.get("defaults", {})
    props = [p for p in plan["props"] if not names or p["name"] in names]
    if mode == "views":
        views([p["name"] for p in props], scratch)
    elif mode == "sheet":
        sheet(props, scratch)
    elif mode == "tiling":
        tiling(scratch)
    else:
        for p in props:
            if not (RAW / p["name"] / "model.glb").is_file():
                print(f"{p['name']}: no model.glb, skipped", flush=True)
                continue
            cfg = {**defaults.get(p["kind"], {}), **fits.get("props", {}).get(p["name"], {})}
            pack(p, cfg, scratch)


if __name__ == "__main__":
    main(sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else [])
