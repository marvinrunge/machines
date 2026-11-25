using System.Diagnostics;
using Godot;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Combat;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// Readies everything a match may show before it is shown, behind the loading screen, so that nothing holds a frame
/// up the first time it comes: first it loads what the match may need (every sound and voice mail, every particle
/// picture and effect of the original, the models of every machine and building each race in the match can have),
/// then it puts one of everything up in front of the camera (each machine and building, damaged, burning and
/// unfinished, every weapon's shot, round, splat and blast, explosions, wrecks, the panels' screens) and keeps it
/// there until Godot has built every pipeline it needed to draw them, and then takes it all away; meanwhile a copy of
/// the match plays its first seconds on a worker thread, so that the simulation's code is compiled. Until it is done
/// the match stands still (see <see cref="Game.Warming"/>), and its first frames still run hidden (<see cref="Game.Hidden"/>).
/// So that the loading screen goes on moving meanwhile, the warm-up leaves it room: two cores free of the loading,
/// a few milliseconds of work in a frame, the machines and buildings put up drawn an eighth at a time in turn (all at
/// once they were some 25,000 draw calls, a tenth of a second a frame; a pipeline is built the first time its group
/// is drawn), and the garbage collector kept from halting every thread while it loads.
/// MACHINES_WARMUP=0 leaves it out; films, the movie maker and runs without a screen do too.
/// </summary>
public partial class Warmup : Node3D
{
    // Milliseconds of loading per frame, so that the loading screen goes on drawing smoothly.
    private const double Budget = 12;

    // The loading on worker threads leaves two cores to the loading screen: Godot's main and rendering threads.
    private static readonly ParallelOptions Loaders = new() { MaxDegreeOfParallelism = Math.Max(1, System.Environment.ProcessorCount - 2) };

    // How long everything stands at least (the blow-ups' later parts show by then), how many frames in a row must
    // build no pipeline, and when to give up waiting.
    private const double ShownAtLeast = 1.6, GiveUpAfter = 30;
    // The machines and buildings put up are drawn in this many groups, one a frame; the quiet frames see each twice.
    private const int Groups = 8;
    private const int QuietFrames = 2 * Groups;

    // The blow-ups put up as they stand these many seconds on, so that their later parts show too.
    private static readonly float[] Ages = [0, 1.5f, 3, 4.5f, 6, 7.5f, 9];

    // The ticks played on a copy of the match (Rehearse): the computer thinks once a second.
    private const int RehearsedTicks = 3 * GameWorld.TicksPerSecond;

    private GameWorld _world = null!;
    private ClassicAssets _assets = null!;
    private GameAudio _audio = null!;
    private Vector3 _focus;
    private CanvasLayer[] _screens = [];
    private IEnumerator<bool> _steps = null!;
    private readonly Stopwatch _clock = new();
    private readonly List<string> _report = [];
    private bool _muted;
    private System.Runtime.GCLatencyMode _latency;
    private Node3D[] _groups = [];
    private int _turn, _placed;
    private bool _eased;

    /// <summary>Whether everything is ready and the match may begin.</summary>
    public bool Done { get; private set; }

    /// <summary>Whether the match has run its first frames and may be shown.</summary>
    public bool Shown { get; private set; }

    // Frames the match runs hidden once it has begun.
    private const int SettleFrames = 15;
    private int _settled;

    /// <summary>
    /// The warm-up for the match <paramref name="world"/> seen from the camera looking at <paramref name="focus"/>,
    /// with the <paramref name="screens"/> that are not up yet (shown for it); null when it is left out.
    /// </summary>
    public static Warmup? Create(GameWorld world, ClassicAssets assets, GameAudio audio, Vector3 focus, params CanvasLayer[] screens)
    {
        if (System.Environment.GetEnvironmentVariable("MACHINES_WARMUP") == "0" || DisplayServer.GetName() == "headless"
            || Cinematic.Asked is not null || Engine.GetWriteMoviePath().Length > 0)
            return null;
        return new Warmup { Name = "Warmup", _world = world, _assets = assets, _audio = audio, _focus = focus, _screens = screens };
    }

    public override void _Ready()
    {
        _clock.Start();
        _steps = Steps().GetEnumerator();
        // Started without the loading screen (an automated run): it comes up now, over the match it waits for.
        if (!LoadingScreen.Busy)
            LoadingScreen.Show(GetTree(), LoadingScreen.MapName(MatchRequest.Current), "", () => { });
        // What is put up makes its sounds into the muted effects.
        var bus = AudioServer.GetBusIndex(GameAudio.EffectsBus);
        _muted = AudioServer.IsBusMute(bus);
        AudioServer.SetBusMute(bus, true);
        // Room for the loading screen: no collection that halts every thread.
        _latency = System.Runtime.GCSettings.LatencyMode;
        System.Runtime.GCSettings.LatencyMode = System.Runtime.GCLatencyMode.SustainedLowLatency;
        _eased = true;
    }

    // The collector as it was: the match's first frames run so, still hidden.
    private void Unease()
    {
        if (!_eased)
            return;
        _eased = false;
        System.Runtime.GCSettings.LatencyMode = _latency;
    }

    public override void _Process(double delta)
    {
        if (Done)
        {
            // The match's first frames run their code the first time (the panels, the views' first ticks): still hidden.
            Report("Ready", 0.97f + 0.03f * _settled / SettleFrames);
            if (++_settled >= SettleFrames)
                Reveal();
            return;
        }
        // The next group of the crowd drawn this frame, the others not.
        _turn++;
        for (var group = 0; group < _groups.Length; group++)
            _groups[group].Visible = group == _turn % _groups.Length;
        var start = Stopwatch.GetTimestamp();
        while (Stopwatch.GetElapsedTime(start).TotalMilliseconds < Budget)
        {
            if (!_steps.MoveNext())
            {
                Done = true;
                Unease();
                AudioServer.SetBusMute(AudioServer.GetBusIndex(GameAudio.EffectsBus), _muted);
                return;
            }
            // A step that has to see frames drawn.
            if (_steps.Current)
                return;
        }
    }

    // A match left while it is readied keeps no muted effects.
    public override void _ExitTree()
    {
        Unease();
        if (!Done)
            AudioServer.SetBusMute(AudioServer.GetBusIndex(GameAudio.EffectsBus), _muted);
    }

    private void Reveal()
    {
        Shown = true;
        GD.Print($"warm-up: {string.Join(", ", _report)}; {_clock.Elapsed.TotalSeconds:F1} s in all");
        if (HitchLog.On)
            GD.Print($"warm-up loaded: {HitchLog.LoadedBefore()}");
        HitchLog.Playing = true;
        QueueFree();
    }

    private void Note(string what, long since) =>
        _report.Add($"{what} {Stopwatch.GetElapsedTime(since).TotalSeconds:F1} s");

    /// <summary>Tells the loading screen's bar what is loading and how far the whole has come (0 to 1).</summary>
    private static void Report(string step, float done) => (LoadingScreen.Step, LoadingScreen.Progress) = (step, done);

    // Each step yields whether it waits for a frame to be drawn.
    private IEnumerable<bool> Steps()
    {
        var rehearsal = Rehearse(_world.Data, MatchRequest.Current);
        var since = Stopwatch.GetTimestamp();
        var sounds = _audio.Files().ToList();
        var particles = Effects.ParticleNames().ToList();
        var files = sounds.Count + particles.Count;
        for (var i = 0; i < files; i++)
        {
            Report("Sounds", 0.08f * i / files);
            if (i < sounds.Count)
                _audio.Preload(sounds[i]);
            else
                Effects.Particle(particles[i - sounds.Count]);
            yield return false;
        }
        // A story's own sound effects, voices and music (StoryAudio).
        if (_world.Story is not null)
            foreach (var story in StoryAudio.Files())
            {
                StoryAudio.Preload(story);
                yield return false;
            }
        // Stage 7's sections, each package's own (Warmup.Stealth, .Armoury, .Escape).
        if (_world.Story is not null)
            foreach (var step in WarmStealth().Concat(WarmArmoury()).Concat(WarmEscape()))
                yield return step;
        Note("sounds", since);

        // The stage stands where the camera looks; what is put on it is drawn under the loading screen.
        since = Stopwatch.GetTimestamp();
        var ground = new NVector2(_focus.X, _focus.Z);
        var at = ActorView.World(ground);
        var stage = new Node3D { Name = "Stage" };
        AddChild(stage);
        _groups = [.. Enumerable.Range(0, Groups).Select(group => new Node3D { Name = $"Group{group}" })];
        foreach (var group in _groups)
            stage.AddChild(group);
        // A ship's deck has no sun: its shadows come from its ceiling lamps and searchlights, spots that draw what they
        // light into a shadow of their own. One such over the stage, so that each model's shadow is drawn once too (the
        // Locator's Bolter, first drawn so in the workshop, held that frame up by some 100 ms).
        if (_world.Deck is not null)
            stage.AddChild(new SpotLight3D
            {
                Name = "ShadowLamp",
                ShadowEnabled = true,
                SpotRange = 60,
                SpotAngle = 80,
                LightEnergy = 0.5f,
                Transform = new Transform3D(Basis.LookingAt(Vector3.Down, Vector3.Back), at + Vector3.Up * 20),
            });
        // The warm-up's own world: actors made in it are never in the match.
        var world = new GameWorld(_world.Data, _world.Planet, 0);
        var views = new List<(Actor Actor, ActorView View)>();
        foreach (var step in Actors(world, stage, ground, views))
            yield return step;
        Note($"effects, {views.Count} machines and buildings", since);

        since = Stopwatch.GetTimestamp();
        var compiled = HitchLog.Compiled();
        Report("Effects", 0.56f);
        foreach (var step in Shots(world, stage, at, views))
            yield return step;
        foreach (var screen in _screens)
            screen.Visible = true;
        // A story's hack board, drawn once as the hack puts it up (its code, letters and sounds).
        CanvasLayer? hack = null;
        if (_world.Story is not null)
        {
            HackSounds.Preload();
            hack = new CanvasLayer { Layer = 10, Name = "WarmHack" };
            AddChild(hack);
            hack.AddChild(HackHost.Warm());
        }
        // On a deck, the space its cutscenes go out to (the burning mothership, the planet, the blasts), drawn under the
        // loading screen with the rest (FilmCamera.RehearseSpace).
        var space = _world.Deck is not null ? FilmCamera.RehearseSpace(this) : null;
        if (_world.Deck is not null)
        {
            var opening = FilmCamera.OpeningSample();
            stage.AddChild(opening);
            opening.GlobalPosition = at + Vector3.Up * 0.5f;
        }
        // Outside, what a story's cutscene on this map shows (the pod streaking in and landing).
        else
            FilmCamera.RehearseOutside(stage, at, MatchRequest.Current.Scenario);

        // Drawn until Godot has built every pipeline it needed: some frames in a row without a new one.
        var shown = Stopwatch.StartNew();
        var quiet = 0;
        var last = HitchLog.Compiled();
        while (shown.Elapsed.TotalSeconds < GiveUpAfter && (quiet < QuietFrames || shown.Elapsed.TotalSeconds < ShownAtLeast))
        {
            // How long Godot takes is not known beforehand: the bar comes nearer its end the longer it goes on.
            Report("Graphics", 0.62f + 0.33f * (1 - Mathf.Exp(-(float)shown.Elapsed.TotalSeconds / 1.5f)));
            yield return true;
            var now = HitchLog.Compiled();
            quiet = now == last ? quiet + 1 : 0;
            last = now;
        }
        Note($"drawn, {HitchLog.Compiled() - compiled} pipelines", since);
        Report("Ready", 0.96f);
        while (!rehearsal.IsCompleted)
            yield return true;
        _report.Add($"rehearsed {rehearsal.Result:F1} s");
        foreach (var screen in _screens)
            screen.Visible = false;
        _groups = [];
        stage.QueueFree();
        hack?.QueueFree();
        space?.QueueFree();
        // The stage goes before the match is shown, and the garbage of all that loading with it: collected later, it
        // held up a frame of the match for a third of a second.
        yield return true;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        yield return true;
    }

    /// <summary>The story's props a film's warm-up drew, kept loaded so the film shows the same meshes.</summary>
    private static readonly HashSet<PackedScene> FilmProps = [];

    // The space stage's sky picture, kept loaded once a story has warmed it up.
    private static Texture2D? SpaceSky;

    /// <summary>
    /// What a hero wears now and, on a story's deck, after taking each weapon from its racks (the markers pickup_&lt;weapon&gt;),
    /// in the first free socket that takes it, as the world fits it (GameWorld.TakeUp): putting the Locator together with
    /// the workshop's Bolter took 110 ms, and its first drawing as long again, as the Bolter was taken.
    /// </summary>
    private IEnumerable<Machines.Sim.Survival.HeroLoadout> Worn(Machines.Sim.Survival.HeroLoadout now)
    {
        yield return now;
        if (_world.Story is null || _world.Deck is not { } deck)
            yield break;
        foreach (var rack in deck.Markers.Where(m => m.Tags.Contains("pickup") && m.Name.StartsWith("pickup_", StringComparison.Ordinal)))
        {
            var weapon = new Machines.Sim.Survival.WiredItem(-1, Machines.Sim.Survival.WiredKind.Weapon, rack.Name["pickup_".Length..].ToUpperInvariant());
            if (Machines.Sim.Survival.WiredCatalogue.KindOf(weapon.Catalogue) != Machines.Sim.Survival.WiredKind.Weapon)
                continue;
            var socket = Enum.GetValues<Mounting>().Select(Machines.Sim.Survival.WiredSlot.Weapon)
                .FirstOrDefault(s => now.At(s) is null && now.WhyNotFit(weapon, s) is null);
            if (socket is not null)
                yield return now.With(socket, weapon);
        }
    }

    /// <summary>
    /// Every machine and building of the research tree in each race's colours, as the match shows them, each building
    /// unfinished too, and the models of the scenarios' own; one of each damaged and burning, selected and pointed at.
    /// </summary>
    private IEnumerable<bool> Actors(GameWorld world, Node3D stage, NVector2 ground, List<(Actor, ActorView)> views)
    {
        // The actors are made first: what they look like says which models to load.
        var actors = new List<Actor>();
        var models = new List<(string Id, Race Look, Vector3 Size)>();
        var bare = new List<(string Id, Race Look)>();
        var looks = new HashSet<Race>();
        foreach (var race in _world.Races.Keys.Where(r => looks.Add(Game.Look(r))))
        {
            var look = Game.Look(race);
            var shown = new HashSet<string>();
            // A story puts up only its cast: the research tree and every chassis of Wired (some 180 models) are not in it.
            if (_world.Story is not null)
            {
                foreach (var (type, sub, level, combo) in StoryCast)
                    if (Add(world, race, ResearchKind.Machine, new ResearchKey(type, sub, level, combo), ground) is { } actor)
                    {
                        actors.Add(actor);
                        if (ActorView.ModelOf(actor) is var (id, size) && shown.Add(id))
                            models.Add((id, look, size));
                    }
                foreach (var pod in _world.Data.ResearchTree.Items.Where(i => i.Kind == ResearchKind.Construction && i.Key.Type == "POD").Take(1))
                    if (Add(world, race, pod.Kind, pod.Key, ground) is { } built)
                        actors.Add(built);
                // What the world cannot make by itself (Wired's own Thresher) by its model.
                foreach (var (type, sub, level, combo) in StoryCast)
                {
                    var id = string.Join('_', new[] { type, level.ToString(System.Globalization.CultureInfo.InvariantCulture), sub, combo }.Where(p => p is { Length: > 0 }));
                    if (shown.Add(id))
                    {
                        models.Add((id, look, Vector3.One * 3));
                        bare.Add((id, look));
                    }
                }
                continue;
            }
            foreach (var item in _world.Data.ResearchTree.Items)
            {
                if (Add(world, race, item.Kind, item.Key, ground) is { } actor)
                {
                    actors.Add(actor);
                    if (ActorView.ModelOf(actor) is var (id, size) && shown.Add(id))
                        models.Add((id, look, size));
                }
                // Each building under construction too: its green hologram is drawn otherwise for each kind of model.
                if (item.Kind == ResearchKind.Construction && Add(world, race, item.Kind, item.Key, ground, complete: false) is { } site)
                    actors.Add(site);
            }
            // Models no research brings: the scenarios' own (the ore hologram, the bosses), and in Wired, for a race with a
            // hero, every chassis' model heroes are put together from (Wired's own machines, the constructors, spies and
            // locators with weapon mounts), so that a chassis changed in the hangar loads nothing.
            var heroes = _world.Wired?.Heroes.Values.Any(state => state.Race == race) == true ? HeroDonors.Chassis.Values : [];
            foreach (var id in _assets.ModelIds(look).Concat(heroes).Where(shown.Add))
            {
                models.Add((id, look, Vector3.One * 3));
                bare.Add((id, look));
            }
        }

        // Loaded on every core, while the loading screen goes on drawing. What runs on these threads makes no typed Godot
        // array or dictionary of objects and no C# node: both write Godot's table of type names, which has no lock.
        var shadows = PlanetLook.PaintedShadows;
        var effects = _assets.EffectNames().ToList();
        var loaded = 0;
        var loading = Task.Run(() =>
        {
            Parallel.ForEach(effects, Loaders, name => Load(() =>
            {
                if (name.StartsWith("demolish/", StringComparison.Ordinal))
                    _assets.EffectRig(name);
                else
                    _assets.EffectMesh(name);
            }));
            Parallel.ForEach(models, Loaders, model => Load(() =>
            {
                if (_assets.RigFor(model.Id, model.Look) is null)
                    _assets.MeshFor(model.Id, model.Look, model.Size);
                if (shadows)
                    _assets.PaintedShadow(model.Id, model.Look);
                Interlocked.Increment(ref loaded);
            }));
        });
        var since = Stopwatch.GetTimestamp();
        while (!loading.IsCompleted)
        {
            Report("Models", 0.08f + 0.37f * Volatile.Read(ref loaded) / Math.Max(1, models.Count));
            yield return true;
        }
        foreach (var name in effects)
            _assets.EffectLights(name);
        Note($"{models.Count} models loaded", since);
        since = Stopwatch.GetTimestamp();

        // Then put up as the match shows them.
        foreach (var actor in actors)
        {
            Report("Machines and buildings", 0.45f + 0.11f * views.Count / actors.Count);
            views.Add((actor, Show(NextGroup(), actor)));
            yield return false;
        }
        foreach (var (id, look) in bare)
        {
            Node3D model = _assets.RigFor(id, look) is { } rig
                ? RigView.Create(rig)
                : new MeshInstance3D { Mesh = _assets.MeshFor(id, look, Vector3.One * 3) };
            NextGroup().AddChild(model);
            model.GlobalPosition = ActorView.World(ground);
            yield return false;
        }
        // Each hero as it stands, put together from those (kept by what it wears, so its view takes the same).
        foreach (var (_, state) in _world.Wired?.Heroes ?? [])
            foreach (var loadout in Worn(state.Loadout))
            {
                if (HeroRig.Compose(_assets, Game.Look(state.Race), loadout) is { } rig)
                {
                    var hero = RigView.Create(rig);
                    NextGroup().AddChild(hero);
                    hero.GlobalPosition = ActorView.World(ground);
                }
                yield return false;
            }
        // A deck's props, one of each (the deck itself stands mostly out of sight), the hull of the mothership made small.
        if (_world.Deck is not null)
            foreach (var name in Props.Names())
            {
                if (Props.Get(name) is { } prop)
                {
                    var shrink = 4 / Mathf.Max(4, Mathf.Max(prop.Size.X, Mathf.Max(prop.Size.Y, prop.Size.Z)));
                    NextGroup().AddChild(new MeshInstance3D
                    {
                        Mesh = prop.Mesh,
                        Transform = new Transform3D(Basis.FromScale(Vector3.One * shrink), ActorView.World(ground)) * prop.Offset,
                    });
                }
                yield return false;
            }
        // And what the deck shows of the story: the alarm's beacons lit, a downed Grunt's revive ring.
        if (_world.Deck is not null)
            foreach (var piece in Game.DeckShowPieces())
            {
                NextGroup().AddChild(piece);
                piece.GlobalPosition = ActorView.World(ground) + Vector3.Up;
            }
        // A story's film shows the story's props as they were made (FilmCamera.SceneModel), not as the deck shows them:
        // the mothership the film opens on held its first frame up for 200 ms. Each is kept loaded for the film.
        if (MatchRequest.Current.Film is not null)
            foreach (var name in Props.Names())
            {
                if (GD.Load<PackedScene>($"{Props.Folder}/{name}.glb") is { } scene && scene.Instantiate() is Node3D prop)
                {
                    FilmProps.Add(scene);
                    NextGroup().AddChild(prop);
                    prop.GlobalPosition = ActorView.World(ground);
                }
                yield return false;
            }
        if (_world.Deck is not null && ResourceLoader.Exists("res://assets/story/space/sky.jpg"))
            SpaceSky ??= GD.Load<Texture2D>("res://assets/story/space/sky.jpg");
        Note($"{actors.Count + bare.Count} put up", since);
        if (views.Count == 0)
            yield break;

        // Damaged and burning, and the boxes of selected and pointed at.
        var race0 = views[0].Item1.Race;
        foreach (var kind in new[] { ResearchKind.Machine, ResearchKind.Construction })
            if (_world.Data.ResearchTree.Items.FirstOrDefault(i => i.Kind == kind) is { } item && Add(world, race0, kind, item.Key, ground) is { } hurt)
            {
                hurt.HitPoints = 1;
                Show(NextGroup(), hurt).Selected = true;
            }
        views[0].Item2.Highlighted = true;
        yield return false;
    }

    /// <summary>
    /// The match's first seconds played on a copy of it, every race by the computer, on a worker thread; returns the
    /// seconds it took. The simulation's code is compiled the first time it runs, which held the match's first two
    /// ticks up for half a second. The copy shares nothing with the match but the data it was read from.
    /// </summary>
    private static Task<double> Rehearse(GameData data, MatchRequest request) => Task.Run(() =>
    {
        var since = Stopwatch.GetTimestamp();
        try
        {
            // A story's Locator is always a person's (StorySeats); its Grunts and the ship are the computer's anyway.
            var players = request.Starts.Players.ToDictionary(p => p.Key,
                p => request.Options.Story is { } story && p.Key == story.Race ? Controller.Player : Controller.Ai);
            var copy = MatchSetup.Create(data, request.Scenario, request.Planet, players, 1, request.Options);
            for (var tick = 0; tick < RehearsedTicks; tick++)
                copy.Step();
            // Each turn goes through JSON (LockstepSession.Pump keeps a copy as the others see it); the first time
            // it looked at every kind of command, a sixth of a second. Online every tick comes as the relay's bundle, with
            // its turns and its comings and goings (P2 design §4.4).
            var first = players.Keys.First();
            NetJson.Decode(NetJson.Encode(new Turn(0, PlayerId.Of(first), [])));
            NetJson.Decode(NetJson.Encode(new Bundle(0, [new Turn(0, PlayerId.Of(first), [new DriveCommand(first, 0, Driving: true, Forward: 1)], Asked: 0)],
                [new PlayerLagging(PlayerId.Of(first), 0, Lagging: true)], Delay: 4)));
            foreach (var type in typeof(Command).Assembly.GetTypes().Where(t => t.IsSubclassOf(typeof(Command)) && !t.IsAbstract))
                NetJson.Options.GetTypeInfo(type);
        }
        catch (Exception exception)
        {
            GD.PushWarning($"warm-up rehearsal: {exception.Message}");
        }
        return Stopwatch.GetElapsedTime(since).TotalSeconds;
    });

    /// <summary>
    /// The machines a story chapter shows besides its heroes (docs/STORY.md, the prologue's cast): the Thresher (Wired's own
    /// Reaper 4), the Reapers with plasma rifles and the Commander of the alarm, the armed Grunt of the workshop, and the
    /// switched-off machines in the clamp rows (Grunt 2 and 3, Reaper 2, a Builder and a technician).
    /// </summary>
    private static readonly (string Type, string? SubType, int Level, string? Combo)[] StoryCast =
    [
        ("GEO_LOCATOR", null, 1, null),
        // The armoury's: the Locator with the Bolter it picks up, the electric Grunts of batches 2-4.
        ("GEO_LOCATOR", null, 1, "T_BOLTER"),
        ("AGGRESSOR", "GRUNT", 1, "R_ELECTRIC_CHARGE"),
        ("AGGRESSOR", "GRUNT", 2, "L_ELECTRIC_CHARGE"),
        ("AGGRESSOR", "GRUNT", 1, "R_BOLTER"),
        ("AGGRESSOR", "GRUNT", 2, "L_BOLTER"),
        ("AGGRESSOR", "GRUNT", 3, "LR_HEAVY_BOLTER1_X2"),
        ("AGGRESSOR", "ASSASSIN", 2, "L_PLASMA_RIFLE"),
        ("AGGRESSOR", "ASSASSIN", 3, "LR_PLASMA_RIFLE_X2"),
        ("AGGRESSOR", "ASSASSIN", 4, "LR_PLASMA_RIFLE_X2"),
        ("ADMINISTRATOR", "COMMANDER", 5, "LR_PULSE_CANNON_X2"),
        ("CONSTRUCTOR", "BUILDER", 3, null),
        ("TECHNICIAN", "LAB_TECH", 2, null),
    ];

    /// <summary>Loads on a worker thread; what fails is told and left to load when the match needs it.</summary>
    private static void Load(Action load)
    {
        try
        {
            load();
        }
        catch (Exception exception)
        {
            GD.PushWarning($"warm-up: {exception.Message}");
        }
    }

    private static Actor? Add(GameWorld world, Race race, ResearchKind kind, ResearchKey key, NVector2 at, bool complete = true)
    {
        try
        {
            return kind == ResearchKind.Machine
                ? world.AddMachine(race, at, key.Type, key.SubType, key.HardwareLevel, key.WeaponCombo)
                : world.AddConstruction(race, at, key.Type, key.SubType, key.HardwareLevel, 0, complete, key.WeaponCombo);
        }
        catch (Exception exception) when (exception is KeyNotFoundException or InvalidOperationException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The group the next of the crowd goes in, round the groups in turn.</summary>
    private Node3D NextGroup() => _groups[_placed++ % _groups.Length];

    private ActorView Show(Node3D stage, Actor actor)
    {
        var view = ActorView.Create(actor, _assets, _audio);
        stage.AddChild(view);
        view.Sync(actor);
        return view;
    }

    /// <summary>Every weapon's shot, round, splat and blast, explosions, wrecks and mines, and what befalls machines and buildings.</summary>
    private IEnumerable<bool> Shots(GameWorld world, Node3D stage, Vector3 at, List<(Actor Actor, ActorView View)> views)
    {
        var effects = new Effects { Name = "Effects" };
        stage.AddChild(effects);
        var support = SupportEffects.Create(_assets);
        stage.AddChild(support);
        var rounds = ProjectileViews.Create(world, _assets, _ => null, effects);
        stage.AddChild(rounds);
        var race = views.Count > 0 ? views[0].Actor.Race : Race.Red;
        var (from, to) = (at + new Vector3(-8, 2, 0), at + new Vector3(8, 2, 0));

        var weapons = _world.Data.Parm.Weapons;
        foreach (var weapon in weapons)
        {
            var name = weapon.TypeName;
            effects.Shot(from, to, name, weapon.ProjectileSpeed, weapon);
            effects.Shot(from, from, name, weapon.ProjectileSpeed, weapon);
            effects.EnergySplat(to, name, Vector3.Right);
            if (!GraphicsSettings.Effects)
            {
                support.BolterFlash(from, Vector3.Right, name, Effects.RoundLit(weapon));
                if (name.StartsWith("FLAME_THROWER", StringComparison.Ordinal))
                    support.FlameBalls(from, to, weapon);
                // Each age a step of its own: a blow-up is a lot to put up in one frame.
                foreach (var age in Ages)
                {
                    Aged([effects], age, () => Blowup.Hit(name, to, _assets, effects));
                    yield return false;
                }
            }
            yield return false;
        }
        rounds.ShowEach(weapons, race, at + Vector3.Up * 2);

        // The blasts: the ground torn open, or a dome with its flash.
        foreach (var weapon in weapons.Where(w => Blast.IsArea(w.TypeName)))
        {
            var blast = new Blast(weapon.TypeName, new NVector2(at.X, at.Z), race, 0, Blast.WavesFor(weapon));
            foreach (var age in Ages)
                if (GroundBlast.Create(_assets, blast, Blast.Extras(weapon)) is { } torn)
                {
                    effects.AddChild(torn);
                    torn.Skip(age);
                    yield return false;
                }
            var (dome, colour) = Game.BlastDome(weapon.TypeName);
            effects.AddChild(dome);
            dome.GlobalPosition = at;
            dome.Scale = new Vector3(10, 6, 10);
            effects.Blast(at, 10, new Color(colour.R, colour.G, colour.B));
            effects.Scorch(at, 50);
            yield return false;
        }

        effects.Impact(at);
        effects.MissileHit(at + Vector3.Up, Vector3.Right, 3);
        foreach (var size in new[] { 1f, 3, 8, 20 })
            effects.Explosion(at, size);
        effects.Destroyed(at, null);
        effects.TreadMark(at, 0, 1, 4);
        effects.ChargerFires(from, to, 2);
        effects.PulseRing(from, Vector3.Right);
        Blowup.PulseDisc(from, Vector3.Right, _assets, effects);
        foreach (var particles in new[] { Effects.DustTrail(), Effects.Plume(4, 3, 6, 2, new Color(0.1f, 0.1f, 0.1f, 0.7f)), Effects.Puffs(3, 1, 2, 1), Effects.VapourTrail(), Effects.DamageFire(2, 3) })
        {
            particles.Emitting = true;
            stage.AddChild(particles);
            particles.GlobalPosition = at + Vector3.Up;
        }
        // A wreck, a land mine and an artefact's damage, as Game lays them.
        foreach (var name in new[] { "debris", "lmine" })
            if (_assets.EffectMesh(name) is { } mesh)
            {
                var lying = new MeshInstance3D { Mesh = mesh };
                stage.AddChild(lying);
                lying.GlobalPosition = at;
            }
        var artefact = new MeshInstance3D { Mesh = new BoxMesh { Size = new Vector3(4, 4, 4) } };
        stage.AddChild(artefact);
        artefact.GlobalPosition = at;
        ArtefactDamage.Create(artefact, new Aabb(new Vector3(-2, -2, -2), new Vector3(4, 4, 4)), _assets.EffectMesh("groundsplat")).Show(100);
        yield return false;

        // What befalls a machine and a building: each in a view of its own, as these take the view apart.
        foreach (var (actor, view) in views.Where(v => v.Actor is Machine).Take(1).Concat(views.Where(v => v.Actor is Construction).Take(1)))
        {
            support.Burn(view);
            support.Scavenge(view);
            support.Loading(view);
            support.Lightning(from, view.Centre, view);
            support.ShowHealing([(-1, from, view)]);
            support.LightUp(view, view.Shape());
            support.BoltSparks(view, from);
            effects.FaceplateSmoke(view, 0);
            view.Construct(at + Vector3.Right * 4, _assets.EffectMesh("torch"), _assets.EffectMesh("sparks"), _assets.EffectMesh("groundlight"));
            view.Scan(_assets.EffectMesh("locator"), 30);
            view.TeleportIn();
            view.Punch();
            view.Shake();
            var other = views.FirstOrDefault(v => v.Actor.Race != actor.Race).Actor;
            // Destroyed, the view goes at once, as Game lets it go.
            foreach (var (fate, goes) in new (Action<ActorView>, bool)[]
                     {
                         (dying => { effects.Destroyed(dying.Centre, dying); dying.BlowUp(effects, _assets); }, true),
                         (dying => dying.Vaporise(), true),
                         (dying => dying.Swallow(), true),
                         (dying => dying.TeleportOut(), false),
                         (dying => dying.Defect(other?.Race ?? actor.Race, actor, _assets), false),
                     })
                foreach (var age in Ages)
                {
                    var dying = Show(stage, actor);
                    Aged([stage, effects], age, () => fate(dying));
                    if (goes)
                        dying.QueueFree();
                    yield return false;
                }
            yield return false;
        }
    }

    /// <summary>Does <paramref name="what"/> and moves the blow-ups it put under <paramref name="parents"/> <paramref name="age"/> seconds on.</summary>
    private static void Aged(Node[] parents, float age, Action what)
    {
        var before = parents.SelectMany(p => p.GetChildren().OfType<Blowup>()).ToHashSet();
        what();
        foreach (var blowup in parents.SelectMany(p => p.GetChildren().OfType<Blowup>()).Where(b => !before.Contains(b)))
            blowup.Skip(age);
    }
}
