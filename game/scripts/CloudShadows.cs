using Godot;

namespace Machines.Game;

/// <summary>
/// The shadows of clouds that drift over the land with the wind, in the remake's light and weather: large soft patches
/// that dim the ground, the machines and the buildings alike. One picture over the whole view, drawn first of what is
/// see-through: for each pixel the depth says where it lies, the sun's direction where on the clouds' plane above it
/// the light comes through, and a noise of soft clouds whether that is a cloud. It multiplies what is drawn, so the
/// effects drawn after it (fire, smoke, flashes) keep their light; the sky is left alone, and the shadows fade out
/// where the fog takes over. How much of the sky is cloud and how dark the shadows are is the weather's (Weather);
/// the sun sets them (low, they fade; at night there are none).
/// </summary>
public partial class CloudShadows : MeshInstance3D
{
    private const string Code = """
        shader_type spatial;
        render_mode unshaded, blend_mul, depth_draw_never, depth_test_disabled, cull_disabled, fog_disabled, shadows_disabled;

        uniform sampler2D depth_texture : hint_depth_texture, filter_nearest, repeat_disable;
        uniform sampler2D clouds : filter_linear_mipmap, repeat_enable;
        uniform vec3 to_sun = vec3(0.0, 1.0, 0.0);
        uniform vec2 drift = vec2(0.0);
        uniform float coverage = 0.4;
        uniform float strength = 0.0;
        // Metres one tile of the clouds' picture covers, the height of the clouds' plane, and where the shadows fade out.
        uniform float size = 600.0;
        uniform float height = 300.0;
        uniform float reach = 900.0;

        void vertex() {
            // The quad covers the screen whatever the camera.
            POSITION = vec4(VERTEX.xy * 2.0, 1.0, 1.0);
        }

        void fragment() {
            float depth = texture(depth_texture, SCREEN_UV).r;
            float dim = 0.0;
            // Reversed depth: 0 is as far as can be, the sky.
            if (depth > 0.0) {
                vec4 view = INV_PROJECTION_MATRIX * vec4(SCREEN_UV * 2.0 - 1.0, depth, 1.0);
                view.xyz /= view.w;
                vec3 world = (INV_VIEW_MATRIX * vec4(view.xyz, 1.0)).xyz;
                // Where the line to the sun meets the clouds' plane.
                vec2 at = world.xz + to_sun.xz / max(to_sun.y, 0.2) * (height - world.y);
                vec2 uv = (at - drift) / size;
                float cloud = texture(clouds, uv).r * 0.75 + texture(clouds, uv * 3.1 + vec2(0.37, 0.71)).r * 0.25;
                float shade = smoothstep(1.0 - coverage - 0.09, 1.0 - coverage + 0.09, cloud);
                dim = strength * shade * (1.0 - smoothstep(reach * 0.55, reach, length(view.xyz)));
            }
            ALBEDO = vec3(1.0 - dim);
        }
        """;

    /// <summary>Soft clouds that tile without a seam: a few to a tile, frayed at their edges.</summary>
    private static readonly Lazy<NoiseTexture2D> Clouds = new(() => new NoiseTexture2D
    {
        Width = 512,
        Height = 512,
        Seamless = true,
        GenerateMipmaps = true,
        Noise = new FastNoiseLite
        {
            NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
            Frequency = 0.007f,
            FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
            FractalOctaves = 5,
            FractalGain = 0.5f,
            Seed = 1999,
        },
    });

    private readonly ShaderMaterial _material = new() { Shader = new Shader { Code = Code }, RenderPriority = (int)Material.RenderPriorityMin };
    private Vector2 _drift;

    public CloudShadows()
    {
        Name = "CloudShadows";
        _material.SetShaderParameter("clouds", Clouds.Value);
        Mesh = new QuadMesh { Size = Vector2.One, CustomAabb = new Aabb(new Vector3(-1e5f, -1e5f, -1e5f), new Vector3(2e5f, 2e5f, 2e5f)) };
        MaterialOverride = _material;
        CastShadow = ShadowCastingSetting.Off;
        ExtraCullMargin = 16384;
        GIMode = GIModeEnum.Disabled;
    }

    /// <summary>
    /// Moves the clouds on with <paramref name="wind"/> (metres a second over the ground; the clouds go faster) over
    /// <paramref name="delta"/> seconds, <paramref name="coverage"/> of the sky (0 to 1) clouded over, the shadows taking
    /// up to <paramref name="strength"/> of the light, their plane <paramref name="ground"/> plus a few hundred metres up.
    /// </summary>
    public void Set(float coverage, float strength, Vector2 wind, float delta, float ground, float reach)
    {
        _drift += wind * 2.5f * delta;
        // Kept within one tile of the picture, so the numbers stay small in a long match.
        _drift = new Vector2(Mathf.PosMod(_drift.X, 600), Mathf.PosMod(_drift.Y, 600));
        var sun = Mathf.Clamp((PlanetLook.SunElevation - 3) / 12, 0, 1) * (1 - PlanetLook.ForcedDark);
        Visible = strength * sun > 0.005f;
        _material.SetShaderParameter("to_sun", PlanetLook.TowardsSun);
        _material.SetShaderParameter("drift", _drift);
        _material.SetShaderParameter("coverage", coverage);
        _material.SetShaderParameter("strength", strength * sun);
        _material.SetShaderParameter("height", ground + 300);
        _material.SetShaderParameter("reach", reach);
    }
}
