using Godot;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Net;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The skirmish setup (MachGuiCtxSkirmish): every map of the original skirmish and multiplayer menus (sceninfo.dat)
/// and the remake's own test maps in a tree, the picked one's overview and description, and the original settings,
/// players, colour, start positions, starting BMUs, mineral resources, technology level and victory condition.
/// For a multiplayer room (<see cref="MultiplayerPage"/>) the same page sets the room up instead: the players come
/// to it, so it asks whether free slots play the AI and what only a network game has, Broadcast Alliances and Disable
/// First Person. With a <paramref name="room"/> it is that room's settings (MachGuiCtxSettings), which its host changes.
/// </summary>
public partial class SkirmishPage(MenuScreens screens, bool forRoom = false, RoomState? room = null)
    : MenuPage(room is not null ? screens.ShowRoom : forRoom ? screens.ShowMultiplayer : screens.ShowMain)
{
    private static readonly string[] SizeNames = ["Small", "Medium", "Large"];
    private static readonly Race[] Colours = [Race.Red, Race.Blue, Race.Green, Race.Yellow];

    // The last settings, kept while the game runs so returning to the menu shows them again.
    private static (string Scenario, string Planet)? _lastMap;
    private static int _lastPlayers = 2, _lastColour, _lastStarts = 1, _lastBmu, _lastResources = 2, _lastTech, _lastVictory, _lastFill, _lastAi = (int)AiLevel.Normal, _lastFog, _lastBroadcast, _lastNoFirstPerson;

    private readonly Dictionary<string, Texture2D?> _maps = [];
    private readonly List<CatalogueScenario> _entries = [];

    private Tree _tree = null!;
    private Label _title = null!, _facts = null!, _description = null!;
    private TextureRect _preview = null!;
    private ChipChoice _players = null!, _bmu = null!;
    private ChipButton _start = null!;

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
            rows.AddChild(Setting("Free slots", Choice(["Play the AI", "Stay empty"], _lastFill, i => _lastFill = i), 180));
            rows.AddChild(Setting("Broadcast alliances", Choice(["Off", "On"], _lastBroadcast, i => _lastBroadcast = i), 180));
            rows.AddChild(Setting("Disable first person", Choice(["Off", "On"], _lastNoFirstPerson, i => _lastNoFirstPerson = i), 180));
        }

        var footer = Line(Gap);
        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(340, 76);
        footer.AddChild(back);
        footer.AddChild(Spring());
        _start = Button(room is not null ? "OK" : forRoom ? "Open room" : "Start", Start, 76, 30);
        _start.CustomMinimumSize = new Vector2(480, 76);
        footer.AddChild(_start);
        page.AddChild(footer);

        FillTree(ScenarioCatalogueParser.Load(System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat")));
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
            if (room is null)
                NetLink.Send(Room(_selected.Name, _selected.Planet, RoomSlots));
            else
                NetLink.Send(new ChangeRoom(_selected.Name, _selected.Planet, RoomSlots, Options(network: true), FillWithAi: _lastFill == 0));
            screens.ShowRoom();
            return;
        }
        var players = int.TryParse(_players.Text.Split(' ')[0], out var n) ? n : 2;
        var starts = StartPositions.Assign(_slots, players, Colours[_lastColour], random: _lastStarts == 1, new Random());
        MatchRequest.Current = new MatchRequest(_selected.Name, _selected.Planet, starts, Options(network: false));
        LoadingScreen.Show(GetTree(), _title.Text, $"{_selected.Planet.ToUpperInvariant()}   ·   {(players == 1 ? Lang.T("NO OPPONENT") : Lang.T("{0} PLAYERS", players))}");
    }

    /// <summary>The settings last chosen on the page; only a <paramref name="network"/> game has the last two.</summary>
    private static MatchOptions Options(bool network)
    {
        var victory = _lastVictory switch
        {
            0 => VictoryCondition.Default,
            1 => VictoryCondition.Annihilation,
            2 => VictoryCondition.Pod,
            _ => VictoryCondition.Timer,
        };
        return new MatchOptions
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
        };
    }

    /// <summary>The page shows the settings of the room it changes.</summary>
    private static void Adopt(RoomState room)
    {
        var options = room.Options;
        _lastMap = (room.Scenario, room.Planet);
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

    /// <summary>A room's settings in a line, for all in it to read (the ready room's list, MachGuiCtxImReady).</summary>
    public static string Summary(MatchOptions options)
    {
        static string Item(string name, string value) => $"{Lang.T(name)}: {Lang.T(value)}";
        static string Switch(string name, bool on) => Item(name, on ? "On" : "Off");
        var victory = options.Victory switch
        {
            VictoryCondition.Annihilation => "Annihilation",
            VictoryCondition.Pod => "Destroy pod",
            VictoryCondition.Timer => Lang.T("Timed: {0}", FormatMinutes(options.TimeLimit)),
            _ => "Default",
        };
        return string.Join("   ·   ",
            Item("Starting BMUs", MatchOptions.BmuFor(options.StartingBmu)?.ToString() ?? "Map default"),
            Item("Resources", options.Resources == ResourceLevel.Default ? "Map default" : options.Resources.ToString()),
            Item("Technology", options.TechLevel == TechLevel.Default ? "Map default" : options.TechLevel.ToString()),
            Item("Victory", victory),
            Item("AI", options.AiLevel.ToString()),
            Switch("Fog of war", options.FogOfWar),
            Switch("Broadcast alliances", options.BroadcastAlliances),
            Switch("Disable first person", options.DisableFirstPerson));
    }

    private static CreateRoom Room(string scenario, string planet, List<Race> slots) =>
        new(Lang.T("{0}'s game", NetLink.Name), scenario, planet, slots, Options(network: true), FillWithAi: _lastFill == 0);

    /// <summary>
    /// A room on the map last picked (or MACHINES_MENU_SELECT's, or the first skirmish map) with the settings last
    /// chosen, for the multiplayer page's automated checks.
    /// </summary>
    public static CreateRoom? LastRoom()
    {
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
