using Godot;
using Machines.Import.Text;

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
/// multiplayer, load, replays, options, graphics, keys, models, campaign, missions, briefing or quit), at that size if one
/// is given, and quits. The first start's questions (setup, setup-import, setup-look, setup-music) come before the main menu until
/// they are answered (<see cref="SetupPage"/>).
/// </summary>
public partial class MenuScreens : Control
{
    private ClassicAssets _assets = null!;
    private StringTable? _strings;
    private Control _host = null!;
    private MenuStage _stage = null!;
    private SubViewport? _shot;
    private string? _shotPath;
    private double _shotTime;

    public ClassicAssets Assets => _assets;

    /// <summary>The page the menus open on next time instead of the main menu: after a campaign mission, its missions.</summary>
    public static string? StartPage { get; set; }

    private static string? Setting(string name) => System.Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var shot = (Setting("MACHINES_MENU_SCREENSHOT") ?? "").Split(',', StringSplitOptions.TrimEntries);
        if (shot[0].Length == 0 && Setting("MACHINES_MP") is null && Setting("MACHINES_MENU_START") is null && (Setting("MACHINES_SCREENSHOT") ?? Setting("MACHINES_MAP")) is not null)
        {
            // Automated checks of a match skip the menus; the match reads them from the environment.
            GetTree().CallDeferred(SceneTree.MethodName.ChangeSceneToFile, "res://scenes/Main.tscn");
            return;
        }
        // For automated checks: MACHINES_IMPORT=<game folder> imports from that copy before the menus open.
        if (Setting("MACHINES_IMPORT") is { } importFrom && ClassicImport.Available)
            GD.Print(ClassicImport.Run(importFrom, line => GD.Print(line)) ?? "import finished");
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
        }

        _stage = new MenuStage();
        _host.AddChild(_stage);
        MenuSounds.Attach(_stage, _assets.GameFolder);
        var page = shot.Length > 1 ? shot[1] : Setting("MACHINES_MP") is not null ? "multiplayer" : Setting("MACHINES_MENU_START") is not null ? "skirmish" : StartPage ?? "main";
        StartPage = null;
        // The first time, the original's loading and intro films play before the main menu (not in automated runs).
        if (page == "main" && shot[0].Length == 0 && StartupFilms.Create(_assets.GameFolder, () => Begin(page)) is { } films)
            _host.AddChild(films);
        else
            Begin(page);
    }

    private void Begin(string page)
    {
        Music.Menu(this);
        // The first start asks its few questions before the main menu.
        Open(page == "main" && _shotPath is null && !SetupPage.Done ? "setup" : page);
    }

    public override void _Process(double delta)
    {
        if (_shotPath is null)
            return;
        _shotTime += delta;
        if (_shotTime < 3)
            return;
        (_shot?.GetTexture() ?? GetViewport().GetTexture()).GetImage().SavePng(_shotPath);
        GetTree().Quit();
    }

    /// <summary>Opens a page by its name (see MACHINES_MENU_SCREENSHOT).</summary>
    private void Open(string page)
    {
        switch (page)
        {
            case "skirmish": ShowSkirmish(); break;
            case "room": ShowSkirmish(forRoom: true); break;
            case "multiplayer": ShowMultiplayer(); break;
            case "load": ShowLoad(); break;
            case "replays": ShowReplays(); break;
            case "options": ShowOptions(); break;
            case "graphics": _stage.Show(new GraphicsPage(ShowOptions, inGame: false)); break;
            case "keys": _stage.Show(new KeysPage(ShowOptions)); break;
            case "models": ShowModels(); break;
            case "setup": ShowSetup(SetupPage.First); break;
            case "setup-import": ShowSetup(SetupPage.Step.Import); break;
            case "setup-look": ShowSetup(SetupPage.Step.Look); break;
            case "setup-music": ShowSetup(SetupPage.Step.Music); break;
            case "campaign": ShowCampaign(); break;
            case "missions": ShowMissions(); break;
            case "briefing": ShowBriefing(Campaign.Entry(CampaignProgress.Load().CurrentPlayer?.LastScenario ?? "tut01") ?? Campaign.Catalogue.CampaignScenarios.First()); break;
            case "quit":
                ShowMain();
                Quit();
                break;
            default: ShowMain(); break;
        }
    }

    public void ShowMain() => _stage.Show(new MainMenuPage(this));

    /// <summary>A question of the first start (see <see cref="SetupPage"/>).</summary>
    public void ShowSetup(SetupPage.Step step) => _stage.Show(new SetupPage(this, step));

    /// <summary>The skirmish setup, or with <paramref name="forRoom"/> the setup of a multiplayer room to open.</summary>
    public void ShowSkirmish(bool forRoom = false) => _stage.Show(new SkirmishPage(this, forRoom));

    public void ShowMultiplayer() => _stage.Show(new MultiplayerPage(this));

    public void ShowLoad() => _stage.Show(FilesPage.Load(ShowMain, path => FilesPage.OpenSaved(GetTree(), path)));

    public void ShowReplays() => _stage.Show(FilesPage.Replays(ShowMain, path => FilesPage.OpenReplay(GetTree(), path)));

    public void ShowOptions() => _stage.Show(new OptionsPage(ShowMain, inGame: false));

    public void ShowModels() => _stage.Show(new ModelsPage(ShowMain));

    /// <summary>The campaign's players.</summary>
    public void ShowCampaign() => _stage.Show(new CampaignPlayersPage(this));

    /// <summary>The choice of mission for the campaign's current player, or the players when there is none.</summary>
    public void ShowMissions()
    {
        if (CampaignProgress.Load().CurrentPlayer is null)
            ShowCampaign();
        else
            _stage.Show(new CampaignMissionsPage(this));
    }

    public void ShowBriefing(Machines.Import.Model.CatalogueScenario mission) => _stage.Show(new CampaignBriefingPage(this, mission));

    public void Message(string text, params (string Text, Action? Act)[] buttons) => _stage.Message(text, buttons);

    public void Quit() => _stage.Message("Leave Machines?", ("Quit", () => GetTree().Quit()), ("Stay", null));

    /// <summary>A text of the original's string table (machstrg.xml in the game copy), or the fallback without it.</summary>
    public string Text(int id, string fallback) =>
        _strings is not null && _strings.Strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;
}
