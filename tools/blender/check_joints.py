"""Measures how well the parts stay together (step 7 of README.md): for each part, the vertices that touch its
parent in the rest pose, and how far they move away from the parent's surface over the cycles. A joint whose
pivot sits where the model's joint is barely opens; a large number means the pivot (or the seam) is off."""
import bpy
from mathutils.bvhtree import BVHTree


def world_tree(obj):
    mesh = obj.data
    return BVHTree.FromPolygons([obj.matrix_world @ v.co for v in mesh.vertices], [p.vertices for p in mesh.polygons])


def contacts(child, parent, reach=0.25):
    """The child's vertices within `reach` (world metres) of its parent, as they stand now."""
    tree = world_tree(parent)
    out = []
    for v in child.data.vertices:
        hit = tree.find_nearest(child.matrix_world @ v.co)
        if hit[0] is not None and hit[3] <= reach:
            out.append(v.index)
    return out


def joint_gaps(parts, frames, rest=0, reach=0.25):
    """
    {child name: (largest gap in metres, the frame it happens at, contact vertices)} for every part whose parent
    node also has a part. The gap at a frame is the 90th percentile of the contact vertices' distance to the
    parent: a joint that opens on one side shows; a stray vertex does not.
    """
    scene = bpy.context.scene
    scene.frame_set(rest)
    pairs = []
    for child in parts:
        node = bpy.data.objects.get(child.parent.name) if child.parent else None
        above = node.parent if node else None
        parent = None
        while above is not None and parent is None:
            parent = next((p for p in parts if p.parent == above), None)
            above = above.parent
        if parent is None:
            continue
        touching = contacts(child, parent, reach)
        if touching:
            pairs.append((child, parent, touching))
    worst = {child.name: (0.0, rest, len(touching)) for child, _, touching in pairs}
    for frame in frames:
        scene.frame_set(frame)
        for child, parent, touching in pairs:
            tree = world_tree(parent)
            distances = sorted(tree.find_nearest(child.matrix_world @ child.data.vertices[i].co)[3] for i in touching)
            gap = distances[int(0.9 * (len(distances) - 1))]
            if gap > worst[child.name][0]:
                worst[child.name] = (gap, frame, len(touching))
    scene.frame_set(rest)
    return worst
