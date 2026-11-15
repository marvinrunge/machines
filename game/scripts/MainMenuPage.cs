using Godot;

namespace Machines.Game;

/// <summary>
/// The main menu (MachGuiCtxMainMenu): the title on the left with Exit under it, as in the original, the menu's
/// buttons down the right, and between them the big chip with a machine turning on its screen. Esc asks whether to
/// quit.
/// </summary>
public partial class MainMenuPage(MenuScreens screens) : MenuPage(screens.Quit)
{
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
        var status = new ChipPanel { Padding = 16 };
        status.AddChild(MenuTheme.Label(
            Lang.T("Look: {0}", Lang.T(GraphicsSettings.Name(GraphicsSettings.Look))) + "\n" +
            Lang.T(ClassicImport.Imported ? "Models imported from your game copy" : "Models not imported yet"), 21, MenuTheme.Dim));
        left.AddChild(status);
        left.AddChild(Button("Exit", screens.Quit, 84, 30));

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

        // Nine buttons of this size stand no higher than the eight before them did (740 against 748), so the page still fits at a large interface size.
        const float height = 68;
        var right = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0), Alignment = BoxContainer.AlignmentMode.Center };
        right.AddThemeConstantOverride("separation", 16);
        row.AddChild(right);
        var campaign = Button("Campaign", screens.ShowCampaign, height, 30);
        right.AddChild(campaign);
        var skirmish = Button("Skirmish", () => screens.ShowSkirmish(), height, 30);
        right.AddChild(skirmish);
        // A battle that had to wait for the file is written now, as Load writes Wired's waiting runs, so its tag is right.
        if (BattleAccounts.Ledger.Waiting)
            BattleAccounts.Ledger.Write();
        var profile = WiredProfile.Load();
        var wired = Button("Wired", screens.ShowHangar, height, 30);
        if (profile.Fresh.Count is > 0 and var fresh)
            wired.Tag = Lang.T("{0} new", fresh);
        right.AddChild(wired);
        var battle = Button("Battle", screens.ShowPod, height, 30);
        if (profile.Battle.Fresh.Count is > 0 and var within)
            battle.Tag = Lang.T("{0} new", within);
        right.AddChild(battle);
        right.AddChild(Button("Multiplayer", screens.ShowMultiplayer, height, 30));
        right.AddChild(Button("Load Game", screens.ShowLoad, height, 30));
        right.AddChild(Button("Replays", screens.ShowReplays, height, 30));
        right.AddChild(Button("Models", screens.ShowModels, height, 30));
        right.AddChild(Button("Options", screens.ShowOptions, height, 30));

        SwitchOn();
        Focus(skirmish);
    }
}
