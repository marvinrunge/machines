using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Machines.Sim.Survival;

namespace Machines.Game;

/// <summary>
/// Pictures of Wired heroes as <see cref="HeroRig"/> puts them together, on a neutral ground from three quarters in front.
/// MACHINES_HERO_SHOT=&lt;png&gt;,&lt;what&gt; takes one and quits, in the look of MACHINES_LOOK:
/// <list type="bullet">
/// <item><c>sheet</c>: every chassis bare and with light, medium and heavy weapons, as far as its slots allow;</item>
/// <item><c>steps</c>: the way from a Grunt with a Bolter to a fully armed Knight, as the hangar goes it;</item>
/// <item><c>all,&lt;chassis&gt;</c>: every weapon its slots take, on all of them at once;</item>
/// <item><c>&lt;chassis&gt;,&lt;left&gt;,&lt;right&gt;,&lt;top&gt;</c>: that hero alone, - for an empty slot.</item>
/// </list>
/// MACHINES_HERO_RACE=blue wears that race's colours; MACHINES_HERO_TURN=&lt;degrees&gt; walks the camera round it and
/// MACHINES_HERO_RISE=&lt;degrees&gt; sets its height, below the ground for what hangs under a flyer.
/// </summary>
public partial class HeroStudio(string path, string[] what) : Node
{
    private const float Fov = 18;
    private const int Cell = 400, Caption = 56, Columns = 8;

    private static readonly float Azimuth = Mathf.DegToRad(38);
    private static readonly Color Ground = new(0.56f, 0.57f, 0.58f), Ink = new(0.08f, 0.08f, 0.09f);

    private static readonly string[] Lights = ["BOLTER", "AUTO_CANNON", "ELECTRIC_CHARGE", "FLAME_THROWER2", "PULSE_RIFLE", "PLASMA_RIFLE"];
    private static readonly string[] Mediums = ["HEAVY_BOLTER1", "PLASMA_CANNON1", "MULTI_LAUNCHER3", "MULTI_LAUNCHER5", "HEAVY_BOLTER2", "PULSE_CANNON", "MULTI_LAUNCHER4"];
    private static readonly string[] Heavies = ["PLASMA_CANNON2", "MULTI_LAUNCHER1", "MULTI_LAUNCHER7", "MULTI_LAUNCHER2"];

    private ClassicAssets _assets = null!;
    private Race _race = Race.Red;
    private float _turn, _rise = Mathf.DegToRad(20);
    private SubViewport _viewport = null!;
    private Camera3D _camera = null!;
    private Node3D _stage = null!, _floor = null!;

    public override async void _Ready()
    {
        _assets = new ClassicAssets(Paths.Root);
        if (Enum.TryParse<Race>(System.Environment.GetEnvironmentVariable("MACHINES_HERO_RACE"), true, out var race))
            _race = race;
        if (float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_HERO_TURN"), System.Globalization.CultureInfo.InvariantCulture, out var turn))
            _turn = Mathf.DegToRad(turn);
        if (float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_HERO_RISE"), System.Globalization.CultureInfo.InvariantCulture, out var rise))
            _rise = Mathf.DegToRad(rise);
        BuildStage();
        await (what switch
        {
            ["sheet"] => Sheet(),
            ["steps"] => Steps(),
            ["all", var chassis] => All(chassis),
            [var chassis, .. var weapons] => One(chassis, weapons),
            _ => Task.CompletedTask,
        });
        GetTree().Quit();
    }

    private void BuildStage()
    {
        _viewport = new SubViewport
        {
            OwnWorld3D = true,
            Msaa3D = Viewport.Msaa.Msaa8X,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled,
            Size = new Vector2I(Cell, Cell),
        };
        AddChild(_viewport);
        _stage = new Node3D { Name = "Stage" };
        _viewport.AddChild(_stage);
        _camera = new Camera3D { Fov = Fov, Near = 0.1f, Far = 1000, Current = true };
        _viewport.AddChild(_camera);

        // Studio's stage in the remake's lighting; the classic look has its plain light, without tone mapping.
        var environment = new Godot.Environment
        {
            BackgroundMode = Godot.Environment.BGMode.Color,
            BackgroundColor = Ground,
            AmbientLightSource = Godot.Environment.AmbientSource.Color,
            AmbientLightColor = new Color(0.74f, 0.75f, 0.78f),
            AmbientLightEnergy = 0.75f,
        };
        if (GraphicsSettings.Lighting)
        {
            environment.Sky = new Sky
            {
                SkyMaterial = new ProceduralSkyMaterial
                {
                    SkyTopColor = new Color(0.78f, 0.82f, 0.88f),
                    SkyHorizonColor = new Color(0.93f, 0.93f, 0.93f),
                    GroundHorizonColor = new Color(0.85f, 0.85f, 0.85f),
                    GroundBottomColor = new Color(0.45f, 0.45f, 0.45f),
                },
            };
            environment.AmbientLightSource = Godot.Environment.AmbientSource.Sky;
            environment.AmbientLightEnergy = 0.8f;
            environment.ReflectedLightSource = Godot.Environment.ReflectionSource.Sky;
            environment.TonemapMode = Godot.Environment.ToneMapper.Agx;
            environment.AdjustmentEnabled = true;
            environment.AdjustmentSaturation = 1.1f;
            environment.AdjustmentContrast = 1.12f;
            environment.SsaoEnabled = true;
            environment.SsaoRadius = 1.2f;
            environment.SsaoIntensity = 1.6f;
        }
        _viewport.AddChild(new WorldEnvironment { Environment = environment });
        _viewport.AddChild(Lamp(Mathf.DegToRad(60), Mathf.DegToRad(50), GraphicsSettings.Lighting ? 1.6f : 1.1f, true));
        var fill = Lamp(Mathf.DegToRad(-70), Mathf.DegToRad(20), 0.45f, false);
        fill.LightCullMask = 1;
        _viewport.AddChild(fill);

        // Only the shadow shows on the ground, so it stays the background's own colour; a lamp without shadows would wipe it out.
        _floor = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * 400 },
            Layers = 2,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            MaterialOverride = new StandardMaterial3D
            {
                ShadowToOpacity = true,
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                AlbedoColor = new Color(0.62f, 0.62f, 0.64f) * (GraphicsSettings.Lighting ? 0.5f : 1),
                Roughness = 1,
                MetallicSpecular = 0,
            },
        };
        _viewport.AddChild(_floor);
    }

    private static DirectionalLight3D Lamp(float azimuth, float elevation, float energy, bool shadow)
    {
        var from = Towards(azimuth, elevation);
        return new DirectionalLight3D
        {
            Transform = new Transform3D(Basis.LookingAt(-from, Vector3.Up), from * 50),
            LightEnergy = energy,
            ShadowEnabled = shadow,
            ShadowBlur = 1.5f,
            DirectionalShadowMaxDistance = 300,
        };
    }

    /// <summary>A direction from the machine: azimuth from its front (+x) towards +z, elevation above.</summary>
    private static Vector3 Towards(float azimuth, float elevation) =>
        new(Mathf.Cos(elevation) * Mathf.Cos(azimuth), Mathf.Sin(elevation), Mathf.Cos(elevation) * Mathf.Sin(azimuth));

    /// <summary>A hero alone, its picture <paramref name="size"/> wide and high, rendered twice as large for sharp edges.</summary>
    private async Task<Image> Photograph(Rig? rig, Vector2I size)
    {
        var picture = Image.CreateEmpty(size.X, size.Y, false, Image.Format.Rgba8);
        if (rig is null)
        {
            picture.Fill(Colors.Black);
            return picture;
        }
        var view = RigView.Create(rig);
        _stage.AddChild(view);
        // It stands as it does in the game: legs in their resting cycle, hovering parts where their motion has them.
        for (var f = 0; f < 3; f++)
            view.Animate(1f / 30, 0, 0, 0, 0);
        var points = Points(view, rig);
        var low = points.Min(p => p.Y);
        _floor.Position = Vector3.Up * Mathf.Min(low, 0.02f);
        // A glider flies high over its export's ground: the floor comes up under it.
        if (low > 3)
            _floor.Position = Vector3.Up * (low - 1.2f);
        Frame(points, (float)size.X / size.Y);
        _viewport.Size = size * 2;
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        // A few frames, so the ambient occlusion has settled.
        for (var f = 0; f < 4; f++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var image = _viewport.GetTexture().GetImage();
        _viewport.RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled;
        view.QueueFree();
        image.Convert(Image.Format.Rgba8);
        image.Resize(size.X, size.Y, Image.Interpolation.Lanczos);
        return image;
    }

    /// <summary>The corners of its parts; the export's root, which only stands it on the ground, does not count, nor do sprites.</summary>
    public static List<Vector3> Points(RigView view, Rig rig)
    {
        var points = new List<Vector3>();
        for (var i = 1; i < rig.Nodes.Length; i++)
        {
            if (view.FindChild($"Part{i}", true, false) is not Node3D part || part.GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: ArrayMesh mesh } instance)
                continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
                if (mesh.SurfaceGetMaterial(s) is not ShaderMaterial)
                    points.AddRange(mesh.SurfaceGetArrays(s)[(int)Mesh.ArrayType.Vertex].AsVector3Array().Select(v => instance.GlobalTransform * v));
        }
        return points.Count > 0 ? points : [Vector3.Zero];
    }

    /// <summary>The camera a little above, far enough back for all of it with a small margin.</summary>
    private void Frame(List<Vector3> points, float aspect)
    {
        var towards = Towards(Azimuth + _turn, _rise);
        _floor.Visible = _rise > 0;
        var basis = Basis.LookingAt(-towards, Vector3.Up);
        var (left, right) = (points.Min(p => p.Dot(basis.X)), points.Max(p => p.Dot(basis.X)));
        var (low, high) = (points.Min(p => p.Dot(basis.Y)), points.Max(p => p.Dot(basis.Y)));
        var depth = points.Average(p => p.Dot(basis.Z));
        var centre = basis.X * (left + right) / 2 + basis.Y * (low + high) / 2 + basis.Z * depth;
        var half = Mathf.Max((right - left) / 2 / aspect, (high - low) / 2) * 1.12f;
        var distance = half / Mathf.Tan(Mathf.DegToRad(Fov / 2)) + (points.Max(p => p.Dot(basis.Z)) - depth);
        _camera.Transform = new Transform3D(basis, centre + towards * distance);
    }

    private static Label Text(string text, Vector2 at, Vector2 size, int fontSize, HorizontalAlignment align = HorizontalAlignment.Center)
    {
        var label = new Label
        {
            Text = text,
            HorizontalAlignment = align,
            VerticalAlignment = VerticalAlignment.Center,
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        label.AddThemeFontOverride("font", MenuTheme.Font);
        label.AddThemeFontSizeOverride("font_size", fontSize);
        label.AddThemeColorOverride("font_color", Ink);
        // Only now, or the words not yet wrapped would stretch it.
        label.Position = at;
        label.Size = size;
        return label;
    }

    /// <summary>Lays pictures and words out and saves the page.</summary>
    private async Task Save(Vector2I size, IEnumerable<Node> content)
    {
        var page = new SubViewport { Size = size, Disable3D = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Disabled };
        AddChild(page);
        page.AddChild(new ColorRect { Color = Ground, Size = size });
        foreach (var node in content)
            page.AddChild(node);
        page.RenderTargetUpdateMode = SubViewport.UpdateMode.Always;
        for (var f = 0; f < 3; f++)
            await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        page.GetTexture().GetImage().SavePng(path);
        GD.Print($"hero: {path}");
    }

    private static TextureRect Pasted(Image picture, Vector2 at) =>
        new() { Texture = ImageTexture.CreateFromImage(picture), Position = at, Size = new Vector2(picture.GetWidth(), picture.GetHeight()) };

    /// <summary>A grid of heroes, each with its words under it.</summary>
    private async Task Grid(List<(string Label, Rig? Rig)> cells, int columns)
    {
        var content = new List<Node>();
        for (var k = 0; k < cells.Count; k++)
        {
            var at = new Vector2(k % columns * Cell, k / columns * (Cell + Caption));
            content.Add(Pasted(await Photograph(cells[k].Rig, new Vector2I(Cell, Cell)), at));
            content.Add(Text(cells[k].Label, at + new Vector2(4, Cell - 4), new Vector2(Cell - 8, Caption), 16));
        }
        await Save(new Vector2I(columns * Cell, (cells.Count + columns - 1) / columns * (Cell + Caption)), content);
    }

    private Rig? Hero(WiredChassis chassis, params string?[] weapons) => HeroRig.Compose(_assets, _race, chassis.Id, weapons);

    private static string Words(WiredChassis chassis, IReadOnlyList<string?> weapons)
    {
        var worn = chassis.Slots.Where(s => s.Fixed is null && weapons[(int)s.Mounting] is not null)
            .Select(s => $"{s.Mounting.ToString()[0]} {Studio.Title(weapons[(int)s.Mounting]!)}").ToList();
        return $"{chassis.Id}: {(worn.Count > 0 ? string.Join(", ", worn) : "bare")}";
    }

    /// <summary>The best of <paramref name="wanted"/> the slot takes, the smaller sizes when it is too small; the list turns with the chassis.</summary>
    private static string?[] Armed(WiredChassis chassis, int index, SlotSize wanted)
    {
        var weapons = new string?[3];
        foreach (var slot in chassis.Slots.Where(s => s.Fixed is null))
        {
            var size = wanted < slot.Size ? wanted : slot.Size;
            weapons[(int)slot.Mounting] = chassis.Glider && slot.Mounting == Mounting.Top
                ? size switch { SlotSize.Light => "WASP_LIGHT_STING", SlotSize.Medium => "WASP_METAL_STING", _ => "BEE_BOMB" }
                : size switch
                {
                    SlotSize.Light => Lights[index % Lights.Length],
                    SlotSize.Medium => Mediums[(index + (int)slot.Mounting) % Mediums.Length],
                    _ => Heavies[(index + (int)slot.Mounting) % Heavies.Length],
                };
        }
        return weapons;
    }

    private async Task Sheet()
    {
        var cells = new List<(string, Rig?)>();
        foreach (var (chassis, index) in WiredCatalogue.Chassis.Select((c, i) => (c, i)))
        {
            var most = chassis.Slots.Where(s => s.Fixed is null).Max(s => s.Size);
            cells.Add((Words(chassis, new string?[3]), Hero(chassis, null, null, null)));
            foreach (var size in Enum.GetValues<SlotSize>().Where(s => s <= most))
            {
                var weapons = Armed(chassis, index, size);
                cells.Add((Words(chassis, weapons), Hero(chassis, weapons)));
            }
        }
        await Grid(cells, Columns);
    }

    private async Task All(string id)
    {
        var chassis = WiredCatalogue.FindChassis(id) ?? throw new KeyNotFoundException(id);
        var cells = new List<(string, Rig?)>();
        foreach (var weapon in WiredCatalogue.Weapons)
        {
            var weapons = new string?[3];
            foreach (var slot in chassis.Slots.Where(s => WiredCatalogue.WhyNotFit(chassis, s.Mounting, weapon) is null))
                weapons[(int)slot.Mounting] = weapon.Id;
            if (weapons.Any(w => w is not null))
                cells.Add((Words(chassis, weapons), Hero(chassis, weapons)));
        }
        await Grid(cells, Math.Min(Columns, cells.Count));
    }

    private async Task One(string id, string[] weapons)
    {
        var chassis = WiredCatalogue.FindChassis(id) ?? throw new KeyNotFoundException(id);
        var worn = Enumerable.Range(0, 3).Select(i => i < weapons.Length && weapons[i] != "-" ? weapons[i] : null).ToArray();
        (await Photograph(Hero(chassis, worn), new Vector2I(1024, 1024))).SavePng(path);
    }

    /// <summary>
    /// The way the user asked about, as the hangar goes it: a chassis change keeps the weapons (<see cref="HeroLoadout.OnChassis"/>),
    /// the second slot stays empty until something is bought for it.
    /// </summary>
    private async Task Steps()
    {
        var ids = 0;
        WiredItem Item(WiredKind kind, string id) => new(++ids, kind, id);
        WiredSlot Slot(Mounting mounting) => WiredSlot.Weapon(mounting);

        var grunt = HeroLoadout.Bare(Item(WiredKind.Chassis, "grunt1")).With(Slot(Mounting.Right), Item(WiredKind.Weapon, "BOLTER"));
        var reaper = grunt.OnChassis(Item(WiredKind.Chassis, "reaper2"));
        var armed = reaper.With(Slot(Mounting.Left), Item(WiredKind.Weapon, "PULSE_CANNON"));
        var knight = HeroLoadout.Bare(Item(WiredKind.Chassis, "knight5"))
            .With(Slot(Mounting.Left), Item(WiredKind.Weapon, "PLASMA_CANNON2"))
            .With(Slot(Mounting.Right), Item(WiredKind.Weapon, "PLASMA_CANNON2"))
            .With(Slot(Mounting.Top), Item(WiredKind.Weapon, "MULTI_LAUNCHER4"));
        (HeroLoadout Loadout, string Title, string Worn)[] steps =
        [
            (grunt, "1. Grunt with a Bolter", "the start: one light slot"),
            (reaper, "2. Reaper chassis, still with the Grunt's Bolter", "the second slot stays empty"),
            (armed, "3. Reaper with a second, better gun", "a Pulse Cannon on the left, the Bolter stays"),
            (knight, "4. The goal: Knight 5, fully armed", "two Plasma Cannons, a Multi Launcher on top"),
        ];

        const int panel = 960, picture = 1040, gap = 24, top = 110, words = 150;
        var content = new List<Node> { Text($"From Grunt to Reaper and on · {GraphicsSettings.Models.ToString().Replace("Hd", "HD")} models", new Vector2(0, 16), new Vector2(steps.Length * (panel + gap), 80), 48) };
        for (var k = 0; k < steps.Length; k++)
        {
            var (loadout, title, worn) = steps[k];
            var x = gap / 2 + k * (panel + gap);
            content.Add(Pasted(await Photograph(HeroRig.Compose(_assets, _race, loadout), new Vector2I(panel, picture)), new Vector2(x, top)));
            content.Add(Text(title, new Vector2(x + 10, top + picture + 8), new Vector2(panel - 20, 90), 36));
            content.Add(Text(worn, new Vector2(x + 10, top + picture + 94), new Vector2(panel - 20, 44), 27));
        }
        await Save(new Vector2I(steps.Length * (panel + gap), top + picture + words), content);
    }
}
