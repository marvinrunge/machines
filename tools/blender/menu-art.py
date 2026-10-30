"""The menus' art, rendered in Blender: the circuit board the menus lie on, the mask the board's shader runs its
signal pulses along, the chip that frames every panel and the parts that sit on the board. The original's menus are
640 by 480 pictures of such a board; these pieces let the remake lay the same look out at any window size.

    blender -b --factory-startup --python tools/blender/menu-art.py -- <out folder> [board] [chip] [props]

The board is a seamless 2048 px tile: everything crossing its edge comes back on the far side, so the menu can repeat
it. Its traces are routed on a 16 px grid in buses that bend by 45 degrees, out of small ICs and between vias.
board_traces.png holds per pixel whether a trace lies there (red), how far along its trace (green: 0 at its start,
1 at its end) and which bus it belongs to (blue), so a pulse can run along a whole bus at once."""
import math
import os
import random
import struct
import sys
import zlib

import bpy
import numpy as np
from mathutils import Vector

T = 2048            # the board tile, in pixels; one Blender unit is one pixel
P = 16              # trace pitch
W = 5.0             # trace width
CELL = 4            # the routing grid's cell, for keeping traces apart
N = T // CELL
CLEAR = W / 2 + 3   # every trace keeps this much room around it
TAN = math.tan(math.radians(22.5))
DIRS = [(1, 0), (1, 1), (0, 1), (-1, 1), (-1, 0), (-1, -1), (0, -1), (1, -1)]   # k: k * 45 degrees
LABELS = ["4410 - 3c", "MX-27", "OUT ~ 2f", "IN ~ 4b", "J4", "JP3", "TP7", "5V", "GND", "CLK", "88-21#", "REV 2",
          "3301 - 7a", "DATA 0-7", "VCC", "SYNC", "LINK", "BUS A", "BUS B", "X1", "ADR 12"]


def unit(k):
    dx, dy = DIRS[k % 8]
    length = math.hypot(dx, dy)
    return Vector((dx / length, dy / length))


def left(d):
    return Vector((-d.y, d.x))


class Board:
    """The board's layout: ICs, buses of traces, pads and vias, small parts and labels, all on a torus."""

    def __init__(self, seed):
        self.rng = random.Random(seed)
        self.occ = np.zeros((N, N), bool)
        self.lines = []      # (points, bus id)
        self.pads = []       # (position, radius, kind, phase, bus id): kind "pad", "via" or "square"
        self.ics = []        # (centre, length, width, pins per long side, pins per short side, rotated, name)
        self.parts = []      # (centre, rotated, kind)
        self.labels = []     # (position, text, size, rotated)
        self._discs = {}

    # ---- the routing grid ---------------------------------------------------------------------------------------

    def _disc(self, radius):
        if radius not in self._discs:
            r = int(math.ceil(radius / CELL))
            self._discs[radius] = np.array([(x, y) for x in range(-r, r + 1) for y in range(-r, r + 1)
                                            if (x * x + y * y) * CELL * CELL <= radius * radius])
        return self._discs[radius]

    def _cells(self, points, radius):
        """The grid cells within radius of the polyline (or of the single point)."""
        samples = []
        if len(points) == 1:
            samples.append(points[0])
        for a, b in zip(points, points[1:]):
            steps = max(1, int((b - a).length / (CELL / 2)))
            samples += [a.lerp(b, i / steps) for i in range(steps + 1)]
        centres = np.array([(int(p.x // CELL), int(p.y // CELL)) for p in samples])
        cells = (centres[:, None, :] + self._disc(radius)[None, :, :]).reshape(-1, 2) % N
        return cells

    def free(self, points, radius=CLEAR):
        cells = self._cells(points, radius)
        return not self.occ[cells[:, 1], cells[:, 0]].any()

    def take(self, points, radius=CLEAR):
        cells = self._cells(points, radius)
        self.occ[cells[:, 1], cells[:, 0]] = True

    def free_box(self, centre, half_x, half_y, margin):
        corners = [Vector((centre.x + sx * (half_x + margin), centre.y + sy * (half_y + margin))) for sx, sy in
                   ((-1, -1), (1, -1), (1, 1), (-1, 1), (-1, -1))]
        lines = [[corners[0].lerp(corners[3], i / 8), corners[1].lerp(corners[2], i / 8)] for i in range(9)]
        return all(self.free(line, CELL) for line in lines)

    def take_box(self, centre, half_x, half_y, margin):
        corners = [Vector((centre.x + sx * (half_x + margin), centre.y + sy * (half_y + margin))) for sx, sy in
                   ((-1, -1), (1, -1), (1, 1), (-1, 1))]
        for i in range(17):
            self.take([corners[0].lerp(corners[3], i / 16), corners[1].lerp(corners[2], i / 16)], CELL)

    # ---- buses ------------------------------------------------------------------------------------------------------

    @staticmethod
    def offset(leader, distance):
        """The polyline parallel to the leader, distance to its left, with mitred bends."""
        out = []
        for i, p in enumerate(leader):
            d_in = (p - leader[i - 1]).normalized() if i > 0 else None
            d_out = (leader[i + 1] - p).normalized() if i + 1 < len(leader) else None
            n1 = left(d_in) if d_in else left(d_out)
            n2 = left(d_out) if d_out else n1
            n = n1 + n2
            n = n / n.dot(n1)
            out.append(p + n * distance)
        return out

    def leader(self, start, program):
        points = [start.copy()]
        for k, steps in program:
            dx, dy = DIRS[k % 8]
            points.append(points[-1] + Vector((dx, dy)) * P * steps)
        return points

    def program(self, k, lines):
        """A bus's course: straight runs joined by 45 degree bends, as (direction, steps) pairs."""
        rng = self.rng
        need = 2 * (lines - 1) * P * TAN + P      # every inner trace keeps at least a pitch between two bends

        def steps(direction, low, high):
            length = P * (math.sqrt(2) if direction % 2 else 1)
            return max(rng.randint(low, high), math.ceil(need / length))

        program = [(k, steps(k, 4, 24))]
        for _ in range(rng.choice([0, 1, 1, 2, 2, 3, 4])):
            turn = rng.choice([-1, 1])
            diagonal = k + turn
            program.append((diagonal, steps(diagonal, 1, 2) if rng.random() < 0.75 else steps(diagonal, 4, 14)))
            k = diagonal + (turn if rng.random() < 0.65 else -turn)
            program.append((k, steps(k, 4, 28)))
        return program

    @staticmethod
    def shortest(k, lines):
        """The fewest steps a stretch between two bends takes, so that the bus's inner traces still run forward."""
        need = 2 * (lines - 1) * P * TAN + P
        return math.ceil(need / (P * (math.sqrt(2) if k % 2 else 1)))

    def lay(self, start, k, lines, bus, start_pads):
        """Routes a bus from start (its rightmost trace) as far as its course stays clear; returns whether it was laid."""
        program = self.program(k, lines)
        d = unit(k)
        # The first stretch off a pin is the pin's: the check starts a little beyond it.
        lead_in = d * (CLEAR + 1)
        while program:
            leader = self.leader(start, program)
            traces = [self.offset(leader, i * P) for i in range(lines)]
            checked = [[t[0] + lead_in] + t[1:] for t in traces]
            if all(self.free(t) for t in checked):
                break
            k_last, s_last = program[-1]
            program[-1] = (k_last, s_last - 1)
            if program[-1][1] <= 0 or len(program) > 1 and program[-1][1] < self.shortest(k_last, lines):
                program.pop()
        if not program or sum(s for _, s in program) < 5:
            return False
        ends = [t[-1] for t in traces]
        end_kind = self.rng.choice(["via", "via", "pad", "square"])
        radius = 6 if end_kind != "square" else 5.5
        if not all(self.free([e], radius + 2) for e in ends):
            return False
        for t in traces:
            self.take(t)
            self.lines.append((t, bus))
        for e in ends:
            self.pads.append((e, radius, end_kind, 1.0, bus))
            self.take([e], radius + 3)
        if start_pads:
            kind = self.rng.choice(["via", "pad"])
            for t in traces:
                self.pads.append((t[0], 6, kind, 0.0, bus))
                self.take([t[0]], 9)
        return True

    # ---- the board --------------------------------------------------------------------------------------------------

    def place_ic(self, index):
        rng = self.rng
        quad = rng.random() < 0.3
        long_pins = rng.randint(4, 8) if not quad else rng.randint(5, 8)
        short_pins = long_pins if quad else 0
        length = long_pins * P + P
        width = (short_pins * P + P) if quad else rng.choice([3, 4, 5]) * P
        rotated = rng.random() < 0.5
        half = Vector((length / 2, width / 2)) if not rotated else Vector((width / 2, length / 2))
        for _ in range(60):
            centre = Vector((rng.randrange(0, T, P) + (P / 2 if long_pins % 2 == 0 else 0), rng.randrange(0, T, P)))
            if self.free_box(centre, half.x + 12, half.y + 12, P):
                break
        else:
            return
        self.take_box(centre, half.x + 12, half.y + 12, 2)
        self.ics.append((centre, length, width, long_pins, short_pins, rotated, f"U{index}"))
        self.labels.append((centre + Vector((half.x + 10, half.y + 8)), f"U{index}", 15, False))
        # Each side's pins lead off in buses of two to five traces.
        sides = [(Vector((0, 1)), long_pins), (Vector((0, -1)), long_pins)] + \
                ([(Vector((1, 0)), short_pins), (Vector((-1, 0)), short_pins)] if quad else [])
        for normal, count in sides:
            if rotated:
                normal = Vector((-normal.y, normal.x))
            along = Vector((normal.y, -normal.x))          # the bus's right-to-left direction, seen going outward
            tip = centre + Vector((normal.x * (half.x + 12), normal.y * (half.y + 12)))
            first = tip - along * (count - 1) * P / 2
            pins = [first + along * i * P for i in range(count)]
            k = DIRS.index((int(round(normal.x)), int(round(normal.y))))
            i = 0
            while i < count:
                group = min(count - i, rng.randint(2, 5))
                # Seen going outward the bus's leader is its rightmost trace, the others to its left.
                self.lay(pins[i + group - 1], k, group, rng.random(), start_pads=False)
                i += group

    def place_part(self):
        rng = self.rng
        kind = rng.choice(["resistor", "resistor", "capacitor", "capacitor", "diode"])
        rotated = rng.random() < 0.5
        half = Vector((14, 7)) if not rotated else Vector((7, 14))
        centre = Vector((rng.uniform(0, T), rng.uniform(0, T)))
        if not self.free_box(centre, half.x, half.y, 6):
            return False
        self.take_box(centre, half.x, half.y, 4)
        self.parts.append((centre, rotated, kind))
        if rng.random() < 0.6:
            name = {"resistor": "R", "capacitor": "C", "diode": "D"}[kind] + str(rng.randint(1, 64))
            spot = centre + (Vector((0, half.y + 12)) if not rotated else Vector((half.x + 16, 0)))
            if self.free([spot], 10):
                self.labels.append((spot, name, 12, False))
                self.take([spot], 10)
        return True

    def place_label(self):
        text = self.rng.choice(LABELS)
        size = self.rng.choice([14, 16, 18])
        rotated = self.rng.random() < 0.25
        length = len(text) * size * 0.62
        centre = Vector((self.rng.uniform(0, T), self.rng.uniform(0, T)))
        half = Vector((length / 2, size / 2)) if not rotated else Vector((size / 2, length / 2))
        if not self.free_box(centre, half.x, half.y, 4):
            return False
        self.take_box(centre, half.x, half.y, 2)
        self.labels.append((centre, text, size, rotated))
        return True

    def build(self):
        rng = self.rng
        for i in range(1, 8):
            self.place_ic(i)
        for _ in range(900):
            if self.occ.mean() > 0.55:
                break
            lines = rng.choice([1, 2, 2, 3, 3, 4, 4, 5, 6, 8])
            k = rng.choice([0, 2, 4, 6] * 3 + [1, 3, 5, 7])
            start = Vector((rng.randrange(0, T, P), rng.randrange(0, T, P)))
            self.lay(start, k, lines, rng.random(), start_pads=True)
        for _ in range(500):
            self.place_part()
        for _ in range(60):
            self.place_label()
        print(f"board: {len(self.ics)} ICs, {len(self.lines)} traces, {len(self.pads)} pads, {len(self.parts)} parts, "
              f"{len(self.labels)} labels, {self.occ.mean():.0%} taken", flush=True)


# ---- the mask ------------------------------------------------------------------------------------------------------

def trace_mask(board):
    """red: trace coverage, green: how far along its trace, blue: its bus. Rows run bottom-up, as Blender's do."""
    cover = np.zeros((T, T), np.float32)
    phase = np.zeros((T, T), np.float32)
    bus = np.zeros((T, T), np.float32)

    def stamp(x0, y0, x1, y1, value):
        """Writes value(px, py) -> (coverage, phase) wherever it covers more than what is there, on the torus."""
        for ox in (-T, 0, T):
            for oy in (-T, 0, T):
                ax, ay, bx, by = int(math.floor(x0 + ox)), int(math.floor(y0 + oy)), int(math.ceil(x1 + ox)), int(math.ceil(y1 + oy))
                ax, ay, bx, by = max(ax, 0), max(ay, 0), min(bx, T), min(by, T)
                if ax >= bx or ay >= by:
                    continue
                px, py = np.meshgrid(np.arange(ax, bx) + 0.5 - ox, np.arange(ay, by) + 0.5 - oy)
                c, f, b = value(px, py)
                window = (slice(ay, by), slice(ax, bx))
                better = c > cover[window]
                cover[window] = np.where(better, c, cover[window])
                phase[window] = np.where(better, f, phase[window])
                bus[window] = np.where(better, b, bus[window])

    for points, b in board.lines:
        total = sum((q - p).length for p, q in zip(points, points[1:]))
        walked = 0.0
        for p, q in zip(points, points[1:]):
            seg = q - p
            length = seg.length

            def value(px, py, p=p, seg=seg, length=length, walked=walked, b=b):
                t = np.clip(((px - p.x) * seg.x + (py - p.y) * seg.y) / (length * length), 0, 1)
                d = np.hypot(px - (p.x + t * seg.x), py - (p.y + t * seg.y))
                return np.clip(W / 2 + 0.5 - d, 0, 1), (walked + t * length) / total, np.full_like(d, b)

            r = W / 2 + 1
            stamp(min(p.x, q.x) - r, min(p.y, q.y) - r, max(p.x, q.x) + r, max(p.y, q.y) + r, value)
            walked += length
    for centre, radius, _, f, b in board.pads:
        def value(px, py, centre=centre, radius=radius, f=f, b=b):
            d = np.hypot(px - centre.x, py - centre.y)
            return np.clip(radius + 0.5 - d, 0, 1), np.full_like(d, f), np.full_like(d, b)

        stamp(centre.x - radius - 1, centre.y - radius - 1, centre.x + radius + 1, centre.y + radius + 1, value)
    return np.stack([cover, phase, bus * (cover > 0)], axis=-1)


def save_png(path, rgb):
    """Writes the rows (bottom-up, values 0..1) as an 8-bit PNG exactly, with no colour management in between."""
    data = np.clip(np.round(rgb[::-1] * 255), 0, 255).astype(np.uint8)
    height, width, channels = data.shape
    raw = b"".join(b"\x00" + row.tobytes() for row in data)

    def chunk(kind, body):
        return struct.pack(">I", len(body)) + kind + body + struct.pack(">I", zlib.crc32(kind + body) & 0xFFFFFFFF)

    header = struct.pack(">IIBBBBB", width, height, 8, {3: 2, 4: 6}[channels], 0, 0, 0)
    with open(path, "wb") as f:
        f.write(b"\x89PNG\r\n\x1a\n" + chunk(b"IHDR", header) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b""))


# ---- Blender scene helpers -------------------------------------------------------------------------------------------

def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    for view in ("Standard", "Filmic", "AgX"):
        try:
            scene.view_settings.view_transform = view
            break
        except TypeError:
            continue
    return scene


def material(name, colour, metallic=0.0, roughness=0.5, noise=0.0, noise_scale=1.0, emission=None):
    m = bpy.data.materials.new(name)
    m.use_nodes = True
    nodes, links = m.node_tree.nodes, m.node_tree.links
    bsdf = next(n for n in nodes if n.type == "BSDF_PRINCIPLED")
    bsdf.inputs["Base Color"].default_value = (*colour, 1)
    bsdf.inputs["Metallic"].default_value = metallic
    bsdf.inputs["Roughness"].default_value = roughness
    if noise:
        tex = nodes.new("ShaderNodeTexNoise")
        tex.inputs["Scale"].default_value = noise_scale
        tex.inputs["Detail"].default_value = 8
        coords = nodes.new("ShaderNodeTexCoord")
        links.new(coords.outputs["Object"], tex.inputs["Vector"])
        ramp = nodes.new("ShaderNodeValToRGB")
        ramp.color_ramp.elements[0].color = (*[c * (1 - noise) for c in colour], 1)
        ramp.color_ramp.elements[1].color = (*[min(1, c * (1 + noise)) for c in colour], 1)
        links.new(tex.outputs["Fac"], ramp.inputs["Fac"])
        links.new(ramp.outputs["Color"], bsdf.inputs["Base Color"])
    if emission:
        bsdf.inputs["Emission Color"].default_value = (*emission, 1)
        bsdf.inputs["Emission Strength"].default_value = 1.0
    return m


class Meshes:
    """Geometry gathered per material, made into one object each."""

    def __init__(self):
        self.parts = {}

    def add(self, mat, verts, faces):
        v, f = self.parts.setdefault(mat, ([], []))
        base = len(v)
        v.extend(tuple(p) for p in verts)
        f.extend(tuple(i + base for i in face) for face in faces)

    def box(self, mat, centre, half, z0, z1):
        cx, cy = centre
        hx, hy = half
        verts = [(cx - hx, cy - hy, z0), (cx + hx, cy - hy, z0), (cx + hx, cy + hy, z0), (cx - hx, cy + hy, z0),
                 (cx - hx, cy - hy, z1), (cx + hx, cy - hy, z1), (cx + hx, cy + hy, z1), (cx - hx, cy + hy, z1)]
        self.add(mat, verts, [(4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)])

    def disc(self, mat, centre, radius, z, inner=0.0, segments=20):
        ring = [(centre.x + math.cos(a) * radius, centre.y + math.sin(a) * radius, z)
                for a in (2 * math.pi * i / segments for i in range(segments))]
        if inner <= 0:
            self.add(mat, ring + [(centre.x, centre.y, z)], [(i, (i + 1) % segments, segments) for i in range(segments)])
        else:
            hole = [(centre.x + math.cos(a) * inner, centre.y + math.sin(a) * inner, z)
                    for a in (2 * math.pi * i / segments for i in range(segments))]
            self.add(mat, ring + hole, [(i, (i + 1) % segments, segments + (i + 1) % segments, segments + i)
                                        for i in range(segments)])

    def strip(self, mat, points, width, height):
        """A trace: a flat-topped strip along the polyline, a little higher in the middle so its edges catch light."""
        rows = []
        for i, p in enumerate(points):
            d_in = (p - points[i - 1]).normalized() if i > 0 else None
            d_out = (points[i + 1] - p).normalized() if i + 1 < len(points) else None
            n1 = left(d_in) if d_in else left(d_out)
            n2 = left(d_out) if d_out else n1
            n = (n1 + n2)
            n = n / n.dot(n1)
            l, r = p + n * width / 2, p - n * width / 2
            rows += [(r.x, r.y, height * 0.45), (p.x, p.y, height), (l.x, l.y, height * 0.45)]
        faces = []
        for i in range(len(points) - 1):
            a, b = 3 * i, 3 * (i + 1)
            faces += [(a, b, b + 1, a + 1), (a + 1, b + 1, b + 2, a + 2)]
        self.add(mat, rows, faces)

    def objects(self, collection):
        made = []
        for mat, (verts, faces) in self.parts.items():
            mesh = bpy.data.meshes.new(mat.name)
            mesh.from_pydata(verts, [], faces)
            mesh.materials.append(mat)
            ob = bpy.data.objects.new(mat.name, mesh)
            collection.objects.link(ob)
            made.append(ob)
        return made


def text(collection, mat, position, body, size, rotated, z):
    curve = bpy.data.curves.new("label", "FONT")
    curve.body = body
    curve.size = size
    curve.align_x, curve.align_y = "CENTER", "CENTER"
    curve.materials.append(mat)
    ob = bpy.data.objects.new("label", curve)
    ob.location = (position.x, position.y, z)
    ob.rotation_euler = (0, 0, math.pi / 2 if rotated else 0)
    collection.objects.link(ob)
    return ob


def sun(scene, direction, energy, angle):
    light = bpy.data.objects.new("sun", bpy.data.lights.new("sun", "SUN"))
    light.data.energy = energy
    light.data.angle = math.radians(angle)
    light.rotation_euler = Vector(direction).normalized().to_track_quat("-Z", "Y").to_euler()
    scene.collection.objects.link(light)
    return light


def world(scene, grey):
    w = bpy.data.worlds.new("world")
    w.use_nodes = True
    w.node_tree.nodes["Background"].inputs[0].default_value = (grey, grey, grey, 1)
    scene.world = w


def top_camera(scene, centre, size, resolution):
    cam = bpy.data.objects.new("camera", bpy.data.cameras.new("camera"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = size
    cam.data.clip_end = 5000
    cam.location = (centre[0], centre[1], 1000)
    scene.collection.objects.link(cam)
    scene.camera = cam
    scene.render.resolution_x, scene.render.resolution_y = resolution
    scene.render.resolution_percentage = 100


def eevee(scene):
    engines = [i.identifier for i in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    scene.render.engine = next(n for n in ("BLENDER_EEVEE", "BLENDER_EEVEE_NEXT") if n in engines)


# ---- the board -------------------------------------------------------------------------------------------------------

LIGHT = (1.0, -1.1, -1.5)       # from the top left, as on the original's boards


def render_board(out, seed):
    board = Board(seed)
    board.build()
    save_png(os.path.join(out, "board_traces.png"), trace_mask(board))

    scene = reset()
    eevee(scene)
    world(scene, 0.16)
    sun(scene, LIGHT, 3.0, 6)
    top_camera(scene, (T / 2, T / 2), T, (T, T))
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGB"

    base = material("board", (0.006, 0.024, 0.007), roughness=0.38, noise=0.3, noise_scale=0.35)
    copper = material("copper", (0.22, 0.16, 0.032), metallic=0.65, roughness=0.5)
    hole = material("hole", (0.004, 0.006, 0.004), roughness=0.9)
    plastic = material("plastic", (0.018, 0.02, 0.02), roughness=0.55, noise=0.25, noise_scale=0.2)
    tin = material("tin", (0.6, 0.6, 0.58), metallic=1.0, roughness=0.32)
    ceramic = material("ceramic", (0.32, 0.2, 0.09), roughness=0.6)
    silk = material("silk", (0.42, 0.38, 0.16), roughness=0.8)
    marking = material("marking", (0.25, 0.25, 0.24), roughness=0.7)

    tile = bpy.data.collections.new("tile")
    scene.collection.children.link(tile)
    m = Meshes()
    for points, _ in board.lines:
        m.strip(copper, points, W, 1.2)
    for centre, radius, kind, _, _ in board.pads:
        if kind == "square":
            m.box(copper, (centre.x, centre.y), (radius, radius), 0, 1.3)
        else:
            m.disc(copper, centre, radius, 1.3, inner=2.4 if kind == "via" else 0)
            if kind == "via":
                m.disc(hole, centre, 2.6, 0.2)
    for centre, length, width, long_pins, short_pins, rotated, name in board.ics:
        hx, hy = (length / 2, width / 2) if not rotated else (width / 2, length / 2)
        m.box(plastic, (centre.x, centre.y), (hx, hy), 0, 12)
        for normal, count in [((0, 1), long_pins), ((0, -1), long_pins), ((1, 0), short_pins), ((-1, 0), short_pins)]:
            if rotated:
                normal = (-normal[1], normal[0])
            nx, ny = normal
            along = Vector((ny, -nx))
            edge = Vector((centre.x + nx * hx, centre.y + ny * hy))
            for i in range(count):
                p = edge + along * (i - (count - 1) / 2) * P
                foot = p + Vector((nx, ny)) * 9
                arm = p + Vector((nx, ny)) * 4
                half = (3.0 if nx == 0 else 4, 4 if nx == 0 else 3.0)
                m.box(tin, (arm.x, arm.y), half, 4, 6)
                m.box(tin, (foot.x, foot.y), (3.0 if nx == 0 else 3, 3 if nx == 0 else 3.0), 0, 2)
        # The pin 1 dot and the outline on the board.
        corner = Vector((centre.x - hx + 6, centre.y + hy - 6))
        m.disc(marking, corner, 2.2, 12.05)
        outline = [Vector((centre.x - hx - 14, centre.y - hy - 14)), Vector((centre.x + hx + 14, centre.y - hy - 14)),
                   Vector((centre.x + hx + 14, centre.y + hy + 14)), Vector((centre.x - hx - 14, centre.y + hy + 14))]
        for a, b in zip(outline, outline[1:] + outline[:1]):
            m.strip(silk, [a, b], 1.6, 0.3)
    for centre, rotated, kind in board.parts:
        hx, hy = (14, 7) if not rotated else (7, 14)
        body = {"resistor": plastic, "capacitor": ceramic, "diode": plastic}[kind]
        cap_x, cap_y = (4, hy) if not rotated else (hx, 4)
        m.box(body, (centre.x, centre.y), (hx - (4 if not rotated else 0), hy - (0 if not rotated else 4)), 0, 6)
        for s in (-1, 1):
            end = centre + (Vector((s * (hx - 4), 0)) if not rotated else Vector((0, s * (hy - 4))))
            m.box(tin, (end.x, end.y), (cap_x, cap_y), 0, 6.2)
        if kind == "diode":
            band = centre + (Vector((5, 0)) if not rotated else Vector((0, 5)))
            m.box(marking, (band.x, band.y), (1.6, hy) if not rotated else (hx, 1.6), 6, 6.1)
    made = m.objects(tile)
    for position, body, size, rotated in board.labels:
        made.append(text(tile, silk, position, body, size, rotated, 0.3))
    for centre, length, width, _, _, rotated, name in board.ics:
        made.append(text(tile, marking, centre, f"MX{random.Random(name).randint(1000, 9999)}", 9, rotated, 12.05))

    # The tile repeats: copies all around, so what crosses an edge shows on the far side too.
    for ox in (-T, 0, T):
        for oy in (-T, 0, T):
            if ox == oy == 0:
                continue
            for ob in made:
                copy = ob.copy()
                copy.location = (ob.location.x + ox, ob.location.y + oy, ob.location.z)
                tile.objects.link(copy)
    plane = bpy.data.meshes.new("base")
    plane.from_pydata([(-T, -T, 0), (2 * T, -T, 0), (2 * T, 2 * T, 0), (-T, 2 * T, 0)], [], [(0, 1, 2, 3)])
    plane.materials.append(base)
    scene.collection.objects.link(bpy.data.objects.new("base", plane))

    scene.render.filepath = os.path.join(out, "board.png")
    bpy.ops.render.render(write_still=True)
    print("rendered board", flush=True)


# ---- the chip and the parts on the board, on a see-through floor that keeps their shadows ------------------------------

def cycles(scene, samples=128):
    scene.render.engine = "CYCLES"
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"


def shadow_floor(scene, size=5000):
    mesh = bpy.data.meshes.new("floor")
    mesh.from_pydata([(-size, -size, 0), (size, -size, 0), (size, size, 0), (-size, size, 0)], [], [(0, 1, 2, 3)])
    ob = bpy.data.objects.new("floor", mesh)
    ob.is_shadow_catcher = True
    scene.collection.objects.link(ob)
    return ob


def place(ob, mat, smooth=None, bevel=0.0, bevel_segments=3):
    ob.data.materials.clear()
    ob.data.materials.append(mat)
    if smooth is not None:
        ob.data.shade_smooth()
        ob.data.set_sharp_from_angle(angle=math.radians(smooth))
    if bevel:
        modifier = ob.modifiers.new("bevel", "BEVEL")
        modifier.width = bevel
        modifier.segments = bevel_segments
        modifier.limit_method = "ANGLE"
    return ob


def solid_box(scene, mat, centre, half, z0, z1, bevel=0.0):
    (cx, cy), (hx, hy) = centre, half
    verts = [(cx - hx, cy - hy, z0), (cx + hx, cy - hy, z0), (cx + hx, cy + hy, z0), (cx - hx, cy + hy, z0),
             (cx - hx, cy - hy, z1), (cx + hx, cy - hy, z1), (cx + hx, cy + hy, z1), (cx - hx, cy + hy, z1)]
    faces = [(3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)]
    mesh = bpy.data.meshes.new("box")
    mesh.from_pydata(verts, [], faces)
    ob = bpy.data.objects.new("box", mesh)
    scene.collection.objects.link(ob)
    return place(ob, mat, bevel=bevel)


def cylinder(mat, radius, depth, location, rotation=(0, 0, 0), bevel=0.0, vertices=64):
    bpy.ops.mesh.primitive_cylinder_add(radius=radius, depth=depth, location=location, rotation=rotation, vertices=vertices)
    return place(bpy.context.object, mat, smooth=40, bevel=bevel)


def torus(mat, major, minor, location, rotation=(0, 0, 0), segments=(64, 24)):
    bpy.ops.mesh.primitive_torus_add(major_radius=major, minor_radius=minor, location=location, rotation=rotation,
                                     major_segments=segments[0], minor_segments=segments[1])
    return place(bpy.context.object, mat, smooth=60)


def cable(scene, mat, points, radius):
    """A round cable along the points, lying on whatever height they give."""
    curve = bpy.data.curves.new("cable", "CURVE")
    curve.dimensions = "3D"
    curve.bevel_depth = radius
    curve.bevel_resolution = 6
    curve.use_fill_caps = True
    spline = curve.splines.new("BEZIER")
    spline.bezier_points.add(len(points) - 1)
    for bp, p in zip(spline.bezier_points, points):
        bp.co = p
        bp.handle_left_type = bp.handle_right_type = "AUTO"
    curve.materials.append(mat)
    ob = bpy.data.objects.new("cable", curve)
    scene.collection.objects.link(ob)
    return ob


def framed(scene, x0, y0, x1, y1, scale):
    """An orthographic top view of the rectangle, scale pixels per unit."""
    top_camera(scene, ((x0 + x1) / 2, (y0 + y1) / 2), max(x1 - x0, y1 - y0),
               (round((x1 - x0) * scale), round((y1 - y0) * scale)))


def shoot(scene, path):
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print("rendered", os.path.basename(path), flush=True)


def studio():
    scene = reset()
    cycles(scene)
    world(scene, 0.16)
    sun(scene, LIGHT, 3.0, 6)
    shadow_floor(scene)
    return scene


# The chip: the frame every menu panel sits in, as a nine-slice texture. Corners stay as they are; the edges between
# repeat, so the pins along the left and right sides are counted out, never stretched. Pixels, at twice the menu's
# own scale: the body's edge lies CHIP_INSET in from the texture's, the screen CHIP_FRAME in from the body's.
CHIP, CHIP_INSET, CHIP_FRAME, CHIP_CORNER, CHIP_PITCH = 256, 40, 16, 64, 32


def render_chip(out):
    scene = studio()
    framed(scene, -CHIP / 2, -CHIP / 2, CHIP / 2, CHIP / 2, 1)
    plastic = material("chip", (0.014, 0.018, 0.016), roughness=0.4, noise=0.2, noise_scale=0.3)
    tin = material("tin", (0.62, 0.62, 0.6), metallic=1.0, roughness=0.3)
    h, i, top = CHIP / 2 - CHIP_INSET, CHIP / 2 - CHIP_INSET - CHIP_FRAME, 14
    square = [(-1, -1), (1, -1), (1, 1), (-1, 1)]
    verts = [(sx * h, sy * h, top) for sx, sy in square] + [(sx * h, sy * h, 0) for sx, sy in square] + \
            [(sx * i, sy * i, top) for sx, sy in square] + [(sx * i, sy * i, 3) for sx, sy in square]
    faces = []
    for a in range(4):
        b = (a + 1) % 4
        faces += [(a, b, 8 + b, 8 + a), (4 + a, 4 + b, b, a), (8 + a, 8 + b, 12 + b, 12 + a)]
    mesh = bpy.data.meshes.new("frame")
    mesh.from_pydata(verts, [], faces)
    frame = bpy.data.objects.new("frame", mesh)
    scene.collection.objects.link(frame)
    place(frame, plastic, bevel=2.5)
    # Pins down the left and right sides, one per pitch in the middle stretch, gull-winged to the board.
    middle = CHIP // 2 - CHIP_CORNER
    for side in (-1, 1):
        for y in range(-middle + CHIP_PITCH // 2, middle, CHIP_PITCH):
            solid_box(scene, tin, (side * (h + 5), y), (5, 6), 6, 9, bevel=0.8)
            solid_box(scene, tin, (side * (h + 11), y), (2, 6), 2, 8, bevel=0.8)
            solid_box(scene, tin, (side * (h + 15), y), (5, 6), 0, 3, bevel=0.8)
    shoot(scene, os.path.join(out, "chip.png"))


# The parts that sit on the board around the menus, drawn at PROP_SCALE pixels per board pixel.
PROP_SCALE = 2


def render_fan(out):
    scene = studio()
    framed(scene, -150, -150, 150, 150, PROP_SCALE)
    plastic = material("fan", (0.02, 0.022, 0.024), roughness=0.45, noise=0.15, noise_scale=0.1)
    dark = material("inside", (0.004, 0.005, 0.005), roughness=0.8)
    blade_mat = material("blade", (0.03, 0.032, 0.036), roughness=0.33)
    sticker = material("sticker", (0.2, 0.2, 0.19), roughness=0.6)
    case = solid_box(scene, plastic, (0, 0), (120, 120), 0, 25, bevel=7)
    cutters = [cylinder(plastic, 112, 80, (0, 0, 12))] + \
              [cylinder(plastic, 5.5, 80, (sx * 104, sy * 104, 12), vertices=24) for sx in (-1, 1) for sy in (-1, 1)]
    for c in cutters:
        c.hide_render = True
        boolean = case.modifiers.new("cut", "BOOLEAN")
        boolean.operation, boolean.object = "DIFFERENCE", c
    case.modifiers.move(len(case.modifiers) - 1, len(case.modifiers) - 1)
    solid_box(scene, dark, (0, 0), (112, 112), 0, 1)
    for a in (45, 165, 285):
        r = math.radians(a)
        strut = solid_box(scene, plastic, (0, 0), (60, 4), 2, 8)
        strut.location = (math.cos(r) * 60, math.sin(r) * 60, 0)
        strut.rotation_euler = (0, 0, r)
    cylinder(plastic, 36, 18, (0, 0, 9))
    # The turning part: hub and seven swept blades.
    turning = [cylinder(blade_mat, 40, 8, (0, 0, 21), bevel=2), cylinder(sticker, 28, 0.5, (0, 0, 25.2))]
    for b in range(7):
        a0 = 2 * math.pi * b / 7
        nu, nv = 10, 6
        verts = []
        for u in range(nu):
            t = u / (nu - 1)
            r = 38 + t * 70
            for v in range(nv):
                s = v / (nv - 1)
                a = a0 + (s - 0.5) * math.radians(40) + t * math.radians(24)
                verts.append((math.cos(a) * r, math.sin(a) * r, 14 + (0.5 - s) * 14))
        faces = [(u * nv + v, u * nv + v + 1, (u + 1) * nv + v + 1, (u + 1) * nv + v) for u in range(nu - 1) for v in range(nv - 1)]
        mesh = bpy.data.meshes.new("blade")
        mesh.from_pydata(verts, [], faces)
        blade = bpy.data.objects.new("blade", mesh)
        scene.collection.objects.link(blade)
        place(blade, blade_mat, smooth=60)
        blade.modifiers.new("thick", "SOLIDIFY").thickness = 2
        turning.append(blade)
    for ob in turning:
        ob.hide_render = True
    shoot(scene, os.path.join(out, "fan.png"))
    for ob in scene.objects:
        ob.hide_render = ob not in turning
    shoot(scene, os.path.join(out, "fan_blades.png"))


def render_capacitors(out):
    scene = studio()
    framed(scene, -110, -110, 150, 60, PROP_SCALE)
    blue = material("sleeve", (0.012, 0.05, 0.2), roughness=0.3)
    band = material("band", (0.5, 0.52, 0.55), roughness=0.4)
    alu = material("alu", (0.7, 0.7, 0.72), metallic=1.0, roughness=0.25)
    wire = material("wire", (0.6, 0.6, 0.58), metallic=1.0, roughness=0.3)
    copper = material("pad", (0.22, 0.16, 0.032), metallic=0.65, roughness=0.5)
    along = (0, math.pi / 2, 0)
    cylinder(blue, 27, 104, (0, 0, 31), along, bevel=3)
    cylinder(band, 27.6, 16, (-36, 0, 31), along)
    for side in (-1, 1):
        cylinder(alu, 23, 4, (side * 53, 0, 31), along, bevel=1.5)
        cylinder(wire, 2.2, 26, (side * 66, 0, 31), along)
        cylinder(wire, 2.2, 31, (side * 79, 0, 15.5))
        cylinder(copper, 7, 1.3, (side * 79, 0, 0.65))
    shoot(scene, os.path.join(out, "capacitor_axial.png"))

    scene = studio()
    framed(scene, -60, -110, 110, 60, PROP_SCALE)
    black = material("sleeve", (0.01, 0.011, 0.012), roughness=0.3)
    stripe = material("stripe", (0.45, 0.36, 0.1), roughness=0.4)
    alu = material("alu", (0.68, 0.68, 0.7), metallic=1.0, roughness=0.28)
    groove = material("groove", (0.15, 0.15, 0.16), metallic=1.0, roughness=0.5)
    cylinder(black, 36, 66, (0, 0, 33), bevel=2)
    torus(black, 34.5, 3, (0, 0, 66))
    cylinder(alu, 33, 1, (0, 0, 66))
    for a in (90, 210, 330):
        g = solid_box(scene, groove, (0, 0), (14, 1.2), 66.4, 66.6)
        g.location = (math.cos(math.radians(a)) * 14, math.sin(math.radians(a)) * 14, 0)
        g.rotation_euler = (0, 0, math.radians(a))
    s = solid_box(scene, stripe, (0, -35.2), (8, 1.4), 2, 64)
    s.rotation_euler = (0, 0, math.radians(-35))
    shoot(scene, os.path.join(out, "capacitor_radial.png"))


def render_coil(out):
    scene = studio()
    framed(scene, -85, -115, 120, 75, PROP_SCALE)
    core = material("core", (0.03, 0.05, 0.035), roughness=0.5)
    copper = material("winding", (0.55, 0.22, 0.06), metallic=1.0, roughness=0.28)
    torus(core, 46, 19, (0, 0, 22))
    turns = 42
    for k in range(turns):
        a = 2 * math.pi * k / turns
        torus(copper, 21.5, 2.4, (math.cos(a) * 46, math.sin(a) * 46, 22), (math.pi / 2, 0, a), segments=(32, 10))
    shoot(scene, os.path.join(out, "coil.png"))


def render_cables(out):
    # A ribbon cable from the left edge into its connector; the image's left edge is where the cable leaves the screen.
    scene = studio()
    framed(scene, -420, -95, 70, 95, PROP_SCALE)
    grey = material("ribbon", (0.32, 0.32, 0.33), roughness=0.5)
    red = material("red wire", (0.35, 0.02, 0.015), roughness=0.5)
    black = material("connector", (0.015, 0.016, 0.017), roughness=0.45)
    holes = material("holes", (0.003, 0.003, 0.003), roughness=0.9)
    wires = 16
    for k in range(wires):
        y = (k - (wires - 1) / 2) * 8.4
        cable(scene, red if k == 0 else grey, [(-500, y, 4.2), (-160, y, 4.2), (-60, y, 16), (-16, y, 16)], 4.2)
    solid_box(scene, black, (8, 0), (26, 82), 0, 30, bevel=2)
    solid_box(scene, black, (8, 0), (12, 90), 6, 22, bevel=1)
    for k in range(wires):
        solid_box(scene, holes, (20, (k - (wires - 1) / 2) * 8.4), (2.5, 2.5), 29.9, 30.2)
    shoot(scene, os.path.join(out, "ribbon.png"))

    # A thick round cable from the left edge to a plug in its socket.
    scene = studio()
    framed(scene, -520, -230, 130, 110, PROP_SCALE)
    rubber = material("rubber", (0.11, 0.115, 0.12), roughness=0.55, noise=0.1, noise_scale=0.2)
    plug = material("plug", (0.5, 0.5, 0.46), roughness=0.45)
    socket = material("socket", (0.62, 0.6, 0.52), roughness=0.5)
    wires = [material(n, c, roughness=0.45) for n, c in (("r", (0.4, 0.03, 0.02)), ("o", (0.5, 0.2, 0.02)),
                                                           ("y", (0.5, 0.42, 0.04)), ("k", (0.02, 0.02, 0.02)))]
    cable(scene, rubber, [(-700, -150, 15), (-430, -175, 15), (-230, -120, 15), (-110, 10, 15), (-36, 0, 18)], 15)
    cylinder(rubber, 18, 26, (-30, 0, 20), (0, math.pi / 2, 0), bevel=3)
    solid_box(scene, plug, (14, 0), (28, 26), 2, 38, bevel=4)
    solid_box(scene, socket, (58, 0), (16, 34), 0, 22, bevel=2)
    for k, mat in enumerate(wires):
        y = (k - 1.5) * 12
        cable(scene, mat, [(40, y, 22), (70, y, 24), (100, y + (k - 1.5) * 6, 3), (118, y + (k - 1.5) * 8, 2)], 3)
    shoot(scene, os.path.join(out, "cable.png"))


def render_props(out):
    render_fan(out)
    render_capacitors(out)
    render_coil(out)
    render_cables(out)


def main():
    args = sys.argv[sys.argv.index("--") + 1:]
    out = os.path.abspath(args[0])
    what = set(args[1:]) or {"board", "chip", "props"}
    os.makedirs(out, exist_ok=True)
    if "board" in what:
        render_board(out, seed=27)
    if "chip" in what:
        render_chip(out)
    if "props" in what:
        render_props(out)


main()
