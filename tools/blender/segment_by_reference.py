"""Pre-segments a new machine model by the original's parts (step 3 of README.md).

Each face goes to the nearest visible part of the reference built by import_classic_rig.py; the labels are
smoothed and every part is made one connected piece. Hinges then get straight seams: the mesh is cut along a
plane through the joint's pivot, across the way the part reaches. The result is the face attribute "part"
(the original node index) and a face colour per part to look at.

    segment(obj, skip=(0, 1, 9, 36), joints=[(4, "split"), (11, "trim"), ...], merge={14: 13})
"""
import colorsys
from collections import Counter

import bmesh
import bpy
from mathutils import Vector
from mathutils.bvhtree import BVHTree


def reference_rig(prefix="orig"):
    """The reference's nodes as posed now: parent, pivot (world) and the world vertices of each node's meshes."""
    parent, pivot, verts = {}, {}, {}
    for o in bpy.data.collections[prefix].objects:
        if o.type == "EMPTY":
            n = int(o.name.split("_")[-1])
            parent[n] = int(o.parent.name.split("_")[-1]) if o.parent else -1
            pivot[n] = o.matrix_world.translation.copy()
        elif o.parent is not None and o.data.polygons:
            n = int(o.parent.name.split("_")[-1])
            verts.setdefault(n, []).extend((o.matrix_world @ v.co, o, i) for i, v in enumerate(o.data.vertices))
    children = {n: [c for c, p in parent.items() if p == n] for n in parent}
    return parent, pivot, verts, children


def reference_trees(prefix="orig", skip=()):
    """One BVH per original node over its meshes, in world space."""
    groups = {}
    for o in bpy.data.collections[prefix].objects:
        if o.type != "MESH" or not o.data.polygons or o.parent is None:
            continue
        node = int(o.parent.name.split("_")[-1])
        if node in skip:
            continue
        points, faces = groups.setdefault(node, ([], []))
        base = len(points)
        points.extend(o.matrix_world @ v.co for v in o.data.vertices)
        faces.extend(tuple(base + i for i in p.vertices) for p in o.data.polygons)
    return {n: BVHTree.FromPolygons(v, f) for n, (v, f) in groups.items()}


def nearest_part(trees, point):
    best, distance = None, float("inf")
    for node, tree in trees.items():
        hit = tree.find_nearest(point)
        if hit[0] is not None and hit[3] < distance:
            best, distance = node, hit[3]
    return best


def subtree(n, children):
    out = [n]
    for c in children[n]:
        out += subtree(c, children)
    return out


def joint_normal(n, parent, pivot, verts, children):
    """
    Which way a joint's part reaches: along the bone that ends at it (from its parent's pivot), else from its
    pivot to the middle of its subtree (meshes, else pivots).
    """
    bone = pivot[n] - pivot[parent[n]] if parent[n] in pivot else Vector()
    if bone.length > 0.1:
        return bone.normalized()
    points = [p for m in subtree(n, children) for p, _, _ in verts.get(m, [])]
    points = points or [pivot[c] for c in subtree(n, children)[1:]]
    middle = sum(points, Vector()) / len(points)
    return (middle - pivot[n]).normalized()


def neighbours(bm):
    bm.faces.ensure_lookup_table()
    return [[g.index for e in f.edges for g in e.link_faces if g is not f] for f in bm.faces]


def smooth(labels, adjacent, rounds):
    """Majority vote of each face and its neighbours, a few rounds: frays the seams less."""
    for _ in range(rounds):
        labels = [Counter([labels[i]] + [labels[j] for j in adjacent[i]]).most_common(1)[0][0]
                  for i in range(len(labels))]
    return labels


def one_piece(labels, adjacent):
    """Keeps each part's largest connected region; smaller islands join the part around them."""
    while True:
        seen = [False] * len(labels)
        regions = {}
        for start in range(len(labels)):
            if seen[start]:
                continue
            seen[start] = True
            stack, region = [start], []
            while stack:
                i = stack.pop()
                region.append(i)
                for j in adjacent[i]:
                    if not seen[j] and labels[j] == labels[start]:
                        seen[j] = True
                        stack.append(j)
            regions.setdefault(labels[start], []).append(region)
        changed = False
        for parts in regions.values():
            parts.sort(key=len, reverse=True)
            for island in parts[1:]:
                inside = set(island)
                around = Counter(labels[j] for i in island for j in adjacent[i] if j not in inside)
                if around:
                    best = around.most_common(1)[0][0]
                    for i in island:
                        labels[i] = best
                    changed = True
        if not changed:
            return labels


def cut_at_joints(bm, layer, world, joints, prefix="orig"):
    """
    Straight seams at hinges. For each (node, mode[, normal[, point]]) in `joints`, parents before children, the
    mesh is cut along the plane through the node's pivot (or the world `point` given: where the model's joint
    sits when the original's pivot is elsewhere), across the way it reaches (or the world `normal` given), and
    the faces are sorted by side. "split" sorts its parent's and its subtree's faces (a limb joint); "trim" only
    its subtree's faces, for a part that sits beside others of its parent (a shoulder pad on the torso).
    """
    parent, pivot, verts, children = reference_rig(prefix)
    inverse = world.inverted()
    for node, mode, *given in joints:
        inside = set(subtree(node, children))
        region = [f for f in bm.faces if f[layer] in inside or (mode == "split" and f[layer] == parent[node])]
        if not region:
            continue
        normal = Vector(given[0]).normalized() if given else joint_normal(node, parent, pivot, verts, children)
        # The plane in the mesh's own space.
        co = inverse @ (Vector(given[1]) if len(given) > 1 else pivot[node])
        no = (inverse.to_3x3().inverted().transposed() @ normal).normalized()
        edges = {e for f in region for e in f.edges}
        vs = {v for f in region for v in f.verts}
        bmesh.ops.bisect_plane(bm, geom=list(region) + list(edges) + list(vs), plane_co=co, plane_no=no)
        for f in bm.faces:
            if f[layer] in inside or (mode == "split" and f[layer] == parent[node]):
                if (f.calc_center_median() - co).dot(no) > 0:
                    if f[layer] not in inside:
                        f[layer] = node
                else:
                    f[layer] = parent[node]


def segment(obj, skip=(), joints=(), merge=None, prefix="orig", rounds=4):
    """The whole pre-segmentation; `merge` folds parts into others (node: into)."""
    merge = merge or {}
    trees = reference_trees(prefix, skip)
    world = obj.matrix_world
    bm = bmesh.new()
    bm.from_mesh(obj.data)
    layer = bm.faces.layers.int.get("part") or bm.faces.layers.int.new("part")
    labels = [nearest_part(trees, world @ f.calc_center_median()) for f in bm.faces]
    labels = [merge.get(n, n) for n in labels]
    adjacent = neighbours(bm)
    labels = one_piece(smooth(labels, adjacent, rounds), adjacent)
    for f, n in zip(bm.faces, labels):
        f[layer] = n
    cut_at_joints(bm, layer, world, joints, prefix)
    adjacent = neighbours(bm)
    labels = one_piece([f[layer] for f in bm.faces], adjacent)
    bm.to_mesh(obj.data)
    bm.free()
    return write(obj, labels)


def write(obj, labels):
    """Stores the labels as the face attribute "part" and colours the faces by part."""
    mesh = obj.data
    attr = mesh.attributes.get("part")
    if attr is not None and (attr.data_type, attr.domain) != ("INT", "FACE"):
        mesh.attributes.remove(attr)
        attr = None
    attr = attr or mesh.attributes.new("part", "INT", "FACE")
    attr.data.foreach_set("value", labels)
    nodes = sorted(set(labels))
    colour = {n: colorsys.hsv_to_rgb((i * 0.618) % 1, 0.85, 1) for i, n in enumerate(nodes)}
    name = "part_colour"
    if name in mesh.color_attributes:
        mesh.color_attributes.remove(mesh.color_attributes[name])
    corner = mesh.color_attributes.new(name, "BYTE_COLOR", "CORNER")
    for p in mesh.polygons:
        c = (*colour[labels[p.index]], 1)
        for li in p.loop_indices:
            corner.data[li].color = c
    mesh.color_attributes.active_color = corner
    return Counter(labels)
