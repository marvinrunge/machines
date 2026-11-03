"""Stage 2 of the model upgrade: the Meshy remakes (renders/meshy/low) fitted into the original rigs, so the
original animations move them unchanged. For each machine export it writes classic/meshy/models/<race>/<id>.json in
the export format, with the Meshy textures in classic/meshy/textures/<meshy model>/.

    blender -b --factory-startup -P tools/blender/meshy_rig.py -- <classic folder> <meshy folder> <plan.json> [id ...]

The plan names per export id the Meshy models of its pieces:
    {"AGGRESSOR_1_GRUNT_R_BOLTER": {"hull": "grunt-1", "weapons": {"r_gun": "grunt-bolter"}, "faceplate": "faceplate-A"}}
Weapons are keyed by their mount (the rig node l_gun, r_gun, gun or t_gun); a name may point into a subfolder
(split/knight-sheet-heavy-bolter-2). Each <meshy folder>/<name>/model.glb is one Meshy model; a fixed.glb beside it
(tools/blender/meshy_fixes.py) is used in its place.
A building is one Meshy model, weapons and all: {"FACTORY_1_MILITARY": {"hull": "military-factory-1", "whole": true}}.
Its interior, which the game shows only to first person inside it, keeps the original's: the Meshy shell has none.
"lower" names a Meshy model whose lower body (all of the hull that the upper body does not carry) replaces the hull
model's, so machines of a kind share their legs: {"hull": "reaper-2", "lower": "reaper-3", ...}. Both are fit to the
whole hull; each keeps the triangles cut to its own nodes.
"neck": true lays two ribbed hoses side by side from a walker's platform (its hip) up into its upper body, round the
axis the upper body turns about, in place of what Meshy built there (the Reapers' odd blocks): {"neck": true}.
"still" names moving parts the Meshy model has no shape for (Mine 5's fans, under a flat panel): their surface goes to
their neighbours and they are left empty, or they would turn pieces of wall.

Wheels always stay the Stage-1 model's, and "upgraded" names other parts that do (the Behemoth's claws).

What a plan may add for a machine Meshy served badly, each explained where it is done: "forged" and "mount" (forge, a
weapon as the original built it in worn metal, with real holes and a pedestal), "vortex" (vortex_weapon), "plasma" (a
ball drawn by the game's shader in place of the gun's sprite), "rotors" (fit_rotors), "split" (split_joint), "lids"
(lay_lids), "mast" (raise_mast), "socket" (clear_socket), "slivers" (drop_slivers), "arm" (carrying_arm), "shape" and
"borrow" (a hull fitted to another export, a piece of another Meshy model) and "covers" (belts, by
tools/blender/cover_texture.py). tools/blender/weapon_metal.py makes the forged maps and goes first.

Each piece goes the same way. The original's parts of that piece (the hull without weapons and eye box, a weapon's
subtree, the eye box) in their rest pose are the target:
- Fit: the original's axes are left-handed (the game reads them as x, z, y) and Blender's right-handed, so the Meshy
  model is first mirrored across y; laid in by turns alone, it would show mirrored in the game (fins and emblems on
  the wrong side, a pumpjack's head at the wrong end). It is then laid onto the target by turning it about the
  vertical (Meshy faces its models either way), scaling and moving it, refined by iterative closest points in both
  directions. A box fits as well backwards as forwards, so the turn is not left to the fit where it is known:
  <meshy folder>/fronts.json names the way each Meshy model faces (found against the original's pictures), and that
  front goes along the original's +X, as drawn or (weapons) mirrored. Where the original marks both sides of a weapon
  and Meshy only one, fronts.json's "outward" names that side, and the weapon is laid (as drawn or mirrored) so it
  faces away from the machine. The eye box's front goes where the original's eyes look. A model without a known
  front takes the best of four turns, as drawn and mirrored.
- Cut: each Meshy triangle goes to the rig node whose original surface lies nearest; labels are smoothed over
  neighbouring triangles and stray islands join their surroundings.
- Each node's triangles are stored in its own space, so the original's animations (legs, wheels, turning upper
  body, recoil) move them about the original pivots.
The eye box keeps the original's eyes (the black picture with white circles, glowing by its emissive colour), laid
onto the Meshy box's screen, and loses the original's glow sprite.
Tracks: the Meshy textures are atlases, so scrolling them would smear the whole model. Each track's Meshy tread (the
triangles of the track and its side plate that lie round the track's outline in side view) is replaced by a belt of
its own: a band along that outline with the tread links' tile (tools/blender/tread_texture.py, classic/meshy/
textures/tread), a whole number of tiles long, which the rig scrolls as the original's did. What lay inside the loop
(the wheels Meshy painted on, which cannot turn) goes; a flat cover with the side plate's paint closes the loop, in
the side plate's node, so it does not scroll."""
import colorsys
import copy
import json
import math
import os
import random
import sys

import bpy
from mathutils import Matrix, Vector, kdtree
from mathutils.bvhtree import BVHTree

sys.path.insert(0, os.path.dirname(__file__))
import cover_texture

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
# Blender's right-handed axes into the original's left-handed ones.
HANDED = Matrix.Scale(-1, 4, Vector((0, 1, 0)))
# How far the original's eyes stand in front of the Meshy box's screen, metres: clear of it, and too little to see.
EYE_LIFT = 0.004
# The eye box's front stands this share of the eyes' picture beyond it all round (the fit, pulled by the antennas,
# left most boxes a little small for their eyes); its antennas, flat in the Meshy models, get this thickness (a
# share of the box's height).
EYE_MARGIN = 0.03
ANTENNA_THICKNESS = 0.06
# A weapon that stands off its mount (Meshy's weapons and hulls were made apart and fit their originals each on its
# own) moves onto it: towards the hull until it is this share of the machine's size into it. Gaps smaller than
# GAP_MIN of the size stay; a weapon moves at most GAP_MAX of its own size.
GAP_SINK = 0.004
GAP_MIN = 0.004
GAP_MAX = 0.4
LID_SINK = 0.02
# How much wider than a fan's blades fit_rotors clears the housing.
ROTOR_ROOM = 1.12
# How much room clear_socket makes round the eyes, and how far back into the machine.
SOCKET_MARGIN = 0.03
SOCKET_DEPTH = 0.45
# Even stretches of the forged steel maps (tools/blender/weapon_metal.py), for what shows one colour.
STEEL_SPOT = (0.432, 0.689)
DARK_SPOT = (0.912, 0.087)
# An antenna's mast (raise_mast), (share of the way, radius).
MAST_PROFILE = [(0, 0.17), (0.05, 0.17), (0.07, 0.095), (0.55, 0.07), (0.56, 0.095), (0.6, 0.095), (0.61, 0.055), (0.96, 0.03),
                (0.97, 0.055), (1, 0.055), (1, 0)]
# The Vortex's hole and the shell of light round it, in metres from its middle.
VORTEX_HOLE = 0.82
VORTEX_SHELL = 1.12
# A launcher's tubes (tools meshy_rig.real_holes): sides, radius as a share of its cell, depth as a share of the cell.
HOLE_SIDES = 12
HOLE_RADIUS = 0.29
HOLE_DEPTH = 0.9
# The belts round the tracks, as shares of the track's height: how thick, how far outside the Meshy tread (to cover
# it), and about how long a link is. The tile holds TREAD_LINKS links (tread_texture.LINKS).
BELT_THICKNESS = 0.08
BELT_MARGIN = 0.012
BELT_PITCH = 0.16
TREAD_LINKS = 4
TREAD_MAPS = {key: f"meshy/textures/tread/{key}.png" for key in ("albedo", "normal", "orm")}


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
    """The Meshy eye box into the original's axes (HANDED), its front turned onto <front> (model space) and its top as
    near up as that allows."""
    up = Vector((0, 0, 1)) - front * front.z
    up = up.normalized() if up.length > 1e-3 else Vector((1, 0, 0))
    target = Matrix((front, up, front.cross(up))).transposed()
    eye_front = HANDED.to_3x3() @ EYE_FRONT
    source = Matrix((eye_front, EYE_UP, eye_front.cross(EYE_UP))).transposed()
    return (target @ source.transposed()).to_4x4() @ HANDED


def triangles(part):
    """The part's triangles as corner triples, by its indices."""
    p = part["positions"]
    corners = [Vector(p[3 * i:3 * i + 3]) for i in part.get("indices", range(len(p) // 3))]
    return [corners[k:k + 3] for k in range(0, len(corners) - 2, 3)]


PICTURES = {}
# The way each Meshy model faces, by its name in the plan (<meshy folder>/fronts.json), and the side of some that
# goes outward (the same azimuths), those whose front the fit keeps exactly ("held") and those made wider across
# after the fit ("widen", by how much).
FRONTS = {}
OUTWARD = {}
HELD = set()
WIDEN = {}


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


def corner_list(part):
    """The part's triangles as corner triples of (position, normal, uv)."""
    p, n, uv = part["positions"], part["normals"], part["uvs"]
    order = part.get("indices", range(len(p) // 3))
    corners = [(Vector(p[3 * i:3 * i + 3]), Vector(n[3 * i:3 * i + 3]), Vector(uv[2 * i:2 * i + 2])) for i in order]
    return [corners[k:k + 3] for k in range(0, len(corners) - 2, 3)]


def from_corners(part, tris):
    """The part with <tris> (corner triples) as its triangles."""
    out = dict(part, positions=[], normals=[], uvs=[])
    for t in tris:
        store(t, Matrix.Identity(4), out)
    out["indices"] = list(range(len(out["positions"]) // 3))
    return out


def grow_eye_box(part, eye_parts, up):
    """The Meshy eye box (its node's space) scaled and moved until its front covers the original's eyes with
    EYE_MARGIN to spare (across and up apart), and its antennas (all above the box's lid) made solid blades. <up> is
    the machine's up in the node's space. Returns the part, the scales (across, up, deep) and the number of antennas."""
    normal = facing(eye_parts)
    if normal is None:
        return part, (1.0, 1.0, 1.0), 0
    up = (up - normal * up.dot(normal)).normalized()
    side = normal.cross(up)
    tris = corner_list(part)

    def area(t):
        return ((t[1][0] - t[0][0]).cross(t[2][0] - t[0][0])).length / 2

    # The lid: the up-facing surface, by area (the antennas' edges facing up are slivers).
    # (By the stored normals: Meshy's windings go either way.)
    lids = sorted((sum(c[0].dot(up) for c in t) / 3, area(t)) for t in tris
                  if sum((c[1] for c in t), Vector()).normalized().dot(up) > 0.8)
    if not lids:
        return part, (1.0, 1.0, 1.0), 0
    half, run, top = sum(a for _, a in lids) / 2, 0.0, lids[-1][0]
    for level, a in lids:
        run += a
        if run >= half:
            top = level
            break
    bottom = min(c[0].dot(up) for t in tris for c in t)
    reach = 0.03 * (top - bottom)
    body = [t for t in tris if sum(c[0].dot(up) for c in t) / 3 <= top + reach]
    blades = [t for t in tris if sum(c[0].dot(up) for c in t) / 3 > top + reach]

    def extent(points, axis):
        values = [q.dot(axis) for q in points]
        return min(values), max(values)

    eye_points = [Vector(e["positions"][k:k + 3]) for e in eye_parts for k in range(0, len(e["positions"]), 3)]
    body_points = [c[0] for t in body for c in t]
    # Across and up each its own scale (the original's box is as wide and high as its eyes' picture, which Meshy's
    # was not always), its middle moved onto the eyes'; its depth scaled as the smaller.
    middle, onto, scales = Vector(), Vector(), []
    for axis in (side, up, normal):
        b0, b1 = extent(body_points, axis)
        middle += axis * (b0 + b1) / 2
        if axis is normal:
            onto += axis * (b0 + b1) / 2
            continue
        e0, e1 = extent(eye_points, axis)
        scale = min(1.6, max(1.0, (e1 - e0) * (1 + 2 * EYE_MARGIN) / max(b1 - b0, 1e-6)))
        scales.append(scale)
        # Only as far as the box must grow: one already large enough stays where the fit put it.
        onto += axis * ((b0 + b1) / 2 + ((e0 + e1) / 2 - (b0 + b1) / 2) * min(1.0, (scale - 1) * 10))
    scales.append(min(scales))
    axes = list(zip((side, up, normal), scales))

    def grown(t):
        out = []
        for q, n, uv in t:
            d = q - middle
            bent = sum((axis * (n.dot(axis) / k) for axis, k in axes), Vector())
            out.append((onto + sum((axis * (d.dot(axis) * k) for axis, k in axes), Vector()), bent.normalized(), uv))
        return out

    body, blades = [grown(t) for t in body], [grown(t) for t in blades]
    thickness = ANTENNA_THICKNESS * (top - bottom) * scales[1]
    solid, count = thicken(blades, thickness)
    return from_corners(part, body + solid), scales, count


def ribbed_tube(path, radius, ribs, uv, rings=80, sides=14, taper=1):
    """A tube along <path> (Bezier corners) whose radius swells at each of <ribs> ribs, as corner triples; its colour
    one spot of the texture (<uv>, v from the top). Its end is <taper> times as thick as its start."""
    def at(t):
        a, b, c, d = path
        s = 1 - t
        return a * s ** 3 + b * 3 * s * s * t + c * 3 * s * t * t + d * t ** 3

    centres = [at(i / rings) for i in range(rings + 1)]
    loops = []
    for i, centre in enumerate(centres):
        ahead = (centres[min(i + 1, rings)] - centres[max(i - 1, 0)]).normalized()
        side = ahead.cross(Vector((1, 0, 0)) if abs(ahead.x) < 0.9 else Vector((0, 1, 0))).normalized()
        lift = side.cross(ahead).normalized()
        swell = (1 + 0.14 * abs(math.sin(math.pi * ribs * i / rings)) ** 0.5) * (1 + (taper - 1) * i / rings)
        loops.append([(centre + (side * math.cos(a) + lift * math.sin(a)) * radius * swell,
                       (side * math.cos(a) + lift * math.sin(a)).normalized())
                      for a in (2 * math.pi * k / sides for k in range(sides))])
    tris = []
    for a, b in zip(loops, loops[1:]):
        for k in range(sides):
            quad = [a[k], a[(k + 1) % sides], b[(k + 1) % sides], b[k]]
            corners = [(q, n, uv) for q, n in quad]
            tris += [corners[:3], [corners[0], corners[2], corners[3]]]
    return tris


def turned(foot, top, profile, uv, sides=16):
    """A body turned about the line from <foot> to <top>, as corner triples: <profile> lists (share of the way, radius)
    from the foot up. <uv> is a spot of the texture or says it from a corner's radius and angle."""
    axis = (top - foot).normalized()
    side = axis.cross(Vector((1, 0, 0)) if abs(axis.x) < 0.9 else Vector((0, 1, 0))).normalized()
    lift = side.cross(axis).normalized()
    tris = []
    for (s0, r0), (s1, r1) in zip(profile, profile[1:]):
        p0, p1 = foot + (top - foot) * s0, foot + (top - foot) * s1
        rise, slope = (p1 - p0).length, r1 - r0
        for k in range(sides):
            a0, a1 = 2 * math.pi * k / sides, 2 * math.pi * (k + 1) / sides

            def corner(p, r, a):
                out = side * math.cos(a) + lift * math.sin(a)
                return (p + out * r, (out * rise - axis * slope).normalized(), uv(r, a) if callable(uv) else Vector(uv))

            quad = [corner(p0, r0, a0), corner(p0, r0, a1), corner(p1, r1, a1), corner(p1, r1, a0)]
            tris += [t for t in ([quad[0], quad[1], quad[2]], [quad[0], quad[2], quad[3]])
                     if (t[1][0] - t[0][0]).cross(t[2][0] - t[0][0]).length > 1e-9]
    return tris


def raise_mast(new, nodes, mast, extras, report):
    """The plan's "mast": an antenna Meshy made a flat strip (its triangles wholly in the box "lo" to "hi") becomes a
    round steel mast from "foot" to "top" in that node."""
    i = next(k for k, n in enumerate(nodes) if n["name"] == mast["node"])
    lo, hi = Vector(mast["lo"]), Vector(mast["hi"])

    def inside(q):
        return all(lo[k] <= q[k] <= hi[k] for k in range(3))

    tris = corner_list(new[i])
    kept = [t for t in tris if not all(inside(c[0]) for c in t)]
    new[i] = from_corners(new[i], kept)
    rod = turned(Vector(mast["foot"]), Vector(mast["top"]), MAST_PROFILE, STEEL_SPOT, sides=10)
    extras.setdefault(i, []).append(from_corners(meshy_part(forge_maps("steel")), rod))
    report.append(f"  mast on {mast['node']} for {len(tris) - len(kept)} triangles of a flat strip")


def seal(part, uv):
    """Closes the big openings of a part (its underside lost to the cut, so the ground shows through it): each loop of
    open edges as wide as a third of the part gets a fan to its middle, showing the texture at <uv>. Returns the part
    and how many it closed."""
    tris = corner_list(part)

    def key(q):
        return tuple(round(x, 3) for x in q)

    uses, spot = {}, {}
    for t in tris:
        for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
            ka, kb = key(a[0]), key(b[0])
            spot[ka], spot[kb] = a[0], b[0]
            uses[frozenset((ka, kb))] = uses.get(frozenset((ka, kb)), 0) + 1
    links = {}
    for edge, count in uses.items():
        if count == 1 and len(edge) == 2:
            a, b = tuple(edge)
            links.setdefault(a, []).append(b)
            links.setdefault(b, []).append(a)
    everything = [c[0] for t in tris for c in t]
    centre = sum(everything, Vector()) / len(everything)
    width = max(max(q[k] for q in everything) - min(q[k] for q in everything) for k in range(3))
    seen, added, closed = set(), [], 0
    for start in links:
        if start in seen:
            continue
        loop, at = [start], start
        seen.add(start)
        while True:
            ahead = [k for k in links[at] if k not in seen]
            if not ahead:
                break
            at = ahead[0]
            seen.add(at)
            loop.append(at)
        points = [spot[k] for k in loop]
        if len(points) < 3 or max(max(q[k] for q in points) - min(q[k] for q in points) for k in range(3)) < width / 3:
            continue
        closed += 1
        middle = sum(points, Vector()) / len(points)
        for a, b in zip(points, points[1:] + points[:1]):
            normal = (a - middle).cross(b - middle)
            if normal.length < 1e-9:
                continue
            normal = normal.normalized()
            if normal.dot(middle - centre) < 0:
                normal = -normal
            added.append([(middle, normal, uv), (a, normal, uv), (b, normal, uv)])
    return from_corners(part, tris + added), closed


def neck_hoses(new, nodes, world, rig, report):
    """Two ribbed hoses side by side from the hip's platform up into the upper body (see the module's notes), in the
    hip's node, so they stay as the upper body turns above them; what Meshy built between the two goes."""
    hip = next((i for i, n in enumerate(nodes) if n["name"] == "hip"), None)
    body = rig.get("upperBody")
    if hip not in new or body not in new:
        return
    into = world[hip].inverted()
    tris = corner_list(new[hip])
    width = max(c[0].y for t in tris for c in t) - min(c[0].y for t in tris for c in t)
    axis = into @ world[body].translation
    # The upper body's underside near its axis, in the hip's space.
    underside = [(into @ world[body]) @ c[0] for t in corner_list(new[body]) for c in t]
    near = [q.z for q in underside if math.hypot(q.x - axis.x, q.y - axis.y) < 0.25 * width]
    if not near:
        return
    bottom = min(near)

    def middle(t):
        return sum((c[0] for c in t), Vector()) / 3

    def area(t):
        return ((t[1][0] - t[0][0]).cross(t[2][0] - t[0][0])).length / 2

    # The platform's top: the area-weighted median height of the hip's surface that faces up, below the upper body.
    tops = sorted((middle(t).z, area(t)) for t in tris
                  if sum((c[1] for c in t), Vector()).normalized().z > 0.8 and middle(t).z < bottom)
    if not tops:
        return
    half, run, top = sum(a for _, a in tops) / 2, 0.0, tops[-1][0]
    for level, a in tops:
        run += a
        if run >= half:
            top = level
            break
    kept = [t for t in tris if middle(t).z <= top + 0.15 * (bottom - top)]
    # The platform's own dark metal colours the hoses (their ribs make them hoses).
    plate = max((t for t in kept if sum((c[1] for c in t), Vector()).normalized().z > 0.8), key=area)
    uv = sum((c[2] for c in plate), Vector((0, 0))) / 3
    # Side by side, nearly touching; none where the upper body sits on the platform (Reaper 2).
    radius = 0.09 * width
    apart = 1.15 * radius
    hoses = []
    for side in (-1, 1) if bottom - top > 0.1 * width else ():
        foot = Vector((axis.x, axis.y + side * apart, top - 0.02 * width))
        head = Vector((axis.x, axis.y + side * apart, bottom + 0.03 * width))
        bow = Vector((0, side * 0.03 * width, 0))
        rise = (head - foot) / 3
        hoses += ribbed_tube([foot, foot + rise + bow, head - rise + bow, head], radius, 7, uv)
    gone = len(tris) - len(kept)
    new[hip] = from_corners(new[hip], kept + hoses)
    new[body], closed = seal(new[body], uv)
    if closed:
        report.append(f"  {closed} openings under the upper body closed")
    # The waist between them is Meshy's too.
    waist = next((i for i, n in enumerate(nodes) if n["name"] == "waist"), None)
    if waist in new:
        gone += len(new.pop(waist)["positions"]) // 9
    report.append(f"  neck: {gone} triangles above the platform gone, two hoses {2 * radius:.2f} thick from {top:.2f} "
                  f"to {bottom:.2f}")


def thicken(tris, thickness):
    """Flat sheets (corner triples) made blades <thickness> thick: each sheet (triangles sharing corners) twice, half
    the thickness either way along its normal, and walls along its open edges. Returns the triangles and the number
    of sheets."""
    def key(q):
        return tuple(round(x, 4) for x in q)

    # Meshy's sheets are often two-sided: one copy each.
    unique, seen = [], set()
    for t in tris:
        k = frozenset(key(c[0]) for c in t)
        if len(k) == 3 and k not in seen:
            seen.add(k)
            unique.append(t)
    parent = {}

    def find(k):
        while parent.setdefault(k, k) != k:
            parent[k] = parent[parent[k]]
            k = parent[k]
        return k

    for t in unique:
        a, b, c = (find(key(x[0])) for x in t)
        parent[b], parent[c] = a, a
    sheets = {}
    for t in unique:
        sheets.setdefault(find(key(t[0][0])), []).append(t)
    out = []
    for sheet in sheets.values():
        first = None
        total = Vector()
        for t in sheet:
            n = (t[1][0] - t[0][0]).cross(t[2][0] - t[0][0])
            if first is None and n.length > 1e-12:
                first = n
            total += n if first is None or n.dot(first) >= 0 else -n
        if total.length < 1e-12:
            out += sheet
            continue
        n = total.normalized()
        offset = n * thickness / 2
        edges = {}
        middle = sum((c[0] for t in sheet for c in t), Vector()) / (3 * len(sheet))
        for t in sheet:
            if ((t[1][0] - t[0][0]).cross(t[2][0] - t[0][0])).dot(n) < 0:
                t = [t[0], t[2], t[1]]
            out.append([(q + offset, n, uv) for q, _, uv in t])
            out.append([(q - offset, -n, uv) for q, _, uv in (t[0], t[2], t[1])])
            for a, b in ((t[0], t[1]), (t[1], t[2]), (t[2], t[0])):
                edges.setdefault(frozenset((key(a[0]), key(b[0]))), []).append((a, b))
        for pairs in edges.values():
            if len(pairs) != 1:
                continue
            (qa, _, ua), (qb, _, ub) = pairs[0]
            wall = (qb - qa).cross(n).normalized()
            if wall.dot((qa + qb) / 2 - middle) < 0:
                wall = -wall
            corners = [(qa + offset, wall, ua), (qb + offset, wall, ub), (qb - offset, wall, ub), (qa - offset, wall, ua)]
            out += [corners[:3], [corners[0], corners[2], corners[3]]]
    return out, len(sheets)


def below_waist(new, nodes, world, rig, report):
    """Hands what Meshy built below the upper body's turning ring (the hoses from the waist down to the turbines, which
    the cut gave the head) to the parts it hangs from, or it would swing round with the upper body and tear off them:
    every triangle of the upper body lower than the original's upper body goes to the nearest of the nodes beside
    it and the one carrying it."""
    top = rig["upperBody"]
    if top not in new:
        return
    level = min((world[top] @ c).z for part in nodes[top]["parts"] if not painted_shadow(part) and not hidden(part)
                for t in triangles(part) for c in t)
    parent = nodes[top]["parent"]
    beside = [i for i in new if i != top and (i == parent or nodes[i]["parent"] == parent)]
    trees = {}
    for i in beside:
        corners = [world[i] @ c[0] for t in corner_list(new[i]) for c in t]
        trees[i] = BVHTree.FromPolygons(corners, [(k, k + 1, k + 2) for k in range(0, len(corners), 3)])
    stay, handed = [], {i: corner_list(new[i]) for i in beside}
    count = 0
    for t in corner_list(new[top]):
        middle = world[top] @ (sum((c[0] for c in t), Vector()) / 3)
        if middle.z >= level or not beside:
            stay.append(t)
            continue
        target = min(beside, key=lambda i: trees[i].find_nearest(middle)[3])
        into = world[target].inverted() @ world[top]
        turn = into.to_3x3()
        handed[target].append([(into @ q, (turn @ n).normalized(), uv) for q, n, uv in t])
        count += 1
    new[top] = from_corners(new[top], stay)
    for i, tris in handed.items():
        new[i] = from_corners(new[i], tris)
    report.append(f"  {count} triangles below the waist ({level:.2f}) handed down from {nodes[top]['name']}")


def lay_lids(new, nodes, names, report):
    """The plan's "lids": Meshy stood the flat lid of a box (the Knights' hover pods) in the air over its walls, so one
    looks in under it. Each loose flat piece is laid on what lies below it, LID_SINK into the walls."""
    for i, node in enumerate(nodes):
        if node["name"] not in names or i not in new:
            continue
        tris = corner_list(new[i])
        parent = {}

        def find(k):
            while parent.setdefault(k, k) != k:
                parent[k] = parent[parent[k]]
                k = parent[k]
            return k

        def key(q):
            return tuple(round(x, 3) for x in q)

        for t in tris:
            first = find(key(t[0][0]))
            for c in t[1:]:
                parent[find(key(c[0]))] = first
        pieces = {}
        for t in tris:
            pieces.setdefault(find(key(t[0][0])), []).append(t)
        for piece in pieces.values():
            points = [c[0] for t in piece for c in t]
            lo = [min(q[k] for q in points) for k in range(3)]
            hi = [max(q[k] for q in points) for k in range(3)]
            if hi[2] - lo[2] > 0.08 or min(hi[0] - lo[0], hi[1] - lo[1]) < 0.8:
                continue
            below = [c[0].z for other in pieces.values() if other is not piece for t in other for c in t
                     if lo[0] <= c[0].x <= hi[0] and lo[1] <= c[0].y <= hi[1] and c[0].z < lo[2]]
            if not below:
                continue
            drop = lo[2] - max(below) + LID_SINK
            if drop <= 0:
                continue
            for t in piece:
                t[:] = [(q - Vector((0, 0, drop)), n, uv) for q, n, uv in t]
            report.append(f"  {node['name']}: lid laid {drop:.3f} lower")
        new[i] = from_corners(new[i], [t for piece in pieces.values() for t in piece])


def split_joint(new, nodes, world, names, extras, report):
    """The plan's "split": Meshy built a foot in one piece where the original hinges a toe on it, so the cut by nearest
    part tore it. [foot, toe]: what lies ahead of the toe's hinge goes to the toe, cut along the plane through the hinge
    and closed on both sides with dark steel, and the toe can swing without opening the foot."""
    foot, toe = (next(k for k, n in enumerate(nodes) if n["name"] == name) for name in names)
    into_foot = world[foot].inverted() @ world[toe]
    turn = into_foot.to_3x3().normalized()
    tris = corner_list(new[foot]) + [[(into_foot @ q, turn @ n, uv) for q, n, uv in t] for t in corner_list(new[toe])]
    hinge = into_foot.translation.x

    def side(c):
        return c[0].x - hinge

    def between(a, b):
        share = side(a) / (side(a) - side(b))
        return (a[0].lerp(b[0], share), a[1].lerp(b[1], share).normalized(), a[2].lerp(b[2], share))

    behind, ahead, seam = [], [], []
    for t in tris:
        back = [c for c in t if side(c) <= 0]
        front = [c for c in t if side(c) > 0]
        if not front or not back:
            (ahead if front else behind).append(t)
            continue
        lone, pair, lone_out, pair_out = (back[0], front, behind, ahead) if len(back) == 1 else (front[0], back, ahead, behind)
        a, b = between(lone, pair[0]), between(lone, pair[1])
        lone_out.append([lone, a, b])
        pair_out += [[a, pair[0], pair[1]], [a, pair[1], b]]
        seam.append((a[0], b[0]))
    middle = sum((q for edge in seam for q in edge), Vector()) / max(2 * len(seam), 1)
    normal = Vector((1, 0, 0))
    uv = Vector(DARK_SPOT)
    faces_foot = [[(middle, normal, uv), (a, normal, uv), (b, normal, uv)] for a, b in seam]
    faces_toe = [[(middle, -normal, uv), (b, -normal, uv), (a, -normal, uv)] for a, b in seam]
    out_of_foot = into_foot.inverted()
    turn_back = out_of_foot.to_3x3().normalized()

    def to_toe(tris):
        return [[(out_of_foot @ q, turn_back @ n, uv) for q, n, uv in t] for t in tris]

    new[foot] = from_corners(new[foot], behind)
    new[toe] = from_corners(new[toe], to_toe(ahead))
    extras.setdefault(foot, []).append(from_corners(meshy_part(forge_maps("dark")), faces_foot))
    extras.setdefault(toe, []).append(from_corners(meshy_part(forge_maps("dark")), to_toe(faces_toe)))
    report.append(f"  {names[0]} and {names[1]}: {len(behind)} and {len(ahead)} triangles, cut along {len(seam)} edges")


def clear_socket(new, world, eye_node, eye_parts, report):
    """The plan's "socket": the hull's own frame for the eyes stood through the eye box. What of the hull lies in the
    box's place, by the original's eyes, goes; the box fills the gap."""
    points = [Vector(e["positions"][k:k + 3]) for e in eye_parts for k in range(0, len(e["positions"]), 3)]
    half_y = max(abs(q.y) for q in points) + SOCKET_MARGIN
    half_z = max(abs(q.z) for q in points) + SOCKET_MARGIN
    gone = 0
    for i in list(new):
        if i == eye_node:
            continue
        into_box = world[eye_node].inverted() @ world[i]
        tris = corner_list(new[i])

        def within(t):
            q = into_box @ (sum((c[0] for c in t), Vector()) / 3)
            return -SOCKET_DEPTH <= q.x <= SOCKET_MARGIN * 4 and abs(q.y) <= half_y and abs(q.z) <= half_z

        kept = [t for t in tris if not within(t)]
        gone += len(tris) - len(kept)
        new[i] = from_corners(new[i], kept)
    report.append(f"  {gone} triangles of the hull out of the eye box's place")


def drop_slivers(new, nodes, slivers, report):
    """The plan's "slivers": a cut leaves long splinters on small parts: those of the nodes whose name has "match" with
    an edge over "edge" go."""
    for i, node in enumerate(nodes):
        if slivers["match"] in node["name"] and i in new:
            tris = corner_list(new[i])
            kept = [t for t in tris if max((t[a][0] - t[b][0]).length for a, b in ((0, 1), (1, 2), (2, 0))) <= slivers["edge"]]
            if len(kept) < len(tris):
                report.append(f"  {len(tris) - len(kept)} splinters off {node['name']}")
            new[i] = from_corners(new[i], kept)


def fit_rotors(new, nodes, world, names, extras, report):
    """The plan's "rotors": Meshy built a fan's blades into its housing, and the cut by nearest part left some in the
    node that turns and some in the one that does not. For each fan named, what lies in its disc goes from the housing
    (its parent) and the node keeps the upgraded model's: the original's disc of blades, a picture that spins whole. A
    dark well and a hub behind it shut the housing."""
    turning = set()
    for i, node in enumerate(nodes):
        if node["name"] not in names:
            continue
        turning.add(i)
        housing = node["parent"]
        into = world[housing].inverted() @ world[i]
        centre, axis = into.translation, (into.to_3x3().normalized() @ Vector((0, 0, 1))).normalized()
        radius = max(abs(c) for p in node["parts"] for c in p["positions"][0::3])

        def in_disc(t):
            for c in t:
                d = c[0] - centre
                along = d.dot(axis)
                if not -radius <= along <= 0.6 * radius or (d - axis * along).length > radius * ROTOR_ROOM:
                    return False
            return True

        if housing in new:
            tris = corner_list(new[housing])
            kept = [t for t in tris if not in_disc(t)]
            new[housing] = from_corners(new[housing], kept)
            report.append(f"  {node['name']}: {len(tris) - len(kept)} triangles of Meshy's blades out of {nodes[housing]['name']}")
        new.pop(i, None)
        foot = centre - axis * radius * 0.42
        well = turned(foot, centre + axis * radius * 0.04, [(0, 0), (0, radius * 1.0), (1, radius * 1.03)], DARK_SPOT, sides=24)
        well += turned(foot, centre + axis * radius * 0.34, [(0, radius * 0.3), (0.72, radius * 0.24), (0.9, radius * 0.14), (1, 0)],
                       STEEL_SPOT, sides=14)
        extras.setdefault(housing, []).append(from_corners(meshy_part(forge_maps("dark")), well))
    return turning


def forge_maps(kind):
    return {key: f"meshy/textures/forged-{kind}/{key}.jpg" for key in ("albedo", "normal", "orm")}


def real_holes(part):
    """A launcher's tube fronts, painted on the original's plate, cut for real: each cell of the picture (the plate's
    uvs count them) becomes a square of plate round a chamfered hole with a tube behind it and a closed end. Returns
    the triangles of the plate that faces forward and those of any other (left flat)."""
    tris = corner_list(part)
    front = max(sum(c[0].x for c in t) / 3 for t in tris)
    plate = [t for t in tris if sum(c[0].x for c in t) / 3 > front - 1e-3]
    rest = [t for t in tris if sum(c[0].x for c in t) / 3 <= front - 1e-3]
    a, b, c = plate[0]
    # Where a uv lies on the plate.
    solve = Matrix(((b[2].x - a[2].x, c[2].x - a[2].x), (b[2].y - a[2].y, c[2].y - a[2].y))).inverted()

    def at(u, v):
        s, t = solve @ Vector((u - a[2].x, v - a[2].y))
        return a[0] + (b[0] - a[0]) * s + (c[0] - a[0]) * t

    forward = (b[0] - a[0]).cross(c[0] - a[0]).normalized()
    if forward.x < 0:
        forward = -forward
    us = [k[2].x for t in plate for k in t]
    vs = [k[2].y for t in plate for k in t]
    side = (at(1, 0) - at(0, 0)).length
    out = []
    rounds = [2 * math.pi * (k + 1.5) / HOLE_SIDES for k in range(HOLE_SIDES + 1)]
    for cell_u in range(round(min(us)), round(max(us))):
        for cell_v in range(round(min(vs)), round(max(vs))):
            mu, mv = cell_u + 0.5, cell_v + 0.5

            def ring(radius, depth, square=False):
                points = []
                for angle in rounds:
                    cu, cv = math.cos(angle), math.sin(angle)
                    reach = 0.5 / max(abs(cu), abs(cv)) if square else radius
                    points.append((at(mu + reach * cu, mv + reach * cv) - forward * depth * side,
                                   Vector((mu + reach * cu, mv + reach * cv))))
                return points

            def band(outer, inner, facing):
                for k in range(HOLE_SIDES):
                    (p0, u0), (p1, u1), (q0, w0), (q1, w1) = outer[k], outer[k + 1], inner[k], inner[k + 1]
                    n0, n1 = facing(p0), facing(p1)
                    out.extend(([(p0, n0, u0), (p1, n1, u1), (q1, n1, w1)], [(p0, n0, u0), (q1, n1, w1), (q0, n0, w0)]))

            middle = at(mu, mv)

            def inward(q):
                return (middle - forward * (middle - q).dot(forward) * 0 - (q - forward * (q - middle).dot(forward))).normalized()

            edge, lip, bottom = ring(0, 0, True), ring(HOLE_RADIUS * 1.14, 0), ring(HOLE_RADIUS, 0.09)
            deep = [(q - forward * side * HOLE_DEPTH, Vector((mu, mv))) for q, _ in bottom]
            band(edge, lip, lambda q: forward)
            band(lip, bottom, lambda q: (forward + inward(q)).normalized())
            band(bottom, deep, inward)
            end = middle - forward * side * (HOLE_DEPTH + 0.09)
            for k in range(HOLE_SIDES):
                out.append([(end, forward, Vector((mu, mv))), (deep[k][0], forward, Vector((mu, mv))), (deep[k + 1][0], forward, Vector((mu, mv)))])
    # The original's turn of the corners, whichever way its uvs run across the plate.
    turn = (b[0] - a[0]).cross(c[0] - a[0]).dot(a[1])
    out = [t if (t[1][0] - t[0][0]).cross(t[2][0] - t[0][0]).dot(t[0][1]) * turn > 0 else [t[0], t[2], t[1]] for t in out]
    return out, rest


def forge(nodes, group, classic_nodes, mount=None):
    """The plan's "forged": a weapon as the original built it, edges unbevelled (the bevels draw the shell back from
    what glows in it), in the worn metal of tools/blender/weapon_metal.py, its launcher tubes real holes. What glows
    stays as it is. "mount" gives a weapon a turned pedestal ("at" across, "z" from and to, "radius") for the original's
    black wedge."""
    for i in group:
        parts = []
        for part in classic_nodes[i]["parts"]:
            name = os.path.basename(part.get("texture", "")).lower()
            tint = part.get("diffuse", [1, 1, 1, 1])
            if mount and not name and max(part.get("emissive", [0, 0, 0])) <= 0.01:
                continue
            if max(part.get("emissive", [0, 0, 0])) > 0.01 or name not in ("mach_b.bmp", "fangrl_t.bmp", "stripes1.bmp", "misshole.bmp"):
                parts.append(part)
                continue
            if name == "misshole.bmp":
                holes, rest = real_holes(part)
                parts.append(from_corners(dict(meshy_part(forge_maps("front"))), holes))
                if rest:
                    parts.append(from_corners(dict(meshy_part(forge_maps("dark"))), rest))
                continue
            kind = {"fangrl_t.bmp": "grille", "stripes1.bmp": "stripes"}.get(name)
            kind = kind or ("paint" if tint[0] > 0.5 and tint[1] < 0.2 else "dark" if max(tint[:3]) < 0.2 else "steel")
            parts.append(from_corners(dict(meshy_part(forge_maps(kind))), corner_list(part)))
        if mount:
            x, y = mount["at"]
            r = mount["radius"]
            profile = [(0, r * 1.5), (0.14, r * 1.5), (0.2, r), (0.72, r), (0.78, r * 1.22), (1, r * 1.22)]
            pedestal = turned(Vector((x, y, mount["z"][0])), Vector((x, y, mount["z"][1])), profile, DARK_SPOT, sides=20)
            parts.append(from_corners(meshy_part(forge_maps("dark")), pedestal))
        nodes[i]["parts"] = parts


def sphere(radius, fx):
    """A part that is a ball about its node, drawn by the effect <fx> (ClassicAssets; the game's FxShaders)."""
    rings, sides = 14, 28

    def point(i, k):
        a, b = math.pi * i / rings, 2 * math.pi * k / sides
        return Vector((math.sin(a) * math.cos(b), math.sin(a) * math.sin(b), math.cos(a)))

    tris = []
    for i in range(rings):
        for k in range(sides):
            quad = [point(i, k), point(i + 1, k), point(i + 1, k + 1), point(i, k + 1)]
            corners = [(q * radius, q, Vector((0, 0))) for q in quad]
            tris += [t for t in ([corners[0], corners[1], corners[2]], [corners[0], corners[2], corners[3]])
                     if (t[1][0] - t[0][0]).cross(t[2][0] - t[0][0]).length > 1e-9]
    return from_corners({"texture": "", "diffuse": [0, 0, 0, 1], "ambient": [1, 1, 1], "emissive": [0, 0, 0], "fx": fx}, tris)


def vortex_weapon(nodes, world, group, classic_nodes, report):
    """The plan's "vortex": the Ballista's vortex gun, the original's parts made whole. Its black sprite is now what
    the shot lets loose, a little black hole with the fired vortex's shaders (a node of its own: they draw a ball about
    their node); its three arms, thin wedges floating in the air, are ribbed tubes rising from the turntable round the
    hole to the head; its turntable, a keyed quad, is a turned platter; the head and the shards of light stay."""
    gun = group[0]
    node = nodes[gun]
    centre = Vector(node["sprites"][0]["centre"])
    head = [p for p in node["parts"] if "dome_b" in p.get("texture", "")]
    shards = [p for p in classic_nodes[gun]["parts"] if max(p.get("emissive", [0, 0, 0])) > 0.01]
    plate = [p for p in classic_nodes[gun]["parts"] if "whl_bt" in p.get("texture", "")][0]["positions"]
    xs, ys, zs = plate[0::3], plate[1::3], plate[2::3]
    middle = Vector(((min(xs) + max(xs)) / 2, (min(ys) + max(ys)) / 2, zs[0]))
    radius = (max(xs) - min(xs)) / 2
    arms = []
    for y in (-1, 1):
        arms += ribbed_tube([middle + Vector((-0.42, 0.74 * y, -0.05)), Vector((1.3, 1.75 * y, 1.1)), Vector((2.35, 1.4 * y, 2.1)),
                             Vector((3.38, 0.1 * y, 2.08))], 0.17, 9, Vector(STEEL_SPOT), rings=40, sides=8, taper=0.42)
    arms += ribbed_tube([middle + Vector((0.92, 0, -0.05)), Vector((3.05, 0, 0.32)), Vector((3.5, 0, 1.0)), Vector((3.56, 0, 1.87))],
                        0.17, 7, Vector(STEEL_SPOT), rings=32, sides=8, taper=0.42)

    def picture(r, a):
        return Vector((0.5 + 0.49 * r / radius * math.cos(a), 0.5 + 0.49 * r / radius * math.sin(a)))

    platter = turned(middle - Vector((0, 0, 0.2)), middle + Vector((0, 0, 0.02)),
                     [(0, radius * 0.94), (0.6, radius * 1.02), (1, radius * 0.96), (1, 0)], picture, sides=32)
    node["parts"] = head + shards + [from_corners(meshy_part(forge_maps("steel")), arms),
                                     from_corners(meshy_part(forge_maps("platter")), platter)]
    node["sprites"] = []
    nodes.append({"name": "vortex", "parent": gun, "transform": [1, 0, 0, 0, 1, 0, 0, 0, 1, *centre],
                  "parts": [sphere(VORTEX_HOLE, "hole"), sphere(VORTEX_SHELL, "shell")], "sprites": [], "spinners": []})
    world.append(world[gun] @ Matrix.Translation(centre))
    report.append(f"  vortex gun: {len(arms)} triangles of arms, a hole of {VORTEX_HOLE} m at {tuple(round(c, 2) for c in centre)}")


def carrying_arm(new, nodes, arm, report):
    """The arm that carries a weapon, where Meshy made none worth keeping: the original's triangles of the node <arm>
    names ("node") out past "reach" across, with the Meshy model's paint. "paint" is a rectangle of the hull's texture
    (u, v low and high); each face shows it from its own side, as a box is papered."""
    i = next(k for k, n in enumerate(nodes) if n["name"] == arm["node"])
    if i not in new:
        return
    tris = [t for part in nodes[i]["parts"] if not painted_shadow(part) and not hidden(part) for t in corner_list(part)
            if sum(c[0].y for c in t) / 3 < arm["reach"]]
    points = [c[0] for t in tris for c in t]
    lo = Vector([min(q[k] for q in points) for k in range(3)])
    size = Vector([max(q[k] for q in points) for k in range(3)]) - lo
    u0, v0, u1, v1 = arm["paint"]
    painted = []
    for t in tris:
        facing = (t[1][0] - t[0][0]).cross(t[2][0] - t[0][0])
        side = max(range(3), key=lambda k: abs(facing[k]))
        a, b = [k for k in range(3) if k != side]
        painted.append([(q, n, Vector((u0 + (u1 - u0) * (q[a] - lo[a]) / max(size[a], 1e-6),
                                       v0 + (v1 - v0) * (q[b] - lo[b]) / max(size[b], 1e-6)))) for q, n, _ in t])
    new[i] = from_corners(new[i], corner_list(new[i]) + painted)
    report.append(f"  the original's arm on {arm['node']}: {len(painted)} triangles")


def first_contact(tree, points, direction, looks=None):
    """How far the nearest of <points> is from the surface in <tree> along <direction>: None if none meets it. With
    <looks> (a triangle's index to its normal) only surface that faces the points counts."""
    gap = None
    for q in points:
        hit, _, index, distance = tree.ray_cast(q, direction)
        if hit is not None and (looks is None or looks(index).dot(direction) < 0) and (gap is None or distance < gap):
            gap = distance
    return gap


def close_gaps(new, nodes, world, weapons, report, fanned=False):
    """Moves each weapon (its nodes' parts in <new>, or the original's where the plan names no Meshy weapon) straight
    onto the part that carries its mount, as the rest pose stands: a left or right weapon across towards it, one on top (t_gun) down; a gun alone in front stays. Weapons <fanned> round the hull (Knight 5's three) would run into each other that way:
    each, the gun too, moves down its own mount's axis. Only
    surface the weapon looks onto counts (by the stored normals; Meshy's windings go either way). Some originals stood
    off their hulls too (the report says how far), but the Meshy hulls have mounts to sit on."""
    def shown(i):
        return [new[i]] if i in new else [p for p in nodes[i]["parts"] if not painted_shadow(p) and not hidden(p)]

    everything = [world[i] @ c[0] for i in new for t in corner_list(new[i]) for c in t]
    size = max((max(q[k] for q in everything) - min(q[k] for q in everything)) for k in range(3))
    for mount, group in weapons.items():
        if mount == "gun" and not fanned:
            continue
        carrier = nodes[group[0]]["parent"]
        if carrier not in new:
            continue
        hull_tris = [[(world[carrier] @ c[0], (world[carrier].to_3x3() @ c[1]).normalized()) for c in t]
                     for t in corner_list(new[carrier])]
        tree = BVHTree.FromPolygons([c[0] for t in hull_tris for c in t], [(k, k + 1, k + 2) for k in range(0, 3 * len(hull_tris), 3)])
        points = [world[i] @ c[0] for i in group for part in shown(i) for t in corner_list(part) for c in t]
        points += [world[i] @ (sum((c[0] for c in t), Vector()) / 3) for i in group for part in shown(i) for t in corner_list(part)]
        if not points:
            continue
        middle = sum(points, Vector()) / len(points)
        body = sum((c[0] for t in hull_tris for c in t), Vector()) / (3 * len(hull_tris))
        axis = 2 if mount == "t_gun" else 1
        direction = Vector([0, 0, 0])
        direction[axis] = 1 if body[axis] > middle[axis] else -1
        if fanned:
            direction = -(world[group[0]].to_3x3() @ Vector((0, 0, 1))).normalized()
        gap = first_contact(tree, points, direction, lambda index: sum((c[1] for c in hull_tris[index]), Vector()))
        carried = [[world[carrier] @ c for c in t] for part in nodes[carrier]["parts"]
                   if not painted_shadow(part) and not hidden(part) for t in triangles(part)]
        before = [world[i] @ c for i in group for part in nodes[i]["parts"]
                  if not painted_shadow(part) and not hidden(part) for t in triangles(part) for c in t]
        original = first_contact(BVHTree.FromPolygons([c for t in carried for c in t], [(k, k + 1, k + 2) for k in range(0, 3 * len(carried), 3)]),
                                 before, direction) if carried and before else None
        own = max((max(q[k] for q in points) - min(q[k] for q in points)) for k in range(3))
        if gap is None or gap < GAP_MIN * size:
            continue
        shift = min(gap + GAP_SINK * size, GAP_MAX * own)
        move = direction * shift
        for i in group:
            local = world[i].to_3x3().inverted() @ move
            for part in [new[i]] if i in new else nodes[i]["parts"]:
                q = part["positions"]
                part["positions"] = [round(q[k] + local[k % 3], 5) for k in range(len(q))]
        report.append(f"  {mount}: moved {shift:.3f} along {tuple(round(c, 2) for c in direction)} onto {nodes[carrier]['name']} "
                      f"({gap / size:.1%} of the machine off it, the original "
                      f"{'not in that way' if original is None else f'{original / size:.1%}'})")


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
        # A map changed by hand keeps Meshy's beside it (<key>-meshy.jpg, tools/blender/calm_glow.py) and stays.
        if os.path.exists(os.path.join(textures, key + "-meshy.jpg")):
            maps[key] = os.path.join(textures, key + ".jpg")
            continue
        # The GLB's own bytes (Meshy packs JPEGs), named by what they are.
        data = bytes(image.packed_file.data)
        target = os.path.join(textures, key + (".png" if data[:4] == b"\x89PNG" else ".jpg"))
        with open(target, "wb") as f:
            f.write(data)
        maps[key] = target
    # A glow map laid beside them (tools/blender/calm_glow.py).
    if os.path.exists(os.path.join(textures, "emission.png")):
        maps["emission"] = os.path.join(textures, "emission.png")
    return tris, maps


def front_turns(front, flips):
    """For each of <flips>, the turn about z that lays a Meshy model's front (fronts.json: the azimuth of a camera
    looking at it, Blender axes, 0 = from -Y, 90 = from +X) along the original's +X."""
    angle = math.radians(front)
    facing = Vector((math.sin(angle), -math.cos(angle), 0))
    turns = []
    for flip, label in flips:
        f = flip @ facing
        turns.append((Matrix.Rotation(-math.atan2(f.y, f.x), 4, "Z") @ flip, f"front {front}°{label}"))
    return turns


def fit(source, target_points, mirror, turn=None, front=None, outward=None, held=False):
    """Into the original's axes (HANDED; and, with <mirror>, the other way too), turn about z (the one <turn> given;
    or onto the known <front>; or else four starts), scale and move the source points onto the target points; returns
    (score, transform, how) with the score the mean distance both ways as a share of the target's size. With
    <outward> (a side's azimuth, as fronts.json's) and a target off the middle, only the starts that turn that side
    away from the middle are tried. <held>: the known front is kept exactly, only scale and place are fit (a model
    built to the original's layout, whose parts the fit would otherwise turn to a compromise)."""
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
    flips = [(HANDED, "")] + ([(Matrix.Identity(4), " mirrored")] if mirror else [])
    starts = [(turn, "turned to the eyes")] if turn is not None else front_turns(front, flips) if front is not None else [
        (Matrix.Rotation(quarter * math.pi / 2, 4, "Z") @ flip, f"{quarter * 90}°{label}, no front known")
        for flip, label in flips for quarter in range(4)]
    if outward is not None and abs(lo.y + hi.y) > 1e-3:
        side = Vector((math.sin(math.radians(outward)), -math.cos(math.radians(outward)), 0))
        starts = [(s, how) for s, how in starts if (s.to_3x3() @ side).y * (lo.y + hi.y) > 0] or starts
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
            step = similarity_about_z(pairs, held)
            current = step @ current
        if best is None or score < best[0]:
            best = (score, current, f"{how}, then {math.degrees(swing(current, start)):+.0f}°")
    return best


def swing(transform, start):
    """How far <transform> is turned about z beyond <start>, radians."""
    m = transform.to_3x3() @ start.to_3x3().inverted()
    return math.atan2(m[1][0], m[0][0])


def similarity_about_z(pairs, held=False):
    """The turn about z (none if <held>), uniform scale and move that best lay the first points of the pairs onto the
    second."""
    n = len(pairs)
    cp = sum((a for a, _ in pairs), Vector()) / n
    cq = sum((b for _, b in pairs), Vector()) / n
    sxx = sxy = norm = 0.0
    for a, b in pairs:
        p, q = a - cp, b - cq
        sxx += p.x * q.x + p.y * q.y
        sxy += p.x * q.y - p.y * q.x
    angle = 0.0 if held else math.atan2(sxy, sxx)
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
    # A model fixed by hand (tools/blender/meshy_fixes.py) in place of Meshy's.
    glb = os.path.join(meshy, model_name, "fixed.glb")
    tris, maps = load_meshy(glb if os.path.exists(glb) else os.path.join(meshy, model_name, "model.glb"),
                            os.path.join(classic, "meshy", "textures", model_name.replace("/", "-")))
    vertices = list({tuple(round(x, 5) for x in c[0]): c[0] for t in tris for c in t}.values())
    score, transform, how = fit(vertices, points, mirror, turn, FRONTS.get(model_name), OUTWARD.get(model_name),
                                model_name in HELD)
    if model_name in WIDEN:
        # Wider across than Meshy made it (and the fit, true to the original's narrow shape, kept it), about its side
        # that faces the machine's middle, so it stays on its mount.
        ys = [(transform @ v).y for v in vertices]
        inner = min((min(ys), max(ys)), key=abs)
        transform = (Matrix.Translation((0, inner, 0)) @ Matrix.Diagonal((1, WIDEN[model_name], 1, 1))
                     @ Matrix.Translation((0, -inner, 0)) @ transform)
        how += f", {WIDEN[model_name]} times as wide"
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


def hull(points):
    """The convex hull of 2D points, anticlockwise (Andrew's monotone chain)."""
    points = sorted(set(points))
    if len(points) < 3:
        return points

    def cross(o, a, b):
        return (a[0] - o[0]) * (b[1] - o[1]) - (a[1] - o[1]) * (b[0] - o[0])

    lower, upper = [], []
    for p in points:
        while len(lower) >= 2 and cross(lower[-2], lower[-1], p) <= 0:
            lower.pop()
        lower.append(p)
    for p in reversed(points):
        while len(upper) >= 2 and cross(upper[-2], upper[-1], p) <= 0:
            upper.pop()
        upper.append(p)
    return lower[:-1] + upper[:-1]


def outline(corners, spacing, window):
    """A closed outline (2D corners) with its corners cut round (Chaikin), sampled every <spacing>, and its outward
    normals averaged over <window> either way (so a belt's inner face follows a corner without folding)."""
    loop = [Vector(c) for c in corners]
    for _ in range(4):
        loop = [p for a, b in zip(loop, loop[1:] + loop[:1]) for p in (a.lerp(b, 0.25), a.lerp(b, 0.75))]
    lengths = [(b - a).length for a, b in zip(loop, loop[1:] + loop[:1])]
    total = sum(lengths)
    count = max(12, round(total / spacing))
    samples, k, run = [], 0, 0.0
    for i in range(count):
        at = total * i / count
        while run + lengths[k] < at:
            run += lengths[k]
            k += 1
        a, b = loop[k], loop[(k + 1) % len(loop)]
        samples.append(a.lerp(b, (at - run) / lengths[k]))
    reach = max(1, round(window / (total / count)))
    normals = []
    for i in range(count):
        tangent = samples[(i + reach) % count] - samples[(i - reach) % count]
        # Anticlockwise: outward is the tangent turned clockwise.
        normals.append(Vector((tangent.y, -tangent.x)).normalized())
    return samples, normals, total


def ray(polygon, origin, direction):
    """How far from <origin> (inside) along <direction> a convex 2D polygon's outline lies."""
    best = None
    for a, b in zip(polygon, polygon[1:] + polygon[:1]):
        edge = b - a
        denominator = direction.cross(edge)
        if abs(denominator) < 1e-12:
            continue
        t = (a - origin).cross(edge) / denominator
        u = (a - origin).cross(direction) / denominator
        if t > 1e-9 and -1e-9 <= u <= 1 + 1e-9 and (best is None or t < best):
            best = t
    return best


def meshy_triangles(part, move):
    """A Meshy part's triangles moved by <move>: [(point, normal, uv)] * 3 each."""
    turn = move.to_3x3().normalized()
    p, n, uv = part["positions"], part["normals"], part["uvs"]
    return [[(move @ Vector(p[t + 3 * k:t + 3 * k + 3]), (turn @ Vector(n[t + 3 * k:t + 3 * k + 3])).normalized(),
              uv[(t // 3 + k) * 2:(t // 3 + k) * 2 + 2]) for k in range(3)] for t in range(0, len(p) - 8, 9)]


def store(corners, move, part):
    """Appends a triangle's corners to an export part, moved by <move> into the part's node."""
    turn = move.to_3x3().normalized()
    for point, normal, uv in corners:
        part["positions"] += [round(c, 5) for c in move @ point]
        part["normals"] += [round(c, 4) for c in (turn @ normal).normalized()]
        part["uvs"] += [round(uv[0], 5), round(uv[1], 5)]


PAINT = {}


def painted(classic, maps, uvs):
    """Whether a Meshy triangle (its corners' uvs) shows paint: saturated red to yellow (the side plates' paint and
    flames), not the grey steel and rust of the wheels Meshy painted on beside them."""
    path = os.path.join(classic, maps["albedo"])
    if path not in PAINT:
        image = bpy.data.images.load(path, check_existing=True)
        width, height = image.size
        pixels = image.pixels[:]
        PAINT[path] = (width, height, pixels)
    width, height, pixels = PAINT[path]
    u = sum(uv[0] for uv in uvs) / 3
    v = sum(uv[1] for uv in uvs) / 3
    hits = 0
    for su, sv in ((u, v),) + tuple((uv[0] * 0.7 + u * 0.3, uv[1] * 0.7 + v * 0.3) for uv in uvs):
        # Blender keeps the rows bottom up.
        k = 4 * (min(height - 1, max(0, int((1 - sv) * height))) * width + min(width - 1, max(0, int(su * width))))
        r, g, b = pixels[k:k + 3]
        hue, saturation, value = colorsys.rgb_to_hsv(r, g, b)
        if value > 0.2 and saturation > 0.45 and (hue < 55 / 360 or hue > 340 / 360):
            hits += 1
    return hits >= 3


def meshy_part(maps):
    return {"texture": "", "diffuse": [1, 1, 1, 1], "ambient": [1, 1, 1], "emissive": [0, 0, 0],
            "positions": [], "normals": [], "uvs": [], "maps": maps}


def belts(out, source_nodes, world, hull_nodes, classic, report, own_cover=None):
    """Replaces each track's Meshy tread with a belt that scrolls (see the module's notes) and covers what lies inside
    it; keeps out's rig tracks with the belts' own repeats per metre."""
    nodes = out["rig"]["nodes"]
    tracks = out["rig"]["tracks"]
    sides = []
    for side in ("left", "right"):
        trx = tracks[side]
        name = nodes[trx]["name"]
        # The side plate's node; without one (APC 2) the covers go to the track's parent, the hull.
        plate_node = next((i for i, n in enumerate(nodes) if n["name"] == name.replace("trx", "trs") and i != trx), None)
        trs = nodes[trx]["parent"] if plate_node is None else plate_node
        sources = [trx] if plate_node is None else [trx, plate_node]
        into = world[trx].inverted()
        # The Meshy tread and side plate in the track's own space.
        tris = [t for node in sources for part in nodes[node]["parts"] if part.get("maps")
                for t in meshy_triangles(part, into @ world[node])]
        # The original's belt bounds where the Meshy tread may be (the cut sometimes hands a track bits of the hull).
        original = [Vector(part["positions"][k:k + 3]) for part in source_nodes[trx]["parts"] for k in range(0, len(part["positions"]), 3)]
        lo = Vector([min(q[k] for q in original) for k in range(3)])
        hi = Vector([max(q[k] for q in original) for k in range(3)])
        slack = (hi - lo) * 0.2
        # Meshy often raised the tread's top run into the hull's shadow (Resource Carrier 5), so it may reach higher.
        rise = Vector((slack.x, slack.y, 2.5 * slack.z))
        near = [c[0] for t in tris for c in t if all(lo[k] - slack[k] <= c[0][k] <= hi[k] + rise[k] for k in range(3))]
        if len(near) < 20:
            report.append(f"  {name}: too little Meshy tread for a belt")
            return
        corners = hull([(q.x, q.z) for q in near])
        height = max(c[1] for c in corners) - min(c[1] for c in corners)
        thickness, margin = BELT_THICKNESS * height, BELT_MARGIN * height
        samples, normals, length = outline(corners, BELT_PITCH * height / 3, thickness)
        loop = [Vector((p.x, p.y)) for p in samples]

        def inside_depth(point, loop=loop):
            q = Vector((point.x, point.z))
            # Anticlockwise: inside lies left of every edge.
            return min((b - a).cross(q - a) / max((b - a).length, 1e-9) for a, b in zip(loop, loop[1:] + loop[:1]))

        def middle_of(t):
            return sum((c[0] for c in t), Vector()) / 3

        # The tread: within the belt's thickness of the outline; the faces that look out across the track (not
        # sideways) give its width.
        tread = [t for t in tris if lo.y - slack.y <= middle_of(t).y <= hi.y + slack.y
                 and -0.5 * height < inside_depth(middle_of(t)) < thickness]
        facing_out = sorted(c[0].y for t in tread for c in t if abs(sum((c[1] for c in t), Vector()).normalized().y) < 0.5)
        if len(facing_out) < 6:
            report.append(f"  {name}: no Meshy tread faces out")
            return
        y0, y1 = facing_out[len(facing_out) // 50], facing_out[-1 - len(facing_out) // 50]
        sides.append((side, trx, trs, sources, samples, normals, length, thickness, margin, height, y0, y1, inside_depth))
    pitch = BELT_PITCH * sum(s[9] for s in sides) / len(sides)
    # Tiles per metre of each belt: the rig scrolls both by their mean, so their bottoms keep still on the ground.
    rates = []
    for side, trx, trs, sources, samples, normals, length, thickness, margin, height, y0, y1, inside_depth in sides:
        # A whole number of tiles round, each about TREAD_LINKS links of <pitch>; counted from the first belt, so two
        # belts of about the same length get the same count even when it lies halfway between two.
        first = sides[0][6]
        tiles = max(1, round(length * max(1, round(first / (pitch * TREAD_LINKS))) / first))
        # The belt runs backwards along its bottom as the machine drives forwards (+x): clockwise seen from -y with x
        # to the right and z up, in the model's axes. The rig's repeats per metre are negative, so the texture moves
        # along the loop's direction.
        turn = world[trx].to_3x3()
        area = sum((turn @ Vector((a.x, 0, a.y))).x * (turn @ Vector((b.x, 0, b.y))).z
                   - (turn @ Vector((b.x, 0, b.y))).x * (turn @ Vector((a.x, 0, a.y))).z
                   for a, b in zip(samples, samples[1:] + samples[:1]))
        if area > 0:
            samples, normals = samples[::-1], normals[::-1]
        count = len(samples)
        arcs = [0.0]
        for a, b in zip(samples, samples[1:] + samples[:1]):
            arcs.append(arcs[-1] + (b - a).length)
        vs = [tiles * a / arcs[-1] for a in arcs]
        rates.append(tiles / arcs[-1])
        outer = [p + n * margin for p, n in zip(samples, normals)]
        inner = [p + n * (margin - thickness) for p, n in zip(samples, normals)]
        part = meshy_part(dict(TREAD_MAPS))

        def quad(corners):
            for k in (0, 1, 2, 0, 2, 3):
                point, normal, uv = corners[k]
                part["positions"] += [round(c, 5) for c in point]
                part["normals"] += [round(c, 4) for c in normal]
                part["uvs"] += [round(uv[0], 5), round(uv[1], 5)]

        for i in range(count):
            j = (i + 1) % count
            a, b = vs[i], vs[i + 1]
            ni, nj = Vector((normals[i].x, 0, normals[i].y)), Vector((normals[j].x, 0, normals[j].y))
            oi, oj = Vector((outer[i].x, 0, outer[i].y)), Vector((outer[j].x, 0, outer[j].y))
            ii, ij = Vector((inner[i].x, 0, inner[i].y)), Vector((inner[j].x, 0, inner[j].y))
            low, high = Vector((0, y0, 0)), Vector((0, y1, 0))
            quad([(oi + low, ni, (0, a)), (oj + low, nj, (0, b)), (oj + high, nj, (0.5, b)), (oi + high, ni, (0.5, a))])
            quad([(ii + high, -ni, (0.75, a)), (ij + high, -nj, (0.75, b)), (ij + low, -nj, (1, b)), (ii + low, -ni, (1, a))])
            for y, normal in ((low, Vector((0, -1, 0))), (high, Vector((0, 1, 0)))):
                quad([(oi + y, normal, (0.5, a)), (oj + y, normal, (0.5, b)), (ij + y, normal, (0.75, b)), (ii + y, normal, (0.75, a))])
        part["indices"] = list(range(len(part["positions"]) // 3))

        # Inside the belt: the Meshy wheels would stand still while the belt runs, so they go, with the old tread and
        # whatever else the cut left there (bits of the hull, of a turning upper body). The side plate facing out is
        # stretched in side view until it fills the belt, and a mirrored copy closes the side facing the machine.
        into = world[trx].inverted()
        middle_y, reach = (y0 + y1) / 2, 0.1 * (y1 - y0)
        centre = world[trx] @ Vector((0, middle_y, 0))
        axis = world[trx].to_3x3() @ Vector((0, 1, 0))
        outward = 1 if centre.y * axis.y > 0 else -1
        # Outwards the side plate may stand off the belt; inwards the hull is near.
        y_out, y_in = (y1, y0) if outward > 0 else (y0, y1)
        lo_y, hi_y = sorted((y_in - outward * reach, y_out + outward * 0.6 * (y1 - y0)))
        cover, removed = [], 0
        back = world[trs].inverted() @ world[trx]
        moved_out = {}
        for node in hull_nodes:
            move = into @ world[node]
            kept_parts = []
            for old in nodes[node]["parts"]:
                if not old.get("maps"):
                    kept_parts.append(old)
                    continue
                kept = meshy_part(old["maps"])
                for corners, local in zip(meshy_triangles(old, move), meshy_triangles(old, Matrix.Identity(4))):
                    middle = sum((c[0] for c in corners), Vector()) / 3
                    if not (lo_y <= middle.y <= hi_y and inside_depth(middle) > -2 * margin):
                        if node == trx:
                            # Outside the belt, but the track's node scrolls: to the side plate's.
                            store(corners, back, moved_out.setdefault(old["maps"]["albedo"], meshy_part(old["maps"])))
                        else:
                            store(local, Matrix.Identity(4), kept)
                        continue
                    facing = sum((c[1] for c in corners), Vector()).normalized()
                    # (Meshy turns some faces inside out: the side counts, not which way the normal points.)
                    if (node in sources and abs(facing.y) > 0.3 and (middle.y - middle_y) * outward > 0.2 * (y1 - y0)
                            and painted(classic, old["maps"], [c[2] for c in corners])):
                        cover.append((corners, old["maps"]))
                    else:
                        removed += 1
                if kept["positions"]:
                    kept["indices"] = list(range(len(kept["positions"]) // 3))
                    kept_parts.append(kept)
            nodes[node]["parts"] = kept_parts
        covers = {}
        target = [Vector((q.x, q.y)) for q in inner]

        def side_area(corners):
            a, b, c = (Vector((p.x, p.z)) for p, _, _ in corners)
            return abs((b - a).cross(c - a)) / 2

        loop_area = sum(abs(a.cross(b)) / 2 for a, b in zip(target, target[1:] + target[:1]))
        painted_share = sum(side_area(t) for t, _ in cover) / max(loop_area, 1e-9)
        if own_cover:
            # The plan's "covers": the plate painted from nothing (tools/blender/cover_texture.py) for its outline.
            cover = [] if not cover else cover
            maps = cover_texture.write(classic, own_cover["name"], [(q.x, q.y) for q in target], own_cover.get("emblem"), own_cover.get("band"))
            x0, x1 = min(q.x for q in target), max(q.x for q in target)
            z0, z1 = min(q.y for q in target), max(q.y for q in target)
            middle = sum(target, Vector((0, 0))) / len(target)
            rings, spokes = 3, 48
            grid = []
            for ring in range(rings + 1):
                row = []
                for spoke in range(spokes):
                    angle = 2 * math.pi * spoke / spokes
                    direction = Vector((math.cos(angle), math.sin(angle)))
                    at = middle + direction * (ray(target, middle, direction) or 0) * ring / rings
                    row.append((at, ((at.x - x0) / (x1 - x0), (z1 - at.y) / (z1 - z0))))
                grid.append(row)
        elif painted_share > 0.12:
            # A flat plate filling the belt, its paint the Meshy plate's: each of its points takes the uv of the
            # point of the Meshy plate that lies as far out in that direction, measured from the plate's middle
            # (Meshy built some plates bent and stepped, which stretched would splinter).
            plate = [Vector(c) for c in hull([(c[0].x, c[0].z) for t, _ in cover for c in t])]
            middle = sum(plate, Vector((0, 0))) / len(plate)
            flat = [([Vector((c[0].x, c[0].z)) for c in t], [Vector(c[2]) for c in t]) for t, _ in cover]
            maps = max({m["albedo"]: m for _, m in cover}.values(), key=lambda m: sum(1 for _, n in cover if n is m))

            def paint_at(q):
                best, near = None, None
                for (a, b, c), uvs in flat:
                    v0, v1, v2 = b - a, c - a, q - a
                    d = v0.cross(v1)
                    if abs(d) < 1e-12:
                        continue
                    s_, t_ = v2.cross(v1) / d, v0.cross(v2) / d
                    if s_ >= -1e-6 and t_ >= -1e-6 and s_ + t_ <= 1 + 1e-6:
                        return uvs[0] + (uvs[1] - uvs[0]) * s_ + (uvs[2] - uvs[0]) * t_
                    gap = (q - (a + b + c) / 3).length
                    if near is None or gap < near:
                        s_, t_ = max(0.0, s_), max(0.0, t_)
                        total = s_ + t_
                        if total > 1:
                            s_, t_ = s_ / total, t_ / total
                        best, near = uvs[0] + (uvs[1] - uvs[0]) * s_ + (uvs[2] - uvs[0]) * t_, gap
                return best

            rings, spokes = 10, 48
            grid = []
            for ring in range(rings + 1):
                row = []
                for spoke in range(spokes):
                    angle = 2 * math.pi * spoke / spokes
                    direction = Vector((math.cos(angle), math.sin(angle)))
                    own, wanted = ray(plate, middle, direction), ray(target, middle, direction)
                    share = ring / rings
                    at = middle + direction * (wanted or 0) * share
                    row.append((at, paint_at(middle + direction * (own or 0) * share * 0.995)))
                grid.append(row)
        if own_cover or painted_share > 0.12:
            y_out = (y1 if outward > 0 else y0) - outward * 0.03 * (y1 - y0)
            y_in = 2 * middle_y - y_out
            part_ = covers.setdefault(maps["albedo"], meshy_part(maps))
            for y, facing in ((y_out, outward), (y_in, -outward)):
                normal = Vector((0, facing, 0))

                def corner(cell):
                    return Vector((cell[0].x, y, cell[0].y)), normal, cell[1]

                for ring in range(rings):
                    for spoke in range(spokes):
                        a, b = grid[ring][spoke], grid[ring][(spoke + 1) % spokes]
                        c, d = grid[ring + 1][(spoke + 1) % spokes], grid[ring + 1][spoke]
                        store([corner(a), corner(b), corner(c)], back, part_)
                        store([corner(a), corner(c), corner(d)], back, part_)
        else:
            # No side plate to stretch: a plain one of dark steel (a spot of the tread tile's running face).
            steel = covers.setdefault("steel", meshy_part(dict(TREAD_MAPS)))
            middle = sum(target, Vector((0, 0))) / len(target)
            for y in (y0 + 0.02 * (y1 - y0), y1 - 0.02 * (y1 - y0)):
                normal = Vector((0, 1 if y > middle_y else -1, 0))
                for a, b in zip(target, target[1:] + target[:1]):
                    store([(Vector((middle.x, y, middle.y)), normal, (0.88, 0.1)), (Vector((a.x, y, a.y)), normal, (0.88, 0.1)),
                           (Vector((b.x, y, b.y)), normal, (0.88, 0.1))], back, steel)
        for extra in list(covers.values()) + list(moved_out.values()):
            extra["indices"] = list(range(len(extra["positions"]) // 3))
            nodes[trs]["parts"].append(extra)
        nodes[trx]["parts"] = [p for p in nodes[trx]["parts"] if not p.get("maps")] + [part]
        report.append(f"  {side} belt: {arcs[-1]:.2f} m round, {tiles * TREAD_LINKS} links, {y1 - y0:.2f} m wide, "
                      f"{thickness:.3f} m thick; {removed} Meshy triangles inside it gone, "
                      + ("a cover painted for it" if own_cover else
                         f"its side plate ({len(cover)} triangles, {100 * painted_share:.0f} % of it) as its cover"
                         if painted_share > 0.12 else "a plain steel cover")
                      + f" (in {nodes[trs]['name']})")
    out["rig"]["tracks"]["repeatsPerMeter"] = round(-sum(rates) / len(rates), 4)


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
    moving = [i for i in hull if nodes[i]["name"] not in still]
    # Another export of the same rig to fit the hull to (Reaper 2 wears Reaper 3's body, which its spike would distort).
    shape = entry.get("shape")
    shape_nodes = json.load(open(os.path.join(classic, "remake", "models", "red", shape + ".json")))["rig"]["nodes"] if shape else nodes
    fitted = fit_piece(entry["hull"], meshy, classic, shape_nodes, world_transforms(shape_nodes), moving, report)
    if entry.get("lower") and rig.get("upperBody") is not None:
        def carried(i):
            while i >= 0:
                if i == rig["upperBody"]:
                    return True
                i = nodes[i]["parent"] if nodes[i]["parent"] != i else -1
            return False
        legs = fit_piece(entry["lower"], meshy, classic, nodes, world, moving, report)
        fitted = {i: part for i, part in fitted.items() if carried(i)}
        fitted.update({i: part for i, part in legs.items() if not carried(i)})
    new.update(fitted)
    # A piece of another Meshy model (Reaper 2's spike), kept beside the hull with its own maps.
    borrowed = {}
    if entry.get("borrow"):
        piece = entry["borrow"]
        i = next(k for k, n in enumerate(nodes) if n["name"] == piece["node"])
        source = fit_piece(piece["model"], meshy, classic, nodes, world, moving, report)[i]

        def within(t):
            middle = sum((c[0] for c in t), Vector()) / 3
            return all(piece["lo"][k] <= middle[k] <= piece["hi"][k] for k in range(3))

        borrowed[i] = from_corners(source, [t for t in corner_list(source) if within(t)])
    for mount, name in entry.get("weapons", {}).items():
        new.update(fit_piece(name, meshy, classic, nodes, world, weapons[mount], report, mirror=True))
    if entry.get("faceplate") and faceplate:
        # The eye box looks where the original's eyes look (in the rest pose: some bow their head).
        look = facing([p for p in nodes[faceplate[0]]["parts"] if eyes(p)])
        turn = eye_turn((world[faceplate[0]].to_3x3().normalized() @ look).normalized()) if look else None
        new.update(fit_piece(entry["faceplate"], meshy, classic, nodes, world, faceplate, report, turn=turn))
        i = faceplate[0]
        if i in new:
            up = (world[i].to_3x3().inverted() @ Vector((0, 0, 1))).normalized()
            new[i], scale, blades = grow_eye_box(new[i], [p for p in nodes[i]["parts"] if eyes(p)], up)
            report.append(f"  eye box grown {scale[0]:.2f} times across, {scale[1]:.2f} up, {blades} antenna sheets made solid")
    if entry.get("lids"):
        lay_lids(new, nodes, entry["lids"], report)
    extras = {}
    if entry.get("socket"):
        clear_socket(new, world, faceplate[0], [p for p in nodes[faceplate[0]]["parts"] if eyes(p)], report)
    if entry.get("slivers"):
        drop_slivers(new, nodes, entry["slivers"], report)
    for names in entry.get("split", []):
        split_joint(new, nodes, world, names, extras, report)
    turning = fit_rotors(new, nodes, world, entry.get("rotors", []), extras, report)
    if entry.get("plasma"):
        # The ball the Bee Bomber carries, a sprite in the original: one with a depth to it. The gun's node is its middle.
        for group in weapons.values():
            nodes[group[0]]["sprites"] = []
            extras.setdefault(group[0], []).extend([sphere(entry["plasma"], "plasma"), sphere(entry["plasma"] * 1.16, "shell")])
    if entry.get("mast"):
        raise_mast(new, nodes, entry["mast"], extras, report)
    if entry.get("forged") or entry.get("vortex"):
        originals = json.load(open(os.path.join(classic, "models", "red", export_id + ".json")))["rig"]["nodes"]
        for group in weapons.values():
            if entry.get("forged"):
                forge(nodes, group, originals, entry.get("mount"))
            else:
                vortex_weapon(nodes, world, group, originals, report)
    # Wheels stay the upgraded model's, turned true: Meshy's are never quite round, and wobble as they roll.
    wheels = rig.get("wheels")
    wheel_nodes = set(wheels["left"] + wheels["right"]) if wheels else set()
    # So do the parts the plan names "upgraded" (the Behemoth's claws, which Meshy's model has none worth the name of).
    wheel_nodes |= {i for i, n in enumerate(nodes) if n["name"] in entry.get("upgraded", [])} | turning
    for i in wheel_nodes:
        new.pop(i, None)
    if entry.get("arm"):
        carrying_arm(new, nodes, entry["arm"], report)
    close_gaps(new, nodes, world, weapons if "weapons" in entry else {}, report, entry.get("fanned", False))
    if entry.get("neck"):
        neck_hoses(new, nodes, world, rig, report)
    if entry.get("waist"):
        below_waist(new, nodes, world, rig, report)
    out = copy.deepcopy(model)
    replaced = set(hull) | {i for m in entry.get("weapons", {}) for i in weapons[m]} | (set(faceplate) if entry.get("faceplate") else set())
    replaced -= wheel_nodes
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
        node["parts"] = kept + ([new[i]] if i in new else []) + ([borrowed[i]] if i in borrowed else []) + extras.get(i, [])
        node["spinners"] = []
    # The model's own sprites (in the rest pose) lose the eye glows too.
    if "sprites" in out:
        out["sprites"] = [s for s in out["sprites"]
                          if not any(s["texture"] == t and (Vector(s["centre"]) - c).length < 0.02 for t, c in glows)
                          and not (entry.get("vortex") and "whl_bt" in s["texture"])
                          and not (entry.get("plasma") and "star_bt" in s["texture"])]
    if out["rig"].get("tracks"):
        belts(out, rig["nodes"], world, [i for i in hull if i not in faceplate], classic, report, entry.get("covers"))
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
    fronts = os.path.join(meshy, "fronts.json")
    if os.path.exists(fronts):
        known = json.load(open(fronts, encoding="utf-8-sig"))
        FRONTS.update(known["fronts"])
        OUTWARD.update(known.get("outward", {}))
        HELD.update(known.get("held", []))
        WIDEN.update(known.get("widen", {}))
    for export_id, entry in plan.items():
        if only and export_id not in only:
            continue
        for line in build(export_id, entry, os.path.abspath(classic), os.path.abspath(meshy)):
            print(line, flush=True)


if __name__ == "__main__":
    main()
