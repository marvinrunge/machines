using Godot;
using Machines.Import.Model;
using Machines.Import.Text;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The original's menus over a running match (MachGuiCtxInGameOptions and the screens it leads to), on the same
/// board as the menus before it (<see cref="MenuStage"/>): Continue goes back to the match; Briefing shows the
/// scenario's text; Restart starts it again; Hot Keys lists the keys; Options are the main menu's; Save Game and Load
/// Game keep and fetch matches; Exit Game leaves for the main menu. After a match its statistics come up here, after
/// a campaign mission its debriefing; a campaign mission's briefing has its tasks and hints.
/// The arrow keys move between buttons, Enter presses one, Esc goes back; no key reaches the match behind.
/// </summary>
public partial class InGameMenu(Game game, bool statistics = false, bool debriefing = false) : Control
{
    private MenuStage _stage = null!;
    private StringTable? _strings;
    private ClassicAssets _assets = null!;

    public override void _Ready()
    {
        MouseFilter = MouseFilterEnum.Stop;
        var path = System.IO.Path.Combine(Paths.OriginalData, "machstrg.xml");
        _strings = File.Exists(path) ? StringTableParser.Load(path) : null;
        _stage = new MenuStage();
        AddChild(_stage);
        _assets = new ClassicAssets(Paths.Root);
        MenuSounds.Attach(_stage, _assets.GameFolder);
        if (debriefing)
            ShowDebriefing();
        else if (statistics)
            ShowStatistics();
        else
            ShowMain();
    }

    /// <summary>The keys reach the menus first, and nothing behind them.</summary>
    public override void _UnhandledInput(InputEvent @event)
    {
        if (@event is InputEventKey)
            GetViewport().SetInputAsHandled();
    }

    private string Text(int id, string fallback) =>
        _strings is not null && _strings.Strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;

    private ChipButton Button(int id, string fallback, Action pressed, bool enabled = true)
    {
        var button = MenuPage.Button(Text(id, fallback), pressed, 84, 30);
        button.CustomMinimumSize = new Vector2(440, 84);
        button.Disabled = !enabled;
        return button;
    }

    private static ChipButton Footer(string text, Action pressed)
    {
        var button = MenuPage.Button(text, pressed);
        button.CustomMinimumSize = new Vector2(400, 76);
        return button;
    }

    // ---- the pages (ctxingam, ctxbrief, ctxhotky, ctxoptns, ctxsave, ctxload, ctxstats) -------------

    /// <summary>The menu's own page: its eight buttons in two columns, as in the original, and a note under them.</summary>
    private void ShowMain(string? note = null) => _stage.Show(new BuiltPage(game.CloseMenu, page =>
    {
        var column = MenuPage.Column(MenuPage.Gap);
        page.AddChild(column);
        var map = MenuTheme.Label(LoadingScreen.MapName(MatchRequest.Current), 22, MenuTheme.Dim);
        map.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        column.AddChild(MenuPage.Line(MenuPage.Gap, MenuPage.Heading("Menu"), MenuPage.Spring(), map));

        var centre = new CenterContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        var grid = new GridContainer { Columns = 2 };
        grid.AddThemeConstantOverride("h_separation", 64);
        grid.AddThemeConstantOverride("v_separation", 34);
        var resume = Button(7312, "Continue", game.CloseMenu);
        // In the original's order: down the left, then down the right.
        foreach (var button in new[]
        {
            resume,
            Button(7321, "Options", ShowOptions),
            Button(7304, "Briefing", ShowBriefing),
            Button(7307, "Save Game", ShowSave, game.CanSave),
            Button(7305, "Restart", () => _stage.Message("Start this match again from the beginning?",
                ("Restart", () => LoadingScreen.Show(game.GetTree(), LoadingScreen.MapName(MatchRequest.Current), "RESTART", game.Restart)), ("Cancel", null)), game.CanSave),
            Button(7310, "Load Game", ShowLoad, !game.Online),
            Button(7306, "Hot Keys", ShowHotkeys),
            Button(7303, "Exit Game", () => _stage.Message(
                game.Online ? "Leave the match for the main menu?" : "Leave the match for the main menu? What is not saved is lost.",
                ("Leave", game.ExitToMenus), ("Stay", null))),
        })
            grid.AddChild(button);
        centre.AddChild(grid);
        column.AddChild(centre);

        if (note is not null)
        {
            var chip = new ChipPanel { Padding = 14, SizeFlagsHorizontal = SizeFlags.ShrinkBegin };
            chip.AddChild(MenuTheme.Label(note, 22, MenuTheme.Text));
            column.AddChild(chip);
        }
        MenuPage.Focus(resume);
    }));

    public void ShowBriefing()
    {
        if (Mission() is { } mission && _assets.ScenarioTexts(mission.Text) is { } texts)
            ShowMissionBriefing(mission, texts, hints: false);
        else
            ShowScenarioBriefing();
    }

    /// <summary>The campaign mission being played, when it is one.</summary>
    private static CatalogueScenario? Mission() =>
        Campaign.IsCampaign(MatchRequest.Current) ? Campaign.Entry(MatchRequest.Current.Scenario) : null;

    /// <summary>
    /// A campaign mission's briefing over the match (MachGuiCtxBriefing): the objective with the tasks the script has
    /// made known, those done marked, or once the mission has been played before its hints.
    /// </summary>
    private void ShowMissionBriefing(CatalogueScenario mission, ScenarioText texts, bool hints) => _stage.Show(new BuiltPage(() => ShowMain(), page =>
    {
        var chip = new ChipPanel { SizeFlagsVertical = SizeFlags.ExpandFill, Padding = 30 };
        var view = Campaign.TextView(25);
        view.Text = Campaign.Rich(hints ? texts.Hint : Campaign.Objectives(texts, game.World.Script, Text));
        chip.AddChild(view);
        var played = CampaignProgress.Load().CurrentPlayer?.Missions.GetValueOrDefault(mission.Name) is { Played: > 0 };
        var toggle = Footer(hints ? Text(7316, "Objectives") : Text(7315, "Hints"), () => ShowMissionBriefing(mission, texts, !hints));
        toggle.Disabled = !hints && (!played || texts.Hint.Length == 0);
        var ok = Footer(Text(7302, "OK"), game.CloseMenu);
        page.AddChild(MenuPage.Column(MenuPage.Gap, MenuPage.Heading(Text(7304, "Briefing")), chip,
            MenuPage.Line(MenuPage.Gap, Footer(Text(7311, "Cancel"), () => ShowMain()), toggle, MenuPage.Spring(), ok)));
        MenuPage.Focus(ok);
    }));

    /// <summary>
    /// The debriefing after a campaign mission (MachGuiCtxDeBriefing): the mission's picture of its victory or defeat,
    /// its closing text, spoken, with the tasks done and failed; then Restart, the statistics, or Continue to the
    /// choice of mission.
    /// </summary>
    private void ShowDebriefing() => _stage.Show(new BuiltPage(null, page =>
    {
        var mission = Mission();
        var texts = _assets.ScenarioTexts(mission?.Text);
        var won = game.World.Winner == Game.Player;
        var picture = new ChipPanel { Padding = 10, CustomMinimumSize = new Vector2(470, 414) };
        // Without a picture of its own, the original's of the player's colour (vicr.bmp, defb.bmp and so on).
        var file = Campaign.Media(_assets.GameFolder, mission?.Media.GetValueOrDefault(won ? "DEBRIEFING" : "DEBRIEFINGLOSE"))
                   ?? Campaign.Media(_assets.GameFolder, $"flics/gui/v&d/{(won ? "vic" : "def")}{Game.Player.ToString()[0]}.bmp");
        if (file is not null && ClassicAssets.DecodeBmp(File.ReadAllBytes(file)) is { } image)
            picture.AddChild(new TextureRect
            {
                Texture = ImageTexture.CreateFromImage(image),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            });
        var restart = Footer(Text(7305, "Restart"), () =>
            LoadingScreen.Show(game.GetTree(), LoadingScreen.MapName(MatchRequest.Current), "RESTART", game.Restart));
        var go = Footer(Text(7312, "Continue"), game.ExitToMenus);
        var left = MenuPage.Column(26, picture, restart, Footer(Text(7317, "Statistics"), ShowStatistics), MenuPage.Spring(across: false), go);
        left.CustomMinimumSize = new Vector2(470, 0);

        var chip = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 30 };
        var view = Campaign.TextView(25);
        view.Text = texts is null ? "" : Campaign.Rich((won ? texts.WinText : texts.LoseText) + Campaign.Tasks(texts, game.World.Script, ended: true, Text));
        chip.AddChild(view);
        var columns = MenuPage.Line(MenuPage.Gap, left, chip);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        var title = MenuTheme.Label((mission is null ? LoadingScreen.MapName(MatchRequest.Current) : Text(mission.StringId, mission.Name)).ToUpperInvariant(), 28, MenuTheme.Text);
        title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        page.AddChild(MenuPage.Column(MenuPage.Gap, MenuPage.Line(MenuPage.Gap, MenuPage.Heading(won ? "Victory" : "Defeat"), MenuPage.Spring(), title), columns));

        if ((won ? texts?.WinVoiceMail : texts?.LoseVoiceMail) is { Length: > 0 } voice)
            game.Audio.SayFile(voice);
        page.TreeExiting += game.Audio.Hush;
        MenuPage.Focus(go);
    }));

    private void ShowScenarioBriefing() => _stage.Show(new BuiltPage(() => ShowMain(), page =>
    {
        var chip = new ChipPanel { SizeFlagsVertical = SizeFlags.ExpandFill, Padding = 30 };
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        scroll.AddChild(MenuTheme.Paragraph(game.BriefingText() ?? "No briefing for this scenario.", 25, MenuTheme.Text));
        chip.AddChild(scroll);
        var ok = Footer(Text(7302, "OK"), game.CloseMenu);
        page.AddChild(MenuPage.Column(MenuPage.Gap, MenuPage.Heading(Text(7304, "Briefing")), chip,
            MenuPage.Line(MenuPage.Gap, Footer(Text(7311, "Cancel"), () => ShowMain()), MenuPage.Spring(), ok)));
        MenuPage.Focus(ok);
    }));

    // A key as the player chose it (KeyMap), or two side by side.
    private static string K(params KeyMap.Act[] acts) => string.Join(" ", acts.Select(KeyMap.Name));

    // The remake's keys, in the hot keys page's two lists; first person's as the options' Classic F.P. Keys set them.
    private static (string Action, string Keys)[] FirstPersonKeys => GameOptions.ClassicDriving
        ?
        [
            ("Drive", K(KeyMap.Act.Forward, KeyMap.Act.Back)), ("Turn", $"{K(KeyMap.Act.Left, KeyMap.Act.Right)}, mouse"),
            ("Turn upper body", $"Shift with {K(KeyMap.Act.Left, KeyMap.Act.Right)} or mouse, right mouse"),
            ("Upper body forward", K(KeyMap.Act.CentreView)), ("Look up and down", "Mouse, Shift with arrows"), ("Fire", "Left mouse button"),
            ("Pick weapons", K(KeyMap.Act.Weapons)), ("Leave first person", $"{K(KeyMap.Act.FirstPerson)}, Esc"),
        ]
        :
        [
            ("Drive", K(KeyMap.Act.Forward, KeyMap.Act.Back)), ("Step sideways", $"{K(KeyMap.Act.Left, KeyMap.Act.Right)} (legs, hover, flight)"),
            ("Turn", $"{K(KeyMap.Act.TurnLeft, KeyMap.Act.TurnRight)}, {K(KeyMap.Act.Left, KeyMap.Act.Right)}, slowly with Ctrl"),
            ("Look round", "Mouse (the machine follows)"), ("Upper body forward", K(KeyMap.Act.CentreView)), ("Look up and down", "Mouse, Shift with arrows"),
            ("Fire", "Left mouse button"), ("Pick weapons", K(KeyMap.Act.Weapons)), ("Leave first person", $"{K(KeyMap.Act.FirstPerson)}, Esc"),
        ];

    private static (string Action, string Keys)[] GeneralKeys =>
    [
        ("Scroll", $"{K(KeyMap.Act.Forward, KeyMap.Act.Left, KeyMap.Act.Back, KeyMap.Act.Right)}, arrows, edges"),
        ("Turn view", K(KeyMap.Act.TurnLeft, KeyMap.Act.TurnRight)), ("Zoom", "Mouse wheel"),
        ("Zenith view", K(KeyMap.Act.ZenithView)), ("Ground view", K(KeyMap.Act.GroundView)), ("1st person", K(KeyMap.Act.FirstPerson)),
        ("Select", "Left click, drag"), ("Order", "Right click"), ("Stop", "Ctrl S"),
        ("Initiative", K(KeyMap.Act.Initiative)), ("Assembly point", K(KeyMap.Act.AssemblyPoint)), ("Create squad 0-9", "Ctrl 0-9"),
        ("Select squad 0-9", "0-9"), ("Turn building", K(KeyMap.Act.TurnBuilding)), ("Pause", K(KeyMap.Act.Pause)),
        ("Hide panel", "Alt Left"), ("Show panel", "Alt Right"), ("Menus", $"Esc, {K(KeyMap.Act.Menu)}"),
    ];

    private void ShowHotkeys() => _stage.Show(new BuiltPage(() => ShowMain(), page =>
    {
        ChipPanel List(int heading, string fallback, (string Action, string Keys)[] keys, int split)
        {
            var chip = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin, Padding = 26 };
            var columns = MenuPage.Line(56);
            foreach (var part in keys.Chunk(split))
            {
                var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                grid.AddThemeConstantOverride("h_separation", 28);
                grid.AddThemeConstantOverride("v_separation", 8);
                foreach (var (action, key) in part)
                {
                    grid.AddChild(MenuTheme.Label(action, 22, MenuTheme.Text));
                    grid.AddChild(MenuTheme.Label(key, 22, MenuTheme.Bright));
                }
                columns.AddChild(grid);
            }
            chip.AddChild(MenuPage.Column(16, MenuTheme.Label(Text(heading, fallback).ToUpperInvariant(), 26, MenuTheme.Bright), columns));
            return chip;
        }
        var back = Footer(Text(7312, "Continue"), () => ShowMain());
        page.AddChild(MenuPage.Column(MenuPage.Gap, MenuPage.Heading(Text(7306, "Hot Keys")),
            List(7064, "First Person Control", FirstPersonKeys, 3),
            List(7065, "General Control", GeneralKeys, 6),
            MenuPage.Spring(across: false),
            MenuPage.Line(MenuPage.Gap, back)));
        MenuPage.Focus(back);
    }));

    private void ShowOptions() => _stage.Show(new OptionsPage(() => ShowMain(), inGame: true));

    private void ShowSave() => _stage.Show(FilesPage.Save(() => ShowMain(), name =>
    {
        var why = game.SaveGame(name);
        if (why is null)
            ShowMain($"Saved: {name}");
        return why;
    }));

    private void ShowLoad() => _stage.Show(FilesPage.Load(() => ShowMain(), path => FilesPage.OpenSaved(game.GetTree(), path)));

    /// <summary>The statistics after a match (MachGuiCtxStatistics); OK leaves for the main menu, or after a campaign mission goes back to its debriefing.</summary>
    private void ShowStatistics() => _stage.Show(new BuiltPage(() => ShowMain(), page =>
    {
        var chip = new ChipPanel { SizeFlagsVertical = SizeFlags.ExpandFill, Padding = 24 };
        chip.AddChild(new StatisticsChart(game, Text));
        var ok = Footer(Text(7302, "OK"), Mission() is not null && !game.Watching ? ShowDebriefing : game.ExitToMenus);
        page.AddChild(MenuPage.Column(MenuPage.Gap, MenuPage.Heading(Text(7093, "Statistics")), chip,
            MenuPage.Line(MenuPage.Gap, MenuPage.Spring(), ok)));
        MenuPage.Focus(ok);
    }));
}

/// <summary>
/// The statistics after a match: each player's score, then blocks of four bars, red, blue, green and yellow, that
/// grow to their figures over two seconds with stats.wav: machines and buildings destroyed and lost and BMUs mined;
/// machines, fighting machines and buildings built, items researched and research points; and who destroyed whose
/// machines and buildings. The blocks share out whatever room the chip has.
/// </summary>
public partial class StatisticsChart(Game game, Func<int, string, string> text) : Control
{
    private static readonly Race[] BarRaces = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    private const int Small = 17, Large = 22;
    private double _since = -1;
    private bool _sounded;

    public override void _Process(double delta) => QueueRedraw();

    private Score? ScoreOf(Race race) => game.World.Races.TryGetValue(race, out var state) ? state.Score : null;

    private string NameOf(Race race) => !game.World.Races.ContainsKey(race) ? text(7097, "N/A")
        : race == Game.Player ? text(7095, "Player")
        : MatchRequest.Current.Network?.Players.FirstOrDefault(p => p.Slot == race && !p.Ai)?.Name ?? text(7096, "Computer");

    public override void _Draw()
    {
        var now = Time.GetTicksMsec() / 1000.0;
        if (_since < 0)
            _since = now;
        var growth = (float)Math.Clamp((now - _since - 0.5) / 2, 0, 1);
        if (growth > 0 && !_sounded)
        {
            _sounded = true;
            game.Audio.PlayGui("gui/sounds/stats.wav");
        }
        var font = GetThemeDefaultFont();
        void Write(Vector2 at, string line, int size, Color colour, float width = -1, HorizontalAlignment align = HorizontalAlignment.Left) =>
            DrawString(font, at + new Vector2(0, font.GetAscent(size)), line, align, width, size, colour);

        // Down the left the players and their scores; to the right four rows of five blocks.
        const float left = 250, heading = 34;
        var block = new Vector2((Size.X - left) / 5, (Size.Y - 4 * heading) / 4);
        var y = 0f;
        Write(new Vector2(0, y), text(7087, "Players").ToUpperInvariant(), Large, MenuTheme.Bright);
        y += heading + 6;
        foreach (var race in BarRaces.Where(game.World.Races.ContainsKey))
        {
            DrawRect(new Rect2(0, y + 6, 12, 12), ClassicAssets.RaceColour(race));
            Write(new Vector2(22, y), NameOf(race), Large, MenuTheme.Text, left - 40);
            Write(new Vector2(22, y + Large + 6), (ScoreOf(race)?.Gross ?? 0).ToString(), Large, MenuTheme.Bright);
            y += 2 * Large + 26;
        }

        // Each row of blocks under a heading: what the bars count (or, for the kills, whose machines or buildings),
        // then the four bars. A kill block's title is the player whose losses it counts.
        void Block(int column, float top, string title, Func<Race, float> value, Race? colourOf = null, int titleLines = 2)
        {
            var at = new Vector2(left + column * block.X, top);
            var width = block.X - 18;
            DrawMultilineString(font, at + new Vector2(0, font.GetAscent(Small)), title, HorizontalAlignment.Left, width, Small, titleLines,
                colourOf is null ? MenuTheme.Text : MenuTheme.Bright);
            var barsTop = BarsTop(top, titleLines);
            var values = BarRaces.Select(value).ToList();
            var range = Math.Max(1, values.Max());
            var row = BarRow(top, titleLines);
            for (var i = 0; i < BarRaces.Length; i++)
            {
                var track = new Rect2(at.X, barsTop + i * row, width - 52, row - 6);
                DrawRect(track, MenuTheme.Well);
                var colour = ClassicAssets.RaceColour(colourOf ?? BarRaces[i]);
                DrawRect(new Rect2(track.Position, new Vector2(track.Size.X * values[i] / range * growth, track.Size.Y)), colour);
                if (growth >= 1)
                    Write(new Vector2(track.End.X + 8, track.Position.Y + (track.Size.Y - Small) / 2 - 2), ((int)values[i]).ToString(), Small, MenuTheme.Text);
            }
        }
        float BarsTop(float top, int titleLines) => top + titleLines * (Small + 6) + 6;
        float BarRow(float top, int titleLines) => Math.Min(26, (top + block.Y - 10 - BarsTop(top, titleLines)) / 4);
        string Title(int id, string fallback) => text(id, fallback).Replace("\r", "").Replace('\n', ' ').Trim();
        float Sum(Dictionary<Race, int>? counts) => counts?.Values.Sum() ?? 0;

        var rows = Enumerable.Range(0, 4).Select(r => r * (block.Y + heading) + heading).ToArray();
        void Heading(int row, string words) => Write(new Vector2(left, rows[row] - heading), words.ToUpperInvariant(), Large, MenuTheme.Bright);
        Heading(0, text(7091, "General"));
        Block(0, rows[0], Title(7077, "Machine kills"), r => Sum(ScoreOf(r)?.MachinesDestroyed));
        Block(1, rows[0], Title(7078, "Construction kills"), r => Sum(ScoreOf(r)?.ConstructionsDestroyed));
        Block(2, rows[0], Title(7079, "Machine losses"), r => ScoreOf(r)?.MachinesLost ?? 0);
        Block(3, rows[0], Title(7080, "Construction losses"), r => ScoreOf(r)?.ConstructionsLost ?? 0);
        Block(4, rows[0], Title(7081, "BMUs mined"), r => ScoreOf(r)?.BmusMined ?? 0);
        Heading(1, text(7094, "Production"));
        Block(0, rows[1], Title(7082, "Machines built"), r => ScoreOf(r)?.MachinesBuilt ?? 0);
        Block(1, rows[1], Title(7083, "Military machines built"), r => ScoreOf(r)?.MilitaryMachinesBuilt ?? 0);
        Block(2, rows[1], Title(7084, "Constructions built"), r => ScoreOf(r)?.ConstructionsBuilt ?? 0);
        Block(3, rows[1], Title(7085, "Items researched"), r => ScoreOf(r)?.ItemsResearched ?? 0);
        Block(4, rows[1], Title(7086, "Research points"), r => ScoreOf(r)?.ResearchUnits ?? 0);
        Heading(2, $"{text(7092, "Kills")}: {Title(7088, "Destroyed machines belonging to")}");
        Heading(3, $"{text(7092, "Kills")}: {Title(7089, "Destroyed constructions belonging to")}");
        foreach (var (victim, i) in BarRaces.Select((r, i) => (r, i)))
        {
            Block(i + 1, rows[2], NameOf(victim), r => ScoreOf(r)?.MachinesDestroyed.GetValueOrDefault(victim) ?? 0, victim, 1);
            Block(i + 1, rows[3], NameOf(victim), r => ScoreOf(r)?.ConstructionsDestroyed.GetValueOrDefault(victim) ?? 0, victim, 1);
        }
        // The rows of the kill blocks are the players who destroyed them, named in the first column beside their bars.
        foreach (var top in rows[2..])
        {
            var row = BarRow(top, 1);
            Write(new Vector2(left, top), text(7087, "Players"), Small, MenuTheme.Dim);
            for (var i = 0; i < BarRaces.Length; i++)
                Write(new Vector2(left, BarsTop(top, 1) + i * row + (row - 6 - Small) / 2 - 2), NameOf(BarRaces[i]), Small, MenuTheme.Text, block.X - 20);
        }
    }
}
