using Godot;
using Machines.Import.Text;
using Machines.Net;

namespace Machines.Game;

/// <summary>
/// The menus before a match (Menu.tscn), in the original's look: the main menu and the pages it leads to, skirmish,
/// multiplayer, saved games, replays, the model viewer and options, on a <see cref="MenuStage"/>.
///
/// Automated runs skip them: MACHINES_MAP and MACHINES_SCREENSHOT start the match straight away, MACHINES_IMPORT
/// imports from a game copy first, MACHINES_HD makes the HD textures and quits, MACHINES_MP opens the multiplayer page
/// and drives the lobby (see <see cref="MultiplayerPage"/>), MACHINES_MENU_SELECT=&lt;scenario&gt;,&lt;planet&gt; picks a
/// map and MACHINES_MENU_START=1 starts it from the skirmish page (MACHINES_LOADING_SCREENSHOT, see
/// <see cref="LoadingScreen"/>). MACHINES_MENU_SCREENSHOT=&lt;png&gt;[,page[,WxH]] saves a page (main, skirmish, room,
/// room-battle, multiplayer, load, replays, options, graphics, keys, keys-run, keys-story, models, pod, pod-path, pod-modules, pod-chassis, pod-room,
/// campaign, missions, briefing, story or quit), at that size if one
/// is given, and quits. The first start's questions (setup, setup-import, setup-look, setup-music) come before the main menu until
/// they are answered (<see cref="SetupPage"/>). MACHINES_MENU_GO=&lt;page&gt;[,seconds] skips the first films and goes from the main
/// menu to that page as a click would (after that many seconds), with the transition (main stays there, to wait for the logos).
/// MACHINES_WIRED_DRIVE plays a whole Wired run from the main menu on (see <see cref="WiredDriver"/>), MACHINES_BATTLE_DRIVE a
/// whole battle (see <see cref="BattleDriver"/>).
/// </summary>
public partial class MenuScreens : Control
{
    // MACHINES_MENU_GO goes to its page once: later visits to the menus are where the game sends them.
    private static bool _wentAhead;

    /// <summary>How long the main menu stays before the logos and the intro come back (MachGuiStartupScreens::updateContext).</summary>
    private const double IdleSeconds = 300;

    private ClassicAssets _assets = null!;
    private StringTable? _strings;
    private Control _host = null!;
    private MenuStage _stage = null!;
    private SubViewport? _shot;
    private string? _shotPath;
    private double _shotTime;
    private string _screen = MenuTransition.Main;
    private bool _begun, _idleFilms;
    private double _idle;

    public ClassicAssets Assets => _assets;

    /// <summary>The page the menus open on next time instead of the main menu: after a match, where it was set up.</summary>
    public static string? StartPage { get; set; }

    private static string? Setting(string name) => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Lang.Apply();
        // For automated checks: MACHINES_WIRED_DRIVE plays a whole run from here (see WiredDriver).
        WiredDriver.Attend(GetTree());
        BattleDriver.Attend(GetTree());
        var shot = (Setting("MACHINES_MENU_SCREENSHOT") ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (shot[0].Length == 0 && Setting("MACHINES_MP") is null && Setting("MACHINES_MENU_START") is null && (Setting("MACHINES_SCREENSHOT") ?? Setting("MACHINES_MAP") ?? Setting("MACHINES_BATTLE")) is not null)
        {
            // Automated checks of a match skip the menus; the match reads them from the environment.
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Main.tscn");
            return;
        }
        // For automated checks: MACHINES_IMPORT=<game folder, CD drive or disc image> imports from it before the menus open.
        if (Setting("MACHINES_IMPORT") is { } importFrom && ClassicImport.Available)
            GD.Print(ClassicImport.FromSource(importFrom, line => GD.Print(line)) ?? "import finished");
        // MACHINES_HD=<game folder> makes only the HD textures (see HdTextures) and quits.
        if (Setting("MACHINES_HD") is { } hdFrom && ClassicImport.Imported)
        {
            GD.Print(HdTextures.Run(hdFrom, line => GD.Print(line)) ?? "HD textures finished");
            GetTree().Quit();
            return;
        }
        // MACHINES_MINIMAPS=1 (or all) takes the remake's maps of the scenarios (see MiniMaps) and quits.
        if (Setting("MACHINES_MINIMAPS") is { } minimaps)
        {
            MiniMaps.TakeAll(GetTree(), again: minimaps == "all");
            return;
        }
        // MACHINES_HERO_SHOT=<png>,<what> photographs Wired heroes (see HeroStudio) and quits.
        if (Setting("MACHINES_HERO_SHOT")?.Split(',', StringSplitOptions.TrimEntries) is [var heroShot, .. var heroes])
        {
            AddChild(new HeroStudio(heroShot, heroes));
            return;
        }

        _assets = new ClassicAssets(Paths.Root);
        var stringsPath = System.IO.Path.Combine(Paths.OriginalData, "machstrg.xml");
        _strings = File.Exists(stringsPath) ? StringTableParser.Load(stringsPath) : null;
        _host = this;
        if (shot[0].Length > 0)
        {
            _shotPath = shot[0];
            if (shot.Length > 2 && shot[2].Split('x') is [var w, var h])
            {
                // A window of any size, without the screen it would need: the page renders into a picture that big.
                var size = new Vector2I(int.Parse(w), int.Parse(h));
                _shot = new SubViewport { Size = size, RenderTargetUpdateMode = SubViewport.UpdateMode.Always };
                AddChild(_shot);
                _host = new Control { Size = size };
                _shot.AddChild(_host);
            }
        }
        else if (Setting("MACHINES_MP") is null)
        {
            OptionsPage.ApplyWindow();
            // The next start's splash: the loading screen's first frame, from this game copy.
            BootSplash.Update(_assets.GameFolder);
        }

        _stage = new MenuStage();
        _host.AddChild(_stage);
        MenuSounds.Attach(_stage, _assets.GameFolder);
        var page = shot.Length > 1 ? shot[1] : Setting("MACHINES_MP") is not null ? "multiplayer" : Setting("MACHINES_MENU_START") is not null ? "skirmish" : StartPage ?? "main";
        StartPage = null;
        // The first time, the original's loading and intro films play before the main menu (not in automated runs).
        // The main menu's machines load and are drawn once where nobody sees them, the first time: under the loading picture.
        var warmup = shot[0].Length == 0 ? MachineWarmup.Begin(_assets) : null;
        if (warmup is not null)
            _host.AddChild(warmup);
        if (page == "main" && shot[0].Length == 0 && Setting("MACHINES_MENU_GO") is null
            && StartupFilms.Create(_assets.GameFolder, () => Begin(page), () => warmup is null || warmup.Done ? 1 : warmup.Progress) is { } films)
            _host.AddChild(films);
        else
            Begin(page);
    }

    private void Begin(string page)
    {
        Music.Menu(this);
        // The first start asks its few questions before the main menu.
        Open(page == "main" && _shotPath is null && !SetupPage.Done ? "setup" : page);
        _begun = true;
        if (!_wentAhead && Setting("MACHINES_MENU_GO")?.Split(',', StringSplitOptions.TrimEntries) is [var go, .. var after])
        {
            _wentAhead = true;
            if (after is [var seconds] && double.TryParse(seconds, System.Globalization.CultureInfo.InvariantCulture, out var wait))
                GetTree().CreateTimer(wait).Timeout += () => Open(go);
            else
                Callable.From(() => Open(go)).CallDeferred();
        }
    }

    public override void _Process(double delta)
    {
        if (_shotPath is null)
        {
            WatchIdle(delta);
            return;
        }
        _shotTime += delta;
        if (_shotTime < 3)
            return;
        // Freed here, and what the garbage collector still holds finalized while the engine is there: finalized after
        // it is gone, a Godot object crashes the exit.
        using (var picture = (_shot?.GetTexture() ?? GetViewport().GetTexture()).GetImage())
            picture.SavePng(_shotPath);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GetTree().Quit();
    }

    /// <summary>
    /// The main menu left for five minutes goes back to the logos and the intro, then returns. As in the original the
    /// time runs from coming to the screen: neither the mouse nor the keys put it off.
    /// </summary>
    private void WatchIdle(double delta)
    {
        if (_idleFilms)
            return;
        _idle = _stage?.Page is MainMenuPage && !MenuTransition.Playing ? _idle + delta : 0;
        if (_idle < IdleSeconds || StartupFilms.Logos(_assets.GameFolder, EndIdleFilms) is not { } films)
            return;
        _idleFilms = true;
        GetViewport().GuiReleaseFocus();
        _host.AddChild(films);
    }

    private void EndIdleFilms()
    {
        (_idleFilms, _idle) = (false, 0);
        Music.Menu(this);
        ShowMain();
    }

    /// <summary>Moves to a page, with the transition from the screen before (see <see cref="MenuTransition"/>).</summary>
    private void Go(string screen, Func<Control> page)
    {
        if (MenuTransition.Playing)
            return;
        var from = _begun ? _screen : screen;
        _screen = screen;
        MenuTransition.Between(_stage, from, screen, () =>
        {
            if (IsInstanceValid(this))
                _stage.Show(page());
        });
    }

    /// <summary>Which of the original's screens the page on show now stands for: the multiplayer page is both the rooms and a room.</summary>
    public void At(string screen) => _screen = screen;

    /// <summary>Opens a page by its name (see MACHINES_MENU_SCREENSHOT).</summary>
    private void Open(string page)
    {
        switch (page)
        {
            case "skirmish": ShowSkirmish(); break;
            case "room": ShowSkirmish(forRoom: true); break;
            case "room-battle":
                SkirmishPage.ChooseBattle();
                ShowSkirmish(forRoom: true);
                break;
            case "pod-room": ShowPod(BattlePod.SampleRoom()); break;
            case "multiplayer": ShowMultiplayer(); break;
            case "load": ShowLoad(); break;
            case "replays": ShowReplays(); break;
            case "options": ShowOptions(); break;
            case "graphics": _stage.Show(new GraphicsPage(ShowOptions, inGame: false)); break;
            case "keys": _stage.Show(new KeysPage(ShowOptions)); break;
            case "keys-run": _stage.Show(new KeysPage(ShowOptions, run: true)); break;
            case "keys-story": _stage.Show(new KeysPage(ShowOptions, story: true)); break;
            case "models": ShowModels(); break;
            case "hangar": ShowHangar(); break;
            case "pod": ShowPod(); break;
            case "pod-path": ShowPod(HangarShelf.Tree); break;
            case "pod-modules": ShowPod(HangarShelf.Modules); break;
            case "pod-chassis": ShowPod(HangarShelf.Chassis); break;
            case "setup": ShowSetup(SetupPage.First); break;
            case "setup-import": ShowSetup(SetupPage.Step.Import); break;
            case "setup-look": ShowSetup(SetupPage.Step.Look); break;
            case "setup-music": ShowSetup(SetupPage.Step.Music); break;
            case "campaign": ShowCampaign(); break;
            case "story": ShowStory(); break;
            case "missions": ShowMissions(); break;
            case "briefing": ShowBriefing(Campaign.Entry(CampaignProgress.Load().CurrentPlayer?.LastScenario ?? "tut01") ?? Campaign.Catalogue.CampaignScenarios.First()); break;
            case "quit":
                ShowMain();
                Quit();
                break;
            default: ShowMain(); break;
        }
    }

    public void ShowMain() => Go(MenuTransition.Main, () => new MainMenuPage(this));

    /// <summary>A question of the first start (see <see cref="SetupPage"/>).</summary>
    public void ShowSetup(SetupPage.Step step) => _stage.Show(new SetupPage(this, step));

    /// <summary>
    /// The skirmish setup, or with <paramref name="forRoom"/> the setup of a multiplayer room to open, or of the
    /// <paramref name="room"/> the host is in.
    /// </summary>
    public void ShowSkirmish(bool forRoom = false, RoomState? room = null) =>
        Go(room is not null ? MenuTransition.Settings : forRoom ? MenuTransition.Join : MenuTransition.Skirmish, () => new SkirmishPage(this, forRoom, room));

    public void ShowMultiplayer() => Go(MenuTransition.Multiplayer, () => new MultiplayerPage(this));

    /// <summary>The multiplayer page, whose Back goes to <paramref name="back"/> instead of the main menu (the pod, for one).</summary>
    public void ShowMultiplayer(Action back) => Go(MenuTransition.Multiplayer, () => new MultiplayerPage(this, back));

    /// <summary>The multiplayer page, on its way back to the room the player is in.</summary>
    public void ShowRoom() => Go(MenuTransition.Room, () => new MultiplayerPage(this));

    public void ShowLoad() => Go(MenuTransition.Load, () => FilesPage.Load(ShowMain, path => FilesPage.OpenSaved(GetTree(), path)));

    public void ShowReplays() => _stage.Show(FilesPage.Replays(ShowMain, path => FilesPage.OpenReplay(GetTree(), path)));

    public void ShowOptions() => Go(MenuTransition.Options, () => new OptionsPage(ShowMain, inGame: false));

    public void ShowModels() => _stage.Show(new ModelsPage(ShowMain));

    /// <summary>Wired: hero creation, in the hangar.</summary>
    public void ShowHangar() => _stage.Show(HangarCreation.Page(this));

    /// <summary>Battle: the pod, where the hero is put together and research and purchases are made.</summary>
    public void ShowPod() => ShowPod(HangarShelf.Weapons);

    /// <summary>The pod, on a shelf.</summary>
    public void ShowPod(HangarShelf shelf) => _stage.Show(BattlePod.Page(this, shelf));

    /// <summary>The pod for a battle room: it goes back to the room, and its way into the battle is Ready.</summary>
    public void ShowPod(RoomState room) => _stage.Show(BattlePod.Page(this, HangarShelf.Weapons, room));

    /// <summary>The campaign's players.</summary>
    /// <summary>The story: its chapters, the checkpoint reached, the Grunts' names (<see cref="StoryPage"/>).</summary>
    public void ShowStory() => Go(MenuTransition.SinglePlayer, () => new StoryPage(this));

    public void ShowCampaign() => Go(MenuTransition.CampaignPlayers, () => new CampaignPlayersPage(this));

    /// <summary>The choice of mission for the campaign's current player, or the players when there is none.</summary>
    public void ShowMissions()
    {
        if (CampaignProgress.Load().CurrentPlayer is null)
            ShowCampaign();
        else
            Go(MenuTransition.Scenario, () => new CampaignMissionsPage(this));
    }

    public void ShowBriefing(Machines.Import.Model.CatalogueScenario mission) => Go(MenuTransition.Briefing, () => new CampaignBriefingPage(this, mission));

    public void Message(string text, params (string Text, Action? Act)[] buttons) => _stage.Message(text, buttons);

    public void Quit() => _stage.Message("Leave Machines?", ("Quit", () => GetTree().Quit()), ("Stay", null));

    /// <summary>A text of the original's string table (machstrg.xml in the game copy), or the fallback without it.</summary>
    public string Text(int id, string fallback) =>
        Lang.Original(_strings is not null && _strings.Strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback, fallback);
}
