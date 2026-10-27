# Remake machine models

## Stage 1: the classic models upgraded

Before any machine gets a new model, `upgrade_classic.py` improves all the classic exports at once. It runs
headless in about two minutes and writes `classic/remake/models/<race>/<id>.json`, which is git-ignored like the
rest of `classic/`:

    blender -b --factory-startup -P tools/blender/upgrade_classic.py -- classic

- It rounds the edges where the original shaded flat, and leaves spike tips sharp so legs keep touching the ground.
- It bakes ambient occlusion into the vertices.
- It turns the original's spinners into solids of revolution. These are flat outlines that turn to face the
  camera: tyres, gatling barrels, the Gorilla's fingers. Tyres get a tread of staggered blocks and matt dark rubber.
  Sprites, the lights and glows, stay flat.
- Rigs, animations, sprites and weapons stay the same.
- The game uses these models in the remake look, together with the metal masks HdTextures makes from the HD
  textures. `MACHINES_MODEL_UPGRADE=0` shows the classic models instead, for comparison.
- `check_upgrade.py` checks that no machine rises above the ground in any animation sample.
- `preview_upgrade.py` renders a model before and after.

## New models

How a new model (from Meshy or by hand) becomes a remake machine that moves like the original. The first one
through is the Gorilla (`AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7`).

## Principles

- **Rigid parts, like the original.** The original animates no skin: a machine is a tree of rigid parts, and
  its plans (`rest`, `walk`, `turnLeft`, `turnRight`, `startWalking`, `stopWalking`, `punchA`, `punchB` ...)
  turn each part about its joint. A remake model is cut into the same parts. Every part hangs 100 % on its node,
  so there is no weight painting, and parts can be swapped.
- **The original's rest pose is the bind pose.** Animations are rotations relative to each part's rest
  transform, so the model is kept or posed in the original's rest pose. Meshy models made from the original
  already stand in it.
- **One part per original node, named by its index.** `n13_hand_r` is node 13 of the original rig. The mapping
  needs no table, and the original's animation tracks find their part.
- **Nothing from the game copy goes into the repo.** Animations are sampled from `classic/` (your own copy),
  like everything else there. The exported `.glb` holds only the new meshes, pivots and sockets. The game
  applies the original animations when it loads them: new rest × (original rest⁻¹ × original sample) per node.
- **Modules plug into sockets.** The faceplate (the eyes, and the first-person camera) and the weapons are
  separate models, docked at empties on the body: `socket_faceplate`, `socket_weapon_l`, `socket_weapon_r`,
  `socket_weapon_t`. The same eye box can go on different machines.

## Steps

Each machine gets a recipe in `recipes/` holding its settings (the Gorilla's: `recipes/gorilla.py`). A recipe
runs steps 1 to 6 (and the check in 7) in an empty Blender file, and it is run again whenever a setting changes.

1. **Reference.** `import_classic_rig.py` builds the original machine from its classic export: one empty per
   node, its meshes, and every animation keyed on the timeline behind a marker.
2. **Align.** `align.py` turns the new model to face +X like the original (z up, metres; Meshy faces −Y), scales
   it to the reference's height and stands it on the ground. The Gorilla is 14.09 m tall.
3. **Pre-segment.** `segment_by_reference.py` sends every face to the nearest visible original part, in the pose
   of the `rest` cycle (frame 0), and leaves out the helper nodes without a part (the Gorilla's 0, 1, 9 and 36).
   The labels are smoothed. A model made of separate pieces (`shells`) keeps each piece whole when most of it
   goes to one part; only pieces that reach across a joint are cut.
4. **Seams.** Hinges get straight seams: the same script cuts the mesh along a plane at each joint the recipe
   lists. A limb joint ("split") cuts across the bone that ends there. A part beside others of its parent
   ("trim", such as a shoulder pad on the torso) takes a normal and point from the recipe. A whole piece is not
   cut: it goes to the side its middle is on. Finally each piece must hang on its part through a chain of
   touching pieces, or it moves to the part it touches (a claw sorted to the torso that sits on a foot). Look at
   every seam; where it is wrong, change the recipe rather than the mesh. Still to come: capping the openings,
   with a cap or a joint ball that hides the gap when the part turns.
5. **Parts and pivots.** `split_parts.split` makes one object per part (`n13_hand_r`), with its origin at its
   node and hung on the reference's animated empty. Pivots go where the model's joints are, and that happens
   *before* cutting, since the seams go through the pivots:
   - `fit_pivots.round_pieces` finds the model's ball joints (pieces whose vertices lie on a sphere), and
     `to_balls` puts the nearest pivots in their centres. The Gorilla's shoulders, elbows and wrists.
   - `fit_pivots.fit_all` fits a pivot to the motion where there is no ball: the point that, over all cycles,
     keeps the part closest to its parent, with left and right mirrored. Since that needs the parts, a recipe
     cuts, fits and cuts again. The Gorilla uses none: it pulls the shoulders into the torso, and the hips to
     the middle and the knees a metre forwards, which makes the legs longer so the shins dig into the ground.
     With planted feet (step 6) the legs' angles are solved anew anyway, and fitted knees open wider than the
     original's.
   - `split_parts.move_pivot` moves a pivot by hand (the Gorilla's ankles, to the middle of the ankle).
   The rotations stay the original's, so the hinges turn the same way, and each offset is recorded for the export.
6. **Plant the feet.** `fix_feet.fix` bakes hip, knee and foot of each leg on every frame. The foot follows the
   untouched original's foot (a second reference, `ref`): it turns about the point where that one meets the
   ground, flat (turned only about the vertical) while that one is down, tipped as the original's while it is
   up. Its lowest point is put on the ground while standing, nothing of foot or shin goes through the ground,
   a foot that would dig in levels out first, and where the leg is too short for the original's stride the foot
   comes in to where it reaches, so it never comes off the shin. The feet's paths are smoothed over a few frames
   before hip and knee are solved (two-bone IK, bending in the plane they bent in), so these rules do not jerk.
   Keys are quaternions with straight interpolation (`import_classic_rig.key`, `linear`), as the game plays them:
   Euler angles flip at ±180° and Bezier curves overshoot between the samples.
7. **Check the motion.** `check_joints.joint_gaps` measures, for every part, how far it moves off its parent
   over the cycles. For the Gorilla, from the original's pivots to the model's joints: elbows 1.2 to 0.3 m,
   shoulders 2.8 to 1.3 m (only in the punches' overhead swing); knees stay at 1.2 m (original pivots), feet
   0.6 m. With the feet planted nothing of the legs is below the ground, standing feet are at most 0.18 m above
   it and tipped at most 9°, and no leg part turns more than 15° from one frame to the next except where a
   cycle starts. `preview.stage` sets up a camera and renders every cycle to a video. Fix what is wrong in the
   pivots or the seams, not in the animation.
8. **Sockets and modules.**
   - Faceplate: a box of standard size, origin at its back centre, looking along +X. The eye point (first
     person) is its front centre. The body gets a recess where it sits.
   - Weapons: their own models, origin at the mount.
9. **Race colour.** A mask of the red areas, made from the albedo by hue and then touched up. Godot tints the
   masked areas Red, Blue, Green or Yellow.
10. **Export.** One `.glb` per machine (2k textures, ORM packed) and one per module. Godot makes the LODs.
11. **Game.** A loader builds a `Rig` from the `.glb` (nodes, meshes, sockets) and applies the classic export's
    animations, so `RigView` plays it unchanged. With the remake graphics on, it is used instead of the classic
    model when one exists.

## Files

- `tools/blender/`: the scripts (run through the Blender MCP or `blender -b -P`).
- `art/<machine>/` (Git LFS): the source `.blend`, the raw Meshy download, and full-size textures.
- The exported `.glb` files sit with the game's assets.

## New Meshy models

- **Download the plain texture export, not the remeshed one.** The plain one is made of the separate pieces
  Meshy built the machine from: plates, joint balls, claws. They overlap like a real machine's, so the joints
  stay closed when the parts turn. The remesh fuses everything into one surface, which can only be cut, and
  cut parts gape at every joint.
- Give it a screenshot of the original, or the fan model from `references/`, as the image.
- Ask for the machine in its rest pose, front-facing and symmetric, with limbs slightly apart from the body.
  Fused limbs are what makes cutting hard.
- Ask for no painted eyes (a plain plate where the faceplate goes) and no weapons. Weapons are made on their own.
- Check the licence of the generation (Meshy's free tier is CC BY 4.0) before it goes into the repo.

## What the original rigs are like

- **The parts the cycles move.** Look at the cycles first (the amplitude of each node over each cycle). In the
  Gorilla's `rest` nothing moves. In `walk` the legs swing up to 90° and the shoulders up to 36°. The punches
  swing the shoulders up to 166° and drop the body by up to 2.6 m.
- **Helper nodes have no part.** The Gorilla's fingers 16–21 and 27–32 are animated but have no mesh. Its
  visible hand is a palm (13) and a finger block (14, 15), which barely moves (5–10°). A closed Meshy fist
  therefore stays one rigid part with the hand.
- **Feet.** The original walks on long toes (5) with its boots (4) folded up behind it: while walking the boots
  are 3 m up and only the toes, hinged at their front edge on the ground, stand. A model with flat feet played
  with those angles floats or digs in, however the pivots are set. So the model's whole foot plate below the
  ankle is node 5, turning about the middle of the ankle, and `fix_feet` plants it (step 6).
- **Hidden objects are not posed.** Blender does not update the place of an object hidden in the viewport when
  the frame changes. Measure the reference with its meshes shown, or measure its empties.

## Open work

- Capping the seams; sockets, faceplate box and weapon modules (step 8); race colour mask (step 9).
- Export (step 10), and the loader in the game (step 11). The legs are baked (step 6), so the export carries
  their tracks rather than the loader applying the original's.
