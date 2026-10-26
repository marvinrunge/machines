"""A stage to look at a rigged machine (step 7 of README.md): ground, sun, sky and a camera at three-quarter front,
the cycle's name stamped on the picture, and the whole timeline rendered to an .mp4.

    stage(r"<video.mp4>")  then  blender -b <file.blend> -a  (or render from the open file)
"""
import math

import bpy
from mathutils import Vector


def stage(video, target=(0, 0, 6.5), eye=(30, -26, 12), size=(1280, 720)):
    scene = bpy.context.scene
    if "ground" not in bpy.data.objects:
        bpy.ops.mesh.primitive_plane_add(size=200, location=(0, 0, 0))
        ground = bpy.context.active_object
        ground.name = "ground"
        material = bpy.data.materials.new("ground")
        material.use_nodes = True
        surface = next(n for n in material.node_tree.nodes if n.type == "BSDF_PRINCIPLED")
        surface.inputs["Base Color"].default_value = (0.55, 0.42, 0.28, 1)
        surface.inputs["Roughness"].default_value = 0.9
        ground.data.materials.append(material)
    if "sun" not in bpy.data.objects:
        light = bpy.data.lights.new("sun", "SUN")
        light.energy = 4
        light.angle = math.radians(3)
        sun = bpy.data.objects.new("sun", light)
        scene.collection.objects.link(sun)
        sun.rotation_euler = (math.radians(50), 0, math.radians(-35))
    if scene.world is None:
        scene.world = bpy.data.worlds.new("world")
    scene.world.use_nodes = True
    sky = next(n for n in scene.world.node_tree.nodes if n.type == "BACKGROUND")
    sky.inputs[0].default_value = (0.45, 0.55, 0.7, 1)
    sky.inputs[1].default_value = 0.9
    camera = bpy.data.objects.get("cam")
    if camera is None:
        camera = bpy.data.objects.new("cam", bpy.data.cameras.new("cam"))
        camera.data.lens = 40
        scene.collection.objects.link(camera)
    camera.location = eye
    camera.rotation_euler = (Vector(target) - Vector(eye)).to_track_quat("-Z", "Y").to_euler()
    scene.camera = camera

    render = scene.render
    engines = [e.identifier for e in bpy.types.RenderSettings.bl_rna.properties["engine"].enum_items]
    render.engine = next(e for e in ("BLENDER_EEVEE_NEXT", "BLENDER_EEVEE") if e in engines)
    render.resolution_x, render.resolution_y = size
    render.resolution_percentage = 100
    render.use_stamp = True
    for name in dir(render):
        if name.startswith("use_stamp_") and name not in ("use_stamp_note",):
            try:
                setattr(render, name, False)
            except (AttributeError, TypeError):
                pass
    render.use_stamp_marker = True
    render.use_stamp_frame = True
    render.stamp_font_size = 28
    settings = render.image_settings
    if "media_type" in settings.bl_rna.properties:
        settings.media_type = "VIDEO"
    settings.file_format = "FFMPEG"
    render.ffmpeg.format = "MPEG4"
    render.ffmpeg.codec = "H264"
    render.ffmpeg.constant_rate_factor = "HIGH"
    render.filepath = video
    for o in bpy.data.collections["orig"].objects:
        o.hide_render = True
    scene.frame_start = 0
