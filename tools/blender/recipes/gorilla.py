"""The Gorilla (AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7) from its Meshy model:
reference, alignment, segmentation, parts and pivots (steps 1 to 6 of README.md). Run it in an empty Blender
file with tools/blender on sys.path:

    import sys; sys.path.insert(0, r"C:\\Projekte\\machines\\tools\\blender")
    from recipes import gorilla; gorilla.run(r"<the Meshy .fbx>")

Everything is cut in the pose of the original's "rest" cycle (frame 0), which the Meshy model stands in."""
import os

import bpy

import align
import import_classic_rig
import segment_by_reference
import split_parts

ID = "AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7"
CLASSIC = os.path.join(os.path.dirname(__file__), "..", "..", "..", "classic", "models", "red", ID + ".json")

# Helper nodes without a visible part: the machine, its body pivot, the waist and the punch's blast point.
SKIP = (0, 1, 9, 36)

# The fingers (16-21, 27-32) have no mesh in the original, and the model's fists are closed: the fingers (14,
# 15 and 25, 26 are the original's finger blocks) stay with the hand.
MERGE = {n: 13 for n in range(14, 22)} | {n: 24 for n in range(25, 33)}

# Seams, parents before children: (node, mode[, world normal[, world point]]).
JOINTS = [
    (4, "split"), (7, "split"),                                     # knees
    (5, "split", (0, 0, -1), (0, 0, 1.2)), (8, "split", (0, 0, -1), (0, 0, 1.2)),          # ankles
    (5, "trim", (1, 0.35, 0), (0, 2.9, 0)), (8, "trim", (1, -0.35, 0), (0, -2.9, 0)),      # toes from the sole
    (11, "trim", (0, -1, 0)), (22, "trim", (0, 1, 0)),             # shoulders
    (12, "split"), (23, "split"),                                   # elbows
    (13, "split"), (24, "split"),                                   # wrists
]

NAMES = {
    2: "pelvis", 3: "thigh_l", 4: "shin_l", 5: "toes_l", 6: "thigh_r", 7: "shin_r", 8: "toes_r", 10: "torso",
    11: "upperarm_r", 12: "forearm_r", 13: "hand_r", 22: "upperarm_l", 23: "forearm_l", 24: "hand_l",
    33: "launcher_l", 34: "launcher_r", 35: "faceplate",
}

# Where the model's joints sit when the original's pivot is elsewhere (world, rest cycle pose). The original's
# toes hinge at their front edge; the model's toes start further back.
PIVOTS = {5: (0.0, 2.6, 0.35), 8: (0.0, -2.6, 0.35)}


def run(fbx, classic=CLASSIC):
    import_classic_rig.build(os.path.abspath(classic))
    there = set(bpy.data.objects)
    bpy.ops.import_scene.fbx(filepath=fbx)
    model = next(o for o in bpy.data.objects if o not in there and o.type == "MESH")
    model.name = "meshy_gorilla"
    raw = model.data.copy()
    raw.name = "meshy_raw"
    raw.use_fake_user = True
    align.align(model, turn=90)
    bpy.context.scene.frame_set(0)
    segment_by_reference.segment(model, skip=SKIP, joints=JOINTS, merge=MERGE)
    parts = split_parts.split(model, NAMES)
    model.hide_viewport = True
    for node, point in PIVOTS.items():
        split_parts.move_pivot(node, point)
    # move_pivot shifted the keys already there; the reference was keyed before, so every cycle has the offset.
    for o in bpy.data.collections["orig"].objects:
        if o.type == "MESH":
            o.hide_viewport = True
    return parts
