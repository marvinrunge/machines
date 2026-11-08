using Godot;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The hangar over a run: the same screen as hero creation, on a stage of its own over the match. The world is not
/// paused, what is done there goes through the Wired commands, and the countdown stands while it is open.
/// </summary>
public partial class HangarHost(Game game, ClassicAssets assets) : Control
{
    private HangarPage _page = null!;
    private ChipButton _ready = null!;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        var stage = new MenuStage();
        AddChild(stage);
        MenuSounds.Attach(this, assets.GameFolder);
        stage.Show(_page = new HangarPage(new MatchDesk(game, assets), game.CloseHangar, null, Bar));
    }

    /// <summary>The hangar's foot: the chip board, the bank and the clock, and the button that calls the wave.</summary>
    private Control Bar(HangarPage page)
    {
        var chip = new ChipPanel { Padding = 8 };
        var row = MenuPage.Line(22);
        row.AddChild(new ChipBoard(page));
        row.AddChild(new HangarClock(game));
        _ready = MenuPage.Button("Ready", game.ReadyForWave, 76, 34);
        _ready.CustomMinimumSize = new Vector2(250, 76);
        var done = MenuPage.Button("Done", page.Back, 46, 19);
        done.CustomMinimumSize = new Vector2(250, 46);
        var buttons = MenuPage.Column(6, _ready, done);
        buttons.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        row.AddChild(buttons);
        chip.AddChild(row);
        return chip;
    }

    public override void _Process(double delta) => _ready.Disabled = game.World.Wired is not { Waiting: true };

    /// <summary>The world said no.</summary>
    public void Refuse(string why) => _page.Refuse(why);

    /// <summary>For automated checks.</summary>
    public HangarPage Page => _page;

    // Keys stay here while it is open, as they do in the menus: Esc goes back by the stage, and the hangar's own key closes it too.
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is not InputEventKey key)
            return;
        if (key is { Pressed: true, Echo: false } && KeyMap.Is(key, KeyMap.Act.Hangar))
            _page.Back();
        GetViewport().SetInputAsHandled();
    }
}

public partial class Game
{
    /// <summary>Seconds between two tries to write a run's end to the profile.</summary>
    private const double WiredRetry = 2;

    private HangarHost? _hangar;
    private readonly HangarWatch _hangarWatch = new();
    private bool _hangarScripted, _wiredCounted;
    private double _wiredRetryAt;

    /// <summary>Whether the hangar is up over the run.</summary>
    public bool HangarOpen => _hangar is not null && IsInstanceValid(_hangar);

    /// <summary>
    /// Opens the hangar, which stops the countdown and leaves the world running. The hero has to be at the pod with no wave
    /// under way: the message says why not otherwise. Nor does it open while the game is paused, as what is done there would
    /// wait for the world and land all at once. Only its own player's screen goes up, and a recording shows none.
    /// </summary>
    public void OpenHangar()
    {
        if (HangarOpen || Watching || World.Wired?.Heroes.GetValueOrDefault(Player) is not { } state)
            return;
        if ((Paused && !Online ? "not while the game is paused" : World.WhyNoHangar(state)) is { } why)
        {
            RunRefused(HangarText.Reason(why));
            return;
        }
        // What the mouse was doing on the field ends here: its button comes up over the hangar, where the field does not hear it.
        EndBand();
        _rightPress = null;
        _rig.Drag = Vector2I.Zero;
        Issue(new WiredHangarCommand(Player, true));
        _hangarWatch.Opening(state.HangarOpens);
        var layer = new CanvasLayer { Layer = 10, Name = "Hangar" };
        AddChild(layer);
        _hangar = new HangarHost(this, _assets);
        _hangar.SetAnchorsPreset(Control.LayoutPreset.FullRect);
        layer.AddChild(_hangar);
        _rig.Active = false;
        Input.MouseMode = Input.MouseModeEnum.Visible;
    }

    /// <summary>Back to the run.</summary>
    public void CloseHangar()
    {
        if (!HangarOpen)
            return;
        Issue(new WiredHangarCommand(Player, false));
        _hangarWatch.Closing();
        TakeDownHangar();
    }

    /// <summary>The screen goes; the mouse goes back to what it was doing, first person and all.</summary>
    private void TakeDownHangar()
    {
        _hangar!.GetParent().QueueFree();
        _hangar = null;
        _rig.Active = _driven is null;
        if (_driven is not null)
            Input.MouseMode = Input.MouseModeEnum.Captured;
        GetViewport().GuiReleaseFocus();
    }

    /// <summary>
    /// A run that has come to its end goes into the profile, whichever way it ended, and what it opened is said on the message
    /// line once it is written, not before. One that had ended when it was loaded has been counted, and one left half way can
    /// still be saved and gone on with.
    /// </summary>
    private void RecordWired()
    {
        if (_wiredCounted || Watching || World.Wired is not { Phase: WiredPhase.Ended } run || run.Heroes.GetValueOrDefault(Player) is not { } state)
            return;
        _wiredCounted = true;
        var options = run.Options;
        var profile = WiredProfile.Load();
        WiredBefore = profile.Held ? null : profile.Record(options.Arena, options.Difficulty);
        var result = new WiredRunResult(options.Arena, options.Difficulty, options.HeroName, state.Loadout.Chassis.Catalogue,
            WiredBalance.Score(state.Stats, options.Difficulty), state.Stats);
        if (WiredProfile.Runs.Tell(result) is { } opened)
            ShowOpened(opened);
        else
            _hud.Message(Lang.T("The run is not in the profile yet: the file cannot be written just now."));
    }

    /// <summary>A profile held by something else at a run's end usually is free again a moment later: the run is tried again every so often.</summary>
    private void RetryWired(double now)
    {
        if (!WiredProfile.Runs.Waiting || now < _wiredRetryAt)
            return;
        _wiredRetryAt = now + WiredRetry;
        if (WiredProfile.Runs.Write() is { } opened)
        {
            _hud.Message(Lang.T("The run is in the profile."));
            ShowOpened(opened);
        }
    }

    private void ShowOpened(IReadOnlyList<string> opened)
    {
        WiredOpened = opened;
        foreach (var id in opened)
            _hud.Message(Lang.T("Opened: {0}", HangarText.Opened(id)));
    }

    /// <summary>What the run that just ended opened, as ids of <see cref="WiredProfile.Unlocks"/>.</summary>
    public IReadOnlyList<string> WiredOpened { get; private set; } = [];

    /// <summary>What stood at this arena and difficulty before the run that just ended; null for a run that was not counted here, as a recording or a save that had ended.</summary>
    public WiredRecord? WiredBefore { get; private set; }

    /// <summary>Ready: the wave comes now, and the seconds saved are paid for.</summary>
    public void ReadyForWave() => Issue(new WiredReadyCommand(Player));

    /// <summary>
    /// The screen follows the sim, which has the last word: it goes up by a command and comes down with Ready, a wave, a
    /// death or the hero leaving, whoever's doing that was. A hangar a save was made in opens no screen and is closed.
    /// </summary>
    private void FollowHangar()
    {
        if (Watching || World.Wired?.Heroes.GetValueOrDefault(Player) is not { } state)
            return;
        switch (_hangarWatch.Follow(HangarOpen, state.HangarOpen, state.HangarOpens))
        {
            case HangarMove.TakeDown:
                TakeDownHangar();
                break;
            case HangarMove.Close:
                Issue(new WiredHangarCommand(Player, false));
                break;
        }
    }

    /// <summary>The world said no to a Wired command: over the hangar the screen says so, otherwise the message line.</summary>
    private void WiredRefused(Command command, string why)
    {
        if (command is WiredHangarCommand { Open: true })
        {
            _hangarWatch.Refused();
            if (HangarOpen && _hangarWatch.Answered(World.Wired!.Heroes[Player].HangarOpens))
                TakeDownHangar();
        }
        if (HangarOpen)
            _hangar!.Refuse(why);
        else
            RunRefused(HangarText.Reason(why));
    }

    /// <summary>
    /// For automated checks of the hangar: everything in it takes waves of play to pay for, so MACHINES_WIRED_GIFT=
    /// &lt;bmu&gt;,&lt;pod damage&gt;,&lt;chip&gt;+&lt;chip&gt;,offer gives a run that before its first tick, the last word being a boss's choice of
    /// chips. The world starts different by it, so its saves and recordings play back only with the same words.
    /// </summary>
    private void GiftForChecks()
    {
        if (World.Wired?.Heroes.GetValueOrDefault(Player) is not { } state || System.Environment.GetEnvironmentVariable("MACHINES_WIRED_GIFT") is not { Length: > 0 } gift)
            return;
        var words = gift.Split(',');
        World.Races[Player].Bmu = int.Parse(words[0]);
        if (World.WiredPod(Player) is { } pod)
            pod.HitPoints -= int.Parse(words[1]);
        foreach (var chip in words[2].Split('+', StringSplitOptions.RemoveEmptyEntries))
            state.Add(WiredKind.Chip, chip, 0);
        if (words.Length > 3)
            World.OfferChips(state);
    }

    private static bool _scriptLoaded;

    /// <summary>The key press that opens the hangar, as the keyboard sends it.</summary>
    private void PressHangarKey()
    {
        var key = KeyMap.Of(KeyMap.Act.Hangar);
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
    }

    private async Task Seconds(double seconds) => await ToSignal(GetTree().CreateTimer(seconds), SceneTreeTimer.SignalName.Timeout);

    /// <summary>What a script needs to know of the run at one moment, the checksum first.</summary>
    public string RunReport
    {
        get
        {
            var run = World.Wired!;
            var state = run.Heroes[Player];
            var hero = World.HeroOf(Player);
            var pod = World.WiredPod(Player);
            return $"tick {World.Tick}, checksum {World.Checksum():x16}, {run.Phase}, wave {run.Wave}, countdown {run.Countdown:0.0}, bank {World.Races[Player].Bmu:0}, " +
                   $"worn {state.Loadout.Chassis.Catalogue} [{string.Join(", ", state.Loadout.Worn.Skip(1).Select(i => $"{i.Catalogue} Mk{i.Mk}"))}], " +
                   $"spares [{string.Join(", ", state.Inventory.Select(i => i.Catalogue))}], offer [{string.Join(", ", state.Offer.Select(o => o ?? "-"))}], " +
                   $"squad {World.Escorts(Player).Count()}, pod {pod?.HitPoints}/{pod?.MaxHitPoints}, hero {hero?.HitPoints}/{hero?.MaxHitPoints} refits {hero?.Refits} inside {hero?.Inside?.ToString() ?? "no"}, " +
                   $"hangar {state.HangarOpen}, driven {_driven?.Id.ToString() ?? "no"}, mouse {Input.MouseMode}, foes {World.Machines.Count(m => m.Race == run.Enemy)}";
        }
    }

    /// <summary>
    /// The hero walks to the pod's door as the order for that sends it. With <paramref name="driven"/> the player gets into it
    /// there and drives it over the threshold, by keys as they are pressed, which is what leaves it inside in first person;
    /// otherwise it goes on to a bay, under the camera it had. Then the key opens the hangar.
    /// </summary>
    private async Task IntoThePod(bool driven)
    {
        var (hero, pod) = (World.HeroOf(Player)!, World.WiredPod(Player)!);
        Select([hero.Id]);
        Issue(new EnterGarrisonCommand(Player, [hero.Id], pod.Id));
        while (driven ? hero.Doorway != pod.Id : hero.Inside is null)
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (driven)
        {
            EnterFirstPerson();
            await Seconds(0.3);
            await DriveOverThreshold(hero, pod);
        }
        await Seconds(1);
        GD.Print($"hangar: in the pod, {(driven ? "first person" : "in a bay")}: {RunReport}");
        PressHangarKey();
    }

    /// <summary>Turns the hero at the point inside the door and drives there with the arrows and the key for forward, until it is in.</summary>
    private async Task DriveOverThreshold(Machine hero, Construction pod)
    {
        var (held, forward) = (new HashSet<Key>(), KeyMap.Of(KeyMap.Act.Forward));
        void Hold(Key key, bool down)
        {
            if (down ? held.Add(key) : held.Remove(key))
                Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = down });
        }
        for (var frame = 0; frame < 1500 && hero.Inside != pod.Id; frame++)
        {
            var way = pod.EntranceInside!.Value - hero.Position;
            var off = Mathf.Wrap(MathF.Atan2(way.Y, way.X) - hero.Facing, -Mathf.Pi, Mathf.Pi);
            Hold(Key.Right, off > 0.08f);
            Hold(Key.Left, off < -0.08f);
            Hold(forward, MathF.Abs(off) < 0.4f);
            await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        }
        foreach (var key in held.ToList())
            Hold(key, false);
    }

    /// <summary>That the hero drives on after the hangar, whatever became of it in there: a second on the key for forward, seen in the world.</summary>
    private async Task StillDrives()
    {
        if (_driven is not { } hero)
            return;
        var (from, key) = (hero.Position, KeyMap.Of(KeyMap.Act.Forward));
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true });
        await Seconds(1);
        Input.ParseInputEvent(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false });
        await Seconds(0.3);
        GD.Print($"hangar: a second on forward moved the hero {System.Numerics.Vector2.Distance(from, hero.Position):0.0} m, inside {hero.Inside?.ToString() ?? "no"}");
    }

    /// <summary>The key where it does not open the hangar: what the world says, in the player's language, and the message line's picture.</summary>
    private async Task KeyRefused(string folder, string picture)
    {
        var why = World.WhyNoHangar(World.Wired!.Heroes[Player]);
        PressHangarKey();
        await Seconds(0.4);
        GD.Print($"hangar: the key says '{(why is null ? "nothing" : HangarText.Reason(why))}', open {HangarOpen}");
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, picture + ".png"));
    }

    /// <summary>The hero is sent away from the pod until the key no longer opens the hangar.</summary>
    private async void ScriptTooFar(string folder)
    {
        var (hero, pod) = (World.HeroOf(Player)!, World.WiredPod(Player)!);
        var away = hero.Position + System.Numerics.Vector2.Normalize(hero.Position - pod.Position) * 90;
        Issue(new MoveCommand(Player, [hero.Id], away));
        for (var second = 0; second < 40 && hero.DistanceTo(pod) <= WiredBalance.PodReach + 2; second++)
            await Seconds(1);
        GD.Print($"hangar: away from the pod: {RunReport}");
        await KeyRefused(folder, "run-keyfar");
        GetTree().Quit();
    }

    /// <summary>A save of the run comes back: where it stands, and that it goes on.</summary>
    private async void ScriptLoaded()
    {
        GD.Print($"hangar: loaded: {RunReport}");
        await Seconds(1);
        GD.Print($"hangar: a second on: {RunReport}");
        await Seconds(6);
        GD.Print($"hangar: seven seconds on: {RunReport}");
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(System.Environment.GetEnvironmentVariable("MACHINES_HANGAR_SCRIPT")!.Split(',')[0], "run-loaded.png"));
        GetTree().Quit();
    }

    /// <summary>The script's end, here as the screen is gone: Ready has been pressed. It waits for the wave, saves the run and loads it.</summary>
    public async void WatchTheWave(string folder)
    {
        var (run, ticks) = (World.Wired!, new List<long>());
        GD.Print($"hangar: step Ready pressed: {RunReport}");
        for (var second = 1; second <= 90 && World.Machines.Count(m => m.Race == run.Enemy) == 0; second++)
            await Seconds(1);
        GD.Print($"hangar: step the wave has come: {RunReport}");
        await Seconds(4);
        GD.Print($"hangar: step four seconds into it: {RunReport}");
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, "run-wave.png"));
        await KeyRefused(folder, "run-keywave");
        await StillDrives();
        if (System.Environment.GetEnvironmentVariable("MACHINES_HANGAR_SAVE") is not { Length: > 0 } name)
        {
            GetTree().Quit();
            return;
        }
        var why = SaveGame(name);
        GD.Print($"hangar: step saved '{name}' ({why ?? "fine"}): {RunReport}");
        _scriptLoaded = true;
        // The way to loading leads through the menu, and that out of first person.
        LeaveFirstPerson();
        why = LoadGame(System.IO.Path.Combine(SaveFolder, name + SaveExtension));
        GD.Print($"hangar: loading: {why ?? "fine"}");
    }

    /// <summary>
    /// For automated checks (see <see cref="HangarScript"/>): a run started from the hangar says what it holds, and with
    /// MACHINES_HANGAR_SCRIPT=&lt;folder&gt;,full the screen is opened over it and worked with the mouse too. With <c>shop</c> the
    /// run's own way there is taken, into the pod and by the key; with <c>view</c> its key on the ground.
    /// </summary>
    private async void ScriptHangar()
    {
        if (_hangarScripted || World.Wired is not { } run || System.Environment.GetEnvironmentVariable("MACHINES_HANGAR_SCRIPT") is not { Length: > 0 } script)
            return;
        _hangarScripted = true;
        if (_scriptLoaded)
        {
            ScriptLoaded();
            return;
        }
        var words = script.Split(',');
        var state = run.Heroes[Player];
        if (Watching || words.Length > 1 && words[1] == "start")
            return;
        if (words.Length > 1 && words[1] is "shop" or "bay" or "view" or "far")
        {
            GD.Print($"hangar: the run: {RunReport}");
            await Seconds(3);
            if (words[1] == "far")
                ScriptTooFar(words[0]);
            else if (words[1] == "view")
                PressHangarKey();
            else
                await IntoThePod(driven: words[1] == "shop");
            return;
        }
        var options = run.Options;
        GD.Print($"hangar: the run: hero '{options.HeroName}', arena {options.Arena}, {options.Difficulty}, {state.Loadout.Chassis.Catalogue} " +
                 $"[{string.Join(", ", state.Loadout.Worn.Skip(1).Select(i => i.Catalogue))}], spares [{string.Join(", ", state.Inventory.Select(i => i.Catalogue))}], " +
                 $"bank {World.Races[Player].Bmu}, colour {Look(Player)}, chip pool {options.ChipPool.Count}, waiting {run.Waiting}");
        await ToSignal(GetTree().CreateTimer(3), SceneTreeTimer.SignalName.Timeout);
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(words[0], "run-started.png"));
        OpenHangar();
        await ToSignal(GetTree().CreateTimer(0.5), SceneTreeTimer.SignalName.Timeout);
        foreach (var refused in World.Rejected)
            GD.Print($"hangar: opening refused: {refused.Reason}");
        GD.Print($"hangar: open {run.Heroes[Player].HangarOpen}: {RunState}");
    }

    /// <summary>The script's last step, here because the screen goes with the hangar: back to the run, and after a look at it out of the game.</summary>
    public async void EndHangarScript(string folder)
    {
        GD.Print($"hangar: before closing: {RunState}");
        CloseHangar();
        await ToSignal(GetTree().CreateTimer(3), SceneTreeTimer.SignalName.Timeout);
        GD.Print($"hangar: after closing: {RunState}");
        GetViewport().GetTexture().GetImage().SavePng(System.IO.Path.Combine(folder, "run-closed.png"));
        GetTree().Quit();
    }

    private string RunState => $"{World.Wired!.Phase}, wave {World.Wired.Wave}, countdown {World.Wired.Countdown:0.0}, tick {World.Tick}, machines " +
                               string.Join(" ", World.Machines.GroupBy(m => m.Race).Select(g => $"{g.Key}:{g.Count()}"));
}
