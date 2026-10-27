"""Checks the upgraded models (upgrade_classic.py) against the classic ones in motion: in every sample of every
animation, how far the machine's lowest point rose. A rounded foot tip shows up here as a machine floating
above the ground while it walks.

    blender -b --factory-startup -P tools/blender/check_upgrade.py -- <classic folder> [limit in metres]
"""
import json
import os
import sys

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from import_classic_rig import matrix  # noqa: E402
from upgrade_classic import world_transforms  # noqa: E402
from mathutils import Vector  # noqa: E402


def drawn(part):
    """
    The parts whose shape is their geometry: not the flat, half-clear black shadow plate under flyers and hovers,
    nor see-through pictures (a wheel's side is a round picture on a square plate, whose corners nobody sees).
    """
    diffuse = part.get("diffuse", [1, 1, 1, 1])
    texture = part.get("texture", "").lower()
    if texture.endswith("_t.bmp") or texture.endswith("_bt.bmp"):
        return False
    return texture or len(diffuse) < 4 or diffuse[3] > 0.99 or sum(diffuse[:3]) > 0.03


def points(model):
    return [[Vector(p["positions"][j:j + 3]) for p in node["parts"] if drawn(p) for j in range(0, len(p["positions"]), 3)]
            for node in model["rig"]["nodes"]]


def lowest(nodes, local, poses):
    """The lowest point over all nodes, posed: `poses` overrides some nodes' transforms."""
    posed = [dict(n, transform=poses.get(i, n["transform"])) for i, n in enumerate(nodes)]
    world = world_transforms(posed)
    return min(((world[i] @ v).z for i, vs in enumerate(local) for v in vs), default=0)


def check(before, after):
    nodes = before["rig"]["nodes"]
    old, new = points(before), points(after)
    worst = (lowest(nodes, new, {}) - lowest(nodes, old, {}), "rest pose")
    for name, animation in before["rig"].get("animations", {}).items():
        tracks = animation["tracks"]
        for s in range(max(len(t["samples"]) for t in tracks)):
            poses = {t["node"]: t["samples"][min(s, len(t["samples"]) - 1)] for t in tracks}
            worst = max(worst, (lowest(nodes, new, poses) - lowest(nodes, old, poses), f"{name} {s}"))
    return worst


def main():
    argv = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    classic = argv[0] if argv else os.path.join(os.path.dirname(__file__), "..", "..", "classic")
    limit = float(argv[1]) if len(argv) > 1 else 0.01
    failed = 0
    folder = os.path.join(classic, "models", "red")
    for name in sorted(os.listdir(folder)):
        upgraded = os.path.join(classic, "remake", "models", "red", name)
        if not name.endswith(".json") or not os.path.exists(upgraded):
            continue
        rise, where = check(json.load(open(os.path.join(folder, name))), json.load(open(upgraded)))
        mark = "FLOATS" if rise > limit else "ok"
        failed += rise > limit
        print(f"{mark:6} {rise * 100:6.1f} cm  {name[:-5]}  ({where})", flush=True)
    print(f"{failed} models rise more than {limit * 100:.0f} cm")


if __name__ == "__main__":
    main()
