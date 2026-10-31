"""The HD look's other races: classic/meshy/models/<race>/<id>.json for blue, green and yellow, made from the red
Meshy exports (tools/blender/meshy_rig.py) once tools/race-recolour.ps1 has painted their colour maps.

    python tools/race_models.py [classic folder]

A Meshy part takes its race's colour map (albedo-<race>.jpg beside the red one), and the original's eyes on the eye
box stay as they are (every race has the same); a node that kept the original's parts (the Gorilla's fists) takes
them from that race's own upgraded export, tinted and with the race's logos. The model's own parts follow the same
way: Meshy maps swapped, the original's red tint (1, 0, 0) turned to the race's."""
import json
import os
import sys

RACES = {"blue": [0, 0.3, 1], "green": [0.817, 1, 0], "yellow": [1, 0.833, 0]}


def recoloured(part, race):
    if "maps" not in part:
        return part
    maps = dict(part["maps"])
    base, extension = os.path.splitext(maps["albedo"])
    maps["albedo"] = f"{base}-{race}.jpg"
    return dict(part, maps=maps)


def main():
    classic = sys.argv[1] if len(sys.argv) > 1 else os.path.join(os.path.dirname(__file__), "..", "classic")
    red = os.path.join(classic, "meshy", "models", "red")
    made = missing = 0
    for name in sorted(os.listdir(red)):
        model = json.load(open(os.path.join(red, name)))
        for race, colour in RACES.items():
            source = os.path.join(classic, "remake", "models", race, name)
            original = json.load(open(source)) if os.path.exists(source) else None
            out = dict(model)
            rig = dict(model["rig"])
            nodes = []
            for i, node in enumerate(rig["nodes"]):
                if any("maps" in p for p in node["parts"]) or original is None:
                    nodes.append(dict(node, parts=[recoloured(p, race) for p in node["parts"]]))
                else:
                    kept = original["rig"]["nodes"][i]
                    nodes.append(dict(node, parts=kept["parts"], sprites=kept.get("sprites", []),
                                      spinners=kept.get("spinners", [])))
            rig["nodes"] = nodes
            out["rig"] = rig
            out["parts"] = [recoloured(p, race) if "maps" in p else
                            dict(p, diffuse=colour + p["diffuse"][3:]) if p.get("diffuse", [])[:3] == [1, 0, 0] else p
                            for p in model["parts"]]
            for p in out["parts"]:
                if "maps" in p and not os.path.exists(os.path.join(classic, p["maps"]["albedo"])):
                    missing += 1
            target = os.path.join(classic, "meshy", "models", race, name)
            os.makedirs(os.path.dirname(target), exist_ok=True)
            with open(target, "w") as f:
                json.dump(out, f, separators=(",", ":"))
            made += 1
    print(f"{made} race models; {missing} parts point at a colour map not made yet")


if __name__ == "__main__":
    main()
