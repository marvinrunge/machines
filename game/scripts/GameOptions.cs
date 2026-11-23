using Godot;

namespace Machines.Game;

/// <summary>
/// The switches of the original's options screen (MachGuiCtxOptions) beside the volumes and the look: the up and down
/// keys reversed, the first-person mouse reversed, the 2D selection box, 3D sound and the gamma correction. They are
/// kept in <c>user://settings.cfg</c> and take at once. MACHINES_OPTIONS sets them for one run (screenshots, checks),
/// unsaved, e.g. <c>gamma=1.6,reverse_keys=on,reverse_mouse=on,selection_2d=on,sound_3d=off,layout=classic,dof=strong,film=on,mouse_speed=1.5,classic_mouse=on,classic_keys=on,weapon_buttons=on,fire_waits=off,hud_size=1.25,crosshair_opacity=0.8,subtitles=large,soft_scares=on,hero_view=first,chase_distance=1.2</c>.
/// Depth of field, the film look, the first-person mouse speed, the choice of mouse and keys, the triggers of the weapons, fire waiting for the gun, the HUD's size,
/// the crosshair's opacity, the story's subtitles and softened scares, and the hero view with its camera distance are the remake's own.
/// </summary>
public static class GameOptions
{
    private const string Settings = "user://settings.cfg";

    /// <summary>The gamma correction's range. The original's slider went from 0.1 to 5, where the screen is black or white.</summary>
    public const float GammaLowest = 0.5f, GammaHighest = 2f;

    /// <summary>The first-person mouse speed's range, times the standard speed.</summary>
    public const float MouseSpeedLowest = 0.25f, MouseSpeedHighest = 2.5f;

    /// <summary>The third-person camera's distance's range, times its standard arm.</summary>
    public const float ChaseDistanceLowest = 0.7f, ChaseDistanceHighest = 1.5f;

    private static bool _read, _reverseKeys, _reverseMouse, _selection2d, _sound3d = true, _classicDriving, _filmLook, _classicMouse, _classicKeys, _weaponButtons, _transitions = true, _fireWaits = true, _softScares;
    private static Captions _subtitles = Captions.Normal;
    private static float _gamma = 1, _mouseSpeed = 1, _hudSize = 1, _crosshairOpacity = Machines.Game.CrosshairOpacity.Default, _chaseDistance = 1;
    private static bool _thirdPerson = true;
    private static Blur _depthOfField;
    private static string? _playerName, _publicRelay;

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

    /// <summary>How the story's subtitles show: not at all, or small, normal or large.</summary>
    public enum Captions { Off, Small, Normal, Large }

    /// <summary>
    /// Whether the story's lines show as subtitles at the foot of the view, and how large; the voices play either way, and a
    /// line without a voice file shows all the same. Kept as <c>story/subtitles</c>.
    /// </summary>
    public static Captions Subtitles
    {
        get { Read(); return _subtitles; }
        set
        {
            Read();
            _subtitles = value;
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("story", "subtitles", value.ToString().ToLowerInvariant());
            config.Save(Settings);
        }
    }

    /// <summary>The subtitles' size, times the standard; 0 while they are off.</summary>
    public static float SubtitleScale => Subtitles switch
    {
        Captions.Small => 0.8f,
        Captions.Normal => 1f,
        Captions.Large => 1.3f,
        _ => 0,
    };

    /// <summary>
    /// Whether the story's scares come softened (Soften scares, docs/STORY.md "Horror-Level"): no loud stinger and no picture
    /// flashing up. Kept as <c>story/soft_scares</c>.
    /// </summary>
    public static bool SoftScares
    {
        get { Read(); return _softScares; }
        set => Keep(ref _softScares, value, "story", "soft_scares");
    }

    private static bool _softList;

    /// <summary>
    /// Whether the ship's list leans the view less (Soften the list, design-escape §8): the horizon tilts at
    /// <see cref="CameraTilt.SoftShare"/> of the list, for players whom the escape's swaying makes queasy; what slides
    /// and drifts with the list is not changed, nor the HUD's gyro. Kept as <c>story/soft_list</c>.
    /// </summary>
    public static bool SoftList
    {
        get { Read(); return _softList; }
        set => Keep(ref _softList, value, "story", "soft_list");
    }

    /// <summary>The sizes the HUD can be shown at, times its standard size (<see cref="HudSize"/>).</summary>
    public static readonly float[] HudSizes = [0.75f, 1f, 1.25f, 1.5f];

    /// <summary>
    /// How large the first-person HUD (the sight, the radar, the weapon panels) and Wired's own HUD are drawn, times their
    /// standard size, which grows with the screen's height (<see cref="HudScale"/>). Not in the original. Kept as <c>hud_size</c>.
    /// </summary>
    public static float HudSize
    {
        get { Read(); return _hudSize; }
        set => Keep(ref _hudSize, Mathf.Clamp(value, HudSizes[0], HudSizes[^1]), "display", "hud_size");
    }

    /// <summary>
    /// How strongly the first-person sight is drawn, from 0.1 to 1 (<see cref="Machines.Game.CrosshairOpacity"/>): its rings
    /// and marks, the target's bars, the compass under it, the spread marks and the dot in the middle; only the brackets of a
    /// located enemy stay as they are. Half at first. Not in the original. Kept as <c>crosshair_opacity</c>.
    /// </summary>
    public static float CrosshairOpacity
    {
        get { Read(); return _crosshairOpacity; }
        set => Keep(ref _crosshairOpacity, Machines.Game.CrosshairOpacity.Clamp(value), "display", "crosshair_opacity");
    }

    /// <summary>Whether a transition leads between the menus' screens (Menu Transitions, see <see cref="MenuTransition"/>).</summary>
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
    /// Whether the hero of a Wired run or a battle is driven from behind and above (third person, the default) rather than
    /// from its faceplate, with the Modern layout; switched in the match with its key (<see cref="KeyMap.Act.ChaseView"/>) or
    /// the mouse wheel, and kept as last used. Not in the original. Kept as <c>hero_view = third</c> or <c>first</c>.
    /// </summary>
    public static bool ThirdPerson
    {
        get { Read(); return _thirdPerson; }
        set
        {
            Read();
            if (_thirdPerson == value)
                return;
            _thirdPerson = value;
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("controls", "hero_view", value ? "third" : "first");
            config.Save(Settings);
        }
    }

    /// <summary>How far behind the hero the third-person camera stands, times its standard arm (Camera distance). Kept as <c>chase_distance</c>.</summary>
    public static float ChaseDistance
    {
        get { Read(); return _chaseDistance; }
        set => Keep(ref _chaseDistance, Mathf.Clamp(value, ChaseDistanceLowest, ChaseDistanceHighest), "controls", "chase_distance");
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
    /// Whether, in Wired with the Modern layout, a held trigger waits for the gun to come round to the sight (Fire waits for
    /// the gun): its weapon fires while the gun is on, while the gun sight rests on an enemy, or for a quarter of a second
    /// after the trigger went down, so a click fires at once wherever the gun points. On at first. Kept as
    /// <c>fire_waits_for_gun</c>.
    /// </summary>
    public static bool FireWaitsForGun
    {
        get { Read(); return _fireWaits; }
        set => Keep(ref _fireWaits, value, "controls", "fire_waits_for_gun");
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

    /// <summary>
    /// The name the player last went online under, given again next time (the multiplayer page's YOUR NAME); null before
    /// the first time. Kept as <c>multiplayer/name</c>.
    /// </summary>
    public static string? PlayerName
    {
        get
        {
            Read();
            return _playerName;
        }
        set
        {
            var name = value?.Trim();
            if (string.IsNullOrEmpty(name) || name == _playerName)
                return;
            Keep(ref _playerName, name, "multiplayer", "name");
        }
    }

    /// <summary>
    /// The public relay the multiplayer page offers first, a web address such as <c>wss://machines-relay-….run.app</c>:
    /// MACHINES_RELAY for one run, else <c>multiplayer/public_relay</c> in the settings, else <see cref="NetLink.PublicRelay"/>.
    /// Empty while there is none; the page then shows what to type.
    /// </summary>
    public static string PublicRelay
    {
        get
        {
            Read();
            return System.Environment.GetEnvironmentVariable("MACHINES_RELAY") is { Length: > 0 } run ? run.Trim()
                : _publicRelay is { Length: > 0 } kept ? kept : NetLink.PublicRelay;
        }
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
            _fireWaits = (bool)config.GetValue("controls", "fire_waits_for_gun", true);
            _thirdPerson = (string)config.GetValue("controls", "hero_view", "third") != "first";
            _chaseDistance = Mathf.Clamp((float)(double)config.GetValue("controls", "chase_distance", 1.0), ChaseDistanceLowest, ChaseDistanceHighest);
            _hudSize = Mathf.Clamp((float)(double)config.GetValue("display", "hud_size", 1.0), HudSizes[0], HudSizes[^1]);
            // Settings saved before the crosshair's opacity was there read the default.
            var opacity = config.GetValue("display", "crosshair_opacity", (double)Machines.Game.CrosshairOpacity.Default);
            _crosshairOpacity = opacity.VariantType is Variant.Type.Float or Variant.Type.Int
                ? Machines.Game.CrosshairOpacity.Clamp((float)opacity.AsDouble()) : Machines.Game.CrosshairOpacity.Default;
            _playerName = (string)config.GetValue("multiplayer", "name", "") is { Length: > 0 } name ? name : null;
            _publicRelay = ((string)config.GetValue("multiplayer", "public_relay", "")).Trim();
            _subtitles = Enum.TryParse<Captions>((string)config.GetValue("story", "subtitles", "normal"), true, out var captions) ? captions : Captions.Normal;
            _softScares = (bool)config.GetValue("story", "soft_scares", false);
            _softList = (bool)config.GetValue("story", "soft_list", false);
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
                case "fire_waits": _fireWaits = on; break;
                case "soft_scares": _softScares = on; break;
                case "soft_list": _softList = on; break;
                case "subtitles" when Enum.TryParse<Captions>(value, true, out var shown): _subtitles = shown; break;
                case "hero_view": _thirdPerson = value != "first"; break;
                case "chase_distance" when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var chase):
                    _chaseDistance = Mathf.Clamp(chase, ChaseDistanceLowest, ChaseDistanceHighest);
                    break;
                case "gamma" when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var gamma):
                    _gamma = Mathf.Clamp(gamma, GammaLowest, GammaHighest);
                    break;
                case "hud_size" when float.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hud):
                    _hudSize = Mathf.Clamp(hud, HudSizes[0], HudSizes[^1]);
                    break;
                case "crosshair_opacity" when Machines.Game.CrosshairOpacity.Parse(value) is { } opacity:
                    _crosshairOpacity = opacity;
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
