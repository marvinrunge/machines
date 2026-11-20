using Godot;

namespace Machines.Game;

/// <summary>
/// The overhauled machines and buildings worn by the field: dust of the planet's own ground settled on what faces up,
/// dark grime in the creases (where the baked occlusion is deep), both broken up into patches that stick to the
/// model. Rain washes the dust off and leaves the grime. Drawn as a second pass over the model's own material
/// (Material.NextPass), so its paint, glow, damage and plans stay as they are; the hologram and other materials laid
/// over a whole model leave it out.
/// </summary>
public static class ModelGrime
{
    /// <summary>The dust when no planet has said what its ground is (the hangar, the model viewer): a dry grey-brown.</summary>
    public static readonly Color NeutralDust = new(0.55f, 0.5f, 0.44f);

    private const string Code = """
        shader_type spatial;
        render_mode blend_mix, depth_draw_never, cull_back, diffuse_burley, specular_schlick_ggx;

        global uniform vec3 planet_dust;
        global uniform float weather_wetness;
        uniform sampler2D breakup : repeat_enable, filter_linear_mipmap;

        varying vec3 local_position;
        varying vec3 local_normal;
        varying float open;

        void vertex() {
            local_position = VERTEX;
            local_normal = NORMAL;
            // The baked occlusion (1 is open); white where a model has none.
            open = COLOR.r;
        }

        void fragment() {
            vec3 world_normal = normalize((INV_VIEW_MATRIX * vec4(NORMAL, 0.0)).xyz);
            // Patches in the part's own space, so they stay put as the machine moves.
            vec3 side = pow(abs(local_normal), vec3(4.0));
            side /= side.x + side.y + side.z + 1e-4;
            vec3 at = local_position * 0.16;
            float patches = texture(breakup, at.yz).r * side.x + texture(breakup, at.xz).r * side.y + texture(breakup, at.xy).r * side.z;
            // Dust settles on what faces up; the rain washes it off.
            float dust = smoothstep(0.3, 0.9, world_normal.y) * smoothstep(0.3, 0.85, patches) * 0.4 * (1.0 - 0.75 * weather_wetness);
            // Grime gathers in the creases.
            float grime = (1.0 - smoothstep(0.45, 0.92, open)) * smoothstep(0.1, 0.7, patches + 0.1) * 0.45;
            float amount = dust + grime;
            ALBEDO = mix(vec3(0.045, 0.04, 0.035), planet_dust, dust / max(amount, 1e-4));
            ROUGHNESS = 0.93;
            METALLIC = 0.0;
            SPECULAR = 0.3;
            ALPHA = clamp(amount, 0.0, 0.8);
        }
        """;

    private static readonly Lazy<ShaderMaterial> Pass = new(() =>
    {
        Weather.EnsureGlobals();
        var material = new ShaderMaterial { Shader = new Shader { Code = Code } };
        material.SetShaderParameter("breakup", new NoiseTexture2D
        {
            Width = 256,
            Height = 256,
            Seamless = true,
            GenerateMipmaps = true,
            Noise = new FastNoiseLite
            {
                NoiseType = FastNoiseLite.NoiseTypeEnum.SimplexSmooth,
                Frequency = 0.02f,
                FractalType = FastNoiseLite.FractalTypeEnum.Fbm,
                FractalOctaves = 3,
                Seed = 7,
            },
        });
        return material;
    });

    /// <summary>The pass to lay over an overhauled model's material.</summary>
    public static Material Overlay => Pass.Value;
}
