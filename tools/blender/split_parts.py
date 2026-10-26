"""Splits a segmented model (face attribute "part", see segment_by_reference.py) into one object per part
(steps 4 and 5 of README.md). Each part is named n<node>_<name>, its origin is its node's pivot, and it hangs on
its node with no offset: played with the original's tracks, it moves as the original part did.

The parts are cut in the pose the reference shows when this runs (the frame the segmentation used), so their
geometry is stored relative to the node as posed then."""
import bmesh
import bpy
from mathutils import Matrix, Vector


def split(obj, names, prefix="orig", into="parts", attach=True):
    """One object per part in the collection `into`; with `attach`, parented to the reference's animated nodes."""
    col = bpy.data.collections.get(into) or bpy.data.collections.new(into)
    if col.name not in bpy.context.scene.collection.children:
        bpy.context.scene.collection.children.link(col)
    labels = [a.value for a in obj.data.attributes["part"].data]
    made = {}
    for node in sorted(set(labels)):
        empty = bpy.data.objects[f"{prefix}_{node:02d}"]
        bm = bmesh.new()
        bm.from_mesh(obj.data)
        bm.faces.ensure_lookup_table()
        bmesh.ops.delete(bm, geom=[f for f in bm.faces if labels[f.index] != node], context="FACES")
        # Into the node's space as it stands now.
        bmesh.ops.transform(bm, matrix=empty.matrix_world.inverted() @ obj.matrix_world, verts=bm.verts)
        name = f"n{node:02d}_{names.get(node, 'part')}"
        mesh = bpy.data.meshes.get(name) or bpy.data.meshes.new(name)
        bm.to_mesh(mesh)
        bm.free()
        mesh.materials.clear()
        for m in obj.data.materials:
            mesh.materials.append(m)
        part = bpy.data.objects.get(name) or bpy.data.objects.new(name, mesh)
        part.data = mesh
        if part.name not in col.objects:
            col.objects.link(part)
        part["node"] = node
        if attach:
            part.parent = empty
            part.matrix_parent_inverse.identity()
            part.matrix_basis.identity()
        made[node] = part
    return made


def move_pivot(node, point, prefix="orig"):
    """
    Moves a node's pivot to the world `point` (where the model's joint sits) without moving anything at the
    current frame: its translation keys and rest shift by the offset in its parent's space, its own geometry
    and its children's keys shift back. Rotations stay, so the original's tracks still apply. Records the
    offset in the node's "pivot_offset" (parent space), which the export carries.
    """
    empty = bpy.data.objects[f"{prefix}_{node:02d}"]
    parent_world = empty.parent.matrix_world if empty.parent else Matrix.Identity(4)
    offset = parent_world.inverted() @ Vector(point) - empty.matrix_basis.translation
    before = empty.matrix_world.copy()
    shift_keys(empty, offset)
    back = empty.matrix_basis.to_3x3().inverted() @ -offset
    for child in empty.children:
        if child.type == "EMPTY":
            shift_keys(child, back)
        else:
            child.data.transform(Matrix.Translation(back))
            child.data.update()
    empty["pivot_offset"] = list(Vector(empty.get("pivot_offset", (0, 0, 0))) + offset)
    bpy.context.view_layer.update()
    return before, empty.matrix_world.copy()


def shift_keys(obj, offset):
    """Adds `offset` to an object's location, keys and rest alike."""
    action = obj.animation_data.action if obj.animation_data else None
    curves = [c for c in getattr(action, "fcurves", [])] if action else []
    if not curves and action is not None:
        # Slotted actions (Blender 4.4+): the curves live in the layers' channelbags.
        for layer in action.layers:
            for strip in layer.strips:
                for bag in strip.channelbags:
                    curves += list(bag.fcurves)
    for c in curves:
        if c.data_path == "location":
            for k in c.keyframe_points:
                k.co.y += offset[c.array_index]
                k.handle_left.y += offset[c.array_index]
                k.handle_right.y += offset[c.array_index]
            c.update()
    if "rest" in obj:
        rest = list(obj["rest"])
        for i in range(3):
            rest[9 + i] += offset[i]
        obj["rest"] = rest
    obj.location = obj.location + offset
