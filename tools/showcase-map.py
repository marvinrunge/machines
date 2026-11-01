"""The showcase map, data/maps/showcase.scn: every building and machine the exporter lists (classic/models.txt), for
each of the four races, standing on the flat floor of the desert planet. The races are allied with each other and
have no AI script, so nothing moves or fights, and each has a level 3 beacon's view, so the whole map can be looked
over. Red, blue, green and yellow each get a quarter of the map, buildings first, then machines, in rows.

    python tools/showcase-map.py [classic folder]

Footprints come from the exported models (classic/remake/models/red/<id>.json), the ground from the planet's
heights (classic/planets/m_desert/heights.bin): a building or machine stands only where the ground under it lies
level."""
import json
import os
import struct
import sys
from array import array

REPO = os.path.join(os.path.dirname(os.path.abspath(__file__)), "..")
PLANET = "m_desert"
RACES = ["RED", "BLUE", "GREEN", "YELLOW"]
# Each race's quarter: x0, y0, x1, y1 (metres).
QUARTERS = {"RED": (0, 0, 802, 802), "BLUE": (802, 0, 1604, 802), "GREEN": (802, 802, 1604, 1604), "YELLOW": (0, 802, 802, 1604)}
EDGE = 60          # kept clear at the map's edge and between the quarters
STEP = 4           # search grid
# How far the ground under a footprint may stray from the floor's height: buildings stand level, machines may lean.
BUILDING_SLACK, MACHINE_SLACK = 0.4, 1.5
BUILDING_MARGIN = 6
MACHINE_MARGIN = 3
# Buildings in this order, each by level.
KINDS = ["FACTORY MILITARY", "FACTORY CIVILIAN", "HARDWARE_LAB MILITARY", "HARDWARE_LAB CIVILIAN", "SMELTER", "MINE",
         "GARRISON", "BEACON", "POD", "MISSILE_EMPLACEMENT TURRET", "MISSILE_EMPLACEMENT SENTRY",
         "MISSILE_EMPLACEMENT LAUNCHER", "MISSILE_EMPLACEMENT ICBM"]


def exports(classic):
    """(id, words) of every CONSTRUCTION and MACHINE line, buildings in KINDS order."""
    buildings, machines = [], []
    for line in open(os.path.join(classic, "models.txt")):
        words = line.split()
        if not words or words[0] not in ("CONSTRUCTION", "MACHINE"):
            continue
        (buildings if words[0] == "CONSTRUCTION" else machines).append(("_".join(words[1:]), words[1:]))

    def kind(words):
        name = words[0] + (" " + words[2] if words[0] in ("FACTORY", "HARDWARE_LAB", "MISSILE_EMPLACEMENT") else "")
        return KINDS.index(name), int(words[1])

    return sorted(buildings, key=lambda b: kind(b[1])), machines


def footprint(classic, export_id):
    """The model's extent on the ground: min x, min y, max x, max y in its own space. Machines start facing along x,
    as buildings at orientation 0 do."""
    model = json.load(open(os.path.join(classic, "remake", "models", "red", export_id + ".json")))
    xs = [x for part in model["parts"] for x in part["positions"][0::3]]
    ys = [y for part in model["parts"] for y in part["positions"][1::3]]
    return min(xs), min(ys), max(xs), max(ys)


class Ground:
    def __init__(self, path):
        data = open(path, "rb").read()
        self.columns, self.rows = struct.unpack_from("<ii", data, 0)
        self.spacing = struct.unpack_from("<f", data, 8)[0]
        self.heights = array("f")
        self.heights.frombytes(data[12:12 + 4 * self.columns * self.rows])
        counts = {}
        for h in self.heights:
            counts[round(h, 1)] = counts.get(round(h, 1), 0) + 1
        self.floor = max(counts, key=counts.get)

    def at(self, x, y):
        cx = min(max(int(round(x / self.spacing)), 0), self.columns - 1)
        cy = min(max(int(round(y / self.spacing)), 0), self.rows - 1)
        return self.heights[cy * self.columns + cx]

    def level(self, x0, y0, x1, y1, slack):
        s = self.spacing
        for cy in range(int(y0 // s), int(y1 // s) + 2):
            for cx in range(int(x0 // s), int(x1 // s) + 2):
                if not (0 <= cx < self.columns and 0 <= cy < self.rows):
                    return False
                if abs(self.heights[cy * self.columns + cx] - self.floor) > slack:
                    return False
        return True


def place(items, quarter, ground):
    """Lays the items out in reading order inside the quarter; returns (item, x, y) per item."""
    qx0, qy0, qx1, qy1 = quarter[0] + EDGE / 2, quarter[1] + EDGE / 2, quarter[2] - EDGE / 2, quarter[3] - EDGE / 2
    qx0, qy0 = max(qx0, EDGE), max(qy0, EDGE)
    qx1, qy1 = min(qx1, 1604 - EDGE), min(qy1, 1604 - EDGE)
    taken, placed = [], []
    cursor = (qy0, qx0)
    for item, (fx0, fy0, fx1, fy1), margin, slack in items:
        w, h = fx1 - fx0 + 2 * margin, fy1 - fy0 + 2 * margin
        y, x = cursor
        while True:
            if x + w > qx1:
                y, x = y + STEP, qx0
            if y + h > qy1:
                raise SystemExit(f"no room left for {item[0]} in its quarter")
            box = (x, y, x + w, y + h)
            if not any(box[0] < t[2] and t[0] < box[2] and box[1] < t[3] and t[1] < box[3] for t in taken) \
                    and ground.level(*box, slack):
                break
            x += STEP
        taken.append(box)
        placed.append((item, x + margin - fx0, y + margin - fy0))
        cursor = (y, x + w)
    return placed


def construction_line(words, x, y, z):
    # CONSTRUCTION type level [subtype] [weapons] -> type level x y z orientation [subtype] [weapons]
    return " ".join([words[0], words[1], f"{x:.2f}", f"{y:.2f}", f"{z:.3f}", "0"] + words[2:])


def machine_line(words, x, y, z):
    # MACHINE type level [subtype] [loadout] -> type hw sw x y z [subtype] [loadout]
    return " ".join([words[0], words[1], words[1], f"{x:.2f}", f"{y:.2f}", f"{z:.3f}"] + words[2:])


def main():
    classic = sys.argv[1] if len(sys.argv) > 1 else os.path.join(REPO, "classic")
    ground = Ground(os.path.join(classic, "planets", PLANET, "heights.bin"))
    buildings, machines = exports(classic)
    items = [((i, w, construction_line), footprint(classic, i), BUILDING_MARGIN, BUILDING_SLACK) for i, w in buildings] \
        + [((i, w, machine_line), footprint(classic, i), MACHINE_MARGIN, MACHINE_SLACK)
           for i, w in machines]
    out = ["// The showcase: every building and machine of every race, allied, standing still (tools/showcase-map.py).",
           "RACES_DEFAULT"] + [f"\t{race} {'PC' if race == 'RED' else 'AI'}" for race in RACES] + ["ENDRACES_DEFAULT", ""]
    for race in RACES:
        placed = place(items, QUARTERS[race], ground)
        first = placed[0]
        out += [f"RACE {race} none",
                f"CAMERA_POS {first[1]:.0f} {first[2] + 60:.0f} 80 -90 41 0 ZENITH",
                "VIRTUAL_BEACON LEVEL_3_BEACON",
                "NBMU 1000"]
        out += [f"ALLY {other}" for other in RACES if other != race]
        out.append("CONSTRUCTIONS")
        out += [line(words, x, y, ground.at(x, y)) for (_, words, line), x, y in placed if line is construction_line]
        out.append("MACHINES")
        out += [line(words, x, y, ground.at(x, y)) for (_, words, line), x, y in placed if line is machine_line]
        out += ["ENDRACE", ""]
        print(f"{race}: {len(placed)} placed, down to y {max(y for _, _, y in placed):.0f}")
    maps = os.path.join(REPO, "data", "maps")
    os.makedirs(maps, exist_ok=True)
    with open(os.path.join(maps, "showcase.scn"), "w", newline="\r\n") as f:
        f.write("\n".join(out))
    print(f"{len(buildings)} buildings and {len(machines)} machines per race, floor at {ground.floor} m")


if __name__ == "__main__":
    main()
