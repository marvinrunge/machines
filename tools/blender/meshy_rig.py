"""Stage 2 of the model upgrade: the Meshy remakes (renders/meshy/low) fitted into the original rigs, so the
original animations move them unchanged. For each machine export it writes classic/meshy/models/<race>/<id>.json in
the export format, with the Meshy textures in classic/meshy/textures/<meshy model>/.

    blender -b --factory-startup -P tools/blender/meshy_rig.py -- <classic folder> <meshy folder> <plan.json> [id ...]

The plan names per export id the Meshy models of its pieces:
    {"AGGRESSOR_1_GRUNT_R_BOLTER": {"hull": "grunt-1", "weapons": {"r_gun": "grunt-bolter"}, "faceplate": "faceplate-A"}}
Weapons are keyed by their mount (the rig node l_gun, r_gun, gun or t_gun); a name may point into a subfolder
(split/knight-sheet-heavy-bolter-2). Each <meshy folder>/<name>/model.glb is one Meshy model.
A building is one Meshy model, weapons and all: {"FACTORY_1_MILITARY": {"hull": "military-factory-1", "whole": true}}.
Its interior, which the game shows only to first person inside it, keeps the original's: the Meshy shell has none.
"still" names moving parts the Meshy model has no shape for (Mine 5's fans, under a flat panel): their surface goes to
their neighbours and they are left empty, or they would turn pieces of wall.

Each piece goes the same way. The original's parts of that piece (the hull without weapons and eye box, a weapon's
subtree, the eye box) in their rest pose are the target:
- Fit: the Meshy model is laid onto the target by turning it about the vertical (Meshy faces its models either way),
  scaling and moving it, refined by iterative closest points in both directions; the best of four turns, as drawn
  and mirrored, wins. A box fits as well backwards as forwards, so the eye box is not turned by its fit: its front
  goes where the original's eyes look.
- Cut: each Meshy triangle goes to the rig node whose original surface lies nearest; labels are smoothed over
  neighbouring triangles and stray islands join their surroundings.
- Each node's triangles are stored in its own space, so the original's animations (legs, wheels, turning upper
  body, recoil) move them about the original pivots.
The eye box keeps the original's eyes (the black picture with white circles, glowing by its emissive colour), laid
onto the Meshy box's screen, and loses the original's glow sprite.
The rig loses its tread scrolling: the Meshy textures are atlases, scrolling them would smear the whole model."""
import copy
import json
import math
import os
import random
import sys

import bpy
from mathutils import Matrix, Vector, kdtree
from mathutils.bvhtree import BVHTree

MOUNTS = ("l_gun", "r_gun", "gun", "t_gun")
SAMPLES = 9000
FIT_ROUNDS = 30
# The share of correspondences used each round (the worst are dropped: Gemini added hoses the original lacks).
FIT_KEEP = 0.85
SMOOTH_ROUNDS = 3
# Islands smaller than this share of their node's triangles join their neighbours.
ISLAND_SHARE = 0.04
# A part with fewer triangles than this takes those within RESCUE_REACH of the machine's size from its surface and
# at most RESCUE_SLACK times as far from it as from their own part (see rescue); never from a part with fewer than
# RESCUE_KEEP, which needs its own.
RESCUE_MIN = 3
RESCUE_REACH = 0.08
RESCUE_SLACK = 1.5
RESCUE_KEEP = 25
# Meshy built every eye box looking along +Y (Blender's axes after the GLB import), its top up; checked against the
# in-game pictures (renders/meshy/low/compare).
EYE_FRONT, EYE_UP = Vector((0, 1, 0)), Vector((0, 0, 1))
# How far the original's eyes stand in front of the Meshy box's screen, metres: clear of it, and too little to see.
EYE_LIFT = 0.004


def matrix(v):
    x, y, z, p = Vector(v[0:3]), Vector(v[3:6]), Vector(v[6:9]), Vector(v[9:12])
    return Matrix(((x.x, y.x, z.x, p.x), (x.y, y.y, z.y, p.y), (x.z, y.z, z.z, p.z), (0, 0, 0, 1)))


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


def painted_shadow(part):
    """The flat half-clear black plate under flying machines (ClassicAssets leaves it out too)."""
    d = part.get("diffuse", [1, 1, 1, 1])
    zs = part["positions"][2::3]
    return not part.get("texture") and len(d) > 3 and d[3] < 0.99 and sum(d[:3]) < 0.03 and max(zs) - min(zs) < 0.1


def hidden(part):
    """A clear part (the pod's turning helper, a gun mount's marker): nothing to see, so nothing to fit to."""
    d = part.get("diffuse", [1, 1, 1, 1])
    return len(d) > 3 and d[3] < 0.01


def eyes(part):
    """The eye box's eyes: the picture on its front that glows (black, its white circles lit by the emissive colour)."""
    return max(part.get("emissive", [0, 0, 0])) > 0.5 and bool(part.get("texture"))


def facing(parts):
    """The parts' mean normal (their own space), from their stored normals or else their triangles."""
    total = Vector()
    for part in parts:
        n, p = part.get("normals", []), part["positions"]
        if len(n) == len(p):
            total += sum((Vector(n[k:k + 3]) for k in range(0, len(n), 3)), Vector())
        else:
            for t in range(0, len(p) - 8, 9):
                a, b, c = (Vector(p[t + k:t + k + 3]) for k in (0, 3, 6))
                total += (b - a).cross(c - a)
    return total.normalized() if total.length > 1e-9 else None


def eye_turn(front):
    """The turn that lays the Meshy eye box's front onto <front> (model space), its top as near up as that allows."""
    up = Vector((0, 0, 1)) - front * front.z
    up = up.normalized() if up.length > 1e-3 else Vector((1, 0, 0))
    target = Matrix((front, up, front.cross(up))).transposed()
    source = Matrix((EYE_FRONT, EYE_UP, EYE_FRONT.cross(EYE_UP))).transposed()
    return (target @ source.transposed()).to_4x4()


def triangles(part):
    """The part's triangles as corner triples, by its indices."""
    p = part["positions"]
    corners = [Vector(p[3 * i:3 * i + 3]) for i in part.get("indices", range(len(p) // 3))]
    return [corners[k:k + 3] for k in range(0, len(corners) - 2, 3)]


PICTURES = {}


def eye_picture(classic, texture):
    """How bright the eye picture is at (u, v), v from the top, read from the HD textures (classic/hd/<texture>.png);
    None without them."""
    path = os.path.join(classic, "hd", os.path.splitext(texture)[0] + ".png")
    if path not in PICTURES:
        PICTURES[path] = None
        if os.path.exists(path):
            image = bpy.data.images.load(path, check_existing=True)
            width, height = image.size
            pixels = image.pixels[:]

            def bright(u, v):
                # Blender keeps the rows bottom up.
                k = 4 * (min(height - 1, max(0, int((1 - v) * height))) * width + min(width - 1, max(0, int(u * width))))
                return sum(pixels[k:k + 3]) / 3
            PICTURES[path] = bright
    return PICTURES[path]


def onto_screen(eye_parts, meshy_part, classic):
    """The original's eyes moved along their normal to stand EYE_LIFT in front of the Meshy box's screen: the
    Meshy surface facing the same way within the eyes' outline (the middle of it, so a frame round the screen does
    not count). Where the box still stands in front of their white circles (a frame's lip, a bulging screen), they
    come forward to clear it; their black may hide behind it. All in the eye box node's own space. Returns the parts
    and how far they moved."""
    normal = facing(eye_parts)
    if normal is None:
        return eye_parts, 0.0
    side = normal.orthogonal().normalized()
    other = normal.cross(side)
    corners = [Vector(p["positions"][k:k + 3]) for p in eye_parts for k in range(0, len(p["positions"]), 3)]
    depth = sum(c.dot(normal) for c in corners) / len(corners)
    us, vs = [c.dot(side) for c in corners], [c.dot(other) for c in corners]
    (u0, u1), (v0, v1) = (min(us), max(us)), (min(vs), max(vs))
    inset_u, inset_v = (u1 - u0) * 0.1, (v1 - v0) * 0.1
    p, n = meshy_part["positions"], meshy_part["normals"]
    found = []
    for t in range(0, len(p) - 8, 9):
        a, b, c = (Vector(p[t + k:t + k + 3]) for k in (0, 3, 6))
        middle = (a + b + c) / 3
        corner_normals = sum((Vector(n[t + k:t + k + 3]) for k in (0, 3, 6)), Vector())
        if corner_normals.normalized().dot(normal) < 0.7:
            continue
        if u0 + inset_u <= middle.dot(side) <= u1 - inset_u and v0 + inset_v <= middle.dot(other) <= v1 - inset_v:
            found.append((middle.dot(normal), ((b - a).cross(c - a)).length))
    # The area-weighted median: the screen, not a knob on it. Without one the eyes stay where the original had them.
    found.sort()
    half, run, shift = sum(area for _, area in found) / 2, 0.0, 0.0
    for screen, area in found:
        run += area
        if run >= half:
            shift = screen + EYE_LIFT - depth
            break
    # Rays from the front onto points of the white circles (every point without the HD textures): the first Meshy
    # surface they meet must stay behind the eyes.
    box = triangles(meshy_part)
    tree = BVHTree.FromPolygons([c for t in box for c in t], [(k, k + 1, k + 2) for k in range(0, 3 * len(box), 3)])
    steps = 32
    for part in eye_parts:
        bright = eye_picture(classic, part["texture"])
        uvs = part["uvs"]
        uv = [Vector(uvs[2 * i:2 * i + 2]) for i in part.get("indices", range(len(uvs) // 2))]
        for t, (a, b, c) in enumerate(triangles(part)):
            ua, ub, uc = uv[3 * t:3 * t + 3]
            # Edges too: some circles touch the picture's border.
            for i in range(steps + 1):
                for j in range(steps + 1 - i):
                    s, r = i / steps, j / steps
                    if bright and bright(*(ua + s * (ub - ua) + r * (uc - ua))) < 0.5:
                        continue
                    point = a + s * (b - a) + r * (c - a)
                    hit = tree.ray_cast(point + normal * 10, -normal)[0]
                    if hit is not None:
                        shift = max(shift, (hit - point).dot(normal) + EYE_LIFT)
    moved = []
    for part in eye_parts:
        q = part["positions"]
        moved.append(dict(part, positions=[round(q[k] + normal[k % 3] * shift, 5) for k in range(len(q))]))
    return moved, shift


def pieces(rig):
    """The rig's nodes split into the hull, each weapon (a mount and everything under it) and the eye box."""
    nodes = rig["nodes"]
    faceplate = rig.get("faceplate", -1)

    def under(i, root):
        while i >= 0:
            if i == root:
                return True
            i = nodes[i]["parent"] if nodes[i]["parent"] != i else -1
        return False

    mounts = {n["name"]: i for i, n in enumerate(nodes) if n["name"] in MOUNTS}
    weapons = {name: [i for i in range(len(nodes)) if under(i, m)] for name, m in mounts.items()}
    taken = {i for group in weapons.values() for i in group} | {faceplate}
    hull = [i for i in range(len(nodes)) if i not in taken]
    return hull, weapons, [faceplate] if faceplate >= 0 else []


def target_triangles(nodes, world, group):
    """The original surface of a group of nodes in the rest pose: (node, a, b, c) per triangle, model space.
    Spinners (the eye box's flat antennas) count too, as an upright blade across the machine's front: the original
    turned them to face the camera, which mostly looked from ahead."""
    tris = []
    for i in group:
        for part in nodes[i]["parts"]:
            if painted_shadow(part) or hidden(part):
                continue
            p = part["positions"]
            for t in range(0, len(p) - 8, 9):
                corners = [world[i] @ Vector(p[t + k:t + k + 3]) for k in (0, 3, 6)]
                tris.append((i, *corners))
        for spinner in nodes[i].get("spinners", []):
            base, axis = Vector(spinner["base"]), Vector(spinner["axis"]).normalized()
            side = axis.cross(Vector((1, 0, 0)))
            side = side.normalized() if side.length > 1e-3 else Vector((0, 1, 0))
            c = spinner["corners"]
            outline = [world[i] @ (base + axis * c[k] + side * c[k + 1]) for k in range(0, len(c) - 1, 2)]
            tris += [(i, outline[0], outline[k], outline[k + 1]) for k in range(1, len(outline) - 1)]
    return tris


def sample(tris, count):
    """Points spread over the triangles by area, each with its node."""
    areas = [((b - a).cross(c - a)).length / 2 for _, a, b, c in tris]
    total = sum(areas) or 1
    points = []
    rng = random.Random(1)
    for (node, a, b, c), area in zip(tris, areas):
        n = max(1, round(count * area / total))
        for _ in range(n):
            u, v = rng.random(), rng.random()
            if u + v > 1:
                u, v = 1 - u, 1 - v
            points.append((a + (b - a) * u + (c - a) * v, node))
    return points


def load_meshy(path, textures):
    """A Meshy GLB's triangles (positions, normals, uvs per corner, Blender space: z up) and its maps saved as PNG."""
    bpy.ops.wm.read_factory_settings(use_empty=True)
    bpy.ops.import_scene.gltf(filepath=path)
    ob = next(o for o in bpy.context.scene.objects if o.type == "MESH")
    mesh = ob.data
    mesh.calc_loop_triangles()
    normals = mesh.corner_normals
    uv = mesh.uv_layers.active.data
    mw = ob.matrix_world
    turn = mw.to_3x3()
    tris = []
    for lt in mesh.loop_triangles:
        corners = [(mw @ mesh.vertices[mesh.loops[l].vertex_index].co, (turn @ normals[l].vector).normalized(),
                    (uv[l].uv[0], 1 - uv[l].uv[1])) for l in lt.loops]
        tris.append(corners)
    os.makedirs(textures, exist_ok=True)
    maps = {}
    material = ob.active_material
    nodes = material.node_tree.nodes
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")

    def image_into(socket_name):
        link = next((l for l in material.node_tree.links if l.to_node == bsdf and l.to_socket.name == socket_name), None)
        node = link.from_node if link else None
        while node is not None and node.type != "TEX_IMAGE":
            node = next((l.from_node for l in material.node_tree.links if l.to_node == node), None)
        return node.image if node else None

    for key, socket in (("albedo", "Base Color"), ("normal", "Normal"), ("orm", "Roughness")):
        image = image_into(socket)
        if image is None or image.packed_file is None:
            continue
        # The GLB's own bytes (Meshy packs JPEGs), named by what they are.
        data = bytes(image.packed_file.data)
        target = os.path.join(textures, key + (".png" if data[:4] == b"\x89PNG" else ".jpg"))
        with open(target, "wb") as f:
            f.write(data)
        maps[key] = target
    return tris, maps


def fit(source, target_points, mirror, turn=None):
    """Turn about z (four starts, and mirrored ones with <mirror>; or the one <turn> given), scale and move the
    source points onto the target points; returns (score, transform) with the score the mean distance both ways as a
    share of the target's size."""
    target = [p for p, _ in target_points]
    tree = kdtree.KDTree(len(target))
    for k, p in enumerate(target):
        tree.insert(p, k)
    tree.balance()
    lo = Vector([min(p[k] for p in target) for k in range(3)])
    hi = Vector([max(p[k] for p in target) for k in range(3)])
    size = (hi - lo).length
    probe = random.Random(2).sample(target, min(2500, len(target)))
    best = None
    starts = [(turn, "turned to the eyes")] if turn is not None else [
        (Matrix.Rotation(quarter * math.pi / 2, 4, "Z") @ (Matrix.Scale(-1, 4, Vector((0, 1, 0))) if mirrored else Matrix.Identity(4)),
         f"{quarter * 90}°{' mirrored' if mirrored else ''}")
        for mirrored in ((False, True) if mirror else (False,)) for quarter in range(4)]
    for start, how in starts:
        pts = [start @ p for p in source]
        slo = Vector([min(p[k] for p in pts) for k in range(3)])
        shi = Vector([max(p[k] for p in pts) for k in range(3)])
        scale = size / max((shi - slo).length, 1e-6)
        centre_s = Vector(((slo.x + shi.x) / 2, (slo.y + shi.y) / 2, slo.z))
        centre_t = Vector(((lo.x + hi.x) / 2, (lo.y + hi.y) / 2, lo.z))
        current = Matrix.Translation(centre_t) @ Matrix.Scale(scale, 4) @ Matrix.Translation(-centre_s) @ start
        score = 0.0
        for _ in range(FIT_ROUNDS):
            moved = [current @ p for p in source]
            pairs = sorted(((m, tree.find(m)[0]) for m in moved), key=lambda q: (q[0] - q[1]).length)
            pairs = pairs[:int(len(pairs) * FIT_KEEP)]
            back = kdtree.KDTree(len(moved))
            for k, p in enumerate(moved):
                back.insert(p, k)
            back.balance()
            reverse = sorted(((back.find(t)[0], t) for t in probe), key=lambda q: (q[0] - q[1]).length)
            reverse = reverse[:int(len(reverse) * FIT_KEEP)]
            pairs += reverse
            score = sum((a - b).length for a, b in pairs) / len(pairs) / size
            step = similarity_about_z(pairs)
            current = step @ current
        if best is None or score < best[0]:
            best = (score, current, how)
    return best


def similarity_about_z(pairs):
    """The turn about z, uniform scale and move that best lay the first points of the pairs onto the second."""
    n = len(pairs)
    cp = sum((a for a, _ in pairs), Vector()) / n
    cq = sum((b for _, b in pairs), Vector()) / n
    sxx = sxy = norm = 0.0
    for a, b in pairs:
        p, q = a - cp, b - cq
        sxx += p.x * q.x + p.y * q.y
        sxy += p.x * q.y - p.y * q.x
    angle = math.atan2(sxy, sxx)
    turn = Matrix.Rotation(angle, 4, "Z")
    dot = sum((turn @ (a - cp)).dot(b - cq) for a, b in pairs)
    norm = sum((a - cp).length_squared for a, _ in pairs)
    scale = dot / norm if norm > 0 else 1
    return Matrix.Translation(cq) @ Matrix.Scale(scale, 4) @ turn @ Matrix.Translation(-cp)


def cut(tris, target_points):
    """Each triangle's node: the nearest original surface, smoothed over neighbours, islands absorbed."""
    tree = kdtree.KDTree(len(target_points))
    for k, (p, _) in enumerate(target_points):
        tree.insert(p, k)
    tree.balance()
    labels = []
    for corners in tris:
        votes = {}
        for p in [sum((c[0] for c in corners), Vector()) / 3] + [c[0] for c in corners]:
            node = target_points[tree.find(p)[1]][1]
            votes[node] = votes.get(node, 0) + 1
        labels.append(max(votes, key=votes.get))
    # Neighbours: triangles sharing an edge (corners welded by position).
    ids, edges = {}, {}
    for t, corners in enumerate(tris):
        keys = [ids.setdefault(tuple(round(x, 5) for x in c[0]), len(ids)) for c in corners]
        for a, b in ((keys[0], keys[1]), (keys[1], keys[2]), (keys[2], keys[0])):
            edges.setdefault((min(a, b), max(a, b)), []).append(t)
    near = [set() for _ in tris]
    for shared in edges.values():
        for t in shared:
            near[t].update(u for u in shared if u != t)
    for _ in range(SMOOTH_ROUNDS):
        new = []
        for t, label in enumerate(labels):
            votes = {label: 1.5}
            for u in near[t]:
                votes[labels[u]] = votes.get(labels[u], 0) + 1
            new.append(max(votes, key=votes.get))
        labels = new
    totals = {}
    for label in labels:
        totals[label] = totals.get(label, 0) + 1
    seen = [False] * len(tris)
    for t in range(len(tris)):
        if seen[t]:
            continue
        island, stack = [], [t]
        seen[t] = True
        while stack:
            u = stack.pop()
            island.append(u)
            for v in near[u]:
                if not seen[v] and labels[v] == labels[t]:
                    seen[v] = True
                    stack.append(v)
        if len(island) < ISLAND_SHARE * totals[labels[t]]:
            votes = {}
            for u in island:
                for v in near[u]:
                    if labels[v] != labels[t]:
                        votes[labels[v]] = votes.get(labels[v], 0) + 1
            if votes:
                winner = max(votes, key=votes.get)
                for u in island:
                    labels[u] = winner
    return rescue(labels, tris, target_points)


def rescue(labels, tris, target_points):
    """Small moving parts (claws, flaps, a forearm) can lose all their triangles to bigger neighbours, and would
    then not move. A part left with fewer than RESCUE_MIN takes the triangles lying near its surface, nearly as near
    as to their own part's."""
    surfaces = {}
    for p, node in target_points:
        surfaces.setdefault(node, []).append(p)
    trees = {}
    for node, points in surfaces.items():
        tree = kdtree.KDTree(len(points))
        for k, p in enumerate(points):
            tree.insert(p, k)
        tree.balance()
        trees[node] = tree
    every = [p for p, _ in target_points]
    lo = Vector([min(p[k] for p in every) for k in range(3)])
    hi = Vector([max(p[k] for p in every) for k in range(3)])
    size = (hi - lo).length
    counts = {}
    for label in labels:
        counts[label] = counts.get(label, 0) + 1
    for node, tree in trees.items():
        if counts.get(node, 0) >= RESCUE_MIN:
            continue
        for t, corners in enumerate(tris):
            if counts.get(labels[t], 0) < RESCUE_KEEP:
                continue
            middle = sum((c[0] for c in corners), Vector()) / 3
            own = trees[labels[t]].find(middle)[2]
            mine = tree.find(middle)[2]
            if mine < RESCUE_REACH * size and mine < RESCUE_SLACK * own + 0.005 * size:
                labels[t] = node
    return labels


def node_parts(tris, labels, transform, world, maps, classic):
    """The cut triangles as export parts, per node in its own space."""
    relative = {k: os.path.relpath(v, classic).replace("\\", "/") for k, v in maps.items()}
    parts = {}
    normal_turn = transform.to_3x3().normalized()
    for corners, node in zip(tris, labels):
        part = parts.setdefault(node, {"texture": "", "diffuse": [1, 1, 1, 1], "ambient": [1, 1, 1], "emissive": [0, 0, 0],
                                       "positions": [], "normals": [], "uvs": [], "maps": relative})
        local = world[node].inverted()
        local_turn = local.to_3x3().normalized()
        for position, normal, uv in corners:
            part["positions"] += [round(c, 5) for c in local @ (transform @ position)]
            part["normals"] += [round(c, 4) for c in (local_turn @ (normal_turn @ normal)).normalized()]
            part["uvs"] += [round(uv[0], 5), round(uv[1], 5)]
    for part in parts.values():
        part["indices"] = list(range(len(part["positions"]) // 3))
    return parts


def fit_piece(model_name, meshy, classic, nodes, world, group, report, mirror=False, turn=None):
    """Fits one Meshy model onto a group of nodes; returns {node: part}. Only weapons may be mirrored (a left and a
    right weapon are mirror images in the original); a hull or eye box mirrored would carry its emblems on the
    wrong side, and Meshy built it from a picture seen the same way. A <turn> given starts the fit from it alone."""
    target = target_triangles(nodes, world, group)
    if not target:
        report.append(f"  {model_name}: the original has no surface here")
        return {}
    points = sample(target, SAMPLES)
    tris, maps = load_meshy(os.path.join(meshy, model_name, "model.glb"),
                            os.path.join(classic, "meshy", "textures", model_name.replace("/", "-")))
    vertices = list({tuple(round(x, 5) for x in c[0]): c[0] for t in tris for c in t}.values())
    score, transform, how = fit(vertices, points, mirror, turn)
    moved = [[(transform @ c[0], c[1], c[2]) for c in t] for t in tris]
    labels = cut(moved, points)
    counts = {}
    for label in labels:
        counts[label] = counts.get(label, 0) + 1
    empty = [nodes[i]["name"] for i in group
             if any(not painted_shadow(p) and not hidden(p) for p in nodes[i]["parts"]) and i not in counts]
    report.append(f"  {model_name}: fit {score:.3f} ({how}), {len(tris)} triangles to "
                  + ", ".join(f"{nodes[i]['name']} {n}" for i, n in sorted(counts.items()))
                  + (f"; nothing for {', '.join(empty)}" if empty else ""))
    return node_parts(tris, labels, transform, world, maps, classic)


def build(export_id, entry, classic, meshy):
    source = os.path.join(classic, "remake", "models", "red", export_id + ".json")
    model = json.load(open(source))
    rig = model["rig"]
    nodes = rig["nodes"]
    world = world_transforms(nodes)
    hull, weapons, faceplate = pieces(rig)
    if entry.get("whole"):
        hull = [i for i, n in enumerate(nodes) if n["name"] != "interior" and i not in faceplate]
    still = entry.get("still", [])
    report = [export_id]
    new = {}
    new.update(fit_piece(entry["hull"], meshy, classic, nodes, world, [i for i in hull if nodes[i]["name"] not in still], report))
    for mount, name in entry.get("weapons", {}).items():
        new.update(fit_piece(name, meshy, classic, nodes, world, weapons[mount], report, mirror=True))
    if entry.get("faceplate") and faceplate:
        # The eye box looks where the original's eyes look (in the rest pose: some bow their head).
        look = facing([p for p in nodes[faceplate[0]]["parts"] if eyes(p)])
        turn = eye_turn((world[faceplate[0]].to_3x3().normalized() @ look).normalized()) if look else None
        new.update(fit_piece(entry["faceplate"], meshy, classic, nodes, world, faceplate, report, turn=turn))
    # Wheels turn about their node's y axis (the axle): each Meshy wheel is centred on it, or it would wobble, and
    # the rig rolls them by the Meshy wheels' own radius, or they would slip.
    wheels = rig.get("wheels")
    radii = []
    for i in (wheels["left"] + wheels["right"]) if wheels else []:
        if i not in new:
            continue
        p = new[i]["positions"]
        xs, zs = p[0::3], p[2::3]
        cx, cz = (max(xs) + min(xs)) / 2, (max(zs) + min(zs)) / 2
        for k in range(0, len(p), 3):
            p[k] = round(p[k] - cx, 5)
            p[k + 2] = round(p[k + 2] - cz, 5)
        radii.append((max(xs) - min(xs) + max(zs) - min(zs)) / 4)
        report.append(f"  wheel {nodes[i]['name']}: moved {math.hypot(cx, cz):.3f} onto its axle")
    out = copy.deepcopy(model)
    if radii:
        out["rig"]["wheels"]["radius"] = round(sum(radii) / len(radii), 4)
        report.append(f"  wheel radius {wheels['radius']} -> {out['rig']['wheels']['radius']}")
    replaced = set(hull) | {i for m in entry.get("weapons", {}) for i in weapons[m]} | (set(faceplate) if entry.get("faceplate") else set())
    glows = []
    for i in replaced:
        node = out["rig"]["nodes"][i]
        # The shadow plate of flying machines stays. Spinners (flat antennas) are in the Meshy model now.
        kept = [p for p in node["parts"] if painted_shadow(p)]
        if entry.get("faceplate") and i in faceplate and i in new:
            # The original's eyes stay, on the Meshy box's screen; they glow by their emissive colour, so the glow
            # sprite in front of them goes.
            moved, shift = onto_screen([p for p in node["parts"] if eyes(p)], new[i], classic)
            kept += moved
            report.append(f"  eyes: {len(moved)} parts moved {shift:+.3f} onto the screen")
            glows += [(s["texture"], world[i] @ Vector(s["centre"])) for s in node.get("sprites", [])]
            node["sprites"] = []
        node["parts"] = kept + ([new[i]] if i in new else [])
        node["spinners"] = []
    # The model's own sprites (in the rest pose) lose the eye glows too.
    if "sprites" in out:
        out["sprites"] = [s for s in out["sprites"]
                          if not any(s["texture"] == t and (Vector(s["centre"]) - c).length < 0.02 for t, c in glows)]
    out["rig"].pop("tracks", None)
    # The model's own parts (its mesh without the rig): every node's, in the rest pose.
    model_parts = []
    for i, node in enumerate(out["rig"]["nodes"]):
        for part in node["parts"]:
            moved = dict(part)
            p = part["positions"]
            moved["positions"] = [round(c, 5) for t in range(0, len(p), 3) for c in world[i] @ Vector(p[t:t + 3])]
            turn = world[i].to_3x3().normalized()
            n = part.get("normals", [])
            moved["normals"] = [round(c, 4) for t in range(0, len(n), 3) for c in (turn @ Vector(n[t:t + 3])).normalized()]
            model_parts.append(moved)
    out["parts"] = model_parts
    out["spinners"] = []
    out["meshy"] = entry
    target = os.path.join(classic, "meshy", "models", "red", export_id + ".json")
    os.makedirs(os.path.dirname(target), exist_ok=True)
    with open(target, "w") as f:
        json.dump(out, f, separators=(",", ":"))
    return report


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    classic, meshy, plan_path = argv[0], argv[1], argv[2]
    only = set(argv[3:])
    plan = json.load(open(plan_path, encoding="utf-8-sig"))
    for export_id, entry in plan.items():
        if only and export_id not in only:
            continue
        for line in build(export_id, entry, os.path.abspath(classic), os.path.abspath(meshy)):
            print(line, flush=True)


if __name__ == "__main__":
    main()
