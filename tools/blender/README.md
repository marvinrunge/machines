# Remake machine models

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
runs steps 1 to 6 in an empty Blender file, and it is run again whenever a setting changes.

1. **Reference.** `import_classic_rig.py` builds the original machine from its classic export: one empty per
   node, its meshes, and every animation keyed on the timeline behind a marker.
2. **Align.** `align.py` turns the new model to face +X like the original (z up, metres; Meshy faces −Y), scales
   it to the reference's height and stands it on the ground. The Gorilla is 14.09 m tall, ×8.14 for Meshy's
   export.
3. **Pre-segment.** `segment_by_reference.py` sends every face to the nearest visible original part, in the pose
   of the `rest` cycle (frame 0), and leaves out the helper nodes without a part (the Gorilla's 0, 1, 9 and 36).
   The labels are smoothed and each part is made one connected piece.
4. **Seams.** Hinges get straight seams: the same script cuts the mesh along a plane at each joint the recipe
   lists. A limb joint ("split") cuts across the bone that ends there. A part beside others of its parent
   ("trim", such as a shoulder pad on the torso) takes a normal and point from the recipe. Look at every seam;
   where it is wrong, change the recipe rather than the mesh. Still to come: capping the openings, with a cap or
   a joint ball that hides the gap when the part turns.
5. **Parts and pivots.** `split_parts.split` makes one object per part (`n13_hand_r`), with its origin at its
   node and hung on the reference's animated empty. Pivots come from the original. `split_parts.move_pivot`
   moves one only where the model's joint visibly sits elsewhere. The axes stay, so the hinges turn the same
   way, and the offset is recorded for the export.
6. **Check the motion.** Scrub the timeline: feet on the ground, no part flying off or passing through
   another. Fix it in the pivots or the seams, not in the animation.
7. **Sockets and modules.**
   - Faceplate: a box of standard size, origin at its back centre, looking along +X. The eye point (first
     person) is its front centre. The body gets a recess where it sits.
   - Weapons: their own models, origin at the mount.
8. **Race colour.** A mask of the red areas, made from the albedo by hue and then touched up. Godot tints the
   masked areas Red, Blue, Green or Yellow.
9. **Export.** One `.glb` per machine (2k textures, ORM packed) and one per module. Godot makes the LODs.
10. **Game.** A loader builds a `Rig` from the `.glb` (nodes, meshes, sockets) and applies the classic export's
    animations, so `RigView` plays it unchanged. With the remake graphics on, it is used instead of the classic
    model when one exists.

## Files

- `tools/blender/`: the scripts (run through the Blender MCP or `blender -b -P`).
- `art/<machine>/` (Git LFS): the source `.blend`, the raw Meshy download, and full-size textures.
- The exported `.glb` files sit with the game's assets.

## New Meshy models

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
- **Feet.** The boot (4) carries the sole and heel, and only the toes (5) stay flat on the ground while the heel
  lifts. Cutting the whole foot plate off as 5 makes it swing loose. The original's toes hinge at their front
  edge, so for the model's shorter toes the pivot moves back to where they start.

## Open work

- Capping the seams; sockets, faceplate box and weapon modules (step 7); race colour mask (step 8).
- Export (step 9), and the loader in the game (step 10).
