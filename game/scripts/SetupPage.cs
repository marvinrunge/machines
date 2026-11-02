using Godot;

namespace Machines.Game;

/// <summary>
/// The first start's few questions, one page each, before the main menu: the import from the player's own copy of the
/// game (without it there are no models to choose a look by), the look, Classic or Overhauled, each shown on the same
/// machines turning side by side, and the soundtrack's folder. Each can be left for later, and the options change
/// every answer at any time. They come once, until <see cref="Done"/> is kept in <c>user://settings.cfg</c>; an
/// import reopens the menus, the intro films play, and the questions go on with the look.
/// MACHINES_MENU_SCREENSHOT=&lt;png&gt;,setup (or setup-import, setup-look, setup-music) shows them.
/// </summary>
public partial class SetupPage(MenuScreens screens, SetupPage.Step step)
    : MenuPage(step == SetupPage.Step.Music ? () => screens.ShowSetup(SetupPage.Step.Look) : null)
{
    public enum Step
    {
        Import,
        Look,
        Music,
    }

    private const string Settings = "user://settings.cfg";

    private bool _importing;

    /// <summary>Whether the first start's questions have been answered or left.</summary>
    public static bool Done
    {
        get
        {
            var config = new ConfigFile();
            return config.Load(Settings) == Error.Ok && (bool)config.GetValue("setup", "done", false);
        }
        set
        {
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("setup", "done", value);
            config.Save(Settings);
        }
    }

    /// <summary>The first question to ask: the import where this build can run it and it has not run, else the look.</summary>
    public static Step First => ClassicImport.Available && !ClassicImport.Imported ? Step.Import : Step.Look;

    public override void Back()
    {
        if (!_importing)
            base.Back();
    }

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        switch (step)
        {
            case Step.Import: ImportStep(page); break;
            case Step.Look: LookStep(page); break;
            default: MusicStep(page); break;
        }
        SwitchOn();
    }

    /// <summary>
    /// The step's heading, and on the first two the language beside it (at first the system's, see <see cref="Lang"/>);
    /// another builds the page again in it.
    /// </summary>
    private Control Top(string heading)
    {
        if (step == Step.Music)
            return Heading(heading);
        var language = new ChipChoice(Lang.Names, Array.IndexOf(Lang.Codes, Lang.Code)) { CustomMinimumSize = new Vector2(300, 64) };
        language.ItemSelected += i =>
        {
            Lang.Code = Lang.Codes[i];
            screens.ShowSetup(step);
        };
        language.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        return Line(Gap, Heading(heading), Spring(), language);
    }

    /// <summary>A chip with the step's text, as wide as the page.</summary>
    private static ChipPanel Words(params string[] paragraphs)
    {
        var chip = new ChipPanel { Padding = 28, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        chip.AddChild(Column(16, paragraphs.Select(text => (Control)MenuTheme.Paragraph(text, 24, MenuTheme.Text)).ToArray()));
        return chip;
    }

    private void ImportStep(VBoxContainer page)
    {
        page.AddChild(Top("Welcome"));
        page.AddChild(Words(
            "Machines Remake rebuilds Machines: Wired for War (1999). It brings nothing of the original along: its models, " +
            "planets, sounds and films come from your own copy of the game (GOG or Steam).",
            "Pick the folder Machines is installed in, the one that holds models.bin. The import takes a few minutes and " +
            "makes the remake's sharper textures on your graphics card as it goes."));
        var status = MenuTheme.Paragraph("Without the import the machines show as simple shapes; the options can import later.", 22, MenuTheme.Text);
        page.AddChild(status);
        page.AddChild(Spring(across: false));

        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Pick the folder Machines is installed in (it holds models.bin)",
            UseNativeDialog = true,
        };
        AddChild(dialog);
        var import = Button("Import", () => dialog.PopupCentered(new Vector2I(900, 600)));
        var later = Button("Later", () => screens.ShowSetup(Step.Look));
        dialog.DirSelected += folder => OptionsPage.RunImport(this, folder, status, busy =>
        {
            import.Disabled = later.Disabled = _importing = busy;
            status.AddThemeColorOverride("font_color", busy ? MenuTheme.Text : MenuTheme.Bright);
        });
        import.CustomMinimumSize = later.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, import, later));
        Focus(import);
    }

    private void LookStep(VBoxContainer page)
    {
        page.AddChild(Top("Choose a look"));
        page.AddChild(MenuTheme.Paragraph(
            "Both play the same game. The options change the look at any time, and under Customize part by part; " +
            "a look with newly made HD models follows once they are finished.", 24, MenuTheme.Text));

        var cards = Line(Gap);
        cards.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(cards);
        var first = MachineDisplay.AnyFirst();
        var classic = Card(cards, Look.Classic, first, "Classic",
            "The original as it was in 1999: its models, its tiled ground, its light and its effects.");
        var overhauled = Card(cards, Look.Overhauled, first, "Overhauled · recommended",
            "The original sharpened: textures upscaled with relief, soft shadows and glow, photographed skies, " +
            "new explosions and smoke, weather, trees and rocks.");
        Focus(GraphicsSettings.Look == Look.Classic ? classic : overhauled);
    }

    /// <summary>One look to choose: its machines turning on a screen, what it is, and the button that takes it.</summary>
    private ChipButton Card(HBoxContainer cards, Look look, int first, string title, string text)
    {
        var parts = GraphicsParts.Of(look, GraphicsParts.Overhauled);
        var screen = new MachineDisplay(new ClassicAssets(Paths.Root, parts.Models), first)
        {
            Classic = !parts.Lighting,
            Zoom = 1.1f,
            CustomMinimumSize = new Vector2(0, 400),
        };
        var display = new ChipPanel { Padding = 4, SizeFlagsVertical = SizeFlags.ExpandFill };
        display.AddChild(screen);
        if (!ClassicImport.Imported)
        {
            var empty = MenuTheme.Label("No models imported", 24, MenuTheme.Dim, HorizontalAlignment.Center);
            empty.SetAnchorsAndOffsetsPreset(LayoutPreset.Center);
            screen.AddChild(empty);
        }
        var button = Button(look == Look.Classic ? "Play classic" : "Play overhauled", () =>
        {
            GraphicsSettings.Look = look;
            screens.ShowSetup(Step.Music);
        });
        // Room for three lines of text on both cards, so that their screens stay the same size in either language.
        var words = MenuTheme.Paragraph(text, 22, MenuTheme.Text);
        words.CustomMinimumSize = new Vector2(0, 116);
        var card = new ChipPanel { Padding = 22, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        card.AddChild(Column(18,
            display,
            MenuTheme.Label(Lang.T(title).ToUpperInvariant(), 30, MenuTheme.Bright),
            words,
            button));
        cards.AddChild(card);
        return button;
    }

    private void MusicStep(VBoxContainer page)
    {
        page.AddChild(Top("Music"));
        page.AddChild(Words(
            "The remake plays the original soundtrack from MP3s of your own, as the original played its CD: the intro, " +
            "the menu tune, each planet's own track and the victory and defeat tunes.",
            "Pick the folder that holds them (Track 00 - Intro.mp3 and the others). Without it the game plays without music."));
        static string Tracks(string? folder) => Music.TrackCount(folder) is var count and > 0
            ? Lang.T("{0} tracks found.", count)
            : Lang.T("No soundtrack picked yet.");
        var tracks = MenuTheme.Label(Tracks(Music.Folder), 24, MenuTheme.Bright);
        page.AddChild(tracks);
        page.AddChild(Spring(across: false));

        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Pick the folder with the soundtrack (Track 00 - Intro.mp3 ...)",
            UseNativeDialog = true,
        };
        AddChild(dialog);
        dialog.DirSelected += folder =>
        {
            Music.Folder = folder;
            tracks.Text = Tracks(folder);
        };
        var pick = Button("Pick folder", () => dialog.PopupCentered(new Vector2I(900, 600)));
        var finish = Button("Finish", () =>
        {
            Done = true;
            screens.ShowMain();
        });
        var back = Button("Back", Back);
        pick.CustomMinimumSize = finish.CustomMinimumSize = back.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, back, pick, finish));
        Focus(Music.Folder is null ? pick : finish);
    }
}
