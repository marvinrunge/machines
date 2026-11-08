using System.Text.RegularExpressions;
using Godot;
using Machines.Import.Model;
using Machines.Sim.Combat;

namespace Machines.Game;

/// <summary>
/// Every machine and building the game builds, one at a time on a big screen, to look the models over: in each look
/// (original, overhauled, HD) and each race's colours, turned and zoomed with the mouse, and moving as the game moves
/// them: driving and turning over a grid, the upper body or a tower's head turning, building, working, the doors, the
/// weapons' recoil, and the one-off actions such as the Gorilla's punches. The look picked here holds for this page
/// alone.
///
/// MACHINES_MODEL=&lt;id&gt;[,word...] picks a model by its line in classic/models.txt (the words after the first,
/// joined by '_') and sets it going, for screenshots: a look (original, overhauled, hd), a colour (red, blue, green,
/// yellow), a motion (forward, backward, left, right), work, build, doors, fire, or the name of an animation.
/// </summary>
public partial class ModelsPage(Action back) : MenuPage(back)
{
    /// <summary>A line of classic/models.txt: a model and what the game builds it for.</summary>
    private sealed record Entry(string Id, string TypeName, string Kind, int Level, string? Weapons, bool Building);

    /// <summary>A weapon and where its next round is in its burst.</summary>
    private sealed class Gun(Mounting mounting, WeaponSpec spec)
    {
        public Mounting Mounting { get; } = mounting;
        public WeaponSpec Spec { get; } = spec;
        public float Wait;
        public int Round;
    }

    private static readonly string[] Looks = ["Original", "Overhauled", "HD"];
    private static readonly Race[] Races = [Race.Red, Race.Blue, Race.Green, Race.Yellow];
    private static readonly string[] Motions = ["Stand", "Forward", "Backward", "Turn left", "Turn right"];

    /// <summary>The cycles the motions and building play; every other animation is an action, played once.</summary>
    private static readonly HashSet<string> Cycles = ["rest", "walk", "turnLeft", "turnRight", "construct"];

    // What was last looked at, while the game runs.
    private static string? _lastId;
    private static int _lastLook = -1, _lastRace;

    private readonly ClassicAssets?[] _assets = new ClassicAssets?[Looks.Length];
    private readonly List<Entry> _entries = [];
    private readonly List<TreeItem> _items = [];
    private readonly List<Gun> _guns = [];
    private Tree _tree = null!;
    private ModelScreen _screen = null!;
    private Label _title = null!, _facts = null!;
    private ChipChoice _look = null!, _race = null!, _motion = null!, _build = null!, _work = null!, _doors = null!, _fire = null!;
    private HSlider _turret = null!;
    private Control _motionRow = null!, _turretRow = null!, _buildRow = null!, _workRow = null!, _doorsRow = null!, _fireRow = null!, _actionsRow = null!;
    private HFlowContainer _actions = null!;
    private MachineLevel? _machine;
    private RigView? _view;

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading("Models"));

        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var list = new ChipPanel { CustomMinimumSize = new Vector2(420, 0), Padding = 14 };
        _tree = new Tree { HideRoot = true };
        _tree.ItemSelected += () =>
        {
            if (_tree.GetSelected()?.GetMetadata(0) is { VariantType: Variant.Type.Int } index)
                Select(_entries[index.AsInt32()]);
        };
        list.AddChild(_tree);
        columns.AddChild(list);

        var display = new ChipPanel { Padding = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _screen = new ModelScreen();
        display.AddChild(_screen);
        columns.AddChild(display);

        var controls = new ChipPanel { CustomMinimumSize = new Vector2(560, 0), Padding = 20 };
        // The switches a model has can outgrow the panel; then it scrolls.
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        var rows = Column(12);
        rows.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        scroll.AddChild(rows);
        controls.AddChild(Column(12, scroll, MenuTheme.Paragraph("Drag the model to turn it, the mouse wheel zooms.", 19, MenuTheme.Dim)));
        columns.AddChild(controls);
        _title = MenuTheme.Paragraph("", 30, MenuTheme.Bright);
        _facts = MenuTheme.Paragraph("", 20, MenuTheme.Dim);
        rows.AddChild(Column(4, _title, _facts));

        _look = Choice(Looks, _lastLook >= 0 ? _lastLook : (int)GraphicsSettings.Models, i =>
        {
            _lastLook = i;
            Reselect();
        });
        rows.AddChild(Setting("Look", _look, 150));
        _race = Choice(Races.Select(r => r.ToString()), _lastRace, i =>
        {
            _lastRace = i;
            Reselect();
        });
        rows.AddChild(Setting("Colour", _race, 150));
        _motion = Choice(Motions, 0, _ => Move());
        rows.AddChild(_motionRow = Setting("Motion", _motion, 150));
        _turret = new HSlider { MinValue = -180, MaxValue = 180, Step = 5, CustomMinimumSize = new Vector2(0, 40) };
        _turret.ValueChanged += degrees => _screen.Aim = Mathf.DegToRad((float)degrees);
        rows.AddChild(_turretRow = Setting("Upper body", _turret, 150));
        _build = Choice(["Off", "On"], 0, _ => Switch());
        rows.AddChild(_buildRow = Setting("Building", _build, 150));
        _work = Choice(["Off", "On"], 0, _ => Switch());
        rows.AddChild(_workRow = Setting("Working", _work, 150));
        _doors = Choice(["Closed", "Open"], 0, _ => Switch());
        rows.AddChild(_doorsRow = Setting("Doors", _doors, 150));
        _fire = Choice(["Off"], 0, _ => ReloadGuns());
        rows.AddChild(_fireRow = Setting("Fire", _fire, 150));
        _actions = new HFlowContainer();
        _actions.AddThemeConstantOverride("h_separation", 12);
        _actions.AddThemeConstantOverride("v_separation", 12);
        rows.AddChild(_actionsRow = Column(10, MenuTheme.Label("Play once", 22, MenuTheme.Text), _actions));

        var footer = Line(Gap);
        var backButton = Button("Back", Back);
        backButton.CustomMinimumSize = new Vector2(420, 76);
        footer.AddChild(backButton);
        footer.AddChild(Spring());
        var previous = Button("Previous", () => Step(-1));
        previous.CustomMinimumSize = new Vector2(270, 76);
        footer.AddChild(previous);
        var next = Button("Next", () => Step(1));
        next.CustomMinimumSize = new Vector2(270, 76);
        footer.AddChild(next);
        page.AddChild(footer);

        var asked = (System.Environment.GetEnvironmentVariable("MACHINES_MODEL") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var word in asked.Skip(1).Select(w => w.ToLowerInvariant()))
        {
            if (Array.FindIndex(Looks, l => l.Equals(word, StringComparison.OrdinalIgnoreCase)) is >= 0 and var look)
                _look.Selected = _lastLook = look;
            if (Array.FindIndex(Races, r => r.ToString().Equals(word, StringComparison.OrdinalIgnoreCase)) is >= 0 and var race)
                _race.Selected = _lastRace = race;
        }
        FillTree(asked.Length > 0 ? asked[0] : _lastId);
        foreach (var word in asked.Skip(1))
            Ask(word);
        SwitchOn();
        Focus(_tree);
    }

    private static ChipChoice Choice(IEnumerable<string> items, int selected, Action<int> changed)
    {
        var choice = new ChipChoice(items, selected);
        choice.ItemSelected += changed;
        return choice;
    }

    public override void _Process(double delta)
    {
        if (_view is null || _fire.Selected == 0)
            return;
        var side = _fire.Text;
        foreach (var gun in _guns.Where(g => side == "All" || Side(g.Mounting) == side))
        {
            gun.Wait -= (float)delta;
            if (gun.Wait > 0)
                continue;
            _view.Fire(gun.Mounting, gun.Spec);
            // The rounds of a burst follow each other over its duration; then the weapon reloads (shortened here).
            var rounds = Math.Max(1, gun.Spec.BurstRounds);
            gun.Round = (gun.Round + 1) % rounds;
            gun.Wait = gun.Round > 0 ? gun.Spec.BurstDuration / rounds : Math.Clamp(gun.Spec.ReloadTime, 0.4f, 3f);
        }
    }

    // ---- the models -----------------------------------------------------------------------------

    /// <summary>
    /// The machines and buildings exported (classic/models.txt): MACHINE type level [subtype] [weapons],
    /// CONSTRUCTION type level [subtype] [weapons]. Factories and labs are military or civilian, emplacements turrets,
    /// sentries, launchers or ICBMs.
    /// </summary>
    private static IEnumerable<Entry> ReadEntries()
    {
        var path = System.IO.Path.Combine(Paths.Classic, "models.txt");
        if (!File.Exists(path))
            yield break;
        foreach (var line in File.ReadLines(path))
        {
            var words = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (words.Length < 3 || words[0] is not ("MACHINE" or "CONSTRUCTION") || !int.TryParse(words[2], out var level))
                continue;
            var building = words[0] == "CONSTRUCTION";
            var withSub = words.Length > 3 && (building
                ? words[1] is "FACTORY" or "HARDWARE_LAB" or "MISSILE_EMPLACEMENT"
                : words[1] is "AGGRESSOR" or "ADMINISTRATOR" or "CONSTRUCTOR" or "TECHNICIAN");
            var kind = !withSub ? words[1] : building && words[1] != "MISSILE_EMPLACEMENT" ? $"{words[1]}_{words[3]}" : words[3];
            var weapons = words.Length > (withSub ? 4 : 3) ? words[^1] : null;
            yield return new Entry(string.Join('_', words.Skip(1)), withSub ? $"{words[1]} {words[3]}" : words[1], kind, level, weapons, building);
        }
    }

    private void FillTree(string? wanted)
    {
        _entries.AddRange(ReadEntries());
        var root = _tree.CreateItem();
        if (_entries.Count == 0)
        {
            Branch(root, "Import the original models first (Options).", MenuTheme.Dim);
            _title.Text = "No models";
            Show(null);
            return;
        }
        TreeItem? select = null;
        foreach (var (heading, buildings) in new[] { ("MACHINES", false), ("BUILDINGS", true) })
        {
            var section = Branch(root, heading, MenuTheme.Bright);
            foreach (var kind in _entries.Where(e => e.Building == buildings).GroupBy(e => e.Kind))
            {
                var kindItem = Branch(section, KindName(kind.Key), MenuTheme.Text);
                kindItem.Collapsed = true;
                foreach (var entry in kind)
                {
                    var item = _tree.CreateItem(kindItem);
                    item.SetText(0, entry.Weapons is null ? Lang.T("Level {0}", entry.Level) : $"{entry.Level} · {Weapons(entry.Weapons)}");
                    item.SetMetadata(0, _entries.IndexOf(entry));
                    item.SetTooltipText(0, entry.Id);
                    _items.Add(item);
                    if (entry.Id.Equals(wanted, StringComparison.OrdinalIgnoreCase))
                        select = item;
                }
            }
        }
        Pick(select ?? _items[0]);
    }

    private TreeItem Branch(TreeItem parent, string text, Color colour)
    {
        var item = _tree.CreateItem(parent);
        item.SetText(0, text);
        item.SetSelectable(0, false);
        item.SetCustomColor(0, colour);
        return item;
    }

    /// <summary>Selects a model in the list, opening its branch; selecting it shows it.</summary>
    private void Pick(TreeItem item)
    {
        for (var parent = item.GetParent(); parent is not null; parent = parent.GetParent())
            parent.Collapsed = false;
        item.Select(0);
        _tree.CallDeferred(Tree.MethodName.ScrollToItem, item, false);
    }

    /// <summary>The model before or after the one shown, across every branch.</summary>
    private void Step(int by)
    {
        if (_items.Count == 0)
            return;
        var at = _tree.GetSelected() is { } selected ? _items.IndexOf(selected) : -1;
        Pick(_items[((at + by) % _items.Count + _items.Count) % _items.Count]);
    }

    private static string KindName(string kind) => Studio.Names.TryGetValue(kind, out var name) ? name : Studio.Title(kind);

    /// <summary>A weapon combo for people: LR_HEAVY_BOLTER1_X2 becomes "Heavy bolter 1 ×2".</summary>
    private static string Weapons(string combo)
    {
        try
        {
            return string.Join(", ", WeaponCombos.Split(combo).GroupBy(w => w.Weapon)
                .Select(g => g.Count() > 1 ? $"{Studio.Title(g.Key)} ×{g.Count()}" : Studio.Title(g.Key)));
        }
        catch (FormatException)
        {
            return combo;
        }
    }

    private static string Side(Mounting mounting) => mounting switch
    {
        Mounting.Left => "Left",
        Mounting.Right => "Right",
        _ => "Top",
    };

    private Entry? Shown => _tree.GetSelected()?.GetMetadata(0) is { VariantType: Variant.Type.Int } index ? _entries[index.AsInt32()] : null;

    /// <summary>The model shown again, in the look and colour now chosen.</summary>
    private void Reselect()
    {
        if (Shown is { } entry)
            Select(entry);
    }

    private void Select(Entry entry)
    {
        _lastId = entry.Id;
        var look = (ModelLook)_look.Selected;
        var assets = _assets[(int)look] ??= new ClassicAssets(Paths.Root, look);
        var race = Races[_race.Selected];
        var view = assets.RigFor(entry.Id, race) is { } rig ? RigView.Create(rig) : null;

        // How the game moves it: a machine's speeds, a building's doors and a tower's turning head.
        _machine = null;
        ConstructionLevel? building = null;
        try
        {
            if (entry.Building)
                building = Paths.Data.Parm.Constructions.FirstOrDefault(c => c.TypeName == entry.TypeName)?.Levels
                    .FirstOrDefault(l => l.HardwareLevel == entry.Level);
            else
                _machine = Paths.Data.Parm.Machines.FirstOrDefault(m => m.TypeName == entry.TypeName)?.Levels
                    .FirstOrDefault(l => l.HardwareLevel == entry.Level);
        }
        catch (Exception error) when (error is IOException or FormatException or KeyNotFoundException)
        {
            GD.PrintErr($"models: no game data for {entry.TypeName}: {error.Message}");
        }
        if (view is not null)
        {
            if (_machine is { BodyTurnRate: > 0 and var bodyRate })
                view.TurnRate = Mathf.DegToRad(bodyRate);
            if (building is not null)
            {
                view.AddDoors(building.Geometry);
                if (building.MaxRotation is > 0 and var rate)
                    view.TurnRate = Mathf.DegToRad(rate);
            }
        }
        Show(view);
        Switch();

        var animations = view?.Rig.Animations ?? [];
        _motionRow.Visible = _machine is not null;
        _turretRow.Visible = view?.Rig.UpperBody > 0;
        _buildRow.Visible = animations.ContainsKey("construct");
        _workRow.Visible = view?.Rig.Working.Length > 0 || animations.ContainsKey("research");
        _doorsRow.Visible = view?.HasDoors == true;

        // The weapons on mounts the model has, to fire one side or all.
        _guns.Clear();
        if (view is not null && entry.Weapons is { } combo)
        {
            try
            {
                foreach (var (mounting, weapon) in WeaponCombos.Split(combo))
                    if (view.Rig.Weapons.Any(w => w.Mounting == Side(mounting).ToLowerInvariant())
                        && Paths.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == weapon) is { } spec)
                        _guns.Add(new Gun(mounting, spec));
            }
            catch (FormatException)
            {
            }
        }
        var sides = _guns.Select(g => Side(g.Mounting)).Distinct().ToList();
        var firing = _fire.Text;
        var fireItems = new[] { "Off" }.Concat(sides).Concat(sides.Count > 1 ? ["All"] : []).ToList();
        _fire.SetItems(fireItems, Math.Max(0, fireItems.IndexOf(firing)));
        _fireRow.Visible = _guns.Count > 0;
        ReloadGuns();

        foreach (var button in _actions.GetChildren())
            button.QueueFree();
        var actions = animations.Keys.Where(a => !Cycles.Contains(a)).ToList();
        foreach (var name in actions)
            _actions.AddChild(Button(Regex.Replace(name, "(?<=[a-z])(?=[A-Z])", " "), () => _view?.Play(name), 50, 19));
        _actionsRow.Visible = actions.Count > 0;

        Describe(entry, assets, race, view);
    }

    private void Show(RigView? view)
    {
        _view = view;
        _screen.Present(view);
        Move();
    }

    /// <summary>Drives or turns the machine as the motion chosen, at its own top speed and turning rate.</summary>
    private void Move()
    {
        var speed = _machine?.Speed ?? 0;
        var turn = Mathf.DegToRad(_machine?.RotationSpeed ?? 0);
        _screen.MaxSpeed = speed;
        _screen.Speed = _motion.Selected switch { 1 => speed, 2 => -speed, _ => 0 };
        _screen.TurnRate = _motion.Selected switch { 3 => turn, 4 => -turn, _ => 0 };
    }

    /// <summary>A constructor builds, a building or technician works and a building opens its doors as chosen.</summary>
    private void Switch()
    {
        if (_view is null)
            return;
        _view.Constructing = _build.Selected == 1;
        _view.Working = _work.Selected == 1;
        _view.DoorsOpen = _doors.Selected == 1;
    }

    /// <summary>Each weapon starts its burst again, the sides firing together.</summary>
    private void ReloadGuns()
    {
        foreach (var gun in _guns)
            (gun.Wait, gun.Round) = (0, 0);
    }

    private void Describe(Entry entry, ClassicAssets assets, Race race, RigView? view)
    {
        _title.Text = Lang.T("{0}, level {1}", KindName(entry.Kind), entry.Level);
        var file = assets.ModelFile(entry.Id, race)?.Replace('\\', '/') ?? "";
        var made = Lang.T(file.Contains("/built/") ? "the Blender remake"
            : file.Contains("/meshy/") ? "the Meshy remake"
            : file.Contains("/remake/") ? "the original, upgraded"
            : "the original");
        var look = (ModelLook)_look.Selected;
        var shows = look switch
        {
            ModelLook.Hd when !file.Contains("/meshy/") && !file.Contains("/built/") => Lang.T("HD: no remake yet, shows {0}", made),
            ModelLook.Overhauled when !file.Contains("/remake/") => Lang.T("Overhauled: not upgraded, shows {0}", made),
            _ => $"{Lang.T(Looks[(int)look])}: {made}",
        };
        var lines = new List<string>();
        if (entry.Weapons is { } weapons)
            lines.Add(Weapons(weapons));
        lines.Add(view is null ? Lang.T("Not exported") : Lang.T("{0}, {1} triangles", shows, Triangles(view).ToString("N0", System.Globalization.CultureInfo.InvariantCulture)));
        if (_machine is not null)
            lines.Add(Lang.T("Top speed {0:0.#} m/s, turns {1:0} °/s", _machine.Speed, _machine.RotationSpeed));
        _facts.Text = string.Join('\n', lines);
    }

    private static int Triangles(Node view) => view.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>()
        .Select(m => m.Mesh).OfType<ArrayMesh>()
        .Sum(mesh => Enumerable.Range(0, mesh.GetSurfaceCount())
            .Sum(s => (mesh.SurfaceGetArrayIndexLen(s) is > 0 and var indices ? indices : mesh.SurfaceGetArrayLen(s)) / 3));

    /// <summary>A word of MACHINES_MODEL: a motion, a switch, firing or an animation (looks and colours are taken first).</summary>
    private void Ask(string word)
    {
        switch (word.ToLowerInvariant())
        {
            case "forward": _motion.Selected = 1; break;
            case "backward": _motion.Selected = 2; break;
            case "left": _motion.Selected = 3; break;
            case "right": _motion.Selected = 4; break;
            case "build": _build.Selected = 1; break;
            case "work": _work.Selected = 1; break;
            case "doors": _doors.Selected = 1; break;
            case "fire": _fire.Selected = _fire.Items.Count - 1; break;
            default:
                // A number turns the upper body that many degrees; anything else plays as an animation.
                if (float.TryParse(word, System.Globalization.CultureInfo.InvariantCulture, out var degrees))
                    _turret.Value = degrees;
                else
                    _view?.Play(word);
                break;
        }
        Move();
        Switch();
    }
}

/// <summary>
/// The model viewer's screen: the model on a grid that moves under it as it drives and turns, so its legs, wheels and
/// tracks can be seen to keep pace with the ground; dragging turns it and the mouse wheel zooms.
/// </summary>
public partial class ModelScreen : MachineScreen
{
    private static readonly Lazy<Shader> GridShader = new(() => new Shader
    {
        Code = """
            shader_type spatial;
            render_mode blend_mix, cull_disabled;

            uniform float cell = 1.0;
            uniform float reach = 10.0;
            varying vec2 grid;
            varying vec3 place;

            void vertex() {
                grid = VERTEX.xz;
                place = (MODEL_MATRIX * vec4(VERTEX, 1.0)).xyz;
            }

            void fragment() {
                vec2 at = grid / cell;
                vec2 lines = abs(fract(at - 0.5) - 0.5) / max(fwidth(at), vec2(1e-4));
                float line = 1.0 - min(min(lines.x, lines.y), 1.0);
                ALBEDO = mix(vec3(0.02, 0.07, 0.03), vec3(0.12, 0.36, 0.14), line);
                EMISSION = vec3(0.04, 0.16, 0.06) * line;
                ROUGHNESS = 1.0;
                ALPHA = (1.0 - smoothstep(reach * 0.5, reach, length(place.xz))) * mix(0.6, 0.95, line);
            }
            """,
    });

    private RigView? _view;
    private MeshInstance3D? _ground;
    private float _cell = 1, _groundAngle;
    private Vector3 _groundShift;
    private bool _dragging;

    /// <summary>Metres per second forwards, negative backwards.</summary>
    public float Speed { get; set; }

    /// <summary>Radians per second, positive turning left.</summary>
    public float TurnRate { get; set; }

    /// <summary>The machine's top speed, which its legs keep pace with.</summary>
    public float MaxSpeed { get; set; }

    /// <summary>Where the upper body turns to, radians anticlockwise from the hull.</summary>
    public float Aim { get; set; }

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        Yaw = Mathf.DegToRad(-20);
        base._Ready();
    }

    /// <summary>Shows <paramref name="view"/> on the grid, or nothing.</summary>
    public void Present(RigView? view)
    {
        _view = view;
        Place(view);
        _ground = null;
        (_groundShift, _groundAngle) = (Vector3.Zero, 0);
        if (Turntable is null)
            return;
        // Lines a metre or a few apart, as the model is big, fading out a little beyond it.
        var reach = Radius * 2.2f;
        _cell = Mathf.Max(1, Mathf.Round(Radius / 2.5f));
        var material = new ShaderMaterial { Shader = GridShader.Value };
        material.SetShaderParameter("cell", _cell);
        material.SetShaderParameter("reach", reach);
        _ground = new MeshInstance3D
        {
            Mesh = new PlaneMesh { Size = Vector2.One * reach * 3, Material = material },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        Turntable.AddChild(_ground);
    }

    public override void _Process(double delta)
    {
        var step = (float)delta;
        _view?.Animate(step, Speed, TurnRate, MaxSpeed, Aim);
        if (_ground is not null)
        {
            // The machine stays and the ground moves: back under it as it drives, round it as it turns.
            _groundAngle -= TurnRate * step;
            var basis = new Basis(Vector3.Up, _groundAngle);
            _groundShift += basis.Inverse() * new Vector3(-Speed * step, 0, 0);
            _groundShift = new Vector3(Mathf.PosMod(_groundShift.X, _cell), 0, Mathf.PosMod(_groundShift.Z, _cell));
            _ground.Transform = new Transform3D(basis, basis * _groundShift);
        }
        base._Process(delta);
    }

    public override void _GuiInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelUp, Pressed: true }:
                Zoom = Mathf.Min(Zoom * 1.1f, 6);
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.WheelDown, Pressed: true }:
                Zoom = Mathf.Max(Zoom / 1.1f, 0.5f);
                break;
            case InputEventMouseMotion motion when _dragging:
                Yaw += motion.Relative.X * 0.01f;
                Elevation = Mathf.Clamp(Elevation + motion.Relative.Y * 0.006f, 0.02f, 1.45f);
                break;
            default:
                return;
        }
        AcceptEvent();
    }
}
