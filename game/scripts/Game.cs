using Godot;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Combat;
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

    // First person
    private Machine? _driven;
    private Camera3D _firstPersonCamera = null!;
    private float _lookPitch;

    public GameWorld World { get; private set; } = null!;
    public IReadOnlyList<int> Selection => _selection;
    public bool Paused { get; set; }

    /// <summary>Gives the player's command to the world (in multiplayer: to every player's world, same tick).</summary>
    public void Issue(Command command)
    {
        if (!Watching)
            _session.Queue(command);
    }

    /// <summary>A replay: the player watches and cannot give orders.</summary>
    public bool Watching => MatchRequest.Current.Replay is not null;

    public bool Online => MatchRequest.Current.Network is not null;

    private LockstepSession _session = null!;
    private int _seed;
    private double _waitingFor;
    private bool _replaySaved;

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
        AddChild(TerrainBuilder.Build(World.Planet, HeightField.Current, map, _assets.PlanetTerrain(request.Planet)));
        AddLighting();

        _effects = new Effects { Name = "Effects" };
        AddChild(_effects);

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

        _hud = new Hud(this, map);
        AddChild(_hud);

        SyncViews();
        _hud.Message(_assets.HasClassicModels
            ? "Classic models loaded from your game copy."
            : "No classic models found (see tools/classic-export); showing simple shapes.");
    }

    /// <summary>Sun, sky and fog from the planet's .env file, following the game clock.</summary>
    private void AddLighting()
    {
        // For automated checks: MACHINES_CLOCK=<seconds> moves the planet's day on by that much.
        var clock = float.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_CLOCK"), System.Globalization.CultureInfo.InvariantCulture, out var c) ? c : 0;
        AddChild(PlanetLook.Create(World.Planet.Environment, () => World.Time + clock, () => _driven is not null ? 0 : _rig?.Distance ?? 160));
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
        if (World.Winner is not null)
            SaveReplay();

        UpdateGhost();
        UpdateFirstPersonCamera();
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
            var parts = focus.Split(',');
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
            // e.g. "NUCLEAR_MISSILE,800,800": sets the weapon off there at once.
            var parts = blastAt.Split(',');
            var weapon = World.Data.Parm.Weapons.First(w => w.TypeName == parts[0]);
            World.StartBlast(weapon, new NVector2(float.Parse(parts[1], System.Globalization.CultureInfo.InvariantCulture),
                float.Parse(parts[2], System.Globalization.CultureInfo.InvariantCulture)), null);
        }
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_SPAWN") is { Length: > 0 } spawn)
        {
            // e.g. "AGGRESSOR,ASSASSIN,2,L_PLASMA_RIFLE": puts one of those down at the camera, to drive.
            // "Blue:AGGRESSOR,GRUNT,2,L_BOLTER" puts down an enemy instead, a little off, to watch a fight.
            var race = spawn.Split(':') is [var name, _] && Enum.TryParse<Race>(name, out var other) ? other : Player;
            var parts = spawn[(spawn.IndexOf(':') + 1)..].Split(',');
            var at = new NVector2(_rig.Position.X, _rig.Position.Z) + (race == Player ? NVector2.Zero : new NVector2(25, 0));
            World.AddMachine(race, at, parts[0], parts[1] is "" or "-" ? null : parts[1], int.Parse(parts[2]), parts.Length > 3 ? parts[3] : null);
            SyncViews();
        }
        if (_runTime == 0 && System.Environment.GetEnvironmentVariable("MACHINES_SCREENSHOT_SELECT") is { Length: > 0 } select)
        {
            // e.g. "CONSTRUCTOR" or "AGGRESSOR"; with MACHINES_SCREENSHOT_FIRSTPERSON=1 the first one is driven.
            // A building type (FACTORY, HARDWARE_LAB ...) selects the first such building instead.
            var machines = World.Machines.Where(m => m.Race == Player && (m.Spec.Type == select || m.Spec.SubType == select)).OrderByDescending(m => m.Id).Select(m => m.Id).ToList();
            Select(machines.Count > 0 ? machines : World.Constructions.Where(c => c.Race == Player && c.Spec.Type == select).Take(1).Select(c => c.Id).ToList());
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
        SaveReplay();
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
                view = ActorView.Create(actor, _assets, _audio);
                _views[actor.Id] = view;
                AddChild(view);
            }
            view.Sync(actor, actor is Machine { Order: AttackOrder attack } ? World.Find(attack.Target) : null);
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
            var at = ActorView.World(e.Position) + Vector3.Up * 2;
            switch (e.Kind)
            {
                case GameEventKind.Fired:
                    if (e.Other is { } target && World.Find(target) is { } victim)
                        Shot(at, ActorView.World(victim.Position) + Vector3.Up * 1.5f, e.Detail ?? "");
                    _audio.Play(SoundTable.FiringSound(e.Detail ?? ""), at);
                    break;
                case GameEventKind.Hit when e.Detail is { } weapon:
                    _audio.Play(SoundTable.HitSound(weapon, Random.Shared.Next(1, 5)), at);
                    break;
                case GameEventKind.Blast when e.Detail is { } weapon:
                    _audio.Play(SoundTable.BlastSound(weapon), at);
                    break;
                case GameEventKind.Destroyed:
                    if (GraphicsSettings.Remake)
                        _effects.Explosion(at - Vector3.Up * 2, _views.TryGetValue(e.Actor, out var gone) ? gone.Size : 4);
                    else
                        Flash(at);
                    if (_views.TryGetValue(e.Actor, out var dead))
                    {
                        _audio.Play(SoundTable.ExplosionSound(dead.IsBuilding, dead.Size, Random.Shared.Next(1, 7)), at);
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
                case GameEventKind.SiteDiscovered when e.Detail == Player.ToString():
                    _hud.Message("Mineral site found");
                    _audio.Say("VID_GEO_MINERAL_LOCATED");
                    break;
            }
        }
    }

    /// <summary>Waits for a left click on the target of the building's ion cannon or nuclear missile.</summary>
    public void BeginStrike(int launcher) =>
        BeginTargeting("Left click the target; right click cancels", (point, _) => Issue(new StrikeCommand(Player, launcher, point)));

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
                Issue(new StopCommand(Player, _selection.ToList()));
                break;
            case Key.F:
                EnterFirstPerson();
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
                Issue(new CaptureCommand(Player, capturers, target.Id));
            Issue(new AttackCommand(Player, Ids(m => m.CanFight), target.Id));
            Issue(new MoveCommand(Player, Ids(m => !m.CanFight && !capturers.Contains(m.Id)), target.Position));
            Acknowledge(machines.Where(m => m.CanFight), "TARGET_ENEMY");
            return;
        }

        if (target is Machine patient && patient.Race == Player && patient.HitPoints < patient.MaxHitPoints
            && machines.Any(m => m.CanHeal && m.Id != patient.Id))
        {
            Issue(new HealCommand(Player, Ids(m => m.CanHeal && m.Id != patient.Id), patient.Id));
            Acknowledge(machines.Where(m => m.CanHeal), "TASKED");
            return;
        }

        if (target is Machine apc && apc.Race == Player && apc.IsType("APC") && machines.Any(m => m.Id != apc.Id && !m.IsType("APC")))
        {
            Issue(new BoardCommand(Player, Ids(m => m.Id != apc.Id && !m.IsType("APC")), apc.Id));
            Acknowledge(machines, "TASKED");
            return;
        }

        if (target is Construction building && building.Race == Player)
        {
            var rest = machines.Select(m => m.Id).ToHashSet();
            if (!building.Complete)
            {
                var constructors = Ids(m => m.Spec.Type == "CONSTRUCTOR");
                Issue(new ConstructCommand(Player, constructors, building.Id));
                rest.ExceptWith(constructors);
            }
            else if (building.HitPoints < building.MaxHitPoints)
            {
                var constructors = Ids(m => m.Spec.Type == "CONSTRUCTOR");
                Issue(new RepairCommand(Player, constructors, building.Id));
                rest.ExceptWith(constructors);
            }
            if (building.IsType("GARRISON") && building.Complete)
            {
                Issue(new EnterGarrisonCommand(Player, rest.ToList(), building.Id));
                rest.Clear();
            }
            if (building.IsType("HARDWARE_LAB"))
            {
                var technicians = Ids(m => m.Spec.Type == "TECHNICIAN");
                Issue(new EnterLabCommand(Player, technicians, building.Id));
                rest.ExceptWith(technicians);
            }
            if (building.IsType("MINE"))
            {
                var store = World.Constructions.Where(c => c.Race == Player && c.Complete && c.Spec.Type is "SMELTER" or "POD")
                    .MinBy(c => NVector2.Distance(c.Position, building.Position));
                var carriers = Ids(m => m.Spec.Type == "RESOURCE_CARRIER");
                if (store is not null)
                    Issue(new TransportCommand(Player, carriers, building.Id, store.Id));
                rest.ExceptWith(carriers);
            }
            if (rest.Count > 0)
                Issue(new MoveCommand(Player, rest.ToList(), building.Position));
            Acknowledge(machines, "TASKED");
            return;
        }

        if (ground is not { } point)
            return;
        var locators = Ids(m => m.Spec.Type == "GEO_LOCATOR");
        if (locators.Count > 0)
            Issue(new LocateCommand(Player, locators, point));
        var others = Ids(m => m.Spec.Type != "GEO_LOCATOR");
        if (Input.IsKeyPressed(Key.Ctrl))
            Issue(new AttackMoveCommand(Player, others, point));
        else
            Issue(new MoveCommand(Player, others, point));
        Acknowledge(machines, "MOVING");
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
        Issue(new PlaceConstructionCommand(Player, constructors, p.Type, p.SubType, p.Level, Snap(p.Type, ground), _placingOrientation));
        if (!Input.IsKeyPressed(Key.Shift))
            CancelPlacement();
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
        Input.MouseMode = Input.MouseModeEnum.Captured;
        _hud.FirstPerson = true;
        _hud.Message("First person: W/S drive, A/D or mouse turn, left mouse fire, F or Esc to leave");
    }

    private void LeaveFirstPerson()
    {
        if (_driven is { } machine && World.Find(machine.Id) is not null)
            Issue(new DriveCommand(Player, machine.Id, Driving: false));
        if (_driven is { } was && _views.TryGetValue(was.Id, out var view))
            view.SetDriven(false);
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

        // Fire at the enemy closest to the crosshair while the left button is held.
        int? aimAt = null;
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
            aimAt = target?.Id;
        }
        // The simulation turns left (counter-clockwise in the original's axes) for a positive turn; the client
        // shows the world with y and z swapped, which mirrors it, so on screen that is a turn to the right.
        Issue(new DriveCommand(Player, machine.Id, Driving: true, forward, Mathf.Clamp(-turn, -1, 1), aimAt));
    }

    private void UpdateFirstPersonCamera()
    {
        if (_driven is not { } machine || !_views.TryGetValue(machine.Id, out var view))
            return;
        // From the faceplate, as the original; the view stays level whatever the machine leans or sways.
        _firstPersonCamera.GlobalPosition = view.Eye;
        _firstPersonCamera.GlobalRotation = new Vector3(_lookPitch, view.Yaw - Mathf.Pi / 2, 0);
    }
}
