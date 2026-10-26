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
    Faces of a whole shell (`shell` layer, see `whole_shells`) are not cut: the shell goes to the side its middle
    is on.
    """
    parent, pivot, verts, children = reference_rig(prefix)
    inverse = world.inverted()
    shell = bm.faces.layers.int.get("shell")
    middles = {}
    if shell is not None:
        for f in bm.faces:
            if f[shell] >= 0:
                total, count = middles.get(f[shell], (Vector(), 0))
                middles[f[shell]] = (total + f.calc_center_median(), count + 1)
        middles = {k: total / count for k, (total, count) in middles.items()}
    for node, mode, *given in joints:
        inside = set(subtree(node, children))
        region = [f for f in bm.faces if f[layer] in inside or (mode == "split" and f[layer] == parent[node])]
        if not region:
            continue
        normal = Vector(given[0]).normalized() if given else joint_normal(node, parent, pivot, verts, children)
        # The plane in the mesh's own space.
        co = inverse @ (Vector(given[1]) if len(given) > 1 else pivot[node])
        no = (inverse.to_3x3().inverted().transposed() @ normal).normalized()
        cuttable = [f for f in region if shell is None or f[shell] < 0]
        edges = {e for f in cuttable for e in f.edges}
        vs = {v for f in cuttable for v in f.verts}
        if cuttable:
            bmesh.ops.bisect_plane(bm, geom=list(cuttable) + list(edges) + list(vs), plane_co=co, plane_no=no)
        for f in bm.faces:
            if f[layer] in inside or (mode == "split" and f[layer] == parent[node]):
                at = middles[f[shell]] if shell is not None and f[shell] >= 0 else f.calc_center_median()
                if (at - co).dot(no) > 0:
                    if f[layer] not in inside:
                        f[layer] = node
                else:
                    f[layer] = parent[node]


def shells_of(bm):
    """Each face's shell: the connected piece of the mesh it belongs to."""
    shell = [-1] * len(bm.faces)
    count = 0
    for f in bm.faces:
        if shell[f.index] >= 0:
            continue
        stack = [f]
        shell[f.index] = count
        while stack:
            for e in stack.pop().edges:
                for g in e.link_faces:
                    if shell[g.index] < 0:
                        shell[g.index] = count
                        stack.append(g)
        count += 1
    return shell


def whole_shells(labels, shell, purity):
    """
    Models made of separate pieces (armour plates, claws): a shell whose faces mostly go to one part (at least
    `purity`) goes to it whole and is never cut. Returns the labels and, per face, its shell or -1 for the faces
    of mixed shells, which stay to be cut at the joints.
    """
    votes = {}
    for label, s in zip(labels, shell):
        votes.setdefault(s, Counter())[label] += 1
    whole = {s: v.most_common(1)[0][0] for s, v in votes.items() if v.most_common(1)[0][1] >= purity * sum(v.values())}
    return [whole.get(s, label) for label, s in zip(labels, shell)], [s if s in whole else -1 for s in shell]


def touching(bm, layer, world, gap, rounds=5):
    """
    A piece must hang on its part: a piece (a shell, or one side of a cut shell) belongs where a chain of pieces
    of its part, each within `gap` (world metres) of the next, leads to the part's biggest piece. Pieces with no
    such chain go to the part they touch most (claws sorted to the torso that sit on a foot).
    """
    from mathutils.kdtree import KDTree
    shell = shells_of(bm)
    tree = KDTree(len(bm.verts))
    for v in bm.verts:
        tree.insert(world @ v.co, v.index)
    tree.balance()
    shell_of_vert = {}
    for f in bm.faces:
        for v in f.verts:
            shell_of_vert.setdefault(v.index, set()).add(shell[f.index])
    # Which shells touch which: fixed, since moving labels does not move geometry.
    near = {}
    for v in bm.verts:
        mine = shell_of_vert.get(v.index, set())
        for _, index, _ in tree.find_range(world @ v.co, gap):
            for s in mine:
                near.setdefault(s, set()).update(shell_of_vert.get(index, set()) - {s})
    for _ in range(rounds):
        pieces = {}
        for f in bm.faces:
            pieces.setdefault((shell[f.index], f[layer]), []).append(f)
        # Pieces touch when their shells touch, or when they are the two sides of one cut shell.
        def contacts(piece):
            s, _ = piece
            return [p for p in pieces if p != piece and (p[0] in near.get(s, ()) or p[0] == s)]
        anchored = set()
        for label in {label for _, label in pieces}:
            start = max((p for p in pieces if p[1] == label), key=lambda p: len(pieces[p]))
            stack = [start]
            anchored.add(start)
            while stack:
                for p in contacts(stack.pop()):
                    if p[1] == label and p not in anchored:
                        anchored.add(p)
                        stack.append(p)
        changed = False
        for piece in [p for p in pieces if p not in anchored]:
            others = Counter(p[1] for p in contacts(piece) if p[1] != piece[1])
            if others:
                for f in pieces[piece]:
                    f[layer] = others.most_common(1)[0][0]
                changed = True
        if not changed:
            return


def segment(obj, skip=(), joints=(), merge=None, prefix="orig", rounds=4, shells=None, gap=0.3):
    """
    The whole pre-segmentation; `merge` folds parts into others (node: into). With `shells` (a purity such as
    0.85), a model made of separate pieces keeps its pieces whole (see `whole_shells`), and each piece ends up
    on a part it touches (`gap`, see `touching`).
    """
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
    if shells is not None:
        labels, whole = whole_shells(labels, shells_of(bm), shells)
        shell = bm.faces.layers.int.get("shell") or bm.faces.layers.int.new("shell")
        for f, s in zip(bm.faces, whole):
            f[shell] = s
    for f, n in zip(bm.faces, labels):
        f[layer] = n
    cut_at_joints(bm, layer, world, joints, prefix)
    if shells is not None:
        touching(bm, layer, world, gap)
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
