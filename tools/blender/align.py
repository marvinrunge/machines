"""Puts a new model onto the original (step 2 of README.md): turned to face +X like the original, scaled to the
reference's height and stood on the ground."""
import math

import bpy
from mathutils import Vector


def world_bounds(objects):
    points = [o.matrix_world @ v.co for o in objects if o.type == "MESH" for v in o.data.vertices]
    return Vector(map(min, *points)), Vector(map(max, *points))


def align(obj, prefix="orig", turn=90.0):
    """`turn`: degrees about z that make the model face +X (Meshy models face -Y: 90)."""
    bpy.context.view_layer.update()
    low, high = world_bounds([o for o in bpy.data.collections[prefix].objects if o.type == "MESH"])
    obj.rotation_euler = (0, 0, math.radians(turn))
    obj.scale = (1, 1, 1)
    obj.location = (0, 0, 0)
    bpy.context.view_layer.update()
    mine_low, mine_high = world_bounds([obj])
    scale = (high.z - max(low.z, 0)) / (mine_high.z - mine_low.z)
    obj.scale = (scale, scale, scale)
    obj.location = (0, 0, -mine_low.z * scale)
    bpy.context.view_layer.update()
    return scale
