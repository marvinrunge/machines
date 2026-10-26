"""The Gorilla (AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7) from its Meshy model:
reference, alignment, segmentation, parts, pivots and planted feet (steps 1 to 6 of README.md). Run it in an
empty Blender file with tools/blender on sys.path:

    import sys; sys.path.insert(0, r"C:\\Projekte\\machines\\tools\\blender")
    from recipes import gorilla; gorilla.run(r"<the Meshy .fbx>")

The .fbx is Meshy's plain texture export (Meshy_AI_Crimson_Sentinel_20261026_texture.fbx), made of separate
pieces, not the remeshed one. Everything is cut in the pose of the original's "rest" cycle (frame 0), which the
Meshy model stands in."""
import os

import bpy

import align
import fit_pivots
import fix_feet
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
    # Ankles: the whole foot plate (sole, heel and claws) is the foot, which fix_feet keeps on the ground.
    (5, "split", (0, 0, -1), (0, 0, 1.2)), (8, "split", (0, 0, -1), (0, 0, 1.2)),
    # Shoulders: the torso piece runs on into both upper arms under the pads; outside the shoulders it is arm.
    (11, "split", (0, -1, 0), (0, -4.08, 0)), (22, "split", (0, 1, 0), (0, 4.08, 0)),
    (12, "split"), (23, "split"),                                   # elbows
    (13, "split"), (24, "split"),                                   # wrists
]

NAMES = {
    2: "pelvis", 3: "thigh_l", 4: "shin_l", 5: "foot_l", 6: "thigh_r", 7: "shin_r", 8: "foot_r", 10: "torso",
    11: "upperarm_r", 12: "forearm_r", 13: "hand_r", 22: "upperarm_l", 23: "forearm_l", 24: "hand_l",
    33: "launcher_l", 34: "launcher_r", 35: "faceplate",
}

# Where the model's joints are, which is where the parts turn and where they are cut. Shoulders, elbows and
# wrists have round joint pieces; their centres are where to start (the wrists' 2.8 m cuffs are not joints).
BALLS = (11, 12, 13, 22, 23, 24)
BALL_SIZE = (0.5, 2.7)
# The original walks on long toes with its boots folded up behind; the model stands on flat foot plates. Its
# feet turn about the middle of the ankle (world, rest cycle pose), and fix_feet puts them down.
PIVOTS = {5: (-1.09, 2.59, 1.25), 8: (-1.09, -2.59, 1.25)}
# Fitting pivots to the motion (fit_pivots) is left out: it pulls the shoulders deep into the torso, and with
# the feet planted the legs' angles are solved anew, so fitted knees open wider (1.5 m) than the original's
# (1.2 m). Hips and knees keep the original's pivots, the arms their balls.
FIT = ()
MIRROR = ()
# Hip, knee and foot of each leg, for fix_feet.
LEGS = ((3, 4, 5), (6, 7, 8))


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
    # Pivots first: the seams go through them.
    fit_pivots.to_balls(BALLS, fit_pivots.round_pieces(model, *BALL_SIZE))
    for node, point in PIVOTS.items():
        split_parts.move_pivot(node, point)
    parts = cut(model, raw)
    # The fitted pivots move the seams too, so the model is cut again through them.
    frames = range(0, bpy.context.scene.frame_end + 1, 3)
    fit_pivots.fit_all(list(parts.values()), frames, only=FIT, mirror=MIRROR)
    parts = cut(model, raw)
    # The moved leg pivots change the legs' lengths: the feet follow the untouched original's feet instead.
    import_classic_rig.build(os.path.abspath(classic), prefix="ref")
    fix_feet.fix(LEGS)
    for o in list(bpy.data.collections["orig"].objects) + list(bpy.data.collections["ref"].objects):
        if o.type == "MESH":
            o.hide_viewport = True
            o.hide_render = True
    return parts


def cut(model, raw):
    """Segments a fresh copy of the model and splits it into parts, replacing any parts cut before."""
    bpy.context.scene.frame_set(0)
    for part in list(bpy.data.collections["parts"].objects) if "parts" in bpy.data.collections else []:
        mesh = part.data
        bpy.data.objects.remove(part)
        bpy.data.meshes.remove(mesh)
    old = model.data
    model.data = raw.copy()
    model.data.name = "meshy_gorilla"
    if old.users == 0:
        bpy.data.meshes.remove(old)
    # Meshy's plain texture export is made of separate pieces (plates, claws): they stay whole.
    segment_by_reference.segment(model, skip=SKIP, joints=JOINTS, merge=MERGE, shells=0.85)
    parts = split_parts.split(model, NAMES)
    model.hide_viewport = True
    model.hide_render = True
    return parts
