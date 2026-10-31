using Godot;

namespace Machines.Game;

/// <summary>
/// A screen in the menus with a machine or building on it, in a small world of its own: the screen's green behind it,
/// the remake's lighting in front, and a camera that keeps the whole model in view. The main menu's showpiece
/// (<see cref="MachineDisplay"/>) and the model viewer (<see cref="ModelsPage"/>) show theirs on one.
/// </summary>
public partial class MachineScreen : TextureRect
{
    private const float Fov = 26;

    // The camera looks from the front, turned towards the machine's left.
    private static readonly float Azimuth = 0.5f;

    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private Vector3 _target;

    /// <summary>The turntable the model stands on, its centre on the ground at the world's origin.</summary>
    protected Node3D? Turntable { get; private set; }

    /// <summary>The radius of the model's bounding sphere, metres.</summary>
    protected float Radius { get; private set; } = 1;

    /// <summary>How far the turntable has turned, radians.</summary>
    public float Yaw { get; set; }

    /// <summary>The camera's height over the ground, radians up from level.</summary>
    public float Elevation { get; set; } = 0.37f;

    /// <summary>How close the camera comes: 1 has the whole model in view, more comes closer.</summary>
    public float Zoom { get; set; } = 1;

    public override void _Ready()
    {
        SizeFlagsHorizontal = SizeFlagsVertical = SizeFlags.ExpandFill;
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.Scale;
        TextureFilter = TextureFilterEnum.Linear;
        // The scene renders at the screen's own pixels, however far the menus are scaled (see _Process).
        _viewport = new SubViewport { OwnWorld3D = true, Msaa3D = Viewport.Msaa.Msaa4X, TransparentBg = false, Size = new Vector2I(16, 16) };
        AddChild(_viewport);
        Texture = _viewport.GetTexture();
        _camera = new Camera3D { Fov = Fov, Near = 0.05f, Far = 500, Current = true };
        _viewport.AddChild(_camera);
        // Behind the machine the screen's own green, lit in the middle; in front the game's remake look (as in Studio).
        var backdrop = new CanvasLayer { Layer = -1 };
        var green = new TextureRect
        {
            Texture = new GradientTexture2D
            {
                Fill = GradientTexture2D.FillEnum.Radial,
                FillFrom = new Vector2(0.5f, 0.45f),
                FillTo = new Vector2(1.1f, 0.45f),
                Gradient = new Gradient { Colors = [new Color(0.09f, 0.24f, 0.1f), new Color(0.02f, 0.065f, 0.028f)] },
            },
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
        };
        green.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        backdrop.AddChild(green);
        _viewport.AddChild(backdrop);
        _viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Canvas,
                BackgroundCanvasMaxLayer = -1,
                AmbientLightSource = Godot.Environment.AmbientSource.Color,
                AmbientLightColor = new Color(0.62f, 0.7f, 0.64f),
                AmbientLightEnergy = 0.7f,
                TonemapMode = Godot.Environment.ToneMapper.Agx,
                AdjustmentEnabled = true,
                AdjustmentSaturation = 1.1f,
                AdjustmentContrast = 1.12f,
                GlowEnabled = true,
                GlowIntensity = 0.6f,
                GlowBloom = 0.04f,
                GlowHdrThreshold = 1.1f,
                SsaoEnabled = true,
                SsaoRadius = 1.2f,
                SsaoIntensity = 1.6f,
            },
        });
        _viewport.AddChild(Light(Mathf.DegToRad(55), Mathf.DegToRad(48), 1.7f, true));
        _viewport.AddChild(Light(Mathf.DegToRad(-80), Mathf.DegToRad(25), 0.5f, false));
        _viewport.AddChild(Light(Mathf.DegToRad(180), Mathf.DegToRad(30), 0.8f, false));   // a green rim from behind
    }

    public override void _Process(double delta)
    {
        var pixels = (Vector2I)(Size * GetGlobalTransformWithCanvas().Scale).Round();
        if (pixels.X > 0 && pixels.Y > 0 && pixels != _viewport.Size)
            _viewport.Size = pixels;
        if (Turntable is not null)
            Turntable.Rotation = new Vector3(0, Yaw, 0);
        Aim();
    }

    /// <summary>
    /// Puts <paramref name="view"/> on a new turntable in place of what stood there: its centre over the turntable's,
    /// its lowest point on the ground. Null clears the screen.
    /// </summary>
    protected void Place(Node3D? view)
    {
        Turntable?.QueueFree();
        Turntable = null;
        if (view is null)
            return;
        var turntable = new Node3D();
        turntable.AddChild(view);
        _viewport.AddChild(turntable);
        var box = new Aabb();
        var first = true;
        foreach (var mesh in view.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>())
        {
            var part = mesh.GlobalTransform * mesh.GetAabb();
            box = first ? part : box.Merge(part);
            first = false;
        }
        var centre = box.GetCenter();
        view.Position = new Vector3(-centre.X, -box.Position.Y, -centre.Z);
        Radius = Math.Max(box.Size.Length() / 2, 0.5f);
        _target = new Vector3(0, box.Size.Y * 0.45f, 0);
        Turntable = turntable;
        Turntable.Rotation = new Vector3(0, Yaw, 0);
        Aim();
    }

    /// <summary>Backs the camera off far enough for the model's whole sphere, across the screen's narrower side.</summary>
    private void Aim()
    {
        var size = _viewport.Size;
        var aspect = size.Y > 0 ? (float)size.X / size.Y : 1;
        var half = Mathf.DegToRad(Fov / 2);
        var narrow = Math.Min(half, Mathf.Atan(Mathf.Tan(half) * aspect));
        var direction = new Vector3(Mathf.Cos(Elevation) * Mathf.Cos(Azimuth), Mathf.Sin(Elevation), Mathf.Cos(Elevation) * Mathf.Sin(Azimuth));
        _camera.Position = _target + direction * (Radius / Mathf.Sin(narrow) * 0.95f / Zoom);
        _camera.LookAt(_target, Vector3.Up);
    }

    private static DirectionalLight3D Light(float azimuth, float elevation, float energy, bool shadow)
    {
        var from = new Vector3(Mathf.Cos(elevation) * Mathf.Cos(azimuth), Mathf.Sin(elevation), Mathf.Cos(elevation) * Mathf.Sin(azimuth));
        return new DirectionalLight3D
        {
            Transform = new Transform3D(Basis.LookingAt(-from, Vector3.Up), from * 50),
            LightEnergy = energy,
            LightColor = shadow || energy < 0.6f ? Colors.White : new Color(0.55f, 1f, 0.5f),
            ShadowEnabled = shadow,
            ShadowBlur = 1.5f,
            DirectionalShadowMaxDistance = 80,
        };
    }
}
