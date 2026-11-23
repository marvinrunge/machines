using Godot;
using Machines.Import.Model;
using Machines.Sim.Navigation;
using Machines.Sim.Story;
using Machines.Sim.World;
using NVector2 = System.Numerics.Vector2;

namespace Machines.Game;

/// <summary>
/// For automated checks of the prologue in the real game, through the real input: MACHINES_STORY_DRIVE=&lt;folder&gt; with
/// MACHINES_STORY=prolog[,&lt;checkpoint&gt;] (and MACHINES_MAP=prolog) plays the chapter on rails. It drives the Locator in
/// first person with held keys and the mouse along its own way over the deck's grid (the Locator's room and ducts), plugs
/// into each hack with the hack's key and plays the board's own solution with clicks, as MACHINES_HACK_SCRIPT does, tells
/// the Grunts to follow with their key, sits through the cutscenes, and after a fall goes on from the checkpoint by the death
/// card's way. Which leg comes next it reads from the world (the hacks done, the director's beats fired), so it can start
/// from any checkpoint. It prints "story drive: section N tick T checksum X" as each section begins, and takes one picture
/// a section into the folder, then one at the end. Once a run it switches the Locator's lamp with the Lamp key, off and on
/// again, and says whether the sim's lamp followed. After the pod is away it sits through the chapter's film outside, takes
/// a picture of it and of the chapter's card, goes on with Space and says whether the story page came.
/// MACHINES_STORY_DRIVE_CRASH=1 lets the countdown run out once first, for the Crashed card. At checkpoint 0 it waits through
/// the Locator's boot in its clamp (about 105 s) as a first player must; MACHINES_STORY_SKIP_BOOT=1 skips it at once.
/// <para>
/// Where its way fails for a long time (90 s on one leg, 150 s waiting for the Grunts) it sets the hero, or the Grunts, down
/// at the leg's end and says so: the only time it touches the world, so the run goes on to the end; the checksums of such a
/// run are its own. At the end it prints "fallbacks: N", the times it did so; a sound run has none, and where the way
/// stalls it says where, which way it wanted and who stood near.
/// </para>
/// <para>
/// Online (a story room of MACHINES_MP) the host's instance drives the Locator so; an instance at a Grunt's seat drives its
/// Grunt after the Locator and, once the pod's hatch is open, aboard. Online it never sets anyone down (that would be its own
/// world's alone): a way that fails long only says so ("stuck online"). A chapter lost online goes back to the room by the
/// card's way, and the run goes on once the host has started the room again.
/// </para>
/// </summary>
public partial class StoryDriver(string folder) : WiredDriver(folder, new Plan("Driver", int.MaxValue, null, 0, new HashSet<string>(), "menu", "squad"))
{
    private static bool _attending;

    /// <summary>Comes along once a story match shows, if the switch says so; it outlives the reloads from a checkpoint.</summary>
    public static void Attend(Game game)
    {
        if (_attending || System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE") is not { Length: > 0 } drive)
            return;
        _attending = true;
        game.GetTree().Root.CallDeferred(Node.MethodName.AddChild, new StoryDriver(drive.Split(',')[0]));
    }

    private static void Tell(string text) => GD.Print($"story drive: {text}");

    // The leg under way and since when; the way along the grid; the last key presses.
    private string _leg = "";
    private double _legSince, _wayAt, _lastUse, _lastFollow, _clickAt, _reverseUntil, _progressAt;
    private NVector2 _wayTo, _progressFrom;
    private List<NVector2> _route = [];
    // The times its way failed and it set someone down (the goal is none), and the stalls logged on this leg.
    private int _section, _falls, _clicks, _fallbacks, _stalls, _stuckOnline;
    private double? _pictureAt;
    private HackHost? _board;
    private int _boardOpened;

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        System.IO.Directory.CreateDirectory(Folder);
        Tell($"plays the prologue on rails, pictures to {Folder}");
        try
        {
            await Drive();
        }
        catch (Exception exception)
        {
            Tell($"failed: {exception}");
        }
        Tell($"fallbacks: {_fallbacks}{(_stuckOnline > 0 ? $", stuck online {_stuckOnline}" : "")}");
        Tell("over");
        GetTree().Quit();
    }

    private async Task Drive()
    {
        while (true)
        {
            var game = await Until(() => Scene is Game { World.Story: not null } found && found.IsNodeReady() ? found : null, 180)
                ?? throw new InvalidOperationException("no story match came");
            _section = 0;
            _leg = "";
            while (IsInstanceValid(game) && game.World.Story is { } story)
            {
                await Frames(1);
                if (!IsInstanceValid(game))
                    break;
                Sections(game, story);
                Beats(game, story);
                HitchLog.Phase = $"section {_section}{(game.InCutscene ? " cutscene" : game.HackShown ? " hack" : "")}";
                if (_pictureAt is { } when && Time.GetTicksMsec() / 1000.0 >= when)
                {
                    _pictureAt = null;
                    await Picture($"section{story.Section}");
                }
                if (ShotDue(game, story) is { } shot)
                    await BeatPicture(game, story, shot);
                // The armoury's own pictures (StoryDriver.Armoury).
                if (ArmouryShot() is { } armouryShot)
                    await BeatPicture(game, story, armouryShot);
                if (story.Over)
                {
                    LetGo();
                    if (story.Ending == StoryEnding.Escaped)
                    {
                        Tell($"escaped: tick {game.World.Tick} checksum {game.World.Checksum():x16} falls {_falls} lamp {_lampChecked ?? "unchecked"} sprinted {_sprinted:0.0} s overheats {_overheats} limiter {(_limiterTried ? "tried" : "untried")}");
                        await Wait(2);
                        await Picture("end");
                        await Film(game);
                        return;
                    }
                    if (++_falls > 4)
                        throw new InvalidOperationException($"lost the chapter {_falls} times ({story.Ending}); giving up");
                    Tell($"lost ({story.Ending}) at tick {game.World.Tick} in section {story.Section}: on from checkpoint {story.Checkpoint}");
                    await Wait(4);
                    await Picture($"lost-{story.Ending}");
                    await Tap(Key.Space);
                    await Wait(3);
                    if (IsInstanceValid(game) && Scene == game)
                        game.FromCheckpoint();
                    await Until(() => Scene != game, 30);
                    break;
                }
                if (game.InCutscene || game.World.HeroOf(Game.Me) is not { } hero)
                {
                    LetGo();
                    continue;
                }
                if (game.Driven is null)
                {
                    await GetIn(game);
                    continue;
                }
                if (Booting(game, story))
                    continue;
                // The Notabschaltung: the Locator shut down waits for its Grunt's cable (MACHINES_STORY_DRIVE_SHUTDOWN=1 forces one).
                if (await ShutDown(game, story, hero))
                    continue;
                // Stage 7's sections drive by their own legs once their packages build them (StoryDriver.Stealth, .Armoury, .Escape).
                if (DriveStealth(game) || DriveArmoury(game) || DriveEscape(game))
                    continue;
                if (_lampChecked is null)
                {
                    await Lamp(game, hero);
                    continue;
                }
                if (await LimiterTried(game, story, hero))
                    continue;
                await Leg(game, story, hero);
                await DriveWatched(game, hero);
            }
        }
    }

    private static readonly bool ForceShutdown = System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE_SHUTDOWN") == "1";
    private bool _shutdownForced, _shutdownTold;

    /// <summary>
    /// The shared leg "wait while shut down" (plan stufe7 C23): while the Locator lies shut down the driver lets go and waits
    /// for a Grunt's cable, telling the shutdown and the restart ("story drive: &lt;section&gt; shutdown|restart tick T seconds S")
    /// with a picture of each. With MACHINES_STORY_DRIVE_SHUTDOWN=1 (and the reserve on, MACHINES_STORY_SHUTDOWN_TEST=1 in
    /// section 7) it shoots its own Locator down once, a test of the Notabschaltung before package E lands.
    /// </summary>
    private async Task<bool> ShutDown(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        if (world.LocatorShutDown)
        {
            LetGo();
            if (!_shutdownTold)
            {
                _shutdownTold = true;
                Tell(FormattableString.Invariant($"story drive: {story.Section} shutdown tick {world.Tick} seconds {world.Time:0.0}"));
                await Wait(1.5f);
                await Picture("shutdown");
            }
            return true;
        }
        if (_shutdownTold)
        {
            _shutdownTold = false;
            Tell(FormattableString.Invariant($"story drive: {story.Section} restart tick {world.Tick} seconds {world.Time:0.0} restarts {story.Reserve.Restarts}"));
            await Picture("restart");
        }
        if (ForceShutdown && !_shutdownForced && Game.Me == story.Locator && story.Section == 7 && story.Flags.Contains(StoryMarks.Reserve))
        {
            _shutdownForced = true;
            world.Damage(hero, 100000, story.Options.Ship);
            Tell($"shot the Locator down at tick {world.Tick}");
            return true;
        }
        return false;
    }

    // Whether the skip was sent, and whether the boot's end was told.
    private bool _bootSkipSent, _bootTold;

    /// <summary>
    /// The Locator's boot in its clamp: waited through, as a first player must, or with MACHINES_STORY_SKIP_BOOT=1 skipped at
    /// once by the Locator's driver (the Use command Esc held sends). Its end is told with the tick and whether it was skipped.
    /// </summary>
    private bool Booting(Game game, StoryRun story)
    {
        if (!story.Flags.Contains(StoryMarks.Boot))
        {
            if (!_bootTold && story.Director.Fired.TryGetValue("boot_done", out var done) && story.Options.Checkpoint == 0)
            {
                _bootTold = true;
                Tell(FormattableString.Invariant($"boot over at {done:0.0} s, tick {game.World.Tick}{(story.Flags.Contains(StoryMarks.BootSkip) ? ", skipped" : "")}"));
            }
            return false;
        }
        LetGo();
        if (!_bootSkipSent && Game.Me == story.Locator && System.Environment.GetEnvironmentVariable("MACHINES_STORY_SKIP_BOOT") == "1")
        {
            _bootSkipSent = true;
            game.SkipStoryBoot();
            Tell($"boot skipped at tick {game.World.Tick}");
        }
        return true;
    }

    // MACHINES_STORY_DRIVE_SHOTS=<beat>:<seconds>[@<camera>][,...]: more pictures, each that many game seconds after a beat of
    // the director's fired (break_in:3 is the Reapers storming the core chamber), named after the beat and the seconds. A
    // camera takes the picture from elsewhere for that frame alone: "@x/y/height/towardX/towardY/towardHeight" (deck metres) or
    // "@<role>/<distance>/<height>[/<side>]", that far before the first standing machine the director cast in that role (thresher,
    // reaper, commander, hostile, row, scare) or a seat of the trio ("seat0" the Locator, "seat1" Ike, "seat2" Bob), looking at its eyes.
    // Such a picture leaves out the HUD and the first-person screen; a camera ending in "!" brings a soft light of its own along.
    private List<(string Beat, float Seconds, string? Camera)>? _beatShots;
    private string? _shotCamera;

    private string? ShotDue(Game game, StoryRun story)
    {
        _beatShots ??= (System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE_SHOTS") ?? "")
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
            .Select(s => s.Split('@', 2))
            .Select(s => (Parts: s[0].Split(':'), Camera: s.Length > 1 ? s[1] : null))
            .Where(p => p.Parts.Length == 2 && float.TryParse(p.Parts[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            .Select(p => (p.Parts[0], float.Parse(p.Parts[1], System.Globalization.CultureInfo.InvariantCulture), p.Camera))
            .ToList();
        foreach (var shot in _beatShots)
        {
            if (story.Director.Fired.TryGetValue(shot.Beat, out var at) && game.World.Time >= at + shot.Seconds)
            {
                _beatShots.Remove(shot);
                _shotCamera = shot.Camera;
                return FormattableString.Invariant($"{shot.Beat}+{shot.Seconds}{(shot.Camera is null ? "" : "-" + shot.Camera.Split('/')[0])}");
            }
        }
        return null;
    }

    /// <summary>A beat's picture, from the camera it asked for (<see cref="ShotDue"/>) if any, the view's own camera again afterwards.</summary>
    private async Task BeatPicture(Game game, StoryRun story, string name)
    {
        var spec = _shotCamera;
        _shotCamera = null;
        var fill = spec?.EndsWith('!') == true;
        if (spec is null || ShotCamera(game, story, spec.TrimEnd('!')) is not { } placed)
        {
            await Picture(name);
            return;
        }
        var (from, toward) = placed;
        var before = GetViewport().GetCamera3D();
        var camera = new Camera3D { Fov = 55, Far = 400 };
        game.AddChild(camera);
        camera.GlobalPosition = from;
        camera.LookAt(toward, Vector3.Up);
        camera.MakeCurrent();
        if (fill)
            camera.AddChild(new OmniLight3D { LightEnergy = 1.2f, OmniRange = 30, LightColor = new Color(0.8f, 0.85f, 1f) });
        var shown = GetTree().Root.FindChildren("*", nameof(CanvasLayer), true, false).OfType<CanvasLayer>().Where(layer => layer.Visible).ToList();
        foreach (var layer in shown)
            layer.Visible = false;
        await Frames(2);
        await Picture(name);
        foreach (var layer in shown.Where(IsInstanceValid))
            layer.Visible = true;
        camera.ClearCurrent(false);
        camera.QueueFree();
        if (IsInstanceValid(before))
            before.MakeCurrent();
    }

    private static (Vector3 From, Vector3 Toward)? ShotCamera(Game game, StoryRun story, string spec)
    {
        var parts = spec.Split('/');
        static float Number(string text) => float.Parse(text, System.Globalization.CultureInfo.InvariantCulture);
        if (parts.Length == 6 && float.TryParse(parts[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out _))
            return (new Vector3(Number(parts[0]), Number(parts[2]), Number(parts[1])), new Vector3(Number(parts[3]), Number(parts[5]), Number(parts[4])));
        if (parts.Length < 3)
            return null;
        Machine? machine = parts[0].StartsWith("seat", StringComparison.Ordinal) && int.TryParse(parts[0][4..], out var seat) && seat < story.Seats.Count
            ? game.World.HeroOf(story.Seats[seat])
            : story.Director.Of(game.World, parts[0]).FirstOrDefault();
        if (machine is null)
            return null;
        var view = game.ViewOf(machine.Id);
        var eye = view?.Eye ?? ActorView.World(machine.Position) + Vector3.Up * 2;
        var ahead = new Vector3(MathF.Cos(machine.Facing), 0, MathF.Sin(machine.Facing));
        // Where its eyes look (its lamp's way, the upper body turned), when it has a lamp.
        if (view?.GetChildren().OfType<SpotLight3D>().FirstOrDefault() is { } lamp && -lamp.GlobalBasis.Z with { Y = 0 } is { } along && along.LengthSquared() > 0.01f)
            ahead = along.Normalized();
        var side = new Vector3(-ahead.Z, 0, ahead.X) * (parts.Length > 3 ? Number(parts[3]) : 0);
        var from = ActorView.World(machine.Position) + ahead * Number(parts[1]) + side;
        return (new Vector3(from.X, ActorView.World(machine.Position).Y + Number(parts[2]), from.Z), eye);
    }

    // The core fight's beats, told with the game seconds they fired at (the fight's timeline).
    private static readonly string[] CoreBeats = ["core_open", "crew_in", "squad", "squad_go", "at_j", "alarm", "hammer_j", "burst_j", "go_round",
        "held", "at_i", "hammer_i", "shoot_i", "burst_i", "break_in", "core_hit_1", "core_hit_2", "core_hit_3", "core_blows",
        "power_fails", "control_lost", "fled", "limiter", "together", "repaired", "notgang", "pod_bay", "launch"];
    private readonly HashSet<string> _toldBeats = [];

    private void Beats(Game game, StoryRun story)
    {
        foreach (var beat in CoreBeats)
        {
            if (story.Director.Fired.TryGetValue(beat, out var at) && _toldBeats.Add(beat))
                Tell(FormattableString.Invariant($"beat {beat} at {at:0.0} s (hack {story.Hacks.Session("hack_core")?.Progress ?? 0:0.00}{(story.Director.Has("break_in") ? "" : $", crates J {game.World.BarricadeCrates("J").Count} I {game.World.BarricadeCrates("I").Count}")})"));
        }
    }

    /// <summary>A section begun: its line and, a moment later, its picture.</summary>
    private void Sections(Game game, StoryRun story)
    {
        if (story.Section == _section)
            return;
        _section = story.Section;
        Tell($"section {_section} tick {game.World.Tick} checksum {game.World.Checksum():x16} checkpoint {story.Checkpoint}");
        _pictureAt = Time.GetTicksMsec() / 1000.0 + 1.5;
    }

    // ---- the legs ---------------------------------------------------------------------------------

    /// <summary>What to do next, read from the world: a hack, a place to go, or waiting for the Grunts or the director.</summary>
    private async Task Leg(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        var desk = story.Hacks;
        var director = story.Director;
        if (Game.Me != story.Locator)
        {
            Accompany(game, story, hero);
            return;
        }
        bool Done(string target) => desk.Done.Contains(target);
        NVector2 Zone(string name) => world.Deck?.Zones.FirstOrDefault(z => z.Name == name) is { } zone ? (zone.Min + zone.Max) / 2 : hero.Position;
        NVector2 Marker(string name) => world.Deck!.Marker(name).Position;

        if (_reviving && Fallen(game, story) is null)
        {
            _reviving = false;
            Hold(KeyMap.Act.Use, false);
        }
        // A fallen Grunt is put together, but in the workshop only once its armed Grunts are down (the Bolter first, as a player would).
        var workshopFight = director.Has("together") && !director.Has("cleared");
        if (director.Has("control_lost") && !director.Has("launch") && !workshopFight && Fallen(game, story) is { } fallen)
            Revive(game, hero, fallen);
        else if (!Done("clamp_l7"))
            await Hack(game, story, hero, "clamp_l7");
        else if (!Done("terminal_ike"))
            await Hack(game, story, hero, "terminal_ike");
        else if (!Done("terminal_bob"))
            await Hack(game, story, hero, "terminal_bob");
        else if (!Done("hack_s"))
            await Hack(game, story, hero, "hack_s");
        else if (!Done("hack_h"))
            await Hack(game, story, hero, "hack_h");
        else if (!director.Has("antechamber"))
            Go(game, hero, "antechamber", Zone("R8"), 2);
        else if (!director.Has("break_in"))
        {
            // Nobody barricades: the Grunts are told to wait where they are as the core's hack begins.
            if (NoBarricade && director.Has("core_open") && !_toldWait)
            {
                _toldWait = true;
                await Tap(KeyMap.Of(KeyMap.Act.GruntWait));
            }
            await Hack(game, story, hero, "hack_core");
        }
        else if (!director.Has("core_blows"))
        {
            // In cover it looks back up at the core the Reapers are shooting to pieces, and watches it blow for a moment.
            if (Go(game, hero, "cover", Cover, 1.5f))
                Look(game, Heading(hero.Position, new NVector2(192, 264)), 0.18f);
        }
        else if (!director.Has("fled") && game.World.Time < director.Fired["core_blows"] + 2)
            Look(game, Heading(hero.Position, new NVector2(192, 264)), 0.18f);
        else if (!director.Has("fled"))
            Go(game, hero, "the vent", Zone("V2"), 1.5f);
        else if (desk.Locked.Contains("hack_d5"))
            Go(game, hero, "crossing", Zone("R12"), 3);
        else if (!Done("hack_d5"))
            await Hack(game, story, hero, "hack_d5");
        else if (!director.Has("together"))
            await Gather(game, story, hero, "together", "R12");
        else if (!director.Has("weapon"))
            await TakeUp(game, hero, "pickup_bolter", Marker("pickup_bolter"));
        else if (!director.Has("cleared"))
            Fight(game, story, hero);
        else if (!director.Has("repaired"))
        {
            Fire(game, hero, 0, false);
            Go(game, hero, "repair", Zone("repair"), 1.5f);
        }
        else if (!director.Has("notgang"))
            Go(game, hero, "notgang", new NVector2(520, 232), 2);
        else if (CrashFirst && _falls == 0)
            Wait(game, hero, "the crash");
        else if (!Done("pod_3_hatch"))
            await Hack(game, story, hero, "pod_3_hatch");
        else if (!story.Flags.Contains(StoryMarks.Done("pod_3_hatch")))
            await TakeUp(game, hero, "pod_3_hatch", story.Hacks.Find("pod_3_hatch", world.Deck)!.Value.Position, GameWorld.UseReach - 1);
        else if (!director.Has("launch"))
            await Gather(game, story, hero, "launch", "pod_zone");
        else
            Wait(game, hero, "the pod");
    }

    private bool _reviving;

    // ---- the sprint -------------------------------------------------------------------------------

    // Shift tried once while the limiter held it; the sprint seconds, the overheats, and whether the first one's picture was taken.
    private bool _limiterTried, _hotShot, _cooling;
    private int _overheats, _lastBlockedAt = -1;
    private readonly HashSet<int> _downSeats = [];
    private double _sprinted, _sprintFrom = -1;

    /// <summary>
    /// Once a run, as soon as the Locator is free of its clamp and the limiter still holds: Shift pressed, as a player tries
    /// it, the drive's answer read and pictured. True when it did so this frame.
    /// </summary>
    private async Task<bool> LimiterTried(Game game, StoryRun story, Machine hero)
    {
        if (_limiterTried || !story.Director.Has("free") || game.World.StorySprintFree || game.HackShown)
            return false;
        _limiterTried = true;
        Stop(hero);
        await Tap(Key.Shift);
        await Wait(1.2);
        await Picture("limiter-locked");
        Tell("tried the sprint while the limiter holds it");
        return true;
    }

    /// <summary>
    /// The sprint held as a player would once it is free: while driving ahead, let go when the drive's heat nears full and
    /// taken up again once it has cooled to a third, except the first time, when it is run until it overheats (its picture).
    /// </summary>
    private void Sprint(Game game, Machine hero, bool ahead)
    {
        if (game.World.StoryDriveOf(hero.Id) is not { } drive || !game.World.StorySprintFree)
        {
            HoldKey(Key.Shift, false);
            return;
        }
        if (_overheats > 0 && drive.Heat >= 0.85f || drive.Blocked)
            _cooling = true;
        else if (drive.Heat <= StoryDrive.Resume)
            _cooling = false;
        // As a person would, it reads the freed limiter's hint for a moment before it first sprints.
        var reading = game.World.Story?.Director.Fired.TryGetValue("limiter", out var freed) == true && game.World.Time < freed + 2;
        HoldKey(Key.Shift, ahead && !_cooling && !reading);
    }

    /// <summary>After each frame's leg: the sprint's seconds counted, an overheat told and its first one pictured.</summary>
    private async Task DriveWatched(Game game, Machine hero)
    {
        if (game.World.StoryDriveOf(hero.Id) is not { } drive)
            return;
        var now = game.World.Time;
        if (drive.Sprinting && _sprintFrom < 0)
            _sprintFrom = now;
        else if (!drive.Sprinting && _sprintFrom >= 0)
        {
            _sprinted += now - _sprintFrom;
            _sprintFrom = -1;
        }
        if (drive.Blocked && drive.BlockedAt != _lastBlockedAt)
        {
            _lastBlockedAt = drive.BlockedAt;
            _overheats++;
            Tell(FormattableString.Invariant($"drive overheated at {now:0.0} s ({_overheats}), sprinted {_sprinted:0.0} s so far"));
        }
        // A Grunt goes down: where, and who was nearest (the chases' fairness at the walk).
        if (game.World.Story is { } run)
        {
            foreach (var seat in run.Seats.Skip(1))
            {
                if (!game.World.CompanionDown(seat))
                {
                    _downSeats.Remove(seat.Value);
                    continue;
                }
                if (!_downSeats.Add(seat.Value) || game.World.HeroOf(seat) is not { } grunt)
                    continue;
                var near = new[] { Prologue.ThresherRole, Prologue.ReaperRole, Prologue.CommanderRole, Prologue.HostileRole }.SelectMany(r => run.Director.Of(game.World, r)).Where(m => !m.IsDead)
                    .OrderBy(m => NVector2.Distance(m.Position, grunt.Position)).FirstOrDefault();
                Tell(FormattableString.Invariant($"grunt {seat.Value} down at {now:0.0} s at {grunt.Position.X:0.0},{grunt.Position.Y:0.0}, nearest {near?.Spec.TypeName ?? "-"}#{near?.Id} {(near is null ? 0 : NVector2.Distance(near.Position, grunt.Position)):0.0} m"));
            }
        }
        if (drive.Blocked && !_hotShot)
        {
            _hotShot = true;
            await Wait(0.4);
            await Picture("drive-hot");
        }
    }

    /// <summary>
    /// A Grunt's seat online: after the Locator, a few metres behind it, and into the pod's zone once its hatch is open (the
    /// launch waits for both Grunts aboard).
    /// </summary>
    private void Accompany(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        if (story.Flags.Contains(StoryMarks.Done("pod_3_hatch")) && world.Deck?.Zone(GameWorld.PodZone) is { } pod)
        {
            if (pod.Contains(hero.Position))
                Wait(game, hero, "the launch, aboard");
            else
                Go(game, hero, "aboard", (pod.Min + pod.Max) / 2, 1);
            return;
        }
        if (world.HeroOf(story.Locator) is not { } locator)
        {
            Wait(game, hero, "the Locator");
            return;
        }
        // Near it, it stands (on the same leg, so the log does not count each stop as one).
        if (NVector2.Distance(hero.Position, locator.Position) <= 5)
        {
            Overdue("after the Locator", double.MaxValue);
            _legSince = Time.GetTicksMsec() / 1000.0;
            Stop(hero);
        }
        else
            Go(game, hero, "after the Locator", locator.Position, 4);
    }

    /// <summary>
    /// MACHINES_STORY_DRIVE_CRASH=1: the first time in the Notgang the hero waits until the countdown runs out, so the chapter
    /// is lost as Crashed (its card, its way back to the checkpoint); after that it plays on as ever.
    /// </summary>
    // MACHINES_STORY_DRIVE_NOBARRICADE=1: the Grunts are told to wait as the core's hack begins, so no door is barricaded.
    private static bool NoBarricade => System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE_NOBARRICADE") is { Length: > 0 } no && no != "0";
    private bool _toldWait;

    /// <summary>The cover in the core chamber the Locator takes at the break-in (the director's objective there).</summary>
    private static NVector2 Cover => new(219, 262);

    private static bool CrashFirst => System.Environment.GetEnvironmentVariable("MACHINES_STORY_DRIVE_CRASH") is { Length: > 0 } crash && crash != "0";

    // "on, off, on" once the Lamp key was seen to switch the Locator's lamp both ways; null before the check.
    private string? _lampChecked;

    /// <summary>
    /// Once a run, as soon as the hero is driven: the Lamp key (KeyMap.Act.Light) tapped twice, the sim's lamp of the hero
    /// (GameWorld.StoryLampOn, switched by StoryLampCommand) read after each, so it goes out and comes on again.
    /// </summary>
    private async Task Lamp(Game game, Machine hero)
    {
        Stop(hero);
        // A moment in the hero first, as a hand would: the view and its keys settle.
        await Wait(1.5);
        if (!IsInstanceValid(game))
            return;
        var seen = new List<bool> { game.World.StoryLampOn(hero.Id) };
        for (var i = 0; i < 2; i++)
        {
            var was = seen[^1];
            await Tap(KeyMap.Of(KeyMap.Act.Light));
            await Until(() => !IsInstanceValid(game) || game.World.StoryLampOn(hero.Id) != was, 2);
            if (!IsInstanceValid(game))
                return;
            seen.Add(game.World.StoryLampOn(hero.Id));
        }
        _lampChecked = string.Join(", ", seen.Select(on => on ? "on" : "off"));
        var switched = seen[1] != seen[0] && seen[2] == seen[0];
        Tell($"lamp key: {_lampChecked}{(switched ? "" : " (did not switch)")}");
    }

    /// <summary>
    /// After the trio got away: the chapter's film outside on its own map (StoryFilm), its picture a while in, then the
    /// chapter's card (StoryDoneCard), its picture, and Space to the menus, where the run ends.
    /// </summary>
    private async Task Film(Game deck)
    {
        var online = deck.Online;
        if (await Until(() => Scene is Game { World.Story: null, FilmingStoryEnd: true } found && found != deck && found.IsNodeReady() ? found : null, 90) is not { } film)
            throw new InvalidOperationException("the chapter's film did not come");
        Tell($"film: {MatchRequest.Current.Film?.Cutscene} on {MatchRequest.Current.Film?.Planet}");
        HitchLog.Phase = "film";
        await Until(() => !IsInstanceValid(film) || film.InCutscene, 20);
        var began = Time.GetTicksMsec() / 1000.0;
        await Wait(12);
        if (IsInstanceValid(film) && film.InCutscene)
            await Picture("film");
        await Until(() => !IsInstanceValid(film) || film.GetNodeOrNull("StoryDoneLayer/StoryDoneCard") is not null, 120);
        if (!IsInstanceValid(film) || film.GetNodeOrNull("StoryDoneLayer/StoryDoneCard") is null)
            throw new InvalidOperationException("the chapter's card did not come");
        Tell($"film over after {Time.GetTicksMsec() / 1000.0 - began:0.0} s: the chapter's card");
        await Wait(3);
        await Picture("card");
        // The menus would start the match again straight away for an automated run (MenuScreens): the run is over now.
        foreach (var name in new[] { "MACHINES_MAP", "MACHINES_STORY" })
            System.Environment.SetEnvironmentVariable(name, null);
        await Tap(Key.Space);
        // Played online the card goes on to the rooms (Game.LeaveStoryFilm), else to the story's page.
        if (online)
        {
            await Until(() => Find<MultiplayerPage>() is not null, 30);
            await Wait(1.5);
            Tell($"back at the menus: rooms {(Find<MultiplayerPage>() is not null ? "shown" : "missing")}");
            return;
        }
        await Until(() => Find<StoryPage>() is not null, 30);
        await Wait(1.5);
        Tell($"back at the menus: story page {(Find<StoryPage>() is not null ? "shown" : "missing")}");
        await Picture("menus");
    }

    /// <summary>A Grunt of the trio lying down outside the pod (the director's rally points at it), or null.</summary>
    private static Machine? Fallen(Game game, StoryRun story)
    {
        var world = game.World;
        var pod = world.Deck?.Zone(GameWorld.PodZone);
        foreach (var seat in story.Seats.Skip(1))
        {
            if (world.CompanionDown(seat) && world.HeroOf(seat) is { } grunt && pod?.Contains(grunt.Position) != true)
                return grunt;
        }
        return null;
    }

    /// <summary>To a fallen Grunt, then the use key held until it is up again (GameWorld.ReviveReach, four seconds).</summary>
    private void Revive(Game game, Machine hero, Machine fallen)
    {
        Fire(game, hero, 0, false);
        if (!Go(game, hero, "revive " + fallen.Id, fallen.Position, GameWorld.ReviveReach - 1.5f))
        {
            if (_reviving)
                Hold(KeyMap.Act.Use, _reviving = false);
            return;
        }
        Look(game, Heading(hero.Position, fallen.Position), -0.1f);
        _reviving = true;
        Hold(KeyMap.Act.Use, true);
    }

    /// <summary>To a weapon's rack or the pod's hatch, then its use key (StoryUseCommand) until the world says it is done.</summary>
    private async Task TakeUp(Game game, Machine hero, string rack, NVector2 at, float near = 1.5f)
    {
        if (!Go(game, hero, "use " + rack, at, near))
            return;
        var now = Time.GetTicksMsec() / 1000.0;
        if (now - _lastUse > 1.5)
        {
            _lastUse = now;
            await Tap(KeyMap.Of(KeyMap.Act.Use));
        }
    }

    /// <summary>
    /// The workshop's armed Grunts, the nearest first: closed in on and shot at through the real triggers (WiredDriver's
    /// Attack). With none of them left standing in sight the hero waits for the director.
    /// </summary>
    private void Fight(Game game, StoryRun story, Machine hero)
    {
        var world = game.World;
        var foe = story.Director.Of(world, Prologue.HostileRole).Where(m => !m.IsDead)
            .OrderBy(m => NVector2.DistanceSquared(m.Position, hero.Position)).ThenBy(m => m.Id).FirstOrDefault();
        if (foe is null)
        {
            Fire(game, hero, 0, false);
            Sprint(game, hero, false);
            Wait(game, hero, "the workshop cleared");
            return;
        }
        if (Overdue("fight", 150))
        {
            Tell($"the fight in the workshop took too long: {foe.Id} at {foe.Position.X:0.0},{foe.Position.Y:0.0} still stands");
            _legSince = Time.GetTicksMsec() / 1000.0;
        }
        Attack(game, hero, foe);
        // Closing in, or round to where it sees its foe, it sprints as a player would: at the trio's walk it stood in the
        // armed Grunts' fire for so long that it was lost there, run after run.
        Sprint(game, hero, Forward);
    }

    /// <summary>Starts a leg's clock when the leg changes; true once it has run longer than so many seconds.</summary>
    private bool Overdue(string leg, double seconds)
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (_leg != leg)
        {
            (_leg, _legSince, _route, _clicks, _stalls) = (leg, now, [], 0, 0);
            Tell($"leg: {leg}");
        }
        return now - _legSince > seconds;
    }

    private void Wait(Game game, Machine hero, string what)
    {
        Overdue("waiting for " + what, double.MaxValue);
        Stop(hero);
        Look(game, game.AimYaw, -0.05f);
    }

    /// <summary>To a place along the grid; after 90 s on the leg the hero is set down there.</summary>
    private bool Go(Game game, Machine hero, string leg, NVector2 place, float near)
    {
        if (Overdue(leg, 90))
        {
            _legSince = Time.GetTicksMsec() / 1000.0;
            // Online the world is every instance's: one set down here alone would part this world from the others'.
            if (game.Online)
            {
                Tell($"stuck online on '{leg}' at {hero.Position.X:0.0},{hero.Position.Y:0.0}, not set down");
                _stuckOnline++;
                return false;
            }
            Tell($"stuck on '{leg}' at {hero.Position.X:0.0},{hero.Position.Y:0.0}: set down at {place.X:0.0},{place.Y:0.0}");
            _fallbacks++;
            hero.Position = place;
            return false;
        }
        return Steer(game, hero, place, near);
    }

    /// <summary>To a hack's target, plugged in with the hack's key, the board's solution clicked turn by turn.</summary>
    private async Task Hack(Game game, StoryRun story, Machine hero, string target)
    {
        var world = game.World;
        if (story.Hacks.Find(target, world.Deck) is not { } found)
            throw new InvalidOperationException($"no hack target {target}");
        // Plugged in, the leg's clock waits: the core's last board holds for as long as the squad takes to get in.
        if (_leg == "hack " + target && story.Hacks.Session(target) is { Open: true })
            _legSince = Time.GetTicksMsec() / 1000.0;
        // A board already up is played where the Locator stands: plugged in a step further off than the way's end (pushed
        // back after its key, as at D5 once), it would walk on forever, the clock waiting, and never click.
        if (story.Hacks.Session(target) is { Open: true } && game.HackBoardShown is not null)
        {
            Overdue("hack " + target, double.MaxValue);
            Stop(hero);
        }
        else if (!Go(game, hero, "hack " + target, found.Position, GameWorld.HackReach - 1.5f))
            return;
        var now = Time.GetTicksMsec() / 1000.0;
        if (game.HackBoardShown is not { } host)
        {
            if (now - _lastUse > 1.5)
            {
                _lastUse = now;
                await Tap(KeyMap.Of(KeyMap.Act.Use));
            }
            return;
        }
        // A board just put up is drawn on a later frame: the first click waits a moment, as a hand would.
        if (!ReferenceEquals(host, _board) || host.Opened != _boardOpened)
        {
            (_board, _boardOpened, _clickAt) = (host, host.Opened, now + 0.8);
            return;
        }
        // A board its target locked (the core's last, at 90 %) is not clicked: the Locator waits at it.
        if (now < _clickAt || story.Hacks.Session(target) is not { Open: true, Broken: false, Held: false, Puzzle: { } puzzle })
            return;
        var turns = puzzle.SolutionTurns().Take(1).ToList();
        if (turns.Count == 0)
            return;
        var (cell, clockwise) = turns[0];
        var at = host.Board.GetGlobalTransformWithCanvas() * host.Board.CellRect(cell).GetCenter();
        var button = clockwise ? MouseButton.Left : MouseButton.Right;
        GetViewport().PushInput(new InputEventMouseMotion { Position = at, GlobalPosition = at });
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = button, Pressed = true, Position = at, GlobalPosition = at });
        GetViewport().PushInput(new InputEventMouseButton { ButtonIndex = button, Pressed = false, Position = at, GlobalPosition = at });
        // The core's boards are played at a person's pace, a turn every 2 s (the squad's timing is tuned to it).
        _clickAt = now + (target == "hack_core" ? 2 : 0.4);
        if (++_clicks % 20 == 0)
            Tell($"hack {target}: {_clicks} clicks, board {story.Hacks.Session(target)?.Stage + 1}, progress {puzzle.Progress:0.00}");
    }

    /// <summary>
    /// Into a zone, where the Grunts are told to follow (their key) until all three are in; after 150 s the Grunts still out
    /// are set down beside the Locator.
    /// </summary>
    private async Task Gather(Game game, StoryRun story, Machine hero, string leg, string zone)
    {
        var world = game.World;
        var area = world.Deck!.Zones.First(z => z.Name == zone);
        var middle = (area.Min + area.Max) / 2;
        if (Overdue(leg, 150) && game.Online)
        {
            Tell($"the Grunts did not all come into {zone} online in 150 s: not set down");
            _stuckOnline++;
            _legSince = Time.GetTicksMsec() / 1000.0;
        }
        else if (Overdue(leg, 150))
        {
            foreach (var seat in story.Seats.Skip(1))
            {
                if (world.HeroOf(seat) is { } grunt && !area.Contains(grunt.Position))
                {
                    Tell(FormattableString.Invariant($"the Grunt of {seat} did not come into {zone} (at {grunt.Position.X:0.0},{grunt.Position.Y:0.0}, {grunt.Order.GetType().Name}): set down beside the Locator"));
                    _fallbacks++;
                    grunt.Position = hero.Position + new NVector2(story.Seats.IndexOf(seat) == 1 ? -2 : 2, 0);
                }
            }
            _legSince = Time.GetTicksMsec() / 1000.0;
            return;
        }
        if (!Steer(game, hero, middle, 2))
            return;
        // Told to follow only while one of those still out has no job of its own: a follow order ends a job, and the pod's
        // boarding brings them to their places beside the hatch, while one following keeps its distance in the queue.
        var now = Time.GetTicksMsec() / 1000.0;
        var idle = story.Seats.Skip(1).Any(seat => world.HeroOf(seat) is { } grunt && !area.Contains(grunt.Position)
            && story.Crew.Of(seat).Task == CompanionTask.None);
        if (idle && now - _lastFollow > 20)
        {
            _lastFollow = now;
            await Tap(KeyMap.Of(KeyMap.Act.GruntFollow));
        }
    }

    // ---- going ------------------------------------------------------------------------------------

    /// <summary>One frame towards a place along the way over the grid, the view on the way; true once near enough.</summary>
    private bool Steer(Game game, Machine hero, NVector2 place, float near)
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (NVector2.Distance(hero.Position, place) <= near)
        {
            Stop(hero);
            HoldKey(Key.Shift, false);
            return true;
        }
        if (now < _reverseUntil)
        {
            Hold(KeyMap.Act.Forward, false);
            Hold(KeyMap.Act.Back, true);
            return false;
        }
        Hold(KeyMap.Act.Back, false);
        if (_route.Count == 0 || NVector2.Distance(_wayTo, place) > 1 || now - _wayAt > 2.5)
        {
            var mobility = GameWorld.MobilityOf(hero);
            _route = FindWay(game.World.Nav, mobility, hero.Position, place, Openable(game.World, mobility), Standing(game.World, hero));
            (_wayTo, _wayAt) = (place, now);
        }
        while (_route.Count > 1 && NVector2.Distance(_route[0], hero.Position) < 1.2f)
            _route.RemoveAt(0);
        var next = _route.Count > 0 ? _route[0] : place;
        var heading = MathF.Atan2(next.Y - hero.Position.Y, next.X - hero.Position.X);
        Look(game, heading, -0.08f);
        TurnHull(hero, heading);
        var off = MathF.Abs(Mathf.Wrap(heading - hero.Facing, -Mathf.Pi, Mathf.Pi));
        Hold(KeyMap.Act.Forward, off < 0.6f);
        Sprint(game, hero, off < 0.3f && NVector2.Distance(hero.Position, place) > near + 3);
        // No headway for three seconds: a moment backwards, then the way again.
        if (NVector2.Distance(hero.Position, _progressFrom) > 0.6f)
            (_progressFrom, _progressAt) = (hero.Position, now);
        else if (now - _progressAt > 3)
        {
            // Where it stalls, for the log: the first few times a leg, with the way's next points and who stands near.
            if (++_stalls <= 3)
            {
                var by = game.World.Machines.Where(m => m.Id != hero.Id && !m.IsDead && NVector2.Distance(m.Position, hero.Position) < 8)
                    .Select(m => FormattableString.Invariant($"{m.Spec.TypeName}#{m.Id} {m.Position.X:0.0},{m.Position.Y:0.0}"));
                Tell(FormattableString.Invariant($"no headway on '{_leg}' at {hero.Position.X:0.0},{hero.Position.Y:0.0} facing {hero.Facing:0.00}, way {string.Join(" ", _route.Take(3).Select(p => $"{p.X:0.0},{p.Y:0.0}"))} of {_route.Count}, near: {string.Join("; ", by)}"));
            }
            (_reverseUntil, _progressAt, _route) = (now + 0.7, now, []);
        }
        return false;
    }

    /// <summary>
    /// The floor cells in and before the doors that open by themselves as the trio comes (the director's AutoDoors): the way
    /// may lead through them, as a player walks up to such a door. Only those with the hero's room once the door is open
    /// (its leaves taken as floor, the walls as ever): taking every floor cell near the door, the way hugged the wall beside
    /// door T, where the hero does not fit, and the hero pushed at that wall for 90 s until it was set down (the intermittent
    /// 'notgang' stall: door T opens only once one of the trio is near it, and sometimes no Grunt had come by).
    /// </summary>
    private static HashSet<int> Openable(GameWorld world, Mobility mobility)
    {
        var cells = new HashSet<int>();
        if (world.Story is not { } story || world.Deck is not { } deck)
            return cells;
        var nav = world.Nav;
        var point = mobility with { Size = 0 };
        bool Blocks(int x, int y) => x < 0 || y < 0 || x >= nav.Width || y >= nav.Depth
            || !nav.IsFree(x, y, point) && deck.CellAt(nav.Centre(x, y)) != DeckCell.Door;
        // The room a cell would have with the doors open, as the grid measures it: twice the distance to the nearest blocked
        // cell, squared, against (2 size + 1) squared.
        bool Roomy(int x, int y)
        {
            var need = (2 * mobility.Size + 1) * (2 * mobility.Size + 1);
            var reach = mobility.Size + 1;
            for (var dy = -reach; dy <= reach; dy++)
            {
                for (var dx = -reach; dx <= reach; dx++)
                {
                    var (ax, ay) = (Math.Max(0, 2 * Math.Abs(dx) - 1), Math.Max(0, 2 * Math.Abs(dy) - 1));
                    if (ax * ax + ay * ay < need && Blocks(x + dx, y + dy))
                        return false;
                }
            }
            return true;
        }
        foreach (var letter in story.Director.AutoDoors)
        {
            var corners = world.DoorLeaves(letter).SelectMany(a => a.Boundary).ToList();
            if (corners.Count == 0)
                continue;
            var (min, max) = (new NVector2(corners.Min(p => p.X), corners.Min(p => p.Y)) - NVector2.One * 4, new NVector2(corners.Max(p => p.X), corners.Max(p => p.Y)) + NVector2.One * 4);
            for (var y = (int)min.Y; y <= (int)max.Y; y++)
            {
                for (var x = (int)min.X; x <= (int)max.X; x++)
                {
                    var (cx, cy) = nav.Cell(new NVector2(x + 0.5f, y + 0.5f));
                    if (deck.CellAt(new NVector2(x + 0.5f, y + 0.5f)) is DeckCell.Floor or DeckCell.Door && !Blocks(cx, cy) && Roomy(cx, cy))
                        cells.Add(cy * nav.Width + cx);
                }
            }
        }
        return cells;
    }

    /// <summary>The cells other machines stand on (the dark rows, the Thresher), with room for the hero to pass: the way goes round them.</summary>
    private static HashSet<int> Standing(GameWorld world, Machine hero)
    {
        var nav = world.Nav;
        var cells = new HashSet<int>();
        foreach (var machine in world.Machines)
        {
            // The Grunts the computer drives step aside when the Locator runs into them (GameWorld.MakesWayInStory), as
            // they do for a player: the way goes through them, or two Grunts that followed it into a bay shut it in.
            if (machine.Id == hero.Id || machine.IsDead || !machine.DrivenByPlayer && world.CompanionSeat(machine) is not null)
                continue;
            var reach = machine.Radius + hero.Radius + 0.5f;
            var apart = NVector2.Distance(hero.Position, machine.Position);
            for (var y = (int)(machine.Position.Y - reach); y <= (int)(machine.Position.Y + reach); y++)
            {
                for (var x = (int)(machine.Position.X - reach); x <= (int)(machine.Position.X + reach); x++)
                {
                    var middle = new NVector2(x + 0.5f, y + 0.5f);
                    // The cells round the hero stay open so it can get away from one it already stands close to, but only
                    // those no nearer to that machine: before, the way led past it from there (a hostile Grunt beside the
                    // lane to the Bolter's rack), and the hero ran into it and backed off by turns.
                    var away = NVector2.Distance(middle, hero.Position) < hero.Radius + 1 && NVector2.Distance(middle, machine.Position) >= apart;
                    if (NVector2.Distance(middle, machine.Position) > reach || away)
                        continue;
                    var (cx, cy) = nav.Cell(middle);
                    cells.Add(cy * nav.Width + cx);
                }
            }
        }
        return cells;
    }

    /// <summary>
    /// A way over the free cells of the deck's grid for the hero's room (and ducts, where it crawls), straightened: the
    /// driver's own reading of the map. The world's own search is not asked, as it keeps searches shared within a tick.
    /// </summary>
    private static List<NVector2> FindWay(NavGrid nav, Mobility mobility, NVector2 from, NVector2 to, IReadOnlySet<int> doors, IReadOnlySet<int> standing)
    {
        var width = nav.Width;
        int Index((int X, int Y) cell) => cell.Y * width + cell.X;
        bool Free(int x, int y) => x >= 0 && y >= 0 && x < width && y < nav.Depth && (nav.IsFree(x, y, mobility) || doors.Contains(y * width + x)) && !standing.Contains(y * width + x);
        var (start, goal) = (Index(nav.Cell(from)), Index(nav.Cell(to)));
        float Guess(int cell) => NVector2.Distance(new NVector2(cell % width, cell / width), new NVector2(goal % width, goal / width));
        var cost = new Dictionary<int, float> { [start] = 0 };
        var came = new Dictionary<int, int>();
        var open = new PriorityQueue<int, float>();
        open.Enqueue(start, Guess(start));
        var (best, nearest) = (start, Guess(start));
        for (var expanded = 0; expanded < 120_000 && open.TryDequeue(out var cell, out _); expanded++)
        {
            if (cell == goal)
            {
                best = goal;
                break;
            }
            if (Guess(cell) < nearest)
                (best, nearest) = (cell, Guess(cell));
            var (x, y) = (cell % width, cell / width);
            for (var dy = -1; dy <= 1; dy++)
            {
                for (var dx = -1; dx <= 1; dx++)
                {
                    var (nx, ny) = (x + dx, y + dy);
                    var next = ny * width + nx;
                    if (dx == 0 && dy == 0 || !Free(nx, ny) && next != goal)
                        continue;
                    if (dx != 0 && dy != 0 && (!Free(x + dx, y) || !Free(x, y + dy)))
                        continue;
                    var through = cost[cell] + (dx != 0 && dy != 0 ? 1.4142f : 1);
                    if (cost.TryGetValue(next, out var known) && known <= through)
                        continue;
                    cost[next] = through;
                    came[next] = cell;
                    open.Enqueue(next, through + Guess(next));
                }
            }
        }
        var cells = new List<int>();
        for (var at = best; at != start; at = came[at])
            cells.Add(at);
        cells.Reverse();
        var points = cells.Select(c => nav.Centre(c % width, c / width)).ToList();
        if (best == goal && points.Count > 0)
            points[^1] = to;
        // Straightened: from each point on to the furthest one in a clear line over the cells the search took as free, so
        // the line does not cut past a machine standing beside the way (a hostile Grunt before the repair niche's door).
        var straight = new List<NVector2>();
        var from0 = from;
        for (var i = 0; i < points.Count;)
        {
            var far = i;
            for (var j = points.Count - 1; j > i; j--)
            {
                if (Clear(nav, Free, from0, points[j]))
                {
                    far = j;
                    break;
                }
            }
            straight.Add(points[far]);
            from0 = points[far];
            i = far + 1;
        }
        return straight;
    }

    /// <summary>Whether the line between two points crosses only free cells (sampled every half cell).</summary>
    private static bool Clear(NavGrid nav, Func<int, int, bool> free, NVector2 a, NVector2 b)
    {
        var steps = (int)MathF.Ceiling(NVector2.Distance(a, b) / (nav.CellSize * 0.5f));
        for (var i = 1; i < steps; i++)
        {
            var (x, y) = nav.Cell(NVector2.Lerp(a, b, (float)i / steps));
            if (!free(x, y))
                return false;
        }
        return true;
    }
}
