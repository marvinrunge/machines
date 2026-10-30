"""Renders the weather's animated flipbooks (game/assets/particles/flipbooks) in Blender, from procedural volumes.

    blender --background --factory-startup --python tools/blender/flipbooks.py -- <output folder> [frames] [size] [name ...]

Each flipbook is a square sheet of 4 x 4 frames (16 by default), white on a transparent background, so the game
tints it: a billowing cloud for storms (grows and thins over its life), a dust devil's funnel turning once round
(it loops), and a splash ring for rain. For the weapons, coloured as they burn (the game does not tint them): a
fireball (a burst of fire cooling into smoke) and a flame (a tongue of fire licking up and dying away). Names pick
some of them; all are made without. Rendered with Cycles, on the GPU when there is one.
"""
import math
import os
import sys

import bpy


def arguments():
    args = sys.argv[sys.argv.index("--") + 1:] if "--" in sys.argv else []
    out = args[0] if args else os.path.join(os.getcwd(), "flipbooks")
    frames = int(args[1]) if len(args) > 1 else 16
    size = int(args[2]) if len(args) > 2 else 256
    return out, frames, size, set(args[3:])


def reset():
    bpy.ops.wm.read_factory_settings(use_empty=True)
    scene = bpy.context.scene
    scene.render.engine = "CYCLES"
    scene.cycles.samples = 64
    scene.cycles.use_denoising = True
    scene.render.film_transparent = True
    scene.render.image_settings.file_format = "PNG"
    scene.render.image_settings.color_mode = "RGBA"
    scene.view_settings.view_transform = "Standard"
    scene.cycles.volume_step_rate = 0.5
    scene.cycles.volume_max_steps = 512
    # The GPU, when Cycles finds one.
    prefs = bpy.context.preferences.addons["cycles"].preferences
    for kind in ("OPTIX", "CUDA", "HIP", "ONEAPI", "METAL"):
        try:
            prefs.compute_device_type = kind
        except TypeError:
            continue
        prefs.get_devices()
        if any(d.type == kind for d in prefs.devices):
            for d in prefs.devices:
                d.use = d.type == kind
            scene.cycles.device = "GPU"
            break
    world = bpy.data.worlds.new("World")
    world.use_nodes = True
    # Bright and even: the game tints the pictures and lights them by the time of day.
    world.node_tree.nodes["Background"].inputs["Strength"].default_value = 2.2
    scene.world = world
    sun = bpy.data.objects.new("Sun", bpy.data.lights.new("Sun", "SUN"))
    sun.data.energy = 4.5
    sun.rotation_euler = (math.radians(35), math.radians(-25), 0)
    scene.collection.objects.link(sun)
    return scene


def camera(scene, width, height, at_z):
    cam = bpy.data.objects.new("Camera", bpy.data.cameras.new("Camera"))
    cam.data.type = "ORTHO"
    cam.data.ortho_scale = max(width, height)
    cam.location = (0, -20, at_z)
    cam.rotation_euler = (math.radians(90), 0, 0)
    scene.collection.objects.link(cam)
    scene.camera = cam


def node(tree, kind, **inputs):
    n = tree.nodes.new(kind)
    for name, value in inputs.items():
        n.inputs[name].default_value = value
    return n


def volume_material(name, colour):
    """A volume whose density comes from the 'density' node group the caller fills in."""
    mat = bpy.data.materials.new(name)
    mat.use_nodes = True
    tree = mat.node_tree
    tree.nodes.clear()
    output = tree.nodes.new("ShaderNodeOutputMaterial")
    volume = node(tree, "ShaderNodeVolumePrincipled", Color=(*colour, 1), Anisotropy=0.3)
    tree.links.new(volume.outputs["Volume"], output.inputs["Volume"])
    return mat, tree, volume


def billow(scene, frames):
    """A cloud of dust or snow: a ball of noise that swells and thins over its life."""
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=1)
    ball = bpy.context.active_object
    mat, tree, volume = volume_material("Billow", (1, 1, 1))
    coords = tree.nodes.new("ShaderNodeTexCoord")
    noise = node(tree, "ShaderNodeTexNoise", Scale=1.6, Detail=6.0, Roughness=0.62)
    noise.noise_dimensions = "4D"
    distance = tree.nodes.new("ShaderNodeVectorMath")
    distance.operation = "LENGTH"
    tree.links.new(coords.outputs["Object"], noise.inputs["Vector"])
    tree.links.new(coords.outputs["Object"], distance.inputs[0])
    # Density: noise above a threshold that rises with age, inside a ball that fades towards its edge.
    edge = node(tree, "ShaderNodeMapRange", **{"From Min": 0.55, "From Max": 1.0, "To Min": 1.0, "To Max": 0.0})
    tree.links.new(distance.outputs["Value"], edge.inputs["Value"])
    threshold = node(tree, "ShaderNodeMapRange", **{"From Min": 0.45, "From Max": 0.75, "To Min": 0.0, "To Max": 1.0})
    tree.links.new(noise.outputs["Fac"], threshold.inputs["Value"])
    times = tree.nodes.new("ShaderNodeMath")
    times.operation = "MULTIPLY"
    tree.links.new(threshold.outputs["Result"], times.inputs[0])
    tree.links.new(edge.outputs["Result"], times.inputs[1])
    strength = tree.nodes.new("ShaderNodeMath")
    strength.operation = "MULTIPLY"
    tree.links.new(times.outputs["Value"], strength.inputs[0])
    tree.links.new(strength.outputs["Value"], volume.inputs["Density"])
    ball.data.materials.append(mat)
    camera(scene, 2.2, 2.2, 0)
    for f in range(frames):
        t = f / (frames - 1)
        scene.frame_set(f + 1)
        # It swells, its noise rolls on, and it thins out.
        s = 0.75 + 0.25 * t
        ball.scale = (s, s, s)
        noise.inputs["W"].default_value = t * 1.6
        threshold.inputs["From Min"].default_value = 0.42 + 0.12 * t
        strength.inputs[1].default_value = 7.0 * (1 - 0.6 * t)
        yield f


def devil(scene, frames):
    """A dust devil's funnel: twisted noise in a cone, turning once round over the flipbook so that it loops."""
    bpy.ops.mesh.primitive_cone_add(vertices=48, radius1=0.25, radius2=1.4, depth=6, location=(0, 0, 3))
    funnel = bpy.context.active_object
    mat, tree, volume = volume_material("Devil", (1, 1, 1))
    coords = tree.nodes.new("ShaderNodeTexCoord")
    # Turn each height by an angle that grows with it (the twist); the object turns as a whole for the spin.
    separate = tree.nodes.new("ShaderNodeSeparateXYZ")
    tree.links.new(coords.outputs["Object"], separate.inputs["Vector"])
    twist = tree.nodes.new("ShaderNodeVectorRotate")
    twist.rotation_type = "Z_AXIS"
    angle = tree.nodes.new("ShaderNodeMath")
    angle.operation = "MULTIPLY"
    angle.inputs[1].default_value = 1.1
    tree.links.new(separate.outputs["Z"], angle.inputs[0])
    tree.links.new(angle.outputs["Value"], twist.inputs["Angle"])
    tree.links.new(coords.outputs["Object"], twist.inputs["Vector"])
    stretch = tree.nodes.new("ShaderNodeVectorMath")
    stretch.operation = "MULTIPLY"
    stretch.inputs[1].default_value = (3.0, 3.0, 0.6)
    tree.links.new(twist.outputs["Vector"], stretch.inputs[0])
    noise = node(tree, "ShaderNodeTexNoise", Scale=1.3, Detail=5.0, Roughness=0.6)
    tree.links.new(stretch.outputs["Vector"], noise.inputs["Vector"])
    threshold = node(tree, "ShaderNodeMapRange", **{"From Min": 0.48, "From Max": 0.72, "To Min": 0.0, "To Max": 1.0})
    tree.links.new(noise.outputs["Fac"], threshold.inputs["Value"])
    # Thicker at the bottom, thin at the top.
    height = node(tree, "ShaderNodeMapRange", **{"From Min": -3.0, "From Max": 3.0, "To Min": 1.0, "To Max": 0.0})
    tree.links.new(separate.outputs["Z"], height.inputs["Value"])
    # Soft at its wall and thinner in its eye: the distance from the axis against the funnel's radius at that height.
    across = tree.nodes.new("ShaderNodeCombineXYZ")
    tree.links.new(separate.outputs["X"], across.inputs["X"])
    tree.links.new(separate.outputs["Y"], across.inputs["Y"])
    off_axis = tree.nodes.new("ShaderNodeVectorMath")
    off_axis.operation = "LENGTH"
    tree.links.new(across.outputs["Vector"], off_axis.inputs[0])
    radius = node(tree, "ShaderNodeMapRange", **{"From Min": -3.0, "From Max": 3.0, "To Min": 0.25, "To Max": 1.4})
    tree.links.new(separate.outputs["Z"], radius.inputs["Value"])
    share = tree.nodes.new("ShaderNodeMath")
    share.operation = "DIVIDE"
    tree.links.new(off_axis.outputs["Value"], share.inputs[0])
    tree.links.new(radius.outputs["Result"], share.inputs[1])
    wall = node(tree, "ShaderNodeFloatCurve")
    curve = wall.mapping.curves[0]
    curve.points[0].location = (0.0, 0.35)
    curve.points[1].location = (1.0, 0.0)
    curve.points.new(0.5, 1.0)
    curve.points.new(0.8, 0.55)
    wall.mapping.update()
    tree.links.new(share.outputs["Value"], wall.inputs["Value"])
    shaped = tree.nodes.new("ShaderNodeMath")
    shaped.operation = "MULTIPLY"
    tree.links.new(height.outputs["Result"], shaped.inputs[0])
    tree.links.new(wall.outputs["Value"], shaped.inputs[1])
    times = tree.nodes.new("ShaderNodeMath")
    times.operation = "MULTIPLY"
    tree.links.new(threshold.outputs["Result"], times.inputs[0])
    tree.links.new(shaped.outputs["Value"], times.inputs[1])
    strength = tree.nodes.new("ShaderNodeMath")
    strength.operation = "MULTIPLY"
    strength.inputs[1].default_value = 6.0
    tree.links.new(times.outputs["Value"], strength.inputs[0])
    tree.links.new(strength.outputs["Value"], volume.inputs["Density"])
    funnel.data.materials.append(mat)
    camera(scene, 3.2, 6.4, 3)
    for f in range(frames):
        scene.frame_set(f + 1)
        funnel.rotation_euler = (0, 0, -math.tau * f / frames)
        yield f


def splash(scene, frames):
    """A raindrop's splash: a crown of droplets thrown out and falling back, and a ring spreading on the ground."""
    import random
    random.seed(3)
    white = bpy.data.materials.new("Water")
    white.use_nodes = True
    emission = white.node_tree.nodes.new("ShaderNodeEmission")
    emission.inputs["Strength"].default_value = 1.0
    white.node_tree.links.new(emission.outputs["Emission"], white.node_tree.nodes["Material Output"].inputs["Surface"])
    drops = []
    for i in range(14):
        bpy.ops.mesh.primitive_uv_sphere_add(segments=8, ring_count=6, radius=0.05)
        drop = bpy.context.active_object
        drop.data.materials.append(white)
        heading = math.tau * i / 14 + random.uniform(-0.15, 0.15)
        drops.append((drop, heading, random.uniform(0.8, 1.2)))
    bpy.ops.mesh.primitive_torus_add(major_radius=0.2, minor_radius=0.02, major_segments=48, minor_segments=6)
    ring = bpy.context.active_object
    ring.data.materials.append(white)
    camera(scene, 2.0, 2.0, 0.55)
    cam = scene.camera
    # Seen a little from above, so the ring on the ground shows.
    cam.location = (0, -20, 6)
    cam.rotation_euler = (math.radians(73), 0, 0)
    for f in range(frames):
        t = f / (frames - 1)
        scene.frame_set(f + 1)
        for drop, heading, speed in drops:
            r = 0.15 + 0.75 * t * speed
            z = 2.4 * t * speed * (1 - t)
            drop.location = (r * math.cos(heading), r * math.sin(heading), z)
            s = 1 - 0.7 * t
            drop.scale = (s, s, s * (1.6 if t < 0.3 else 1))
        ring.scale = (1 + 3.5 * t, 1 + 3.5 * t, 1)
        emission.inputs["Strength"].default_value = 1.0 - 0.85 * t
        yield f


def fire_material(name, taper=0.0):
    """
    A burning volume: density from noise the caller shapes (see fireball and flame), and where it is hot a glow whose
    colour runs from deep red through orange to a yellow core; where it has cooled, dark smoke. With a taper the ball
    narrows towards its top to that share of its width, a tongue of flame. Returns the material and the nodes the
    frames change: the noise, where it is sampled from, its threshold, the density's strength and the heat's.
    """
    mat, tree, volume = volume_material(name, (0.16, 0.15, 0.14))
    volume.inputs["Absorption Color"].default_value = (0.02, 0.02, 0.02, 1)
    coords = tree.nodes.new("ShaderNodeTexCoord")
    shift = tree.nodes.new("ShaderNodeMapping")
    tree.links.new(coords.outputs["Object"], shift.inputs["Vector"])
    noise = node(tree, "ShaderNodeTexNoise", Scale=2.4, Detail=5.0, Roughness=0.62)
    noise.noise_dimensions = "4D"
    tree.links.new(shift.outputs["Vector"], noise.inputs["Vector"])
    shape = coords.outputs["Object"]
    if taper:
        # Divide the width at each height by how wide the tongue is there: full at its foot, the taper at its top.
        separate = tree.nodes.new("ShaderNodeSeparateXYZ")
        tree.links.new(coords.outputs["Object"], separate.inputs["Vector"])
        width = node(tree, "ShaderNodeMapRange", **{"From Min": -1.0, "From Max": 1.0, "To Min": 1.0, "To Max": taper})
        tree.links.new(separate.outputs["Z"], width.inputs["Value"])
        narrowed = tree.nodes.new("ShaderNodeCombineXYZ")
        for axis in ("X", "Y"):
            divide = tree.nodes.new("ShaderNodeMath")
            divide.operation = "DIVIDE"
            tree.links.new(separate.outputs[axis], divide.inputs[0])
            tree.links.new(width.outputs["Result"], divide.inputs[1])
            tree.links.new(divide.outputs["Value"], narrowed.inputs[axis])
        tree.links.new(separate.outputs["Z"], narrowed.inputs["Z"])
        shape = narrowed.outputs["Vector"]
    distance = tree.nodes.new("ShaderNodeVectorMath")
    distance.operation = "LENGTH"
    tree.links.new(shape, distance.inputs[0])
    edge = node(tree, "ShaderNodeMapRange", **{"From Min": 0.3, "From Max": 1.0, "To Min": 1.0, "To Max": 0.0})
    tree.links.new(distance.outputs["Value"], edge.inputs["Value"])
    threshold = node(tree, "ShaderNodeMapRange", **{"From Min": 0.42, "From Max": 0.68, "To Min": 0.0, "To Max": 1.0})
    tree.links.new(noise.outputs["Fac"], threshold.inputs["Value"])
    shaped = tree.nodes.new("ShaderNodeMath")
    shaped.operation = "MULTIPLY"
    tree.links.new(threshold.outputs["Result"], shaped.inputs[0])
    tree.links.new(edge.outputs["Result"], shaped.inputs[1])
    density = tree.nodes.new("ShaderNodeMath")
    density.operation = "MULTIPLY"
    tree.links.new(shaped.outputs["Value"], density.inputs[0])
    tree.links.new(density.outputs["Value"], volume.inputs["Density"])
    # The heat: hottest where it is dense and deep inside, scaled by how far the fire has burnt out.
    inner = tree.nodes.new("ShaderNodeMath")
    inner.operation = "MULTIPLY"
    tree.links.new(shaped.outputs["Value"], inner.inputs[0])
    tree.links.new(edge.outputs["Result"], inner.inputs[1])
    heat = tree.nodes.new("ShaderNodeMath")
    heat.operation = "MULTIPLY"
    heat.use_clamp = True
    tree.links.new(inner.outputs["Value"], heat.inputs[0])
    ramp = tree.nodes.new("ShaderNodeValToRGB")
    stops = ramp.color_ramp.elements
    stops[0].position, stops[0].color = 0.0, (0.0, 0.0, 0.0, 1)
    stops[1].position, stops[1].color = 1.0, (1.0, 0.82, 0.42, 1)
    for at, colour in ((0.08, (0.3, 0.025, 0.0, 1)), (0.3, (0.9, 0.18, 0.015, 1)), (0.6, (1.0, 0.5, 0.08, 1))):
        stop = stops.new(at)
        stop.color = colour
    tree.links.new(heat.outputs["Value"], ramp.inputs["Fac"])
    tree.links.new(ramp.outputs["Color"], volume.inputs["Emission Color"])
    glow = tree.nodes.new("ShaderNodeMath")
    glow.operation = "MULTIPLY"
    glow.inputs[1].default_value = 3.5
    tree.links.new(heat.outputs["Value"], glow.inputs[0])
    tree.links.new(glow.outputs["Value"], volume.inputs["Emission Strength"])
    return mat, noise, shift, threshold, density, heat


def fireball(scene, frames):
    """A burst of fire: a glowing ball that swells, tears into tatters and cools into dark smoke."""
    bpy.ops.mesh.primitive_ico_sphere_add(subdivisions=3, radius=1)
    ball = bpy.context.active_object
    mat, noise, _, threshold, density, heat = fire_material("Fireball")
    ball.data.materials.append(mat)
    camera(scene, 2.2, 2.2, 0)
    for f in range(frames):
        t = f / (frames - 1)
        scene.frame_set(f + 1)
        s = 0.5 + 0.5 * t ** 0.5
        ball.scale = (s, s, s)
        noise.inputs["W"].default_value = t * 1.2
        threshold.inputs["From Min"].default_value = 0.4 + 0.14 * t
        density.inputs[1].default_value = 5.0 * (1 - 0.5 * t)
        heat.inputs[1].default_value = 2.4 * max(0.0, 1 - t / 0.75) ** 1.4
        yield f


def flame(scene, frames):
    """A tongue of flame, broad at its foot and pointed at its top: it leaps up hot and yellow, its fire rising through
    it, and reddens, tears and dies away."""
    bpy.ops.mesh.primitive_uv_sphere_add(segments=32, ring_count=16, radius=1)
    tongue = bpy.context.active_object
    mat, noise, shift, threshold, density, heat = fire_material("Flame", taper=0.2)
    noise.inputs["Scale"].default_value = 2.6
    tongue.data.materials.append(mat)
    camera(scene, 1.1, 2.2, 0)
    for f in range(frames):
        t = f / (frames - 1)
        scene.frame_set(f + 1)
        # Twice as tall as wide, rising a little and thinning as it goes.
        tongue.location = (0, 0, -0.05 + 0.15 * t)
        s = 1 - 0.3 * t
        tongue.scale = (0.5 * s, 0.5 * s, 1.0)
        shift.inputs["Location"].default_value = (0, 0, -1.4 * t)
        shift.inputs["Scale"].default_value = (1, 1, 0.55)
        noise.inputs["W"].default_value = t * 0.8
        threshold.inputs["From Min"].default_value = 0.36 + 0.14 * t
        density.inputs[1].default_value = 9.0 * (1 - 0.4 * t)
        heat.inputs[1].default_value = 3.0 * (1 - 0.6 * t)
        yield f


def sheet(paths, out, columns, gain):
    """
    The frames side by side in a square sheet, left to right and top to bottom, their colour brightened by gain (the
    volumes shade themselves grey inside; the game wants them near white, to tint).
    """
    images = [bpy.data.images.load(p) for p in paths]
    w, h = images[0].size
    rows = math.ceil(len(images) / columns)
    result = bpy.data.images.new("sheet", w * columns, h * rows, alpha=True)
    pixels = [0.0] * (w * columns * h * rows * 4)
    for i, image in enumerate(images):
        cx, cy = i % columns, rows - 1 - i // columns
        source = list(image.pixels)
        for k in range(0, len(source), 4):
            source[k] = min(1.0, source[k] * gain)
            source[k + 1] = min(1.0, source[k + 1] * gain)
            source[k + 2] = min(1.0, source[k + 2] * gain)
        for y in range(h):
            row = (cy * h + y) * w * columns + cx * w
            pixels[row * 4:(row + w) * 4] = source[y * w * 4:(y + 1) * w * 4]
    result.pixels = pixels
    result.filepath_raw = out
    result.file_format = "PNG"
    result.save()


def main():
    out, frames, size, only = arguments()
    os.makedirs(out, exist_ok=True)
    for name, make, (rx, ry), gain in (("billow", billow, (1, 1), 1.8), ("devil", devil, (1, 2), 1.5), ("splash", splash, (1, 1), 1.0),
                                        ("fireball", fireball, (1, 1), 1.0), ("flame", flame, (1, 2), 1.0)):
        if only and name not in only:
            continue
        scene = reset()
        # Fire glows inside: more samples, or its edge stays grainy.
        if name in ("fireball", "flame"):
            scene.cycles.samples = 256
        # The devil is twice as tall as wide; its frames are too.
        scene.render.resolution_x = size * rx // ry
        scene.render.resolution_y = size
        paths = []
        for f in make(scene, frames):
            path = os.path.join(out, f"_{name}_{f:02d}.png")
            scene.render.filepath = path
            bpy.ops.render.render(write_still=True)
            paths.append(path)
        sheet(paths, os.path.join(out, f"{name}.png"), int(math.sqrt(frames)), gain)
        for p in paths:
            os.remove(p)
        print(f"flipbook {name}: {frames} frames")


main()
