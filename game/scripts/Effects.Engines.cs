using Godot;

namespace Machines.Game;

// The drives of a ship in a cutscene: its engine bells burning (the mothership's ten at its stern showed their dull grey
// texture inside), the exhaust streaming from them, the air shimmering in their heat, the light they throw.
public partial class Effects
{
    /// <summary>
    /// A model's engine bells: where their exits lie along the model's x (its exhaust goes out along +x), their radius, and
    /// each one's middle as (up, across), in the model's own metres. The mothership's ten (game/assets/story/space/
    /// mothership.glb, its stern at x 600): four above, two at the sides, four below.
    /// </summary>
    private static readonly Dictionary<string, (float Exit, float Radius, Vector2[] Bells)> Nozzles = new()
    {
        ["space/mothership"] = (592, 19,
        [
            new(64, -44), new(64, 44), new(64, -127), new(64, 127),
            new(-4, -171), new(-4, 171),
            new(-70, -40), new(-70, 40), new(-70, -118), new(-70, 118),
        ]),
    };

    /// <summary>
    /// The drives of <paramref name="model"/> (a story prop by its name, <c>space/mothership</c>) lit at
    /// <paramref name="strength"/>: in each bell a white-hot glow, from each a plume of exhaust widening and cooling, about
    /// it the air shimmering, and one light on the stern. Hangs under the model and goes with it. False when the model's
    /// bells are not known.
    /// </summary>
    public static bool Engines(Node3D model, string name, float strength)
    {
        if (!Nozzles.TryGetValue(name, out var nozzles))
            return false;
        var (exit, radius, bells) = nozzles;
        // Turned so that the meshes' own y (a cylinder's axis) and a quad's face point out of the stern, along +x.
        var along = new Basis(Vector3.Down, Vector3.Right, Vector3.Back);
        var facing = new Basis(Vector3.Forward, Vector3.Up, Vector3.Right);
        var engines = new Node3D { Name = "Engines" };
        model.AddChild(engines);
        foreach (var bell in bells)
        {
            var at = new Vector3(exit, bell.X, bell.Y);
            // The fire deep in the bell, filling it (it stood dull grey).
            var glow = new MeshInstance3D
            {
                Mesh = new QuadMesh { Size = Vector2.One * 2 },
                MaterialOverride = NozzleMaterial.Value,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(facing.Scaled(Vector3.One * radius * 0.97f), at - Vector3.Right * radius * 0.35f),
            };
            glow.SetInstanceShaderParameter("energy", strength);
            engines.AddChild(glow);
            // The exhaust: a cone of hot gas, bluish white at its root, orange and gone some four bells out.
            var plume = new MeshInstance3D
            {
                Mesh = PlumeMesh.Value,
                MaterialOverride = PlumeMaterial.Value,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(along * Basis.FromScale(new Vector3(radius, radius * 4.5f, radius)), at + Vector3.Right * radius * 2.25f),
            };
            plume.SetInstanceShaderParameter("energy", strength);
            engines.AddChild(plume);
            // And the air (what there is of it up here) shimmering round it, longer and wider.
            var shimmer = new MeshInstance3D
            {
                Mesh = PlumeMesh.Value,
                MaterialOverride = ShimmerMaterial.Value,
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                Transform = new Transform3D(along * Basis.FromScale(new Vector3(radius * 1.5f, radius * 10, radius * 1.5f)), at + Vector3.Right * radius * 5),
            };
            shimmer.SetInstanceShaderParameter("strength", strength);
            engines.AddChild(shimmer);
        }
        engines.AddChild(new OmniLight3D
        {
            LightColor = new Color(1, 0.62f, 0.32f),
            LightEnergy = 3 * strength,
            OmniRange = 900,
            OmniAttenuation = 1.3f,
            ShadowEnabled = false,
            Position = new Vector3(exit + radius * 4, 0, 0),
        });
        return true;
    }

    // A cone 1 long along y, from 0.75 wide at its root (y -0.5) to 1.3 at its end, open at both.
    private static readonly Lazy<Mesh> PlumeMesh = new(() => new CylinderMesh
    {
        TopRadius = 1.3f,
        BottomRadius = 0.75f,
        Height = 1,
        RadialSegments = 24,
        Rings = 6,
        CapTop = false,
        CapBottom = false,
    });

    private static readonly Lazy<ShaderMaterial> NozzleMaterial = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

                instance uniform float energy = 1.0;

                void fragment() {
                    float r = length(UV * 2.0 - 1.0);
                    float fill = 1.0 - smoothstep(0.55, 1.0, r);
                    float core = pow(max(1.0 - r, 0.0), 2.0);
                    float flicker = 0.88 + 0.12 * sin(TIME * 41.0 + r * 9.0) * sin(TIME * 17.0);
                    vec3 colour = mix(vec3(1.0, 0.42, 0.14), vec3(1.0, 0.93, 0.82), core);
                    ALBEDO = colour * (fill * 1.1 + core * 2.6) * energy * flicker;
                }
                """,
        },
    });

    private static readonly Lazy<ShaderMaterial> PlumeMaterial = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

                instance uniform float energy = 1.0;
                varying float along;
                varying vec3 local;

                void vertex() {
                    along = VERTEX.y + 0.5;
                    local = VERTEX;
                }

                void fragment() {
                    // Brightest down its middle as seen, soft at its sides; strong at the root, gone at the end.
                    float facing = abs(dot(normalize(NORMAL), VIEW));
                    float body = pow(facing, 4.0);
                    float fade = pow(1.0 - along, 3.0) * smoothstep(0.0, 0.03, along);
                    float pulse = 0.78 + 0.22 * sin(along * 46.0 - TIME * 70.0 + local.x * 5.0) * sin(local.z * 6.0 + TIME * 19.0);
                    vec3 colour = mix(vec3(1.0, 0.36, 0.08), vec3(0.75, 0.85, 1.0), clamp(body * (1.0 - along * 4.0), 0.0, 1.0));
                    ALBEDO = colour * body * fade * pulse * 1.1 * energy;
                }
                """,
        },
    });

    private static readonly Lazy<ShaderMaterial> ShimmerMaterial = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, depth_draw_never, cull_back, shadows_disabled, fog_disabled;

                uniform sampler2D screen : hint_screen_texture, filter_linear_mipmap;
                instance uniform float strength = 1.0;
                varying float along;
                varying vec3 local;

                void vertex() {
                    along = VERTEX.y + 0.5;
                    local = VERTEX;
                }

                void fragment() {
                    float facing = abs(dot(normalize(NORMAL), VIEW));
                    float fade = (1.0 - along) * smoothstep(0.0, 0.08, along) * pow(facing, 1.3);
                    vec2 wobble = vec2(sin(along * 38.0 - TIME * 23.0 + local.x * 9.0), cos(along * 31.0 - TIME * 19.0 + local.z * 11.0));
                    ALBEDO = textureLod(screen, SCREEN_UV + wobble * 0.006 * fade * strength, 0.0).rgb;
                    ALPHA = clamp(fade * 2.0, 0.0, 1.0);
                }
                """,
        },
    });
}
