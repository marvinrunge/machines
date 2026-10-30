using Godot;
using Machines.Import.Model;

namespace Machines.Game;

/// <summary>
/// The main menu's showpiece: a machine turning slowly on the big chip's screen, another every few seconds, in each
/// race's colours in turn. It is built as the game builds it (<see cref="ClassicAssets.RigFor"/>), so it shows the
/// look the player has chosen; without imported models the screen stays empty.
/// </summary>
public partial class MachineDisplay : TextureRect
{
    private static readonly (string Id, string Name)[] Machines =
    [
        ("AGGRESSOR_5_NINJA_L_MULTI_LAUNCHER7_T_GORILLA_PUNCH_R_MULTI_LAUNCHER7", "Gorilla"),
        ("AGGRESSOR_5_KNIGHT_LRT_PLASMA_CANNON2_X3", "Knight"),
        ("ADMINISTRATOR_5_COMMANDER_LR_PULSE_CANNON_X2", "Commander"),
        ("AGGRESSOR_4_BALLISTA_LR_MULTI_LAUNCHER2_X2", "Ballista"),
        ("CONSTRUCTOR_5_BEHEMOTH", "Behemoth"),
        ("AGGRESSOR_3_ASSASSIN_LR_PLASMA_RIFLE_X2", "Reaper"),
        ("ADMINISTRATOR_2_BOSS_T_BEE_BOMB", "Boss"),
        ("APC_5", "APC"),
    ];

    private static readonly Race[] Races = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    private const float Shown = 9;      // seconds per machine
    private const float Fov = 26;

    private readonly ClassicAssets _assets;
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private Node3D? _machine;
    private int _next = (int)(GD.Randi() % Machines.Length);
    private double _shownFor;

    /// <summary>The machine on the screen now, for the caption under it.</summary>
    public string Caption { get; private set; } = "";

    public event Action? Changed;

    public MachineDisplay(ClassicAssets assets) => _assets = assets;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Ignore;
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
        ShowNext();
    }

    public override void _Process(double delta)
    {
        var pixels = (Vector2I)(Size * GetGlobalTransformWithCanvas().Scale).Round();
        if (pixels.X > 0 && pixels.Y > 0 && pixels != _viewport.Size)
            _viewport.Size = pixels;
        _shownFor += delta;
        if (_machine is not null)
        {
            _machine.RotateY((float)delta * 0.35f);
            Aim();
        }
        if (_shownFor >= Shown)
            ShowNext();
    }

    private void ShowNext()
    {
        _shownFor = 0;
        _machine?.QueueFree();
        _machine = null;
        for (var tries = 0; tries < Machines.Length && _machine is null; tries++)
        {
            var (id, name) = Machines[_next % Machines.Length];
            var race = Races[_next / Machines.Length % Races.Length];
            _next = (_next + 1) % (Machines.Length * Races.Length);
            if (_assets.RigFor(id, race) is not { } rig)
                continue;
            var turntable = new Node3D();
            var view = RigView.Create(rig);
            turntable.AddChild(view);
            _viewport.AddChild(turntable);
            Frame(turntable, view);
            _machine = turntable;
            Caption = $"{name}  ·  {race}";
        }
        Changed?.Invoke();
    }

    /// <summary>Stands the machine on the turntable's centre and backs the camera off until all of it is in view.</summary>
    private void Frame(Node3D turntable, Node3D view)
    {
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
        _radius = Math.Max(box.Size.Length() / 2, 0.5f);
        _target = new Vector3(0, box.Size.Y * 0.45f, 0);
        turntable.Rotation = new Vector3(0, Mathf.DegToRad(-20), 0);
        Aim();
    }

    private float _radius = 1;
    private Vector3 _target;

    /// <summary>Backs the camera off far enough for the machine's whole sphere, across the screen's narrower side.</summary>
    private void Aim()
    {
        var size = _viewport.Size;
        var aspect = size.Y > 0 ? (float)size.X / size.Y : 1;
        var half = Mathf.DegToRad(Fov / 2);
        var narrow = Math.Min(half, Mathf.Atan(Mathf.Tan(half) * aspect));
        var direction = new Vector3(0.82f, 0.36f, 0.45f).Normalized();
        _camera.Position = _target + direction * (_radius / Mathf.Sin(narrow) * 0.95f);
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
