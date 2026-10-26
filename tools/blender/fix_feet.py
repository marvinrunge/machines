"""Plants the feet (step 6 of README.md). The original walks on long toes with its boots folded up behind; a model
with flat feet, played with the original's angles, floats or digs into the ground. Here each foot follows the
original's foot (flat on the ground while that one is down), and the hip and knee are solved for every frame so
that the leg reaches it (two-bone IK), bending in the same plane as before.

    fix(legs=[(3, 4, 5), (6, 7, 8)], reference="ref")   # "ref": the untouched original, from import_classic_rig
"""
import bpy
from mathutils import Matrix, Quaternion, Vector

from import_classic_rig import fcurves, key, linear


def node(prefix, n):
    return bpy.data.objects[f"{prefix}_{n:02d}"]


def turned(matrix, rotation, about):
    """`matrix` turned by the rotation `rotation` about the world point `about`."""
    return Matrix.Translation(about) @ rotation.to_matrix().to_4x4() @ Matrix.Translation(-about) @ matrix


def solve(hip, knee, ankle, target, hint):
    """Where the knee goes so that hip-knee-ankle keeps its lengths and reaches `target`, bending towards `hint`."""
    thigh, shin = (knee - hip).length, (ankle - knee).length
    reach = target - hip
    distance = min(max(reach.length, abs(thigh - shin) + 1e-4), thigh + shin - 1e-4)
    along = reach.normalized()
    a = (thigh * thigh - shin * shin + distance * distance) / (2 * distance)
    h = max(thigh * thigh - a * a, 0) ** 0.5
    side = (hint - hip) - along * (hint - hip).dot(along)
    side = side.normalized() if side.length > 1e-6 else along.orthogonal().normalized()
    return hip + along * a + side * h, hip + along * distance


def reach(h, k, t, foot):
    """
    The thigh and shin turned so the leg ends at `foot`'s pivot, or as near as the leg reaches: (hip world,
    knee world, the ankle reached). h, k and t are the hip's, knee's and foot's world matrices as played.
    """
    new_knee, ankle = solve(h.translation, k.translation, t.translation, foot.translation, k.translation)
    # The thigh turns so it points at the new knee, the shin so it points at the ankle.
    turn = (k.translation - h.translation).rotation_difference(new_knee - h.translation)
    hip_world = turned(h, turn, h.translation)
    knee_world = turned(k, turn, h.translation)
    ankle_now = turned(t, turn, h.translation).translation
    turn = (ankle_now - knee_world.translation).rotation_difference(ankle - knee_world.translation)
    knee_world = turned(knee_world, turn, knee_world.translation)
    # Where the shin ends: the ankle, a fixed point of the shin.
    return hip_world, knee_world, knee_world @ (k.inverted() @ t.translation)


def onto(foot, ankle):
    """The foot moved (not turned) so its pivot is at `ankle`: it never comes off the shin."""
    return Matrix.Translation(ankle - foot.translation) @ foot


def soles(part, most=400):
    """
    Up to `most` of a part's vertices, in its own space, lowest at rest first: what can touch the ground. All
    of them count, not only the sole: a shin tipped far forwards digs in with its front.
    """
    world = part.matrix_world
    ordered = sorted(part.data.vertices, key=lambda v: (world @ v.co).z)
    step = max(1, len(ordered) // most)
    return [v.co.copy() for v in ordered[::step]]


def flat(rotation):
    """Only the part of a rotation about the vertical: which way a foot points, without tipping it."""
    q = rotation.normalized()
    twist = Quaternion((q.w, 0, 0, q.z))
    return twist.normalized() if twist.magnitude > 1e-6 else Quaternion()


def smooth(wanted, toe, frame, frames, cycles, weights=(1, 2, 3, 4, 3, 2, 1)):
    """The foot's place at `frame` averaged with its neighbours in the same cycle (between two markers)."""
    start = max([c for c in cycles if c <= frame], default=frames[0])
    end = min([c for c in cycles if c > frame], default=frames[-1] + 1)
    middle = len(weights) // 2
    total, place, turn, first = 0.0, Vector(), Vector((0, 0, 0, 0)), None
    for i, weight in enumerate(weights):
        f = frame + i - middle
        if (f, toe) not in wanted or not start <= f < end:
            continue
        m = wanted[f, toe]
        q = m.to_quaternion()
        if first is None:
            first = q
        if q.dot(first) < 0:
            q = -q
        total += weight
        place += m.translation * weight
        turn += Vector(q) * weight
    turn = Quaternion(turn / total).normalized()
    return Matrix.Translation(place / total) @ turn.to_matrix().to_4x4()


def fix(legs, frames=None, prefix="orig", reference="ref", rest=0, lift=0.5):
    """
    Re-keys every frame of the hip, knee and toe (foot) nodes of each leg (hip, knee, toe). The foot follows
    the original's foot: it turns as that one turns, about the point where that one meets the ground. While
    the original's foot is down (its pivot within `lift` metres of where it rests) the model's foot stands flat,
    turned only about the vertical, its lowest point on the ground; while it is up it tips as the original's
    does. It never goes through the ground, and it never comes off the shin: where the leg cannot reach as far
    as the original's stride, the foot comes in.
    """
    scene = bpy.context.scene
    frames = list(frames or range(scene.frame_start, scene.frame_end + 1))
    scene.frame_set(rest)
    resting = {toe: (node(reference, toe).matrix_world.copy(), node(prefix, toe).matrix_world.copy()) for _, _, toe in legs}
    down = {toe: node(reference, toe).matrix_world.translation.z for _, _, toe in legs}
    # The model's parts (split_parts marks them with their node), not the reference's own meshes on the same nodes.
    parts = {p.parent.name: p for p in bpy.data.objects if p.type == "MESH" and "node" in p and p.parent}
    ground = {n: soles(parts[node(prefix, n).name]) if node(prefix, n).name in parts else []
              for hip, knee, toe in legs for n in (knee, toe)}
    # First where each foot should be, frame by frame, with the leg as played.
    played, wanted = {}, {}
    for frame in frames:
        scene.frame_set(frame)
        for hip, knee, toe in legs:
            h, k, t = (node(prefix, n).matrix_world.copy() for n in (hip, knee, toe))
            played[frame, toe] = (node(prefix, hip).parent.matrix_world.copy(), h, k, t)
            original = node(reference, toe).matrix_world
            original_rest, foot_rest = resting[toe]
            standing = min(max(1 - (original.translation.z - down[toe]) / lift, 0), 1)
            turn = original.to_quaternion() @ original_rest.to_quaternion().inverted()
            turn = turn.slerp(flat(turn), standing)
            foot = (Matrix.Translation(original.translation) @ turn.to_matrix().to_4x4()
                    @ Matrix.Translation(-original_rest.translation) @ foot_rest)
            # Where the leg is too short for the original's stride, the foot comes in to where the leg reaches.
            # A foot that digs in levels out the deeper it would go (the original's toes point down where a
            # flat foot cannot), then rises.
            for _ in range(6):
                _, knee_world, ankle = reach(h, k, t, foot)
                foot = onto(foot, ankle)
                deepest = min((foot @ v).z for v in ground[toe])
                if deepest < 0:
                    tipped = foot.to_quaternion() @ foot_rest.to_quaternion().inverted()
                    level = tipped.slerp(flat(tipped), 0.6 * min(-deepest / 0.4, 1))
                    foot = Matrix.Translation(foot.translation) @ level.to_matrix().to_4x4() @ foot_rest.to_3x3().to_4x4()
                low = min([(knee_world @ v).z for v in ground[knee]] + [(foot @ v).z for v in ground[toe]])
                raise_by = -low if low < 0 else -low * standing
                foot = Matrix.Translation((0, 0, raise_by)) @ foot
            wanted[frame, toe] = foot
    # Then the feet's paths smoothed over a few frames (within each cycle): the rules above switch on and off.
    cycles = sorted(m.frame for m in scene.timeline_markers)
    for hip, knee, toe in legs:
        smoothed = {f: smooth(wanted, toe, f, frames, cycles) for f in frames}
        wanted.update({(f, toe): m for f, m in smoothed.items()})
    # Last the legs reach for them, and the feet go where the legs reach, never into the ground.
    baked = {n: [] for leg in legs for n in leg}
    for frame in frames:
        for hip, knee, toe in legs:
            above, h, k, t = played[frame, toe]
            foot = wanted[frame, toe]
            for _ in range(3):
                hip_world, knee_world, ankle = reach(h, k, t, foot)
                foot = onto(foot, ankle)
                low = min([(knee_world @ v).z for v in ground[knee]] + [(foot @ v).z for v in ground[toe]])
                if low >= 0:
                    break
                foot = Matrix.Translation((0, 0, -low)) @ foot
            baked[hip].append((frame, above.inverted() @ hip_world))
            baked[knee].append((frame, hip_world.inverted() @ knee_world))
            baked[toe].append((frame, knee_world.inverted() @ foot))
    scene.frame_set(rest)
    for n, keys in baked.items():
        obj = node(prefix, n)
        for curve in [c for c in fcurves(obj)]:
            curve.keyframe_points.clear()
        obj.pop("_last_quaternion", None)
        for frame, local in keys:
            obj.matrix_basis = local
            key(obj, frame)
    linear([node(prefix, n) for n in baked])
    scene.frame_set(rest)
