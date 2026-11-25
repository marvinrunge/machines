using Godot;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The skirmish setup (MachGuiCtxSkirmish): every map of the original skirmish and multiplayer menus (sceninfo.dat)
/// and the remake's own test maps in a tree, the picked one's overview and description, and the original settings,
/// players, colour, start positions, starting BMUs, mineral resources, technology level and victory condition.
/// For a multiplayer room (<see cref="MultiplayerPage"/>) the same page sets the room up instead: the players come
/// to it, so it asks whether free slots play the AI, how many commanders a side has (one, or two sharing it, the remake's
/// own) and what only a network game has, Broadcast Alliances and Disable First Person. With a <paramref name="room"/> it
/// is that room's settings (MachGuiCtxSettings), which its host changes. A room is a normal match or a battle (the remake's
/// own team mode of heroes): in Battle the page offers only the battle maps and asks for the team size (two against two or
/// five against five), the bracket, the lives and the victory, as every seat is a hero, a player's from their pod or a bot.
/// </summary>
public partial class SkirmishPage(MenuScreens screens, bool forRoom = false, RoomState? room = null)
    : MenuPage(room is not null ? screens.ShowRoom : forRoom ? screens.ShowMultiplayer : screens.ShowSinglePlayer)
{
    private static readonly string[] SizeNames = ["Small", "Medium", "Large"];
    private static readonly Race[] Colours = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    // The last settings, kept while the game runs so returning to the menu shows them again.
    private static (string Scenario, string Planet)? _lastMap;
    private static int _lastPlayers = 2, _lastColour, _lastStarts = 1, _lastBmu, _lastResources = 2, _lastTech, _lastVictory, _lastFill, _lastAi = (int)AiLevel.Normal, _lastFog, _lastBroadcast, _lastNoFirstPerson;

    // A room's mode (0 normal, 1 battle) and a battle's settings; tier 0 until chosen, then the pod's hero sets it. A normal
    // room's commanders per side and a battle's team size, by their place in RoomOptions' lists.
    private static int _lastMode, _lastTier, _lastLives = 1, _lastWin, _lastCommanders, _lastTeamSize;

    // A battle room's team size the page does not offer (fifteen, from MACHINES_MP's team=15 or a room that has it), kept as
    // it is until the host chooses one of those it offers.
    private static int? _unofferedTeamSize;
    private static string _lastBattleMap = "m_desert";
    private static readonly BattleWin[] Wins = [BattleWin.Both, BattleWin.Wipe, BattleWin.Capture];

    // The battle map picked, and its lives chooser, which follows the team size chosen.
    private BattleMap? _battleMap;
    private ChipChoice? _lives;

    private readonly Dictionary<string, Texture2D?> _maps = [];
    private readonly List<CatalogueScenario> _entries = [];

    private Tree _tree = null!;
    private Label _title = null!, _facts = null!, _description = null!;
    private TextureRect _preview = null!;
    private ChipChoice _players = null!, _bmu = null!;
    private ChipButton _start = null!;
    private Control _normal = null!;
    private Control? _battle;
    private ScenarioCatalogue _catalogue = null!;

    private CatalogueScenario? _selected;
    private List<Race> _slots = [];

    protected override void Build()
    {
        if (room is not null)
            Adopt(room);
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading(room is not null ? "Settings" : forRoom ? "New room" : "Skirmish"));

        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var maps = new ChipPanel { CustomMinimumSize = new Vector2(340, 0), Padding = 14 };
        _tree = new Tree { HideRoot = true };
        _tree.ItemSelected += OnMapSelected;
        _tree.ItemActivated += Start;
        maps.AddChild(_tree);
        columns.AddChild(maps);

        var about = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 20 };
        _title = MenuTheme.Paragraph("", 30, MenuTheme.Bright);
        _facts = MenuTheme.Paragraph("", 20, MenuTheme.Dim);
        _preview = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1.6f,
        };
        _description = MenuTheme.Paragraph("", 21, MenuTheme.Text);
        var scroll = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill };
        scroll.AddChild(_description);
        about.AddChild(Column(12, _title, _facts, _preview, scroll));
        columns.AddChild(about);

        var settings = new ChipPanel { CustomMinimumSize = new Vector2(480, 0), Padding = 20 };
        var rows = Column(14);
        _normal = rows;
        if (forRoom)
        {
            _battle = BattleRows();
            _battle.Visible = false;
            // A room's rows (and at the large interface more than the screen is high) scroll, so Back stays on it.
            var all = Column(14, rows, _battle);
            all.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            var scrolled = new ScrollContainer { HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
            scrolled.AddChild(all);
            settings.AddChild(scrolled);
        }
        else
            settings.AddChild(rows);
        columns.AddChild(settings);
        _players = new ChipChoice([]);
        _players.ItemSelected += _ => _lastPlayers = int.TryParse(_players.Text.Split(' ')[0], out var n) ? n : 2;
        _bmu = Choice([], _lastBmu, i => _lastBmu = i);
        if (!forRoom)
        {
            rows.AddChild(Setting("Players", _players, 180));
            rows.AddChild(Setting("Your colour", Choice(Colours.Select(c => c.ToString()), _lastColour, i => _lastColour = i), 180));
            rows.AddChild(Setting("Start positions", Choice(["Fixed", "Random"], _lastStarts, i => _lastStarts = i), 180));
        }
        rows.AddChild(Setting("Starting BMUs", _bmu, 180));
        rows.AddChild(Setting("Resources", Choice(["Low", "Medium", "High"], _lastResources, i => _lastResources = i), 180));
        rows.AddChild(Setting("Technology", Choice(["Map default", "Low", "Medium", "High"], _lastTech, i => _lastTech = i), 180));
        rows.AddChild(Setting("Victory", Choice(new[] { "Default", "Annihilation", "Destroy pod" }
            .Concat(MatchOptions.TimeLimits.Select(s => Lang.T("Timed: {0}", FormatMinutes(s)))), _lastVictory, i => _lastVictory = i), 180));
        rows.AddChild(Setting("AI", Choice(["Easy", "Normal", "Hard"], _lastAi, i => _lastAi = i), 180));
        rows.AddChild(Setting("Fog of war", Choice(["On", "Off"], _lastFog, i => _lastFog = i), 180));
        if (forRoom)
        {
            rows.AddChild(Setting("Commanders per side", Choice(RoomOptions.CommandersPerSide.Select(n => n.ToString()), _lastCommanders, i => _lastCommanders = i), 180));
            rows.AddChild(Setting("Free slots", Choice(["Play the AI", "Stay empty"], _lastFill, i => _lastFill = i), 180));
            rows.AddChild(Setting("Broadcast alliances", Choice(["Off", "On"], _lastBroadcast, i => _lastBroadcast = i), 180));
            rows.AddChild(Setting("Disable first person", Choice(["Off", "On"], _lastNoFirstPerson, i => _lastNoFirstPerson = i), 180));
        }

        var footer = Line(Gap);
        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(340, 76);
        footer.AddChild(back);
        footer.AddChild(Spring());
        if (forRoom)
        {
            // A room's mode stands in the foot, where the settings column has room for neither mode's rows and it.
            var mode = new ChipPanel { Padding = 6, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var setting = Setting("Mode", Choice(["Normal", "Battle"], _lastMode, SwitchMode), 0);
            setting.CustomMinimumSize = new Vector2(440, 0);
            mode.AddChild(setting);
            footer.AddChild(mode);
            footer.AddChild(Spring());
        }
        _start = Button(room is not null ? "OK" : forRoom ? "Open room" : "Start", Start, 76, 30);
        _start.CustomMinimumSize = new Vector2(480, 76);
        footer.AddChild(_start);
        page.AddChild(footer);

        _catalogue = ScenarioCatalogueParser.Load(System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat"));
        ShowMode();
        SwitchOn();
        Focus(_tree);
        // MACHINES_MENU_START=1 presses Start on the map picked (MACHINES_MENU_SELECT), the way a player would.
        if (!forRoom && System.Environment.GetEnvironmentVariable("MACHINES_MENU_START") == "1")
            Callable.From(Start).CallDeferred();
    }

    private static ChipChoice Choice(IEnumerable<string> items, int selected, Action<int> changed)
    {
        var choice = new ChipChoice(items, selected);
        choice.ItemSelected += changed;
        return choice;
    }

    /// <summary>
    /// A battle room's settings: the team size, the bracket by its top tier, the lives and how the battle is won. A new team
    /// size proposes its lives (<see cref="RoomOptions.ProposedLives"/>), which the host may change after.
    /// </summary>
    private Control BattleRows()
    {
        if (_lastTier == 0)
            _lastTier = Math.Clamp(PodTier(), 1, BattleCatalogue.MostTier);
        var tiers = Enumerable.Range(1, BattleCatalogue.MostTier).Select(BracketName);
        var note = MenuTheme.Paragraph(Lang.T("Every player brings the hero from their pod; every other seat is a bot hero of the same tier."), 19, MenuTheme.Dim);
        _lives = Choice(Enumerable.Range(1, 5).Select(BattlePod.LivesName), _lastLives - 1, i => _lastLives = i + 1);
        // A room of a size not offered (fifteen) shows it after those offered, as long as it keeps it.
        List<int> shown = [.. RoomOptions.TeamSizes, .. _unofferedTeamSize is { } unoffered ? [unoffered] : Array.Empty<int>()];
        var sizes = Choice(shown.Select(size => RoomOptions.TeamSizeName(size, null)), _unofferedTeamSize is null ? _lastTeamSize : shown.Count - 1, i =>
        {
            TakeTeamSize(shown[i]);
            _lastLives = RoomOptions.ProposedLives(shown[i], _battleMap);
            if (_lives is not null)
                _lives.Selected = _lastLives - 1;
            ShowBattleText();
        });
        return Column(14,
            Setting("Team size", sizes, 180),
            Setting("Bracket", Choice(tiers, _lastTier - 1, i => _lastTier = i + 1), 180),
            Setting("Lives", _lives, 180),
            Setting("Victory", Choice(Wins.Select(BattlePod.VictoryName), _lastWin, i => _lastWin = i), 180),
            note);
    }

    /// <summary>The team size chosen, as <see cref="BattleOptions.TeamSize"/>.</summary>
    private static int TeamSize => _unofferedTeamSize ?? RoomOptions.TeamSizes[Math.Clamp(_lastTeamSize, 0, RoomOptions.TeamSizes.Count - 1)];

    /// <summary>
    /// Takes a battle's team size as the page's choice: one it offers as that choice, another (fifteen) as it is, up to
    /// <see cref="BattleBalance.MostTeamSize"/>, the choice standing on today's layout meanwhile.
    /// </summary>
    private static void TakeTeamSize(int teamSize)
    {
        var offered = RoomOptions.Offered(teamSize);
        _lastTeamSize = Math.Max(0, offered);
        _unofferedTeamSize = offered < 0 && teamSize > 0 ? Math.Min(teamSize, BattleBalance.MostTeamSize) : null;
    }

    /// <summary>The commanders a side has in the normal room being set up.</summary>
    private static int Commanders => RoomOptions.CommandersPerSide[Math.Clamp(_lastCommanders, 0, RoomOptions.CommandersPerSide.Count - 1)];

    /// <summary>The tier of the hero last put together in the pod: a new battle room is first offered at its bracket.</summary>
    private static int PodTier() => BattleAccounts.Load().LastHero is { } last && BattleCatalogue.Tier(last.Chassis) is > 0 and var tier ? tier : 1;

    /// <summary>A bracket by its top tier: "Tier III (II–III)".</summary>
    public static string BracketName(int tier)
    {
        var bottom = BattleCatalogue.BracketBottom(tier);
        var top = Lang.T("Tier {0}", BattleDraft.Roman(tier));
        return bottom == tier ? top : $"{top} ({BattleDraft.Roman(bottom)}–{BattleDraft.Roman(tier)})";
    }

    /// <summary>Whether the page sets up a battle room.</summary>
    private bool Battle => forRoom && _lastMode == 1;

    private void SwitchMode(int mode)
    {
        _lastMode = mode;
        ShowMode();
    }

    /// <summary>The maps and the settings of the mode chosen.</summary>
    private void ShowMode()
    {
        _normal.Visible = !Battle;
        if (_battle is not null)
            _battle.Visible = Battle;
        _tree.Clear();
        _entries.Clear();
        _selected = null;
        _start.Disabled = true;
        if (Battle)
            FillBattleTree();
        else
            FillTree(_catalogue);
    }

    private static string FormatMinutes(int seconds) => seconds < 3600 ? $"{seconds / 60} min" : $"{seconds / 3600.0:0.#} h";

    // ---- maps -----------------------------------------------------------------------------------

    private void FillTree(ScenarioCatalogue catalogue)
    {
        var root = _tree.CreateItem();
        TreeItem? first = null, remembered = null;
        TreeItem Branch(TreeItem parent, string text, Color colour)
        {
            var item = _tree.CreateItem(parent);
            item.SetText(0, text);
            item.SetSelectable(0, false);
            item.SetCustomColor(0, colour);
            return item;
        }
        TreeItem Leaf(TreeItem parent, string text, CatalogueScenario scenario)
        {
            var item = _tree.CreateItem(parent);
            item.SetText(0, text);
            item.SetMetadata(0, Variant.From(_entries.Count));
            _entries.Add(scenario);
            if (_lastMap == (scenario.Name, scenario.Planet) && remembered is null)
                remembered = item;
            return item;
        }

        foreach (var (section, terrains) in new[] { ("Skirmish", catalogue.Skirmish), ("Multiplayer maps", catalogue.Multiplayer) })
        {
            var sectionItem = Branch(root, Lang.T(section).ToUpperInvariant(), MenuTheme.Bright);
            foreach (var size in terrains.GroupBy(t => t.Size))
            {
                var sizeItem = Branch(sectionItem, SizeNames[(int)size.Key], MenuTheme.Dim);
                foreach (var terrain in size)
                {
                    var terrainItem = Branch(sizeItem, screens.Text(terrain.StringId, terrain.Name), MenuTheme.Text);
                    terrainItem.Collapsed = section != "Skirmish";
                    foreach (var scenario in terrain.Scenarios)
                    {
                        var leaf = Leaf(terrainItem, screens.Text(scenario.StringId, scenario.Name), scenario);
                        first ??= leaf;
                    }
                }
            }
        }

        // The remake's own maps (data/maps/maps.txt: scenario, planet and title per line), such as the showcase.
        var own = System.IO.Path.Combine(Paths.Maps, "maps.txt");
        if (File.Exists(own))
        {
            var ownItem = Branch(root, "TEST MAPS", MenuTheme.Bright);
            foreach (var line in File.ReadLines(own).Where(l => l.Trim() is { Length: > 0 } t && !t.StartsWith("//")))
            {
                var words = line.Split(' ', 3, StringSplitOptions.RemoveEmptyEntries);
                var scenario = new CatalogueScenario(words[0], words[1], 0, null, null, [], null, new Dictionary<string, string>(), null, 0);
                Leaf(ownItem, words.Length > 2 ? words[2] : words[0], scenario);
            }
        }

        if (System.Environment.GetEnvironmentVariable("MACHINES_MENU_SELECT") is { } preselect)
            remembered = FindItem(root, preselect) ?? remembered;
        if ((remembered ?? first) is { } select)
        {
            for (var parent = select.GetParent(); parent is not null; parent = parent.GetParent())
                parent.Collapsed = false;
            select.Select(0);
            _tree.CallDeferred(Tree.MethodName.ScrollToItem, select, true);
        }
    }

    /// <summary>The battle maps; the one last picked is chosen.</summary>
    private void FillBattleTree()
    {
        var root = _tree.CreateItem();
        var section = _tree.CreateItem(root);
        section.SetText(0, Lang.T("Battle maps").ToUpperInvariant());
        section.SetSelectable(0, false);
        section.SetCustomColor(0, MenuTheme.Bright);
        TreeItem? select = null;
        var known = _catalogue.Skirmish.Concat(_catalogue.Multiplayer).SelectMany(t => t.Scenarios).ToList();
        foreach (var map in BattleCatalogue.Maps)
        {
            var leaf = _tree.CreateItem(section);
            leaf.SetText(0, BattlePod.MapName(map));
            leaf.SetMetadata(0, Variant.From(_entries.Count));
            _entries.Add(known.FirstOrDefault(s => s.Name == map.Scenario)
                         ?? new CatalogueScenario(map.Scenario, map.Planet, map.StringId, null, null, [], 4, new Dictionary<string, string>(), null, 0));
            if (select is null || map.Scenario == _lastBattleMap)
                select = leaf;
        }
        if (select is not null)
        {
            select.Select(0);
            _tree.CallDeferred(Tree.MethodName.ScrollToItem, select, true);
        }
    }

    /// <summary>
    /// A battle map's two teams in words: "Red and Blue against Green and Yellow"; with a team size the races its heroes
    /// play, each team's first (<see cref="BattleSeats.Teams"/>): "Red against Green". With <paramref name="look"/> each race
    /// is named by the colour a match shows it in (<see cref="Game.Look"/>), which with a team size may be another of its
    /// team's colours: "Blue against Green".
    /// </summary>
    public static string Teams(BattleMap map, int teamSize = 0, Func<Race, Race>? look = null)
    {
        string Team(IEnumerable<Race> team) => string.Join(Lang.T(" and "), team.Select(r => Lang.T((look?.Invoke(r) ?? r).ToString())));
        var (a, b) = BattleSeats.Teams(new BattleOptions { TeamSize = teamSize }, map);
        return Lang.T("{0} against {1}", Team(a), Team(b));
    }

    /// <summary>A team size in words, "2 vs 2", "5 vs 5" or "10 vs 10", in the player's language.</summary>
    public static string TeamSizeName(int teamSize, BattleMap? map = null) => Lang.T(RoomOptions.TeamSizeName(teamSize, map));

    /// <summary>How a battle goes, for a team size: the heroes a team, how a team wins, and the draw.</summary>
    public static string BattleText(int teamSize, BattleMap? map) => RoomOptions.HeroesPerTeam(teamSize, map) switch
    {
        2 => Lang.T("Two heroes against two. A team wins when the other has no hero and no life left, or when its heroes have stood in the circle round the other team's pod long enough to take it. When the time is up, it is a draw."),
        5 => Lang.T("Five heroes against five. A team wins when the other has no hero and no life left, or when its heroes have stood in the circle round the other team's pod long enough to take it. When the time is up, it is a draw."),
        var heroes => Lang.T("{0} heroes against {0}. A team wins when the other has no hero and no life left, or when its heroes have stood in the circle round the other team's pod long enough to take it. When the time is up, it is a draw.", heroes),
    };

    private TreeItem? FindItem(TreeItem item, string wanted)
    {
        for (var child = item.GetFirstChild(); child is not null; child = child.GetNext())
        {
            if (child.GetMetadata(0).VariantType == Variant.Type.Int && _entries[child.GetMetadata(0).AsInt32()] is var entry
                && $"{entry.Name},{entry.Planet}" == wanted)
                return child;
            if (FindItem(child, wanted) is { } found)
                return found;
        }
        return null;
    }

    private void OnMapSelected()
    {
        var item = _tree.GetSelected();
        if (item?.GetMetadata(0).VariantType != Variant.Type.Int)
            return;
        _selected = _entries[item.GetMetadata(0).AsInt32()];
        if (Battle && BattleCatalogue.Map(_selected.Name) is { } battleMap)
        {
            ShowBattleMap(item, battleMap);
            return;
        }
        _lastMap = (_selected.Name, _selected.Planet);

        var scenario = Paths.Data.LoadScenario(_selected.Name);
        _slots = scenario.Races.Select(r => r.Race).OrderBy(r => r).ToList();
        var mapBmu = scenario.Races.Select(r => r.Bmu).FirstOrDefault(b => b is not null);
        var maxPlayers = Math.Min(_selected.MaxPlayers ?? _slots.Count, _slots.Count);

        _title.Text = item.GetText(0);
        _facts.Text = $"{_selected.Planet.ToUpperInvariant()}   ·   {Lang.T("UP TO {0} PLAYERS", maxPlayers)}";
        if (!_maps.TryGetValue(_selected.Planet, out var map))
            _maps[_selected.Planet] = map = screens.Assets.PlanetMap(_selected.Planet);
        _preview.Texture = MiniMaps.For(_selected.Name) ?? map;
        _description.Text = screens.Assets.MenuText(_selected.Text)
            ?? (screens.Assets.HasClassicModels ? "" : "Map overviews and descriptions come from your own game copy (Options, Import).");

        var counts = Enumerable.Range(Math.Min(2, maxPlayers), Math.Max(0, maxPlayers - Math.Min(2, maxPlayers) + 1)).ToList();
        _players.SetItems(counts.Select(n => n == 1 ? "1 (no opponent)" : n.ToString()), Math.Max(0, counts.IndexOf(Math.Clamp(_lastPlayers, counts.FirstOrDefault(), counts.LastOrDefault()))));
        _bmu.SetItems(new[] { mapBmu is { } bmu ? Lang.T("Map default ({0})", bmu) : "Map default" }
            .Concat(Enum.GetValues<StartingBmu>().Skip(1).Select(level => MatchOptions.BmuFor(level)!.Value.ToString())), _lastBmu);

        _start.Disabled = forRoom ? RoomSlots.Count < 2 : _slots.Count < 1;
    }

    /// <summary>A battle map picked: its two teams, its picture and how a battle goes.</summary>
    private void ShowBattleMap(TreeItem item, BattleMap map)
    {
        _lastBattleMap = map.Scenario;
        _battleMap = map;
        _slots = map.TeamA.Concat(map.TeamB).Order().ToList();
        _title.Text = item.GetText(0);
        _preview.Texture = BattlePod.MapPicture(map, screens.Assets);
        ShowBattleText();
        _start.Disabled = false;
    }

    /// <summary>The picked battle map's teams and how a battle goes, for the team size chosen.</summary>
    private void ShowBattleText()
    {
        if (_battleMap is not { } map || !Battle)
            return;
        _facts.Text = $"{map.Planet.ToUpperInvariant()}   ·   {TeamSizeName(TeamSize, map)}   ·   {Teams(map, TeamSize)}";
        _description.Text = BattleText(TeamSize, map);
    }

    /// <summary>The start positions a room on the picked map has: those past the map's players are not for anyone.</summary>
    private List<Race> RoomSlots => _slots.Take(_selected?.MaxPlayers ?? _slots.Count).ToList();

    // ---- starting -------------------------------------------------------------------------------

    private void Start()
    {
        if (_selected is null || _start.Disabled)
            return;
        if (forRoom)
        {
            if (!NetLink.Connected)
            {
                screens.Message("The connection to the relay is gone.", ("OK", screens.ShowMultiplayer));
                return;
            }
            var slots = Battle ? _slots : RoomSlots;
            if (room is null)
                NetLink.Send(Room(_selected.Name, _selected.Planet, slots));
            else
                NetLink.Send(new ChangeRoom(_selected.Name, _selected.Planet, slots, Options(network: true, room.Options), FillWithAi: Battle || _lastFill == 0,
                    SeatsPerSlot: Battle ? 1 : Commanders));
            screens.ShowRoom();
            return;
        }
        var players = int.TryParse(_players.Text.Split(' ')[0], out var n) ? n : 2;
        var starts = StartPositions.Assign(_slots, players, Colours[_lastColour], random: _lastStarts == 1, new Random());
        MatchRequest.Current = new MatchRequest(_selected.Name, _selected.Planet, starts, Options(network: false));
        LoadingScreen.Show(GetTree(), _title.Text, $"{_selected.Planet.ToUpperInvariant()}   ·   {(players == 1 ? Lang.T("NO OPPONENT") : Lang.T("{0} PLAYERS", players))}");
    }

    /// <summary>
    /// The settings last chosen on the page; only a <paramref name="network"/> game has the last two, and only a room can be a
    /// battle. A room's settings are changed from <paramref name="kept"/>, the options it has, so that what the page does not
    /// show (a Wired run's or a battle's own settings, the rules a recording keeps) comes through the change unharmed.
    /// </summary>
    private static MatchOptions Options(bool network, MatchOptions? kept = null)
    {
        var basis = kept ?? new MatchOptions();
        if (network && _lastMode == 1)
            return RoomOptions.Battle(basis, _lastTier, _lastLives, Wins[Math.Clamp(_lastWin, 0, Wins.Length - 1)], TeamSize);
        var victory = _lastVictory switch
        {
            0 => VictoryCondition.Default,
            1 => VictoryCondition.Annihilation,
            2 => VictoryCondition.Pod,
            _ => VictoryCondition.Timer,
        };
        return RoomOptions.Normal(basis, new MatchOptions
        {
            StartingBmu = (StartingBmu)_lastBmu,
            Resources = ResourceLevel.Low + _lastResources,
            TechLevel = (TechLevel)_lastTech,
            Victory = victory,
            TimeLimit = victory == VictoryCondition.Timer ? MatchOptions.TimeLimits[_lastVictory - 3] : 1800,
            AiLevel = (AiLevel)_lastAi,
            FogOfWar = _lastFog == 0,
            BroadcastAlliances = network && _lastBroadcast == 1,
            DisableFirstPerson = network && _lastNoFirstPerson == 1,
        });
    }

    /// <summary>The page shows the settings of the room it changes.</summary>
    private static void Adopt(RoomState room)
    {
        var options = room.Options;
        if (options.Battle is { } battle)
        {
            (_lastMode, _lastBattleMap, _lastTier, _lastLives) = (1, room.Scenario, battle.Tier, battle.Lives);
            _lastWin = Math.Max(0, Array.IndexOf(Wins, battle.Win));
            TakeTeamSize(battle.TeamSize);
            return;
        }
        _lastMode = 0;
        _lastMap = (room.Scenario, room.Planet);
        _lastCommanders = Math.Max(0, RoomOptions.CommandersPerSide.ToList().IndexOf(room.SeatsPerSlot));
        (_lastBmu, _lastTech, _lastAi) = ((int)options.StartingBmu, (int)options.TechLevel, (int)options.AiLevel);
        _lastResources = Math.Max(0, options.Resources - ResourceLevel.Low);
        _lastVictory = options.Victory switch
        {
            VictoryCondition.Annihilation => 1,
            VictoryCondition.Pod => 2,
            VictoryCondition.Timer => 3 + Math.Max(0, MatchOptions.TimeLimits.ToList().IndexOf(options.TimeLimit)),
            _ => 0,
        };
        _lastFill = room.FillWithAi ? 0 : 1;
        _lastFog = options.FogOfWar ? 0 : 1;
        _lastBroadcast = options.BroadcastAlliances ? 1 : 0;
        _lastNoFirstPerson = options.DisableFirstPerson ? 1 : 0;
    }

    /// <summary>
    /// A room's settings in a line, for all in it to read (the ready room's list, MachGuiCtxImReady); with
    /// <paramref name="commanders"/> a side has more than one, that too.
    /// </summary>
    public static string Summary(MatchOptions options, int commanders = 1)
    {
        if (options.Battle is { } battle)
            return string.Join("   ·   ",
                $"{Lang.T("Battle")}: {TeamSizeName(battle.TeamSize)}",
                BracketName(battle.Tier),
                BattlePod.LivesName(battle.Lives),
                $"{Lang.T("Victory")}: {BattlePod.VictoryName(battle.Win)}",
                Lang.T("Free seats: bot heroes"));
        static string Item(string name, string value) => $"{Lang.T(name)}: {Lang.T(value)}";
        static string Switch(string name, bool on) => Item(name, on ? "On" : "Off");
        var victory = options.Victory switch
        {
            VictoryCondition.Annihilation => "Annihilation",
            VictoryCondition.Pod => "Destroy pod",
            VictoryCondition.Timer => Lang.T("Timed: {0}", FormatMinutes(options.TimeLimit)),
            _ => "Default",
        };
        return string.Join("   ·   ", new[] { commanders > 1 ? Lang.T("{0} commanders per side", commanders) : null }.OfType<string>().Concat([
            Item("Starting BMUs", MatchOptions.BmuFor(options.StartingBmu)?.ToString() ?? "Map default"),
            Item("Resources", options.Resources == ResourceLevel.Default ? "Map default" : options.Resources.ToString()),
            Item("Technology", options.TechLevel == TechLevel.Default ? "Map default" : options.TechLevel.ToString()),
            Item("Victory", victory),
            Item("AI", options.AiLevel.ToString()),
            Switch("Fog of war", options.FogOfWar),
            Switch("Broadcast alliances", options.BroadcastAlliances),
            Switch("Disable first person", options.DisableFirstPerson)]));
    }

    private static CreateRoom Room(string scenario, string planet, List<Race> slots) => _lastMode == 1
        ? new(Lang.T("{0}'s battle", NetLink.Name), scenario, planet, slots, Options(network: true), FillWithAi: true)
        : new(Lang.T("{0}'s game", NetLink.Name), scenario, planet, slots, Options(network: true), FillWithAi: _lastFill == 0, SeatsPerSlot: Commanders);

    /// <summary>
    /// Opens the room page on its battle settings next time (MACHINES_MENU_SCREENSHOT's room-battle), with
    /// MACHINES_MENU_TEAM=&lt;n&gt; heroes a team chosen (5 or 10 for five or ten against as many, 15 kept as it is; else
    /// today's layout) and its lives proposed, as choosing it on the page does.
    /// </summary>
    public static void ChooseBattle()
    {
        _lastMode = 1;
        if (!int.TryParse(System.Environment.GetEnvironmentVariable("MACHINES_MENU_TEAM"), out var team))
            return;
        TakeTeamSize(team);
        _lastLives = RoomOptions.ProposedLives(TeamSize, null);
    }

    /// <summary>
    /// A battle room on <paramref name="scenario"/> (a battle map) at a bracket, with <paramref name="teamSize"/> heroes a team
    /// (0 today's layout), for the multiplayer page's automated checks
    /// (MACHINES_MP=host,&lt;address&gt;,battle=&lt;map&gt;[,tier=,lives=,win=,team=]); null off the battle maps.
    /// </summary>
    public static CreateRoom? BattleRoom(string scenario, int tier, int lives, BattleWin win, int teamSize = 0)
    {
        if (BattleCatalogue.Map(scenario) is not { } map)
            return null;
        (_lastMode, _lastBattleMap, _lastTier, _lastLives) = (1, map.Scenario, Math.Clamp(tier, 1, BattleCatalogue.MostTier), Math.Clamp(lives, 1, 5));
        _lastWin = Math.Max(0, Array.IndexOf(Wins, win));
        TakeTeamSize(teamSize);
        return Room(map.Scenario, map.Planet, map.TeamA.Concat(map.TeamB).Order().ToList());
    }

    /// <summary>
    /// A room on the map last picked (or MACHINES_MENU_SELECT's, or the first skirmish map) with the settings last
    /// chosen and <paramref name="commanders"/> a side, for the multiplayer page's automated checks.
    /// </summary>
    public static CreateRoom? LastRoom(int commanders = 1)
    {
        _lastMode = 0;
        _lastCommanders = Math.Max(0, RoomOptions.CommandersPerSide.ToList().IndexOf(commanders));
        var map = _lastMap;
        if (map is null && System.Environment.GetEnvironmentVariable("MACHINES_MENU_SELECT")?.Split(',') is [var name, var planet])
            map = (name, planet);
        if (map is null && ScenarioCatalogueParser.Load(System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat"))
                .Skirmish.SelectMany(t => t.Scenarios).FirstOrDefault() is { } first)
            map = (first.Name, first.Planet);
        if (map is not { } chosen)
            return null;
        var (scenario, world) = chosen;
        var slots = Paths.Data.LoadScenario(scenario).Races.Select(r => r.Race).OrderBy(r => r).ToList();
        return slots.Count < 2 ? null : Room(scenario, world, slots);
    }
}
