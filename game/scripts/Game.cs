using Godot;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.Navigation;
using Machines.Sim.Presentation;
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

    // The holograms over found mineral sites, by site.
    private readonly Dictionary<int, Node3D> _holograms = [];

    /// <summary>
    /// A found mineral site shows a turning ore hologram in its finder's colour (MachLogMineralSite::beDiscoveredBy),
    /// seen by everyone, until its ore runs out.
    /// </summary>
    private void SyncOreHolograms()
    {
        foreach (var site in World.Sites)
        {
            var shown = site.DiscoveredBy.Count > 0 && site.Ore > 0;
            if (shown && !_holograms.ContainsKey(site.Id))
            {
                var finder = site.DiscoveredBy.Contains(Player) ? Player : site.DiscoveredBy.Min();
                Node3D hologram;
                if (_assets.RigFor("ORE_HOLOGRAPH_1", Look(finder)) is { } rig)
                {
                    var view = RigView.Create(rig);
                    view.RunsItself = true;
                    hologram = view;
                }
                else
                {
                    hologram = new MeshInstance3D { Mesh = new SphereMesh { Radius = 1.5f, Height = 3 } };
                }
                hologram.Position = ActorView.World(site.Position);
                hologram.Name = $"Ore{site.Id}";
                AddChild(hologram);
                _holograms[site.Id] = hologram;
                var hum = _audio.Loop("Hum");
                hologram.AddChild(hum.Player);
                hum.Play("SID_OREHOLO");
            }
            else if (!shown && _holograms.Remove(site.Id, out var gone))
            {
                gone.QueueFree();
            }
        }
    }
    // The wreckage on the ground, by wreck, with its fire.
    private readonly Dictionary<int, (Node3D View, GpuParticles3D Fire, GameAudio.LoopSound Crackle)> _debris = [];

    /// <summary>
    /// Wreckage shows as a heap of dark scrap over the ground it covers, as the original's debris units lay, on fire
    /// for its first minute (MachPhysDebris::startBurning) and smoking until it burns out or is gathered.
    /// </summary>
    private void SyncDebris()
    {
        foreach (var wreck in World.Debris.Where(d => !_debris.ContainsKey(d.Id)))
        {
            var heap = new Node3D { Name = $"Debris{wreck.Id}" };
            var size = wreck.Boundary.Max - wreck.Boundary.Min;
            var spread = Mathf.Clamp(Mathf.Max(size.X, size.Y) * 0.4f, 1.5f, 12);
            var random = new Random(wreck.Id);
            float Between(float low, float high) => low + (float)random.NextDouble() * (high - low);
            var metal = new StandardMaterial3D { AlbedoColor = new Color(0.17f, 0.15f, 0.14f), Metallic = 0.5f, Roughness = 0.75f };
            var charred = new StandardMaterial3D { AlbedoColor = new Color(0.07f, 0.06f, 0.06f), Roughness = 0.95f };
            // The original's own wreck (the debris exemplar, exported as fx/debris), as it lay: one unit at the
            // middle, turned at random, its own size whatever burnt there (MachPhysDebris); without it, a heap of scrap.
            if (_assets.EffectMesh("debris") is { } original)
                heap.AddChild(new MeshInstance3D
                {
                    Mesh = original,
                    Position = ActorView.World(wreck.Position),
                    Rotation = new Vector3(0, Between(0, Mathf.Tau), 0),
                });
            for (var i = _assets.EffectMesh("debris") is null ? Math.Clamp(4 + wreck.Quantity / 25, 4, 14) : 0; i > 0; i--)
            {
                var angle = Between(0, Mathf.Tau);
                var at = wreck.Position + new System.Numerics.Vector2(MathF.Cos(angle), MathF.Sin(angle)) * Between(0, spread);
                var piece = Between(0.3f, 0.7f) * spread;
                heap.AddChild(new MeshInstance3D
                {
                    Mesh = new BoxMesh { Size = new Vector3(piece, piece * Between(0.2f, 0.5f), piece * Between(0.4f, 1)) },
                    MaterialOverride = i % 3 == 0 ? charred : metal,
                    Position = ActorView.World(at) + Vector3.Up * piece * 0.1f,
                    Rotation = new Vector3(Between(-0.5f, 0.5f), Between(0, Mathf.Tau), Between(-0.5f, 0.5f)),
                });
            }
            var middle = ActorView.World(wreck.Position);
            var fire = Effects.DamageFire(spread * 0.8f, spread);
            fire.Position = middle + Vector3.Up * 0.3f;
            fire.Emitting = World.Time - wreck.CreatedAt < GameWorld.DebrisBurnTime;
            heap.AddChild(fire);
            var smoke = Effects.Plume(4, 3, spread * 3, spread * 1.2f, new Color(0.1f, 0.1f, 0.1f, 0.7f));
            smoke.Position = middle + Vector3.Up;
            heap.AddChild(smoke);
            AddChild(heap);
            var crackle = _audio.Loop("Crackle");
            fire.AddChild(crackle.Player);
            if (fire.Emitting)
                crackle.Play("SID_FIRE");
            _debris[wreck.Id] = (heap, fire, crackle);
        }
        foreach (var (id, (view, fire, crackle)) in _debris.ToList())
        {
            if (World.FindDebris(id) is not { } wreck)
            {
                view.QueueFree();
                _debris.Remove(id);
            }
            else if (fire.Emitting && World.Time - wreck.CreatedAt >= GameWorld.DebrisBurnTime)
            {
                fire.Emitting = false;
                crackle.Play(null);
            }
        }
    }

    // The land mines on the ground, by mine.
    private readonly Dictionary<int, Node3D> _mines = [];

    /// <summary>
    /// Land mines lie where the spies left them, seen by everyone (MachPhysLandMine): the original's model when it
    /// is exported (fx/lmine), otherwise a small dark plate with a red light.
    /// </summary>
    private void SyncLandMines()
    {
        foreach (var mine in World.LandMines.Where(m => !_mines.ContainsKey(m.Id)))
        {
            Node3D view;
            if (_assets.EffectMesh("lmine") is { } mesh)
                view = new MeshInstance3D { Mesh = mesh, Name = $"Mine{mine.Id}" };
            else
            {
                view = new MeshInstance3D
                {
                    Name = $"Mine{mine.Id}",
                    Mesh = new CylinderMesh { TopRadius = 0.45f, BottomRadius = 0.55f, Height = 0.18f },
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(0.12f, 0.12f, 0.11f), Metallic = 0.6f, Roughness = 0.5f },
                };
                view.AddChild(new MeshInstance3D
                {
                    Mesh = new SphereMesh { Radius = 0.08f, Height = 0.16f },
                    Position = Vector3.Up * 0.1f,
                    MaterialOverride = new StandardMaterial3D { AlbedoColor = new Color(1, 0.15f, 0.1f), EmissionEnabled = true, Emission = new Color(1, 0.1f, 0.05f), EmissionEnergyMultiplier = 2 },
                });
            }
            view.Position = ActorView.World(mine.Position) + Vector3.Up * 0.05f;
            AddChild(view);
            _mines[mine.Id] = view;
        }
        foreach (var id in _mines.Keys.Where(id => !World.LandMines.Exists(m => m.Id == id)).ToList())
        {
            _mines[id].QueueFree();
            _mines.Remove(id);
        }
    }

    /// <summary>The wreck the cursor's ray meets before anything else, if any.</summary>
    private DebrisState? DebrisAt(Vector2 screen)
    {
        if (Fog?.Blind == true)
            return null;
        var camera = GetViewport().GetCamera3D();
        var (from, direction) = (camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        var nearest = HeightField.Current?.Hit(from, direction) is { } ground ? from.DistanceTo(ground) + 1 : camera.Far;
        DebrisState? best = null;
        foreach (var (id, (view, _, _)) in _debris)
        {
            if (ActorView.PartsHit(view, from, direction) is { } distance && distance < nearest)
            {
                nearest = distance;
                best = World.FindDebris(id);
            }
        }
        return best;
    }

    private readonly List<int> _selection = [];
    private double _accumulator;

    // The tick the world should have reached by the clock, and how far behind it may fall before it gives up on
    // catching the time back (after a long wait it just carries on).
    private long _clockTick;
    private const int MaxCatchUp = 10;

    private ClassicAssets _assets = null!;
    private CameraRig _rig = null!;
    private Hud _hud = null!;
    private Effects _effects = null!;
    private SupportEffects _support = null!;

    // How much ore each machine carried at the last tick, to see transporters load and unload.
    private readonly Dictionary<int, int> _oreCarried = [];

    // Where each tracked or wheeled machine left its last tread mark.
    private readonly Dictionary<int, Vector3> _lastMark = [];

    // Actors destroyed this tick, whose views are blown apart rather than removed.
    private readonly HashSet<int> _destroyed = [];
    private readonly HashSet<int> _vaporised = [];
    private readonly HashSet<int> _swallowed = [];
    private GameAudio _audio = null!;
    private PlanetLook _planetLook = null!;
    private FirstPersonScreen _screen = null!;

    // Drag selection
    private Vector2? _dragStart;

    // Building placement
    private (string Type, string? SubType, int Level)? _placing;
    private int _placingOrientation;
    private Node3D? _ghost;
    private StandardMaterial3D? _ghostMaterial;
    private bool _ghostPrinted;

    // Area weapons going off, drawn as growing spheres
    private readonly Dictionary<Blast, MeshInstance3D> _blastViews = [];
    private readonly HashSet<Blast> _groundBlasts = [];

    // First person
    private Machine? _driven;
    private Camera3D _firstPersonCamera = null!;
    private float _lookPitch;

    public GameWorld World { get; private set; } = null!;

    /// <summary>After each tick of the world, whatever the frame rate (a film gives its orders here).</summary>
    public event Action? Ticked;

    /// <summary>The game's sounds (interface sounds included).</summary>
    public GameAudio Audio => _audio;

    /// <summary>The player's fog of war; null when watching a replay.</summary>
    public FogOfWar? Fog { get; private set; }
    public IReadOnlyList<int> Selection => _selection;
    public bool Paused { get; set; }

    /// <summary>Gives the player's command to the world (in multiplayer: to every player's world, same tick).</summary>
    public void Issue(Command command)
    {
        if (!Watching)
            _session.Queue(command);
    }

    /// <summary>
    /// Gives machines (or buildings) an order the player has finished choosing, with the original's click for it:
    /// MachGuiCommand::apply played taskmach.wav each time a command went out.
    /// </summary>
    /// <param name="answer">The report that answers it, in place of what its kind of command answers.</param>
    public void Order(Command command, string? answer = null)
    {
        if (RefusesOrders())
            return;
        Issue(ThroughCommander(command));
        if (!Watching)
        {
            _audio.PlayGui("gui/sounds/taskmach.wav");
            if (answer is not null)
                _audio.Say(answer);
            else
                Answer(command);
        }
    }

    /// <summary>The first of the machines given an order answers as its command in the original did (machgui's cmd*.cpp).</summary>
    private void Answer(Command command)
    {
        void First(IEnumerable<int> ids, string report)
        {
            if (ids.Select(World.Find<Machine>).OfType<Machine>().FirstOrDefault() is { } first && SoundTable.VoiceOf(first) is { } voice)
                _audio.Say($"VID_{voice}_{report}", speaker: first.Id);
        }
        void Plain(IReadOnlyList<int> ids, string mail)
        {
            if (ids.Count > 0)
                _audio.Say(mail);
        }
        switch (command)
        {
            case MoveCommand c: First(c.Machines, "MOVING"); break;
            case FollowCommand c: First(c.Machines, "MOVING"); break;
            case AttackMoveCommand c: First(c.Machines, "MOVING"); break;
            case AttackCommand c: First(c.Machines, "TARGET_ENEMY"); break;
            case HealCommand c: First(c.Machines, "HEAL_TARGET"); break;
            case PatrolCommand c: First(c.Machines, "TASKED"); break;
            case StandGroundCommand c: First(c.Machines, "TASKED"); break;
            case RefillLandMinesCommand c: First(c.Spies, "TASKED"); break;
            case RecycleCommand c: First(c.Machines, "RECYCLE"); break;
            case SelfDestructCommand c: First(c.Actors, "SELF_DESTRUCT"); break;
            case ConstructCommand c: Plain(c.Constructors, "VID_DOZER_MOVE_TO_SITE"); break;
            case RepairCommand c: Plain(c.Constructors, "VID_DOZER_MOVE_TO_SITE"); break;
            case CaptureCommand c: Plain(c.Constructors, "VID_DOZER_MOVE_TO_SITE"); break;
            case DeconstructCommand c: Plain(c.Constructors, "VID_DOZER_MOVE_TO_SITE"); break;
            case LocateCommand c: Plain(c.Locators, "VID_GEO_LOCATING"); break;
            case DropLandMinesCommand c: Plain(c.Spies, "VID_SPY_TASKED_LAY_MINE"); break;
            case DeployCommand c: Plain(c.Apcs, "VID_APC_MOVING_TO_DEPLOY_POINT"); break;
            case ScavengeCommand c: Plain(c.Scavengers, "VID_RESOURCE_CARRIER_SCAVENGING"); break;
            case EnterLabCommand c when c.Technicians.Select(World.Find<Machine>).Any(m => m?.IsSpy == true):
                Plain(c.Technicians, "VID_SPY_TASKED_INFLITRATE");
                break;
            case EnterLabCommand c: First(c.Technicians, "MOVING"); break;
            // A carrier already on a route only says it heard (MachGuiTransportCommand::doApply).
            case TransportCommand c when c.Carriers.Select(World.Find<Machine>).FirstOrDefault()?.Order is TransportOrder:
                First(c.Carriers, "TASKED");
                break;
            case TransportCommand c: Plain(c.Carriers, "VID_RESOURCE_CARRIER_TRANSPORTING"); break;
        }
    }

    /// <summary>
    /// While a game on this machine is paused, no orders go out: the original refused the command with clickbad.wav
    /// (MachInGameScreen::activeCommand while the sim was suspended).
    /// </summary>
    public bool RefusesOrders()
    {
        if (!Paused || Online || Watching)
            return false;
        _audio.PlayGui("gui/sounds/clickbad.wav");
        return true;
    }

    /// <summary>A replay: the player watches and cannot give orders.</summary>
    public bool Watching => MatchRequest.Current.Replay is not null;

    public bool Online => MatchRequest.Current.Network is not null;

    private LockstepSession _session = null!;
    private int _seed;
    private double _waitingFor;
    private bool _replaySaved;
    private bool _musicEnded;

    /// <summary>The track sceninfo.dat names for a scenario (its MUSIC), if any.</summary>
    private static int? ScenarioMusic(string scenario)
    {
        var catalogue = System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat");
        return File.Exists(catalogue)
            ? ScenarioCatalogueParser.Load(catalogue).AllScenarios.FirstOrDefault(s => s.Name.Equals(scenario, StringComparison.OrdinalIgnoreCase))?.Music
            : null;
    }

    public override void _Ready()
    {
        if (PerfLog.Create() is { } perf)
            AddChild(perf);
        var request = MatchRequest.Current;
        var data = Paths.Data;
        // A film plays the same battle each time it is recorded.
        _seed = request.Seed ?? (Cinematic.Asked is not null ? 1 : (int)(Time.GetUnixTimeFromSystem() % int.MaxValue));
        World = MatchSetup.Create(data, request.Scenario, request.Planet, request.Starts.Players, _seed, request.Options);
        GiftForChecks();

        // Every match runs through lockstep: alone with one tick of input delay, online with the relay's, or
        // played back from a replay.
        var humans = request.Starts.Players.Where(p => p.Value == Controller.Player).Select(p => p.Key);
        _session = request.Replay?.Playback(World)
            ?? request.Saved?.Resume(World, Player)
            ?? new LockstepSession(World, Player, humans, request.Network?.InputDelay ?? 1,
                firstLocalTurn: request.ResumeFrom, echoed: request.Network is not null);
        if (request.Network is not null)
            _session.Outgoing += NetLink.Send;

        _assets = new ClassicAssets(Paths.Root);
        ClassicGui.Use(_assets.GameFolder);
        var map = _assets.PlanetMap(request.Planet);
        var surface = World.Planet.Surface;
        HeightField.Current = _assets.PlanetHeights(request.Planet) is { } heights
            ? HeightField.FromExport(heights, surface.Width, surface.Depth)
            : new HeightField(surface);
        // The simulation stops shots at the same ground, from the same game copy. A match played together, watched
        // or carried on uses the ground it was played with (MatchInfo.Terrain): online, every player's alike or none.
        var recorded = request.Replay?.Match ?? request.Saved?.Match ?? request.Network;
        if (_assets.PlanetHeights(request.Planet) is { } ground && TerrainHeights.FromExport(ground) is var terrain
            && (recorded is null || recorded.Terrain == terrain.Fingerprint()))
            World.Terrain = terrain;
        var groundMissing = recorded?.Terrain is { } needed && World.Terrain?.Fingerprint() != needed;
        // A saved game: the world plays its recording through to where it was saved before anything is shown.
        if (request.Saved is { } saved)
        {
            while (World.Tick < saved.Ticks && _session.TryStep())
            {
            }
            _wiredCounted = World.Wired is { Phase: Machines.Sim.Survival.WiredPhase.Ended };
            if (_scriptLoaded)
                GD.Print($"hangar: caught up with the save at tick {World.Tick}, checksum {World.Checksum():x16}");
        }
        // The ground's and the grass's shaders read the weather's values (wind, wetness): they must exist first.
        Weather.EnsureGlobals();
        AddChild(TerrainBuilder.Build(World.Planet, HeightField.Current, map, _assets.PlanetTerrain(request.Planet)));
        if (data.LoadScenario(request.Scenario).Artefacts is { } artefacts)
            AddChild(Artefacts(artefacts));
        AddLighting();
        // The weather follows what the camera looks at (or the camera, driving), and its own time is the match's.
        if (Weather.Create(request.Planet, _seed, () => World.Time, () => _driven is not null ? _firstPersonCamera.GlobalPosition : _rig.GlobalPosition,
                () => _driven is not null ? 30 : _rig.Distance) is { } weather)
            AddChild(weather);

        _effects = new Effects { Name = "Effects" };
        AddChild(_effects);
        _support = SupportEffects.Create(_assets);
        AddChild(_support);
        _audio = GameAudio.Create(_assets.GameFolder);
        _projectiles = ProjectileViews.Create(World, _assets, id => _views.GetValueOrDefault(id), _effects, _audio);
        AddChild(_projectiles);

        _rig = new CameraRig { Name = "CameraRig" };
        AddChild(_rig);
        var start = data.LoadScenario(request.Scenario).Race(Player)?.Camera;
        _rig.Setup(start is null ? new Vector3(400, 0, 400) : new Vector3(start.Position.X, 0, start.Position.Y),
            new Vector2(World.Planet.Surface.Width, World.Planet.Surface.Depth));
        // A scenario may start in the ground camera (MachCameras::initialise).
        if (start?.Type == "GROUND")
            UseZenithCamera(false);
        _startView = _rig.Snapshot;

        // The machine being driven sits on its own render layer, which this camera leaves out.
        _firstPersonCamera = new Camera3D { Far = 3000, Near = 0.1f, Fov = 70, Name = "FirstPersonCamera", CullMask = ~ActorView.DrivenLayer & 0xFFFFF };
        AddChild(_firstPersonCamera);

        AddChild(_audio);
        if (data.LoadScenario(request.Scenario).Artefacts is { } humming)
            ArtefactHums(humming);
        // A film gets its music afterwards (tools/cinematic/render.sh).
        if (Cinematic.Asked is null)
            Music.Match(this, ScenarioMusic(request.Scenario));

        _screen = FirstPersonScreen.Create(_planetLook, _assets.Texture("models/texture4/static.bmp"));
        AddChild(_screen);
        _hud = new Hud(this, map);
        AddChild(_hud);
        SetUpRun();
        if (groundMissing)
            _hud.Message("This match was played on the classic export's ground, which this game copy lacks: it will not play out the same");
        _cursor = new ClassicCursor { Name = "Cursor" };
        AddChild(_cursor);

        // The fog of war is the player's: a replay shows everything. Automated screenshots look anywhere, so
        // they leave it out unless MACHINES_FOG=1 asks for it.
        var screenshot = System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT") is { Length: > 0 };
        if (!Watching && Cinematic.Asked is null && World.Options.FogOfWar && (!screenshot || System.Environment.GetEnvironmentVariable("MACHINES_FOG") == "1"))
        {
            // The original uncovered and tested its map image, one pixel at a time.
            Fog = new FogOfWar(World, Player, map is null ? 6 : surface.Width / map.GetWidth());
            AddChild(Fog);
        }

        SyncViews();
        if (World.Wired is null || !_assets.HasClassicModels)
            _hud.Message(_assets.HasClassicModels
                ? "Classic models loaded from your game copy."
                : "No classic models found (see tools/classic-export); showing simple shapes.");

        // A film hides the panel and films the match with its own camera; a match shows the film look the options ask for.
        if (Cinematic.Asked is not null)
        {
            _hud.Visible = false;
            var film = new Cinematic(World, id => _views.GetValueOrDefault(id), _projectiles.ViewOf, _rig);
            Ticked += film.OnTick;
            AddChild(film);
        }
        else
        {
            _filmLook = new FilmLook();
            AddChild(_filmLook);
            _filmLook.Match();
        }
    }

    private FilmLook? _filmLook;
    private ProjectileViews _projectiles = null!;

    /// <summary>
    /// The options' film look and depth of field on the camera in use: the strategy camera sharp around the ground it
    /// looks at, the first-person view sharp up to the middle distance.
    /// </summary>
    private void ShowFilmLook()
    {
        if (_filmLook is null)
            return;
        _filmLook.Visible = GameOptions.FilmLook;
        var blur = (float)GameOptions.DepthOfField;
        if (_driven is not null)
            FilmLook.Focus(_firstPersonCamera, 45, blur, near: false);
        else
            FilmLook.Focus(_rig.Camera, _rig.Distance, blur);
    }

    /// <summary>Sun, sky and fog from the planet's .env file, following the game clock.</summary>
    /// <summary>
    /// The scenery and destructible objects the scenario sets on the planet (ARTEFACTS: trees, rocks, wrecks,
    /// ruins), each at its place and turned as the scenario says, standing on the ground. Each one's model is kept
    /// by its place in the list (<see cref="_artefactModels"/>), so the active ones vanish when they are destroyed.
    /// </summary>
    private Node3D Artefacts(ArtefactSection section)
    {
        var root = new Node3D { Name = "Artefacts" };
        if (section.ModelsPath is not { } models)
            return root;
        Vector3 At(Vector2 position, float z) => new(position.X, HeightField.Current?.At(position.X, position.Y) ?? z, position.Y);
        var placements = section.Placements.Select((placement, index) => (Placement: placement, Index: index)).ToList();
        if (GraphicsSettings.Scenery)
        {
            // The remake's trees and rocks stand in for the original's (Scenery); the rest keeps its model.
            var (scenery, left) = Scenery.Place(placements, p =>
                (p.Placement.TypeName, At(new Vector2(p.Placement.Position.X, p.Placement.Position.Y), p.Placement.Position.Z),
                    p.Placement.Orientation, _assets.ArtefactMesh(models, p.Placement.TypeName)));
            root.AddChild(scenery);
            // Scenery adds one model for each placement it took, in order.
            foreach (var (taken, model) in placements.Except(left).Zip(scenery.GetChildren().OfType<Node3D>()))
                _artefactModels[taken.Index] = model;
            placements = left;
        }
        foreach (var (placement, index) in placements)
        {
            if (_assets.ArtefactMesh(models, placement.TypeName) is not { } mesh)
                continue;
            var at = At(new Vector2(placement.Position.X, placement.Position.Y), placement.Position.Z);
            var model = new MeshInstance3D
            {
                Name = placement.TypeName,
                Mesh = mesh,
                Transform = new Transform3D(new Basis(Vector3.Up, -Mathf.DegToRad(placement.Orientation)), at),
                // The red moons' lava pools glow and creep in the remake, as the lava on the ground does.
                MaterialOverride = GraphicsSettings.Scenery && placement.TypeName.Equals("lava", StringComparison.OrdinalIgnoreCase) ? Scenery.Lava() : null,
            };
            if (PlanetLook.PaintedShadows && _assets.ArtefactShadow(models, placement.TypeName) is { } plate)
                model.AddChild(new MeshInstance3D { Mesh = plate, Name = "Shadow", MaterialOverride = PlanetLook.ShadowPlate, CastShadow = GeometryInstance3D.ShadowCastingSetting.Off });
            root.AddChild(model);
            _artefactModels[index] = model;
        }
        return root;
    }

    // The artefacts' models by their place in the scenario's list (an artefact's id less Artefact.IdBase).
    private readonly Dictionary<int, Node3D> _artefactModels = [];
    private readonly Dictionary<int, ArtefactDamage> _artefactDamage = [];

    /// <summary>The machinery among the artefacts hums for as long as it stands (MachLogArtefacts::newPhysArtefact).</summary>
    private void ArtefactHums(ArtefactSection section)
    {
        foreach (var (index, model) in _artefactModels)
        {
            if (section.Placements[index].TypeName.ToLowerInvariant() is not ("cmain" or "tile22" or "control" or "cool1" or "power" or "contrl" or "shield" or "cdom"))
                continue;
            var hum = _audio.Loop("Hum");
            model.AddChild(hum.Player);
            hum.Play("SID_" + section.Placements[index].TypeName.ToUpperInvariant());
        }
    }

    /// <summary>What is hit and left standing shows it, once the game has its picture of it (MachPhysArtefact::damageLevel).</summary>
    private void SyncArtefacts()
    {
        foreach (var artefact in World.Artefacts)
        {
            if (artefact.HitPoints >= artefact.MaxHitPoints || artefact.IsDead || !_artefactModels.TryGetValue(artefact.Placement, out var model))
                continue;
            if (!_artefactDamage.TryGetValue(artefact.Placement, out var damage))
                _artefactDamage[artefact.Placement] = damage = ArtefactDamage.Create(model, ArtefactBox(model), _assets.EffectMesh("groundsplat"));
            damage.Show(100 * (1 - artefact.HitPoints / (float)artefact.MaxHitPoints));
        }
    }

    private static Aabb ArtefactBox(Node3D model) => model is MeshInstance3D { Mesh: { } mesh } ? mesh.GetAabb()
        : ActorView.MeshBounds(model, Transform3D.Identity) ?? new Aabb(-Vector3.One, Vector3.One * 2);

    /// <summary>
    /// An artefact blows up, in the classic look as a building without pieces of its own does
    /// (MachPhysArtefact::destroy, MachPhysObjectExplosion::explode).
    /// </summary>
    private void BlowUpArtefact(int placement, Node3D model, Vector3 at)
    {
        if (_artefactDamage.Remove(placement, out var damage))
            damage.QueueFree();
        var box = ArtefactBox(model);
        _audio.Play(SoundTable.ExplosionSound(true, box.Size.Length(), Random.Shared.Next(1, 7)), at);
        if (GraphicsSettings.Effects)
        {
            _effects.Destroyed(at, null);
            model.QueueFree();
            return;
        }
        var place = model.GlobalTransform;
        Blowup.Building(place, box, _assets, _effects);
        Blowup.Swell(model, model, _effects, glow: false);
        Blowup.Demolish(place, box, "", _assets, _effects, hot: false);
    }

    /// <summary>
    /// The standing artefact whose model the cursor's ray meets before the ground and anything else, if any: the
    /// attack command can be aimed at it, and the prompt line names it.
    /// </summary>
    private Artefact? ArtefactAt(Vector2 screen)
    {
        if (Fog?.Blind == true)
            return null;
        var camera = GetViewport().GetCamera3D();
        var (from, direction) = (camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        var nearest = HeightField.Current?.Hit(from, direction) is { } ground ? from.DistanceTo(ground) + 1 : camera.Far;
        Artefact? best = null;
        foreach (var artefact in World.Artefacts)
        {
            if (!_artefactModels.TryGetValue(artefact.Placement, out var model) || model is not VisualInstance3D shape)
                continue;
            var box = shape.GlobalTransform * shape.GetAabb();
            if (box.IntersectsSegment(from, from + direction * camera.Far) && from.DistanceTo(box.GetCenter()) < nearest)
            {
                nearest = from.DistanceTo(box.GetCenter());
                best = artefact;
            }
        }
        return best;
    }

    private void AddLighting()
    {
        // For automated checks: MACHINES_CLOCK=<seconds> moves the planet's day on by that much, "noon" to the sun's highest.
        var setting = System.Environment.GetEnvironmentVariable("MACHINES_CLOCK");
        var clock = float.TryParse(setting, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c
            : setting == "noon" ? PlanetLook.Noon(World.Planet.Environment) : 0;
        var planet = MatchRequest.Current.Planet;
        _planetLook = PlanetLook.Create(World.Planet.Environment, planet, () => World.Time + clock, () => _driven is not null ? 0 : _rig?.Distance ?? 160,
            satellite => _assets.PlanetSky(planet, satellite), _assets.PlanetSkyDome(planet), _assets.PlanetSkyline(planet));
        AddChild(_planetLook);
    }

    // ---- simulation loop ------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        ReadNetwork();
        if (Online && World.Winner is null && _session.Ready(CatchUpLead) >= CatchUpLead)
        {
            CatchUp();
        }
        else if ((!Paused || Online) && (World.Winner is null ? !MatchOver || Online : !Watching && !MatchOver))
        {
            _accumulator += Math.Min(delta, 0.25);
            while (_accumulator >= GameWorld.Dt)
            {
                _accumulator -= GameWorld.Dt;
                // The clock: one tick per 50 ms of real time. A world that fell behind it (waiting for a
                // player's turns) catches up a little faster, but never runs ahead of it.
                _clockTick = Math.Max(_clockTick + 1, World.Tick);
                _clockTick = Math.Min(_clockTick, World.Tick + MaxCatchUp);
                UpdateDriving();
                _session.Pump();
                var stepped = 0;
                while (stepped < 3 && World.Tick < _clockTick && _session.TryStep())
                {
                    stepped++;
                    foreach (var rejected in World.Rejected.Where(r => r.Command.Race == Player))
                    {
                        if (rejected.Command is WiredCommand)
                            WiredRefused(rejected.Command, rejected.Reason);
                        else
                            _hud.Message(rejected.Reason);
                    }
                    ShowEvents();
                    ShowRunEvents();
                    SyncBlasts();
                    SyncViews();
                    Ticked?.Invoke();
                    // For automated checks: MACHINES_CHECKSUM_AT=<tick> prints the world's checksum at that tick.
                    if (System.Environment.GetEnvironmentVariable("MACHINES_CHECKSUM_AT") is { } at && at.Split(',').Contains(World.Tick.ToString()))
                        GD.Print($"checksum at tick {World.Tick}: {World.Checksum():x16}");
                }
                _waitingFor = stepped > 0 ? 0 : _waitingFor + GameWorld.Dt;
            }
            if (_waitingFor > 1)
            {
                var names = _session.Waiting().Select(race => MatchRequest.Current.Network?.Players.FirstOrDefault(p => p.Slot == race)?.Name ?? race.ToString());
                _hud.Banner(Lang.T("Waiting for {0}", string.Join(", ", names)));
            }
        }

        if (_session.DesyncTick is { } desync)
            _hud.Banner(Lang.T("Out of sync since tick {0}: the players' games differ", desync));
        else if (World.Winner is { } winner && Watching)
            _hud.Banner(Lang.T("{0} wins", Lang.T(Look(winner).ToString())));
        else if (MatchOver && World.Wired is null && ClassicGui.Bitmap(EndPicture) is null)
            _hud.Banner(World.Winner == Player ? "Victory" : "Defeat");
        else if (_waitingFor <= 1)
            _hud.Banner(null);
        // A run ends on its own screen, and misc/defeat.bmp has its word in English.
        _hud.Picture(MatchOver && World.Wired is null ? EndPicture : Paused && !Online && _menu is null ? "misc/paused.bmp" : null);
        // Once the match is decided every sound stops; two seconds on the world stands still under the picture of how
        // it ended, and after 19 seconds won, 14 lost or 5 in training, or at Esc or Space, the statistics
        // (MachGuiCtxStatistics) come up over it (MachInGameScreen::gameState); after a campaign mission its film of
        // victory or defeat, then its debriefing.
        if (World.Winner is not null || World.Races[Player].Defeated && !Watching || World.Wired is { Phase: Machines.Sim.Survival.WiredPhase.Ended })
        {
            var now = Time.GetTicksMsec() / 1000.0;
            if (_decidedAt is null)
            {
                RecordCampaign();
                RecordWired();
                foreach (var sound in FindChildren("*", nameof(AudioStreamPlayer3D), true, false).OfType<AudioStreamPlayer3D>())
                    sound.Stop();
                _audio.Hush();
            }
            _decidedAt ??= now;
            RetryWired(now);
            var wait = World.Wired is not null ? WiredHold : Watching || Training ? 5 : World.Winner == Player ? 19 : 14;
            // The cursor is gone while the world stands still (MachInGameScreen::update).
            if (MatchOver && !_statisticsShown && _driven is null)
                Input.MouseMode = Input.MouseModeEnum.Hidden;
            if (!_statisticsShown && (now - _decidedAt > wait || _endSkipped) && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT") is not { Length: > 0 } && Cinematic.Asked is null)
            {
                _statisticsShown = true;
                Input.MouseMode = Input.MouseModeEnum.Visible;
                // A run has no winner, so nothing below keeps its recording.
                if (World.Wired is not null)
                    SaveReplay();
                CloseMenu();
                if (Campaign.IsCampaign(MatchRequest.Current) && !Watching)
                    ShowCampaignEnding();
                else
                    OpenMenu(statistics: Watching, debriefing: !Watching);
            }
        }
        // The tunes of victory and defeat come with the picture; a training mission ends without (MachGuiStartupScreens).
        if (!_musicEnded && MatchOver)
        {
            _musicEnded = true;
            if (!Training)
                Music.End(this, World.Winner == Player);
        }
        if (World.Winner is not null)
            SaveReplay();

        FollowRun(delta);
        UpdateGhost();
        UpdateFirstPersonCamera();
        ShowFilmLook();
        ShowInteriors();
        ShowHealing();
        if (Fog is not null)
        {
            Fog.Update(delta, new NVector2(_rig.Position.X, _rig.Position.Z), _driven is not null);
            _audio.Muted = Fog.Blind;
        }
        UpdateHover();
        UpdateBand();
        _cursor.Kind = CursorKind();
        _hud.Refresh();
        TakeScreenshotIfAsked(delta);
    }

    /// <summary>A world this many ticks behind the turns it has (rejoining a running match) fast-forwards.</summary>
    private const int CatchUpLead = 60;

    /// <summary>
    /// Runs as many ticks as fit in a frame, without sounds or effects, and draws only where they end: a player
    /// rejoining a match rebuilds the whole match so far this way.
    /// </summary>
    private void CatchUp()
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        while (clock.ElapsedMilliseconds < 30)
        {
            _session.Pump();
            if (!_session.TryStep())
                break;
        }
        _accumulator = 0;
        _waitingFor = 0;
        SyncBlasts();
        SyncViews();
        var resume = MatchRequest.Current.ResumeFrom is { } from && World.Tick < from ? Lang.T(" of {0}", from) : "";
        _hud.Banner(Lang.T("Catching up: tick {0}{1}", World.Tick, resume));
    }

    private double _runTime;

    /// <summary>
    /// For automated checks: MACHINES_SCREENSHOT=&lt;file.png&gt; saves the window after MACHINES_SCREENSHOT_AFTER
    /// seconds (default 4) and quits. MACHINES_SCREENSHOT_FOCUS=x,y moves the camera there first.
    /// </summary>
    private void TakeScreenshotIfAsked(double delta)
    {
        FollowHangar();
        ScriptHangar();
        var path = System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT");
        if (string.IsNullOrEmpty(path))
            return;
        _rig.EdgePan = false;
        ScriptRefit();
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_FOCUS") is { } focus)
        {
            // "-,-,pitch,distance" keeps the camera where it starts.
            var parts = focus.Split(',');
            if (parts[0] != "-")
                _rig.Position = new Vector3(float.Parse(parts[0], System.Globalization.CultureInfo.InvariantCulture), 0,
                    float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture));
            if (parts.Length >= 4)
            {
                _rig.Pitch = float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture);
                _rig.Distance = float.Parse(parts[3], System.Globalization.CultureInfo.InvariantCulture);
                if (parts.Length >= 5)
                    _rig.Yaw = Mathf.DegToRad(float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture));
            }
        }
        // MACHINES_SCREENSHOT_ARTEFACT=0.1: every artefact down to that share of its hit points, the camera on the nearest.
        if (_runTime == 0 && float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_ARTEFACT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var artefactShare))
        {
            foreach (var artefact in World.Artefacts)
                artefact.HitPoints = (int)(artefact.MaxHitPoints * artefactShare);
            var (camera, standing) = (new NVector2(_rig.Position.X, _rig.Position.Z), World.Artefacts.Where(a => !a.IsDead));
            if (standing.MinBy(a => NVector2.Distance(a.Position, camera)) is { } nearest)
                _rig.Position = ActorView.World(nearest.Position);
        }
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_BLAST") is { Length: > 0 } blastAt)
        {
            // e.g. "NUCLEAR_MISSILE,800,800": sets the weapon off there at once; "BEE_BOMB,-,-" where the camera looks.
            var parts = blastAt.Split(',');
            var weapon = World.Data.Parm.Weapons.First(w => w.TypeName == parts[0]);
            World.StartBlast(weapon, parts[1] == "-"
                ? new NVector2(_rig.Position.X, _rig.Position.Z)
                : new NVector2(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                    float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)), null);
        }
        // e.g. "AGGRESSOR,ASSASSIN,2,L_PLASMA_RIFLE": puts one of those down at the camera, to drive.
        // "Blue:AGGRESSOR,GRUNT,2,L_BOLTER" puts down an enemy instead, 25 m east (straight ahead), to watch a fight;
        // several go apart with ';'. Two more numbers move it that many metres east and south: "...,L_BOLTER,-10,0".
        foreach (var spawn in _runTime == 0 ? System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_SPAWN")?.Split(';') ?? [] : [])
        {
            var race = spawn.Split(':') is [var name, _] && Enum.TryParse<Race>(name, out var other) ? other : Player;
            var parts = spawn[(spawn.IndexOf(':') + 1)..].Split(',');
            var at = new NVector2(_rig.Position.X, _rig.Position.Z) + (race == Player ? NVector2.Zero : new NVector2(25, 0));
            if (parts.Length > 5)
                at += new NVector2(float.Parse(parts[4], System.Globalization.CultureInfo.InvariantCulture), float.Parse(parts[5], System.Globalization.CultureInfo.InvariantCulture));
            var spawned = World.AddMachine(race, at, parts[0], parts[1] is "" or "-" ? null : parts[1], int.Parse(parts[2]), parts.Length > 3 && parts[3] is not ("" or "-") ? parts[3] : null);
            // MACHINES_SCREENSHOT_KILL=1 destroys the enemies put down at once, to look at their wrecks.
            if (race != Player && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_KILL") == "1")
                spawned.HitPoints = 0;
            SyncViews();
        }
        // MACHINES_SCREENSHOT_HURT=0.08: every machine down to that share of its hit points.
        if (_runTime == 0 && float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_HURT"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var hurtShare))
            foreach (var machine in World.Machines)
                machine.HitPoints = Math.Max(1, (int)(machine.MaxHitPoints * hurtShare));
        // e.g. "HARDWARE_LAB,CIVILIAN,1": a finished building of the player's where it fits nearest the camera, a lab with
        // something to research and the player's technicians going in.
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_BUILT")?.Split(',') is [var builtType, var builtSub, var builtLevel])
        {
            var (sub, level, centre) = (builtSub is "" or "-" ? null : builtSub, int.Parse(builtLevel), new NVector2(_rig.Position.X, _rig.Position.Z));
            var spot = Enumerable.Range(0, 900).Select(i => centre + new NVector2(i % 30 - 15, i / 30 - 15) * 8)
                .OrderBy(p => NVector2.Distance(p, centre)).First(p => Enumerable.Range(0, 25).All(j => World.Nav.IsFree(p + new NVector2(j % 5 - 2, j / 5 - 2) * 12, crossesWater: false)));
            var built = World.AddConstruction(Player, spot, builtType, sub, level, 0, complete: true);
            SyncViews();
            if (built.IsType("HARDWARE_LAB") && World.Races[Player].Available.FirstOrDefault(k => World.WhyCannotResearch(Player, k, built) is null) is { } item)
                Issue(new ResearchCommand(Player, built.Id, item));
            if (built.IsType("HARDWARE_LAB"))
                Issue(new EnterLabCommand(Player, World.Machines.Where(m => m.Race == Player && m.Spec.Type == "TECHNICIAN").Select(m => m.Id).ToList(), built.Id));
        }
        // e.g. "punchA,3": every machine with that animation plays it once, 3 s in (after loading), to look at it.
        if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_PLAY")?.Split(',') is [var play, var playAt]
            && double.Parse(playAt, System.Globalization.CultureInfo.InvariantCulture) is var startAt
            && _runTime < startAt && _runTime + delta >= startAt)
        {
            foreach (var view in _views.Values)
                view.Play(play);
        }
        PlayScriptedInput();
        // e.g. "MINE,-,1": the player is placing that building, at MACHINES_SCREENSHOT_POINT.
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_GHOST")?.Split(',') is [var ghostType, var ghostSub, var ghostLevel])
            BeginPlacement(ghostType, ghostSub is "" or "-" ? null : ghostSub, int.Parse(ghostLevel));
        // MACHINES_SCREENSHOT_TRANSPORT=1: the player's carriers shuttle ore from their first mine to their first smelter or pod.
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_TRANSPORT") == "1"
            && World.Constructions.FirstOrDefault(c => c.Race == Player && c.IsType("MINE")) is { } shuttleMine
            && World.Constructions.FirstOrDefault(c => c.Race == Player && c.Spec.Type is "SMELTER" or "POD") is { } shuttleStore)
            Issue(new TransportCommand(Player, World.Machines.Where(m => m.Race == Player && m.Spec.Type == "RESOURCE_CARRIER").Select(m => m.Id).ToList(),
                shuttleMine.Id, shuttleStore.Id));
        // MACHINES_SCREENSHOT_MINES=1: the player's spies lay their land mines in a row east of the camera.
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MINES") == "1")
            Issue(new DropLandMinesCommand(Player, World.Machines.Where(m => m.Race == Player && m.IsSpy).Select(m => m.Id).ToList(),
                Enumerable.Range(1, 4).Select(i => new NVector2(_rig.Position.X + 6 * i, _rig.Position.Z + 4)).ToList()));
        // MACHINES_SCREENSHOT_SITES=1: the player has found every mineral site.
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_SITES") == "1")
        {
            foreach (var site in World.Sites)
                site.DiscoveredBy.Add(Player);
            SyncOreHolograms();
        }
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_PLACE") is { Length: > 0 } place)
        {
            // e.g. "GARRISON,-,1": the player's constructors start that building where it fits nearest the camera.
            var parts = place.Split(',');
            var (type, sub, level) = (parts[0], parts[1] is "" or "-" ? null : parts[1], int.Parse(parts[2]));
            var constructors = World.Machines.Where(m => m.Race == Player && m.Spec.Type == "CONSTRUCTOR").Select(m => m.Id).ToList();
            var centre = new NVector2(_rig.Position.X, _rig.Position.Z);
            var spot = Enumerable.Range(0, 400).Select(i => centre + new NVector2(i % 20 - 10, i / 20 - 10) * 8)
                .OrderBy(p => NVector2.Distance(p, centre))
                .FirstOrDefault(p => World.WhyCannotPlace(Player, type, sub, level, p, 0) is null, centre);
            Issue(new PlaceConstructionCommand(Player, constructors, type, sub, level, spot, 0));
        }
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_SELECT") is { Length: > 0 } select)
        {
            // e.g. "CONSTRUCTOR" or "AGGRESSOR"; with MACHINES_SCREENSHOT_FIRSTPERSON=1 the first one is driven.
            // A building type (FACTORY, HARDWARE_LAB ...) selects the first such building instead.
            var machines = World.Machines.Where(m => m.Race == Player && (m.Spec.Type == select || m.Spec.SubType == select)).OrderByDescending(m => m.Id).Select(m => m.Id).ToList();
            Select(machines.Count > 0 ? machines : World.Constructions.Where(c => c.Race == Player && c.Spec.Type == select).Take(1).Select(c => c.Id).ToList());
            // MACHINES_SCREENSHOT_MENU=build,2: the selected constructors' build menu, scrolled down two rows.
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU")?.Split(',') is ["build", .. var scroll])
                _hud.Panel.OpenBuildMenu(scroll.Length > 0 ? int.Parse(scroll[0]) : 0);
            // MACHINES_SCREENSHOT_SQUAD=3: the selection becomes squadron 3; MACHINES_SCREENSHOT_MENU=squads shows the bank.
            if (int.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_SQUAD"), out var squad))
                FormSquadron(squad);
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") is "squads" or "form")
                _hud.Panel.OpenSquadrons(forming: System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "form");
            // MACHINES_SCREENSHOT_MENU=cover: the self-destruct button's cover is open; navm or navc: a navigator.
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "cover")
                _hud.Panel.OpenSelfDestructCover();
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") is "navm" or "navc")
                _hud.Panel.OpenNavigator(machines: System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "navm");
            // MACHINES_SCREENSHOT_MENU=menus or stats: the in-game menus, or the statistics; hotkeys: the hot keys page.
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") is "menus" or "stats" or "hotkeys")
                OpenMenu(statistics: System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "stats");
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "hotkeys")
                _menu!.ShowHotkeys();
            // MACHINES_SCREENSHOT_MENU=defeat: the player's race has lost, to look at the end of a match.
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "defeat")
                World.Races[Player].Defeated = true;
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_FIRSTPERSON") == "1")
            {
                Select(_selection.Take(1).ToList());
                // MACHINES_SCREENSHOT_DOOR=12 sets it down that many metres before the door of the nearest building
                // it can go into, looking in, to drive through (with MACHINES_SCREENSHOT_KEYS=W).
                if (float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_DOOR"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var before)
                    && SelectedMachines().FirstOrDefault() is { } walker
                    && World.Constructions.Where(c => World.CanEnter(walker, c)).MinBy(c => NVector2.Distance(c.Position, walker.Position)) is { } door)
                {
                    var inward = NVector2.Normalize(door.EntranceInside!.Value - door.EntranceOutside!.Value);
                    walker.Position = door.Door!.Value - inward * before;
                    walker.Facing = MathF.Atan2(inward.Y, inward.X);
                }
                EnterFirstPerson();
                // MACHINES_SCREENSHOT_GOGGLES=1 puts the night vision goggles on.
                if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_GOGGLES") == "1")
                    _screen.ToggleGoggles();
                // MACHINES_SCREENSHOT_LOOK=-20 tilts the view that many degrees (down when negative); "-20,60" also
                // turns the upper body 60 degrees (to the right when positive).
                var looks = System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_LOOK")?.Split(',') ?? [];
                if (looks.Length > 0 && float.TryParse(looks[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var look))
                    _lookPitch = Mathf.DegToRad(look);
                if (looks.Length > 1 && float.TryParse(looks[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var head))
                {
                    _headTarget = Mathf.DegToRad(head);
                    if (_driven is { } aimed)
                        _aimYaw = aimed.Facing + _headTarget;
                }
                // MACHINES_SCREENSHOT_KEYS=A,Q holds those keys down from the start, to drive for a picture.
                foreach (var name in System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_KEYS")?.Split(',') ?? [])
                    if (System.Enum.TryParse<Key>(name, ignoreCase: true, out var held))
                        Input.ParseInputEvent(new InputEventKey { Keycode = held, PhysicalKeycode = held, Pressed = true });
            }
        }
        // MACHINES_SCREENSHOT_MENU=briefing or debrief (with MACHINES_CAMPAIGN): a campaign mission's briefing over the
        // match after MACHINES_SCREENSHOT_AFTER less a second, or its debriefing.
        if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") is "briefing" or "debrief" && _menu is null
            && _runTime >= (double.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_AFTER"), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var menuAt) ? menuAt : 4) - 1)
        {
            var debrief = System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "debrief";
            OpenMenu(statistics: false, debriefing: debrief);
            if (!debrief)
                _menu?.ShowBriefing();
        }
        // MACHINES_SCREENSHOT_MOVE=x,y,at sends the selection to (x, y) that many seconds in; "attack,at" sends it at the
        // nearest enemy instead, "build,at" to the nearest of the player's unfinished buildings, "enter,at" into the nearest
        // building that has an inside.
        if (!_moveSent && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MOVE")?.Split(',') is { Length: >= 2 } move
            && double.TryParse(move[^1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var moveAt)
            && _runTime >= moveAt)
        {
            _moveSent = true;
            var movers = SelectedMachines().ToList();
            var middle = movers.Count > 0 ? movers.Aggregate(NVector2.Zero, (sum, m) => sum + m.Position) / movers.Count : NVector2.Zero;
            var ids = movers.Select(m => m.Id).ToList();
            if (move[0] == "attack" && World.Actors.Where(a => !a.IsDead && World.AreEnemies(Player, a.Race)).MinBy(a => NVector2.Distance(a.Position, middle)) is { } enemy)
                Issue(new AttackCommand(Player, ids, enemy.Id));
            else if (move[0] == "build" && World.Constructions.Where(c => c.Race == Player && !c.Complete).MinBy(c => NVector2.Distance(c.Position, middle)) is { } site)
                Issue(new ConstructCommand(Player, ids, site.Id));
            else if (move[0] == "board" && World.Machines.Where(m => m.Race == Player && m.Spec.Type == "APC").MinBy(m => NVector2.Distance(m.Position, middle)) is { } apc)
                Issue(new BoardCommand(Player, ids, apc.Id));
            else if (move[0] == "enter" && World.Constructions.Where(c => c.Race == Player && c.HasInterior).MinBy(c => NVector2.Distance(c.Position, middle)) is { } lab)
                foreach (var mover in movers)
                    Issue(mover.Spec.Type == "TECHNICIAN" && lab.IsType("HARDWARE_LAB") ? new EnterLabCommand(Player, [mover.Id], lab.Id) : new EnterGarrisonCommand(Player, [mover.Id], lab.Id));
            else if (move.Length >= 3 && float.TryParse(move[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var x)
                && float.TryParse(move[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var y))
                Issue(new MoveCommand(Player, ids, new NVector2(x, y)));
        }
        // MACHINES_SCREENSHOT_CHAT=<said>|<typed> in a multiplayer match: says one to everyone, then types the other.
        if (_runTime == 0 && Online && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_CHAT")?.Split('|') is [var said, var typed])
            _hud.Chat.Automate(said, typed);
        _runTime += delta;
        var after = double.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_AFTER"),
            System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var s) ? s : 4;
        if (_runTime < after)
            return;
        // MACHINES_SCREENSHOT_FRAMES=<n> keeps n frames in a row (path-0.png ...), to see what flickers.
        if (int.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_FRAMES"), out var frames) && frames > 1)
        {
            GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.ChangeExtension(path, null) + $"-{_framesKept}.png");
            if (++_framesKept < frames)
                return;
        }
        else
            GetViewport().GetTexture().GetImage().SavePng(path);
        SaveReplay();
        GetTree().Quit();
    }

    private int _framesKept;
    private bool _moveSent;

    private bool _viewsSynced;

    // The machines in an APC, which teleport out when they leave it.
    private readonly HashSet<int> _aboard = [];

    private void SyncViews()
    {
        foreach (var actor in World.Actors)
        {
            // Captured or turned actors are rebuilt in their new colours.
            Race? turned = null;
            if (_views.TryGetValue(actor.Id, out var view) && view.Race != actor.Race)
            {
                turned = view.Shown;
                view.QueueFree();
                _views.Remove(actor.Id);
                _selection.Remove(actor.Id);
                view = null;
            }
            if (view is null)
            {
                view = ActorView.Create(actor, _assets, _audio, HeroOf(actor));
                _views[actor.Id] = view;
                AddChild(view);
                view.SplatMesh = _assets.EffectMesh("groundsplat");
                view.FirePictures = _support.FirePictures;
                // A machine new to the world, from a factory or as reinforcements, teleports in.
                if (_viewsSynced && turned is null && actor is Machine)
                    view.TeleportIn();
                // A machine turned traitor flickers between its old and new colours.
                if (turned is { } old && actor is Machine)
                    view.Defect(old, actor, _assets);
                // A building clears the grass and stones from the ground it stands on.
                if (actor is Construction placed && GetNodeOrNull<GroundScatter>("Terrain/GroundScatter") is { } scatter)
                    scatter.ClearUnder(new Vector2(placed.Footprint.Min.X - 1, placed.Footprint.Min.Y - 1),
                        new Vector2(placed.Footprint.Max.X + 1, placed.Footprint.Max.Y + 1));
            }
            if (actor is Machine machine)
                ShowRefit(view, machine);
            // Upper bodies and towers' heads turn to what they attack, ordered or of their own choice, and to
            // whom they heal; one driven in first person turns where its driver looks.
            view.Sync(actor, actor switch
            {
                Machine { Order: AttackOrder attack } => World.Find(attack.Target),
                Machine { Order: HealOrder heal } => World.Find(heal.Target),
                _ => World.AimOf(actor.Id),
            });
            if (actor is Machine)
                view.Moving(World.MoveOf(actor.Id), World.Time);
            if (actor is Machine { DrivenByPlayer: true } driven)
                view.UpperBodyAngle = driven.Id == _driven?.Id ? _headTarget : World.PlayerHeads.GetValueOrDefault(driven.Id);
            // In the remake tracks and wheels leave their marks on the ground behind them.
            if (GraphicsSettings.Effects && actor is Machine { Inside: null, Stats.Locomotion: Locomotion.Tracks or Locomotion.Wheels } rolling)
            {
                var here = ActorView.World(rolling.Position);
                if (!_lastMark.TryGetValue(rolling.Id, out var last) || last.DistanceTo(here) > 4)
                    _lastMark[rolling.Id] = here;
                else if (last.DistanceTo(here) >= 1.2f)
                {
                    _effects.TreadMark((last + here) / 2, view.Yaw, view.Bounds.Size.Z * 0.9f, last.DistanceTo(here) + 0.15f);
                    _lastMark[rolling.Id] = here;
                }
            }
            if (actor is Machine { Inside: null } && _aboard.Remove(actor.Id))
                view.TeleportIn();
            // A geo locator scans the ground as it goes.
            if (actor is Machine { Spec.Type: "GEO_LOCATOR" } locator)
                view.Scan(locator.Order is LocateOrder ? _assets.EffectMesh("locator") : null, locator.Stats.ScannerRange);
            // A transporter loading or unloading ore glows with rings of light (MachPhysResourceCarrier::doLoading).
            if (actor is Machine carrier)
            {
                if (_oreCarried.TryGetValue(carrier.Id, out var carried) && carried != carrier.Ore)
                {
                    if (carrier.IsScavenger)
                        _support.Scavenge(view);
                    else
                        _support.Loading(view);
                    _audio.Play("SID_RESOURCE_PICKUP", view.Centre);
                }
                _oreCarried[carrier.Id] = carrier.Ore;
            }
            // Constructors at work reach out to the building with beams and sparks.
            if (actor is Machine { Spec.Type: "CONSTRUCTOR" } constructor)
                view.Construct(World.WorkedOn(constructor) is { } site ? ActorView.World((site.Footprint.Min + site.Footprint.Max) / 2) + Vector3.Up * 2 : null,
                    _assets.EffectMesh("torch"), _assets.EffectMesh("sparks"), _assets.EffectMesh("groundlight"));
            // Technicians in a lab research with their arms while it has work (MachLogHardwareLab's MachLogResearchAnimation).
            if (actor is Machine { Spec.Type: "TECHNICIAN" } technician)
                view.Working = technician.Inside is { } workplace && World.Find<Construction>(workplace) is { } lab
                    && lab.Technicians.Contains(technician.Id) && World.IsWorking(lab);
            if (actor is Construction building)
            {
                view.Working = World.IsWorking(building);
                view.IonTop = building.Weapons.Any(w => w.Spec.TypeName == "ION_ORBITAL_CANNON");
                view.Hum = building.Complete ? SoundTable.BuildingSound(building.Spec.Type, building.Spec.SubType, building.Stats.HardwareLevel, World.IsWorking(building)) : null;
                if (view.Dome(building.DomeRaised))
                    _audio.Play(building.DomeRaised ? "SID_MISSDOMEOPEN" : "SID_MISSDOMECLOSE", ActorView.World(building.Position));
                // The doors open for its own machines going through or waiting before them.
                var doorsOpen = building.EntranceOutside is { } door && World.Machines.Any(m => (m.Inside is null || m.DrivenByPlayer)
                    && (m.Doorway == building.Id || m.Race == building.Race && NVector2.DistanceSquared(m.Position, door) < 10 * 10));
                if (doorsOpen != view.DoorsOpen)
                    _audio.Play("SID_DOOR", ActorView.World(building.EntranceOutside ?? building.Position));
                view.DoorsOpen = doorsOpen;
            }
        }
        _viewsSynced = true;
        SyncOreHolograms();
        SyncDebris();
        SyncLandMines();

        foreach (var id in _views.Keys.Where(id => World.Find(id) is null).ToList())
        {
            // A destroyed machine flies apart, a building swells and bursts; anything else just goes.
            if (_destroyed.Remove(id))
                _views[id].BlowUp(_effects, _assets);
            else if (_vaporised.Remove(id))
                _views[id].Vaporise();
            else if (_swallowed.Remove(id))
                _views[id].Swallow();
            _views[id].QueueFree();
            _views.Remove(id);
            _selection.Remove(id);
            if (_driven?.Id == id)
                _screen.Die(DriverLost);
        }
        if (_driven is { } lost && lost.Race != Player)
            _screen.Die(DriverLost);
        SyncArtefacts();
    }

    private void ShowEvents()
    {
        foreach (var e in World.Events)
        {
            var at = ActorView.World(e.Position) + Vector3.Up * 2;
            switch (e.Kind)
            {
                case GameEventKind.Fired:
                    // The punch is no shot: the Gorilla swings, and its blast follows when the fists land.
                    if (e.Detail == "GORILLA_PUNCH")
                    {
                        if (_views.TryGetValue(e.Actor, out var puncher))
                            puncher.Punch();
                        if (World.Find(e.Actor)?.Race == Player)
                            _audio.Say("VID_NINJA_SMASH");
                    }
                    // Flying rounds are drawn in flight (ProjectileViews); the others streak to their mark. Both
                    // flash at the weapon's muzzle.
                    else if (World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail) is { } spec && GameWorld.Flies(spec))
                    {
                        var muzzle = MuzzleOf(e.Actor, spec) ?? at;
                        _effects.Shot(muzzle, muzzle, e.Detail ?? "", spec.ProjectileSpeed, spec);
                    }
                    // The electric charger strikes with lightning, and a halo runs over its victim.
                    else if (e.Detail == "ELECTRIC_CHARGE" && e.Other is { } struck && World.Find(struck) is { } shocked
                             && World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail) is { } charge)
                    {
                        var victimView = _views.GetValueOrDefault(struck);
                        var (muzzle, aim) = (MuzzleOf(e.Actor, charge) ?? at, victimView?.Centre ?? ActorView.World(shocked.Position) + Vector3.Up * 1.5f);
                        _support.Lightning(muzzle, aim, victimView);
                        _effects.ChargerFires(muzzle, aim, Blast.Extras(charge)[2]);
                        if (victimView is not null)
                            _effects.FaceplateSmoke(victimView, Blast.Extras(charge)[1]);
                    }
                    // In a Wired run a round from first person strays, and the simulation says where it went.
                    else if (e.Mark is { } strayed && World.Find(e.Actor) is { } straying
                             && World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail) is { } stray)
                    {
                        var muzzle = MuzzleOf(e.Actor, stray) ?? at;
                        var mark = new Vector3(strayed.X, strayed.Z, strayed.Y) + _sightLift;
                        var reach = NVector2.Distance(straying.Position, new NVector2(strayed.X, strayed.Y));
                        Shot(muzzle, reach > stray.Range ? muzzle + (mark - muzzle) * (stray.Range / reach) : mark, e.Detail ?? "");
                    }
                    // Fired in first person at the point under the crosshair: to that point, as far as the weapon
                    // reaches along the line of sight (a hill in the way draws it with the Impact below).
                    else if (World.PlayerAims.TryGetValue(e.Actor, out var sight) && sight.Height is { } sightHeight
                             && World.Find(e.Actor) is { } sighting && !Blast.IsArea(e.Detail ?? "")
                             && World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail) is { } sighted
                             && !World.Events.Any(x => x.Kind == GameEventKind.Impact && x.Actor == e.Actor && x.Detail == e.Detail))
                    {
                        var muzzle = MuzzleOf(e.Actor, sighted) ?? at;
                        var mark = new Vector3(sight.Point.X, sightHeight, sight.Point.Y);
                        var reach = NVector2.Distance(sighting.Position, sight.Point);
                        Shot(muzzle, reach > sighted.Range ? muzzle + (mark - muzzle) * (sighted.Range / reach) : mark, e.Detail ?? "");
                    }
                    else if (e.Other is { } target && World.Find(target) is { } victim)
                    {
                        var weapon = World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail);
                        Shot(weapon is null ? at : MuzzleOf(e.Actor, weapon) ?? at, ActorView.World(victim.Position) + Vector3.Up * 1.5f, e.Detail ?? "");
                    }
                    // The commander's launcher has a sound of its own (MachPhysMultiLauncher::newMissile).
                    _audio.Play(e.Detail == "MULTI_LAUNCHER5" && World.Find<Machine>(e.Actor)?.IsType("ADMINISTRATOR", "COMMANDER") == true
                        ? "SID_MLAUNCH8" : SoundTable.FiringSound(e.Detail ?? ""), at);
                    break;
                case GameEventKind.Hit when e.Detail is { } weapon:
                    _hud.Panel.Hit(e.Actor);
                    // A flamethrower sets the machine it hits alight.
                    if (weapon.StartsWith("FLAME_THROWER", StringComparison.Ordinal) && World.Find<Machine>(e.Actor) is not null
                        && _views.TryGetValue(e.Actor, out var burning))
                    {
                        _support.Burn(burning);
                        burning.Crackle(15);
                    }
                    // In the classic look a bolt strikes sparks on what it hits, as does the vortex on what it does not
                    // kill; the remake's effects have the tracer's.
                    if (!GraphicsSettings.Effects && (weapon.Contains("BOLTER") || weapon == "AUTO_CANNON" || weapon == "VORTEX" && World.Find(e.Actor) is { IsDead: false })
                        && _views.TryGetValue(e.Actor, out var sparking))
                        _support.BoltSparks(sparking, weapon == "VORTEX" ? VortexCentre(sparking) : e.Other is { } from && _views.TryGetValue(from, out var shooter) ? shooter.Centre : sparking.Centre);
                    if (!GraphicsSettings.Effects)
                        ClassicVictim(e, weapon);
                    // Bolts, plasma and lightning shake the machine they hit.
                    if (SoundTable.ShakesMachine(weapon) && World.Find<Machine>(e.Actor) is not null && _views.TryGetValue(e.Actor, out var shaken))
                        shaken.Shake();
                    // The machine being driven, hit, now and then crackles in the driver's ears (one hit in twenty).
                    if (e.Actor == _driven?.Id && _screen.Hit(_driven.Stats.NightVision))
                        _audio.PlayGui("gui/sounds/machhit.wav");
                    _audio.Play(SoundTable.HitSound(weapon, Random.Shared.Next(1, 5)), at);
                    break;
                case GameEventKind.Blast when e.Detail is { } weapon:
                    _audio.Play(SoundTable.BlastSound(weapon), at);
                    // The nuke and the punch shake the earth (MachPhysNuclearBomb, MachPhysPunchBlast).
                    if (weapon == "NUCLEAR_MISSILE")
                        CameraRig.Shake(at, 15, 2);
                    else if (weapon == "GORILLA_PUNCH")
                        CameraRig.Shake(at, 15, 0.6f);
                    break;
                case GameEventKind.Destroyed when e.Actor >= Artefact.IdBase:
                    // An artefact: its model goes in the explosion, which is as big as it was.
                    if (_artefactModels.Remove(e.Actor - Artefact.IdBase, out var standing))
                        BlowUpArtefact(e.Actor - Artefact.IdBase, standing, at);
                    break;
                case GameEventKind.Recycled:
                    _vaporised.Add(e.Actor);
                    _audio.Play("SID_RECYCLE", at);
                    break;
                case GameEventKind.Destroyed:
                    _audio.SpeakerLost(e.Actor);
                    var vaporised = e.Detail == "ION_ORBITAL_CANNON";
                    var swallowed = e.Detail == "VORTEX";
                    (vaporised ? _vaporised : swallowed ? _swallowed : _destroyed).Add(e.Actor);
                    if (!vaporised && !swallowed)
                        _effects.Destroyed(at - Vector3.Up * 2, _views.GetValueOrDefault(e.Actor));
                    if (e.Detail == "SELF_DESTRUCT" && _views.TryGetValue(e.Actor, out var charged) && !charged.IsBuilding)
                        _audio.Play("SID_MACHINE_SELF_DESTRUCT", at);
                    if (_views.TryGetValue(e.Actor, out var dead))
                    {
                        if (!vaporised && !swallowed)
                            _audio.Play(SoundTable.ExplosionSound(dead.IsBuilding, dead.Size, Random.Shared.Next(1, 7)), at);
                        // A big building blowing up shakes the earth for two seconds (MachPhysObjectExplosion).
                        if (dead.Quake is { } degrees && !vaporised && !swallowed)
                            CameraRig.Shake(at, degrees, 2);
                    }
                    break;
                case GameEventKind.MachineBuilt when World.Find<Machine>(e.Actor) is { } built && built.Race == Player:
                    // In a Wired run the squad strip shows who has come.
                    if (World.Wired is null)
                        _hud.Message(Lang.T("Machine ready: {0}", e.Detail));
                    if (SoundTable.BuiltReport(built) is { } builtReport)
                        _audio.Say(builtReport);
                    break;
                case GameEventKind.ConstructionComplete when World.Find(e.Actor)?.Race == Player:
                    _hud.Message(Lang.T("Construction complete: {0}", e.Detail));
                    _audio.Say("VID_DOZER_BUILDING_COMPLETE");
                    break;
                case GameEventKind.ResearchComplete when World.Find(e.Actor)?.Race == Player:
                    _hud.Message(Lang.T("Research complete: {0}", e.Detail));
                    _audio.Say("VID_TECHNICIAN_RESEARCH_COMPLETE");
                    break;
                case GameEventKind.Boarded:
                    _aboard.Add(e.Actor);
                    _views.GetValueOrDefault(e.Actor)?.TeleportOut();
                    _audio.Play("SID_ENTERED_APC", at);
                    if (World.Find(e.Actor)?.Race == Player)
                        _audio.Say("VID_APC_UNIT_BOARDED");
                    break;
                case GameEventKind.Deployed when World.Find(e.Actor)?.Race == Player:
                    _hud.Message(Lang.T("APC deployed {0} machines", e.Detail));
                    _audio.Say("VID_APC_DEPLOYMENT_COMPLETE");
                    break;
                // A round the ground stopped: one that flies ends in its splat as it vanishes (ProjectileViews); a
                // bolt or shell streaks into the hillside and bursts there (MachPhys::ON_TERRAIN).
                case GameEventKind.Impact when e.Detail is { } weapon:
                    if (World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == weapon) is { } stopped && !GameWorld.Flies(stopped))
                    {
                        var ground = ActorView.World(e.Position) + Vector3.Up * 0.5f;
                        Shot(MuzzleOf(e.Actor, stopped) ?? ground, ground, weapon);
                        _effects.Impact(ground);
                        _audio.Play(SoundTable.HitSound(weapon, Random.Shared.Next(1, 5)), ground);
                    }
                    break;
                // A spy has stolen a piece of research (VID_SPY_RESEARCH_DOWNLOADED).
                case GameEventKind.ResearchStolen when World.Find(e.Actor)?.Race == Player:
                    _hud.Message(Lang.T("Research stolen: {0}", e.Detail));
                    _audio.Say("VID_SPY_RESEARCH_DOWNLOADED");
                    break;
                // A spy drops a mine with a click (SID_MINE_DROP) and it arms itself (SID_LMINE_DEPLOY, MachPhysLandMine); one
                // going off throws up a blast under the machine.
                case GameEventKind.MineLaid:
                    _audio.Play("SID_MINE_DROP", at);
                    _audio.Play("SID_LMINE_DEPLOY", at);
                    break;
                case GameEventKind.MineExploded:
                {
                    var blast = ActorView.World(e.Position);
                    if (GraphicsSettings.Effects)
                        _effects.Explosion(blast, 3);
                    else
                        _effects.Impact(blast);
                    _audio.Play(SoundTable.ExplosionSound(false, 3, Random.Shared.Next(1, 7)), blast);
                    CameraRig.Shake(blast, 4, 0.4f);
                    break;
                }
                // A scavenger gathers the wreck (MachLogResourceCarrier::doLoading).
                case GameEventKind.Scavenged:
                    if (_views.TryGetValue(e.Actor, out var scavenger))
                    {
                        _support.Scavenge(scavenger);
                        _audio.Play("SID_RESOURCE_PICKUP", at);
                    }
                    break;
                case GameEventKind.SiteDiscovered when e.Detail == Player.ToString():
                    _hud.Message("Mineral site found");
                    _audio.Say("VID_GEO_MINERAL_LOCATED");
                    break;
                case GameEventKind.VoiceMail when e.Other == (int)Player && e.Detail is { } mail:
                    // The mission script's mails wait their turn rather than being dropped.
                    _audio.Say(mail, queue: true, speaker: e.Actor);
                    break;
                // A change of alliance is news to the two players it is between (MachGuiDispositionChangeNotifiable).
                case GameEventKind.AllianceChanged when e.Actor == (int)Player:
                    _hud.Message(Lang.T(e.Detail == "ALLY" ? "Allied with {0}" : "No longer allied with {0}", PlayerName((Race)e.Other!.Value)));
                    break;
                case GameEventKind.AllianceChanged when e.Other == (int)Player:
                    _hud.Message(Lang.T(e.Detail == "ALLY" ? "{0} is now allied with you" : "{0} is no longer allied with you", PlayerName((Race)e.Actor)));
                    break;
                // With Broadcast Alliances on, a network game hears of the others' too, but never of computers among themselves.
                case GameEventKind.AllianceChanged when Online && World.Options.BroadcastAlliances
                                                        && (World.Races[(Race)e.Actor].Controller != Controller.Ai || World.Races[(Race)e.Other!.Value].Controller != Controller.Ai):
                    _hud.Message(Lang.T(e.Detail == "ALLY" ? "{0} is now allied with {1}" : "{0} is no longer allied with {1}", PlayerName((Race)e.Actor), PlayerName((Race)e.Other!.Value)));
                    break;
                case GameEventKind.Explosion:
                    // The mission script's explosion (MachLogExplosionAction), as big as its SIZE; a big one shakes the view.
                    var blastSize = float.TryParse(e.Detail, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsedSize) ? parsedSize : 10;
                    _effects.Destroyed(at - Vector3.Up * 2, null);
                    _audio.Play(SoundTable.ExplosionSound(true, blastSize, Random.Shared.Next(1, 7)), at);
                    if (blastSize >= 20)
                        CameraRig.Shake(at, Math.Min(15, blastSize / 4), 2);
                    break;
            }
        }
    }

    // Shots fired by each shooter's weapons so far, so that twin weapons and barrels take turns.
    private readonly Dictionary<(int, string), int> _shotsShown = [];

    /// <summary>
    /// The muzzle a shooter's weapon of this kind fires from now, its twins and barrels in turn; that weapon
    /// recoils.
    /// </summary>
    private Vector3? MuzzleOf(int shooter, Machines.Import.Model.WeaponSpec weapon)
    {
        if (World.Find(shooter) is not { } actor || !_views.TryGetValue(shooter, out var view))
            return null;
        var mounts = (actor switch { Machine m => m.Weapons, Construction c => c.Weapons, _ => [] })
            .Where(w => w.Spec.TypeName == weapon.TypeName).ToList();
        if (mounts.Count == 0)
            return null;
        if (HeroOf(actor) is not null)
            mounts = HeroGuns.Firing(mounts);
        var shot = _shotsShown.GetValueOrDefault((shooter, weapon.TypeName));
        _shotsShown[(shooter, weapon.TypeName)] = shot + 1;
        // The weapon the round leaves recoils with it.
        var mounting = mounts[shot % mounts.Count].Mounting;
        view.Fire(mounting, weapon);
        return view.Muzzle(mounting, weapon, shot / mounts.Count);
    }

    /// <summary>
    /// Healers in reach of a damaged machine they are ordered to heal (as the simulation heals: within 95 % of the
    /// charger's range) draw their beam from the charger to it, and it glows with the healing crosses.
    /// </summary>
    private void ShowHealing()
    {
        var healing = new List<(int, Vector3, ActorView)>();
        foreach (var healer in World.Machines)
        {
            if (healer.Order is not HealOrder heal || World.Find<Machine>(heal.Target) is not { } patient
                || healer.Weapons.FirstOrDefault(w => w.IsHealer) is not { } charger
                || patient.HitPoints >= patient.MaxHitPoints || patient.Inside is not null
                || healer.DistanceTo(patient) > charger.Spec.Range * 0.95f + 0.5f
                || !_views.TryGetValue(healer.Id, out var healerView) || !_views.TryGetValue(patient.Id, out var patientView))
                continue;
            healing.Add((healer.Id, healerView.Muzzle(charger.Mounting, charger.Spec, 0) ?? healerView.Centre, patientView));
        }
        _support.ShowHealing(healing);
    }

    /// <summary>Building interiors, and the machines in them, show only to first person looking out from inside them.</summary>
    private void ShowInteriors()
    {
        Vector3? eye = _driven is not null ? _firstPersonCamera.GlobalPosition : null;
        var seen = new HashSet<int>();
        foreach (var (id, view) in _views)
            if (view.HasInterior && view.ShowInteriorFrom(eye, _driven?.Inside == id))
                seen.Add(id);
        foreach (var machine in World.Machines)
            if (_views.TryGetValue(machine.Id, out var inside))
            {
                inside.SeenInside = machine.Inside is { } building && seen.Contains(building);
                inside.Floor = inside.SeenInside && _views.TryGetValue(machine.Inside!.Value, out var around) ? around.Position.Y : null;
            }
        // In there the planet is a world away (the original's interior domain): no ground comes up through the floor,
        // no rain comes through the roof.
        foreach (var outside in new[] { "Terrain", "Weather" })
            if (GetNodeOrNull<Node3D>(outside) is { } planet)
                planet.Visible = seen.Count == 0;
    }

    private void SyncBlasts()
    {
        _groundBlasts.RemoveWhere(b => !World.Blasts.Contains(b));
        foreach (var blast in World.Blasts.Where(b => !_blastViews.ContainsKey(b) && !_groundBlasts.Contains(b)))
        {
            // The punch and the bee bomb tear the ground open, as the original's own effects; they burn out
            // by themselves, after the blast's damage has been done.
            var spec = World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == blast.Weapon);
            if (GroundBlast.Create(_assets, blast, spec is null ? [] : Blast.Extras(spec)) is { } ground)
            {
                _effects.AddChild(ground);
                _groundBlasts.Add(blast);
                // The remake leaves the ground burnt where the ion cannon and the nuke struck.
                if (GraphicsSettings.Effects && blast.Weapon is "ION_ORBITAL_CANNON" or "NUCLEAR_MISSILE")
                    _effects.Scorch(ActorView.World(blast.Position), blast.Weapon == "NUCLEAR_MISSILE" ? 120 : 50);
                continue;
            }
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
                CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
                MaterialOverride = new StandardMaterial3D
                {
                    // The remake's dome glows (added light, seen through); the classic one is a tinted shell.
                    AlbedoColor = GraphicsSettings.Effects && blast.Weapon != "VORTEX" ? new Color(colour.R, colour.G, colour.B, colour.A * 0.6f) : colour,
                    BlendMode = GraphicsSettings.Effects && blast.Weapon != "VORTEX" ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            _effects.AddChild(view);
            view.GlobalPosition = ActorView.World(blast.Position);
            _blastViews[blast] = view;
            if (GraphicsSettings.Effects)
                _effects.Blast(view.GlobalPosition, blast.Radius(blast.StartTime + blast.Duration), new Color(colour.R, colour.G, colour.B));
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

    /// <summary>A shot: the remake's flying bolt at the weapon's speed, or the classic short beam.</summary>
    // Where the vortex nearest a victim is, whose line to it the sparks stand on.
    private Vector3 VortexCentre(ActorView victim) =>
        World.Blasts.Where(b => b.Weapon == "VORTEX").Select(b => ActorView.World(b.Position)).OrderBy(c => c.DistanceSquaredTo(victim.Centre))
            .Cast<Vector3?>().FirstOrDefault() ?? victim.Centre;

    // What the classic look's weapons do to what they hit besides: a pulse bolt lights it up for a moment
    // (MachPhysPulseWeapon::applyVictimAnimation), and a bolt strikes sparks on an artefact as on anything else.
    private void ClassicVictim(GameEvent e, string weapon)
    {
        var artefact = e.Actor >= Artefact.IdBase ? _artefactModels.GetValueOrDefault(e.Actor - Artefact.IdBase) : null;
        if (weapon.Contains("PULSE") && artefact is not null)
            _support.LightUp(artefact, ArtefactDamage.Meshes(artefact));
        else if (weapon.Contains("PULSE") && _views.TryGetValue(e.Actor, out var victim))
            _support.LightUp(victim, victim.Shape());
        if (artefact is not null && (weapon.Contains("BOLTER") || weapon == "AUTO_CANNON"))
        {
            var (box, shooter) = (ArtefactBox(artefact), e.Other is { } from ? _views.GetValueOrDefault(from) : null);
            var centre = artefact.GlobalTransform * box.GetCenter();
            _support.BoltSparks(centre, box.Size, shooter?.Centre ?? centre);
        }
    }

    private void Shot(Vector3 from, Vector3 to, string weapon)
    {
        var spec = World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == weapon);
        _effects.Shot(from, to, weapon, spec?.ProjectileSpeed ?? 80, spec);
        if (GraphicsSettings.Effects)
            return;
        _support.BolterFlash(from, (to - from).Normalized(), weapon, spec is null ? 0.1f : Effects.RoundLit(spec));
        if (spec is not null && weapon.StartsWith("FLAME_THROWER", StringComparison.Ordinal))
            _support.FlameBalls(from, to, spec);
    }

    // ---- input ----------------------------------------------------------------------------------

    /// <summary>
    /// Ctrl and F1 to F3 (MachPromptText::doHandleKeyEvent): the player takes that other human player for an ally,
    /// or an ally no longer; nothing for a player who is not there. The others count as in the chat's F1 to F3.
    /// </summary>
    private void ToggleAlliance(int index)
    {
        if (MatchChat.Opponents().ElementAtOrDefault(index) is not { } opponent || !World.Races.TryGetValue(Player, out var own))
            return;
        Issue(new AllianceCommand(Player, opponent.Slot, !own.Allies.Contains(opponent.Slot)));
    }

    /// <summary>Hands the relay's messages to the session and tells the player what happened.</summary>
    private static string PlayerName(Race race) =>
        MatchRequest.Current.Network?.Players.FirstOrDefault(p => p.Slot == race)?.Name ?? race.ToString();

    private void ReadNetwork()
    {
        if (!Online || NetLink.Connection is not { } connection)
            return;
        while (connection.Inbox.TryRead(out var message))
        {
            _session.Receive(message);
            switch (message)
            {
                // Rejoining, the match so far arrives too; only what happens from now on is news.
                case PlayerLeft left when left.FromTick >= (MatchRequest.Current.ResumeFrom ?? 0):
                    _hud.Message(Lang.T("{0} left the match; the AI takes over", PlayerName(left.Race)));
                    break;
                case PlayerReturned back when back.Race != Player && back.FromTick >= (MatchRequest.Current.ResumeFrom ?? 0):
                    _hud.Message(Lang.T("{0} is back and takes over from the AI", PlayerName(back.Race)));
                    break;
                case Chat chat:
                    _hud.Chat.Receive(chat);
                    break;
            }
        }
        if (connection.Closed && _session.CanStep is false)
            _hud.Banner("Connection to the relay lost");
    }

    /// <summary>
    /// Keeps the match as a replay in <c>replays/</c> next to the game: the setup and every turn, which the
    /// deterministic simulation turns back into the same match.
    /// </summary>
    private void SaveReplay()
    {
        if (_replaySaved || Watching || World.Tick == 0)
            return;
        _replaySaved = true;
        try
        {
            var folder = System.IO.Path.Combine(Paths.Root, "replays");
            Directory.CreateDirectory(folder);
            var match = MatchRequest.Current.ToMatchInfo(_seed, _session.InputDelay, World.Terrain?.Fingerprint());
            var file = System.IO.Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm}_{match.Scenario}{Replay.Extension}");
            new Replay(match, _session.Played, World.Tick, _session.Presence).Save(file);
            _hud.Message(Lang.T("Replay saved: {0}", System.IO.Path.GetFileName(file)));
        }
        catch (IOException exception)
        {
            _hud.Message(Lang.T("Replay not saved: {0}", exception.Message));
        }
    }

    // ---- the in-game menus (MachGuiCtxInGameOptions) ------------------------------------------

    /// <summary>Where saved games go, as replays that end where the game was saved.</summary>
    public static string SaveFolder => System.IO.Path.Combine(Paths.Root, "saves");

    public const string SaveExtension = ".msave";

    private InGameMenu? _menu;
    private bool _pausedBeforeMenu;

    // When the match was decided, and whether its statistics have come up.
    private double? _decidedAt;
    private bool _statisticsShown;

    // Esc or Space cut the wait for the statistics short.
    private bool _endSkipped;

    /// <summary>The game stands still two seconds after it was decided (MachInGameScreen::actualGameState).</summary>
    public bool MatchOver => _decidedAt is { } at && !Watching && Time.GetTicksMsec() / 1000.0 - at > 2;

    /// <summary>A mission of the campaign's first system (MachGuiDbScenario::isTrainingScenario).</summary>
    private static bool Training => Campaign.IsCampaign(MatchRequest.Current) && Campaign.Catalogue.Campaign.FirstOrDefault() is { } system
        && system.Planets.SelectMany(p => p.Scenarios).Any(s => s.Name.Equals(MatchRequest.Current.Scenario, StringComparison.OrdinalIgnoreCase));

    // How it ended, in the bitmap of MachWorldViewWindow::doDisplay.
    private string EndPicture => World.Winner == Player ? Training ? "misc/complete.bmp" : "misc/victory.bmp" : Training ? "misc/failed.bmp" : "misc/defeat.bmp";

    /// <summary>Opens the menus over the match (Esc, F10, the return button); a game on this machine waits meanwhile.</summary>
    public void OpenMenu() => OpenMenu(statistics: false);

    private void OpenMenu(bool statistics, bool debriefing = false)
    {
        if (_menu is not null && IsInstanceValid(_menu))
            return;
        _pausedBeforeMenu = Paused;
        if (!Online)
            Paused = true;
        var layer = new CanvasLayer { Layer = 10, Name = "Menus" };
        AddChild(layer);
        _menu = new InGameMenu(this, statistics, debriefing);
        _menu.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_menu);
        _rig.Active = false;
    }

    /// <summary>Back to the match from the menus.</summary>
    public void CloseMenu()
    {
        if (_menu is null || !IsInstanceValid(_menu))
            return;
        _menu.GetParent().QueueFree();
        _menu = null;
        _rig.Active = _driven is null;
        if (!Online)
            Paused = _pausedBeforeMenu;
    }

    /// <summary>Whether this match can be saved, restarted or swapped for a saved one: a game on this machine.</summary>
    public bool CanSave => !Online && !Watching && World.Winner is null && World.Wired is not { Phase: Machines.Sim.Survival.WiredPhase.Ended };

    /// <summary>The match so far, saved under <paramref name="name"/>; null when it went well, otherwise why not.</summary>
    public string? SaveGame(string name)
    {
        try
        {
            Directory.CreateDirectory(SaveFolder);
            var match = MatchRequest.Current.ToMatchInfo(_seed, _session.InputDelay, World.Terrain?.Fingerprint());
            var invalid = System.IO.Path.GetInvalidFileNameChars();
            var file = System.IO.Path.Combine(SaveFolder, new string(name.Select(c => invalid.Contains(c) ? '_' : c).ToArray()) + SaveExtension);
            new Replay(match, _session.Played, World.Tick, _session.Presence).Save(file);
            return null;
        }
        catch (IOException exception)
        {
            return exception.Message;
        }
    }

    /// <summary>Leaves this match for a saved one.</summary>
    public string? LoadGame(string path)
    {
        try
        {
            var saved = Replay.Load(path);
            var local = saved.Match.Players.FirstOrDefault(p => !p.Ai)?.Slot;
            MatchRequest.Current = MatchRequest.FromMatch(saved.Match, clientId: -1, local) with { Saved = saved };
            GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
            return null;
        }
        catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException)
        {
            return exception.Message;
        }
    }

    /// <summary>The same scenario again from the start (the Restart button).</summary>
    public void Restart()
    {
        MatchRequest.Current = MatchRequest.Current with { Saved = null, Seed = null, ResumeFrom = null };
        GetTree().ChangeSceneToFile("res://scenes/Main.tscn");
    }

    /// <summary>Back to the main menu (the Exit Game button, F10 used to), keeping a replay of the match.</summary>
    public void ExitToMenus() => ExitToMenus(home: false);

    /// <summary>The same, and with <paramref name="home"/> to the main page itself rather than where the match was set up.</summary>
    public void ExitToMenus(bool home)
    {
        SaveReplay();
        if (Online)
            NetLink.Send(new LeaveRoom());
        // The menus open where the match was set up: the choice of mission, the skirmish setup or the rooms.
        if (!Watching && !home)
            MenuScreens.StartPage = Online ? "multiplayer" : World.Wired is not null ? "hangar" : Campaign.IsCampaign(MatchRequest.Current) ? "missions" : "skirmish";
        GetTree().ChangeSceneToFile("res://scenes/Menu.tscn");
    }

    /// <summary>
    /// A campaign mission played to its end goes into the campaign player's progress (MachGuiStartupScreens's
    /// contextVictory and contextDefeat): its score, and for a win the flags it set and the machines it saves.
    /// </summary>
    private void RecordCampaign()
    {
        if (!Campaign.IsCampaign(MatchRequest.Current) || Watching || Online || Campaign.Entry(MatchRequest.Current.Scenario) is not { } mission)
            return;
        var progress = CampaignProgress.Load();
        if (progress.CurrentPlayer is not { } player)
            return;
        var won = World.Winner == Player;
        player.Record(mission.Name, won, World.Races[Player].Score.Gross, World.Script?.FlagsSet ?? [],
            won && mission.SaveMachines is { } race ? MissionScript.Survivors(World, race) : null);
        try
        {
            progress.Save();
        }
        catch (IOException exception)
        {
            _hud.Message(Lang.T("The campaign progress was not saved: {0}", exception.Message));
        }
    }

    /// <summary>After a campaign mission: its film of victory or defeat when it has one (WIN, LOSE), then the debriefing.</summary>
    private void ShowCampaignEnding()
    {
        var won = World.Winner == Player;
        if (Campaign.Media(_assets.GameFolder, Campaign.Entry(MatchRequest.Current.Scenario)?.Media.GetValueOrDefault(won ? "WIN" : "LOSE")) is { } film)
            FilmScreen.Play(GetTree(), film, () =>
            {
                if (IsInstanceValid(this))
                    OpenMenu(statistics: false, debriefing: true);
            });
        else
            OpenMenu(statistics: false, debriefing: true);
    }

    /// <summary>The scenario's briefing text from the game copy, when there is one.</summary>
    public string? BriefingText()
    {
        var request = MatchRequest.Current;
        var catalogue = System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat");
        if (!File.Exists(catalogue))
            return null;
        var entry = ScenarioCatalogueParser.Load(catalogue).AllScenarios
            .FirstOrDefault(s => s.Name.Equals(request.Scenario, StringComparison.OrdinalIgnoreCase) && s.Planet.Equals(request.Planet, StringComparison.OrdinalIgnoreCase))
            ?? ScenarioCatalogueParser.Load(catalogue).AllScenarios
                .FirstOrDefault(s => s.Name.Equals(request.Scenario, StringComparison.OrdinalIgnoreCase));
        return entry is null ? null : _assets.MenuText(entry.Text);
    }

    // ---- cameras (MachCameras) --------------------------------------------------------------

    /// <summary>Whether the player drives a machine in first person.</summary>
    public bool InFirstPerson => _driven is not null;

    // The zenith camera's pitch and distance while the ground camera is in use.
    private (float Pitch, float Distance)? _zenith;

    /// <summary>Whether the zenith camera (looking down from high up) is in use rather than the ground camera.</summary>
    public bool ZenithCamera => _zenith is null;

    /// <summary>
    /// Switches between the zenith camera and the ground camera, low over the ground looking ahead
    /// (MachCameras::useGroundCamera; 2 and 0 on the number pad).
    /// </summary>
    public void ToggleCamera() => UseZenithCamera(!ZenithCamera);

    private void UseZenithCamera(bool zenith)
    {
        if (zenith == ZenithCamera)
            return;
        // A box being drawn goes with the camera it was drawn in.
        if (_banding)
            EndBand();
        if (zenith && _zenith is { } before)
        {
            (_rig.Pitch, _rig.Distance) = before;
            _zenith = null;
        }
        else if (!zenith)
        {
            _zenith = (_rig.Pitch, _rig.Distance);
            (_rig.Pitch, _rig.Distance) = (9, 28);
        }
        _rig.Ground = !zenith;
    }

    /// <summary>Where the mouse ray meets the ground, in original ground coordinates.</summary>
    public NVector2? GroundAt(Vector2 screen)
    {
        var camera = GetViewport().GetCamera3D();
        var hit = HeightField.Current?.Hit(camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        return hit is { } point ? new NVector2(point.X, point.Z) : null;
    }

    /// <summary>
    /// The actor under the cursor: the nearest whose model the cursor's ray meets, part by part, before it meets the
    /// ground (MachWorldViewWindow::dispatchCursor); failing that, a machine within a few pixels of it, so that small
    /// ones far off can still be picked. The machine being driven is looked out of, never at.
    /// </summary>
    public Actor? ActorAt(Vector2 screen)
    {
        // In the dark of the fog of war nothing can be picked (MachContinentMap::cursorInFogOfWar).
        if (Fog?.Blind == true)
            return null;
        var camera = GetViewport().GetCamera3D();
        var (from, direction) = (camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        var nearest = HeightField.Current?.Hit(from, direction) is { } ground ? from.DistanceTo(ground) : camera.Far;
        ActorView? best = null;
        foreach (var view in _views.Values)
        {
            if (view.ActorId != _driven?.Id && view.Hit(from, direction) is { } distance && distance < nearest)
            {
                nearest = distance;
                best = view;
            }
        }
        if (best is not null)
            return World.Find(best.ActorId);

        var closest = 8f;
        foreach (var view in _views.Values)
        {
            if (!view.Visible || view.IsBuilding || view.ActorId == _driven?.Id || camera.IsPositionBehind(view.Centre))
                continue;
            var off = camera.UnprojectPosition(view.Centre).DistanceTo(screen);
            if (off < closest)
            {
                closest = off;
                best = view;
            }
        }
        return best is null ? null : World.Find(best.ActorId);
    }

    /// <summary>The found mineral site whose ore hologram the cursor's ray meets before anything else, if any.</summary>
    private MineralSiteState? SiteAt(Vector2 screen)
    {
        if (Fog?.Blind == true)
            return null;
        var camera = GetViewport().GetCamera3D();
        var (from, direction) = (camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        var nearest = HeightField.Current?.Hit(from, direction) is { } ground ? from.DistanceTo(ground) : camera.Far;
        MineralSiteState? best = null;
        foreach (var (id, hologram) in _holograms)
        {
            if (ActorView.PartsHit(hologram, from, direction) is { } distance && distance < nearest)
            {
                nearest = distance;
                best = World.Sites[id];
            }
        }
        return best;
    }

    // For automated checks: MACHINES_SCREENSHOT_POINT=x,y (screen pixels) stands in for the mouse when hovering.
    internal static readonly Vector2? PointedForScreenshot =
        System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_POINT")?.Split(',') is [var x, var y]
            ? new Vector2(float.Parse(x, System.Globalization.CultureInfo.InvariantCulture), float.Parse(y, System.Globalization.CultureInfo.InvariantCulture))
            : null;

    // What the mouse points at in the world this frame, and the view drawn in blue for it.
    private Actor? _hovered;
    private ActorView? _highlighted;

    /// <summary>
    /// Finds what the mouse points at once a frame: the actor there gets the blue box unless it is selected
    /// (MachLog::HIGHLIGHTED), and the prompt line names it, or the ore left in a deposit (displayActorPromptText).
    /// </summary>
    private void UpdateHover()
    {
        var mouse = PointedForScreenshot ?? GetViewport().GetMousePosition();
        // A film points at nothing, wherever the mouse is.
        var inWorld = _driven is null && Cinematic.Asked is null && (PointedForScreenshot is not null || GetViewport().GuiGetHoveredControl() is null);
        var onMap = _driven is null ? _hud.Panel.PointedOnMap : null;
        _hovered = onMap is { } map ? map.Actor : inWorld ? ActorAt(mouse) : null;
        _hoveredArtefact = inWorld && _hovered is null ? ArtefactAt(mouse) : null;
        _hoveredDebris = onMap?.Wreck ?? (inWorld && _hovered is null ? DebrisAt(mouse) : null);
        var deposit = inWorld && _hovered is null && _hoveredArtefact is null && _hoveredDebris is null ? SiteAt(mouse) : null;
        _pointed = onMap ?? new Pointed(_hovered ?? _hoveredArtefact, _hoveredDebris, inWorld ? GroundAt(mouse) : null, deposit is not null);
        var view = _hovered is not null ? _views.GetValueOrDefault(_hovered.Id) : null;
        if (view != _highlighted)
        {
            if (_highlighted is not null && IsInstanceValid(_highlighted))
                _highlighted.Highlighted = false;
            _highlighted = view;
        }
        if (view is not null)
            view.Highlighted = !view.Selected;
        _hud.Panel.Prompt = !inWorld && onMap is null ? null
            : _hovered is not null ? _hud.Panel.PromptFor(_hovered)
            : _hoveredArtefact is { } artefact ? _hud.Panel.PromptFor(artefact)
            : _hoveredDebris is { } wreck ? Lang.T("Debris   {0} BMU", wreck.Quantity)
            : deposit is { } site ? ClassicPanel.DepositPrompt(site)
            : null;
        // Pointed at with nothing selected, an actor shows in the single icon (MachInGameScreen::highlightActor).
        _hud.Panel.Highlighted = _hovered;
        _hud.Panel.CommandPrompt = _inHand is { } hand ? Lang.T(hand.Message) : _placing is not null ? PlacementPrompt() : null;
    }

    // The wreck the mouse points at this frame, when no actor is in front of it.
    private DebrisState? _hoveredDebris;

    // The artefact the mouse points at this frame, when no actor is in front of it.
    private Artefact? _hoveredArtefact;

    /// <summary>The selected scavengers (level-3 resource carriers), which gather wreckage.</summary>
    private List<int> SelectedScavengers() => SelectedMachines().Where(m => m.IsScavenger).Select(m => m.Id).ToList();

    /// <summary>Moves the strategy camera over a ground point.</summary>
    public void LookAt(NVector2 point) => _rig.Position = new Vector3(point.X, 0, point.Y);

    public void Select(List<int> ids)
    {
        _squadronSelected = null;
        foreach (var id in _selection)
            if (_views.TryGetValue(id, out var view)) view.Selected = false;
        _selection.Clear();
        _selection.AddRange(ids.Where(id => World.Find(id) is not null));
        foreach (var id in _selection)
            if (_views.TryGetValue(id, out var view)) view.Selected = true;
        // The first selected machine answers, as in the original.
        if (SelectedMachines().FirstOrDefault() is { } first && SoundTable.VoiceOf(first) is { } voice)
            _audio.Say($"VID_{voice}_SELECTED");
    }

    // ---- squadrons and initiative ---------------------------------------------------------------

    /// <summary>The selected machines become squadron <paramref name="squad"/>, whose old members are let go.</summary>
    public void FormSquadron(int squad)
    {
        Order(new FormSquadronCommand(Player, SelectedMachines().Select(m => m.Id).ToList(), squad));
        _hud.Message(Lang.T("Squadron {0} formed", squad));
        // Orders go through its commander from now on (MachGuiFormSquadronCommand: applyCommandToSquadron).
        _squadronSelected = squad;
    }

    /// <summary>
    /// The members of squadron <paramref name="squad"/> are selected (those inside an APC or a lab stay out); until
    /// the selection changes, orders go to it through its commander (MachInGameScreen::applyCommandToSquadron).
    /// </summary>
    public void SelectSquadron(int squad)
    {
        Select(World.Machines.Where(m => m.Race == Player && m.Squad == squad && m.Inside is null).Select(m => m.Id).ToList());
        _squadronSelected = squad;
    }

    // The squadron picked by its number or from the bank, while the selection is still just that.
    private int? _squadronSelected;

    /// <summary>
    /// An order for the selected squadron goes through its commander when it has one (MachGuiCommand::apply in the
    /// squadron context); orders that are not for machines go as they are.
    /// </summary>
    private Command ThroughCommander(Command command) =>
        _squadronSelected is { } squad && World.CommanderOf(Player, squad) is not null && command is MoveCommand or AttackCommand
            or AttackMoveCommand or StopCommand or ConstructCommand or LocateCommand or PatrolCommand or HealCommand or RepairCommand
            or CaptureCommand or DeconstructCommand or RecycleCommand or StandGroundCommand or DefConCommand
            or DropLandMinesCommand or RefillLandMinesCommand or CamouflageCommand or FollowCommand or SeriesCommand
            ? new SquadronCommand(Player, squad, command)
            : command;

    /// <summary>The initiative of the selection: which of low, normal and high DefCon its machines have.</summary>
    public (bool Low, bool Normal, bool High) Initiative()
    {
        var machines = SelectedMachines().ToList();
        return (machines.Any(m => m.DefCon == DefCon.Low), machines.Any(m => m.DefCon == DefCon.Normal), machines.Any(m => m.DefCon == DefCon.High));
    }

    // From normal, the initiative steps on towards high or towards low, turning round at either end.
    private bool _initiativeToHigh = true;

    /// <summary>
    /// Steps the selection's initiative as the original's button did (MachGuiDefconCommand::doApply): a mixed
    /// selection goes all to the higher DefCon, low to normal, high to normal, normal on to high or low in turn.
    /// </summary>
    public void CycleInitiative()
    {
        var (low, normal, high) = Initiative();
        if (!low && !normal && !high)
            return;
        if (low && !normal && !high)
            _initiativeToHigh = true;
        else if (high && !normal && !low)
            _initiativeToHigh = false;
        var next = (low, normal, high) switch
        {
            (_, _, true) when low || normal => DefCon.High,
            (true, true, false) => DefCon.Normal,
            (true, false, false) => DefCon.Normal,
            (false, true, false) => _initiativeToHigh ? DefCon.High : DefCon.Low,
            _ => DefCon.Normal,
        };
        Order(new DefConCommand(Player, SelectedMachines().Select(m => m.Id).ToList(), next));
        // Its own result counts for the next step (the original's update() on the new settings).
        if (next is DefCon.Low)
            _initiativeToHigh = true;
        else if (next is DefCon.High)
            _initiativeToHigh = false;
    }

    /// <summary>The first of the ordered machines acknowledges: moving, tasked or attacking.</summary>
    private void Acknowledge(IEnumerable<Machine> machines, string report)
    {
        if (machines.FirstOrDefault() is { } first && SoundTable.VoiceOf(first) is { } voice)
            _audio.Say($"VID_{voice}_{report}");
    }

    public IEnumerable<Machine> SelectedMachines() =>
        _selection.Select(World.Find<Machine>).OfType<Machine>().Where(m => m.Race == Player);

    /// <summary>
    /// Right click: the right order for each selected machine and what was clicked. It goes to the machines
    /// themselves even for a squadron: the original's default command had no admin version.
    /// </summary>
    private void OrderAt(Vector2 screen)
    {
        // With Alt held, or nothing but buildings selected, it does what the cursor shows.
        var (ctrl, alt) = (Input.IsKeyPressed(Key.Ctrl), Input.IsKeyPressed(Key.Alt));
        if (alt || !SelectedMachines().Any())
        {
            var at = PointedAt(screen);
            if (AimAt(at, ctrl, alt) is var aim && aim is not (Aim.Select or Aim.Menu or Aim.Invalid) && (!alt || aim is Aim.Move or Aim.Follow or Aim.Recycle
                or Aim.Deconstruct or Aim.Treachery or Aim.Deploy or Aim.AssemblyPoint or Aim.EnterBuilding or Aim.Attack))
            {
                Carry(aim, at, ctrl, shift: false);
                return;
            }
        }
        var squadron = _squadronSelected;
        _squadronSelected = null;
        try
        {
            DefaultOrderAt(screen);
        }
        finally
        {
            _squadronSelected = squadron;
        }
    }

    private void DefaultOrderAt(Vector2 screen)
    {
        var machines = SelectedMachines().ToList();
        if (machines.Count == 0)
            return;
        var target = ActorAt(screen);
        var ground = GroundAt(screen);
        List<int> Ids(Func<Machine, bool> which) => machines.Where(which).Select(m => m.Id).ToList();

        if (target is not null && World.AreEnemies(Player, target.Race))
        {
            // Constructors take enemy buildings over, spies slip into enemy labs to steal research; everyone armed
            // attacks.
            var capturers = target is Construction { Complete: true } ? Ids(m => m.Spec.Type == "CONSTRUCTOR") : [];
            if (capturers.Count > 0)
                Order(new CaptureCommand(Player, capturers, target.Id));
            var spies = target is Construction { Complete: true } lab && lab.IsType("HARDWARE_LAB") ? Ids(m => m.IsSpy) : [];
            if (spies.Count > 0)
                Order(new EnterLabCommand(Player, spies, target.Id));
            Order(new AttackCommand(Player, Ids(m => m.CanFight), target.Id));
            Order(new MoveCommand(Player, Ids(m => !m.CanFight && !capturers.Contains(m.Id) && !spies.Contains(m.Id)), target.Position));
            Acknowledge(machines.Where(m => m.CanFight), "TARGET_ENEMY");
            return;
        }

        // An artefact is neutral to every race, so the armed machines attack it, as the cursor showed.
        if (target is null && ArtefactAt(screen) is { } artefact && World.CanAttack(Player, artefact) && machines.Any(m => m.CanFight))
        {
            Order(new AttackCommand(Player, Ids(m => m.CanFight), artefact.Id));
            Order(new MoveCommand(Player, Ids(m => !m.CanFight), artefact.Position));
            Acknowledge(machines.Where(m => m.CanFight), "TARGET_ENEMY");
            return;
        }

        if (target is Machine patient && patient.Race == Player && patient.HitPoints < patient.MaxHitPoints
            && machines.Any(m => m.CanHeal && m.Id != patient.Id))
        {
            Order(new HealCommand(Player, Ids(m => m.CanHeal && m.Id != patient.Id), patient.Id));
            Acknowledge(machines.Where(m => m.CanHeal), "TASKED");
            return;
        }

        if (target is Machine apc && apc.Race == Player && apc.IsType("APC") && machines.Any(m => m.Id != apc.Id && !m.IsType("APC")))
        {
            Order(new BoardCommand(Player, Ids(m => m.Id != apc.Id && !m.IsType("APC")), apc.Id));
            Acknowledge(machines, "TASKED");
            return;
        }

        if (target is Construction building && building.Race == Player)
        {
            var rest = machines.Select(m => m.Id).ToHashSet();
            if (!building.Complete)
            {
                var constructors = Ids(m => m.Spec.Type == "CONSTRUCTOR");
                Order(new ConstructCommand(Player, constructors, building.Id));
                rest.ExceptWith(constructors);
            }
            else if (building.HitPoints < building.MaxHitPoints)
            {
                var constructors = Ids(m => m.Spec.Type == "CONSTRUCTOR");
                Order(new RepairCommand(Player, constructors, building.Id));
                rest.ExceptWith(constructors);
            }
            if (building.IsType("HARDWARE_LAB"))
            {
                var technicians = Ids(m => m.Spec.Type == "TECHNICIAN");
                Order(new EnterLabCommand(Player, technicians, building.Id));
                rest.ExceptWith(technicians);
            }
            if (building.IsType("MINE"))
            {
                var store = World.Constructions.Where(c => c.Race == Player && c.Complete && c.Spec.Type is "SMELTER" or "POD")
                    .MinBy(c => NVector2.Distance(c.Position, building.Position));
                var carriers = Ids(m => m.Spec.Type == "RESOURCE_CARRIER");
                if (store is not null)
                    Order(new TransportCommand(Player, carriers, building.Id, store.Id));
                rest.ExceptWith(carriers);
            }
            // Whoever fits goes in, with a building that has a floor behind its door (MachGuiMoveCommand::applyEnterBuilding).
            var entering = machines.Where(m => rest.Contains(m.Id) && World.CanEnter(m, building)).Select(m => m.Id).ToList();
            if (entering.Count > 0)
            {
                Order(new EnterGarrisonCommand(Player, entering, building.Id));
                rest.ExceptWith(entering);
            }
            if (rest.Count > 0)
                Order(new MoveCommand(Player, rest.ToList(), building.Position));
            Acknowledge(machines, "TASKED");
            return;
        }

        // Scavengers go to gather the wreck clicked; the others drive onto it.
        if (DebrisAt(screen) is { } wreck && SelectedScavengers() is { Count: > 0 } scavengers)
        {
            Order(new ScavengeCommand(Player, scavengers, [wreck.Id]));
            if (Ids(m => !m.IsScavenger) is { Count: > 0 } helpers)
                Order(new MoveCommand(Player, helpers, wreck.Position));
            Acknowledge(machines, "TASKED");
            return;
        }

        if (ground is not { } point)
            return;
        // Ground no one can stand on (the no-entry cursor) refuses the order with the original's bad click.
        if (!World.Nav.IsFree(point, machines.Any(GameWorld.CrossesWater)))
        {
            _audio.PlayGui("gui/sounds/clickbad.wav");
            return;
        }
        var locators = Ids(m => m.Spec.Type == "GEO_LOCATOR");
        if (locators.Count > 0)
            Order(new LocateCommand(Player, locators, point));
        var others = Ids(m => m.Spec.Type != "GEO_LOCATOR");
        if (Input.IsKeyPressed(Key.Ctrl))
            Order(new AttackMoveCommand(Player, others, point));
        else
            Order(new MoveCommand(Player, others, point));
        Acknowledge(machines, "MOVING");
    }

    // ---- building placement ---------------------------------------------------------------------

    public void BeginPlacement(string type, string? subType, int level)
    {
        CancelPlacement();
        _placing = (type, subType, level);
        _placingOrientation = 0;
        // The building itself, see-through, in green where it may stand and red where not.
        var spec = World.Data.Parm.Construction(subType is null ? type : $"{type} {subType}");
        var (id, size) = ActorView.ModelOf(new Construction(0, Player, NVector2.Zero, spec, spec.Level(level), 0));
        _ghostMaterial = new StandardMaterial3D
        {
            AlbedoColor = new Color(0.3f, 1, 0.3f, 0.4f),
            Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
            ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
        };
        // Built from its rig, its pads lie printed on the ground as the building's will (see UpdateGhost).
        _ghost = _assets.RigFor(id, Look(Player)) is { } rig
            ? RigView.Create(rig)
            : new MeshInstance3D { Mesh = _assets.MeshFor(id, Look(Player), size) };
        _ghostPrinted = false;
        AddChild(_ghost);
        foreach (var mesh in _ghost.FindChildren("*", nameof(MeshInstance3D), true, false).OfType<MeshInstance3D>().Append(_ghost as MeshInstance3D).OfType<MeshInstance3D>())
        {
            mesh.MaterialOverride = _ghostMaterial;
            mesh.CastShadow = GeometryInstance3D.ShadowCastingSetting.Off;
        }
        _hud.Message("Left click to place, Space to turn, right click to cancel");
    }

    private void CancelPlacement()
    {
        _placing = null;
        _placingAt = null;
        _placingWhy = null;
        _placedAt.Clear();
        _joined.Clear();
        _ghost?.QueueFree();
        _ghost = null;
        _ghostMaterial = null;
    }

    private void UpdateGhost()
    {
        if (_placing is not { } p || _ghost is null || GroundAt(PointedForScreenshot ?? GetViewport().GetMousePosition()) is not { } at)
            return;
        // With Ctrl held the building stays where it is and turns to face the mouse (MachGuiConstructCommand).
        if (Input.IsKeyPressed(Key.Ctrl) && _placingAt is { } held)
        {
            var away = at - held;
            if (away != NVector2.Zero)
                _placingOrientation = 90 * (Math.Abs(away.X) >= Math.Abs(away.Y) ? away.X >= 0 ? 0 : 2 : away.Y >= 0 ? 1 : 3);
            at = held;
        }
        _placingAt = at;
        // Placed and turned as the building's own view will be (ActorView). A mine goes where the mouse is, as
        // anything else: anywhere near a found site will do (MachGuiConstructCommand never moved it onto the site).
        _ghost.Position = ActorView.World(at);
        _ghost.Rotation = new Vector3(0, -Mathf.DegToRad(_placingOrientation), 0);
        _placingWhy = WhyNotHere(p, at);
        var ok = _placingWhy is null;
        // Where it cannot stand it flashes, four times a second.
        _ghost.Visible = ok || (int)(Time.GetTicksMsec() / 250) % 2 == 1;
        var tint = ok ? new Color(0.3f, 1, 0.3f, 0.4f) : new Color(1, 0.25f, 0.2f, 0.4f);
        _ghostMaterial!.AlbedoColor = tint;
        if (_ghost is RigView view)
        {
            // Printed once where it first stands, reaching far enough up and down to lie on the ground wherever the
            // mouse takes it.
            if (!_ghostPrinted && HeightField.Current is { } ground)
            {
                view.PrintPads(ground.At, reach: 12);
                _ghostPrinted = true;
            }
            view.PadTint = tint;
        }
    }

    // Where the building to place stands now, and why it cannot; the sites picked with Shift so far, new and joined.
    private NVector2? _placingAt;
    private string? _placingWhy;
    private readonly List<NVector2> _placedAt = [];
    private readonly List<int> _joined = [];

    /// <summary>The cursor while placing: the arrow where it may stand, no entry where not, join over the player's own unfinished site.</summary>
    private string PlacingCursor() =>
        _pointed.Actor is Construction { Complete: false } site && site.Race == Player ? "jconst" : _placingWhy is null ? "arrow" : "noentry";

    private const string TooManyUnits = "max units", InTheFog = "fog of war";

    // MachGuiConstructCommand::isValidPosition, which asks for the race's limit first and the fog last.
    private string? WhyNotHere((string Type, string? SubType, int Level) p, NVector2 at) => World.MaxUnitsExist(Player) ? TooManyUnits
        : World.WhyCannotPlace(Player, p.Type, p.SubType, p.Level, at, _placingOrientation) ?? (Fog?.Blind == true ? InTheFog : null);

    /// <summary>What the prompt line says while placing: how to, or as the original's Info lines why not here.</summary>
    private string PlacementPrompt() => _placingWhy switch
    {
        null => ClassicPanel.Text(15, "Click on location. Hold ctrl key down to change orientation."),
        TooManyUnits => ClassicPanel.Text(67, "Info : Max units already exist"),
        InTheFog => ClassicPanel.Text(61, "Info : Can't construct in Fog of War"),
        var why when why.Contains("BMU") => ClassicPanel.Text(66, "Info : 5 Bmus required to place construction"),
        var why when why.StartsWith("mine", StringComparison.Ordinal) => ClassicPanel.Text(55, "Info : Mine not on mineral site or too close to Smelter or Pod"),
        var why when why.StartsWith("smelter", StringComparison.Ordinal) => ClassicPanel.Text(56, "Info : Smelter too close to mine"),
        var why when why.Contains("obstacles") => ClassicPanel.Text(58, "Info : Obstacles"),
        var why => Lang.T(why),
    };

    /// <summary>
    /// A pick while placing: the site stands at once, or an unfinished site of the player's joins the list. With Shift
    /// more follow, each with the waypoint click; the last sets the constructors to work on them in turn with the
    /// command's own click (MachGuiConstructCommand, MachGuiCommand::apply).
    /// </summary>
    private void PlaceBuilding(Pointed at, bool shift)
    {
        if (_placing is not { } p || RefusesOrders())
            return;
        var constructors = Movers().Where(m => m.Spec.Type == "CONSTRUCTOR").Select(m => m.Id).ToList();
        PlaceConstructionCommand? placed = null;
        var spot = _placingAt ?? at.Ground;
        if (at.Actor is Construction { Complete: false } site && site.Race == Player)
            _joined.Add(site.Id);
        else if (spot is not { } ground)
            return;
        else
        {
            // Nothing can be started on less than 5 BMUs (MachGuiConstructCommand::doApply).
            if (World.Races[Player].Bmu < GameWorld.MinimumBmuToPlace)
            {
                _audio.Say("VID_POD_INSUFFICIENT_BMUS");
                return;
            }
            // A site that can't stand there gets the original's refusal click.
            if (WhyNotHere(p, ground) is not null)
            {
                _audio.PlayGui("gui/sounds/clickbad.wav");
                return;
            }
            placed = new PlaceConstructionCommand(Player, [], p.Type, p.SubType, p.Level, ground, _placingOrientation);
            if (shift)
            {
                Issue(placed);
                _placedAt.Add(ground);
            }
        }
        if (shift)
        {
            Waypoint();
            return;
        }
        // The sites picked with Shift by their places, those that have come to stand.
        var sites = World.Constructions.Where(c => c.Race == Player && !c.Complete && _placedAt.Any(point => NVector2.Distance(point, c.Position) < 1)).Select(c => c.Id);
        var steps = _joined.Concat(sites).Distinct().Select(id => (Command)new ConstructCommand(Player, [], id)).ToList();
        if (placed is not null)
            steps.Add(placed);
        Issue(steps is [PlaceConstructionCommand one] ? one with { Constructors = constructors } : new SeriesCommand(Player, constructors, steps));
        _audio.Say("VID_DOZER_MOVE_TO_SITE");
        _audio.PlayGui("gui/sounds/taskmach.wav");
        CancelPlacement();
    }

    // ---- first person ---------------------------------------------------------------------------

    public void EnterFirstPerson()
    {
        // MachGuiFirstPerson::okayToSwitchTo1stPerson: one machine of the player's, not in an APC, while the match allows it.
        if (World.Options.DisableFirstPerson)
        {
            _hud.Message("First person is switched off in this match");
            return;
        }
        // In a run first person is the hero's, whatever stands selected.
        if (World.Wired is not null && !Watching && !IntoTheHero())
            return;
        if (_selection.Count != 1 || SelectedMachines().FirstOrDefault() is not { } machine || machine.Inside is { } inside && World.Find<Machine>(inside) is not null)
        {
            _hud.Message("Select one of your machines to drive it (F)");
            return;
        }
        // What the mouse was doing on the field ends here in a run, where its buttons are triggers from now on.
        if (World.Wired is not null)
        {
            EndBand();
            _rightPress = null;
            _rig.Drag = Vector2I.Zero;
        }
        _firstPersonFromGround = !ZenithCamera;
        _lastRightClick = 0;
        _driven = machine;
        if (_views.TryGetValue(machine.Id, out var view))
            view.SetDriven(true);
        Issue(new StopCommand(Player, [machine.Id]));
        Issue(new DriveCommand(Player, machine.Id, Driving: true));
        _rig.Active = false;
        _firstPersonCamera.Current = true;
        _lookPitch = 0;
        _headTarget = 0;
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _weaponSelect = int.MaxValue;
        // A panel button keeping the focus would take Tab for moving the focus on.
        GetViewport().GuiReleaseFocus();
        _hud.FirstPerson = true;
        _screen.Enter();
        // The radar comes on, and a machine with weapons readies them (MachGuiFirstPerson).
        if (machine.Weapons.Count > 0)
            _audio.PlayGui("gui/sounds/weapchng.wav");
        _aimYaw = machine.Facing;
        _hullTurnsDue.Clear();
        // The keys as the player chose them (KeyMap).
        static string K(KeyMap.Act act) => KeyMap.Name(act);
        var (drive, sides, turn) = ($"{K(KeyMap.Act.Forward)}/{K(KeyMap.Act.Back)}", $"{K(KeyMap.Act.Left)}/{K(KeyMap.Act.Right)}",
            $"{K(KeyMap.Act.TurnLeft)}/{K(KeyMap.Act.TurnRight)}");
        var sideways = Lang.T(machine.CanStrafe ? "{0} step sideways" : "{0} turn", sides);
        var tail = WeaponButtons
            ? Lang.T("{0}, {1} and {2} each fire a weapon, {3} or Esc to leave", K(KeyMap.Act.FireLeft), K(KeyMap.Act.FireRight), K(KeyMap.Act.FireTop), K(KeyMap.Act.FirstPerson))
            : Lang.T("left mouse fire, {0} weapons, {1} or Esc to leave", K(KeyMap.Act.Weapons), K(KeyMap.Act.FirstPerson));
        var centre = K(KeyMap.Act.CentreView);
        // A run has the driver in the hero from its first moment and again after every death, and its HUD has its own say.
        if (World.Wired is not null)
            return;
        _hud.Message((GameOptions.ClassicDriving, machine.CanTurnUpperBody) switch
        {
            (true, true) when WeaponButtons => Lang.T("First person: {0} drive, {1} or mouse turn, Shift turns the upper body ({2} centres it), {3}", drive, sides, centre, tail),
            (true, true) => Lang.T("First person: {0} drive, {1} or mouse turn, Shift or right mouse turns the upper body ({2} centres it), {3}", drive, sides, centre, tail),
            (true, false) => Lang.T("First person: {0} drive, {1} or mouse turn, {2}", drive, sides, tail),
            (false, true) => Lang.T("First person: {0} drive, {1}, {2} turn (Ctrl slowly), the mouse looks and the upper body follows ({3} centres it), {4}", drive, sideways, turn, centre, tail),
            (false, false) => Lang.T("First person: {0} drive, {1}, {2} turn (Ctrl slowly), the mouse looks and the machine follows, {3}", drive, sideways, turn, tail),
        });
    }

    // Which camera first person came from, and goes back to unless told otherwise.
    private bool _firstPersonFromGround;

    // When the right button last came up, for its double click.
    private double _lastRightClick;

    private void LeaveFirstPerson() => LeaveFirstPerson(_firstPersonFromGround);

    /// <summary>Out of first person to the given camera, over the machine (MachGuiFirstPerson::detachCamera).</summary>
    private void LeaveFirstPerson(bool ground)
    {
        if (_driven is { } machine && World.Find(machine.Id) is not null)
            Issue(new DriveCommand(Player, machine.Id, Driving: false));
        var left = _driven;
        if (_driven is { } was && _views.TryGetValue(was.Id, out var view))
            view.SetDriven(false);
        _driven = null;
        _hands.LetGo();
        _headTarget = 0;
        _rightButtonTurnedHead = false;
        _rig.Active = true;
        _rig.Camera.Current = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _hud.FirstPerson = false;
        _screen.Leave();
        UseZenithCamera(!ground);
        if (left is not null && World.Find(left.Id) is { IsDead: false } still)
            LookAtActor(still);
    }

    private float _mouseTurn;

    // Where the driven machine's upper body is to turn, radians anticlockwise from its hull in the original's axes
    // (on screen, mirrored, positive is to the right); it gets there at its BODY_TURN_RATE.
    private float _headTarget;

    // The right button turned the upper body; letting go of it brings the upper body back to the front.
    private bool _rightButtonTurnedHead;

    // With the modern keys, where the view looks in the world, radians anticlockwise in the original's axes: the mouse
    // turns it at once, and the machine follows, its upper body keeping that heading while the hull turns under it.
    private float _aimYaw;

    /// <summary>
    /// With the modern keys the mouse turns the view at once, as in a tank game, and the machine follows it at its own
    /// rate: the upper body of a machine that has one, else the whole hull.
    /// </summary>
    private bool FreeLook => !GameOptions.ClassicDriving && _driven is not null;

    // Radians the view turns for a pixel the mouse moves: up and down, and with the modern keys sideways as well; the
    // options' F.P. Mouse Speed scales it, and the classic keys' mouse turns too.
    private static float LookPerPixel => 0.002f * GameOptions.MouseSpeed;

    // The hull turns the driven machine has been told to make and has not made yet, by the tick that carries each out
    // (radians anticlockwise in the original's axes): a hull following the view counts them, or it would overshoot.
    private readonly Queue<(long Tick, float Turn)> _hullTurnsDue = new();

    /// <summary>How fast the simulation turns the driven machine's hull, radians a second (GameWorld's driving).</summary>
    private float HullTurnRate(Machine machine, bool slow) =>
        Mathf.DegToRad(World.Options.OriginalMotion ? World.DrivenTurnRate(machine, slow) : World.Data.Parm.General.FirstPersonMaxFastTurnRate);

    /// <summary>
    /// With the modern keys, how Q and E turn the hull, and A and D where the machine cannot step sideways: 1 for a
    /// full turn to the left on screen, -1 to the right.
    /// </summary>
    private static float KeyTurn(Machine machine)
    {
        var turn = (KeyMap.Held(KeyMap.Act.TurnLeft) ? 1f : 0f) - (KeyMap.Held(KeyMap.Act.TurnRight) ? 1f : 0f);
        if (!Input.IsKeyPressed(Key.Shift))
            turn += ArrowTurn();
        if (!machine.CanStrafe && !GameOptions.ClassicKeys)
            turn += (KeyMap.Held(KeyMap.Act.Left) ? 1f : 0f) - (KeyMap.Held(KeyMap.Act.Right) ? 1f : 0f);
        return Mathf.Clamp(turn, -1, 1);
    }

    /// <summary>The left and right arrows, on the keyboard or the number pad: they turn the machine, with Shift its upper body (MachGuiFirstPerson's keys).</summary>
    private static float ArrowTurn() =>
        (Input.IsKeyPressed(Key.Left) || Input.IsKeyPressed(Key.Kp4) ? 1f : 0f) - (Input.IsKeyPressed(Key.Right) || Input.IsKeyPressed(Key.Kp6) ? 1f : 0f);

    /// <summary>How fast the keys turn the upper body, radians a second: a tenth of that with Ctrl.</summary>
    private static float HeadKeySpeed(Machine machine) =>
        Mathf.DegToRad(machine.Stats.BodyTurnRate ?? 180) * (Input.IsKeyPressed(Key.Ctrl) ? 0.1f : 1);

    /// <summary>
    /// With the classic keys, whether the mouse and A/D turn the driven machine's upper body rather than the whole
    /// machine: with Shift or the right button held, for a machine whose upper body turns (MachGuiFirstPerson::doUpdate).
    /// The right button is a trigger while each weapon has its own.
    /// </summary>
    private bool TurningHead => GameOptions.ClassicDriving && _driven is { CanTurnUpperBody: true }
        && (Input.IsKeyPressed(Key.Shift) || !WeaponButtons && Input.IsMouseButtonPressed(MouseButton.Right));

    private void FirstPersonInput(InputEvent @event)
    {
        if (_screen.Lost)
            return;
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false } key when key.Keycode == Key.Escape || KeyMap.Is(key, KeyMap.Act.FirstPerson) || MatchOver && key.Keycode == Key.Space:
                LeaveFirstPerson();
                break;
            case InputEventKey { Pressed: true } when MatchOver:
                break;
            // 0 and 2 on the number pad leave for the ground and the zenith camera.
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Kp0 }:
                LeaveFirstPerson(ground: true);
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Kp2 }:
                LeaveFirstPerson(ground: false);
                break;
            case InputEventKey { Pressed: true, Echo: false } key when KeyMap.Is(key, KeyMap.Act.Menu) && !TakenInFirstPerson(key.Keycode):
                LeaveFirstPerson();
                OpenMenu();
                break;
            case InputEventKey { Pressed: true, Echo: false } key when KeyMap.Is(key, KeyMap.Act.Pause) && !TakenInFirstPerson(key.Keycode):
                TogglePause();
                break;
            case InputEventKey { Pressed: true, Echo: false } key when KeyMap.Is(key, KeyMap.Act.Weapons) && !WeaponButtons:
                SelectNextWeapons();
                break;
            // N switches the goggles of a machine that has them.
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.N } when _driven is { Stats.NightVision: true }:
                _screen.ToggleGoggles();
                break;
            // C (the original's keypad 5) turns the upper body back to the front.
            case InputEventKey { Pressed: true, Echo: false } key when KeyMap.Is(key, KeyMap.Act.CentreView):
                _headTarget = 0;
                if (_driven is { } centred)
                    _aimYaw = centred.Facing;
                break;
            case InputEventMouseMotion motion when FreeLook:
                _aimYaw = Mathf.Wrap(_aimYaw + motion.Relative.X * LookPerPixel, -Mathf.Pi, Mathf.Pi);
                LookUpDown(motion);
                break;
            // The right button clicked twice within half a second leaves (MachGuiFirstPerson::doHandleRightClickEvent).
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } when !WeaponButtons:
                var clicked = Time.GetTicksMsec() / 1000.0;
                if (clicked - _lastRightClick < 0.5)
                {
                    LeaveFirstPerson();
                    break;
                }
                _lastRightClick = clicked;
                if (_rightButtonTurnedHead)
                {
                    _rightButtonTurnedHead = false;
                    _headTarget = 0;
                }
                break;
            case InputEventMouseMotion motion when TurningHead:
                _rightButtonTurnedHead |= Input.IsMouseButtonPressed(MouseButton.Right);
                _headTarget = Mathf.Wrap(_headTarget + motion.Relative.X * 2 * LookPerPixel, -Mathf.Pi, Mathf.Pi);
                LookUpDown(motion);
                break;
            case InputEventMouseMotion motion:
                _mouseTurn -= motion.Relative.X * 10 * LookPerPixel;
                LookUpDown(motion);
                break;
            default:
                RunInput(@event);
                break;
        }
    }

    // The mouse looks down as it comes back, up with the options' Reverse F.P. Mouse (MachGuiFirstPerson).
    private void LookUpDown(InputEventMouseMotion motion) => LookBy(motion.Relative.Y * (GameOptions.ReverseMouse ? LookPerPixel : -LookPerPixel));

    // Shift and the up arrow look down, Shift and the down arrow up, slowly with Ctrl too (MachGuiFirstPerson's
    // LOOKDOWN and LOOKUP keys); the options' Reverse Up/Down Keys swaps the arrows.
    private void LookWithKeys()
    {
        if (!Input.IsKeyPressed(Key.Shift) || Input.IsKeyPressed(Key.Up) == Input.IsKeyPressed(Key.Down) || HangarOpen)
            return;
        var step = Mathf.DegToRad(Input.IsKeyPressed(Key.Ctrl) ? 15 : 150) * (float)GetProcessDeltaTime();
        LookBy(Input.IsKeyPressed(Key.Up) != GameOptions.ReverseKeys ? -step : step);
    }

    private void LookBy(float radians)
    {
        var limits = World.Data.Parm.General;
        _lookPitch = Mathf.Clamp(_lookPitch + radians,
            -Mathf.DegToRad(limits.FirstPersonLookMaxAngle), Mathf.DegToRad(-limits.FirstPersonLookMinAngle));
    }

    // Which weapons fire in first person (MachGuiFirstPerson::doWeaponSelect): one of them by its index, or all
    // of them when it equals their number.
    private int _weaponSelect = int.MaxValue;

    /// <summary>The selected weapons as one bit each, in mounting order: all of them where each has its own trigger.</summary>
    private int SelectedWeapons(Machine machine) =>
        WeaponButtons || _weaponSelect >= machine.Weapons.Count ? -1 : 1 << _weaponSelect;

    /// <summary>Tab: the next single weapon, then all of them together (the Ninja skips its punch alone).</summary>
    private void SelectNextWeapons()
    {
        if (_driven is not { } machine || machine.Weapons.Count < 2)
            return;
        _audio.PlayGui("gui/sounds/weapchng.wav");
        var count = machine.Weapons.Count;
        _weaponSelect = _weaponSelect >= count ? 0 : _weaponSelect + 1;
        // The original's Ninja (the Gorilla) lists its punch last and never picks it alone; here the punch
        // sits between the launchers, so it is passed over wherever it is.
        if (_weaponSelect < count && machine.Weapons[_weaponSelect].Spec.TypeName == "GORILLA_PUNCH")
            _weaponSelect++;
    }

    /// <summary>What the keys held say of going ahead (1), back (-1) or neither.</summary>
    private static float ForwardKeys()
    {
        var shift = Input.IsKeyPressed(Key.Shift);
        var moves = !GameOptions.ClassicKeys;
        return (moves && KeyMap.Held(KeyMap.Act.Forward) || !shift && (Input.IsKeyPressed(Key.Up) || Input.IsKeyPressed(Key.Kp8)) ? 1f : 0f)
            - (moves && KeyMap.Held(KeyMap.Act.Back) || !shift && Input.IsKeyPressed(Key.Down) ? 1f : 0f);
    }

    private void UpdateDriving()
    {
        if (_driven is not { } machine || _screen.Lost)
            return;
        // Behind the hangar's screen the hero stands: its keys and buttons are the screen's.
        if (HangarOpen)
        {
            Issue(new DriveCommand(Player, machine.Id, Driving: true, Weapons: SelectedWeapons(machine), Head: _headTarget));
            return;
        }
        // The arrows drive and turn whatever the moves' keys are, and the up arrow on the number pad too; the one down
        // there leaves (MachGuiFirstPerson's keys). With Shift they turn the upper body and look instead.
        var shift = Input.IsKeyPressed(Key.Shift);
        var moves = !GameOptions.ClassicKeys;
        var forward = ForwardKeys();
        var keys = moves ? (KeyMap.Held(KeyMap.Act.Left) ? 1f : 0f) - (KeyMap.Held(KeyMap.Act.Right) ? 1f : 0f) : 0;
        var arrows = ArrowTurn();
        var turn = Mathf.Clamp(_mouseTurn, -1, 1);
        _mouseTurn *= 0.5f;
        var strafe = 0f;
        if (!GameOptions.ClassicDriving)
        {
            // Modern: Q and E turn; A and D step sideways where the machine can (legs, hover, flight), else turn too.
            if (machine.CanStrafe)
                strafe = keys;
            if (machine.CanTurnUpperBody)
            {
                // The upper body keeps the view's heading in the world while the hull turns under it.
                turn = KeyTurn(machine);
                if (shift)
                    _aimYaw = Mathf.Wrap(_aimYaw - arrows * HeadKeySpeed(machine) * GameWorld.Dt, -Mathf.Pi, Mathf.Pi);
                _headTarget = Mathf.Wrap(_aimYaw - machine.Facing, -Mathf.Pi, Mathf.Pi);
            }
            else
            {
                // Without one the hull turns to where the view looks (the keys turn the view, frame by frame), as
                // fast as it can, counting the turns already on their way so as to stop there.
                while (_hullTurnsDue.TryPeek(out var due) && due.Tick < World.Tick)
                    _hullTurnsDue.Dequeue();
                var step = HullTurnRate(machine, Input.IsKeyPressed(Key.Ctrl)) * GameWorld.Dt;
                var behind = Mathf.Wrap(_aimYaw - machine.Facing - _hullTurnsDue.Sum(due => due.Turn), -Mathf.Pi, Mathf.Pi);
                turn = -Mathf.Clamp(behind / step, -1, 1);
                _hullTurnsDue.Enqueue((World.Tick + _session.InputDelay, -turn * step));
            }
        }
        // With Shift (or the right button) held, A and D turn the upper body as the original's Shift and arrows did.
        else if (TurningHead)
            _headTarget = Mathf.Wrap(_headTarget - (keys + arrows) * HeadKeySpeed(machine) * GameWorld.Dt, -Mathf.Pi, Mathf.Pi);
        else
            turn += keys + (shift ? 0 : arrows);

        // While the left button or Space is held the weapons fire at the very point under the crosshair, as the original's
        // did (MachGuiFirstPerson, MachPhys1stPersonDriver::aimData): where the line of sight first meets a model or
        // the ground within their reach, else its end in the air, whether or not anything is there. With a trigger for
        // each weapon those held fire, and the others keep reloading; the aim goes on until a burst begun is out, however
        // short the click that began it was.
        NVector2? aimAt = null;
        float? aimHeight = null;
        var buttons = WeaponButtons;
        var selected = buttons ? HeldWeapons(machine) : SelectedWeapons(machine);
        var firing = buttons ? selected != 0 : Input.IsMouseButtonPressed(MouseButton.Left) || SpaceFires;
        if ((firing || buttons && machine.Weapons.Any(w => w.RoundsLeft > 0)) && machine.CanFight)
        {
            // None of the selected weapons charged: the original's dud click, at most every 0.8 s (MachGuiFirstPerson).
            var now = Time.GetTicksMsec() / 1000.0;
            if (!firing || machine.Weapons.Where((_, i) => (selected & (1 << i)) != 0).Any(w => (w.DealsDamage || w.IsTreachery) && w.ChargePercent >= 100))
                _lastCouldFire = now;
            else if (now - _lastCouldFire > 0.8)
            {
                _audio.PlayGui("gui/sounds/nowefire.wav");
                _lastCouldFire = now;
            }
            var (sight, met) = SightAt(GetViewport().GetVisibleRect().Size / 2, machine.Weapons.Max(w => w.Spec.Range));
            (aimAt, aimHeight) = (new NVector2(sight.X, sight.Z), sight.Y);
            _sightLift = Vector3.Zero;
            // In a run the sight on a model means what stops rounds of it; the picture still shows them going where the eye looks.
            if (World.Wired is not null && met is not null)
            {
                var (point, height) = World.AimInside(met, aimAt.Value, sight.Y);
                (aimAt, aimHeight) = (point, height);
                _sightLift = sight - new Vector3(point.X, height, point.Y);
            }
            _projectiles.SightLift = _sightLift.Y;
        }
        // The simulation turns left (counter-clockwise in the original's axes) for a positive turn, and steps left for a
        // positive strafe; the client shows the world with y and z swapped, which mirrors it, so on screen both go
        // right. Ctrl turns slowly (FIRST_PERSON_MAX_SLOW_TURN_RATE).
        Issue(new DriveCommand(Player, machine.Id, Driving: true, forward, Mathf.Clamp(-turn, -1, 1), aimAt, selected, _headTarget,
            -strafe, Input.IsKeyPressed(Key.Ctrl), aimHeight, buttons));
    }

    /// <summary>
    /// Where the line of sight through a point of the screen first meets a model (not the driven machine's) or the
    /// ground, at most <paramref name="range"/> metres from the camera, else where it ends in the air
    /// (MachPhys1stPersonDriver::aimData); with the actor it met, if any. In the dark of the fog it meets no actor.
    /// </summary>
    private (Vector3 Point, Actor? Actor) SightAt(Vector2 screen, float range)
    {
        var camera = GetViewport().GetCamera3D();
        var (from, direction) = (camera.ProjectRayOrigin(screen), camera.ProjectRayNormal(screen));
        var nearest = HeightField.Current?.Hit(from, direction) is { } ground ? Mathf.Min(range, from.DistanceTo(ground)) : range;
        ActorView? met = null;
        if (Fog?.Blind != true)
        {
            foreach (var view in _views.Values)
            {
                if (view.ActorId != _driven?.Id && view.Hit(from, direction) is { } distance && distance < nearest)
                    (nearest, met) = (distance, view);
            }
        }
        return (from + direction * nearest, met is null ? null : World.Find(met.ActorId));
    }

    // When a selected weapon of the driven machine was last charged with the trigger held.
    private double _lastCouldFire;

    // How far from where a run's rounds were sent the sight really rests, on the picture of what it met.
    private Vector3 _sightLift;

    /// <summary>Where the driven machine's upper body has turned to so far, radians anticlockwise from its hull.</summary>
    private float HeadNow(Machine machine) => machine.CanTurnUpperBody && _views.TryGetValue(machine.Id, out var view) ? view.UpperBodyNow : 0;

    private void UpdateFirstPersonCamera()
    {
        if (_driven is not { } machine || !_views.TryGetValue(machine.Id, out var view))
            return;
        LookWithKeys();
        // With the modern keys a machine without an upper body turns its view with the keys, smoothly, and the hull
        // follows it at the same rate.
        if (FreeLook && !machine.CanTurnUpperBody && (!Paused || Online) && !HangarOpen)
            _aimYaw = Mathf.Wrap(_aimYaw - KeyTurn(machine) * HullTurnRate(machine, Input.IsKeyPressed(Key.Ctrl)) * (float)GetProcessDeltaTime(),
                -Mathf.Pi, Mathf.Pi);
        // From the faceplate, as the original, turning with the upper body; the view stays level whatever the
        // machine leans or sways.
        if (machine.CanTurnUpperBody)
            view.UpperBodyAngle = _headTarget;
        var head = HeadNow(machine);
        _firstPersonCamera.GlobalPosition = view.Eye;
        // With the modern keys the view looks where the mouse turned it, not waiting for the machine to follow.
        var yaw = FreeLook ? -_aimYaw : view.Yaw - head;
        _firstPersonCamera.GlobalRotation = new Vector3(_lookPitch, yaw - Mathf.Pi / 2, 0);
        _firstPersonCamera.Basis *= CameraRig.ShakeAt(_firstPersonCamera.GlobalPosition);

        _hud.Weapons.Show(machine.Weapons, SelectedWeapons(machine));
        _hud.Weapons.Held = ActHeld;
        _hud.Weapons.ShowTriggers(WeaponButtons ? GunPlaces(machine) : null);
        _hud.Radar.Driven = machine;
        _hud.Radar.Head = head;
        // In a run the sight opens as far as its rounds stray.
        if (World.Wired is not null)
        {
            _hud.Crosshair.Spread = World.AimSpread(machine);
            _hud.Crosshair.Outlined = _screen.Goggles;
        }

        // The crosshair shows what it rests on: an enemy to shoot or a friend to spare (ore and wrecks don't count).
        // It looks along the same line of sight the weapons fire along, as far as they reach.
        var reach = machine.Weapons.Select(w => w.Spec.Range).DefaultIfEmpty(0).Max();
        var (sighted, aimed) = SightAt(GetViewport().GetVisibleRect().Size / 2, reach);
        _hud.Crosshair.Target = aimed is null ? null
            : (aimed.HitPoints / (float)Math.Max(1, aimed.MaxHitPoints), aimed.Armour / (float)Math.Max(1, aimed.MaxArmour));
        // The compass reads the heading as the original's azimuth: degrees anticlockwise from its x axis.
        _hud.Crosshair.Heading = Mathf.RadToDeg(FreeLook ? _aimYaw : machine.Facing + head);
        _hud.Crosshair.Aim = aimed is null ? Crosshair.Sight.None
            : !World.AreEnemies(Player, aimed.Race) ? Crosshair.Sight.Friend
            : WeaponsBear(machine, view.Eye, sighted) ? Crosshair.Sight.Enemy
            : Crosshair.Sight.Miss;
    }

    /// <summary>
    /// Whether a selected weapon can tilt to the point: within 95% of its DEVIATION_ANGLES either way of where the upper
    /// body faces (MachPhys1stPersonDriver::targetPointAnglesValid); 10 degrees for a weapon without them.
    /// </summary>
    private bool WeaponsBear(Machine machine, Vector3 from, Vector3 point)
    {
        var heading = machine.Facing + HeadNow(machine);
        var ahead = new Vector3(Mathf.Cos(heading), 0, Mathf.Sin(heading));
        var to = point - from;
        var along = to.Dot(ahead);
        var across = (to - ahead * along - Vector3.Up * to.Y).Length();
        var selected = SelectedWeapons(machine);
        return machine.Weapons.Where((_, i) => (selected & (1 << i)) != 0).Any(weapon =>
        {
            var angles = weapon.Spec.Presentation.FirstOrDefault(line => line.Keyword == "DEVIATION_ANGLES")?.Args;
            float Tan(int i) => Mathf.Tan(Mathf.DegToRad(angles is { Count: > 1 } && float.TryParse(angles[i], System.Globalization.CultureInfo.InvariantCulture, out var degrees) ? degrees : 10));
            return across <= 0.95f * along * Tan(0) && Math.Abs(to.Y) <= 0.95f * along * Tan(1);
        });
    }
}
