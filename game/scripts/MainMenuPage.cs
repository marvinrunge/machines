using Godot;

namespace Machines.Game;

/// <summary>
/// The main menu (MachGuiCtxMainMenu) and the two menus it leads to in the same layout: the title on the left with Exit
/// (or Back) under it, as in the original, the menu's buttons down the right, and between them the big chip with a
/// machine turning on its screen. The main menu holds Single Player, Multiplayer, Load Game, Extras and Options; Single
/// Player (MachGuiCtxSinglePlayer) the story, the campaign, skirmish, Wired and Battle; Extras the replays and the
/// models. Esc asks whether to quit, or goes back to the main menu.
/// </summary>
public partial class MainMenuPage(MenuScreens screens, MainMenuPage.Menu menu = MainMenuPage.Menu.Main)
    : MenuPage(menu == MainMenuPage.Menu.Main ? screens.Quit : screens.ShowMain)
{
    public enum Menu { Main, SinglePlayer, Extras }

    /// <summary>Which of the three menus this is.</summary>
    public Menu Kind => menu;

    // Buttons of this size: five or six of them stand well inside the stage at the largest interface size.
    private const float ButtonHeight = 68;

    protected override void Build()
    {
        var row = Line(48);
        AddChild(row);

        var left = new VBoxContainer { CustomMinimumSize = new Vector2(380, 0) };
        left.AddThemeConstantOverride("separation", 28);
        row.AddChild(left);
        var title = new ChipPanel { Padding = 26 };
        var titleLines = Column(-6,
            MenuTheme.Label("MACHINES", 76, MenuTheme.Bright, HorizontalAlignment.Center),
            MenuTheme.Label("REMAKE", 26, MenuTheme.Text, HorizontalAlignment.Center));
        title.AddChild(titleLines);
        left.AddChild(title);
        left.AddChild(Spring(across: false));
        if (menu == Menu.Main)
        {
            var status = new ChipPanel { Padding = 16 };
            status.AddChild(MenuTheme.Label(
                Lang.T("Look: {0}", Lang.T(GraphicsSettings.Name(GraphicsSettings.Look))) + "\n" +
                Lang.T(DiscCheck.Bundled ? "Everything included, your CD checked"
                    : ClassicImport.Imported ? "Models imported from your game copy" : "Models not imported yet"), 21, MenuTheme.Dim));
            left.AddChild(status);
        }
        left.AddChild(Button(menu == Menu.Main ? "Exit" : "Back", menu == Menu.Main ? screens.Quit : Back, 84, 30));

        var display = new ChipPanel { Padding = 4, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var screen = new MachineDisplay(screens.Assets);
        var caption = MenuTheme.Label("", 24, MenuTheme.Text, HorizontalAlignment.Right);
        caption.SizeFlagsVertical = SizeFlags.ShrinkEnd;
        var layers = new Control();
        layers.AddChild(screen);
        screen.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var captionBox = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
        captionBox.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        captionBox.AddThemeConstantOverride("margin_right", 18);
        captionBox.AddThemeConstantOverride("margin_bottom", 10);
        captionBox.AddChild(caption);
        layers.AddChild(captionBox);
        screen.Changed += () => caption.Text = screen.Caption.ToUpperInvariant();
        display.AddChild(layers);
        row.AddChild(display);

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0), Alignment = BoxContainer.AlignmentMode.Center };
        right.AddThemeConstantOverride("separation", 14);
        row.AddChild(right);
        // Below the main menu, the menu's name over its buttons, so the player knows where they are.
        if (menu != Menu.Main)
        {
            var heading = MenuTheme.Label(Lang.T(menu == Menu.SinglePlayer ? "Single Player" : "Extras").ToUpperInvariant(), 34, MenuTheme.Bright, HorizontalAlignment.Center);
            heading.CustomMinimumSize = new Vector2(0, 56);
            right.AddChild(heading);
        }
        // A battle that had to wait for the file is written now, as Load writes Wired's waiting runs, so the tags are right.
        if (BattleAccounts.Ledger.Waiting)
            BattleAccounts.Ledger.Write();
        var first = menu switch
        {
            Menu.SinglePlayer => SinglePlayer(right),
            Menu.Extras => Extras(right),
            _ => Main(right),
        };

        SwitchOn();
        Focus(first);
    }

    private ChipButton Add(VBoxContainer column, string text, Action pressed)
    {
        var button = Button(text, pressed, ButtonHeight, 30);
        column.AddChild(button);
        return button;
    }

    private ChipButton Main(VBoxContainer right)
    {
        var single = Add(right, "Single Player", screens.ShowSinglePlayer);
        // What is new in the single player's modes shows on its button too.
        var profile = WiredProfile.Load();
        if (StoryProgress.Load().Chapter is null)
            single.Tag = Lang.T("New");
        else if (profile.Fresh.Count + profile.Battle.Fresh.Count is > 0 and var fresh)
            single.Tag = Lang.T("{0} new", fresh);
        Add(right, "Multiplayer", screens.ShowMultiplayer);
        Add(right, "Load Game", screens.ShowLoad);
        Add(right, "Extras", screens.ShowExtras);
        Add(right, "Options", screens.ShowOptions);
        return single;
    }

    private ChipButton SinglePlayer(VBoxContainer right)
    {
        // The story first: it is where a new player begins.
        var story = Add(right, "Story", screens.ShowStory);
        if (StoryProgress.Load().Chapter is null)
            story.Tag = Lang.T("New");
        Add(right, "Campaign", screens.ShowCampaign);
        Add(right, "Skirmish", () => screens.ShowSkirmish());
        var profile = WiredProfile.Load();
        var wired = Add(right, "Wired", screens.ShowHangar);
        if (profile.Fresh.Count is > 0 and var fresh)
            wired.Tag = Lang.T("{0} new", fresh);
        var battle = Add(right, "Battle", screens.ShowPod);
        if (profile.Battle.Fresh.Count is > 0 and var within)
            battle.Tag = Lang.T("{0} new", within);
        return story;
    }

    private ChipButton Extras(VBoxContainer right)
    {
        var replays = Add(right, "Replays", screens.ShowReplays);
        Add(right, "Models", screens.ShowModels);
        return replays;
    }
}
