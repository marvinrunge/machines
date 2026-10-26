using Godot;
using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// A skirmish match as the menu set it up (<see cref="MatchRequest"/>): you against the original AI scripts.
/// Runs the simulation at its fixed 20 ticks per second and turns mouse and keyboard into commands.
/// </summary>
public partial class Game : Node3D
{
    /// <summary>The start position (scenario race) the local player commands.</summary>
    public static Race Player => MatchRequest.Current.Starts.PlayerSlot;

    /// <summary>The colour a race is shown in; start positions keep the scenario's race in the simulation.</summary>
    public static Race Look(Race race) => MatchRequest.Current.Starts.Colours.GetValueOrDefault(race, race);

    private readonly Dictionary<int, ActorView> _views = [];
    private readonly List<int> _selection = [];
    private double _accumulator;

    private ClassicAssets _assets = null!;
    private CameraRig _rig = null!;
    private Hud _hud = null!;
    private Node3D _effects = null!;

    // Drag selection
    private Vector2? _dragStart;

    // Building placement
    private (string Type, string? SubType, int Level)? _placing;
    private int _placingOrientation;
    private MeshInstance3D? _ghost;

    // A command waiting for the player to click its target: the ground point and the actor there, if any
    private Action<NVector2, Actor?>? _targeting;

    // Area weapons going off, drawn as growing spheres
    private readonly Dictionary<Blast, MeshInstance3D> _blastViews = [];

    // First person
    private Machine? _driven;
    private Camera3D _firstPersonCamera = null!;
    private float _lookPitch;

    public GameWorld World { get; private set; } = null!;
    public IReadOnlyList<int> Selection => _selection;
    public bool Paused { get; set; }

    public override void _Ready()
    {
        var request = MatchRequest.Current;
        var data = Paths.Data;
        World = MatchSetup.Create(data, request.Scenario, request.Planet, request.Starts.Players,
            seed: (int)(Time.GetUnixTimeFromSystem() % int.MaxValue), request.Options);

        _assets = new ClassicAssets(Paths.Root);
        var map = _assets.PlanetMap(request.Planet);
        var surface = World.Planet.Surface;
        HeightField.Current = _assets.PlanetHeights(request.Planet) is { } heights
            ? HeightField.FromExport(heights, surface.Width, surface.Depth)
            : new HeightField(surface);
        AddChild(TerrainBuilder.Build(World.Planet, HeightField.Current, map, _assets.PlanetTerrain(request.Planet)));
        AddLighting();

        _effects = new Node3D { Name = "Effects" };
        AddChild(_effects);

        _rig = new CameraRig { Name = "CameraRig" };
        AddChild(_rig);
        var start = data.LoadScenario(request.Scenario).Race(Player)?.Camera;
        _rig.Setup(start is null ? new Vector3(400, 0, 400) : new Vector3(start.Position.X, 0, start.Position.Y),
            new Vector2(World.Planet.Surface.Width, World.Planet.Surface.Depth));

        _firstPersonCamera = new Camera3D { Far = 3000, Fov = 70, Name = "FirstPersonCamera" };
        AddChild(_firstPersonCamera);

        _hud = new Hud(this, map);
        AddChild(_hud);

        SyncViews();
        _hud.Message(_assets.HasClassicModels
            ? "Classic models loaded from your game copy."
            : "No classic models found (see tools/classic-export); showing simple shapes.");
    }

    /// <summary>Sun, sky and fog from the planet's .env file, following the game clock.</summary>
    private void AddLighting() =>
        AddChild(PlanetLook.Create(World.Planet.Environment, () => World.Time, () => _driven is not null ? 0 : _rig?.Distance ?? 160));

    // ---- simulation loop ------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        if (!Paused && World.Winner is null)
        {
            _accumulator += Math.Min(delta, 0.25);
            while (_accumulator >= GameWorld.Dt)
            {
                _accumulator -= GameWorld.Dt;
                UpdateDriving();
                World.Step();
                foreach (var rejected in World.Rejected.Where(r => r.Command.Race == Player))
                    _hud.Message(rejected.Reason);
                ShowEvents();
                SyncBlasts();
                SyncViews();
            }
        }

        if (World.Winner is { } winner)
            _hud.Banner(winner == Player ? "Victory" : "Defeat");
        else if (World.Races[Player].Defeated)
            _hud.Banner("Defeat");

        UpdateGhost();
        UpdateFirstPersonCamera();
        _hud.Refresh();
        TakeScreenshotIfAsked(delta);
    }

    private double _runTime;

    /// <summary>
    /// For automated checks: MACHINES_SCREENSHOT=&lt;file.png&gt; saves the window after MACHINES_SCREENSHOT_AFTER
    /// seconds (default 4) and quits. MACHINES_SCREENSHOT_FOCUS=x,y moves the camera there first.
    /// </summary>
    private void TakeScreenshotIfAsked(double delta)
    {
        var path = System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT");
        if (string.IsNullOrEmpty(path))
            return;
        _rig.EdgePan = false;
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_FOCUS") is { } focus)
        {
            var parts = focus.Split(',');
            _rig.Position = new Vector3(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), 0,
                float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            if (parts.Length >= 4)
            {
                _rig.Pitch = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                _rig.Distance = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
            }
        }
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_BLAST") is { Length: > 0 } blastAt)
        {
            // e.g. "NUCLEAR_MISSILE,800,800": sets the weapon off there at once.
            var parts = blastAt.Split(',');
            var weapon = World.Data.Parm.Weapons.First(w => w.TypeName == parts[0]);
            World.StartBlast(weapon, new NVector2(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)), null);
        }
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_SELECT") is { Length: > 0 } select)
        {
            // e.g. "CONSTRUCTOR" or "AGGRESSOR"; with MACHINES_SCREENSHOT_FIRSTPERSON=1 the first one is driven.
            Select(World.Machines.Where(m => m.Race == Player && m.Spec.Type == select).Select(m => m.Id).ToList());
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_FIRSTPERSON") == "1")
            {
                Select(_selection.Take(1).ToList());
                EnterFirstPerson();
            }
        }
        _runTime += delta;
        var after = double.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_AFTER"), out var s) ? s : 4;
        if (_runTime < after)
            return;
        GetViewport().GetTexture().GetImage().SavePng(path);
        GetTree().Quit();
    }

    private void SyncViews()
    {
        foreach (var actor in World.Actors)
        {
            // Captured or turned actors are rebuilt in their new colours.
            if (_views.TryGetValue(actor.Id, out var view) && view.Race != actor.Race)
            {
                view.QueueFree();
                _views.Remove(actor.Id);
                _selection.Remove(actor.Id);
                view = null;
            }
            if (view is null)
            {
                view = ActorView.Create(actor, _assets);
                _views[actor.Id] = view;
                AddChild(view);
            }
            view.Sync(actor);
        }

        foreach (var id in _views.Keys.Where(id => World.Find(id) is null).ToList())
        {
            _views[id].QueueFree();
            _views.Remove(id);
            _selection.Remove(id);
            if (_driven?.Id == id)
                LeaveFirstPerson();
        }
    }

    private void ShowEvents()
    {
        foreach (var e in World.Events)
        {
            switch (e.Kind)
            {
                case GameEventKind.Fired when e.Other is { } target && World.Find(target) is { } victim:
                    Tracer(ActorView.World(e.Position) + Vector3.Up * 2, ActorView.World(victim.Position) + Vector3.Up * 1.5f, e.Detail ?? "");
                    break;
                case GameEventKind.Destroyed:
                    Flash(ActorView.World(e.Position));
                    break;
                case GameEventKind.MachineBuilt when World.Find(e.Actor)?.Race == Player:
                    _hud.Message($"Machine ready: {e.Detail}");
                    break;
                case GameEventKind.ConstructionComplete when World.Find(e.Actor)?.Race == Player:
                    _hud.Message($"Construction complete: {e.Detail}");
                    break;
                case GameEventKind.ResearchComplete when World.Find(e.Actor)?.Race == Player:
                    _hud.Message($"Research complete: {e.Detail}");
                    break;
                case GameEventKind.SiteDiscovered when e.Detail == Player.ToString():
                    _hud.Message("Mineral site found");
                    break;
            }
        }
    }

    /// <summary>Waits for a left click on the target of the building's ion cannon or nuclear missile.</summary>
    public void BeginStrike(int launcher) =>
        BeginTargeting("Left click the target; right click cancels", (point, _) => World.Issue(new StrikeCommand(Player, launcher, point)));

    /// <summary>The next left click picks the target for <paramref name="onClick"/>; a right click cancels.</summary>
    public void BeginTargeting(string message, Action<NVector2, Actor?> onClick)
    {
        CancelPlacement();
        _targeting = onClick;
        _hud.Message(message);
    }

    private void SyncBlasts()
    {
        foreach (var blast in World.Blasts.Where(b => !_blastViews.ContainsKey(b)))
        {
            var colour = blast.Weapon switch
            {
                "VORTEX" => new Color(0.1f, 0.05f, 0.2f, 0.75f),
                "ION_ORBITAL_CANNON" => new Color(0.55f, 0.75f, 1f, 0.45f),
                "NUCLEAR_MISSILE" => new Color(1f, 0.55f, 0.15f, 0.45f),
                "BEE_BOMB" => new Color(1f, 0.85f, 0.2f, 0.4f),
                _ => new Color(0.8f, 0.7f, 0.5f, 0.35f),
            };
            var view = new MeshInstance3D
            {
                Mesh = new SphereMesh { Radius = 1, Height = 2 },
                MaterialOverride = new StandardMaterial3D
                {
                    AlbedoColor = colour,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            _effects.AddChild(view);
            view.GlobalPosition = ActorView.World(blast.Position);
            _blastViews[blast] = view;
            if (blast.Weapon is "ION_ORBITAL_CANNON" or "NUCLEAR_MISSILE")
                _hud.Message(blast.Weapon == "NUCLEAR_MISSILE" ? "Nuclear detonation" : "Ion cannon strike");
        }

        foreach (var (blast, view) in _blastViews.ToList())
        {
            if (!World.Blasts.Contains(blast))
            {
                view.QueueFree();
                _blastViews.Remove(blast);
                continue;
            }
            var radius = Mathf.Max(0.5f, blast.Radius(World.Time));
            // Domes, flattened a little: the original's spheres and rings hug the ground.
            view.Scale = new Vector3(radius, radius * 0.6f, radius);
        }
    }

    private void Tracer(Vector3 from, Vector3 to, string weapon)
    {
        var length = from.DistanceTo(to);
        if (length < 0.5f)
            return;
        var colour = weapon.Contains("PLASMA") ? new Color(0.3f, 1, 0.4f)
            : weapon.Contains("FLAME") ? new Color(1, 0.5f, 0.1f)
            : weapon.Contains("ELECTRIC") ? new Color(0.5f, 0.7f, 1)
            : new Color(1, 0.9f, 0.4f);
        var beam = new MeshInstance3D
        {
            Mesh = new BoxMesh { Size = new Vector3(0.25f, 0.25f, length) },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = colour, ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        _effects.AddChild(beam);
        beam.GlobalPosition = (from + to) / 2;
        beam.LookAt(to, Vector3.Up);
        GetTree().CreateTimer(0.08).Timeout += beam.QueueFree;
    }

    private void Flash(Vector3 at)
    {
        var flash = new MeshInstance3D
        {
            Mesh = new SphereMesh { Radius = 4, Height = 8 },
            MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1, 0.6f, 0.2f), ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded },
        };
        _effects.AddChild(flash);
        flash.GlobalPosition = at + Vector3.Up * 2;
        GetTree().CreateTimer(0.3).Timeout += flash.QueueFree;
    }

    // ---- input ----------------------------------------------------------------------------------

    public override void _UnhandledInput(InputEvent @event)
    {
        if (_driven is not null)
        {
            FirstPersonInput(@event);
            return;
        }

        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key:
                OnKey(key);
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Left } left:
                if (left.Pressed)
                {
                    if (_targeting is { } onClick)
                    {
                        if (GroundAt(left.Position) is { } point)
                            onClick(point, ActorAt(left.Position));
                        _targeting = null;
                    }
                    else if (_placing is not null)
                        PlaceBuilding();
                    else
                        _dragStart = left.Position;
                }
                else if (_dragStart is { } start)
                {
                    SelectArea(start, left.Position, left.ShiftPressed);
                    _dragStart = null;
                    _hud.DragRect = null;
                }
                break;

            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: true } right:
                if (_targeting is not null)
                    _targeting = null;
                else if (_placing is not null)
                    CancelPlacement();
                else
                    OrderAt(right.Position);
                break;

            case InputEventMouseMotion motion when _dragStart is { } from:
                _hud.DragRect = new Rect2(from, motion.Position - from).Abs();
                break;
        }
    }

    private void OnKey(InputEventKey key)
    {
        switch (key.Keycode)
        {
            case Key.Escape:
                if (_placing is not null) CancelPlacement();
                else Select([]);
                break;
            case Key.R when _placing is not null:
                _placingOrientation = (_placingOrientation + 90) % 360;
                break;
            case Key.S when key.CtrlPressed || key.ShiftPressed:
                World.Issue(new StopCommand(Player, _selection.ToList()));
                break;
            case Key.F:
                EnterFirstPerson();
                break;
            case Key.P:
                Paused = !Paused;
                _hud.Message(Paused ? "Paused" : "Running");
                break;
            case Key.F10:
                GetTree().ChangeSceneToFile("res://scenes/Menu.tscn");
                break;
        }
    }

    /// <summary>Where the mouse ray meets the ground, in original ground coordinates.</summary>
    public NVector2? GroundAt(Vector2 screen)
    {
        var camera = GetViewport().GetCamera3D();
        var hit = HeightField.Current?.Hit(camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        return hit is { } point ? new NVector2(point.X, point.Z) : null;
    }

    /// <summary>The actor under the cursor: a machine near it on screen, or the building whose footprint holds it.</summary>
    public Actor? ActorAt(Vector2 screen)
    {
        var camera = GetViewport().GetCamera3D();
        Actor? best = null;
        var bestDistance = 18f;
        foreach (var machine in World.Machines.Where(m => m.Inside is null))
        {
            var p = ActorView.World(machine.Position) + Vector3.Up * 1.5f;
            if (camera.IsPositionBehind(p))
                continue;
            var d = camera.UnprojectPosition(p).DistanceTo(screen);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = machine;
            }
        }
        if (best is not null)
            return best;

        return GroundAt(screen) is { } ground ? World.Constructions.FirstOrDefault(c => c.DistanceFrom(ground) <= 0) : null;
    }

    private void SelectArea(Vector2 from, Vector2 to, bool add)
    {
        var rect = new Rect2(from, to - from).Abs();
        var camera = GetViewport().GetCamera3D();
        List<int> picked;
        if (rect.Size.Length() < 6)
        {
            picked = ActorAt(to) is { } actor ? [actor.Id] : [];
        }
        else
        {
            picked = World.Machines
                .Where(m => m.Race == Player && m.Inside is null && !camera.IsPositionBehind(ActorView.World(m.Position)))
                .Where(m => rect.HasPoint(camera.UnprojectPosition(ActorView.World(m.Position))))
                .Select(m => m.Id)
                .ToList();
        }
        Select(add ? _selection.Union(picked).ToList() : picked);
    }

    public void Select(List<int> ids)
    {
        foreach (var id in _selection)
            if (_views.TryGetValue(id, out var view)) view.Selected = false;
        _selection.Clear();
        _selection.AddRange(ids.Where(id => World.Find(id) is not null));
        foreach (var id in _selection)
            if (_views.TryGetValue(id, out var view)) view.Selected = true;
        _hud.SelectionChanged();
    }

    public IEnumerable<Machine> SelectedMachines() =>
        _selection.Select(World.Find<Machine>).OfType<Machine>().Where(m => m.Race == Player);

    /// <summary>Right click: the right order for each selected machine and what was clicked.</summary>
    private void OrderAt(Vector2 screen)
    {
        var machines = SelectedMachines().ToList();
        if (machines.Count == 0)
            return;
        var target = ActorAt(screen);
        var ground = GroundAt(screen);
        List<int> Ids(Func<Machine, bool> which) => machines.Where(which).Select(m => m.Id).ToList();

        if (target is not null && World.AreEnemies(Player, target.Race))
        {
            // Constructors take enemy buildings over; everyone armed attacks.
            var capturers = target is Construction { Complete: true } ? Ids(m => m.Spec.Type == "CONSTRUCTOR") : [];
            if (capturers.Count > 0)
                World.Issue(new CaptureCommand(Player, capturers, target.Id));
            World.Issue(new AttackCommand(Player, Ids(m => m.CanFight), target.Id));
            World.Issue(new MoveCommand(Player, Ids(m => !m.CanFight && !capturers.Contains(m.Id)), target.Position));
            return;
        }

        if (target is Machine patient && patient.Race == Player && patient.HitPoints < patient.MaxHitPoints
            && machines.Any(m => m.CanHeal && m.Id != patient.Id))
        {
            World.Issue(new HealCommand(Player, Ids(m => m.CanHeal && m.Id != patient.Id), patient.Id));
            return;
        }

        if (target is Construction building && building.Race == Player)
        {
            var rest = machines.Select(m => m.Id).ToHashSet();
            if (!building.Complete)
            {
                var constructors = Ids(m => m.Spec.Type == "CONSTRUCTOR");
                World.Issue(new ConstructCommand(Player, constructors, building.Id));
                rest.ExceptWith(constructors);
            }
            else if (building.HitPoints < building.MaxHitPoints)
            {
                var constructors = Ids(m => m.Spec.Type == "CONSTRUCTOR");
                World.Issue(new RepairCommand(Player, constructors, building.Id));
                rest.ExceptWith(constructors);
            }
            if (building.IsType("GARRISON") && building.Complete)
            {
                World.Issue(new EnterGarrisonCommand(Player, rest.ToList(), building.Id));
                rest.Clear();
            }
            if (building.IsType("HARDWARE_LAB"))
            {
                var technicians = Ids(m => m.Spec.Type == "TECHNICIAN");
                World.Issue(new EnterLabCommand(Player, technicians, building.Id));
                rest.ExceptWith(technicians);
            }
            if (building.IsType("MINE"))
            {
                var store = World.Constructions.Where(c => c.Race == Player && c.Complete && c.Spec.Type is "SMELTER" or "POD")
                    .MinBy(c => NVector2.Distance(c.Position, building.Position));
                var carriers = Ids(m => m.Spec.Type == "RESOURCE_CARRIER");
                if (store is not null)
                    World.Issue(new TransportCommand(Player, carriers, building.Id, store.Id));
                rest.ExceptWith(carriers);
            }
            if (rest.Count > 0)
                World.Issue(new MoveCommand(Player, rest.ToList(), building.Position));
            return;
        }

        if (ground is not { } point)
            return;
        var locators = Ids(m => m.Spec.Type == "GEO_LOCATOR");
        if (locators.Count > 0)
            World.Issue(new LocateCommand(Player, locators, point));
        var others = Ids(m => m.Spec.Type != "GEO_LOCATOR");
        if (Input.IsKeyPressed(Key.Ctrl))
            World.Issue(new AttackMoveCommand(Player, others, point));
        else
            World.Issue(new MoveCommand(Player, others, point));
    }

    // ---- building placement ---------------------------------------------------------------------

    public void BeginPlacement(string type, string? subType, int level)
    {
        CancelPlacement();
        _placing = (type, subType, level);
        _placingOrientation = 0;
        _ghost = new MeshInstance3D
        {
            Mesh = new BoxMesh(),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.3f, 1, 0.3f, 0.4f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
        };
        AddChild(_ghost);
        _hud.Message("Left click to place, R to rotate, right click to cancel");
    }

    private void CancelPlacement()
    {
        _placing = null;
        _ghost?.QueueFree();
        _ghost = null;
    }

    private void UpdateGhost()
    {
        if (_placing is not { } p || _ghost is null || GroundAt(GetViewport().GetMousePosition()) is not { } ground)
            return;
        var at = Snap(p.Type, ground);
        var spec = World.Data.Parm.Construction(p.SubType is null ? p.Type : $"{p.Type} {p.SubType}");
        var probe = new Construction(0, Player, at, spec, spec.Level(p.Level), _placingOrientation);
        var size = probe.Footprint.Max - probe.Footprint.Min;
        ((BoxMesh)_ghost.Mesh).Size = new Vector3(size.X, 6, size.Y);
        _ghost.Position = ActorView.World((probe.Footprint.Min + probe.Footprint.Max) / 2) + Vector3.Up * 3;
        var ok = World.WhyCannotPlace(Player, p.Type, p.SubType, p.Level, at, _placingOrientation) is null;
        ((StandardMaterial3D)_ghost.MaterialOverride).AlbedoColor = ok ? new Color(0.3f, 1, 0.3f, 0.4f) : new Color(1, 0.25f, 0.2f, 0.4f);
    }

    /// <summary>Mines snap onto the nearest found mineral site.</summary>
    private NVector2 Snap(string type, NVector2 ground)
    {
        if (type != "MINE")
            return ground;
        var site = World.Sites.Where(s => s.DiscoveredBy.Contains(Player) && s.Mine is null)
            .MinBy(s => NVector2.Distance(s.Position, ground));
        return site is not null && NVector2.Distance(site.Position, ground) < 40 ? site.Position : ground;
    }

    private void PlaceBuilding()
    {
        if (_placing is not { } p || GroundAt(GetViewport().GetMousePosition()) is not { } ground)
            return;
        var constructors = SelectedMachines().Where(m => m.Spec.Type == "CONSTRUCTOR").Select(m => m.Id).ToList();
        World.Issue(new PlaceConstructionCommand(Player, constructors, p.Type, p.SubType, p.Level, Snap(p.Type, ground), _placingOrientation));
        if (!Input.IsKeyPressed(Key.Shift))
            CancelPlacement();
    }

    // ---- first person ---------------------------------------------------------------------------

    private void EnterFirstPerson()
    {
        if (SelectedMachines().FirstOrDefault() is not { } machine)
        {
            _hud.Message("Select one of your machines to drive it (F)");
            return;
        }
        _driven = machine;
        machine.DrivenByPlayer = true;
        World.Issue(new StopCommand(Player, [machine.Id]));
        _rig.Active = false;
        _firstPersonCamera.Current = true;
        _lookPitch = 0;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _hud.FirstPerson = true;
        _hud.Message("First person: W/S drive, A/D or mouse turn, left mouse fire, F or Esc to leave");
    }

    private void LeaveFirstPerson()
    {
        if (_driven is { } machine)
        {
            machine.DrivenByPlayer = false;
            World.DriveInput.Remove(machine.Id);
            World.PlayerAims.Remove(machine.Id);
        }
        _driven = null;
        _rig.Active = true;
        _rig.Camera.Current = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _hud.FirstPerson = false;
    }

    private float _mouseTurn;

    private void FirstPersonInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F or Key.Escape }:
                LeaveFirstPerson();
                break;
            case InputEventMouseMotion motion:
                _mouseTurn -= motion.Relative.X * 0.02f;
                var limits = World.Data.Parm.General;
                _lookPitch = Mathf.Clamp(_lookPitch - motion.Relative.Y * 0.002f,
                    -Mathf.DegToRad(limits.FirstPersonLookMaxAngle), Mathf.DegToRad(-limits.FirstPersonLookMinAngle));
                break;
        }
    }

    private void UpdateDriving()
    {
        if (_driven is not { } machine)
            return;
        var forward = (Input.IsKeyPressed(Key.W) ? 1f : 0f) - (Input.IsKeyPressed(Key.S) ? 1f : 0f);
        var turn = (Input.IsKeyPressed(Key.A) ? 1f : 0f) - (Input.IsKeyPressed(Key.D) ? 1f : 0f) + Mathf.Clamp(_mouseTurn, -1, 1);
        _mouseTurn *= 0.5f;
        World.DriveInput[machine.Id] = (forward, Mathf.Clamp(turn, -1, 1));

        // Fire at the enemy closest to the crosshair while the left button is held.
        if (Input.IsMouseButtonPressed(MouseButton.Left) && machine.CanFight)
        {
            var range = machine.Weapons.Max(w => w.Spec.Range);
            var aim = new NVector2(MathF.Cos(machine.Facing), MathF.Sin(machine.Facing));
            var target = World.Actors
                .Where(a => !a.IsDead && World.AreEnemies(Player, a.Race) && machine.DistanceTo(a) <= range)
                .Select(a => (Actor: a, Angle: MathF.Acos(Math.Clamp(NVector2.Dot(aim, NVector2.Normalize(a.Position - machine.Position + new NVector2(1e-3f, 0))), -1, 1))))
                .Where(x => x.Angle < 0.35f)
                .OrderBy(x => x.Angle)
                .Select(x => x.Actor)
                .FirstOrDefault();
            if (target is not null)
                World.PlayerAims[machine.Id] = target.Id;
            else
                World.PlayerAims.Remove(machine.Id);
        }
        else
        {
            World.PlayerAims.Remove(machine.Id);
        }
    }

    private void UpdateFirstPersonCamera()
    {
        if (_driven is not { } machine || !_views.TryGetValue(machine.Id, out var view))
            return;
        var eye = view.GlobalPosition + Vector3.Up * Mathf.Max(2.5f, view.Size * 0.9f);
        _firstPersonCamera.GlobalPosition = eye;
        _firstPersonCamera.GlobalRotation = new Vector3(_lookPitch, view.GlobalRotation.Y - Mathf.Pi / 2, 0);
    }
}
