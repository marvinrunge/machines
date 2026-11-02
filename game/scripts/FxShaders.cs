using Godot;

namespace Machines.Game;

/// <summary>
/// The shaders of the remake's weapon effects (see <see cref="Effects"/>), made in code like the rest of them. Each
/// takes its colour, strength and size per instance (<see cref="GeometryInstance3D.SetInstanceShaderParameter"/>),
/// so one material serves every tracer, bolt and beam.
/// </summary>
public static class FxShaders
{
    /// <summary>
    /// A streak of light from the node's origin backwards along its z axis (its -z points where it flies), turned about
    /// that axis to face the camera: a tracer, a bolt's tail, an exhaust flame, a beam. <c>reach</c> metres long,
    /// <c>thickness</c> across at its head and <c>taper</c> of that at its end, white-hot in its core and the colour
    /// round it (<c>white</c> says how white); brightest at its head (<c>head</c> 1) or even along it (0). <c>flow</c>
    /// runs a ripple along it (beams) <c>wavelength</c> metres apart, <c>flicker</c> makes it waver (flames), <c>fade</c>
    /// dims it. It and the
    /// <see cref="Orb"/> blend premultiplied, so their glow tints what lies behind them instead of only whitening it.
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Streak = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_premul_alpha, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform vec3 colour : source_color = vec3(1.0, 0.8, 0.4);
                instance uniform float energy = 4.0;
                instance uniform float thickness = 0.15;
                instance uniform float reach = 3.0;
                instance uniform float head = 1.0;
                instance uniform float flow = 0.0;
                instance uniform float flicker = 0.0;
                instance uniform float taper = 1.0;
                instance uniform float white = 0.75;
                instance uniform float fade = 1.0;
                instance uniform float wavelength = 3.7;

                varying float along;
                varying float across;
                varying float wave;

                void vertex() {
                    vec3 back = normalize(MODEL_MATRIX[2].xyz);
                    vec3 origin = MODEL_MATRIX[3].xyz;
                    vec3 eye = normalize(CAMERA_POSITION_WORLD - origin);
                    vec3 side = cross(back, eye);
                    side = length(side) > 0.001 ? normalize(side) : vec3(1.0, 0.0, 0.0);
                    along = 0.5 - VERTEX.y;
                    across = VERTEX.x * 2.0;
                    vec3 world = origin + back * along * reach + side * VERTEX.x * thickness * mix(1.0, taper, along);
                    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
                    // Each flame wavers out of step with the others.
                    float phase = dot(origin, vec3(1.7, 3.1, 2.3));
                    wave = 1.0 - flicker * (0.5 + 0.5 * sin(TIME * 71.0 + phase) * sin(TIME * 43.0 + phase * 1.3));
                }

                void fragment() {
                    float a = abs(across);
                    float core = exp(-a * a * 28.0);
                    float glow = exp(-a * a * 4.0) * (1.0 - a);
                    float even = smoothstep(0.0, 0.05, along) * smoothstep(1.0, 0.8, along);
                    float trail = smoothstep(0.0, 0.03, along) * pow(1.0 - along, 1.6);
                    float ripple = flow == 0.0 ? 1.0 : 0.7 + 0.3 * sin(along * reach * 6.2832 / wavelength - TIME * flow);
                    vec3 hot = mix(colour, vec3(1.0), white);
                    float shape = mix(even, trail, head) * ripple * wave * fade;
                    ALBEDO = (hot * core * 1.6 + colour * glow) * energy * shape;
                    ALPHA = clamp((core + glow) * shape * 0.6, 0.0, 1.0);
                }
                """,
        },
    });

    /// <summary>
    /// A ball of energy facing the camera (a plasma or pulse bolt's head, a splat's flash): a white-hot core, a shell
    /// that swirls in the colour, and a soft halo, its size the node's scale (a unit quad). <c>swirl</c> sets how fast
    /// the shell turns, <c>fade</c> dims it.
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Orb = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_premul_alpha, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform vec3 colour : source_color = vec3(0.35, 1.0, 0.45);
                instance uniform float energy = 4.0;
                instance uniform float swirl = 14.0;
                instance uniform float fade = 1.0;

                void vertex() {
                    // Facing the camera, keeping the node's scale.
                    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
                    MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(
                        vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
                        vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
                        vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
                        vec4(0.0, 0.0, 0.0, 1.0));
                }

                void fragment() {
                    vec2 p = UV * 2.0 - 1.0;
                    float r = length(p);
                    float angle = atan(p.y, p.x);
                    float turn = 0.5 + 0.5 * sin(angle * 5.0 + TIME * swirl + r * 9.0);
                    float core = exp(-r * r * 22.0);
                    float shell = exp(-pow((r - 0.32) * 6.0, 2.0)) * (0.5 + 0.5 * turn);
                    float halo = exp(-r * r * 3.5) * 0.35;
                    float edge = 1.0 - smoothstep(0.8, 1.0, r);
                    vec3 hot = mix(colour, vec3(1.0), 0.4);
                    ALBEDO = (hot * core * 1.6 + colour * (shell + halo)) * energy * fade * edge;
                    ALPHA = clamp((core + shell + halo) * fade * edge * 0.7, 0.0, 1.0);
                }
                """,
        },
    });

    /// <summary>
    /// A flash of light facing the camera (a muzzle flash): a white-hot core, <c>spikes</c> rays about it turned by
    /// <c>turn</c>, thinner rays between them and a soft halo, its size the node's scale (a unit quad).
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Flash = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform vec3 colour : source_color = vec3(1.0, 0.8, 0.4);
                instance uniform float energy = 4.0;
                instance uniform float spikes = 4.0;
                instance uniform float turn = 0.0;
                instance uniform float fade = 1.0;

                void vertex() {
                    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
                    MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(
                        vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
                        vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
                        vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
                        vec4(0.0, 0.0, 0.0, 1.0));
                }

                void fragment() {
                    vec2 p = UV * 2.0 - 1.0;
                    float r = length(p);
                    float a = atan(p.y, p.x) + turn;
                    float rays = pow(abs(cos(a * spikes * 0.5)), 28.0) * pow(max(1.0 - r, 0.0), 1.5);
                    float between = pow(abs(sin(a * spikes * 0.5)), 60.0) * pow(max(1.0 - r * 1.6, 0.0), 2.0) * 0.6;
                    float core = exp(-r * r * 40.0);
                    float halo = exp(-r * r * 7.0) * 0.45;
                    vec3 hot = mix(colour, vec3(1.0), 0.7);
                    ALBEDO = (hot * core * 2.5 + colour * (rays + between + halo)) * energy * fade;
                }
                """,
        },
    });

    /// <summary>
    /// A spark for particles flying with <see cref="ParticleProcessMaterial.ParticleFlagAlignY"/>: a thin streak along
    /// its flight, as long as the particle's scale and a seventh as thick, its head bright and its tail fading, in the
    /// particle's colour (its strength in the alpha).
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Spark = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                void vertex() {
                    // The particle's y axis runs along its flight, its length the particle's size.
                    vec3 axis = MODEL_MATRIX[1].xyz;
                    float size = max(length(axis), 0.0001);
                    vec3 along = axis / size;
                    vec3 origin = MODEL_MATRIX[3].xyz;
                    vec3 side = cross(along, normalize(CAMERA_POSITION_WORLD - origin));
                    side = length(side) > 0.001 ? normalize(side) : vec3(1.0, 0.0, 0.0);
                    vec3 world = origin + along * VERTEX.y * size + side * VERTEX.x * size * 0.14;
                    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
                }

                void fragment() {
                    vec2 p = UV * 2.0 - 1.0;
                    float across = exp(-p.x * p.x * 5.0) * (1.0 - p.x * p.x);
                    // UV.y runs from the head (the quad's top, -1 here) to the tail (1).
                    float along = smoothstep(1.0, -0.2, p.y) * smoothstep(-1.0, -0.8, p.y);
                    ALBEDO = COLOR.rgb * across * along * COLOR.a;
                }
                """,
        },
    });

    /// <summary>A streak of light (<see cref="Streak"/>) for a node whose -z points along it, culled by its whole length.</summary>
    public static MeshInstance3D NewStreak(Color colour, float energy, float thickness, float reach, float head = 1, float taper = 1,
        float flicker = 0, float white = 0.75f)
    {
        var streak = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = Streak.Value,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            CustomAabb = new Aabb(-Vector3.One * (reach + thickness), Vector3.One * (reach + thickness) * 2),
        };
        streak.SetInstanceShaderParameter("colour", new Vector3(colour.R, colour.G, colour.B));
        streak.SetInstanceShaderParameter("energy", energy);
        streak.SetInstanceShaderParameter("thickness", thickness);
        streak.SetInstanceShaderParameter("reach", reach);
        streak.SetInstanceShaderParameter("head", head);
        streak.SetInstanceShaderParameter("taper", taper);
        streak.SetInstanceShaderParameter("flicker", flicker);
        streak.SetInstanceShaderParameter("white", white);
        return streak;
    }

    /// <summary>A ball of energy (<see cref="Orb"/>), <paramref name="size"/> metres across.</summary>
    public static MeshInstance3D NewOrb(Color colour, float energy, float size)
    {
        var orb = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = Orb.Value,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = Vector3.One * size,
        };
        orb.SetInstanceShaderParameter("colour", new Vector3(colour.R, colour.G, colour.B));
        orb.SetInstanceShaderParameter("energy", energy);
        return orb;
    }

    /// <summary>A flash of light (<see cref="Flash"/>), <paramref name="size"/> metres across, its rays turned at random.</summary>
    public static MeshInstance3D NewFlash(Color colour, float energy, float size, float spikes = 4)
    {
        var flash = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One },
            MaterialOverride = Flash.Value,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Scale = Vector3.One * size,
        };
        flash.SetInstanceShaderParameter("colour", new Vector3(colour.R, colour.G, colour.B));
        flash.SetInstanceShaderParameter("energy", energy);
        flash.SetInstanceShaderParameter("spikes", spikes);
        flash.SetInstanceShaderParameter("turn", (float)GD.RandRange(0, Mathf.Tau));
        return flash;
    }

    /// <summary>
    /// The vortex as a black hole: a solid sphere, pitch black where it faces the camera, with thin white wisps
    /// winding about its axis (the original's sphere is black with white streaks, models/texture4/wall_b) that light
    /// up towards its edge, and a bright thread along the edge itself; a unit sphere (<see cref="HoleSphere"/>)
    /// scaled by the node. <c>calm</c> stills the wisps, leaving it pure black as the original's last two seconds.
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Hole = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, cull_disabled, shadows_disabled;

                instance uniform float energy = 3.0;
                instance uniform float calm = 0.0;

                varying vec3 local;

                void vertex() {
                    local = normalize(VERTEX);
                }

                void fragment() {
                    float lat = asin(clamp(local.y, -1.0, 1.0));
                    float lon = atan(local.z, local.x);
                    float a = 0.5 + 0.5 * sin(lon * 4.0 + lat * 7.0 - TIME * 2.4 + 1.5 * sin(lat * 5.0 - TIME * 1.3));
                    float b = 0.5 + 0.5 * sin(lon * 7.0 - lat * 5.0 + TIME * 3.1 + 2.0 * sin(lon * 3.0 + lat * 2.0 + TIME * 0.7));
                    // Thin strands, none at the poles where they would all meet.
                    float wisps = (pow(a, 24.0) + 0.7 * pow(b, 30.0)) * smoothstep(0.97, 0.75, abs(local.y));
                    float edge = 1.0 - clamp(abs(dot(NORMAL, VIEW)), 0.0, 1.0);
                    float glow = wisps * 1.5 * pow(edge, 1.8) * (1.0 - calm) + 1.4 * pow(edge, 10.0);
                    ALBEDO = vec3(0.85, 0.9, 1.0) * glow * energy;
                }
                """,
        },
    });

    /// <summary>The unit sphere (a metre across) that <see cref="Hole"/> and <see cref="Shell"/> draw.</summary>
    public static readonly Lazy<SphereMesh> HoleSphere = new(() => new SphereMesh { Radius = 0.5f, Height = 1, RadialSegments = 48, Rings = 24 });

    /// <summary>
    /// A shell of light about the vortex (the original's white sphere): bright along its edge and, but for a glow of
    /// <c>fill</c>, clear where it faces the camera, so the black hole inside stays black; <c>fade</c> dims it.
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Shell = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_back, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform float energy = 2.0;
                instance uniform float fill = 0.0;
                instance uniform float fade = 1.0;

                void fragment() {
                    float edge = 1.0 - clamp(dot(NORMAL, VIEW), 0.0, 1.0);
                    ALBEDO = vec3(0.9, 0.95, 1.0) * (pow(edge, 5.0) + fill) * energy * fade;
                }
                """,
        },
    });

    /// <summary>
    /// Space drawn in about a black hole: a disc facing the camera, its size the node's scale (a unit quad), that
    /// shows what lies behind it pulled in towards the hole and turned about it, most just outside the hole (<c>hole</c>,
    /// its radius as a share of the disc's) and not at all at the disc's edge. <c>pull</c> sets how much. What stands
    /// in front of the hole stays as it is. It is drawn before the other see-through effects, which it would
    /// otherwise hide.
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Lens = new(() => new ShaderMaterial
    {
        RenderPriority = (int)Material.RenderPriorityMin,
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_mix, cull_disabled, depth_test_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                uniform sampler2D screen : hint_screen_texture, filter_linear;
                uniform sampler2D depth : hint_depth_texture, filter_nearest;
                instance uniform float pull = 1.0;
                instance uniform float hole = 0.4;

                varying float centre;
                varying float reach;

                void vertex() {
                    MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
                    MODELVIEW_MATRIX = MODELVIEW_MATRIX * mat4(
                        vec4(length(MODEL_MATRIX[0].xyz), 0.0, 0.0, 0.0),
                        vec4(0.0, length(MODEL_MATRIX[1].xyz), 0.0, 0.0),
                        vec4(0.0, 0.0, length(MODEL_MATRIX[2].xyz), 0.0),
                        vec4(0.0, 0.0, 0.0, 1.0));
                    centre = -(VIEW_MATRIX * vec4(MODEL_MATRIX[3].xyz, 1.0)).z;
                    reach = length(MODEL_MATRIX[0].xyz) * 0.5 * hole;
                }

                // 1 where the scene lies level with the hole or behind it, 0 a radius and more in front of it.
                float behind(vec2 at, mat4 unproject, float middle, float radius) {
                    vec4 seen = unproject * vec4(at * 2.0 - 1.0, texture(depth, at).r, 1.0);
                    return smoothstep(middle - radius * 1.2, middle - radius * 0.2, -seen.z / seen.w);
                }

                void fragment() {
                    vec2 p = UV * 2.0 - 1.0;
                    float r = length(p);
                    // 0 at the hole's edge, 1 at the disc's.
                    float x = (r - hole) / (1.0 - hole);
                    float strength = pull * smoothstep(0.0, 0.15, x) * pow(1.0 - clamp(x, 0.0, 1.0), 2.0);
                    float turn = strength * 0.4;
                    vec2 from = mat2(vec2(cos(turn), sin(turn)), vec2(-sin(turn), cos(turn))) * p * (1.0 + strength * 0.12);
                    // From the disc's own measure to the screen's, which the disc faces square on.
                    vec2 pixels = (from - p) * 0.5 / vec2(dFdx(UV.x), dFdy(UV.y));
                    vec2 drawn = clamp(SCREEN_UV + pixels / VIEWPORT_SIZE, vec2(0.0), vec2(1.0));
                    float kept = behind(SCREEN_UV, INV_PROJECTION_MATRIX, centre, reach) * behind(drawn, INV_PROJECTION_MATRIX, centre, reach);
                    ALBEDO = texture(screen, mix(SCREEN_UV, drawn, kept)).rgb;
                    ALPHA = x > 0.0 && x < 1.0 ? 1.0 : 0.0;
                }
                """,
        },
    });

    /// <summary>The quad that draws <see cref="Spark"/> particles.</summary>
    public static readonly Lazy<QuadMesh> SparkQuad = new(() => new QuadMesh { Size = Vector2.One, Material = Spark.Value });

    /// <summary>
    /// A band of light along any line (<see cref="RibbonMesh"/>), turned at each point to face the camera and
    /// <c>width</c> metres wide whatever the node's scale: a healing helix, a bolt of lightning. White-hot in its core
    /// and the colour round it; <c>flow</c> runs pulses along it at that speed, <c>fade</c> dims it.
    /// </summary>
    public static readonly Lazy<ShaderMaterial> Ribbon = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_premul_alpha, cull_disabled, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform vec3 colour : source_color = vec3(0.3, 1.0, 0.4);
                instance uniform float energy = 3.0;
                instance uniform float width = 0.2;
                instance uniform float flow = 0.0;
                instance uniform float fade = 1.0;

                void vertex() {
                    // NORMAL carries the line's direction there, UV.y which side of it the vertex lies.
                    vec3 world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                    vec3 along = normalize((MODEL_MATRIX * vec4(NORMAL, 0.0)).xyz);
                    vec3 side = cross(along, normalize(CAMERA_POSITION_WORLD - world));
                    side = length(side) > 0.001 ? normalize(side) : vec3(0.0, 1.0, 0.0);
                    world += side * (UV.y * 2.0 - 1.0) * width * 0.5;
                    POSITION = PROJECTION_MATRIX * VIEW_MATRIX * vec4(world, 1.0);
                }

                void fragment() {
                    float a = abs(UV.y * 2.0 - 1.0);
                    float core = exp(-a * a * 20.0);
                    float glow = exp(-a * a * 4.0) * (1.0 - a);
                    float pulse = flow == 0.0 ? 1.0 : 0.55 + 0.45 * sin(UV.x * 40.0 - TIME * flow);
                    float ends = smoothstep(0.0, 0.03, UV.x) * smoothstep(1.0, 0.97, UV.x);
                    float shape = pulse * ends * fade;
                    // The core takes the energy, the glow round it keeps the colour rather than burning out to white.
                    ALBEDO = (mix(colour, vec3(1.0), 0.6) * core * 1.5 * energy + colour * glow * min(energy, 2.5)) * shape;
                    ALPHA = clamp((core + glow) * shape * 0.6, 0.0, 1.0);
                }
                """,
        },
    });

    /// <summary>
    /// The mesh for <see cref="Ribbon"/>: two vertices at each point of <paramref name="points"/> (in the node's
    /// space), carrying the line's direction there and how far along it they are.
    /// </summary>
    public static ArrayMesh RibbonMesh(IReadOnlyList<Vector3> points)
    {
        var count = points.Count;
        var vertices = new Vector3[count * 2];
        var normals = new Vector3[count * 2];
        var uvs = new Vector2[count * 2];
        var indices = new int[(count - 1) * 6];
        var total = 0f;
        var gone = new float[count];
        for (var i = 1; i < count; i++)
            gone[i] = total += points[i].DistanceTo(points[i - 1]);
        for (var i = 0; i < count; i++)
        {
            var along = (points[Math.Min(i + 1, count - 1)] - points[Math.Max(i - 1, 0)]).Normalized();
            for (var side = 0; side < 2; side++)
            {
                vertices[i * 2 + side] = points[i];
                normals[i * 2 + side] = along;
                uvs[i * 2 + side] = new Vector2(total > 0 ? gone[i] / total : 0, side);
            }
            if (i == count - 1)
                continue;
            var k = i * 6;
            (indices[k], indices[k + 1], indices[k + 2]) = (i * 2, i * 2 + 1, i * 2 + 2);
            (indices[k + 3], indices[k + 4], indices[k + 5]) = (i * 2 + 1, i * 2 + 3, i * 2 + 2);
        }
        var arrays = new Godot.Collections.Array();
        arrays.Resize((int)Mesh.ArrayType.Max);
        arrays[(int)Mesh.ArrayType.Vertex] = vertices;
        arrays[(int)Mesh.ArrayType.Normal] = normals;
        arrays[(int)Mesh.ArrayType.TexUV] = uvs;
        arrays[(int)Mesh.ArrayType.Index] = indices;
        var mesh = new ArrayMesh();
        mesh.AddSurfaceFromArrays(Mesh.PrimitiveType.Triangles, arrays);
        return mesh;
    }

    /// <summary>A band of light (<see cref="Ribbon"/>) along <paramref name="points"/>.</summary>
    public static MeshInstance3D NewRibbon(IReadOnlyList<Vector3> points, Color colour, float energy, float width, float flow = 0)
    {
        var ribbon = new MeshInstance3D
        {
            Mesh = RibbonMesh(points),
            MaterialOverride = Ribbon.Value,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            ExtraCullMargin = width,
        };
        ribbon.SetInstanceShaderParameter("colour", new Vector3(colour.R, colour.G, colour.B));
        ribbon.SetInstanceShaderParameter("energy", energy);
        ribbon.SetInstanceShaderParameter("width", width);
        ribbon.SetInstanceShaderParameter("flow", flow);
        return ribbon;
    }
}
