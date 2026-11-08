using Godot;
using Machines.Import.Model;
using Machines.Import.Scenarios;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// Hero creation: the hangar with the choices that come before a run. Name and colours stand over the values; difficulty,
/// arena and the start of the run on a chip below. It remembers the last hero. Wired is the remake's own addition.
/// </summary>
public static class HangarCreation
{
    private static readonly (Race Race, Color Colour)[] Colours =
    [
        (Race.Red, new Color(0.9f, 0.16f, 0.12f)),
        (Race.Blue, new Color(0.2f, 0.4f, 0.95f)),
        (Race.Green, new Color(0.2f, 0.75f, 0.25f)),
        (Race.Yellow, new Color(0.95f, 0.78f, 0.15f)),
    ];

    private const int ArrivalNames = 3;

    private static readonly WiredDifficulty[] Difficulties = Enum.GetValues<WiredDifficulty>();

    /// <summary>The hangar as the main menu opens it.</summary>
    public static HangarPage Page(MenuScreens screens)
    {
        // A run that was left waiting for the file gets another try, so that what it opened is here.
        if (WiredProfile.Runs.Waiting)
            WiredProfile.Runs.Write();
        var profile = WiredProfile.Load();
        var fresh = profile.TakeFresh();
        if (fresh.Count > 0)
            profile.Save();
        var desk = new CreationDesk(Paths.Data, screens.Assets, profile) { Fresh = fresh };
        var hangar = new HangarPage(desk, screens.ShowMain, page => Plate(page, desk), page => Bar(page, desk, screens));
        if (fresh.Count > 0)
            hangar.Announce(Arrived(fresh));
        return hangar;
    }

    /// <summary>What arrived, in a line that fits the banner.</summary>
    private static string Arrived(IReadOnlyCollection<string> fresh)
    {
        var names = string.Join(", ", fresh.Take(ArrivalNames).Select(HangarText.Opened));
        return fresh.Count > ArrivalNames ? Lang.T("New: {0} and {1} more", names, fresh.Count - ArrivalNames) : Lang.T("New: {0}", names);
    }

    /// <summary>The planet an arena lies on, by the original's list of scenarios.</summary>
    private static string Planet(string arena)
    {
        var catalogue = ScenarioCatalogueParser.Load(System.IO.Path.Combine(Paths.OriginalData, "sceninfo.dat"));
        return catalogue.Skirmish.Concat(catalogue.Multiplayer).SelectMany(t => t.Scenarios).FirstOrDefault(s => s.Name == arena)?.Planet ?? arena;
    }

    /// <summary>An arena's picture: the map rendered in the look, or the planet's of the game copy.</summary>
    public static Texture2D? Map(string arena, ClassicAssets assets) => MiniMaps.For(arena) ?? assets.PlanetMap(Planet(arena));

    /// <summary>The hero's name and colours.</summary>
    private static Control Plate(HangarPage page, CreationDesk desk)
    {
        var name = new LineEdit { Text = desk.Draft.Name, PlaceholderText = Lang.T("Name your hero"), MaxLength = 24, CustomMinimumSize = new Vector2(0, 50) };
        name.TextChanged += text => desk.Draft.Name = text;
        var lamps = new List<ColourLamp>();
        var row = MenuPage.Line(8);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        foreach (var (race, colour) in Colours)
        {
            var lamp = new ColourLamp { Colour = colour, On = race == desk.Draft.Colour };
            lamp.Pressed += () =>
            {
                desk.Draft.Colour = race;
                for (var i = 0; i < lamps.Count; i++)
                    lamps[i].On = Colours[i].Race == race;
                page.Recompose();
            };
            lamps.Add(lamp);
            row.AddChild(lamp);
        }
        return MenuPage.Column(8, name, row);
    }

    private static string Name(WiredDifficulty difficulty) => Lang.T(difficulty.ToString());

    /// <summary>The chip under it all: back, difficulty, arena, and off.</summary>
    private static Control Bar(HangarPage page, CreationDesk desk, MenuScreens screens)
    {
        var (draft, profile) = (desk.Draft, desk.Profile);
        var chip = new ChipPanel { Padding = 10 };
        var row = MenuPage.Line(18);

        var back = MenuPage.Button("Back", page.Back, 72, 26);
        back.CustomMinimumSize = new Vector2(190, 72);
        back.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(back);

        // All of them stand on the bar, those not open yet too: what they ask for is said when they come up, and the draft stays with the last open one.
        var arenas = WiredProfile.Arenas.Select(a => a.Scenario).ToList();
        var (pickedArena, pickedDifficulty) = (draft.Arena, draft.Difficulty);

        var difficulties = Difficulties.ToList();
        var difficulty = new ChipChoice(difficulties.Select(Name), difficulties.IndexOf(draft.Difficulty)) { CustomMinimumSize = new Vector2(210, 62), Closed = at => !profile.CanPlay(difficulties[at]) };
        var arena = new ChipChoice(arenas.Select(HangarText.Arena), Math.Max(0, arenas.IndexOf(draft.Arena))) { CustomMinimumSize = new Vector2(300, 62), Closed = at => !profile.CanPlay(arenas[at]) };
        ChipButton start = null!;
        var map = new TextureRect
        {
            CustomMinimumSize = new Vector2(84, 84),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
        };
        var record = MenuTheme.Label("", 18, MenuTheme.Dim);
        void ShowArena()
        {
            var closed = !profile.CanPlay(pickedArena) ? Lang.T(WiredProfile.Clear, WiredProfile.Arenas.First(a => a.Scenario == pickedArena).Wave)
                : !profile.CanPlay(pickedDifficulty) ? Lang.T(WiredProfile.ClearOnHard, WiredProfile.HardcoreWave) : null;
            map.Texture = Map(pickedArena, screens.Assets);
            map.Modulate = profile.CanPlay(pickedArena) ? Colors.White : new Color(0.3f, 0.3f, 0.3f);
            var best = profile.Record(pickedArena, pickedDifficulty);
            record.Text = closed ?? (best.Waves > 0 ? Lang.T("Record: wave {0}, {1} points", best.Waves, WiredReport.Figure(best.Score, Lang.Code == "de")) : Lang.T("No record yet"));
            record.AddThemeColorOverride("font_color", closed is null ? MenuTheme.Dim : HangarPage.Red);
            start.Disabled = closed is not null;
            arena.Tag = desk.Fresh.Contains(pickedArena) ? Lang.T("New") : null;
            difficulty.Tag = pickedDifficulty == WiredDifficulty.Hardcore && desk.Fresh.Contains(WiredProfile.Hardcore) ? Lang.T("New") : null;
            page.Bar = record.Text;
        }
        difficulty.ItemSelected += at =>
        {
            pickedDifficulty = difficulties[at];
            var gone = profile.CanPlay(pickedDifficulty) ? draft.Choose(pickedDifficulty) : [];
            ShowArena();
            if (gone.Count > 0)
                page.Refuse(Lang.T("Too dear at this difficulty, it went back: {0}", string.Join(", ", gone.Select(HangarText.Name))));
        };
        arena.ItemSelected += at =>
        {
            pickedArena = arenas[at];
            if (profile.CanPlay(pickedArena))
                draft.Arena = pickedArena;
            ShowArena();
        };

        var difficultyColumn = MenuPage.Column(4, MenuTheme.Label("Difficulty", 17, MenuTheme.Dim), difficulty);
        var arenaColumn = MenuPage.Column(4, MenuTheme.Label("Arena", 17, MenuTheme.Dim), arena);
        difficultyColumn.SizeFlagsVertical = arenaColumn.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(difficultyColumn);
        row.AddChild(arenaColumn);
        row.AddChild(map);
        record.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        record.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        row.AddChild(record);

        start = MenuPage.Button("Start the mission", () => Start(page, desk, HangarText.Arena(draft.Arena), Planet(draft.Arena)), 78, 30);
        start.CustomMinimumSize = new Vector2(370, 78);
        start.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(start);
        chip.AddChild(row);
        ShowArena();
        return chip;
    }

    /// <summary>The run begins: the hero is remembered, and what it chose goes into the match's options.</summary>
    private static void Start(HangarPage page, CreationDesk desk, string title, string planet)
    {
        var draft = desk.Draft;
        // A hero left unnamed stays so: the screens call it "Hero" in the language they are shown in.
        draft.Name = draft.Name.Trim();
        desk.Profile.LastHero = draft.Sheet;
        desk.Profile.Save();
        var slots = Paths.Data.LoadScenario(draft.Arena).Races.Select(r => r.Race).ToList();
        var starts = StartPositions.Assign(slots, slots.Count, draft.Colour, random: false, new Random());
        // The arena is known ground: what is unknown is what the waves bring.
        MatchRequest.Current = new MatchRequest(draft.Arena, planet, starts, new MatchOptions { Wired = draft.Options, FogOfWar = false });
        LoadingScreen.Show(page.GetTree(), title, $"{Lang.T("Wired")}   ·   {Name(draft.Difficulty).ToUpperInvariant()}");
    }
}
