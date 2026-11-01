using Godot;

namespace Machines.Game;

/// <summary>
/// The look of a film over the match, the remake's own: darker corners, fine grain that changes every frame, and
/// colours graded towards cool shadows and warm light. A cinematic adds black bars above and below and fades to and
/// from black. It lies under the panel and the cursor, which stay as they are.
/// </summary>
public partial class FilmLook : CanvasLayer
{
    private const string Shader = """
        shader_type canvas_item;
        uniform sampler2D screen : hint_screen_texture, filter_linear;
        uniform float vignette = 0.0;
        uniform float grain = 0.0;
        uniform float grade = 0.0;
        uniform float bars = 0.0;
        uniform float fade = 0.0;

        float noise(vec2 at) {
            return fract(sin(dot(at, vec2(12.9898, 78.233))) * 43758.5453);
        }

        void fragment() {
            vec3 colour = texture(screen, SCREEN_UV).rgb;
            float light = dot(colour, vec3(0.2126, 0.7152, 0.0722));
            // Cool shadows, warm light, a little more contrast and colour.
            vec3 graded = colour * mix(vec3(0.93, 1.0, 1.07), vec3(1.07, 1.0, 0.9), smoothstep(0.1, 0.75, light));
            graded = (graded - 0.45) * 1.08 + 0.45;
            graded = mix(vec3(dot(graded, vec3(0.2126, 0.7152, 0.0722))), graded, 1.1);
            colour = mix(colour, max(graded, vec3(0.0)), grade);
            // The corners darken as through a lens.
            vec2 centred = (SCREEN_UV - 0.5) * vec2(1.0, 0.8);
            colour *= 1.0 - vignette * smoothstep(0.25, 0.75, length(centred));
            // Grain, strongest in the middle tones, new every frame.
            float n = noise(FRAGCOORD.xy + fract(TIME * 7.13) * vec2(311.7, 183.3)) - 0.5;
            colour += n * grain * (1.0 - abs(light - 0.45));
            colour *= 1.0 - fade;
            if (SCREEN_UV.y < bars || SCREEN_UV.y > 1.0 - bars)
                colour = vec3(0.0);
            COLOR = vec4(colour, 1.0);
        }
        """;

    private readonly ShaderMaterial _material = new() { Shader = new Shader { Code = Shader } };
    private readonly ColorRect _picture = new() { MouseFilter = Control.MouseFilterEnum.Ignore };

    /// <summary>How much the corners darken, 0 to 1.</summary>
    public float Vignette { set => _material.SetShaderParameter("vignette", value); }

    /// <summary>How strong the grain is, as a share of full brightness.</summary>
    public float Grain { set => _material.SetShaderParameter("grain", value); }

    /// <summary>How much of the graded colour shows, 0 to 1.</summary>
    public float Grade { set => _material.SetShaderParameter("grade", value); }

    /// <summary>The height of each black bar as a share of the picture's.</summary>
    public float Bars { set => _material.SetShaderParameter("bars", value); }

    /// <summary>How far the picture has faded to black, 0 to 1.</summary>
    public float Fade { set => _material.SetShaderParameter("fade", value); }

    public FilmLook()
    {
        // Below the panel (layer 1) and the gamma correction over everything.
        Layer = -1;
        Name = "FilmLook";
        _picture.Material = _material;
        _picture.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        AddChild(_picture);
    }

    /// <summary>The film look the options ask for over a match: corners, grain and colours, no bars.</summary>
    public void Match()
    {
        Visible = GameOptions.FilmLook;
        Vignette = 0.35f;
        Grain = 0.035f;
        Grade = 0.8f;
    }

    /// <summary>
    /// Blurs what lies away from <paramref name="focus"/> metres in front of <paramref name="camera"/>:
    /// <paramref name="strength"/> 0 leaves it sharp, 1 is the options' Subtle, 2 their Strong, more blurs more.
    /// <paramref name="near"/> blurs what is nearer too; the first-person view leaves its own machine sharp.
    /// </summary>
    public static void Focus(Camera3D camera, float focus, float strength, bool near = true)
    {
        if (strength <= 0)
        {
            camera.Attributes = null;
            return;
        }
        if (camera.Attributes is not CameraAttributesPractical attributes)
        {
            camera.Attributes = attributes = new CameraAttributesPractical();
            RenderingServer.CameraAttributesSetDofBlurQuality(RenderingServer.DofBlurQuality.High, false);
            RenderingServer.CameraAttributesSetDofBlurBokehShape(RenderingServer.DofBokehShape.Circle);
        }
        // The sharp zone narrows as the blur grows: about from 0.55 to 1.4 times the focus at Subtle, 0.75 to 1.2 at Strong.
        var depth = 1 / (1 + 0.6f * strength);
        attributes.DofBlurFarEnabled = true;
        attributes.DofBlurFarDistance = focus * (1 + 0.65f * depth);
        attributes.DofBlurFarTransition = focus * 1.2f * depth;
        attributes.DofBlurNearEnabled = near;
        attributes.DofBlurNearDistance = focus * (1 - 0.7f * depth);
        attributes.DofBlurNearTransition = focus * 0.35f * depth;
        attributes.DofBlurAmount = 0.05f * strength;
    }
}
