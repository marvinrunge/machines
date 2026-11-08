using Godot;

namespace Machines.Game;

/// <summary>
/// The switches of the original's options screen (MachGuiCtxOptions) beside the volumes and the look: the up and down
/// keys reversed, the first-person mouse reversed, the 2D selection box, 3D sound and the gamma correction. They are
/// kept in <c>user://settings.cfg</c> and take at once. MACHINES_OPTIONS sets them for one run (screenshots, checks),
/// unsaved, e.g. <c>gamma=1.6,reverse_keys=on,reverse_mouse=on,selection_2d=on,sound_3d=off,layout=classic,dof=strong,film=on,mouse_speed=1.5,classic_mouse=on,classic_keys=on,weapon_buttons=on</c>.
/// Depth of field, the film look, the first-person mouse speed, the choice of mouse and keys and the triggers of the weapons are the remake's own.
/// </summary>
public static class GameOptions
{
    private const string Settings = "user://settings.cfg";

    /// <summary>The gamma correction's range. The original's slider went from 0.1 to 5, where the screen is black or white.</summary>
    public const float GammaLowest = 0.5f, GammaHighest = 2f;

    /// <summary>The first-person mouse speed's range, times the standard speed.</summary>
    public const float MouseSpeedLowest = 0.25f, MouseSpeedHighest = 2.5f;

    private static bool _read, _reverseKeys, _reverseMouse, _selection2d, _sound3d = true, _classicDriving, _filmLook, _classicMouse, _classicKeys, _weaponButtons, _transitions = true;
    private static float _gamma = 1, _mouseSpeed = 1;
    private static Blur _depthOfField;

    /// <summary>How far from what the camera looks at the picture blurs (<see cref="DepthOfField"/>).</summary>
    public enum Blur { Off, Subtle, Strong }

    /// <summary>
    /// Depth of field: what lies nearer or further than what the camera looks at blurs, a little (Subtle) or much
    /// (Strong); the strategy camera sharp around its focus on the ground, the first-person view in the distance.
    /// Not in the original. Kept as <c>depth_of_field = off</c>, <c>subtle</c> or <c>strong</c>.
    /// </summary>
    public static Blur DepthOfField
    {
        get { Read(); return _depthOfField; }
        set
        {
            Read();
            _depthOfField = value;
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("display", "depth_of_field", value.ToString().ToLowerInvariant());
            config.Save(Settings);
        }
    }

    /// <summary>Whether the original's films play between the menus' screens (Menu Transitions, see <see cref="MenuFilms"/>).</summary>
    public static bool Transitions
    {
        get { Read(); return _transitions; }
        set => Keep(ref _transitions, value, "display", "transitions");
    }

    /// <summary>Whether the match looks filmed: darker corners, fine grain and graded colours (<see cref="FilmLook"/>). Not in the original.</summary>
    public static bool FilmLook
    {
        get { Read(); return _filmLook; }
        set => Keep(ref _filmLook, value, "display", "film_look");
    }

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
    /// How far the view turns in first person for a move of the mouse, times the standard speed (F.P. Mouse Speed, not
    /// in the original). Kept as <c>mouse_speed</c>.
    /// </summary>
    public static float MouseSpeed
    {
        get { Read(); return _mouseSpeed; }
        set => Keep(ref _mouseSpeed, Mathf.Clamp(value, MouseSpeedLowest, MouseSpeedHighest), "controls", "mouse_speed");
    }

    /// <summary>
    /// Whether first person drives as the original did (Classic: A and D turn the machine, the mouse turns it too and
    /// turns the upper body only with Shift or the right button) rather than as in a tank game (Modern, the default:
    /// A and D step sideways where the machine can, Q and E turn it, the mouse looks round at once and the upper body,
    /// or the machine without one, follows). Kept as <c>layout = classic</c> or <c>modern</c>.
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
    /// Whether the mouse works as the original's did (Classic Mouse): the left button selects and gives every order
    /// through the intelligent cursor, a short click of the right one takes the selection away or puts a command back,
    /// and dragging with it moves the camera. Otherwise, as in today's strategy games, the left button selects and the
    /// right one gives the order. Kept as <c>classic_mouse</c>.
    /// </summary>
    public static bool ClassicMouse
    {
        get { Read(); return _classicMouse; }
        set => Keep(ref _classicMouse, value, "controls", "classic_mouse");
    }

    /// <summary>
    /// Whether the keys are the original's (Classic Keys): every command on its bare letter, the arrows and Page Up and
    /// Page Down for the camera, the original's height limits for the zenith camera. Otherwise the remake's layout holds
    /// (<see cref="KeyMap"/>), and only the command letters it leaves free work. Kept as <c>classic_keys</c>.
    /// </summary>
    public static bool ClassicKeys
    {
        get { Read(); return _classicKeys; }
        set => Keep(ref _classicKeys, value, "controls", "classic_keys");
    }

    /// <summary>
    /// Whether each weapon has its own trigger in first person (Mouse button per weapon): the left mouse button fires the
    /// left weapon, the right one the right weapon, the wheel the top one, and held ones fire together. The right button then
    /// no longer turns the upper body or leaves by a double click, and Tab no longer selects. Off at first, as the
    /// original's left button and Tab stay the default; a run of Wired always has it (<see cref="Game.WeaponButtons"/>).
    /// Kept as <c>weapon_buttons</c>.
    /// </summary>
    public static bool WeaponButtons
    {
        get { Read(); return _weaponButtons; }
        set => Keep(ref _weaponButtons, value, "controls", "weapon_buttons");
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
            _mouseSpeed = Mathf.Clamp((float)(double)config.GetValue("controls", "mouse_speed", 1.0), MouseSpeedLowest, MouseSpeedHighest);
            _depthOfField = Enum.TryParse<Blur>((string)config.GetValue("display", "depth_of_field", "off"), true, out var blur) ? blur : Blur.Off;
            _filmLook = (bool)config.GetValue("display", "film_look", false);
            _transitions = (bool)config.GetValue("display", "transitions", true);
            _classicMouse = (bool)config.GetValue("controls", "classic_mouse", false);
            _classicKeys = (bool)config.GetValue("controls", "classic_keys", false);
            _weaponButtons = (bool)config.GetValue("controls", "weapon_buttons", false);
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
                case "dof" when Enum.TryParse<Blur>(value, true, out var blur): _depthOfField = blur; break;
                case "film": _filmLook = on; break;
                case "classic_mouse": _classicMouse = on; break;
                case "classic_keys": _classicKeys = on; break;
                case "weapon_buttons": _weaponButtons = on; break;
                case "gamma" when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gamma):
                    _gamma = Mathf.Clamp(gamma, GammaLowest, GammaHighest);
                    break;
                case "mouse_speed" when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var speed):
                    _mouseSpeed = Mathf.Clamp(speed, MouseSpeedLowest, MouseSpeedHighest);
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
