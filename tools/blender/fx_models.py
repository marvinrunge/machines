"""The remake's solid projectiles and the things the weapons leave lying about, built in Blender from primitives:

    blender -b --factory-startup -P tools/blender/fx_models.py -- <classic folder> [name ...]

For each name (all of them without names) it reads the original (classic/models/fx/<name>.json) only to report its
size beside the new one, builds the model anew, bakes its maps and writes classic/remake/fx/<name>.json, in the
export format with the maps in classic/remake/fx/<name>/ (git-ignored like the rest of classic/). The game shows
them in the remake looks in place of the originals (ClassicAssets.EffectMesh).

The models keep the originals' size, axes and origin: x is the nose, z is up, the tail (where the exhaust and the
vapour trail start) at x = 0, the mine and the wreck stand on z = 0. Hulls are solids of revolution with their seams,
rings and nozzles in the profile; fins, hatches and plates are real geometry; the paint wears moderately at the
edges, soot darkens the tail. Nothing comes from the game copy but the originals' proportions, read off by eye.

Maps, 1k: albedo (with the baked occlusion), normal (tangent space, OpenGL), orm (occlusion, roughness, metal) and,
for glowing parts, emission. Baked in Cycles, on the GPU when there is one.
"""
import json
import math
import os
import random
import sys

import bmesh
import bpy
import numpy as np
from mathutils import Matrix, Vector

SIZE = 1024
BARE_METAL = (0.52, 0.52, 0.53)


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:]
    return os.path.abspath(args[0]), args[1:]


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    prefs = bpy.context.preferences.addons["cycles"].preferences
    for kind in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
        try:
            prefs.compute_device_type = kind
            prefs.get_devices()
            if any(d.type == kind for d in prefs.devices):
                for d in prefs.devices:
                    d.use = d.type == kind
                scene.cycles.device = "GPU"
                break
        except TypeError:
            continue
    return scene


# ---- materials -------------------------------------------------------------------------------------------------

class Shader:
    """A material built from a spec, which can show each of its channels (albedo, roughness, metal, emission) as
    plain emission for baking, and its bump as the normal."""

    def __init__(self, spec, size, soot):
        self.spec = spec
        self.material = bpy.data.materials.new(spec.get("name", "part"))
        self.material.use_nodes = True
        self.tree = self.material.node_tree
        self.bsdf = next(n for n in self.tree.nodes if n.type == "BSDF_PRINCIPLED")
        self.out = next(n for n in self.tree.nodes if n.type == "OUTPUT_MATERIAL")
        self.emit = self.tree.nodes.new("ShaderNodeEmission")
        self.target = self.tree.nodes.new("ShaderNodeTexImage")
        self.channels = {}
        self.build(size, soot)

    def node(self, kind, **settings):
        node = self.tree.nodes.new(kind)
        for key, value in settings.items():
            setattr(node, key, value)
        return node

    def link(self, socket, into):
        if isinstance(socket, (int, float)):
            into.default_value = socket
        elif isinstance(socket, tuple):
            into.default_value = (*socket[:3], 1) if len(into.default_value) == 4 else socket
        else:
            self.tree.links.new(socket, into)

    def math(self, op, a, b=0.0, clamp=False):
        node = self.node("ShaderNodeMath", operation=op, use_clamp=clamp)
        self.link(a, node.inputs[0])
        self.link(b, node.inputs[1])
        return node.outputs[0]

    def mix(self, fac, a, b):
        node = self.node("ShaderNodeMix", data_type="RGBA")
        self.link(fac, node.inputs[0])
        self.link(a, node.inputs[6])
        self.link(b, node.inputs[7])
        return node.outputs[2]

    def mixf(self, fac, a, b):
        node = self.node("ShaderNodeMix", data_type="FLOAT")
        self.link(fac, node.inputs[0])
        self.link(a, node.inputs[2])
        self.link(b, node.inputs[3])
        return node.outputs[0]

    def ramp(self, value, low, high):
        node = self.node("ShaderNodeMapRange", interpolation_type="SMOOTHSTEP", clamp=True)
        self.link(value, node.inputs["Value"])
        node.inputs["From Min"].default_value = low
        node.inputs["From Max"].default_value = high
        return node.outputs["Result"]

    def noise(self, coord, scale, detail=4.0):
        node = self.node("ShaderNodeTexNoise")
        self.link(coord, node.inputs["Vector"])
        node.inputs["Scale"].default_value = scale
        node.inputs["Detail"].default_value = detail
        return node.outputs["Fac"]

    def build(self, size, soot):
        s = self.spec
        coord = self.node("ShaderNodeTexCoord").outputs["Object"]
        colour = s.get("colour", (0.5, 0.5, 0.5))
        kind = s.get("kind", "paint")
        if kind == "glow":
            albedo, rough, metal = (0.04, 0.04, 0.04), 0.5, 0.0
            emission = s["colour"]
            self.channels = {"albedo": albedo, "rough": rough, "metal": metal, "emit": emission}
            for key, into in (("albedo", "Base Color"), ("rough", "Roughness"), ("metal", "Metallic")):
                self.link(self.channels[key], self.bsdf.inputs[into])
            self.link(emission, self.bsdf.inputs["Emission Color"])
            self.bsdf.inputs["Emission Strength"].default_value = 1
            return

        # A pattern on the paint: stripes along a direction, ragged by noise (hazard stripes, tiger stripes, bands).
        paint = colour
        if "stripes" in s:
            other, frequency, direction, duty, ragged = s["stripes"]
            along = self.node("ShaderNodeVectorMath", operation="DOT_PRODUCT")
            self.link(coord, along.inputs[0])
            along.inputs[1].default_value = direction
            wave = along.outputs["Value"]
            if ragged:
                wave = self.math("ADD", wave, self.math("MULTIPLY", self.noise(coord, 3.0 / size), ragged))
            stripe = self.math("LESS_THAN", self.math("FRACT", self.math("MULTIPLY", wave, frequency)), duty)
            paint = self.mix(stripe, colour, other)

        # Grime: the paint a little uneven, darker in streaks.
        grime = self.ramp(self.noise(coord, 6.0 / size, 6.0), 0.3, 0.75)
        paint = self.mix(self.math("MULTIPLY", grime, 0.25 * s.get("grime", 1.0)), paint, (0.08, 0.07, 0.06))

        # Chipped edges: where the bevelled normal turns from the true one, broken up by noise.
        wear = s.get("wear", 0.0)
        mask = 0.0
        if wear > 0:
            bevel = self.node("ShaderNodeBevel", samples=8)
            bevel.inputs["Radius"].default_value = size * 0.012
            geometry = self.node("ShaderNodeNewGeometry")
            dot = self.node("ShaderNodeVectorMath", operation="DOT_PRODUCT")
            self.link(bevel.outputs["Normal"], dot.inputs[0])
            self.link(geometry.outputs["Normal"], dot.inputs[1])
            edge = self.math("SUBTRACT", 1.0, dot.outputs["Value"])
            broken = self.math("MULTIPLY", self.math("MULTIPLY", edge, 14.0), self.noise(coord, 14.0 / size, 8.0))
            # Scuffs away from the edges too, sparse.
            scuffs = self.ramp(self.noise(coord, 22.0 / size, 10.0), 0.68, 0.74)
            mask = self.math("MULTIPLY", self.math("ADD", self.ramp(broken, 0.35, 0.55), self.math("MULTIPLY", scuffs, 0.5)), wear, clamp=True)
        bare = s.get("bare", BARE_METAL)
        albedo = self.mix(mask, paint, bare) if wear > 0 else paint

        # Soot from the exhaust over the tail.
        if soot:
            x = self.node("ShaderNodeSeparateXYZ")
            self.link(coord, x.inputs[0])
            sooty = self.math("SUBTRACT", 1.0, self.ramp(x.outputs["X"], soot[0], soot[1]))
            albedo = self.mix(self.math("MULTIPLY", sooty, 0.75 * s.get("soot", 1.0)), albedo, (0.03, 0.03, 0.03))

        rough = self.mixf(mask, s.get("rough", 0.5), 0.32) if wear > 0 else s.get("rough", 0.5)
        metal = self.mixf(mask, s.get("metal", 0.0), 1.0) if wear > 0 else s.get("metal", 0.0)
        self.channels = {"albedo": albedo, "rough": rough, "metal": metal, "emit": (0.0, 0.0, 0.0)}
        self.link(albedo, self.bsdf.inputs["Base Color"])
        self.link(rough, self.bsdf.inputs["Roughness"])
        self.link(metal, self.bsdf.inputs["Metallic"])

        # The surface's grain and the chipped paint's step, as bump.
        bump = self.node("ShaderNodeBump")
        bump.inputs["Strength"].default_value = 0.25
        bump.inputs["Distance"].default_value = size * 0.002
        height = self.math("ADD", self.math("MULTIPLY", self.noise(coord, 90.0 / size, 2.0), 0.3), self.math("MULTIPLY", mask, -1.0))
        self.link(height, bump.inputs["Height"])
        self.link(bump.outputs["Normal"], self.bsdf.inputs["Normal"])

    def show(self, channel):
        """Shows one channel as emission (for an EMIT bake), or the material itself (None)."""
        if channel is None:
            self.tree.links.new(self.bsdf.outputs[0], self.out.inputs["Surface"])
            return
        value = self.channels[channel]
        socket = self.emit.inputs["Color"]
        for link in list(socket.links):
            self.tree.links.remove(link)
        if isinstance(value, float) or isinstance(value, int):
            socket.default_value = (value, value, value, 1)
        elif isinstance(value, tuple):
            socket.default_value = (*value[:3], 1)
        else:
            self.tree.links.new(value, socket)
        self.emit.inputs["Strength"].default_value = 1
        self.tree.links.new(self.emit.outputs[0], self.out.inputs["Surface"])

    def bake_into(self, image):
        self.target.image = image
        self.tree.nodes.active = self.target


# ---- geometry --------------------------------------------------------------------------------------------------

class Model:
    """A model built piece by piece into one mesh: solids of revolution about x, fins, plates and boxes."""

    def __init__(self, name, size, soot=None):
        self.name = name
        self.size = size
        self.soot = soot
        self.bm = bmesh.new()
        self.specs = []

    def material(self, **spec):
        self.specs.append(spec)
        return len(self.specs) - 1

    def _close(self, faces):
        bmesh.ops.recalc_face_normals(self.bm, faces=faces)

    def lathe(self, profile, segments, paint, matrix=None, start=0.0):
        """A solid of revolution about x through profile points (x, r), r = 0 closing it at the axis. paint is a slot
        or a function (x, angle) -> slot of a face's middle."""
        rings = []
        for x, r in profile:
            if r <= 1e-6:
                rings.append([self.bm.verts.new((x, 0, 0))])
            else:
                rings.append([self.bm.verts.new((x, r * math.cos(start + k * math.tau / segments),
                                                 r * math.sin(start + k * math.tau / segments))) for k in range(segments)])
        faces = []
        for a, b in zip(rings, rings[1:]):
            if len(a) == 1 and len(b) == 1:
                continue
            for k in range(segments):
                k1 = (k + 1) % segments
                if len(a) == 1:
                    verts = (a[0], b[k1], b[k])
                elif len(b) == 1:
                    verts = (a[k], a[k1], b[0])
                else:
                    verts = (a[k], a[k1], b[k1], b[k])
                if len(set(verts)) < 3:
                    continue
                face = self.bm.faces.new(verts)
                middle = sum((v.co for v in verts), Vector()) / len(verts)
                face.material_index = paint(middle.x, math.atan2(middle.z, middle.y)) if callable(paint) else paint
                faces.append(face)
        self._close(faces)
        if matrix is not None:
            bmesh.ops.transform(self.bm, matrix=matrix, verts=list({v for f in faces for v in f.verts}))
        return faces

    def fin(self, outline, angle, thickness, slot, round_edges=0.42, tip=0.5, segments=1):
        """A fin standing out from the axis at angle: outline points (x, distance from the axis), counter-clockwise,
        thinning towards its outer edge (tip times the root's thickness), its rim rounded."""
        normal = Vector((0, -math.sin(angle), math.cos(angle)))
        radial = Vector((0, math.cos(angle), math.sin(angle)))
        root = min(s for _, s in outline)
        reach = max(s for _, s in outline) - root or 1

        def corner(x, s, side):
            t = thickness * (1 - (1 - tip) * (s - root) / reach) / 2
            return Vector((x, 0, 0)) + radial * s + normal * side * t

        front = [self.bm.verts.new(corner(x, s, 1)) for x, s in outline]
        back = [self.bm.verts.new(corner(x, s, -1)) for x, s in outline]
        faces = [self.bm.faces.new(front), self.bm.faces.new(list(reversed(back)))]
        n = len(outline)
        for k in range(n):
            faces.append(self.bm.faces.new((front[k], back[k], back[(k + 1) % n], front[(k + 1) % n])))
        for f in faces:
            f.material_index = slot
        self._close(faces)
        if round_edges:
            rim = [e for e in {e for f in faces[:2] for e in f.edges}]
            made = bmesh.ops.bevel(self.bm, geom=rim, offset=thickness * round_edges, segments=segments, profile=0.5,
                                   affect="EDGES", clamp_overlap=True)
            # The rounded rim is the fin's own (a bevel's new faces would take the first material).
            for f in made["faces"]:
                f.material_index = slot
        return faces

    def plate(self, x0, x1, a0, a1, radius, height, slot, steps=4, matrix=None):
        """A curved plate on a hull of that radius, from x0 to x1 and angle a0 to a1, standing out by height (a
        hatch, a conduit, a window), sunk a little into the hull so no gap shows."""
        inner, outer = radius - height, radius + height
        grid = []
        for r in (inner, outer):
            grid.append([[self.bm.verts.new((x, r * math.cos(a), r * math.sin(a)))
                          for a in np.linspace(a0, a1, steps + 1)] for x in (x0, x1)])
        faces = []
        lo, hi = grid
        for layer in (lo, hi):
            for k in range(steps):
                faces.append(self.bm.faces.new((layer[0][k], layer[0][k + 1], layer[1][k + 1], layer[1][k])))
        for k in range(steps):
            for end in (0, 1):
                faces.append(self.bm.faces.new((lo[end][k], lo[end][k + 1], hi[end][k + 1], hi[end][k])))
        for end in (0, -1):
            faces.append(self.bm.faces.new((lo[0][end], lo[1][end], hi[1][end], hi[0][end])))
        for f in faces:
            f.material_index = slot
        self._close(faces)
        if matrix is not None:
            bmesh.ops.transform(self.bm, matrix=matrix, verts=list({v for f in faces for v in f.verts}))
        return faces

    def box(self, size, slot, matrix, bevel=0.0):
        made = bmesh.ops.create_cube(self.bm, size=1.0, matrix=matrix @ Matrix.Diagonal((*size, 1)))
        faces = list({f for v in made["verts"] for f in v.link_faces})
        for f in faces:
            f.material_index = slot
        if bevel:
            edges = list({e for f in faces for e in f.edges})
            made = bmesh.ops.bevel(self.bm, geom=edges, offset=bevel, segments=1, profile=0.5, affect="EDGES", clamp_overlap=True)
            for f in made["faces"]:
                f.material_index = slot
        return faces

    def finish(self, scene):
        bmesh.ops.triangulate(self.bm, faces=self.bm.faces[:])
        mesh = bpy.data.meshes.new(self.name)
        self.bm.to_mesh(mesh)
        self.bm.free()
        for face in mesh.polygons:
            face.use_smooth = True
        mesh.set_sharp_from_angle(angle=math.radians(38))
        ob = bpy.data.objects.new(self.name, mesh)
        scene.collection.objects.link(ob)
        self.shaders = [Shader(spec, self.size, self.soot) for spec in self.specs]
        for shader in self.shaders:
            mesh.materials.append(shader.material)
        return ob


# ---- profiles ----------------------------------------------------------------------------------------------------

def ogive(x0, x1, r, steps=7, tip=0.0, blunt=1.0):
    """A nose from radius r at x0 to the tip at x1, rounded like a tangent ogive (blunt > 1 fuller)."""
    points = []
    for k in range(1, steps + 1):
        t = k / steps
        points.append((x0 + (x1 - x0) * t, tip + (r - tip) * (1 - t ** (1.6 * blunt)) ** 0.55 if k < steps else tip))
    return points


def groove(x, r, width=0.012, depth=0.006):
    """A seam round the hull: a narrow ring cut into it."""
    return [(x - width / 2, r), (x - width / 4, r - depth), (x + width / 4, r - depth), (x + width / 2, r)]


def nozzle(x, exit_r, throat_r, depth, lip=0.006):
    """The engine's nozzle at the tail, opening backwards at x: its bottom inside, its inner wall out to the lip,
    round the lip and onto the outside. Ends on the outside at the exit radius."""
    return [(x + depth, 0.0), (x + depth, throat_r * 0.6), (x + depth * 0.6, throat_r), (x, exit_r - lip),
            (x - lip * 0.5, exit_r - lip * 0.3), (x, exit_r)]


def bands(*edges):
    """A paint function over x: edges are (x, slot) pairs, each slot from its x onwards."""
    def paint(x, _angle):
        slot = edges[0][1]
        for at, s in edges:
            if x >= at:
                slot = s
        return slot
    return paint


def swept(root0, root1, tip0, tip1, base, reach):
    """A fin's outline: the root along the hull from root0 to root1 at distance base, the tip from tip0 to tip1 at
    distance reach."""
    return [(root0, base), (root1, base), (tip1, reach), (tip0, reach)]


# ---- the models --------------------------------------------------------------------------------------------------

def mis1():
    """Multi-launcher 1 (the missile assault tank's): a slim dart, yellow nose, black ring, grey body, black tail
    band and four small black fins."""
    m = Model("mis1", 1.42, soot=(0.0, 0.3))
    grey = m.material(colour=(0.62, 0.63, 0.62), rough=0.45, wear=0.4)
    black = m.material(colour=(0.035, 0.035, 0.035), rough=0.5, wear=0.5)
    yellow = m.material(colour=(0.85, 0.62, 0.05), rough=0.4, wear=0.35)
    metal = m.material(colour=(0.25, 0.24, 0.23), rough=0.35, metal=1.0, soot=0.4)
    r = 0.06
    profile = (nozzle(0.0, 0.045, 0.026, 0.05) + [(0.03, r)] + groove(0.14, r) + groove(0.58, r)
               + groove(1.03, r) + [(1.13, r)] + ogive(1.13, 1.42, r, steps=8))
    m.lathe(profile, 16, bands((-1, metal), (0.035, black), (0.14, grey), (1.03, black), (1.12, yellow)))
    for k in range(4):
        m.fin(swept(0.03, 0.24, 0.05, 0.13, r - 0.008, 0.165), math.radians(45 + 90 * k), 0.014, black)
    return m


def mis2():
    """Multi-launcher 2 (the Goliath's): a white missile with black rings round its middle and behind its nose,
    four big swept fins at the tail and four small canards behind the nose."""
    m = Model("mis2", 2.14, soot=(0.0, 0.45))
    white = m.material(colour=(0.82, 0.82, 0.8), rough=0.45, wear=0.35)
    black = m.material(colour=(0.035, 0.035, 0.035), rough=0.5, wear=0.4)
    pink = m.material(colour=(0.62, 0.45, 0.46), rough=0.45, wear=0.3)
    metal = m.material(colour=(0.22, 0.21, 0.2), rough=0.35, metal=1.0)
    r = 0.11
    profile = (nozzle(0.0, 0.08, 0.05, 0.08) + [(0.04, r)] + groove(0.5, r) + [(0.98, r), (0.985, r + 0.006), (1.36, r + 0.006), (1.365, r)]
               + groove(1.5, r) + [(1.6, r)] + ogive(1.6, 2.14, r, steps=9))
    m.lathe(profile, 20, bands((-1, metal), (0.045, white), (0.98, black), (1.05, pink), (1.12, black), (1.2, pink),
                               (1.28, black), (1.37, white), (1.62, black), (1.7, white), (1.78, black), (1.86, white)))
    for k in range(4):
        m.fin(swept(0.03, 0.55, 0.05, 0.22, r - 0.01, 0.52), math.radians(90 * k), 0.022, white)
        m.fin(swept(1.38, 1.5, 1.4, 1.46, r - 0.01, 0.2), math.radians(90 * k), 0.012, black)
    return m


def mis3():
    """Multi-launcher 3 (Knight 4's): a small ribbed motor pushing a big double-cone warhead painted in black and
    white hazard stripes, with a raised rim where it is widest."""
    m = Model("mis3", 2.28, soot=(-0.13, 0.25))
    hazard = m.material(colour=(0.8, 0.8, 0.78), rough=0.45, wear=0.35,
                        stripes=((0.03, 0.03, 0.03), 3.2, (0.7, 0.0, 0.7), 0.5, 0.0))
    black = m.material(colour=(0.05, 0.05, 0.05), rough=0.45, wear=0.45)
    metal = m.material(colour=(0.3, 0.29, 0.28), rough=0.35, metal=1.0)
    motor = 0.16
    profile = (nozzle(-0.13, 0.12, 0.07, 0.08) + [(-0.09, motor)] + groove(0.0, motor, 0.03, 0.015) + groove(0.12, motor, 0.03, 0.015)
               + groove(0.24, motor, 0.03, 0.015) + groove(0.36, motor, 0.03, 0.015) + [(0.5, motor), (0.52, 0.2), (1.44, 0.565),
               (1.47, 0.59), (1.53, 0.59), (1.56, 0.565)] + [(1.56 + (2.15 - 1.56) * t, 0.565 * (1 - t) ** 1.05) for t in (0.25, 0.5, 0.75, 0.92, 1.0)])
    m.lathe(profile, 24, bands((-1, metal), (-0.08, black), (0.51, hazard), (1.45, black), (1.555, hazard), (2.08, black)))
    for k in range(4):
        m.fin(swept(-0.08, 0.3, -0.06, 0.08, motor - 0.01, 0.3), math.radians(45 + 90 * k), 0.02, black)
    return m


def mis4():
    """Multi-launcher 4 (Knight 5's): a dark grey missile with two orange bands and a red nose, and four low
    strakes along its tail."""
    m = Model("mis4", 2.15, soot=(0.0, 0.4))
    dark = m.material(colour=(0.13, 0.13, 0.13), rough=0.5, wear=0.4)
    orange = m.material(colour=(0.85, 0.42, 0.04), rough=0.45, wear=0.35)
    red = m.material(colour=(0.6, 0.05, 0.03), rough=0.4, wear=0.3)
    metal = m.material(colour=(0.3, 0.29, 0.28), rough=0.35, metal=1.0)
    r = 0.19
    profile = (nozzle(0.0, 0.14, 0.08, 0.1) + [(0.05, r)] + groove(0.4, r) + [(0.7, r), (0.71, r + 0.008), (0.86, r + 0.008), (0.87, r)]
               + groove(1.05, r) + [(1.2, r), (1.21, r + 0.008), (1.36, r + 0.008), (1.37, r)] + [(1.7, r)] + ogive(1.7, 2.15, r, steps=8))
    m.lathe(profile, 20, bands((-1, metal), (0.055, dark), (0.7, orange), (0.875, dark), (1.2, orange), (1.375, dark), (1.72, red)))
    for k in range(4):
        m.fin(swept(0.04, 0.6, 0.06, 0.3, r - 0.01, 0.25), math.radians(45 + 90 * k), 0.018, dark)
    return m


def mis5():
    """Multi-launcher 5 (Commander 4's, Sentry 3's): a fat, blunt missile in orange with black and yellow tiger
    stripes and three big dark swept fins."""
    m = Model("mis5", 2.0, soot=(0.0, 0.35))
    tiger = m.material(colour=(0.82, 0.22, 0.03), rough=0.45, wear=0.3,
                       stripes=((0.03, 0.03, 0.02), 4.0, (0.75, 0.0, -0.66), 0.32, 0.18))
    yellow = m.material(colour=(0.9, 0.62, 0.06), rough=0.45, wear=0.3,
                        stripes=((0.03, 0.03, 0.02), 4.0, (0.75, 0.0, -0.66), 0.32, 0.18))
    dark = m.material(colour=(0.08, 0.08, 0.08), rough=0.5, wear=0.45)
    metal = m.material(colour=(0.25, 0.24, 0.23), rough=0.35, metal=1.0)
    r = 0.6
    profile = (nozzle(0.0, 0.32, 0.2, 0.15) + [(0.03, 0.4), (0.18, 0.56), (0.3, r)] + groove(0.7, r, 0.02, 0.01)
               + [(1.35, r)] + ogive(1.35, 1.99, r, steps=8, tip=0.0, blunt=2.2))
    m.lathe(profile, 24, bands((-1, metal), (0.02, dark), (0.24, tiger), (1.35, yellow)))
    for k in range(3):
        m.fin(swept(0.08, 1.3, 0.04, 0.5, r - 0.03, 1.25), math.radians(90 + 120 * k), 0.06, dark, segments=2)
    return m


def mis6():
    """Multi-launcher 6 (Sentry 4's SAM): a dark missile with glowing blue windows down its sides, a white nose cone,
    three big delta wings and four small white tail fins."""
    m = Model("mis6", 2.93, soot=(-0.12, 0.35))
    dark = m.material(colour=(0.07, 0.075, 0.085), rough=0.4, wear=0.4)
    white = m.material(colour=(0.82, 0.82, 0.8), rough=0.4, wear=0.3)
    frame = m.material(colour=(0.2, 0.21, 0.23), rough=0.35, metal=1.0, wear=0.2)
    glow = m.material(kind="glow", colour=(0.25, 0.55, 1.0))
    metal = m.material(colour=(0.25, 0.24, 0.23), rough=0.35, metal=1.0)
    r = 0.34
    profile = (nozzle(-0.12, 0.22, 0.13, 0.12) + [(-0.07, 0.27), (0.2, r)] + groove(0.45, r) + groove(1.0, r)
               + [(1.84, r), (1.86, 0.3), (1.88, 0.3)] + ogive(1.88, 2.81, 0.3, steps=9))
    m.lathe(profile, 24, bands((-1, metal), (-0.06, dark), (1.87, white)))
    for a in (math.radians(15), math.radians(165)):
        m.plate(0.55, 1.55, a - 0.38, a + 0.38, r, 0.018, frame, steps=6)
        m.plate(0.6, 1.5, a - 0.3, a + 0.3, r + 0.01, 0.012, glow, steps=6)
    for k in range(3):
        m.fin(swept(0.93, 1.84, 0.93, 1.05, r - 0.02, 1.0), math.radians(90 + 120 * k), 0.035, dark)
    for k in range(4):
        m.fin(swept(-0.1, 0.3, -0.1, 0.0, 0.25, 0.42), math.radians(45 + 90 * k), 0.016, white)
    return m


def mis7():
    """Multi-launcher 7 (the Gorilla's): a slim silver dart with a long needle nose, four long angular fins and a
    red glowing ring at its tail."""
    m = Model("mis7", 1.23, soot=(0.0, 0.2))
    silver = m.material(colour=(0.7, 0.71, 0.72), rough=0.3, metal=1.0, wear=0.0)
    dark = m.material(colour=(0.06, 0.06, 0.065), rough=0.4, wear=0.4)
    glow = m.material(kind="glow", colour=(1.0, 0.12, 0.05))
    metal = m.material(colour=(0.25, 0.24, 0.23), rough=0.35, metal=1.0)
    r = 0.075
    profile = (nozzle(0.0, 0.055, 0.032, 0.05) + [(0.03, r), (0.08, r), (0.085, r + 0.006), (0.12, r + 0.006), (0.125, r)]
               + groove(0.45, r) + [(0.7, r)] + ogive(0.7, 1.23, r, steps=9))
    m.lathe(profile, 16, bands((-1, metal), (0.035, silver), (0.08, glow), (0.125, silver)))
    for k in range(4):
        m.fin([(0.02, r - 0.008), (0.85, r - 0.008), (0.3, 0.2), (0.06, 0.2)], math.radians(45 + 90 * k), 0.014, dark)
    return m


def lmiss():
    """The large missile (Launcher 4's): a long silver missile with a red nose and tail ring, a ribbed black-and-white
    section behind the nose, four swept fins at the tail and four small ones at its middle."""
    m = Model("lmiss", 7.8, soot=(-3.15, -1.6))
    silver = m.material(colour=(0.6, 0.61, 0.61), rough=0.4, wear=0.35)
    white = m.material(colour=(0.82, 0.82, 0.8), rough=0.45, wear=0.35)
    black = m.material(colour=(0.04, 0.04, 0.04), rough=0.5, wear=0.4)
    red = m.material(colour=(0.62, 0.04, 0.03), rough=0.4, wear=0.3)
    metal = m.material(colour=(0.25, 0.24, 0.23), rough=0.35, metal=1.0)
    r = 0.34
    ribs = []
    for k in range(7):
        x = 1.3 + k * 0.16
        ribs += [(x, r), (x + 0.01, r + 0.02), (x + 0.07, r + 0.02), (x + 0.08, r)]
    profile = (nozzle(-3.15, 0.27, 0.16, 0.2) + [(-3.05, r)] + groove(-2.3, r) + groove(-1.0, r) + groove(0.3, r)
               + ribs + [(2.5, r)] + groove(2.9, r) + [(3.55, r)] + ogive(3.55, 4.68, r, steps=10))
    def paint(x, _angle):
        if x < -3.04:
            return metal
        if x < -2.85:
            return red
        if 1.3 <= x < 2.42:
            return black if int((x - 1.3) / 0.08) % 2 == 0 else white
        return red if x >= 3.55 else silver
    m.lathe(profile, 24, paint)
    for k in range(4):
        m.fin(swept(-3.0, -1.9, -2.9, -2.45, r - 0.02, 0.93), math.radians(45 + 90 * k), 0.05, white)
        m.fin(swept(0.2, 0.9, 0.4, 0.75, r - 0.02, 0.62), math.radians(45 + 90 * k), 0.035, white)
    return m


def nmisea():
    """The nuclear missile: twenty metres of olive-drab rocket, a white section and a black nose cone, panel seams,
    a conduit along its back, hatches, four fins at the tail and four at the nose with lamps at their tips."""
    m = Model("nmisea", 20.8, soot=(0.0, 3.0))
    olive = m.material(colour=(0.24, 0.26, 0.16), rough=0.55, wear=0.3)
    white = m.material(colour=(0.78, 0.78, 0.75), rough=0.5, wear=0.3)
    black = m.material(colour=(0.035, 0.035, 0.035), rough=0.45, wear=0.35)
    dark = m.material(colour=(0.12, 0.12, 0.12), rough=0.45, wear=0.4)
    yellow = m.material(colour=(0.85, 0.65, 0.05), rough=0.45, wear=0.3)
    metal = m.material(colour=(0.25, 0.24, 0.23), rough=0.35, metal=1.0)
    lamp = m.material(kind="glow", colour=(1.0, 0.25, 0.1))
    r = 0.8
    profile = nozzle(0.0, 0.62, 0.38, 0.6, lip=0.03) + [(0.15, 0.7), (0.6, r)]
    for x in (2.0, 3.6, 5.2, 6.8, 8.4, 10.0, 11.0, 12.6, 14.2):
        profile += groove(x, r, 0.06, 0.03)
    profile += [(16.6, r), (16.65, r + 0.03), (16.85, r + 0.03), (16.9, r)] + ogive(16.9, 20.79, r, steps=12)
    m.lathe(profile, 32, bands((-1, metal), (0.1, dark), (3.6, olive), (11.0, white), (14.2, black), (16.65, yellow),
                               (16.9, black)))
    # A conduit along the top and hatches on the sides.
    m.plate(2.2, 14.0, math.radians(84), math.radians(96), r, 0.05, dark, steps=2)
    for x in (4.2, 7.4, 12.0):
        for side in (-1, 1):
            m.plate(x, x + 0.9, math.radians(side * 15) - 0.22, math.radians(side * 15) + 0.22, r, 0.02, olive if x < 11 else white, steps=4)
    for k in range(4):
        angle = math.radians(45 + 90 * k)
        m.fin(swept(0.3, 2.6, 0.5, 1.5, r - 0.05, 1.85), angle, 0.14, dark, segments=2)
        m.fin(swept(15.0, 16.5, 15.4, 16.2, r - 0.05, 1.5), angle, 0.1, dark)
        tip = Matrix.Rotation(angle, 4, "X") @ Matrix.Translation((15.8, 1.5, 0))
        m.lathe([(-0.1, 0.0), (-0.1, 0.06), (0.1, 0.06), (0.1, 0.0)], 8, lamp, matrix=tip @ Matrix.Rotation(math.pi / 2, 4, "Z"))
    return m


def stingmetal():
    """The wasp's metal sting: a red, segmented barb like a hornet's sting, swelling behind a black spike, three
    barbs swept back from the spike and a glowing tail."""
    m = Model("stingmetal", 1.25, soot=None)
    red = m.material(colour=(0.45, 0.03, 0.02), rough=0.3, metal=0.6, wear=0.25, bare=(0.3, 0.05, 0.04))
    black = m.material(colour=(0.03, 0.03, 0.03), rough=0.25, metal=0.9, wear=0.0)
    glow = m.material(kind="glow", colour=(1.0, 0.75, 0.4))
    # Swelling to its widest a third of the way along, in four plates each overlapping the next like a wasp's.
    def girth(x):
        return 0.1 + 0.075 * math.sin(math.pi * min(1.0, (x + 0.12) / 0.95))
    profile = [(-0.16, 0.0), (-0.16, 0.05), (-0.11, 0.085), (-0.02, girth(-0.02))]
    for k in range(4):
        x = 0.02 + k * 0.18
        profile += [(x, girth(x) - 0.006), (x + 0.03, girth(x) + 0.004), (x + 0.17, girth(x + 0.17))]
    profile += [(0.78, girth(0.78) - 0.01), (0.84, 0.085), (0.92, 0.05), (1.0, 0.022), (1.09, 0.0)]
    m.lathe(profile, 14, bands((-1, glow), (-0.02, red), (0.8, black)))
    for k in range(3):
        m.fin([(0.82, 0.07), (0.95, 0.03), (0.78, 0.17)], math.radians(90 + 120 * k), 0.025, black, round_edges=0.3)
    return m


def lmine():
    """The land mine: a low red hexagonal case with a chamfered top, a raised plate in yellow and black hazard stripes,
    a red lamp in a dome at its middle and six bolts."""
    m = Model("lmine", 1.46, soot=None)
    red = m.material(colour=(0.55, 0.05, 0.03), rough=0.45, wear=0.4)
    hazard = m.material(colour=(0.85, 0.65, 0.05), rough=0.45, wear=0.35,
                        stripes=((0.03, 0.03, 0.03), 9.0, (0.7, 0.7, 0.0), 0.5, 0.0))
    metal = m.material(colour=(0.35, 0.34, 0.33), rough=0.35, metal=1.0, wear=0.0)
    lamp = m.material(kind="glow", colour=(1.0, 0.08, 0.04))
    up = Matrix.Rotation(-math.pi / 2, 4, "Y")
    # Six corners, one pointing along x as the original's.
    corners = math.radians(30)
    case = [(0.0, 0.0), (0.0, 0.69), (0.025, 0.73), (0.08, 0.73), (0.115, 0.66), (0.12, 0.64), (0.12, 0.4), (0.125, 0.38)]
    m.lathe(case + [(0.125, 0.0)], 6, red, matrix=up, start=corners)
    m.lathe([(0.115, 0.0), (0.115, 0.35), (0.145, 0.34), (0.15, 0.33), (0.15, 0.0)], 6, hazard, matrix=up, start=corners)
    m.lathe([(0.145, 0.0), (0.145, 0.1), (0.155, 0.1), (0.16, 0.085)] + [(0.16 + 0.04 * math.sin(t), 0.085 * math.cos(t)) for t in np.linspace(0.3, math.pi / 2, 5)],
            12, lambda x, a: lamp if x > 0.16 else metal, matrix=up)
    for k in range(6):
        a = math.radians(30 + 60 * k)
        at = Matrix.Translation((0.52 * math.cos(a), 0.52 * math.sin(a), 0.12))
        m.lathe([(0.0, 0.0), (0.0, 0.03), (0.015, 0.03), (0.022, 0.02), (0.022, 0.0)], 6, metal, matrix=at @ up)
    return m


def debris():
    """A machine's wreck (the debris left for scavengers): a heap of torn plates, girders, pipes and a wheel, three and
    a half metres across, smouldering in places."""
    m = Model("debris", 3.7, soot=None)
    rust = m.material(colour=(0.2, 0.12, 0.08), rough=0.75, wear=0.5, bare=(0.35, 0.33, 0.31))
    paint = m.material(colour=(0.4, 0.07, 0.05), rough=0.55, wear=0.6, bare=(0.3, 0.29, 0.28))
    grey = m.material(colour=(0.28, 0.28, 0.27), rough=0.5, metal=0.8, wear=0.3)
    char = m.material(colour=(0.04, 0.035, 0.03), rough=0.9, wear=0.2, bare=(0.15, 0.13, 0.12))
    ember = m.material(kind="glow", colour=(1.0, 0.42, 0.08))
    rng = random.Random(7)

    def where(spread, height):
        a = rng.uniform(0, math.tau)
        d = spread * math.sqrt(rng.random())
        return Vector((d * math.cos(a), d * math.sin(a), height))

    def turned(at, tilt):
        return (Matrix.Translation(at) @ Matrix.Rotation(rng.uniform(0, math.tau), 4, "Z")
                @ Matrix.Rotation(rng.uniform(-tilt, tilt), 4, "X") @ Matrix.Rotation(rng.uniform(-tilt, tilt), 4, "Y"))

    # A charred bed under it all.
    m.lathe([(0.0, 0.0), (0.0, 1.85), (0.05, 1.7), (0.12, 1.2), (0.18, 0.4), (0.2, 0.0)], 12,
            lambda x, a: char, matrix=Matrix.Rotation(-math.pi / 2, 4, "Y") @ Matrix.Diagonal((1, 1, 0.9, 1)))
    # Torn plates, some bent in two.
    for k in range(9):
        size = Vector((rng.uniform(0.6, 1.3), rng.uniform(0.4, 0.9), 0.05))
        at = where(1.3, rng.uniform(0.15, 0.5))
        slot = paint if k % 3 else rust
        frame = turned(at, 0.6)
        m.box(size, slot, frame, bevel=0.012)
        if k % 2:
            m.box(Vector((size.x * 0.7, size.y, 0.05)), slot,
                  frame @ Matrix.Translation((size.x * 0.5, 0, 0)) @ Matrix.Rotation(rng.uniform(0.5, 1.1), 4, "Y")
                  @ Matrix.Translation((size.x * 0.33, 0, 0)), bevel=0.012)
    # Girders: I-beams, sticking up out of the heap.
    for k in range(4):
        length = rng.uniform(1.0, 1.9)
        frame = turned(where(1.0, rng.uniform(0.3, 0.6)), 0.7)
        for z in (-0.09, 0.09):
            m.box(Vector((length, 0.16, 0.025)), grey, frame @ Matrix.Translation((0, 0, z)), bevel=0.006)
        m.box(Vector((length, 0.025, 0.18)), grey, frame, bevel=0.006)
    # Pipes and a hose.
    for k in range(4):
        length = rng.uniform(0.8, 1.6)
        r = rng.uniform(0.05, 0.1)
        frame = turned(where(1.2, rng.uniform(0.2, 0.5)), 0.5)
        m.lathe([(-length / 2, 0.0), (-length / 2, r * 0.7), (-length / 2 + 0.01, r)] + groove(0.0, r, 0.03, 0.01)
                + [(length / 2 - 0.01, r), (length / 2, r * 0.7), (length / 2, 0.0)], 10, rust if k % 2 else grey, matrix=frame)
    # A wheel lying on its side.
    hub = Matrix.Translation(where(0.8, 0.42)) @ Matrix.Rotation(-math.pi / 2 + 0.3, 4, "Y")
    tyre = [(-0.12, 0.0), (-0.12, 0.2), (-0.13, 0.25)]
    tyre += [(0.13 * math.sin(t), 0.38 + 0.12 * math.cos(t)) for t in np.linspace(-math.pi / 2, math.pi / 2, 7)]
    tyre += [(0.13, 0.25), (0.12, 0.2), (0.12, 0.0)]
    m.lathe(tyre, 18, lambda x, a: char, matrix=hub)
    # Embers glowing in the bed.
    for k in range(7):
        at = where(1.1, 0.16)
        m.box(Vector((rng.uniform(0.15, 0.35), rng.uniform(0.1, 0.25), 0.06)), ember, turned(at, 0.2))
    return m


MODELS = {f.__name__: f for f in (mis1, mis2, mis3, mis4, mis5, mis6, mis7, lmiss, nmisea, stingmetal, lmine, debris)}


# ---- baking and writing ------------------------------------------------------------------------------------------

def unwrap(ob):
    bpy.ops.object.select_all(action="DESELECT")
    ob.select_set(True)
    bpy.context.view_layer.objects.active = ob
    bpy.ops.object.mode_set(mode="EDIT")
    bpy.ops.mesh.select_all(action="SELECT")
    bpy.ops.uv.smart_project(angle_limit=math.radians(55), island_margin=0.02)
    bpy.ops.object.mode_set(mode="OBJECT")


def bake(scene, ob, shaders, kind, channel, colour):
    image = bpy.data.images.new(f"{ob.name}-{channel or kind}", SIZE, SIZE, alpha=False)
    image.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    for shader in shaders:
        shader.show(channel)
        shader.bake_into(image)
    scene.cycles.samples = {"AO": 64, "NORMAL": 16}.get(kind, 32)
    options = {"normal_space": "TANGENT"} if kind == "NORMAL" else {}
    bpy.ops.object.bake(type=kind, use_clear=True, margin=12, margin_type='ADJACENT_FACES', **options)
    pixels = np.empty(SIZE * SIZE * 4, dtype=np.float32)
    image.pixels.foreach_get(pixels)
    pixels = pixels.reshape(SIZE, SIZE, 4)
    bpy.data.images.remove(image)
    return pixels


def save(pixels, path, colour):
    image = bpy.data.images.new("out", SIZE, SIZE, alpha=False)
    image.colorspace_settings.name = "sRGB" if colour else "Non-Color"
    pixels = pixels.copy()
    pixels[..., 3] = 1
    image.pixels.foreach_set(np.clip(pixels, 0, 1).ravel())
    image.filepath_raw = path
    image.file_format = "PNG"
    image.save()
    bpy.data.images.remove(image)


def write(classic, ob, model, glowing):
    folder = os.path.join(classic, "remake", "fx", model.name)
    os.makedirs(folder, exist_ok=True)
    scene = bpy.context.scene
    unwrap(ob)
    albedo = bake(scene, ob, model.shaders, "EMIT", "albedo", True)
    rough = bake(scene, ob, model.shaders, "EMIT", "rough", False)
    metal = bake(scene, ob, model.shaders, "EMIT", "metal", False)
    for shader in model.shaders:
        shader.show(None)
    occlusion = bake(scene, ob, model.shaders, "AO", None, False)
    normal = bake(scene, ob, model.shaders, "NORMAL", None, False)
    # The occlusion darkens the colour a little (the game has no occlusion map for these).
    ao = occlusion[..., :1]
    albedo[..., :3] *= 0.45 + 0.55 * ao
    orm = np.stack([ao[..., 0], rough[..., 0], metal[..., 0], np.ones_like(ao[..., 0])], axis=-1)
    maps = {}
    for key, pixels, colour in (("albedo", albedo, True), ("normal", normal, False), ("orm", orm, False)):
        save(pixels, os.path.join(folder, key + ".png"), colour)
        maps[key] = f"remake/fx/{model.name}/{key}.png"
    if glowing:
        emit = bake(scene, ob, model.shaders, "EMIT", "emit", True)
        save(emit, os.path.join(folder, "emission.png"), True)
        maps["emission"] = f"remake/fx/{model.name}/emission.png"

    mesh = ob.data
    mesh.calc_loop_triangles()
    uv = mesh.uv_layers.active.data
    normals = mesh.corner_normals
    part = {"texture": "", "diffuse": [1, 1, 1, 1], "ambient": [1, 1, 1], "emissive": [0, 0, 0],
            "positions": [], "normals": [], "uvs": [], "maps": maps}
    if glowing:
        part["emissionEnergy"] = 3.0
    for tri in mesh.loop_triangles:
        for loop in tri.loops:
            part["positions"] += [round(c, 5) for c in mesh.vertices[mesh.loops[loop].vertex_index].co]
            part["normals"] += [round(c, 4) for c in normals[loop].vector]
            part["uvs"] += [round(uv[loop].uv[0], 5), round(1 - uv[loop].uv[1], 5)]
    part["indices"] = list(range(len(part["positions"]) // 3))
    out = {"id": model.name, "parts": [part], "sprites": [], "spinners": [], "remake": {"triangles": len(mesh.loop_triangles)}}
    with open(os.path.join(classic, "remake", "fx", model.name + ".json"), "w") as f:
        json.dump(out, f, separators=(",", ":"))
    return len(mesh.loop_triangles)


def original_size(classic, name):
    path = os.path.join(classic, "models", "fx", name + ".json")
    if not os.path.exists(path):
        return None
    p = [c for part in json.load(open(path))["parts"] for c in part["positions"]]
    return [max(p[i::3]) - min(p[i::3]) for i in range(3)]


def main():
    classic, names = arguments()
    for name in names or MODELS:
        scene = reset()
        model = MODELS[name]()
        ob = model.finish(scene)
        glowing = any(spec.get("kind") == "glow" for spec in model.specs)
        triangles = write(classic, ob, model, glowing)
        size = [d for d in ob.dimensions]
        was = original_size(classic, name)
        print(f"{name}: {triangles} triangles, {size[0]:.2f} x {size[1]:.2f} x {size[2]:.2f} m"
              + (f" (original {was[0]:.2f} x {was[1]:.2f} x {was[2]:.2f})" if was else ""), flush=True)


if __name__ == "__main__":
    main()
