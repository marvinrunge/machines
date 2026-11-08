using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;
using Machines.WiredBench;

namespace Machines.Game;

/// <summary>
/// For automated checks of a whole Wired run in the real game, through the real input: MACHINES_WIRED_DRIVE=&lt;folder&gt;[,&lt;option&gt;...]
/// goes from the main menu to the hero's creation, starts the run and plays it in first person with held keys and buttons:
/// it fetches wrecks, opens the hangar at the pod and shops there by clicks and drags, calls the waves, takes the chip a boss
/// leaves, orders the squad, uses the abilities and the dash, gives up at a wave, falls, comes back on the spare bodies and
/// goes through the end screen to the menu. What to do the balance bench's bot decides (tools/wired-bench); the driver only
/// carries its commands out with the keys and the mouse, and never touches the world. It prints "drive: ..." lines, with the
/// world's state and checksum every 100 ticks, and writes its pictures to the folder. Over a recording (MACHINES_REPLAY) it
/// prints the same lines and nothing else, for the checksums to be compared. Options:
/// <list type="bullet">
/// <item><c>name=&lt;text&gt;</c>: the hero's name (Driver);</item>
/// <item><c>waves=&lt;n&gt;</c>: from the start of that wave on it stops firing and drives into the enemy until the run is lost,
/// with its abilities and the dash still used (6);</item>
/// <item><c>save=&lt;n&gt;</c>: once that wave is cleared it saves through the menu, as "driver &lt;n&gt;", and plays on;</item>
/// <item><c>load=&lt;s&gt;</c>: that many seconds of the world after the save it loads the save again through the menu (30, 0 for never);</item>
/// <item><c>shots=&lt;moment&gt;+...</c> or <c>all</c>: pictures at menu, creation, start, hangar, wave, boss, chip, down, body, dash,
/// ability, stuck, save, load, lose, end and after, and at t&lt;tick&gt;; a file "snap" in the folder asks for one at once, named by what it holds;</item>
/// <item><c>end=menu|hangar</c>: the end screen's button it presses (menu);</item>
/// <item><c>shop=squad|kit</c>: what it buys besides the Grunt 2, a squad or the thrusters and an ability (squad).</item>
/// </list>
/// Wired is the remake's own addition.
/// </summary>
public partial class WiredDriver(string folder, WiredDriver.Plan plan) : Node
{
    /// <summary>What the switch asks for.</summary>
    public sealed record Plan(string Name, int GiveUp, int? SaveAfter, float LoadAfter, IReadOnlySet<string> Shots, string End, string Shop)
    {
        public static Plan Parse(IEnumerable<string> options)
        {
            var set = options.Select(o => o.Split('=', 2)).Where(w => w.Length == 2).ToDictionary(w => w[0].Trim(), w => w[1].Trim());
            string? Get(string key) => set.TryGetValue(key, out var value) && value.Length > 0 ? value : null;
            return new Plan(Get("name") ?? "Driver", int.Parse(Get("waves") ?? "6"), Get("save") is { } save ? int.Parse(save) : null,
                float.Parse(Get("load") ?? "30", System.Globalization.CultureInfo.InvariantCulture),
                (Get("shots") ?? "").Split('+', StringSplitOptions.RemoveEmptyEntries).ToHashSet(), Get("end") ?? "menu", Get("shop") ?? "squad");
        }

        /// <summary>Whether a picture is wanted at a moment.</summary>
        public bool Shoots(string moment) => Shots.Contains("all") || Shots.Contains(moment);
    }

    /// <summary>
    /// How the driver shops, its own and not one of the bench's: the Grunt 2 is cheap enough to be changed to at once, then
    /// "squad" recruits whenever it can, and "kit" leaves the squad out and so comes to the thrusters (the dash) and an
    /// ability. A short run cannot pay for both.
    /// </summary>
    private Strategy Shopping => new("driver", ["grunt2"], plan.Shop == "kit" ? [Goal.Chassis, Goal.Module, Goal.Ability] : [Goal.Chassis, Goal.Squad, Goal.Module, Goal.Ability])
    {
        Modules = ["thrusters"],
        Abilities = ["discharge", "minefield", "supercharge"],
    };

    private static bool _attending;

    private readonly Queue<string> _pictures = new();
    private int _shots;
    private Vector2 _mouse = new(4, 4);
    private bool _down;
    private double _lastDeal;

    /// <summary>Comes along if the switch says so, from the first screen on; it outlives every change of scene.</summary>
    public static void Attend(SceneTree tree)
    {
        if (_attending || System.Environment.GetEnvironmentVariable("MACHINES_WIRED_DRIVE") is not { Length: > 0 } drive)
            return;
        _attending = true;
        var words = drive.Split(',');
        tree.Root.CallDeferred(Node.MethodName.AddChild, new WiredDriver(words[0], Plan.Parse(words.Skip(1))));
    }

    private static void Say(string text) => GD.Print($"drive: {text}");

    private Node? Scene => GetTree().CurrentScene;

    public override async void _Ready()
    {
        ProcessMode = ProcessModeEnum.Always;
        System.IO.Directory.CreateDirectory(folder);
        Say($"plan: hero {plan.Name}, shops for {plan.Shop}, gives up at wave {plan.GiveUp}, saves after wave {plan.SaveAfter?.ToString() ?? "none"}, " +
            $"loads {plan.LoadAfter} s later, pictures [{string.Join(", ", plan.Shots)}], ends by {plan.End}, folder {folder}");
        try
        {
            await Until(() => Scene is Game found && found.IsNodeReady() || Find<MainMenuPage>() is not null, 120);
            if (Scene is Game { Watching: true })
                await Watch();
            else
            {
                if (Scene is not Game)
                {
                    await Menu();
                    await Create();
                }
                if (await Play())
                    await End();
            }
        }
        catch (Exception exception)
        {
            Say($"failed: {exception}");
        }
        Say("over");
        GetTree().Quit();
    }

    // ---- the way in -------------------------------------------------------------------------------

    /// <summary>The main menu's Wired button.</summary>
    private async Task Menu()
    {
        await Until(() => Find<MainMenuPage>(), 60);
        await Until(() => !MenuFilms.Playing, 30);
        // The page may be put up again in its first moments: the one there after them is the one to press.
        await Wait(1.5);
        var menu = await Until(() => Find<MainMenuPage>() is { } page && IsInstanceValid(page) ? page : null, 30) ?? throw new InvalidOperationException("no main menu came");
        await Moment("menu");
        var wired = Buttons(menu).First(b => b.Text == Lang.T("Wired").ToUpperInvariant());
        Say("main menu: pressing Wired");
        await Click(Middle(wired));
    }

    /// <summary>The hero's creation: name, colour, difficulty and arena, then the start.</summary>
    private async Task Create()
    {
        var page = await Until(() => Find<HangarPage>() is { Desk: CreationDesk } found ? found : null, 60) ?? throw new InvalidOperationException("no creation came");
        await Wait(2.5);
        var desk = (CreationDesk)page.Desk;

        var name = page.FindChildren("*", nameof(LineEdit), true, false).OfType<LineEdit>().First();
        await Click(Middle(name));
        await Frames();
        await Press(Godot.Key.A, ctrl: true);
        foreach (var letter in plan.Name)
            await Letter(letter);
        await Click(new Vector2(GetViewport().GetVisibleRect().Size.X / 2, 30));

        // The lamps stand in the order of the colours: red first.
        var lamp = page.FindChildren("*", "", true, false).OfType<ColourLamp>().First();
        await Click(Middle(lamp));

        var choices = page.FindChildren("*", "", true, false).OfType<ChipChoice>().ToList();
        await Step(choices[0], Lang.T(nameof(WiredDifficulty.Normal)));
        await Step(choices[1], HangarText.Arena("m_desert"));
        await Wait(0.5);
        Say($"creation: name '{desk.Draft.Name}', colour {desk.Draft.Colour}, {desk.Draft.Difficulty}, {desk.Draft.Arena}, " +
            $"{desk.Loadout.Chassis.Catalogue} [{string.Join(", ", desk.Loadout.Worn.Skip(1).Select(i => i.Catalogue))}], bank {desk.Bank}");
        await Moment("creation");
        var start = Buttons(page).First(b => b.Text == Lang.T("Start the mission").ToUpperInvariant());
        Say("creation: pressing start");
        await Click(Middle(start));
    }

    /// <summary>Steps a choice on with clicks on its right half until it shows the item.</summary>
    private async Task Step(ChipChoice choice, string item)
    {
        for (var i = 0; i < choice.Items.Count && choice.Items[choice.Selected] != item; i++)
        {
            await Click(Middle(choice) + new Vector2(choice.Size.X / 4, 0));
            await Wait(0.25);
        }
        if (choice.Items[choice.Selected] != item)
            Say($"creation: '{item}' is not to be had, it stays on '{choice.Items[choice.Selected]}'");
    }

    // ---- the run ----------------------------------------------------------------------------------

    /// <summary>A run in the match, carried on into each match a load brings. True when its end screen came up.</summary>
    private async Task<bool> Play()
    {
        Game? left = null;
        while (true)
        {
            // After a load the match it left may stand a frame or two longer: the one to drive is the one that comes.
            if (await Until(() => Scene is Game found && found != left && found.IsNodeReady() && found.World.Wired is not null ? found : null, 120) is not { } game)
            {
                Say("no run came");
                return false;
            }
            if (await Drive(game))
                return true;
            left = game;
        }
    }

    /// <summary>The end screen: its show, its button, and the menu it leads to.</summary>
    private async Task End()
    {
        var page = WiredEndScreen.Current!;
        Say($"end screen: {page.Figures}");
        await Wait(1.2);
        await Moment("end");
        for (var waited = 0.0; !page.Done && waited < 30; waited += 0.1)
            await Wait(0.1);
        await Wait(1);
        await Moment("end");
        Say($"end screen done: {page.Figures}");
        if (Replays().FirstOrDefault() is { } replay)
            Say($"replay {replay}");
        var word = plan.End == "hangar" ? "Hangar" : "Menu";
        if (page.ButtonOf(Lang.T(word).ToUpperInvariant()) is not { } button)
        {
            Say($"no button {word}");
            return;
        }
        await Click(Middle(button));
        Say($"pressed {word}");
        var after = await Until(() => (Node?)Find<MainMenuPage>() ?? Find<HangarPage>(), 30);
        await Wait(2);
        await Moment("after");
        var record = WiredProfile.Load().Record("m_desert", WiredDifficulty.Normal);
        Say($"after the run: {after?.GetType().Name ?? "nothing"}, record on m_desert Normal: wave {record.Waves}, {record.Score} points");
    }

    /// <summary>The recordings kept since the driver came, newest first.</summary>
    private IEnumerable<string> Replays()
    {
        var replays = System.IO.Path.Combine(Paths.Root, "replays");
        return System.IO.Directory.Exists(replays)
            ? System.IO.Directory.GetFiles(replays, "*" + Machines.Net.Replay.Extension).Where(f => System.IO.File.GetLastWriteTime(f) >= _began)
                .OrderByDescending(System.IO.File.GetLastWriteTime)
            : [];
    }

    private readonly DateTime _began = DateTime.Now.AddSeconds(-5);

    // ---- a recording ------------------------------------------------------------------------------

    /// <summary>Over a recording only the world's state is printed, at the same ticks as when it was played.</summary>
    private async Task Watch()
    {
        var game = (Game)Scene!;
        var ticks = MatchRequest.Current.Replay!.Ticks;
        Say($"watching a recording of {ticks} ticks");
        game.Ticked += () => Report(game);
        await Until(() => !IsInstanceValid(game) || game.World.Tick >= ticks, 7200);
        await Wait(1);
        if (IsInstanceValid(game))
            Say($"recording over: tick {game.World.Tick} checksum {game.World.Checksum():x16}");
    }

    /// <summary>The world's state every 100 ticks: tick, checksum, wave, phase, bank, the hero, the spare bodies.</summary>
    private static void Report(Game game)
    {
        var world = game.World;
        if (world.Tick % 100 != 0 || world.Wired is not { } run || !run.Heroes.TryGetValue(Game.Player, out var state))
            return;
        var hero = world.HeroOf(Game.Player);
        var pod = world.WiredPod(Game.Player);
        Say($"tick {world.Tick} checksum {world.Checksum():x16} wave {run.Wave} {run.Phase} countdown {run.Countdown:0.0} bank {world.Races[Game.Player].Bmu:0} " +
            $"hero {state.Loadout.Chassis.Catalogue} {hero?.HitPoints}/{hero?.MaxHitPoints} armour {hero?.Armour}/{hero?.MaxArmour} refits {hero?.Refits} " +
            $"worn [{string.Join(", ", state.Loadout.Worn.Skip(1).Select(i => $"{i.Catalogue} Mk{i.Mk}"))}] bodies {state.SpareBodies} " +
            $"spares [{string.Join(", ", state.Inventory.Select(i => i.Catalogue))}] squad {world.Escorts(Game.Player).Count()} pod {pod?.HitPoints}/{pod?.MaxHitPoints} " +
            $"foes {run.EnemiesLeft} kills {state.Stats.Kills}");
    }

    // ---- pictures ---------------------------------------------------------------------------------

    /// <summary>A picture if the plan wants one at this moment.</summary>
    private async Task Moment(string moment, string? name = null)
    {
        if (plan.Shoots(moment))
            await Picture(name ?? moment);
    }

    private async Task Picture(string name)
    {
        await ToSignal(RenderingServer.Singleton, RenderingServer.SignalName.FramePostDraw);
        var path = System.IO.Path.Combine(folder, $"{++_shots:000}-{name}.png");
        GetViewport().GetTexture().GetImage().SavePng(path);
        Say($"picture {path}");
    }

    /// <summary>The pictures asked for since the last frame: by the plan's moments, or by a file "snap" in the folder.</summary>
    private async Task Pictures()
    {
        var snap = System.IO.Path.Combine(folder, "snap");
        if (System.IO.File.Exists(snap))
        {
            var name = System.IO.File.ReadAllText(snap).Trim();
            System.IO.File.Delete(snap);
            _pictures.Enqueue(name.Length > 0 ? name : "snap");
        }
        while (_pictures.TryDequeue(out var picture))
            await Picture(picture);
    }

    // ---- the hands --------------------------------------------------------------------------------

    private T? Find<T>() where T : class => GetTree().Root.FindChildren("*", "", true, false).OfType<T>().FirstOrDefault();

    private static IEnumerable<ChipButton> Buttons(Node node) => node.FindChildren("*", "", true, false).OfType<ChipButton>();

    private static Vector2 Middle(Control control) => control.GetGlobalTransform() * (control.Size / 2);

    private Vector2 ScreenMiddle => GetViewport().GetVisibleRect().Size / 2;

    private async Task Wait(double seconds) => await ToSignal(GetTree().CreateTimer(seconds, processAlways: true), SceneTreeTimer.SignalName.Timeout);

    private async Task Frames(int count = 2)
    {
        for (var i = 0; i < count; i++)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
    }

    /// <summary>Waits for something to come, at most so many seconds; null if it did not.</summary>
    private async Task<T?> Until<T>(Func<T?> found, double seconds) where T : class
    {
        for (var waited = 0.0; waited < seconds; waited += 0.1)
        {
            if (found() is { } it)
                return it;
            await Wait(0.1);
        }
        return found();
    }

    private async Task Until(Func<bool> done, double seconds) => await Until(() => done() ? this : null, seconds);

    /// <summary>The pointer goes to a point of the screen, in steps for a drag.</summary>
    private async Task Move(Vector2 to, int steps = 1)
    {
        var from = _mouse;
        for (var i = 1; i <= steps; i++)
        {
            var at = from.Lerp(to, (float)i / steps);
            GetViewport().PushInput(new InputEventMouseMotion
            {
                Position = at,
                GlobalPosition = at,
                Relative = at - _mouse,
                ButtonMask = _down ? MouseButtonMask.Left : 0,
            });
            _mouse = at;
            await Frames();
        }
    }

    private void Button(MouseButton button, bool pressed) => GetViewport().PushInput(new InputEventMouseButton
    {
        Position = _mouse,
        GlobalPosition = _mouse,
        ButtonIndex = button,
        Pressed = pressed,
        ButtonMask = pressed ? (MouseButtonMask)(1 << ((int)button - 1)) : 0,
        Factor = 1,
    });

    /// <summary>A click on the screen's controls: the pointer goes there first, as a hand would.</summary>
    private async Task Click(Vector2 at, MouseButton button = MouseButton.Left)
    {
        await Move(at);
        await Frames(2);
        Button(button, true);
        await Frames();
        Button(button, false);
        await Frames();
    }

    /// <summary>A card or a chip carried from one place to another with the button held.</summary>
    private async Task Drag(Vector2 from, Vector2 to)
    {
        await Move(from);
        await Frames();
        Button(MouseButton.Left, true);
        _down = true;
        await Frames();
        await Move(to, 12);
        await Wait(0.3);
        Button(MouseButton.Left, false);
        _down = false;
        await Frames();
    }

    /// <summary>A key pressed and let go on the screen's controls.</summary>
    private async Task Press(Key key, bool ctrl = false)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true, CtrlPressed = ctrl });
        await Frames();
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false, CtrlPressed = ctrl });
        await Frames();
    }

    private async Task Letter(char letter)
    {
        var key = char.IsAsciiLetter(letter) ? (Key)char.ToUpperInvariant(letter) : char.IsAsciiDigit(letter) ? (Key)letter : Godot.Key.Space;
        GetViewport().PushInput(new InputEventKey { Pressed = true, Unicode = letter, Keycode = key, ShiftPressed = char.IsUpper(letter) });
        await Frames();
        GetViewport().PushInput(new InputEventKey { Pressed = false, Keycode = key });
        await Frames();
    }
}
