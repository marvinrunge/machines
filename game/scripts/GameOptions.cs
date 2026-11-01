using Godot;

namespace Machines.Game;

/// <summary>
/// The switches of the original's options screen (MachGuiCtxOptions) beside the volumes and the look: the up and down
/// keys reversed, the first-person mouse reversed, the 2D selection box, 3D sound and the gamma correction. They are
/// kept in <c>user://settings.cfg</c> and take at once. MACHINES_OPTIONS sets them for one run (screenshots, checks),
/// unsaved, e.g. <c>gamma=1.6,reverse_keys=on,reverse_mouse=on,selection_2d=on,sound_3d=off,layout=classic</c>.
/// </summary>
public static class GameOptions
{
    private const string Settings = "user://settings.cfg";

    /// <summary>The gamma correction's range. The original's slider went from 0.1 to 5, where the screen is black or white.</summary>
    public const float GammaLowest = 0.5f, GammaHighest = 2f;

    private static bool _read, _reverseKeys, _reverseMouse, _selection2d, _sound3d = true, _classicDriving;
    private static float _gamma = 1;

    /// <summary>
    /// Whether the up arrow looks up rather than down where the arrows look (Reverse Up/Down Keys): Shift and the arrows
    /// in first person, Ctrl, Shift and the arrows in the ground camera. Unreversed they push the view down as a
    /// pilot's stick does.
    /// </summary>
    public static bool ReverseKeys
    {
        get { Read(); return _reverseKeys; }
        set => Keep(ref _reverseKeys, value, "controls", "reverse_keys");
    }

    /// <summary>Whether pushing the mouse forward looks down in first person rather than up (Reverse F.P. Mouse).</summary>
    public static bool ReverseMouse
    {
        get { Read(); return _reverseMouse; }
        set => Keep(ref _reverseMouse, value, "controls", "reverse_mouse");
    }

    /// <summary>
    /// Whether first person drives as the original did (Classic: A and D turn the machine, the mouse turns it too and
    /// turns the upper body only with Shift or the right button) rather than as in a tank game (Modern, the default:
    /// A and D step sideways where the machine can, Q and E turn it, the mouse always turns the upper body). Kept as
    /// <c>layout = classic</c> or <c>modern</c>.
    /// </summary>
    public static bool ClassicDriving
    {
        get { Read(); return _classicDriving; }
        set
        {
            Read();
            _classicDriving = value;
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("controls", "layout", value ? "classic" : "modern");
            config.Save(Settings);
        }
    }

    /// <summary>
    /// Whether what is selected or pointed at is marked by the corners of a frame facing the camera rather than by
    /// a wireframe box (2D Selection box, MachPhysMarker).
    /// </summary>
    public static bool Selection2d
    {
        get { Read(); return _selection2d; }
        set => Keep(ref _selection2d, value, "controls", "selection_2d");
    }

    /// <summary>Whether the world's sounds come from where they happen, left or right; without, only their distance tells (3D Sound).</summary>
    public static bool Sound3d
    {
        get { Read(); return _sound3d; }
        set => Keep(ref _sound3d, value, "audio", "sound_3d");
    }

    /// <summary>
    /// The gamma correction of everything the window shows, menus included (the original's gamma ramp): each colour
    /// becomes colour^(1/gamma), so above 1 the dark brightens, below it darkens.
    /// </summary>
    public static float Gamma
    {
        get { Read(); return _gamma; }
        set
        {
            Keep(ref _gamma, Mathf.Clamp(value, GammaLowest, GammaHighest), "display", "gamma");
            ShowGamma();
        }
    }

    // Reads the settings the first time one is asked for, then MACHINES_OPTIONS over them.
    private static void Read()
    {
        if (_read)
            return;
        _read = true;
        var config = new ConfigFile();
        if (config.Load(Settings) == Error.Ok)
        {
            _reverseKeys = (bool)config.GetValue("controls", "reverse_keys", false);
            _reverseMouse = (bool)config.GetValue("controls", "reverse_mouse", false);
            _selection2d = (bool)config.GetValue("controls", "selection_2d", false);
            _classicDriving = (string)config.GetValue("controls", "layout", "modern") == "classic";
            _sound3d = (bool)config.GetValue("audio", "sound_3d", true);
            _gamma = Mathf.Clamp((float)(double)config.GetValue("display", "gamma", 1.0), GammaLowest, GammaHighest);
        }
        foreach (var part in (System.Environment.GetEnvironmentVariable("MACHINES_OPTIONS") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var (key, value) = part.Split('=') is [var k, var v] ? (k, v) : (part, "on");
            var on = value is "on" or "1" or "true";
            switch (key)
            {
                case "reverse_keys": _reverseKeys = on; break;
                case "reverse_mouse": _reverseMouse = on; break;
                case "selection_2d": _selection2d = on; break;
                case "sound_3d": _sound3d = on; break;
                case "layout": _classicDriving = value == "classic"; break;
                case "gamma" when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gamma):
                    _gamma = Mathf.Clamp(gamma, GammaLowest, GammaHighest);
                    break;
            }
        }
    }

    private static void Keep<T>(ref T field, T value, string section, string key)
    {
        Read();
        field = value;
        var config = new ConfigFile();
        config.Load(Settings);
        config.SetValue(section, key, Variant.From(value));
        config.Save(Settings);
    }

    // ---- gamma ----------------------------------------------------------------------------------

    private static CanvasLayer? _gammaLayer;
    private static ShaderMaterial? _gammaMaterial;

    private const string GammaShader = """
        shader_type canvas_item;
        uniform sampler2D screen : hint_screen_texture, filter_nearest;
        uniform float gamma = 1.0;
        void fragment() {
            vec3 colour = texture(screen, SCREEN_UV).rgb;
            COLOR = vec4(pow(max(colour, vec3(0.0)), vec3(1.0 / gamma)), 1.0);
        }
        """;

    /// <summary>
    /// Puts the gamma correction over the window: a layer above every other on the tree's root, where it stays from
    /// the menus to the match and back. At a gamma of 1 it is hidden and costs nothing.
    /// </summary>
    public static void ShowGamma()
    {
        if (Engine.GetMainLoop() is not SceneTree tree)
            return;
        if (_gammaLayer is null || !GodotObject.IsInstanceValid(_gammaLayer))
        {
            _gammaMaterial = new ShaderMaterial { Shader = new Shader { Code = GammaShader } };
            var picture = new ColorRect { Material = _gammaMaterial, MouseFilter = Control.MouseFilterEnum.Ignore };
            picture.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
            _gammaLayer = new CanvasLayer { Layer = 128, Name = "Gamma" };
            _gammaLayer.AddChild(picture);
            tree.Root.CallDeferred(Node.MethodName.AddChild, _gammaLayer);
        }
        _gammaMaterial!.SetShaderParameter("gamma", Gamma);
        _gammaLayer.Visible = !Mathf.IsEqualApprox(Gamma, 1);
    }
}
