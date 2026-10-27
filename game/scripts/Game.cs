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
            }
            else if (!shown && _holograms.Remove(site.Id, out var gone))
            {
                gone.QueueFree();
            }
        }
    }
    // The wreckage on the ground, by wreck, with its fire.
    private readonly Dictionary<int, (Node3D View, GpuParticles3D Fire)> _debris = [];

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
            for (var i = Math.Clamp(4 + wreck.Quantity / 25, 4, 14); i > 0; i--)
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
            _debris[wreck.Id] = (heap, fire);
        }
        foreach (var (id, (view, fire)) in _debris.ToList())
        {
            if (World.FindDebris(id) is not { } wreck)
            {
                view.QueueFree();
                _debris.Remove(id);
            }
            else if (fire.Emitting && World.Time - wreck.CreatedAt >= GameWorld.DebrisBurnTime)
            {
                fire.Emitting = false;
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
        foreach (var (id, (view, _)) in _debris)
        {
            if (ActorView.PartsHit(view, from, direction) is { } distance && distance < nearest)
            {
                nearest = distance;
                best = World.FindDebris(id);
            }
        }
        return best;
    }

    /// <summary>The wreck lying at a ground point (within its heap), if any.</summary>
    public DebrisState? DebrisNear(NVector2 point) => World.Debris
        .Where(d => NVector2.Distance(d.Position, point) <= MathF.Max(3, (d.Boundary.Max - d.Boundary.Min).Length() * 0.3f))
        .MinBy(d => NVector2.Distance(d.Position, point));

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
    private GameAudio _audio = null!;

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
    private readonly HashSet<Blast> _groundBlasts = [];

    // First person
    private Machine? _driven;
    private Camera3D _firstPersonCamera = null!;
    private float _lookPitch;

    public GameWorld World { get; private set; } = null!;

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
    public void Order(Command command)
    {
        Issue(command);
        if (!Watching)
            _audio.PlayGui("gui/sounds/taskmach.wav");
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
        var request = MatchRequest.Current;
        var data = Paths.Data;
        _seed = request.Seed ?? (int)(Time.GetUnixTimeFromSystem() % int.MaxValue);
        World = MatchSetup.Create(data, request.Scenario, request.Planet, request.Starts.Players, _seed, request.Options);

        // Every match runs through lockstep: alone with one tick of input delay, online with the relay's, or
        // played back from a replay.
        var humans = request.Starts.Players.Where(p => p.Value == Controller.Player).Select(p => p.Key);
        _session = request.Replay?.Playback(World)
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
        // The simulation stops shots at the same ground, from the same game copy (every player needs it alike: the
        // world's checksum carries it, so differing ground shows at once as being out of sync).
        if (_assets.PlanetHeights(request.Planet) is { } ground)
            World.Terrain = TerrainHeights.FromExport(ground);
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
        AddChild(ProjectileViews.Create(World, _assets, id => _views.GetValueOrDefault(id), _effects));

        _rig = new CameraRig { Name = "CameraRig" };
        AddChild(_rig);
        var start = data.LoadScenario(request.Scenario).Race(Player)?.Camera;
        _rig.Setup(start is null ? new Vector3(400, 0, 400) : new Vector3(start.Position.X, 0, start.Position.Y),
            new Vector2(World.Planet.Surface.Width, World.Planet.Surface.Depth));

        // The machine being driven sits on its own render layer, which this camera leaves out.
        _firstPersonCamera = new Camera3D { Far = 3000, Near = 0.1f, Fov = 70, Name = "FirstPersonCamera", CullMask = ~ActorView.DrivenLayer & 0xFFFFF };
        AddChild(_firstPersonCamera);

        _audio = GameAudio.Create(_assets.GameFolder);
        AddChild(_audio);
        Music.Match(this, ScenarioMusic(request.Scenario));

        _hud = new Hud(this, map);
        AddChild(_hud);
        _cursor = new ClassicCursor { Name = "Cursor" };
        AddChild(_cursor);

        // The fog of war is the player's: a replay shows everything. Automated screenshots look anywhere, so
        // they leave it out unless MACHINES_FOG=1 asks for it.
        var screenshot = System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT") is { Length: > 0 };
        if (!Watching && (!screenshot || System.Environment.GetEnvironmentVariable("MACHINES_FOG") == "1"))
        {
            // The original uncovered and tested its map image, one pixel at a time.
            Fog = new FogOfWar(World, Player, map is null ? 6 : surface.Width / map.GetWidth());
            AddChild(Fog);
        }

        SyncViews();
        _hud.Message(_assets.HasClassicModels
            ? "Classic models loaded from your game copy."
            : "No classic models found (see tools/classic-export); showing simple shapes.");
    }

    /// <summary>Sun, sky and fog from the planet's .env file, following the game clock.</summary>
    /// <summary>
    /// The scenery and destructible objects the scenario sets on the planet (ARTEFACTS: trees, rocks, wrecks,
    /// ruins), each at its place and turned as the scenario says, standing on the ground. They are drawn only; the
    /// simulation does not know them yet.
    /// </summary>
    private Node3D Artefacts(ArtefactSection section)
    {
        var root = new Node3D { Name = "Artefacts" };
        if (section.ModelsPath is not { } models)
            return root;
        Vector3 At(Vector2 position, float z) => new(position.X, HeightField.Current?.At(position.X, position.Y) ?? z, position.Y);
        var placements = section.Placements.ToList();
        if (GraphicsSettings.Remake)
        {
            // The remake's trees and rocks stand in for the original's (Scenery); the rest keeps its model.
            var (scenery, left) = Scenery.Place(placements, p =>
                (p.TypeName, At(new Vector2(p.Position.X, p.Position.Y), p.Position.Z), p.Orientation, _assets.ArtefactMesh(models, p.TypeName)));
            root.AddChild(scenery);
            placements = left;
        }
        foreach (var placement in placements)
        {
            if (_assets.ArtefactMesh(models, placement.TypeName) is not { } mesh)
                continue;
            var at = At(new Vector2(placement.Position.X, placement.Position.Y), placement.Position.Z);
            root.AddChild(new MeshInstance3D
            {
                Name = placement.TypeName,
                Mesh = mesh,
                Transform = new Transform3D(new Basis(Vector3.Up, -Mathf.DegToRad(placement.Orientation)), at),
                // The red moons' lava pools glow and creep in the remake, as the lava on the ground does.
                MaterialOverride = GraphicsSettings.Remake && placement.TypeName.Equals("lava", StringComparison.OrdinalIgnoreCase) ? Scenery.Lava() : null,
            });
        }
        return root;
    }

    private void AddLighting()
    {
        // For automated checks: MACHINES_CLOCK=<seconds> moves the planet's day on by that much, "noon" to the sun's highest.
        var setting = System.Environment.GetEnvironmentVariable("MACHINES_CLOCK");
        var clock = float.TryParse(setting, System.Globalization.CultureInfo.InvariantCulture, out var c) ? c
            : setting == "noon" ? PlanetLook.Noon(World.Planet.Environment) : 0;
        var planet = MatchRequest.Current.Planet;
        AddChild(PlanetLook.Create(World.Planet.Environment, planet, () => World.Time + clock, () => _driven is not null ? 0 : _rig?.Distance ?? 160,
            satellite => _assets.PlanetSky(planet, satellite), _assets.PlanetSkyDome(planet)));
    }

    // ---- simulation loop ------------------------------------------------------------------------

    public override void _Process(double delta)
    {
        ReadNetwork();
        if (Online && World.Winner is null && _session.Ready(CatchUpLead) >= CatchUpLead)
        {
            CatchUp();
        }
        else if ((!Paused || Online) && World.Winner is null)
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
                        _hud.Message(rejected.Reason);
                    ShowEvents();
                    SyncBlasts();
                    SyncViews();
                    // For automated checks: MACHINES_CHECKSUM_AT=<tick> prints the world's checksum at that tick.
                    if (System.Environment.GetEnvironmentVariable("MACHINES_CHECKSUM_AT") is { } at && long.TryParse(at, out var checkTick) && World.Tick == checkTick)
                        GD.Print($"checksum at tick {World.Tick}: {World.Checksum():x16}");
                }
                _waitingFor = stepped > 0 ? 0 : _waitingFor + GameWorld.Dt;
            }
            if (_waitingFor > 1)
            {
                var names = _session.Waiting().Select(race => MatchRequest.Current.Network?.Players.FirstOrDefault(p => p.Slot == race)?.Name ?? race.ToString());
                _hud.Banner($"Waiting for {string.Join(", ", names)}");
            }
        }

        if (_session.DesyncTick is { } desync)
            _hud.Banner($"Out of sync since tick {desync}: the players' games differ");
        else if (World.Winner is { } winner)
            _hud.Banner(Watching ? $"{Look(winner)} wins" : winner == Player ? "Victory" : "Defeat");
        else if (World.Races[Player].Defeated && !Watching)
            _hud.Banner("Defeat");
        else if (_waitingFor <= 1)
            _hud.Banner(null);
        if (!_musicEnded && !Watching && (World.Winner is not null || World.Races[Player].Defeated))
        {
            _musicEnded = true;
            Music.End(this, World.Winner == Player);
        }
        if (World.Winner is not null)
            SaveReplay();

        UpdateGhost();
        UpdateFirstPersonCamera();
        ShowInteriors();
        ShowHealing();
        if (Fog is not null)
        {
            Fog.Update(delta, new NVector2(_rig.Position.X, _rig.Position.Z), _driven is not null);
            _audio.Muted = Fog.Blind;
        }
        UpdateHover();
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
        var resume = MatchRequest.Current.ResumeFrom is { } from && World.Tick < from ? $" of {from}" : "";
        _hud.Banner($"Catching up: tick {World.Tick}{resume}");
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
        // e.g. "punchA,3": every machine with that animation plays it once, 3 s in (after loading), to look at it.
        if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_PLAY")?.Split(',') is [var play, var playAt]
            && double.Parse(playAt, System.Globalization.CultureInfo.InvariantCulture) is var startAt
            && _runTime < startAt && _runTime + delta >= startAt)
        {
            foreach (var view in _views.Values)
                view.Play(play);
        }
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
            Issue(new PlaceConstructionCommand(Player, constructors, type, sub, level, spot, 0));        }
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
            // MACHINES_SCREENSHOT_MENU=cover: the self-destruct button's cover is open.
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_MENU") == "cover")
                _hud.Panel.OpenSelfDestructCover();
            if (System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_FIRSTPERSON") == "1")
            {
                Select(_selection.Take(1).ToList());
                EnterFirstPerson();
                // MACHINES_SCREENSHOT_LOOK=-20 tilts the view that many degrees (down when negative); "-20,60" also
                // turns the upper body 60 degrees (to the right when positive).
                var looks = System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_LOOK")?.Split(',') ?? [];
                if (looks.Length > 0 && float.TryParse(looks[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var look))
                    _lookPitch = Mathf.DegToRad(look);
                if (looks.Length > 1 && float.TryParse(looks[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var head))
                    _headTarget = Mathf.DegToRad(head);
            }
        }
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
                view = ActorView.Create(actor, _assets, _audio);
                _views[actor.Id] = view;
                AddChild(view);
                // A building clears the grass and stones from the ground it stands on.
                if (actor is Construction placed && GetNodeOrNull<GroundScatter>("Terrain/GroundScatter") is { } scatter)
                    scatter.ClearUnder(new Vector2(placed.Footprint.Min.X - 1, placed.Footprint.Min.Y - 1),
                        new Vector2(placed.Footprint.Max.X + 1, placed.Footprint.Max.Y + 1));
            }
            // Upper bodies and towers' heads turn to what they attack, ordered or of their own choice, and to
            // whom they heal; one driven in first person turns where its driver looks.
            view.Sync(actor, actor switch
            {
                Machine { Order: AttackOrder attack } => World.Find(attack.Target),
                Machine { Order: HealOrder heal } => World.Find(heal.Target),
                _ => World.AimOf(actor.Id),
            });
            if (actor is Machine { DrivenByPlayer: true } driven)
                view.UpperBodyAngle = driven.Id == _driven?.Id ? _headTarget : World.PlayerHeads.GetValueOrDefault(driven.Id);
            // In the remake tracks and wheels leave their marks on the ground behind them.
            if (GraphicsSettings.Remake && actor is Machine { Inside: null, Stats.Locomotion: Locomotion.Tracks or Locomotion.Wheels } rolling)
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
            // A transporter loading or unloading ore glows with rings of light (MachPhysResourceCarrier::doLoading).
            if (actor is Machine carrier)
            {
                if (_oreCarried.TryGetValue(carrier.Id, out var carried) && carried != carrier.Ore)
                    _support.Loading(view);
                _oreCarried[carrier.Id] = carrier.Ore;
            }
            // Constructors at work reach out to the building with beams and sparks.
            if (actor is Machine { Spec.Type: "CONSTRUCTOR" } constructor)
                view.Construct(World.WorkedOn(constructor) is { } site ? ActorView.World((site.Footprint.Min + site.Footprint.Max) / 2) + Vector3.Up * 2 : null,
                    _assets.EffectMesh("torch"), _assets.EffectMesh("sparks"));
            if (actor is Construction building)
            {
                view.Working = World.IsWorking(building);
                // The doors open for its own machines going through or waiting before them.
                view.DoorsOpen = building.EntranceOutside is { } door && World.Machines.Any(m => m.Inside is null
                    && (m.Doorway == building.Id || m.Race == building.Race && NVector2.DistanceSquared(m.Position, door) < 10 * 10));
            }
        }
        SyncOreHolograms();
        SyncDebris();
        SyncLandMines();

        foreach (var id in _views.Keys.Where(id => World.Find(id) is null).ToList())
        {
            // A destroyed machine flies apart in the remake; anything else just goes.
            if (GraphicsSettings.Remake && _destroyed.Remove(id))
                _views[id].Shatter(_effects);
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
            var at = ActorView.World(e.Position) + Vector3.Up * 2;
            switch (e.Kind)
            {
                case GameEventKind.Fired:
                    // The punch is no shot: the Gorilla swings, and its blast follows when the fists land.
                    if (e.Detail == "GORILLA_PUNCH")
                    {
                        if (_views.TryGetValue(e.Actor, out var puncher))
                            puncher.Punch();
                    }
                    // Flying rounds are drawn in flight (ProjectileViews); the others streak to their mark. Both
                    // flash at the weapon's muzzle.
                    else if (World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail) is { } spec && GameWorld.Flies(spec))
                    {
                        var muzzle = MuzzleOf(e.Actor, spec) ?? at;
                        _effects.Shot(muzzle, muzzle, e.Detail ?? "", spec.ProjectileSpeed);
                    }
                    // The electric charger strikes with lightning, and a halo runs over its victim.
                    else if (e.Detail == "ELECTRIC_CHARGE" && e.Other is { } struck && World.Find(struck) is { } shocked
                             && World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail) is { } charge)
                    {
                        var victimView = _views.GetValueOrDefault(struck);
                        _support.Lightning(MuzzleOf(e.Actor, charge) ?? at, victimView?.Centre ?? ActorView.World(shocked.Position) + Vector3.Up * 1.5f, victimView);
                    }
                    else if (e.Other is { } target && World.Find(target) is { } victim)
                    {
                        var weapon = World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == e.Detail);
                        Shot(weapon is null ? at : MuzzleOf(e.Actor, weapon) ?? at, ActorView.World(victim.Position) + Vector3.Up * 1.5f, e.Detail ?? "");
                    }
                    _audio.Play(SoundTable.FiringSound(e.Detail ?? ""), at);
                    break;
                case GameEventKind.Hit when e.Detail is { } weapon:
                    // A flamethrower sets the machine it hits alight.
                    if (weapon.StartsWith("FLAME_THROWER", StringComparison.Ordinal) && World.Find<Machine>(e.Actor) is not null
                        && _views.TryGetValue(e.Actor, out var burning))
                        _support.Burn(burning);
                    // The machine being driven, hit, now and then crackles in the driver's ears (one hit in twenty).
                    if (e.Actor == _driven?.Id && Random.Shared.Next(20) == 0)
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
                case GameEventKind.Destroyed:
                    _destroyed.Add(e.Actor);
                    if (GraphicsSettings.Remake)
                        _effects.Explosion(at - Vector3.Up * 2, _views.TryGetValue(e.Actor, out var gone) ? gone.Size : 4);
                    else
                        Flash(at);
                    if (_views.TryGetValue(e.Actor, out var dead))
                    {
                        _audio.Play(SoundTable.ExplosionSound(dead.IsBuilding, dead.Size, Random.Shared.Next(1, 7)), at);
                        // A building blowing up shakes the earth, the bigger the harder (MachPhysObjectExplosion).
                        if (dead.IsBuilding)
                            CameraRig.Shake(at, dead.Size >= 20 ? 10 : 5.5f, 1.2f);
                        if (dead.Race == Player)
                            _audio.Say(dead.Voice is { } voice ? $"VID_{voice}_DESTROYED" : "VID_POD_BUILDING_DESTROYED");
                    }
                    break;
                case GameEventKind.MachineBuilt when World.Find<Machine>(e.Actor) is { } built && built.Race == Player:
                    _hud.Message($"Machine ready: {e.Detail}");
                    if (SoundTable.VoiceOf(built) is { } builtVoice)
                        _audio.Say($"VID_{builtVoice}_BUILT");
                    break;
                case GameEventKind.ConstructionComplete when World.Find(e.Actor)?.Race == Player:
                    _hud.Message($"Construction complete: {e.Detail}");
                    _audio.Say("VID_DOZER_BUILDING_COMPLETE");
                    break;
                case GameEventKind.ResearchComplete when World.Find(e.Actor)?.Race == Player:
                    _hud.Message($"Research complete: {e.Detail}");
                    _audio.Say("VID_TECHNICIAN_RESEARCH_COMPLETE");
                    break;
                case GameEventKind.Boarded when World.Find(e.Actor)?.Race == Player:
                    _audio.Say("VID_APC_UNIT_BOARDED");
                    break;
                case GameEventKind.Deployed when World.Find(e.Actor)?.Race == Player:
                    _hud.Message($"APC deployed {e.Detail} machines");
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
                // A spy drops a mine with a click (SID_MINE_DROP); one going off throws up a blast under the machine.
                case GameEventKind.MineLaid:
                    _audio.Play("SID_MINE_DROP", at);
                    break;
                case GameEventKind.MineExploded:
                {
                    var blast = ActorView.World(e.Position);
                    if (GraphicsSettings.Remake)
                        _effects.Explosion(blast, 3);
                    else
                        _effects.Impact(blast);
                    _audio.Play(SoundTable.ExplosionSound(false, 3, Random.Shared.Next(1, 7)), blast);
                    CameraRig.Shake(blast, 4, 0.4f);
                    break;
                }
                // A scavenger loads the wreck with the carriers' rings of light (MachLogScavengeOperation's doLoading).
                case GameEventKind.Scavenged:
                    if (_views.TryGetValue(e.Actor, out var scavenger))
                        _support.Loading(scavenger);
                    break;
                case GameEventKind.SiteDiscovered when e.Detail == Player.ToString():
                    _hud.Message("Mineral site found");
                    _audio.Say("VID_GEO_MINERAL_LOCATED");
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

    /// <summary>Building interiors show only to first person looking out from inside them.</summary>
    private void ShowInteriors()
    {
        Vector3? eye = _driven is not null ? _firstPersonCamera.GlobalPosition : null;
        foreach (var view in _views.Values)
            if (view.HasInterior)
                view.ShowInteriorFrom(eye);
    }

    /// <summary>Waits for a left click on the target of the building's ion cannon or nuclear missile.</summary>
    public void BeginStrike(int launcher) =>
        BeginTargeting("Left click the target; right click cancels", "attack", (point, _) => Order(new StrikeCommand(Player, launcher, point)));

    /// <summary>
    /// The next left click picks the target for <paramref name="onClick"/>; a right click cancels. Meanwhile the
    /// mouse shows <paramref name="cursor"/> (see <see cref="ClassicCursor"/>).
    /// </summary>
    public void BeginTargeting(string message, string cursor, Action<NVector2, Actor?> onClick)
    {
        CancelPlacement();
        _targeting = onClick;
        _targetingCursor = cursor;
        _hud.Message(message);
    }

    /// <summary>
    /// Left clicks pick points for <paramref name="onDone"/>: with Shift held each adds one more, with the waypoint
    /// click, and the first without Shift is the last (MachGuiPatrolCommand, MachGuiDropLandMineCommand).
    /// </summary>
    public void BeginWaypoints(string message, string cursor, Action<List<NVector2>> onDone)
    {
        var points = new List<NVector2>();
        void Next(NVector2 point, Actor? _)
        {
            points.Add(point);
            if (Input.IsKeyPressed(Key.Shift))
            {
                _audio.PlayGui("gui/sounds/waypoint.wav");
                BeginTargeting(message, cursor, Next);
            }
            else
                onDone(points);
        }
        BeginTargeting(message, cursor, Next);
    }

    /// <summary>The next left click sets the selected factories' assembly point (MachGuiAssemblyPointCommand).</summary>
    public void BeginAssemblyPoint()
    {
        var factories = _selection.Select(World.Find<Construction>).OfType<Construction>()
            .Where(c => c.Race == Player && c.Complete && c.IsType("FACTORY")).Select(c => c.Id).ToList();
        if (factories.Count > 0)
            BeginTargeting("Click where new machines gather; right click cancels", "assembl",
                (point, _) => Order(new AssemblyPointCommand(Player, factories, point)));
    }

    private string _targetingCursor = "move";
    private ClassicCursor _cursor = null!;

    /// <summary>
    /// The cursor for what lies under the mouse, as MachGuiCommand's cursorOnActor and cursorOnTerrain chose it: the
    /// arrow over the panel, edge arrows while scrolling, the targeting mode's own, attack over an enemy, select
    /// over your own, move over open ground when machines are selected.
    /// </summary>
    private string CursorKind()
    {
        var mouse = GetViewport().GetMousePosition();
        var size = GetViewport().GetVisibleRect().Size;
        if (GetViewport().GuiGetHoveredControl() is not null || _placing is not null || _driven is not null)
            return "arrow";
        const float edge = 6;
        var vertical = mouse.Y <= edge ? "n" : mouse.Y >= size.Y - edge ? "s" : "";
        var horizontal = mouse.X <= edge ? "w" : mouse.X >= size.X - edge ? "e" : "";
        if (vertical.Length + horizontal.Length > 0 && _rig.EdgePan)
            return "scrl" + vertical + horizontal;
        if (_targeting is not null)
            return _targetingCursor;
        var actor = _hovered;
        var mine = SelectedMachines().Where(m => m.Race == Player).ToList();
        if (actor is not null && World.AreEnemies(Player, actor.Race))
            return mine.Any(m => m.CanFight) ? "attack" : "select";
        if (actor is not null)
            return "select";
        if (mine.Count == 0)
            return "arrow";
        // Scavengers over wreckage (MachGuiIntelligentCursorOnActor's SCAVENGE_CURSOR).
        if (_hoveredDebris is not null && mine.Any(m => m.IsScavenger))
            return "scav";
        return GroundAt(mouse) is { } ground && World.Nav.IsFree(ground, mine.Any(GameWorld.CrossesWater)) ? "move" : "noentry";
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
                if (GraphicsSettings.Remake && blast.Weapon is "ION_ORBITAL_CANNON" or "NUCLEAR_MISSILE")
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
                    AlbedoColor = GraphicsSettings.Remake && blast.Weapon != "VORTEX" ? new Color(colour.R, colour.G, colour.B, colour.A * 0.6f) : colour,
                    BlendMode = GraphicsSettings.Remake && blast.Weapon != "VORTEX" ? BaseMaterial3D.BlendModeEnum.Add : BaseMaterial3D.BlendModeEnum.Mix,
                    Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                    ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
                    CullMode = BaseMaterial3D.CullModeEnum.Disabled,
                },
            };
            _effects.AddChild(view);
            view.GlobalPosition = ActorView.World(blast.Position);
            _blastViews[blast] = view;
            if (GraphicsSettings.Remake)
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
    private void Shot(Vector3 from, Vector3 to, string weapon)
    {
        if (GraphicsSettings.Remake)
            _effects.Shot(from, to, weapon, World.Data.Parm.Weapons.FirstOrDefault(w => w.TypeName == weapon)?.ProjectileSpeed ?? 80);
        else
            Tracer(from, to, weapon);
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
                        // Cleared first: a command taking more points sets itself up again.
                        _targeting = null;
                        if (GroundAt(left.Position) is { } point)
                            onClick(point, ActorAt(left.Position));
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
            // Space turns the building a quarter, as in the original (MachGuiConstructCommand); R does too.
            case Key.Space or Key.R when _placing is not null:
                _placingOrientation = (_placingOrientation + 90) % 360;
                break;
            case Key.S when key.CtrlPressed || key.ShiftPressed:
                Order(new StopCommand(Player, _selection.ToList()));
                break;
            case Key.F:
                EnterFirstPerson();
                break;
            // Ctrl and a number forms the selection into that squadron, the number alone selects it
            // (MachGuiSquadronBank); Tab steps the initiative (MachGuiDefconCommand).
            case >= Key.Key0 and <= Key.Key9 when key.CtrlPressed:
                FormSquadron((int)(key.Keycode - Key.Key0));
                break;
            case >= Key.Key0 and <= Key.Key9:
                SelectSquadron((int)(key.Keycode - Key.Key0));
                break;
            case Key.Tab:
                CycleInitiative();
                break;
            case Key.B:
                BeginAssemblyPoint();
                break;
            case Key.P when Online:
                _hud.Message("A multiplayer match cannot be paused");
                break;
            case Key.P:
                Paused = !Paused;
                _hud.Message(Paused ? "Paused" : "Running");
                break;
            case Key.F10:
                SaveReplay();
                if (Online)
                    NetLink.Send(new LeaveRoom());
                GetTree().ChangeSceneToFile("res://scenes/Menu.tscn");
                break;
        }
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
                    _hud.Message($"{PlayerName(left.Race)} left the match; the AI takes over");
                    break;
                case PlayerReturned back when back.Race != Player && back.FromTick >= (MatchRequest.Current.ResumeFrom ?? 0):
                    _hud.Message($"{PlayerName(back.Race)} is back and takes over from the AI");
                    break;
                case Chat chat:
                    _hud.Message($"{chat.From}: {chat.Text}");
                    _audio.PlayGui("gui/sounds/chatmsg.wav");
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
            var match = MatchRequest.Current.ToMatchInfo(_seed, _session.InputDelay);
            var file = System.IO.Path.Combine(folder, $"{DateTime.Now:yyyy-MM-dd_HH-mm}_{match.Scenario}{Replay.Extension}");
            new Replay(match, _session.Played, World.Tick, _session.Presence).Save(file);
            _hud.Message($"Replay saved: {System.IO.Path.GetFileName(file)}");
        }
        catch (IOException exception)
        {
            _hud.Message($"Replay not saved: {exception.Message}");
        }
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
    private static readonly Vector2? PointedForScreenshot =
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
        var inWorld = _driven is null && (PointedForScreenshot is not null || GetViewport().GuiGetHoveredControl() is null);
        _hovered = inWorld ? ActorAt(mouse) : null;
        _hoveredDebris = inWorld && _hovered is null ? DebrisAt(mouse) : null;
        var view = _hovered is not null ? _views.GetValueOrDefault(_hovered.Id) : null;
        if (view != _highlighted)
        {
            if (_highlighted is not null && IsInstanceValid(_highlighted))
                _highlighted.Highlighted = false;
            _highlighted = view;
        }
        if (view is not null)
            view.Highlighted = !view.Selected;
        _hud.Panel.Prompt = !inWorld ? null
            : _hovered is not null ? _hud.Panel.PromptFor(_hovered)
            : _hoveredDebris is { } wreck ? $"Debris   {wreck.Quantity} BMU"
            : SiteAt(mouse) is { } site ? ClassicPanel.DepositPrompt(site)
            : null;
    }

    // The wreck the mouse points at this frame, when no actor is in front of it.
    private DebrisState? _hoveredDebris;

    /// <summary>The selected scavengers (level-3 resource carriers), which gather wreckage.</summary>
    private List<int> SelectedScavengers() => SelectedMachines().Where(m => m.IsScavenger).Select(m => m.Id).ToList();

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

    /// <summary>Moves the strategy camera over a ground point.</summary>
    public void LookAt(NVector2 point) => _rig.Position = new Vector3(point.X, 0, point.Y);

    public void Select(List<int> ids)
    {
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
        _hud.Message($"Squadron {squad} formed");
    }

    /// <summary>The members of squadron <paramref name="squad"/> are selected (those inside an APC or a lab stay out).</summary>
    public void SelectSquadron(int squad) =>
        Select(World.Machines.Where(m => m.Race == Player && m.Squad == squad && m.Inside is null).Select(m => m.Id).ToList());

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
                Order(new CaptureCommand(Player, capturers, target.Id));
            Order(new AttackCommand(Player, Ids(m => m.CanFight), target.Id));
            Order(new MoveCommand(Player, Ids(m => !m.CanFight && !capturers.Contains(m.Id)), target.Position));
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
            if (building.IsType("GARRISON") && building.Complete)
            {
                Order(new EnterGarrisonCommand(Player, rest.ToList(), building.Id));
                rest.Clear();
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
        _ghost = new MeshInstance3D
        {
            Mesh = _assets.MeshFor(id, Look(Player), size),
            MaterialOverride = new StandardMaterial3D
            {
                AlbedoColor = new Color(0.3f, 1, 0.3f, 0.4f),
                Transparency = BaseMaterial3D.TransparencyEnum.Alpha,
                ShadingMode = BaseMaterial3D.ShadingModeEnum.Unshaded,
            },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
        };
        AddChild(_ghost);
        _hud.Message("Left click to place, Space to turn, right click to cancel");
    }

    private void CancelPlacement()
    {
        _placing = null;
        _ghost?.QueueFree();
        _ghost = null;
    }

    private void UpdateGhost()
    {
        if (_placing is not { } p || _ghost is null || GroundAt(PointedForScreenshot ?? GetViewport().GetMousePosition()) is not { } at)
            return;
        // Placed and turned as the building's own view will be (ActorView). A mine goes where the mouse is, as
        // anything else: anywhere near a found site will do (MachGuiConstructCommand never moved it onto the site).
        _ghost.Position = ActorView.World(at);
        _ghost.Rotation = new Vector3(0, -Mathf.DegToRad(_placingOrientation), 0);
        var ok = World.WhyCannotPlace(Player, p.Type, p.SubType, p.Level, at, _placingOrientation) is null;
        ((StandardMaterial3D)_ghost.MaterialOverride).AlbedoColor = ok ? new Color(0.3f, 1, 0.3f, 0.4f) : new Color(1, 0.25f, 0.2f, 0.4f);
    }

    private void PlaceBuilding()
    {
        if (_placing is not { } p || GroundAt(GetViewport().GetMousePosition()) is not { } ground)
            return;
        var constructors = SelectedMachines().Where(m => m.Spec.Type == "CONSTRUCTOR").Select(m => m.Id).ToList();
        // A site that can't stand there gets the original's refusal click (MachGuiConstructCommand).
        if (World.WhyCannotPlace(Player, p.Type, p.SubType, p.Level, ground, _placingOrientation) is { } why)
        {
            _audio.PlayGui("gui/sounds/clickbad.wav");
            _hud.Message(why);
            return;
        }
        Issue(new PlaceConstructionCommand(Player, constructors, p.Type, p.SubType, p.Level, ground, _placingOrientation));
        // With Shift held more sites follow, each with the waypoint click; the last one sets the constructors to
        // work with the command's own click (MachGuiConstructCommand, MachGuiCommand::apply).
        if (Input.IsKeyPressed(Key.Shift))
            _audio.PlayGui("gui/sounds/waypoint.wav");
        else
        {
            _audio.PlayGui("gui/sounds/taskmach.wav");
            CancelPlacement();
        }
    }

    // ---- first person ---------------------------------------------------------------------------

    public void EnterFirstPerson()
    {
        if (SelectedMachines().FirstOrDefault() is not { } machine)
        {
            _hud.Message("Select one of your machines to drive it (F)");
            return;
        }
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
        // The radar comes on, and a machine with weapons readies them (MachGuiFirstPerson).
        if (machine.Weapons.Count > 0)
            _audio.PlayGui("gui/sounds/weapchng.wav");
        _hud.Message(machine.CanTurnUpperBody
            ? "First person: W/S drive, A/D or mouse turn, Shift or right mouse turns the upper body (C centres it), left mouse fire, Tab weapons, F or Esc to leave"
            : "First person: W/S drive, A/D or mouse turn, left mouse fire, Tab weapons, F or Esc to leave");
    }

    private void LeaveFirstPerson()
    {
        if (_driven is { } machine && World.Find(machine.Id) is not null)
            Issue(new DriveCommand(Player, machine.Id, Driving: false));
        if (_driven is { } was && _views.TryGetValue(was.Id, out var view))
            view.SetDriven(false);
        _driven = null;
        _headTarget = 0;
        _rightButtonTurnedHead = false;
        _rig.Active = true;
        _rig.Camera.Current = true;
        Input.MouseMode = Input.MouseModeEnum.Visible;
        _hud.FirstPerson = false;
    }

    private float _mouseTurn;

    // Where the driven machine's upper body is to turn, radians anticlockwise from its hull in the original's axes
    // (on screen, mirrored, positive is to the right); it gets there at its BODY_TURN_RATE.
    private float _headTarget;

    // The right button turned the upper body; letting go of it brings the upper body back to the front.
    private bool _rightButtonTurnedHead;

    /// <summary>
    /// Whether the mouse and A/D turn the driven machine's upper body rather than the whole machine: with Shift or
    /// the right button held, for a machine whose upper body turns (MachGuiFirstPerson::doUpdate).
    /// </summary>
    private bool TurningHead => _driven is { CanTurnUpperBody: true } && (Input.IsKeyPressed(Key.Shift) || Input.IsMouseButtonPressed(MouseButton.Right));

    private void FirstPersonInput(InputEvent @event)
    {
        switch (@event)
        {
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.F or Key.Escape }:
                LeaveFirstPerson();
                break;
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.Tab }:
                SelectNextWeapons();
                break;
            // C (the original's keypad 5) turns the upper body back to the front.
            case InputEventKey { Pressed: true, Echo: false, Keycode: Key.C or Key.Kp5 }:
                _headTarget = 0;
                break;
            case InputEventMouseButton { ButtonIndex: MouseButton.Right, Pressed: false } when _rightButtonTurnedHead:
                _rightButtonTurnedHead = false;
                _headTarget = 0;
                break;
            case InputEventMouseMotion motion when TurningHead:
                _rightButtonTurnedHead |= Input.IsMouseButtonPressed(MouseButton.Right);
                _headTarget = Mathf.Wrap(_headTarget + motion.Relative.X * 0.004f, -Mathf.Pi, Mathf.Pi);
                LookUpDown(motion);
                break;
            case InputEventMouseMotion motion:
                _mouseTurn -= motion.Relative.X * 0.02f;
                LookUpDown(motion);
                break;
        }
    }

    private void LookUpDown(InputEventMouseMotion motion)
    {
        var limits = World.Data.Parm.General;
        _lookPitch = Mathf.Clamp(_lookPitch - motion.Relative.Y * 0.002f,
            -Mathf.DegToRad(limits.FirstPersonLookMaxAngle), Mathf.DegToRad(-limits.FirstPersonLookMinAngle));
    }

    // Which weapons fire in first person (MachGuiFirstPerson::doWeaponSelect): one of them by its index, or all
    // of them when it equals their number.
    private int _weaponSelect = int.MaxValue;

    /// <summary>The selected weapons as one bit each, in mounting order.</summary>
    private int SelectedWeapons(Machine machine) =>
        _weaponSelect >= machine.Weapons.Count ? -1 : 1 << _weaponSelect;

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

    private void UpdateDriving()
    {
        if (_driven is not { } machine)
            return;
        var forward = (Input.IsKeyPressed(Key.W) ? 1f : 0f) - (Input.IsKeyPressed(Key.S) ? 1f : 0f);
        var keys = (Input.IsKeyPressed(Key.A) ? 1f : 0f) - (Input.IsKeyPressed(Key.D) ? 1f : 0f);
        var turn = Mathf.Clamp(_mouseTurn, -1, 1);
        _mouseTurn *= 0.5f;
        // With Shift (or the right button) held, A and D turn the upper body as the original's Shift and arrows did.
        if (TurningHead)
            _headTarget = Mathf.Wrap(_headTarget - keys * Mathf.DegToRad(machine.Stats.BodyTurnRate ?? 180) * GameWorld.Dt, -Mathf.Pi, Mathf.Pi);
        else
            turn += keys;

        // While the left button is held the weapons fire where the crosshair points: at the enemy under it, the
        // ground it rests on, or straight ahead (where the upper body faces) as far as they reach, whether or not
        // anything is there.
        NVector2? aimAt = null;
        if (Input.IsMouseButtonPressed(MouseButton.Left) && machine.CanFight)
        {
            // None of the selected weapons charged: the original's dud click, at most every 0.8 s (MachGuiFirstPerson).
            var selected = SelectedWeapons(machine);
            var now = Time.GetTicksMsec() / 1000.0;
            if (machine.Weapons.Where((_, i) => (selected & (1 << i)) != 0).Any(w => (w.DealsDamage || w.IsTreachery) && w.ChargePercent >= 100))
                _lastCouldFire = now;
            else if (now - _lastCouldFire > 0.8)
            {
                _audio.PlayGui("gui/sounds/nowefire.wav");
                _lastCouldFire = now;
            }
            var range = machine.Weapons.Max(w => w.Spec.Range);
            var centre = GetViewport().GetVisibleRect().Size / 2;
            var looking = machine.Facing + HeadNow(machine);
            var ahead = machine.Position + new NVector2(MathF.Cos(looking), MathF.Sin(looking)) * range;
            aimAt = ActorAt(centre) is { } enemy && World.AreEnemies(Player, enemy.Race) ? enemy.Position
                : GroundAt(centre) is { } ground && NVector2.Distance(ground, machine.Position) > 2 ? ground
                : ahead;
        }
        // The simulation turns left (counter-clockwise in the original's axes) for a positive turn; the client
        // shows the world with y and z swapped, which mirrors it, so on screen that is a turn to the right.
        Issue(new DriveCommand(Player, machine.Id, Driving: true, forward, Mathf.Clamp(-turn, -1, 1), aimAt, SelectedWeapons(machine), _headTarget));
    }

    // When a selected weapon of the driven machine was last charged with the trigger held.
    private double _lastCouldFire;

    /// <summary>Where the driven machine's upper body has turned to so far, radians anticlockwise from its hull.</summary>
    private float HeadNow(Machine machine) => machine.CanTurnUpperBody && _views.TryGetValue(machine.Id, out var view) ? view.UpperBodyNow : 0;

    private void UpdateFirstPersonCamera()
    {
        if (_driven is not { } machine || !_views.TryGetValue(machine.Id, out var view))
            return;
        // From the faceplate, as the original, turning with the upper body; the view stays level whatever the
        // machine leans or sways.
        if (machine.CanTurnUpperBody)
            view.UpperBodyAngle = _headTarget;
        var head = HeadNow(machine);
        _firstPersonCamera.GlobalPosition = view.Eye;
        _firstPersonCamera.GlobalRotation = new Vector3(_lookPitch, view.Yaw - head - Mathf.Pi / 2, 0);
        _firstPersonCamera.Basis *= CameraRig.ShakeAt(_firstPersonCamera.GlobalPosition);

        _hud.Weapons.Show(machine.Weapons, SelectedWeapons(machine));
        _hud.Radar.Driven = machine;
        _hud.Radar.Head = head;

        // The crosshair shows what it rests on: an enemy to shoot or a friend to spare (ore and wrecks don't count).
        var under = ActorAt(GetViewport().GetVisibleRect().Size / 2);
        var reach = machine.Weapons.Select(w => w.Spec.Range).DefaultIfEmpty(0).Max();
        var aimed = under is null || under.Id == machine.Id || machine.DistanceTo(under) > reach ? null : under;
        _hud.Crosshair.Target = aimed is null ? null
            : (aimed.HitPoints / (float)Math.Max(1, aimed.MaxHitPoints), aimed.Armour / (float)Math.Max(1, aimed.MaxArmour));
        // The compass reads the heading as the original's azimuth: degrees anticlockwise from its x axis.
        _hud.Crosshair.Heading = Mathf.RadToDeg(machine.Facing + head);
        _hud.Crosshair.Aim = aimed is null ? Crosshair.Sight.None
            : World.AreEnemies(Player, aimed.Race) ? Crosshair.Sight.Enemy
            : Crosshair.Sight.Friend;
    }
}
