"""Moves each pivot onto the model's own joint (step 5 of README.md): the point a part turns about so that,
over all the cycles, it stays closest to its parent (the gap check_joints.py measures). The original's rotations
stay; only the point they turn about moves, so the motion follows the model's joints instead of the original's.

    fit_all(parts, frames)  ->  {node: (world point, gap before, gap after)}, applied with split_parts.move_pivot
"""
import bpy
from mathutils import Matrix, Vector
from mathutils.bvhtree import BVHTree

import check_joints
import split_parts


def parent_part(child, parts):
    """The part on the nearest node above the child's node that has one."""
    above = child.parent.parent if child.parent else None
    while above is not None:
        found = next((p for p in parts if p.parent == above), None)
        if found is not None:
            return found
        above = above.parent
    return None


def samples(child, parent, frames, rest):
    """Per frame, what moving the child's pivot does in its parent's space (see `gap`)."""
    scene = bpy.context.scene
    node = child.parent
    scene.frame_set(rest)
    rest_rotation = node.matrix_basis.to_3x3()
    out = []
    for frame in frames:
        scene.frame_set(frame)
        into_parent = parent.matrix_world.inverted() @ node.parent.matrix_world
        local = node.matrix_basis
        turn = local.to_3x3() @ rest_rotation.inverted()
        # A child vertex x lands at  into_parent @ (local @ x + (I - turn) @ d)  for a pivot moved by d.
        out.append((frame, into_parent @ local, into_parent.to_3x3() @ (Matrix.Identity(3) - turn),
                    turn.to_quaternion().angle))
    scene.frame_set(rest)
    return out


def gap(points, frames, tree, d, worst=float("inf")):
    """The largest 90th-percentile distance from the parent, over the frames, for the pivot moved by d."""
    largest = 0.0
    for _, place, shift, _ in frames:
        offset = shift @ d
        distances = sorted(tree.find_nearest(place @ x + offset)[3] for x in points)
        largest = max(largest, distances[int(0.9 * (len(distances) - 1))])
        if largest >= worst:
            break
    return largest


def fit(child, parent, frames, rest=0, reach=0.25, span=1.5, most_points=40, most_frames=40, pull=0.05):
    """The best pivot for `child` as a world point at rest, with the gap before and after."""
    touching = check_joints.contacts(child, parent, reach)
    if not touching:
        return None
    step = max(1, len(touching) // most_points)
    points = [child.data.vertices[i].co.copy() for i in touching[::step]]
    mesh = parent.data
    tree = BVHTree.FromPolygons([v.co for v in mesh.vertices], [p.vertices for p in mesh.polygons])
    moving = samples(child, parent, frames, rest)
    # The frames that turn the joint most decide; the rest barely move it.
    moving = sorted(moving, key=lambda s: -s[3])[:most_frames]
    before = gap(points, moving, tree, Vector())
    best, score = Vector(), before
    for size in (span / 3, span / 9, span / 27):
        centre = best.copy()
        for i in range(-3, 4):
            for j in range(-3, 4):
                for k in range(-3, 4):
                    d = centre + Vector((i, j, k)) * size
                    # A little pull towards the original's pivot keeps a flat search from wandering.
                    value = gap(points, moving, tree, d, score - pull * d.length) + pull * d.length
                    if value < score:
                        best, score = d, value
    node = child.parent
    bpy.context.scene.frame_set(rest)
    point = node.parent.matrix_world @ (node.matrix_basis.translation + best)
    return point, before, gap(points, moving, tree, best)


def gap_at(child, parent, point, frames, rest=0, reach=0.25, most_points=40, most_frames=40):
    """The gap `fit` would measure with the child's pivot at the world `point` (at rest)."""
    touching = check_joints.contacts(child, parent, reach)
    if not touching:
        return None
    step = max(1, len(touching) // most_points)
    points = [child.data.vertices[i].co.copy() for i in touching[::step]]
    mesh = parent.data
    tree = BVHTree.FromPolygons([v.co for v in mesh.vertices], [p.vertices for p in mesh.polygons])
    moving = sorted(samples(child, parent, frames, rest), key=lambda s: -s[3])[:most_frames]
    node = child.parent
    d = node.parent.matrix_world.inverted() @ Vector(point) - node.matrix_basis.translation
    return gap(points, moving, tree, d)


def round_pieces(obj, low=0.3, high=3.0, roundness=0.12, smallest=30):
    """
    The model's ball joints: pieces (shells) whose vertices lie on a sphere, as (world centre, radius). Their
    centres are where the model's joints really turn.
    """
    import math

    import bmesh
    import numpy

    import segment_by_reference
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    shell = segment_by_reference.shells_of(bm)
    verts = {}
    for f in bm.faces:
        for v in f.verts:
            verts.setdefault(shell[f.index], set()).add(v)
    found = []
    for vs in verts.values():
        if len(vs) < smallest:
            continue
        points = [obj.matrix_world @ v.co for v in vs]
        a = numpy.array([[2 * p.x, 2 * p.y, 2 * p.z, 1] for p in points])
        b = numpy.array([p.length_squared for p in points])
        solution = numpy.linalg.lstsq(a, b, rcond=None)[0]
        centre = Vector(solution[:3])
        radius = math.sqrt(max(solution[3] + centre.length_squared, 0))
        error = math.sqrt(sum(((p - centre).length - radius) ** 2 for p in points) / len(points))
        if low < radius < high and error < roundness * radius:
            found.append((centre, radius))
    bm.free()
    return found


def to_balls(nodes, balls, prefix="orig", reach=2.5):
    """Moves each node's pivot to the centre of the round piece nearest to it, if one is within `reach`."""
    moved = {}
    for node in nodes:
        pivot = bpy.data.objects[f"{prefix}_{node:02d}"].matrix_world.translation
        near = [((centre - pivot).length, centre) for centre, _ in balls]
        if near:
            distance, centre = min(near, key=lambda n: n[0])
            if distance <= reach:
                split_parts.move_pivot(node, centre)
                moved[node] = centre
    return moved


def fit_all(parts, frames, rest=0, only=None, mirror=(), apply=True, **options):
    """
    Fits the pivots of the parts whose nodes are in `only` (all when None) and moves them with
    split_parts.move_pivot. `mirror` pairs left and right nodes ((3, 6), ...): their points are averaged across
    the model's middle (y = 0), so both sides turn about the same joint. Returns {node: (point, before, after)}.
    """
    results = {}
    for child in parts:
        node = child.get("node")
        parent = parent_part(child, parts)
        if (only is not None and node not in only) or parent is None:
            continue
        found = fit(child, parent, frames, rest, **options)
        if found is not None:
            results[node] = found
    for left, right in mirror:
        if left in results and right in results:
            a, b = results[left][0], results[right][0]
            middle = (a + Vector((b.x, -b.y, b.z))) / 2
            results[left] = (middle, results[left][1], None)
            results[right] = (Vector((middle.x, -middle.y, middle.z)), results[right][1], None)
    if apply:
        for node, (point, _, _) in results.items():
            split_parts.move_pivot(node, point)
    return results
