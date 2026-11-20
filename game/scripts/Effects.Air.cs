using Godot;

namespace Machines.Game;

/// <summary>
/// What an explosion does to the air and the ground about it, in the remake's effects: the shock wave bends the
/// light as it races out, then the hot air above shimmers as it rises and cools; sparks and debris land on the
/// ground and bounce off it instead of falling through.
/// </summary>
public partial class Effects
{
    public override void _Ready()
    {
        // The ground under the view, for what falls to land on: the weather brings one on most planets (rain splashing
        // on it); elsewhere the effects lay their own.
        if (GraphicsSettings.Effects && GetTree().Root.FindChildren("*", nameof(GpuParticlesCollisionHeightField3D), true, false).Count == 0)
            AddChild(new GpuParticlesCollisionHeightField3D
            {
                Size = new Vector3(420, 900, 420),
                Resolution = GpuParticlesCollisionHeightField3D.ResolutionEnum.Resolution1024,
                UpdateMode = GpuParticlesCollisionHeightField3D.UpdateModeEnum.WhenMoved,
                FollowCameraEnabled = true,
                Name = "Ground",
            });
    }

    /// <summary><paramref name="process"/>'s particles land on the ground and bounce off it, losing speed as they slide.</summary>
    private static ParticleProcessMaterial Bouncing(ParticleProcessMaterial process, float bounce)
    {
        process.CollisionMode = ParticleProcessMaterial.CollisionModeEnum.Rigid;
        process.CollisionBounce = bounce;
        process.CollisionFriction = 0.5f;
        return process;
    }

    /// <summary>
    /// The air bent by a blast at <paramref name="at"/>: a ring racing out to <paramref name="radius"/> metres in the
    /// first third of <paramref name="duration"/> seconds, then a shimmer over the middle that rises and dies away.
    /// <paramref name="strength"/> scales how far it bends. Drawn before the fire and smoke, which lie over it.
    /// </summary>
    private void Shimmer(Vector3 at, float radius, float duration, float strength)
    {
        var material = new ShaderMaterial { Shader = ShimmerShader.Value, RenderPriority = -100 };
        material.SetShaderParameter("strength", strength);
        // Set before it is tweened: a parameter never set is no property the tween could find.
        material.SetShaderParameter("age", 0f);
        material.SetShaderParameter("noise", TerrainMaterials.NoiseTexture);
        var air = new MeshInstance3D
        {
            Mesh = new QuadMesh { Size = Vector2.One * radius * 2 },
            MaterialOverride = material,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = "Shimmer",
        };
        AddChild(air);
        air.GlobalPosition = at;
        var age = air.CreateTween();
        age.TweenProperty(material, "shader_parameter/age", 1f, duration);
        age.TweenCallback(Callable.From(air.QueueFree));
    }

    private static readonly Lazy<Shader> ShimmerShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode unshaded, depth_draw_never, cull_disabled, shadows_disabled, fog_disabled;

            uniform sampler2D screen : hint_screen_texture, filter_linear;
            uniform sampler2D noise : repeat_enable, filter_linear_mipmap;
            uniform float age = 0.0;
            uniform float strength = 1.0;

            void vertex() {
                // Faces the camera.
                MODELVIEW_MATRIX = VIEW_MATRIX * mat4(INV_VIEW_MATRIX[0], INV_VIEW_MATRIX[1], INV_VIEW_MATRIX[2], MODEL_MATRIX[3]);
                MODELVIEW_NORMAL_MATRIX = mat3(MODELVIEW_MATRIX);
            }

            void fragment() {
                vec2 from_middle = UV * 2.0 - 1.0;
                float r = length(from_middle);
                // The shock wave: a thin ring racing out, gone by the middle of the blast's life.
                float wave = exp(-pow((r - smoothstep(0.0, 0.35, age)) * 9.0, 2.0)) * (1.0 - smoothstep(0.25, 0.5, age));
                // The hot air: a shimmer over the middle, rising.
                float heat = (1.0 - smoothstep(0.2, 0.9, r)) * smoothstep(0.08, 0.3, age) * (1.0 - smoothstep(0.55, 1.0, age));
                vec2 wobble = texture(noise, UV * 1.7 + vec2(0.0, TIME * 0.45)).rg - 0.5;
                vec2 bend = (from_middle / max(r, 1e-3) * wave * 0.035 + wobble * heat * 0.014) * strength;
                ALBEDO = texture(screen, SCREEN_UV + bend).rgb;
                ALPHA = clamp((wave + heat) * 3.0, 0.0, 1.0) * (1.0 - smoothstep(0.92, 1.0, r));
            }
            """,
    });
}
