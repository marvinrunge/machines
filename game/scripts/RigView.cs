using Godot;
using Machines.Import.Model;
using Machines.Import.Parm;
using Machines.Sim.Combat;
using Machines.Sim.Presentation;

namespace Machines.Game;

/// <summary>
/// Draws a <see cref="Rig"/> and moves its parts as the original did: spider legs play their walking, turning and
/// resting cycles (MachPhysSpiderLegs), wheels roll by the distance travelled over their radius (MachPhysWheels),
/// track treads scroll their texture (MachPhysITrack), and the upper body turns towards its target
/// (MachPhysMachine::upperBodyTurnTo). One-off actions, such as the Gorilla's punches, play over the cycles.
/// </summary>
public partial class RigView : Node3D
{
    /// <summary>
    /// The upper body's turning speed, radians per second: a machine's BODY_TURN_RATE, a tower's head its
    /// MAX_ROTATION.
    /// </summary>
    public float TurnRate { get; set; } = 2.5f;

    /// <summary>How far the upper body has turned from the hull, radians anticlockwise in the original's axes.</summary>
    public float UpperBody
    {
        get => _rig.UpperBody > 0 ? _upperBody : 0;
        set => _upperBody = value;
    }

    /// <summary>What it draws: its parts, cycles, actions and weapons.</summary>
    public Rig Rig => _rig;

    private Rig _rig = null!;
    private Node3D[] _nodes = [];
    private string _cycle = "rest";
    private float _phase;
    private float _leftWheel, _rightWheel;
    private float _upperBody;
    private float _idleTime = (float)GD.RandRange(0, 10);
    private float _workTime;
    private RigAnimation? _action;
    private float _actionTime;
    private readonly List<StandardMaterial3D> _leftTread = [], _rightTread = [];

    public static RigView Create(Rig rig)
    {
        var view = new RigView { _rig = rig, Name = "Rig" };
        view._nodes = rig.Nodes.Select((n, i) =>
        {
            var node = new Node3D { Name = $"Part{i}", Transform = n.Rest };
            if (n.Mesh is not null)
                node.AddChild(new MeshInstance3D { Mesh = n.Mesh, Name = "Mesh" });
            return node;
        }).ToArray();
        for (var i = 0; i < view._nodes.Length; i++)
        {
            var parent = rig.Nodes[i].Parent;
            (parent >= 0 && parent < view._nodes.Length && parent != i ? view._nodes[parent] : view).AddChild(view._nodes[i]);
        }
        if (rig.Tracks is { } tracks)
        {
            view.OwnMaterials(tracks.Left, view._leftTread);
            view.OwnMaterials(tracks.Right, view._rightTread);
        }
        // A building's interior is hidden until first person looks out from inside it (see ShowInterior).
        view._interior = Array.FindIndex(rig.Nodes, n => n.Name == "interior");
        if (view._interior > 0)
            view._nodes[view._interior].Visible = false;
        view.OwnGlow();
        view.OwnScreens();
        view.IonTop = false;
        return view;
    }

    /// <summary>A pod with the ion cannon wears its other top (MachPhysPod::ionCannonResearched).</summary>
    public bool IonTop
    {
        set
        {
            if (Find("ion_top") is var ion and > 0 && Find("top") is var top and > 0 && _nodes[ion].GetNodeOrNull("Mesh") is not null)
            {
                _nodes[ion].Visible = value;
                _nodes[top].Visible = !value;
            }
        }
    }

    /// <summary>A geo locator searching plays its locating cycle over the walk.</summary>
    public bool Locating { get; set; }

    private float _locateTime;

    private int _interior = -1;

    /// <summary>The building has an interior (MachPhysConstruction::pInteriorDomain).</summary>
    public bool HasInterior => _interior > 0;

    /// <summary>
    /// Shows the interior. The original drew a building's interior domain only while the camera was in it, and the
    /// outside only while the camera was not, so from inside the shell only keeps the sun out.
    /// </summary>
    public bool ShowInterior
    {
        set
        {
            if (!HasInterior || _nodes[_interior].Visible == value)
                return;
            _nodes[_interior].Visible = value;
            for (var i = 0; i < _nodes.Length; i++)
                if (i != _interior && _nodes[i].GetNodeOrNull<MeshInstance3D>("Mesh") is { } mesh)
                    mesh.CastShadow = value ? GeometryInstance3D.ShadowCastingSetting.ShadowsOnly : GeometryInstance3D.ShadowCastingSetting.On;
            LightInterior(value);
        }
    }

    private OmniLight3D? _interiorLight;

    // The interior's lamps from its model.
    private readonly List<OmniLight3D> _lamps = [];

    /// <summary>With the sun kept out the interior has the lamps its model names (W4dLightData), or one of its own under the roof in its middle.</summary>
    private void LightInterior(bool on)
    {
        if (_rig.Lights.Length > 0)
        {
            if (on && _lamps.Count == 0)
                foreach (var light in _rig.Lights)
                {
                    // The original's colours go up to 3, for vertex lighting that stops at white.
                    var strength = Mathf.Max(light.Colour.R, Mathf.Max(light.Colour.G, light.Colour.B));
                    var lamp = new OmniLight3D
                    {
                        Position = light.Position,
                        LightColor = light.Colour / strength,
                        OmniRange = light.Range,
                        LightEnergy = Mathf.Sqrt(strength) * (GraphicsSettings.Lighting ? 1 : 0.3f),
                    };
                    _nodes[_interior].AddChild(lamp);
                    _lamps.Add(lamp);
                }
            foreach (var lamp in _lamps)
                lamp.Visible = on;
            return;
        }
        if (_interiorLight is null && _nodes[_interior].GetNodeOrNull<MeshInstance3D>("Mesh") is { } mesh)
        {
            var bounds = mesh.GetAabb();
            _interiorLight = new OmniLight3D
            {
                Position = bounds.GetCenter() + Vector3.Up * bounds.Size.Y * 0.3f,
                OmniRange = bounds.Size.Length() * 0.75f,
                OmniAttenuation = 0.6f,
                LightEnergy = GraphicsSettings.Lighting ? 2.5f : 1.2f,
                LightColor = new Color(1, 0.95f, 0.88f),
            };
            _nodes[_interior].AddChild(_interiorLight);
        }
        if (_interiorLight is not null)
            _interiorLight.Visible = on;
    }

    /// <summary>Whether a point in the world lies within the interior's bounds.</summary>
    public bool InInterior(Vector3 point) =>
        HasInterior && _nodes[_interior].GetNodeOrNull<MeshInstance3D>("Mesh") is { } mesh
        && (mesh.GlobalTransform * mesh.GetAabb()).HasPoint(point);

    /// <summary>A weapon's part moving through one round: sliding back and forward again, or turning on.</summary>
    private sealed class Firing
    {
        public required int Node { get; init; }
        public float Time;
        public float Distance, Back, Fore;
        public float Angle, Target, TurnRate;
    }

    private readonly Dictionary<int, Firing> _firing = [];

    /// <summary>
    /// The weapon at <paramref name="mounting"/> fires one round and moves with it: back and forward by its RECOIL
    /// distance and times, fitted into the round's share of the burst as MachPhysWeapon::recoil fitted them, or
    /// the autocannon's barrels turn on.
    /// </summary>
    public void Fire(Mounting mounting, WeaponSpec weapon)
    {
        var recoil = weapon.Presentation.FirstOrDefault(l => l.Keyword == "RECOIL" && l.Args.Count >= 3)?.Args
            .Select(a => float.Parse(a, System.Globalization.CultureInfo.InvariantCulture)).ToArray() ?? [0f, 0.05f, 0.15f];
        var (distance, back, fore) = (recoil[0], recoil[1], recoil[2]);
        var round = weapon.BurstDuration / Math.Max(1, weapon.BurstRounds);
        // Rounds faster than the recoil cut its way forward short.
        if (round - (back + fore) < 0.01f)
            fore = Math.Max(round - back, 0.01f);
        var side = mounting switch
        {
            Mounting.Left => "left",
            Mounting.Right => "right",
            _ => "top",
        };
        Fire(side, distance, back, fore, round, turn: weapon.TypeName == "AUTO_CANNON");
    }

    /// <summary>
    /// A round leaves the weapon mounted at <paramref name="mounting"/> (left, right or top). Its recoiling part
    /// slides <paramref name="distance"/> metres back along the barrel in <paramref name="back"/> seconds and
    /// forward in <paramref name="fore"/> (MachPhysWeapon::recoil); with <paramref name="turn"/> the autocannon's
    /// six barrels turn on by one instead, 60 degrees over the round's <paramref name="round"/> seconds
    /// (MachPhysBolter::fire).
    /// </summary>
    public void Fire(string mounting, float distance, float back, float fore, float round, bool turn)
    {
        foreach (var weapon in _rig.Weapons)
        {
            if (weapon.Mounting != mounting || weapon.Node <= 0 || weapon.Node >= _nodes.Length)
                continue;
            if (!_firing.TryGetValue(weapon.Node, out var firing))
                _firing[weapon.Node] = firing = new Firing { Node = weapon.Node };
            if (turn)
            {
                firing.Target += Mathf.Pi / 3;
                firing.TurnRate = Mathf.Pi / 3 / Mathf.Max(round, 0.01f);
            }
            else if (distance > 0)
            {
                (firing.Time, firing.Distance, firing.Back, firing.Fore) = (0, distance, Mathf.Max(back, 0.001f), Mathf.Max(fore, 0.001f));
            }
        }
    }

    private void MoveFiring(float delta)
    {
        foreach (var firing in _firing.Values)
        {
            firing.Time += delta;
            // Back fast, forward slower, then still until the next round (PhysLinearMotionPlan).
            var t = firing.Time;
            var back = t < firing.Back ? t / firing.Back
                : t < firing.Back + firing.Fore ? 1 - (t - firing.Back) / firing.Fore
                : 0;
            firing.Angle = Mathf.MoveToward(firing.Angle, firing.Target, firing.TurnRate * delta);
            if (firing.Angle >= Mathf.Tau)
                (firing.Angle, firing.Target) = (firing.Angle - Mathf.Tau, firing.Target - Mathf.Tau);
            // The barrel points along the part's x axis, which the axis swap leaves as Godot's x.
            _nodes[firing.Node].Transform = _rig.Nodes[firing.Node].Rest
                * new Transform3D(new Basis(Vector3.Right, -firing.Angle), new Vector3(-firing.Distance * back, 0, 0));
        }
    }

    private readonly List<(StandardMaterial3D Material, Color Colour)> _glow = [];
    private bool _glowing;

    /// <summary>Gives the chimneys' hot surfaces (<see cref="ChimneyTable.GlowTexture"/>) their own materials, to pulse.</summary>
    private void OwnGlow()
    {
        foreach (var node in _nodes)
        {
            if (node.GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: { } mesh } instance)
                continue;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is StandardMaterial3D shared
                    && shared.GetMeta("texture", "").AsString() == ChimneyTable.GlowTexture)
                {
                    var own = (StandardMaterial3D)shared.Duplicate();
                    instance.SetSurfaceOverrideMaterial(s, own);
                    _glow.Add((own, own.AlbedoColor));
                }
            }
        }
    }

    // A lab's screens and its neon strips, with when each strip starts.
    private readonly List<StandardMaterial3D> _screens = [];
    private readonly List<(StandardMaterial3D Material, float Start)> _neons = [];
    private bool _screensOn;
    private const string ScreenTexture = "scrn1.bmp";
    private const float ScreenScroll = 0.1f, NeonPeriod = 0.2f, NeonStagger = 0.25f;

    private void OwnScreens()
    {
        foreach (var node in _nodes)
        {
            if (node.GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: { } mesh } instance)
                continue;
            var name = _rig.Nodes[Array.IndexOf(_nodes, node)].Name;
            var neon = name.StartsWith("neon", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[4..], out var n) ? n : 0;
            for (var s = 0; s < mesh.GetSurfaceCount(); s++)
            {
                if (mesh.SurfaceGetMaterial(s) is not StandardMaterial3D shared)
                    continue;
                var screen = shared.GetMeta("texture", "").AsString().Equals(ScreenTexture, StringComparison.OrdinalIgnoreCase);
                if (!screen && neon == 0)
                    continue;
                var own = (StandardMaterial3D)shared.Duplicate();
                instance.SetSurfaceOverrideMaterial(s, own);
                if (screen)
                {
                    _screens.Add(own);
                    // Under the remake's lights a screen would be dull metal: it shines by itself.
                    if (GraphicsSettings.Lighting)
                        own.ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded;
                }
                else
                    _neons.Add((own, (neon - 1) * NeonStagger));
            }
        }
    }

    /// <summary>What a lab's screens show at rest (STATIC.BMP in MachPhysHardwareLab::doWorking).</summary>
    public Texture2D? RestingScreen
    {
        set
        {
            _restingScreen = value;
            _workingScreen = _screens.FirstOrDefault()?.AlbedoTexture;
            foreach (var screen in _screens)
                screen.AlbedoTexture = value;
        }
    }

    private Texture2D? _restingScreen, _workingScreen;

    /// <summary>
    /// While a lab works its screens run (MachPhysHardwareLab::doWorking: 0.1 of the picture a second) and its neon
    /// strips pulse from black to white, each a quarter of a second after the last; at rest they stand still.
    /// </summary>
    private void ShowScreens(bool working, float time)
    {
        if (_screens.Count + _neons.Count == 0 || !working && !_screensOn)
            return;
        _screensOn = working;
        foreach (var screen in _screens)
        {
            screen.Uv1Offset = working ? new Vector3(time * ScreenScroll % 1, 0, 0) : Vector3.Zero;
            if (_restingScreen is not null)
                screen.AlbedoTexture = working ? _workingScreen : _restingScreen;
        }
        foreach (var (neon, start) in _neons)
        {
            neon.EmissionEnabled = working;
            neon.Emission = Colors.White;
            neon.EmissionEnergyMultiplier = time < start ? 0 : 0.5f - 0.5f * Mathf.Cos(Mathf.Tau * (time - start) / NeonPeriod);
        }
    }

    /// <summary>
    /// While a factory works its chimneys glow, pulsing from half to full brightness (W4dColourPulsePlan); the
    /// remake lets them shine too. When it stops they go back to their colour.
    /// </summary>
    private void Glow(bool working, float time)
    {
        if (_glow.Count == 0 || (!working && !_glowing))
            return;
        _glowing = working;
        var pulse = working
            ? ChimneyTable.GlowLow + (1 - ChimneyTable.GlowLow) * (0.5f - 0.5f * Mathf.Cos(Mathf.Tau * time / ChimneyTable.GlowPeriod))
            : 1;
        foreach (var (material, colour) in _glow)
        {
            material.AlbedoColor = new Color(colour.R * pulse, colour.G * pulse, colour.B * pulse, colour.A);
            material.EmissionEnabled = working && GraphicsSettings.Lighting;
            if (material.EmissionEnabled)
            {
                material.EmissionTexture = material.AlbedoTexture;
                material.EmissionOperator = BaseMaterial3D.EmissionOperatorEnum.Multiply;
                material.Emission = Colors.White;
                material.EmissionEnergyMultiplier = 1.5f * pulse;
            }
        }
    }

    /// <summary>
    /// Where first person looks out from: the centre front of the faceplate as it stands now (the original's
    /// camera attachment), or null when the export has none.
    /// </summary>
    /// <summary>A building's working motion plays (MachPhysConstruction::isWorking), a technician's research cycle.</summary>
    public bool Working { get; set; }

    /// <summary>A constructor building or repairing: its arms play the building cycle.</summary>
    public bool Constructing { get; set; }

    /// <summary>The building's doors open (they slide at the original's 1 m/s, MachPhysDoor) or close.</summary>
    public bool DoorsOpen { get; set; }

    private readonly List<(int Node, Vector3 Open)> _doors = [];
    private float _doorOpening;

    /// <summary>
    /// The doors of a building's ENTRANCE lines (MachPhysConstructionData::addEntrance): across the way through,
    /// at the door point, 6.9 m wide for a small door and 11.9 m for a large one.
    /// </summary>
    public void AddDoors(IEnumerable<ParmLine> geometry)
    {
        foreach (var entrance in geometry.Where(g => g.Keyword == "ENTRANCE" && g.Args.Count >= 8))
        {
            float F(int i) => float.Parse(entrance.Args[i], System.Globalization.CultureInfo.InvariantCulture);
            var through = new Vector2(F(2) - F(0), F(3) - F(1));
            if (through.LengthSquared() < 0.0001f)
                continue;
            var width = entrance.Args[7] == "LARGE_DOOR" ? 11.9f : 6.9f;
            AddDoors(entrance.Args[6], new Vector2(through.Y, -through.X).Normalized() * width);
        }
    }

    /// <summary>Whether the building has doors that open (see <see cref="AddDoors(IEnumerable{ParmLine})"/>).</summary>
    public bool HasDoors => _doors.Count > 0;

    /// <summary>
    /// The doors of a building's entrance (MachPhysConstruction::setupEntrances): the part called
    /// <paramref name="name"/>, or the halves l<paramref name="name"/> and r<paramref name="name"/>, which slide
    /// apart along the door line; <paramref name="along"/> is the door's full width along it (original axes).
    /// </summary>
    public void AddDoors(string name, Vector2 along)
    {
        var left = Array.FindIndex(_rig.Nodes, n => n.Name.Equals("l" + name, StringComparison.OrdinalIgnoreCase)
                                                    || n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        var right = Array.FindIndex(_rig.Nodes, n => n.Name.Equals("r" + name, StringComparison.OrdinalIgnoreCase));
        var halves = (left >= 0 ? 1 : 0) + (right >= 0 ? 1 : 0);
        if (halves == 0)
            return;
        // The left half slides one way, the right the other, each its share of the width; z up becomes y.
        var open = new Vector3(along.X, 0, along.Y) / halves;
        if (left >= 0)
            _doors.Add((left, -open));
        if (right >= 0)
            _doors.Add((right, open));
    }

    // The pads printed on the ground, each with the plate it stands in for, and those whose print is still being made.
    private readonly List<(Decal Print, MeshInstance3D Plate)> _pads = [];
    private readonly List<(Decal Print, MeshInstance3D Plate)> _padsToCome = [];

    /// <summary>How far above and below the ground the grid gives a print reaches, metres: the ground drawn strays from it.</summary>
    private const float PadReach = 2;

    /// <summary>
    /// Prints the pads onto the ground instead of standing them as flat plates (see <see cref="PadPrint"/>): each
    /// pad's picture is cast straight down onto the ground layer over the rectangle it covers, as deep and as high as
    /// the ground there goes (<paramref name="ground"/> gives its height at a world x and z). The plate stands until
    /// its print is ready. Call it once the building stands where it stays; returns the rectangles printed, in world
    /// x and z.
    /// </summary>
    public List<Rect2> PrintPads(Func<float, float, float> ground, float reach = PadReach)
    {
        var printed = new List<Rect2>();
        if (!IsInsideTree() || _pads.Count + _padsToCome.Count > 0)
            return printed;
        for (var i = 0; i < _nodes.Length; i++)
        {
            if (!PadPrint.IsPad(_rig.Nodes[i].Name) || _nodes[i].GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: { } mesh } plate)
                continue;
            // The ground under the pad, from its own height: a grid over its rectangle, a metre or so apart.
            var at = _nodes[i].GlobalTransform;
            var bounds = mesh.GetAabb();
            var area = new Rect2(bounds.Position.X, bounds.Position.Z, bounds.Size.X, bounds.Size.Z);
            if (area.Size.X < 0.1f || area.Size.Y < 0.1f)
                continue;
            float low = float.MaxValue, high = float.MinValue, minX = float.MaxValue, maxX = float.MinValue, minZ = float.MaxValue, maxZ = float.MinValue;
            var steps = (X: Math.Max(2, (int)Mathf.Ceil(area.Size.X)), Z: Math.Max(2, (int)Mathf.Ceil(area.Size.Y)));
            for (var sx = 0; sx <= steps.X; sx++)
            {
                for (var sz = 0; sz <= steps.Z; sz++)
                {
                    var point = at * new Vector3(area.Position.X + area.Size.X * sx / steps.X, 0, area.Position.Y + area.Size.Y * sz / steps.Z);
                    var below = ground(point.X, point.Z) - point.Y;
                    (low, high) = (Mathf.Min(low, below), Mathf.Max(high, below));
                    (minX, maxX, minZ, maxZ) = (Mathf.Min(minX, point.X), Mathf.Max(maxX, point.X), Mathf.Min(minZ, point.Z), Mathf.Max(maxZ, point.Z));
                }
            }
            (low, high) = (low - reach, high + reach);
            var decal = new Decal
            {
                Name = "Print",
                Position = new Vector3(area.GetCenter().X, (low + high) / 2, area.GetCenter().Y),
                Size = new Vector3(area.Size.X, high - low, area.Size.Y),
                // Evenly over all the ground in reach, slopes and steps too; only the ground takes it.
                UpperFade = 0,
                LowerFade = 0,
                NormalFade = 0,
                CullMask = TerrainBuilder.GroundLayer,
                Visible = false,
            };
            _nodes[i].AddChild(decal);
            _padsToCome.Add((decal, plate));
            printed.Add(new Rect2(minX, minZ, maxX - minX, maxZ - minZ));
        }
        ShowPrintedPads();
        return printed;
    }

    /// <summary>Swaps the plates whose prints have come for them (a print with nothing to show leaves its plate).</summary>
    private void ShowPrintedPads()
    {
        for (var p = _padsToCome.Count - 1; p >= 0; p--)
        {
            var (decal, plate) = _padsToCome[p];
            if (plate.Mesh is not { } mesh || !PadPrint.TryGet(mesh, out var print))
                continue;
            _padsToCome.RemoveAt(p);
            if (print is null)
            {
                decal.QueueFree();
                continue;
            }
            decal.TextureAlbedo = print.Texture;
            var area = print.Area;
            decal.Position = new Vector3(area.GetCenter().X, decal.Position.Y, area.GetCenter().Y);
            decal.Size = new Vector3(area.Size.X, decal.Size.Y, area.Size.Y);
            decal.Modulate = _padTint;
            decal.Visible = true;
            plate.Visible = false;
            _pads.Add((decal, plate));
        }
    }

    private Color _padTint = Colors.White;

    /// <summary>The colour the printed pads are tinted with (an unfinished building's glowing green), white for none.</summary>
    public Color PadTint
    {
        set
        {
            _padTint = value;
            foreach (var (print, _) in _pads)
                print.Modulate = value;
        }
    }

    /// <summary>Moves its own parts every frame, for things no actor view drives (an ore hologram).</summary>
    public bool RunsItself { get; set; }

    public override void _Process(double delta)
    {
        if (RunsItself)
            Animate((float)delta, 0, 0, 0, 0);
        if (_padsToCome.Count > 0)
            ShowPrintedPads();
    }

    /// <summary>A part's place in the world as it stands now, by its name in the model, or null.</summary>
    public Transform3D? PartTransform(string name)
    {
        var index = Find(name);
        return index >= 0 && _nodes[index].IsInsideTree() ? _nodes[index].GlobalTransform : null;
    }

    /// <summary>The index of the part called <paramref name="name"/> in the model, or -1.</summary>
    public int Find(string name) => Array.FindIndex(_rig.Nodes, n => n.Name.Equals(name, StringComparison.OrdinalIgnoreCase));

    /// <summary>A part's transform within its parent part, as it stands now; set, it stands so until moved again.</summary>
    public Transform3D this[int part]
    {
        get => _nodes[part].Transform;
        set => _nodes[part].Transform = value;
    }

    /// <summary>A part's transform within its parent part at rest.</summary>
    public Transform3D Rest(int part) => _rig.Nodes[part].Rest;

    /// <summary>Hangs a node from a part, to move with it.</summary>
    public void Attach(int part, Node3D node) => _nodes[part].AddChild(node);

    /// <summary>A part's transform within the whole machine (the model's own axes), as it stands now.</summary>
    public Transform3D InMachine(int part)
    {
        var at = _nodes[part].Transform;
        // Up through the parts it hangs from, as Create hung them.
        for (var child = part; _rig.Nodes[child].Parent is var parent && parent >= 0 && parent < _nodes.Length && parent != child; child = parent)
            at = _nodes[parent].Transform * at;
        return at;
    }

    // Parts whose idle motion stands still for now, and how far behind the others each has fallen.
    private readonly HashSet<int> _idleHeld = [];
    private readonly Dictionary<int, float> _idleBehind = [];

    /// <summary>
    /// Stops a part's idle motion where it is, or lets it go on from there (a glider's bobbing while it moves,
    /// MachPhysGlider::stopBobbing and startBobbing).
    /// </summary>
    public void HoldIdle(int part, bool held)
    {
        if (held)
            _idleHeld.Add(part);
        else
            _idleHeld.Remove(part);
    }

    public Vector3? Eye => _rig.Faceplate >= 0 && _rig.Faceplate < _nodes.Length
        ? _nodes[_rig.Faceplate].GlobalTransform * _rig.Eye
        : null;

    /// <summary>The middle of the faceplate's top and its width; a look without the plate as a mesh of its own gives the eye and a usual width.</summary>
    public (Vector3 Top, float Width)? FaceplateTop
    {
        get
        {
            if (Eye is not { } eye)
                return null;
            if (_nodes[_rig.Faceplate].GetNodeOrNull<MeshInstance3D>("Mesh") is not { } plate)
                return (eye, 0.5f);
            var box = plate.GetAabb();
            var size = box.Size * plate.GlobalBasis.Scale;
            return (plate.GlobalTransform * (box.GetCenter() + Vector3.Up * box.Size.Y / 2), Mathf.Clamp(Mathf.Max(size.X, size.Z), 0.25f, 1.5f));
        }
    }

    /// <summary>
    /// The part whose mesh reaches highest in its own space, a faceplate apart, with that mesh's box
    /// (MachPhysMachineDamage::update looks for it to set the flames on).
    /// </summary>
    public (Node3D Part, Aabb Box)? TopPart()
    {
        (Node3D Part, Aabb Box)? top = null;
        for (var i = 0; i < _nodes.Length; i++)
            if (_rig.Nodes[i] is { Mesh: { } mesh, Name: not ("faceplate" or "fplat") } && (top is null || mesh.GetAabb().End.Y > top.Value.Box.End.Y))
                top = (_nodes[i], mesh.GetAabb());
        return top;
    }

    /// <summary>What turns a thing lying on the view's own axes as the upper body has turned, about the head's place.</summary>
    public Transform3D TurnedAboutHead()
    {
        var pivot = _rig.UpperBody > 0 && _rig.UpperBody < _nodes.Length ? _rig.Nodes[_rig.UpperBody].Rest.Origin with { Y = 0 } : Vector3.Zero;
        var turned = new Basis(Vector3.Up, -_upperBody);
        return new Transform3D(turned, pivot - turned * pivot);
    }

    /// <summary>
    /// Plays the animation <paramref name="name"/> once from its start, over the cycles (a machine's
    /// <c>plan</c> in the original); false when the export has none of that name.
    /// </summary>
    public bool Play(string name)
    {
        // A silo's dome stays as its plan leaves it.
        if (name is "opening" or "closing")
        {
            Dome(_domeOpen ?? false);
            return Dome(name == "opening");
        }
        if (!_rig.Animations.TryGetValue(name, out var animation) || animation.Duration <= 0)
            return false;
        _action = animation;
        _actionTime = 0;
        return true;
    }

    // Legs that have them set off and come to a stop by a plan of their own, played once each (MachPhysSpiderLegs::moveAnimations).
    private string Stepping(string cycle)
    {
        var walking = cycle == "walk";
        if (_cycle == "startWalking" && walking || _cycle == "stopWalking" && !walking)
            return _phase < 1 ? _cycle : cycle;
        if (walking && _cycle != "walk" && _rig.Animations.ContainsKey("startWalking"))
            return "startWalking";
        if (!walking && _cycle is "walk" or "startWalking" && cycle is "rest" or "turnLeft" or "turnRight" && _rig.Animations.ContainsKey("stopWalking"))
            return "stopWalking";
        return cycle;
    }

    /// <summary>
    /// Moves the parts for one frame. <paramref name="speed"/> is metres per second forwards (negative backwards),
    /// <paramref name="turnRate"/> radians per second (positive turns left), <paramref name="maxSpeed"/> the
    /// machine's top speed, and <paramref name="aim"/> the upper body's wanted angle relative to the body.
    /// </summary>
    public void Animate(float delta, float speed, float turnRate, float maxSpeed, float aim, float sideways = 0)
    {
        // Legs walk whichever way the machine goes, sideways too in first person.
        var moving = Mathf.Abs(speed) > 0.3f || Mathf.Abs(sideways) > 0.3f;
        var turning = !moving && Mathf.Abs(turnRate) > 0.3f;

        // A finished action gives its parts back: to rest, then to the cycles below.
        if (_action is { } done && (_actionTime += delta) >= done.Duration)
        {
            foreach (var track in done.Tracks)
                if (track.Node < _nodes.Length)
                    _nodes[track.Node].Transform = _rig.Nodes[track.Node].Rest;
            _action = null;
        }

        // Parts that move by themselves; the walking cycles below take over the parts they move.
        _idleTime += delta;
        foreach (var part in _idleHeld)
            _idleBehind[part] = _idleBehind.GetValueOrDefault(part) + delta;
        foreach (var idle in _rig.Idle)
            if (idle.Track.Node < _nodes.Length && idle.Duration > 0)
            {
                var time = _idleTime - _idleBehind.GetValueOrDefault(idle.Track.Node);
                _nodes[idle.Track.Node].Transform = idle.Track.At(Mathf.PosMod(time, idle.Duration) / idle.Duration);
            }

        // Doors slide open as machines come and shut behind them.
        if (_doors.Count > 0)
        {
            var width = _doors.Max(d => d.Open.Length());
            var step = width > 0 ? delta / width : 1;
            _doorOpening = Mathf.Clamp(_doorOpening + (DoorsOpen ? step : -step), 0, 1);
            foreach (var (node, open) in _doors)
                _nodes[node].Transform = _rig.Nodes[node].Rest.Translated(open * _doorOpening);
        }

        // A building at work; when it stops, its parts stay where they stopped.
        if (Working)
        {
            _workTime += delta;
            foreach (var work in _rig.Working)
                if (work.Track.Node < _nodes.Length && work.Duration > 0)
                    _nodes[work.Track.Node].Transform = work.Track.At(_workTime % work.Duration / work.Duration);
        }
        Glow(Working, _workTime);
        ShowScreens(Working, _workTime);

        if (_rig.Animations.Count > 0)
        {
            // A constructor at work plays its building cycle (MachPhysConstructor::startConstructing) instead, a
            // technician at work in a lab its research (MachPhysTechnician::research).
            var cycle = Constructing && _rig.Animations.ContainsKey("construct") ? "construct"
                : Working && _rig.Animations.ContainsKey("research") ? "research"
                : moving ? "walk" : turning ? (turnRate > 0 ? "turnLeft" : "turnRight") : "rest";
            cycle = Stepping(cycle);
            if (cycle != _cycle)
            {
                // Parts the new cycle leaves alone go back to rest (the arms after building).
                if (_rig.Animations.TryGetValue(_cycle, out var ended) && !_rig.Animations.ContainsKey(cycle))
                    foreach (var track in ended.Tracks)
                        if (track.Node < _nodes.Length)
                            _nodes[track.Node].Transform = _rig.Nodes[track.Node].Rest;
                _cycle = cycle;
                _phase = 0;
            }
            if (_rig.Animations.TryGetValue(_cycle, out var animation) && animation.Duration > 0)
            {
                // Legs keep pace with the ground: the walk cycle runs at the machine's share of its top speed.
                var rate = moving && _cycle == "walk" ? Mathf.Clamp(Mathf.Sqrt(speed * speed + sideways * sideways) / Mathf.Max(1, maxSpeed), 0.5f, 1.5f) : 1;
                _phase += delta * rate / animation.Duration;
                _phase = _cycle is "startWalking" or "stopWalking" ? Mathf.Min(_phase, 1) : _phase % 1;
                foreach (var track in animation.Tracks)
                    if (track.Node < _nodes.Length)
                        _nodes[track.Node].Transform = track.At(_phase);
            }
        }

        if (Locating && _rig.Animations.TryGetValue("locate", out var locate) && locate.Duration > 0)
        {
            _locateTime = (_locateTime + delta) % locate.Duration;
            foreach (var track in locate.Tracks)
                if (track.Node < _nodes.Length)
                    _nodes[track.Node].Transform = track.At(_locateTime / locate.Duration);
        }

        if (_action is { } action)
            foreach (var track in action.Tracks)
                if (track.Node < _nodes.Length)
                    _nodes[track.Node].Transform = track.At(_actionTime / action.Duration);

        if (_rig.Wheels is { } wheels && wheels.Radius > 0)
        {
            // Turning on the spot drives the sides against each other, as the original reversed one side.
            var spin = turnRate * 1.5f;
            _leftWheel += (speed - spin) * delta / wheels.Radius;
            _rightWheel += (speed + spin) * delta / wheels.Radius;
            // The axle is the original's local y axis, Godot's local z; the axis swap reverses the angle.
            foreach (var i in wheels.Left)
                Spin(i, -_leftWheel);
            foreach (var i in wheels.Right)
                Spin(i, -_rightWheel);
        }

        if (_rig.Tracks is { } tracks)
        {
            var spin = turnRate * 1.5f;
            Scroll(_leftTread, (speed - spin) * tracks.RepeatsPerMeter * delta);
            Scroll(_rightTread, (speed + spin) * tracks.RepeatsPerMeter * delta);
        }

        MoveFiring(delta);
        MoveDome(delta);

        if (_rig.UpperBody > 0 && _rig.UpperBody < _nodes.Length)
        {
            var step = Mathf.Clamp(Mathf.AngleDifference(_upperBody, aim), -TurnRate * delta, TurnRate * delta);
            _upperBody += step;
            // The original turns about its vertical z axis; with the axis swap that is Godot's y, reversed.
            _nodes[_rig.UpperBody].Transform = (_head ?? _rig.Nodes[_rig.UpperBody].Rest) * new Transform3D(new Basis(Vector3.Up, -_upperBody), Vector3.Zero);
        }
        MoveShake(delta);
    }

    // The positions a shake goes through and how far along it is.
    private readonly List<(int Node, Quaternion[] Keys)> _shaking = [];
    private float _shakeTime;
    private const float ShakeTime = 0.5f, ShakeDamp = 0.8f, ShakeAngle = 45;
    private const int ShakeKeys = 4;

    /// <summary>
    /// A hit shakes up to two of the head, neck and body for half a second, in positions of ever less angle that swing
    /// to and fro (MachPhysMachine::shake). A part that has its own motion keeps it, as does one shaken already.
    /// </summary>
    public void Shake()
    {
        if (_shaking.Count > 0)
            return;
        var moved = _rig.Animations.TryGetValue(_cycle, out var cycle) ? cycle.Tracks.Select(t => t.Node).ToHashSet() : [];
        var random = Random.Shared;
        foreach (var part in new[] { "head", "neck", "body" }.Select(Find).Where(i => i > 0 && i < _nodes.Length).Take(2))
        {
            if (moved.Contains(part))
                continue;
            var keys = new Quaternion[ShakeKeys + 1];
            var (angle, sign) = (ShakeAngle * ShakeDamp, 1f);
            for (var k = 0; k < ShakeKeys; k++)
            {
                // The angle shared out at random over the three axes.
                var x = random.NextSingle();
                var y = random.NextSingle() * (1 - x);
                var z = random.NextSingle() * (1 - x - y);
                keys[k] = Quaternion.FromEuler(new Vector3(x, -z, y) * Mathf.DegToRad(angle) * sign);
                sign = -sign;
                angle *= ShakeDamp;
            }
            keys[ShakeKeys] = Quaternion.Identity;
            _shaking.Add((part, keys));
        }
        _shakeTime = 0;
    }

    private void MoveShake(float delta)
    {
        if (_shaking.Count == 0)
            return;
        _shakeTime += delta;
        var at = Mathf.Min(_shakeTime / ShakeTime, 1) * (ShakeKeys + 1);
        var (key, between) = ((int)at, at - (int)at);
        foreach (var (node, keys) in _shaking)
        {
            var from = key == 0 ? Quaternion.Identity : keys[Math.Min(key, ShakeKeys) - 1];
            var turn = from.Slerp(keys[Math.Min(key, ShakeKeys)], between);
            // The upper body is set every frame from where it aims, the others stand at rest.
            var stands = node == _rig.UpperBody ? _nodes[node].Transform : _rig.Nodes[node].Rest;
            _nodes[node].Transform = stands * new Transform3D(new Basis(turn), Vector3.Zero);
        }
        if (_shakeTime >= ShakeTime)
        {
            foreach (var (node, _) in _shaking)
                if (node != _rig.UpperBody)
                    _nodes[node].Transform = _rig.Nodes[node].Rest;
            _shaking.Clear();
        }
    }

    private void Spin(int node, float angle)
    {
        if (node > 0 && node < _nodes.Length)
            _nodes[node].Transform = _rig.Nodes[node].Rest * new Transform3D(new Basis(Vector3.Back, angle), Vector3.Zero);
    }

    private static void Scroll(List<StandardMaterial3D> materials, float by)
    {
        foreach (var material in materials)
            material.Uv1Offset = new Vector3(material.Uv1Offset.X, (material.Uv1Offset.Y + by) % 1, 0);
    }

    /// <summary>Gives a tread its own copies of its materials, so scrolling it leaves other machines alone.</summary>
    private void OwnMaterials(int node, List<StandardMaterial3D> into)
    {
        if (node <= 0 || node >= _nodes.Length || _nodes[node].GetNodeOrNull<MeshInstance3D>("Mesh") is not { Mesh: { } mesh } instance)
            return;
        for (var s = 0; s < mesh.GetSurfaceCount(); s++)
        {
            if (mesh.SurfaceGetMaterial(s) is StandardMaterial3D shared)
            {
                var own = (StandardMaterial3D)shared.Duplicate();
                instance.SetSurfaceOverrideMaterial(s, own);
                into.Add(own);
            }
        }
    }
}
