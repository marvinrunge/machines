"""Where the HD machines tear at their joints. meshy_rig.py cuts each Meshy model, one surface, into the original's rig
nodes; where a cut runs between two nodes that move apart (a shoulder, a knee, a turning upper body), the surface
opens. This plays every motion the rig has and measures, along each such seam, how far its two sides drift apart.

    blender -b --factory-startup -P tools/blender/joint_inventory.py -- <classic folder> <out.json> [id ...]

The motions: each animation (walking, punching) at 17 points of its cycle, the idle and working motions likewise,
the upper body turned 90° and 180° each way, and wheels a quarter turn. Parts without Meshy maps (the original's
eyes, the flying machines' shadow plate) are left out: they are no cut.

How far a seam opens is how far each of its vertices, carried by one node, lies from the seam as the other node now
carries it (not from its own twin: a turret's ring that turns in itself stays closed).
Per machine and per pair of nodes the report keeps the seam's length, how far it opens at most and in which motion,
and how much of it opens more than OPEN (a share of the machine's size); machines are ranked by their worst seam.
Only the seams are measured: a hose that Meshy laid apart from the body, or two parts passing through each other,
does not show here."""
import json
import math
import os
import sys

from mathutils import Matrix, Vector, kdtree

PHASES = 17
# A seam opening by more than this share of the machine's size counts as open (1.5 % is about 6 cm on a Knight).
OPEN = 0.015
# Seam points for the nearest-point search lie this share of the machine's size apart.
STEP = 0.002
WELD = 4


def matrix(v):
    x, y, z, p = Vector(v[0:3]), Vector(v[3:6]), Vector(v[6:9]), Vector(v[9:12])
    return Matrix(((x.x, y.x, z.x, p.x), (x.y, y.y, z.y, p.y), (x.z, y.z, z.z, p.z), (0, 0, 0, 1)))


def worlds(nodes, local):
    world = {}

    def of(i):
        if i not in world:
            p = nodes[i]["parent"]
            world[i] = of(p) @ local[i] if 0 <= p < len(nodes) and p != i else local[i]
        return world[i]

    return [of(i) for i in range(len(nodes))]


def at(samples, phase):
    """A track's transform at a point of its cycle, between its two nearest samples."""
    position = min(max(phase, 0), 1) * (len(samples) - 1)
    i = min(int(position), len(samples) - 2)
    if i < 0:
        return matrix(samples[0])
    return matrix(samples[i]).lerp(matrix(samples[i + 1]), position - i)


def poses(rig, rest):
    """(name, {node: local transform}) for every motion the rig has."""
    nodes = rig["nodes"]
    for name, animation in rig.get("animations", {}).items():
        for k in range(PHASES):
            phase = k / (PHASES - 1)
            yield f"{name} {phase:.2f}", {t["node"]: at(t["samples"], phase) for t in animation["tracks"] if t["node"] < len(nodes)}
    for kind in ("idle", "working"):
        tracks = [t for t in rig.get(kind, []) if t["node"] < len(nodes) and t["samples"]]
        if tracks:
            for k in range(PHASES):
                phase = k / (PHASES - 1)
                yield f"{kind} {phase:.2f}", {t["node"]: at(t["samples"], phase) for t in tracks}
    upper = rig.get("upperBody", -1)
    if 0 < upper < len(nodes):
        for degrees in (90, 180, -90):
            yield f"upper body {degrees:+d}°", {upper: rest[upper] @ Matrix.Rotation(math.radians(degrees), 4, "Z")}
    wheels = rig.get("wheels")
    if wheels:
        spun = [i for i in wheels["left"] + wheels["right"] if 0 < i < len(nodes)]
        if spun:
            yield "wheels a quarter turn", {i: rest[i] @ Matrix.Rotation(math.pi / 2, 4, "Y") for i in spun}


def seams(nodes, world):
    """The Meshy surface's edges whose two sides lie in different nodes: {(a, b): [(p, q), ...]} in model space
    (rest pose), a < b."""
    edges = {}
    for i, node in enumerate(nodes):
        for part in node["parts"]:
            if not part.get("maps"):
                continue
            p = part["positions"]
            corners = [world[i] @ Vector(p[3 * k:3 * k + 3]) for k in part.get("indices", range(len(p) // 3))]
            for t in range(0, len(corners) - 2, 3):
                keys = [tuple(round(x, WELD) for x in c) for c in corners[t:t + 3]]
                for a, b in ((0, 1), (1, 2), (2, 0)):
                    edge = (min(keys[a], keys[b]), max(keys[a], keys[b]))
                    edges.setdefault(edge, set()).add(i)
    found = {}
    for (p, q), owners in edges.items():
        if len(owners) < 2:
            continue
        owners = sorted(owners)
        for a in owners:
            for b in owners:
                if a < b:
                    found.setdefault((a, b), []).append((Vector(p), Vector(q)))
    return found


def inventory(model):
    rig = model["rig"]
    nodes = rig["nodes"]
    rest = [matrix(n["transform"]) for n in nodes]
    rest_world = worlds(nodes, rest)
    inverse = [m.inverted() for m in rest_world]
    every = [rest_world[i] @ Vector(n["parts"][k]["positions"][j:j + 3])
             for i, n in enumerate(nodes) for k in range(len(n["parts"])) if n["parts"][k].get("maps")
             for j in range(0, len(n["parts"][k]["positions"]), 3)]
    if not every:
        return None
    lo = Vector([min(p[k] for p in every) for k in range(3)])
    hi = Vector([max(p[k] for p in every) for k in range(3)])
    size = (hi - lo).length
    cut = seams(nodes, rest_world)
    # Each seam vertex once, as each of its two nodes carries it; and points along the seam, STEP apart, for the
    # nearest-point search.
    step = STEP * size
    points = {pair: sorted({tuple(v) for e in edges for v in e}) for pair, edges in cut.items()}
    along = {pair: [p + (q - p) * (k / n) for p, q in edges for n in [max(1, math.ceil((q - p).length / step))] for k in range(n + 1)]
             for pair, edges in cut.items()}
    local = {pair: ([inverse[pair[0]] @ Vector(v) for v in vs], [inverse[pair[1]] @ Vector(v) for v in vs]) for pair, vs in points.items()}
    local_along = {pair: ([inverse[pair[0]] @ v for v in vs], [inverse[pair[1]] @ v for v in vs]) for pair, vs in along.items()}
    worst = {pair: (0.0, "") for pair in cut}
    gaps = {pair: {v: 0.0 for v in vs} for pair, vs in points.items()}
    motions = 0
    for name, override in poses(rig, rest):
        motions += 1
        posed = worlds(nodes, [override.get(i, rest[i]) for i in range(len(nodes))])
        for pair, vs in points.items():
            # Each side's seam vertices against the other side's seam as it now lies: a ring turning in itself (a
            # turret's) or a hinge's line sliding along itself stays closed, though its points have moved on.
            sides = []
            for side in (0, 1):
                other = [posed[pair[1 - side]] @ v for v in local_along[pair][1 - side]]
                tree = kdtree.KDTree(len(other))
                for k, v in enumerate(other):
                    tree.insert(v, k)
                tree.balance()
                sides.append([tree.find(posed[pair[side]] @ v)[2] for v in local[pair][side]])
            biggest = 0.0
            for v, ga, gb in zip(vs, *sides):
                gap = max(ga, gb)
                if gap > gaps[pair][v]:
                    gaps[pair][v] = gap
                biggest = max(biggest, gap)
            if biggest > worst[pair][0]:
                worst[pair] = (biggest, name)
    joints = []
    for pair, edges in cut.items():
        a, b = pair
        length = sum((p - q).length for p, q in edges)
        opened = sum((p - q).length for p, q in edges
                     if max(gaps[pair][tuple(p)], gaps[pair][tuple(q)]) > OPEN * size)
        joints.append({"nodes": [nodes[a]["name"], nodes[b]["name"]], "ids": [a, b],
                       "seam": round(length, 3), "open": round(opened, 3),
                       "gap": round(worst[pair][0], 3), "share": round(worst[pair][0] / size, 4), "motion": worst[pair][1]})
    joints.sort(key=lambda j: -j["share"])
    return {"size": round(size, 3), "motions": motions, "joints": joints,
            "worst": joints[0]["share"] if joints else 0.0,
            "tracks": [nodes[i]["name"] for i in (rig.get("tracks") or {}).values() if isinstance(i, int)]}


def main():
    argv = sys.argv[sys.argv.index("--") + 1:]
    classic, out = argv[0], argv[1]
    only = set(argv[2:])
    folder = os.path.join(classic, "meshy", "models", "red")
    report = {}
    for name in sorted(os.listdir(folder)):
        export_id = os.path.splitext(name)[0]
        if only and export_id not in only:
            continue
        model = json.load(open(os.path.join(folder, name)))
        rig = model.get("rig", {})
        # Buildings don't move apart (their working parts are their own Meshy-less pieces or still).
        if not (rig.get("animations") or rig.get("idle") or rig.get("working") or rig.get("upperBody", -1) > 0 or rig.get("wheels")):
            continue
        found = inventory(model)
        if found is None:
            continue
        report[export_id] = found
        top = found["joints"][:3]
        print(f"{export_id}: size {found['size']} m, {found['motions']} motions, worst {100 * found['worst']:.1f} %; "
              + "; ".join(f"{j['nodes'][0]}|{j['nodes'][1]} {100 * j['gap']:.0f} cm in {j['motion']} ({j['open']} of {j['seam']} m open)"
                          for j in top), flush=True)
    os.makedirs(os.path.dirname(os.path.abspath(out)), exist_ok=True)
    with open(out, "w") as f:
        json.dump(dict(sorted(report.items(), key=lambda kv: -kv[1]["worst"])), f, indent=1)


if __name__ == "__main__":
    main()
