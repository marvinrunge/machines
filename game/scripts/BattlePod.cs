using Godot;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The pod of Battle as the main menu opens it: the hangar's screen over a <see cref="BattleDesk"/>, with the hero's name,
/// colours, tier and XP over its values, and below it all the battle to fight (map, lives, victory) and the way in: against
/// bots on this machine, or a room in Multiplayer. Battle is the remake's own addition.
/// </summary>
public static class BattlePod
{
    private static readonly BattleWin[] Victories = [BattleWin.Both, BattleWin.Wipe, BattleWin.Capture];

    /// <summary>
    /// The pod, on the shelf asked for. For a battle <paramref name="room"/> it goes back to the room, shows the room's battle
    /// instead of offering one, and its way in is Ready.
    /// </summary>
    public static HangarPage Page(MenuScreens screens, HangarShelf shelf = HangarShelf.Weapons, RoomState? room = null)
    {
        var account = BattleAccounts.Load();
        var fresh = account.TakeFresh();
        if (fresh.Count > 0)
            account.Save();
        var desk = new BattleDesk(Paths.Data, screens.Assets, account) { Fresh = fresh, First = shelf };
        void Back()
        {
            desk.Draft.Keep();
            if (room is null)
            {
                screens.ShowMain();
                return;
            }
            MultiplayerPage.FromPod(ready: false);
            screens.ShowRoom();
        }
        var pod = new HangarPage(desk, Back, page => Plate(page, desk),
            page => room?.Options.Battle is { } battle ? RoomBar(page, desk, screens, room, battle) : Bar(page, desk, screens));
        if (fresh.Count > 0)
            pod.Announce(Lang.T("Within reach: {0}", string.Join(", ", fresh.Select(HangarText.Name))));
        return pod;
    }

    /// <summary>A battle map's name in the player's language (de.po has it under the context arena).</summary>
    public static string MapName(BattleMap map) => TranslationServer.Translate(map.Name, "arena");

    /// <summary>A battle map's picture: the map rendered in the look, or the planet's of the game copy.</summary>
    public static Texture2D? MapPicture(BattleMap map, ClassicAssets assets) => MiniMaps.For(map.Scenario) ?? assets.PlanetMap(map.Planet);

    /// <summary>How a battle is won, in words.</summary>
    public static string VictoryName(BattleWin win) => Lang.T(win switch
    {
        BattleWin.Wipe => "Wipe out",
        BattleWin.Capture => "Take the pod",
        _ => "Wipe out or take the pod",
    });

    /// <summary>What a battle asks of the player, in a sentence: the opening's goal line and the briefing's.</summary>
    public static string Goal(BattleWin win) => win switch
    {
        BattleWin.Wipe => Lang.T("Destroy the enemy team"),
        BattleWin.Capture => Lang.T("Take the enemy pod: stand in its circle"),
        _ => Lang.T("Destroy the enemy team or take their pod"),
    };

    /// <summary>
    /// The in-game menu's briefing for a battle (the scenario's own text is a skirmish's): the map, the bracket, lives and
    /// victory, the teams and the goal.
    /// </summary>
    public static string Briefing(BattleOptions battle, string scenario)
    {
        var map = BattleCatalogue.Map(scenario);
        var (top, bottom) = (battle.Tier, BattleCatalogue.BracketBottom(battle.Tier));
        var bracket = top == bottom ? BattleDraft.Roman(top) : $"{BattleDraft.Roman(bottom)}–{BattleDraft.Roman(top)}";
        var lines = new List<string>
        {
            map is null ? scenario : MapName(map),
            $"{Lang.T("Tier {0}", bracket)}   ·   {LivesName(battle.Lives)}   ·   {VictoryName(battle.Win)}",
        };
        if (map is not null)
            lines.Add(SkirmishPage.Teams(map));
        lines.Add(Goal(battle.Win));
        return string.Join("\n\n", lines);
    }

    public static string LivesName(int lives) => lives == 1 ? Lang.T("1 life") : Lang.T("{0} lives", lives);

    /// <summary>The hero's name, colours, tier and XP.</summary>
    private static Control Plate(HangarPage page, BattleDesk desk)
    {
        var draft = desk.Draft;
        var name = new LineEdit { Text = draft.Name, PlaceholderText = Lang.T("Name your hero"), MaxLength = BattleHero.MostName, CustomMinimumSize = new Vector2(0, 50) };
        name.TextChanged += text => draft.Name = text;
        var lamps = new List<ColourLamp>();
        var row = MenuPage.Line(8);
        row.Alignment = BoxContainer.AlignmentMode.Center;
        foreach (var (race, colour) in HangarCreation.Colours)
        {
            var lamp = new ColourLamp { Colour = colour, On = race == draft.Colour };
            lamp.Pressed += () =>
            {
                draft.Colour = race;
                for (var i = 0; i < lamps.Count; i++)
                    lamps[i].On = HangarCreation.Colours[i].Race == race;
                page.Recompose();
            };
            lamps.Add(lamp);
            row.AddChild(lamp);
        }
        return MenuPage.Column(8, name, row, new BattlePlate(desk));
    }

    /// <summary>The chip under it all: back, the battle to fight, and the two ways into one.</summary>
    private static Control Bar(HangarPage page, BattleDesk desk, MenuScreens screens)
    {
        var draft = desk.Draft;
        var chip = new ChipPanel { Padding = 10 };
        // Close set, so that the bar fits the large interface with the longest German words (map, victory and bracket).
        var row = MenuPage.Line(4);

        var back = MenuPage.Button("Back", page.Back, 72, 26);
        back.CustomMinimumSize = new Vector2(116, 72);
        back.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(back);

        var maps = BattleCatalogue.Maps.ToList();
        var map = new ChipChoice(maps.Select(MapName), Math.Max(0, maps.FindIndex(m => m.Scenario == draft.Map))) { CustomMinimumSize = new Vector2(250, 62) };
        var picture = new TextureRect
        {
            CustomMinimumSize = new Vector2(54, 54),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
        };
        var lives = new ChipChoice(Enumerable.Range(1, 5).Select(LivesName), draft.Lives - 1) { CustomMinimumSize = new Vector2(150, 62) };
        var victory = new ChipChoice(Victories.Select(VictoryName), Math.Max(0, Array.IndexOf(Victories, draft.Win))) { CustomMinimumSize = new Vector2(250, 62) };
        var bracket = MenuTheme.Label("", 21, MenuTheme.Text);
        bracket.HorizontalAlignment = HorizontalAlignment.Center;
        bracket.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        // As high as the choices beside it, so that its title stands in line with theirs.
        bracket.CustomMinimumSize = new Vector2(64, 62);
        bracket.VerticalAlignment = VerticalAlignment.Center;
        // A little room before it, so that a long bracket ("VII–VIII") does not touch the victory chooser's arrow.
        bracket.AddThemeStyleboxOverride("normal", new StyleBoxEmpty { ContentMarginLeft = 16 });

        void Show()
        {
            var chosen = BattleCatalogue.Map(draft.Map)!;
            picture.Texture = MapPicture(chosen, screens.Assets);
            var (top, bottom) = (draft.Tier, BattleCatalogue.BracketBottom(draft.Tier));
            bracket.Text = top == bottom ? BattleDraft.Roman(top) : $"{BattleDraft.Roman(bottom)}–{BattleDraft.Roman(top)}";
            page.Bar = $"{MapName(chosen)} · {LivesName(draft.Lives)} · {VictoryName(draft.Win)} · {Lang.T("Tier {0}", bracket.Text)}";
        }
        map.ItemSelected += at =>
        {
            draft.Map = maps[at].Scenario;
            Show();
        };
        lives.ItemSelected += at => draft.Lives = at + 1;
        victory.ItemSelected += at => draft.Win = Victories[at];
        lives.ItemSelected += _ => Show();
        victory.ItemSelected += _ => Show();

        Control Titled(string title, Control choice, HorizontalAlignment align = HorizontalAlignment.Left)
        {
            var label = MenuTheme.Label(title, 17, MenuTheme.Dim);
            label.HorizontalAlignment = align;
            var column = MenuPage.Column(4, label, choice);
            column.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return column;
        }
        row.AddChild(Titled("Map", map));
        row.AddChild(picture);
        row.AddChild(Titled("Lives", lives));
        row.AddChild(Titled("Victory", victory));
        row.AddChild(Titled("Bracket", bracket, HorizontalAlignment.Center));

        // The two ways into a battle stand one above the other, so that the bar fits the large interface and long German words.
        var fight = MenuPage.Button("Fight against bots", () => Start(page, desk), 40, 19);
        fight.CustomMinimumSize = new Vector2(230, 40);
        var online = MenuPage.Button("Multiplayer", () =>
        {
            draft.Keep();
            // Its Back comes back here, where the player came from.
            screens.ShowMultiplayer(() => screens.ShowPod());
        }, 40, 19);
        online.CustomMinimumSize = new Vector2(230, 40);
        var ways = MenuPage.Column(8, fight, online);
        ways.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        ways.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd | Control.SizeFlags.Expand;
        row.AddChild(ways);
        // The bracket is topped by the worn chassis' tier, which changes on the shelves and the Path above.
        row.AddChild(new PodWatch(() => draft.Tier, Show));
        chip.AddChild(row);
        Show();
        return chip;
    }

    /// <summary>The chip under it all in a battle room: back to the room, the room's battle as its host set it, and Ready.</summary>
    private static Control RoomBar(HangarPage page, BattleDesk desk, MenuScreens screens, RoomState room, BattleOptions battle)
    {
        var chip = new ChipPanel { Padding = 10 };
        var row = MenuPage.Line(18);

        var back = MenuPage.Button("Back", page.Back, 72, 26);
        back.CustomMinimumSize = new Vector2(130, 72);
        back.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        row.AddChild(back);

        var map = BattleCatalogue.Map(room.Scenario);
        var mapName = map is null ? room.Scenario : MapName(map);
        Control Titled(string title, string value, float width)
        {
            var label = MenuTheme.Label(title, 17, MenuTheme.Dim);
            var text = MenuTheme.Label(value, 22, MenuTheme.Text);
            text.AutoTranslateMode = Node.AutoTranslateModeEnum.Disabled;
            // Only the room's name, which a player chose, is cut to its room; the rest are the game's own words.
            text.ClipText = title == "Room";
            text.CustomMinimumSize = new Vector2(width, 0);
            var column = MenuPage.Column(6, label, text);
            column.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
            return column;
        }
        row.AddChild(Titled("Room", room.Name, 260));
        if (map is not null)
            row.AddChild(new TextureRect
            {
                Texture = MapPicture(map, screens.Assets),
                CustomMinimumSize = new Vector2(54, 54),
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            });
        row.AddChild(Titled("Map", mapName, 190));
        row.AddChild(Titled("Lives", LivesName(battle.Lives), 90));
        row.AddChild(Titled("Victory", VictoryName(battle.Win), 230));
        var (top, bottom) = (battle.Tier, BattleCatalogue.BracketBottom(battle.Tier));
        var bracket = top == bottom ? BattleDraft.Roman(top) : $"{BattleDraft.Roman(bottom)}–{BattleDraft.Roman(top)}";
        row.AddChild(Titled("Bracket", bracket, 70));
        page.Bar = $"{room.Name} · {mapName} · {LivesName(battle.Lives)} · {VictoryName(battle.Win)} · {Lang.T("Tier {0}", bracket)}";

        var ready = MenuPage.Button("Ready", () =>
        {
            var draft = desk.Draft;
            if (RoomOptions.WhyNotHere(draft.Hero(Race.Red), battle.Tier) is { } why)
            {
                page.Refuse(why);
                return;
            }
            draft.Keep();
            MultiplayerPage.FromPod(ready: true);
            screens.ShowRoom();
        }, 72, 26);
        ready.CustomMinimumSize = new Vector2(230, 72);
        ready.SizeFlagsVertical = Control.SizeFlags.ShrinkCenter;
        ready.SizeFlagsHorizontal = Control.SizeFlags.ShrinkEnd | Control.SizeFlags.Expand;
        row.AddChild(ready);
        chip.AddChild(row);
        return chip;
    }

    /// <summary>A battle room for MACHINES_MENU_SCREENSHOT's pod-room: Canyon Attack, tier II, two lives.</summary>
    public static RoomState SampleRoom() => new(1, Lang.T("{0}'s battle", "Host"), "m_desert", "m_desert", [Race.Red, Race.Blue, Race.Green, Race.Yellow],
        new MatchOptions { Victory = VictoryCondition.Battle, FogOfWar = false, Battle = new BattleOptions { Tier = 2, Lives = 2 } },
        true, 0, [], [new RoomSeat(null, SeatUse.Open), new RoomSeat(null, SeatUse.Open), new RoomSeat(null, SeatUse.Computer), new RoomSeat(null, SeatUse.Computer)]);

    /// <summary>
    /// The battle against the computer begins: the hero and the battle chosen are remembered, and the player's hero with three
    /// bots by the mirror rule goes into the match's options, the bracket topped by the worn chassis' tier.
    /// </summary>
    private static void Start(HangarPage page, BattleDesk desk)
    {
        var draft = desk.Draft;
        if (draft.WhyNotFight() is { } why)
        {
            page.Refuse(BattleDesk.Say(why));
            return;
        }
        draft.Keep();
        var map = BattleCatalogue.Map(draft.Map)!;
        // A hero not named yet fights under the player's name, as a room's relay names it.
        MatchRequest.Current = MatchRequest.ForBattle(map, draft.Options(unnamed: NetLink.OfflineName), BattleDraft.PlayerRace(map, draft.Colour), watch: false);
        LoadingScreen.Show(page.GetTree(), Title(map), Detail(draft.Tier, draft.Lives, draft.Win));
    }

    /// <summary>The loading screen's title for a battle on a map.</summary>
    public static string Title(BattleMap map) => Lang.T("Battle · {0}", MapName(map));

    /// <summary>The loading screen's line under it: tier, lives and victory.</summary>
    public static string Detail(int tier, int lives, BattleWin win) =>
        $"{Lang.T("Tier {0}", BattleDraft.Roman(tier))}   ·   {LivesName(lives)}   ·   {VictoryName(win)}".ToUpperInvariant();
}

/// <summary>
/// The worn chassis on the pod's plate: its tier on a badge beside its name, the XP it has earned, and a bar of how far the
/// XP that pays it goes towards the next chassis on its line.
/// </summary>
public partial class BattlePlate(BattleDesk desk) : Control
{
    public override void _Ready()
    {
        CustomMinimumSize = new Vector2(0, 84);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Process(double delta) => QueueRedraw();

    public override void _Draw()
    {
        var draft = desk.Draft;
        var account = desk.Account;
        var font = MenuTheme.Font;
        var tier = Lang.T("Tier {0}", BattleDraft.Roman(draft.Tier)).ToUpperInvariant();
        var badge = font.GetStringSize(tier, HorizontalAlignment.Left, -1, 17).X + 14;
        var plate = new Rect2(0, 2, badge, 26);
        DrawRect(plate, new Color(MenuTheme.Bright, 0.9f));
        DrawString(font, new Vector2(7, 21), tier, HorizontalAlignment.Left, -1, 17, MenuTheme.OnLight);
        HangarText.Line(this, new Vector2(badge + 10, 22), HangarText.Name(draft.Chassis).ToUpperInvariant(), Size.X - badge - 10, 21, MenuTheme.Bright);

        var xp = account.Xp(draft.Chassis);
        HangarText.Line(this, new Vector2(0, 50), Lang.T("{0} XP · {1} free XP", xp, account.FreeXp), Size.X, 16, MenuTheme.Text);
        var bar = new Rect2(0, 58, Size.X, 10);
        DrawRect(bar, MenuTheme.Well);
        if (draft.Next() is { } next)
        {
            var (have, need) = (draft.XpTowards(next), BattleRules.ResearchCost(next));
            var share = need <= 0 ? 1 : Mathf.Clamp(have / (float)need, 0, 1);
            DrawRect(new Rect2(bar.Position, new Vector2(bar.Size.X * share, bar.Size.Y)), share >= 1 ? MenuTheme.Bright : MenuTheme.Glow.Darkened(0.15f));
            DrawRect(bar, MenuTheme.Edge, false, 1);
            HangarText.Line(this, new Vector2(0, 83), Lang.T("{0} / {1} XP to {2}", Math.Min(have, need), need, HangarText.Name(next)), Size.X, 15, MenuTheme.Dim.Lightened(0.25f));
        }
        else
        {
            DrawRect(new Rect2(bar.Position, bar.Size), MenuTheme.Glow.Darkened(0.15f));
            DrawRect(bar, MenuTheme.Edge, false, 1);
            // Only a chassis that leads nowhere is the top of its line; one whose followers are all researched is not.
            HangarText.Line(this, new Vector2(0, 83), Lang.T(draft.Leads ? "Everything after it is researched" : "The top of its line"), Size.X, 15, MenuTheme.Dim.Lightened(0.25f));
        }
    }
}

/// <summary>Calls <paramref name="changed"/> whenever what <paramref name="read"/> reads changes, checked every frame.</summary>
public partial class PodWatch(Func<int> read, Action changed) : Node
{
    private int _last = read();

    public override void _Process(double delta)
    {
        var now = read();
        if (now == _last)
            return;
        _last = now;
        changed();
    }
}
