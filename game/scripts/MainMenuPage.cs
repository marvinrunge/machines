using Godot;

namespace Machines.Game;

/// <summary>
/// The main menu (MachGuiCtxMainMenu): the title on the left with Exit under it, as in the original, the menu's
/// buttons down the right, and between them the big chip with a machine turning on its screen. The campaign comes when
/// the remake can play it. Esc asks whether to quit.
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
            $"Look: {GraphicsSettings.Name(GraphicsSettings.Look)}\n" +
            (ClassicImport.Imported ? "Models imported from your game copy" : "Models not imported yet"), 21, MenuTheme.Dim));
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

        var right = new VBoxContainer { CustomMinimumSize = new Vector2(360, 0), Alignment = BoxContainer.AlignmentMode.Center };
        right.AddThemeConstantOverride("separation", 26);
        row.AddChild(right);
        var campaign = Button("Campaign", () => { }, 84, 30);
        campaign.Disabled = true;
        campaign.TooltipText = "Comes when the remake can play the campaign.";
        right.AddChild(campaign);
        var skirmish = Button("Skirmish", () => screens.ShowSkirmish(), 84, 30);
        right.AddChild(skirmish);
        right.AddChild(Button("Multiplayer", screens.ShowMultiplayer, 84, 30));
        right.AddChild(Button("Load Game", screens.ShowLoad, 84, 30));
        right.AddChild(Button("Replays", screens.ShowReplays, 84, 30));
        right.AddChild(Button("Options", screens.ShowOptions, 84, 30));

        SwitchOn();
        Focus(skirmish);
    }
}
