using System.Globalization;
using Godot;

namespace Machines.Game;

/// <summary>
/// The stage a cutscene's space part is filmed on (the remake's own, <see cref="CutsceneScript"/>): a world of its own beside
/// the match's, drawn over its picture while a space take runs. The stars and the nebula (game/assets/story/space/sky.jpg,
/// made with Gemini and upscaled), the planet below (planet.glb, made with Tripo from a Gemini picture), its haze glowing at
/// its rim, one sun, and the match's effects at the scale of ships. Nothing of the match is in it: no map, no machines.
/// <para>
/// Metres as the script's points have them (x, y across, up). The part line sets it: <c>planet=x,y,up</c> its middle,
/// <c>radius=</c> its size, <c>face=yaw,pitch</c> turns its pictured face (towards the stage's middle by default),
/// <c>sun=yaw,pitch</c> where the light comes from (degrees, yaw 0 along +x, 90 along +y).
/// </para>
/// </summary>
public partial class SpaceStage : CanvasLayer
{
    private const string Assets = "res://assets/story/space";

    /// <summary>What a stage with no word of the script has: a planet 200 km round, 260 km below and ahead.</summary>
    private static readonly Vector3 DefaultPlanet = new(0, -260_000, 120_000);

    private const float DefaultRadius = 200_000;

    private readonly SubViewport _viewport = new()
    {
        OwnWorld3D = true,
        Msaa3D = Viewport.Msaa.Msaa4X,
        RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
        Name = "SpaceWorld",
    };

    private readonly SubViewportContainer _box = new() { Stretch = true, MouseFilter = Control.MouseFilterEnum.Ignore, Visible = false };

    /// <summary>Everything on the stage hangs under this: the props, the effects' bursts, the flashes.</summary>
    public Node3D Root { get; } = new() { Name = "Space" };

    /// <summary>The stage's camera, which a space take moves.</summary>
    public Camera3D Camera { get; } = new() { Near = 2, Far = 3_000_000, Fov = 50, Name = "SpaceCamera" };

    /// <summary>The match's effects, made at the scale of ships on this stage.</summary>
    public Effects Effects { get; } = new() { Name = "Effects" };

    public Vector3 PlanetCentre { get; }
    public float PlanetRadius { get; }

    private readonly DirectionalLight3D _sun = new() { Name = "Sun" };
    private readonly IReadOnlyDictionary<string, string> _options;

    public SpaceStage(IReadOnlyDictionary<string, string> options)
    {
        _options = options;
        // Under the film look (layer -1), which reads the picture as drawn, and over the match's own picture.
        Layer = -2;
        Name = "SpaceStage";
        PlanetCentre = Vector(options.GetValueOrDefault("planet")) is { } planet ? new Vector3(planet.X, planet.Z, planet.Y) : DefaultPlanet;
        PlanetRadius = Number(options.GetValueOrDefault("radius")) ?? DefaultRadius;
    }

    /// <summary>Whether a space take is on: the stage drawn over the match's picture, and drawn at all only then.</summary>
    public bool Showing
    {
        get => _box.Visible;
        set
        {
            _box.Visible = value;
            _viewport.RenderTargetUpdateMode = value ? SubViewport.UpdateMode.Always : SubViewport.UpdateMode.Disabled;
            if (value)
                Camera.MakeCurrent();
        }
    }

    public override void _Ready()
    {
        _box.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_box);
        _box.AddChild(_viewport);
        _viewport.AddChild(new WorldEnvironment { Environment = Backdrop(), Name = "Environment" });
        _viewport.AddChild(Root);
        Root.AddChild(Camera);
        Root.AddChild(Effects);
        var (yaw, pitch) = Angles(_options.GetValueOrDefault("sun")) ?? (-35f, 18f);
        // The light comes from there: it shines along the opposite way.
        _sun.LightColor = new Color(1, 0.95f, 0.88f);
        _sun.LightEnergy = 1.35f;
        _sun.ShadowEnabled = true;
        _sun.DirectionalShadowMaxDistance = 9000;
        _sun.DirectionalShadowMode = DirectionalLight3D.ShadowMode.Parallel4Splits;
        Root.AddChild(_sun);
        _sun.Basis = Basis.LookingAt(-Along(yaw, pitch), Vector3.Up);
        // The planet's own glow on what faces it, the sunlight its sands give back: a ship's belly is not black.
        var shine = new DirectionalLight3D { LightColor = new Color(1, 0.66f, 0.42f), LightEnergy = 0.35f, ShadowEnabled = false, Name = "PlanetShine" };
        Root.AddChild(shine);
        var up = (-PlanetCentre).Normalized();
        shine.Basis = Basis.LookingAt(up, Mathf.Abs(up.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
        Planet();
    }

    /// <summary>The way a yaw and pitch in degrees point (yaw 0 along +x, 90 along +y of the script, which is the stage's +z).</summary>
    private static Vector3 Along(float yaw, float pitch)
    {
        var (y, p) = (Mathf.DegToRad(yaw), Mathf.DegToRad(pitch));
        return new Vector3(Mathf.Cos(p) * Mathf.Cos(y), Mathf.Sin(p), Mathf.Cos(p) * Mathf.Sin(y));
    }

    /// <summary>The surface of the planet under a point: where a line from its middle through the point meets it, and the way out there.</summary>
    public (Vector3 At, Vector3 Up) Surface(Vector3 near)
    {
        var up = (near - PlanetCentre).Normalized();
        return (PlanetCentre + up * PlanetRadius, up);
    }

    // ---- the planet ----------------------------------------------------------------------------------

    private void Planet()
    {
        if (!ResourceLoader.Exists($"{Assets}/planet.glb") || GD.Load<PackedScene>($"{Assets}/planet.glb")?.Instantiate() is not Node3D model)
        {
            GD.PrintErr("space: no planet model");
            return;
        }
        // The model is a ball about a metre round standing on its base: centred and scaled to the planet's radius.
        var meshes = model.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().ToList();
        var box = meshes.Select(m => m.GetAabb()).Aggregate((a, b) => a.Merge(b));
        var scale = PlanetRadius / (box.Size.X / 2);
        var pivot = new Node3D { Name = "Planet" };
        Root.AddChild(pivot);
        pivot.AddChild(model);
        model.Position = -box.GetCenter() * scale;
        model.Scale = Vector3.One * scale;
        foreach (var mesh in meshes)
        {
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
            // Matt, and a little darker than Tripo painted it: in full sun under the filmic curve it glared pale yellow.
            for (var surface = 0; surface < mesh.GetSurfaceOverrideMaterialCount(); surface++)
                if ((mesh.GetActiveMaterial(surface)?.Duplicate() as StandardMaterial3D) is { } matt)
                {
                    matt.AlbedoColor = new Color(0.72f, 0.66f, 0.62f);
                    matt.Roughness = 1;
                    matt.Metallic = 0;
                    matt.MetallicSpecular = 0.2f;
                    mesh.SetSurfaceOverrideMaterial(surface, matt);
                }
        }
        // Its pictured face (the model's +z) turned towards the middle of the stage, where the ship flies, or as the script says.
        var toward = (-PlanetCentre).Normalized();
        if (Angles(_options.GetValueOrDefault("face")) is var (yaw, pitch))
            toward = Along(yaw, pitch);
        pivot.Basis = Basis.LookingAt(-toward, Mathf.Abs(toward.Y) > 0.98f ? Vector3.Forward : Vector3.Up);
        pivot.Position = PlanetCentre;
        // The haze about it, lit where the sun is.
        var haze = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = PlanetRadius * 1.02f, Height = PlanetRadius * 2.04f, RadialSegments = 128, Rings = 64 },
            MaterialOverride = HazeMaterial.Value,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Name = "Haze",
        };
        Root.AddChild(haze);
        haze.Position = PlanetCentre;
        haze.SetInstanceShaderParameter("sun", -_sun.GlobalBasis.Z);
        haze.SetInstanceShaderParameter("centre", PlanetCentre);
        haze.SetInstanceShaderParameter("radius", PlanetRadius);
    }

    /// <summary>
    /// A planet's thin dusty air seen from space: a rim of warm haze at its edge, thickest where it is seen edge-on and lit by
    /// the sun, a pale band beyond the edge against the black, nothing on the night side.
    /// </summary>
    private static readonly Lazy<ShaderMaterial> HazeMaterial = new(() => new ShaderMaterial
    {
        Shader = new Shader
        {
            Code = """
                shader_type spatial;
                render_mode unshaded, blend_add, cull_back, depth_draw_never, shadows_disabled, fog_disabled;

                instance uniform vec3 sun = vec3(0.0, 1.0, 0.0);
                instance uniform vec3 centre;
                instance uniform float radius = 1.0;
                varying vec3 world;

                void vertex() {
                    world = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
                }

                void fragment() {
                    vec3 n = normalize(world - centre);
                    vec3 view = normalize(CAMERA_POSITION_WORLD - world);
                    float edge = 1.0 - clamp(dot(n, view), 0.0, 1.0);
                    // Lit where the sun stands over it, fading through the twilight band.
                    float lit = smoothstep(-0.25, 0.35, dot(n, normalize(-sun)));
                    // Seen against the sun, the haze glows brighter (forward scattering).
                    float forward = pow(clamp(dot(-view, normalize(-sun)), 0.0, 1.0), 6.0);
                    vec3 inner = vec3(0.95, 0.52, 0.28);
                    vec3 outer = vec3(0.9, 0.66, 0.5);
                    vec3 colour = mix(inner, outer, smoothstep(0.55, 0.95, edge));
                    float glow = pow(edge, 6.0) * 0.28 + pow(edge, 20.0) * 0.5;
                    ALBEDO = colour * glow * lit * (0.75 + 1.0 * forward);
                }
                """,
        },
    });

    // ---- the sky -------------------------------------------------------------------------------------

    /// <summary>A new environment of the stage's sky (the stars and nebula round the planet), for a view of space elsewhere.</summary>
    public static Godot.Environment Sky() => Backdrop();

    private static Godot.Environment Backdrop()
    {
        var picture = ResourceLoader.Exists($"{Assets}/sky.jpg") ? GD.Load<Texture2D>($"{Assets}/sky.jpg") : null;
        var material = new ShaderMaterial { Shader = new Shader { Code = SkyCode } };
        if (picture is not null)
            material.SetShaderParameter("picture", picture);
        material.SetShaderParameter("has_picture", picture is not null);
        material.SetShaderParameter("aspect", picture is null ? 2f : (float)picture.GetWidth() / picture.GetHeight());
        return new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Sky,
            Sky = new Godot.Sky { SkyMaterial = material, ProcessMode = Godot.Sky.ProcessModeEnum.Quality, RadianceSize = Godot.Sky.RadianceSizeEnum.Size256 },
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.09f, 0.08f, 0.1f),
            AmbientLightEnergy = 1,
            ReflectedLightSource = Godot.Environment.ReflectionSource.Sky,
            TonemapMode = Godot.Environment.ToneMapper.Filmic,
            TonemapExposure = 1.05f,
            GlowEnabled = true,
            GlowIntensity = 0.7f,
            GlowBloom = 0.06f,
            GlowHdrThreshold = 1.0f,
            GlowBlendMode = Godot.Environment.GlowBlendModeEnum.Softlight,
        };
    }

    /// <summary>
    /// The space picture round the sky: half the circle wide, mirrored for the other half so it has no seam, and as high as
    /// its shape gives; above and below it fades into a field of small stars.
    /// </summary>
    private const string SkyCode = """
        shader_type sky;

        uniform sampler2D picture : source_color, filter_linear_mipmap, repeat_disable;
        uniform bool has_picture = false;
        uniform float aspect = 2.0;

        float hash(vec3 p) {
            p = fract(p * vec3(443.897, 441.423, 437.195));
            p += dot(p, p.yzx + 19.19);
            return fract((p.x + p.y) * p.z);
        }

        vec3 stars(vec3 d) {
            vec3 cell = floor(d * 420.0);
            float h = hash(cell);
            if (h < 0.9965)
                return vec3(0.0);
            vec3 centre = (cell + 0.5 + (vec3(hash(cell + 1.7), hash(cell + 3.1), hash(cell + 5.3)) - 0.5) * 0.6) / 420.0;
            float d2 = distance(normalize(centre), d) * 420.0;
            float b = smoothstep(0.55, 0.0, d2) * (h - 0.9965) / 0.0035;
            return vec3(0.9, 0.93, 1.0) * b * 1.4;
        }

        void sky() {
            vec3 d = normalize(EYEDIR);
            float yaw = atan(d.x, -d.z);
            float pitch = asin(clamp(d.y, -1.0, 1.0));
            // Half the circle holds the picture once, the other half its mirror image.
            float u = abs(yaw) / PI;
            float height = PI / aspect;
            float v = 0.5 - pitch / height;
            vec3 colour = stars(d);
            if (has_picture) {
                float inside = smoothstep(0.0, 0.08, v) * smoothstep(1.0, 0.92, v);
                vec3 painted = texture(picture, vec2(u, clamp(v, 0.0, 1.0))).rgb;
                colour = mix(colour, painted, inside);
            }
            COLOR = colour;
        }
        """;

    // ---- the script's words ----------------------------------------------------------------------------

    private static float? Number(string? text) =>
        text is not null && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) ? value : null;

    private static Vector3? Vector(string? text) =>
        text?.Split(',') is [var x, var y, var z] && Number(x) is { } a && Number(y) is { } b && Number(z) is { } c ? new Vector3(a, b, c) : null;

    private static (float Yaw, float Pitch)? Angles(string? text) =>
        text?.Split(',') is [var yaw, var pitch] && Number(yaw) is { } y && Number(pitch) is { } p ? (y, p) : null;
}
