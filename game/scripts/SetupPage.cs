using Godot;

namespace Machines.Game;

/// <summary>
/// The first start's few questions, one page each, before the main menu: the import from the player's own copy of the
/// game (without it there are no models to choose a look by), the look, Classic or Overhauled, each shown on the same
/// machines turning side by side, and the soundtrack (with the classic look the original's folder, with the remake's
/// its own music). Each can be left for later, and the options change every answer at any time. They come once,
/// until <see cref="Done"/> is kept in <c>user://settings.cfg</c>; an import reopens the menus, the intro films play,
/// and the questions go on with the look.
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

    /// <summary>
    /// The import, from the copy <see cref="GameLocator"/> found on this computer with one button, or from a folder
    /// the player picks (where none was found, or another).
    /// </summary>
    private void ImportStep(VBoxContainer page)
    {
        // An installed copy first, else the CD in a drive or a disc image of it.
        var installed = GameLocator.Find().FirstOrDefault();
        var disc = installed is null ? GameDisc.First(GameDisc.Find(), GameDisc.Kind.One) : null;
        var found = installed ?? disc;
        page.AddChild(Top("Welcome"));
        page.AddChild(Words(
            "Machines Remake rebuilds Machines: Wired for War (1999). It brings nothing of the original along: its models, " +
            "planets, sounds and films come from your own copy of the game.",
            installed is not null
                ? "Your copy was found on this computer. The import takes a few minutes and makes the remake's sharper " +
                  "textures on your graphics card as it goes."
            : disc is not null
                ? "Your Machines CD was found. The import copies the game from it and takes a few minutes. Put in CD 2 as " +
                  "well, or keep its disc image beside, and the campaign's films come along."
                : "Put the first Machines CD in the drive and it is found at once. Or pick a disc image of the CD (.iso), " +
                  "or the folder Machines is installed in. The import takes a few minutes."));
        if (found is not null)
        {
            // The disc by its name, a folder by its path, cut short where it is too long for the page.
            static string Name(string place) => File.Exists(place) ? Path.GetFileName(place) : Lang.T("CD in drive {0}", place.TrimEnd('\\', '/'));
            var second = disc is null ? null : GameDisc.Second(found);
            var where = MenuTheme.Label(disc is null ? found : second is null ? Name(found) : $"{Name(found)}  +  {Name(second)}", 24, MenuTheme.Bright);
            where.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            where.ClipText = true;
            where.CustomMinimumSize = new Vector2(1, 0);
            where.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            page.AddChild(where);
        }
        var status = MenuTheme.Paragraph("Without the import the machines show as simple shapes; the options can import later.", 22, MenuTheme.Text);
        page.AddChild(status);
        page.AddChild(Spring(across: false));

        var folders = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Pick the folder Machines is installed in (it holds models.bin)",
            UseNativeDialog = true,
        };
        AddChild(folders);
        var images = OptionsPage.ImageDialog();
        AddChild(images);
        var buttons = new List<ChipButton>();
        void Import(string source) => OptionsPage.RunImport(this, source, status, busy =>
        {
            foreach (var button in buttons)
                button.Disabled = busy;
            _importing = busy;
            status.AddThemeColorOverride("font_color", busy ? MenuTheme.Text : MenuTheme.Bright);
        });
        folders.DirSelected += Import;
        images.FileSelected += Import;
        if (found is not null)
            buttons.Add(Button("Import", () => Import(found)));
        buttons.Add(Button("Disc image", () => images.PopupCentered(new Vector2I(900, 600))));
        buttons.Add(Button(found is null ? "Folder" : "Other folder", () => folders.PopupCentered(new Vector2I(900, 600))));
        buttons.Add(Button("Later", () => screens.ShowSetup(Step.Look)));
        foreach (var button in buttons)
            button.CustomMinimumSize = new Vector2(found is null ? 360 : 280, 76);
        page.AddChild(Line(Gap, [.. buttons]));
        Focus(buttons[0]);

        // While nothing is found, a CD put in the drive brings the page back with it.
        if (found is null)
        {
            var watch = new Godot.Timer { WaitTime = 2, Autostart = true };
            watch.Timeout += () =>
            {
                if (!_importing && GameDisc.InDrive() is not null)
                    screens.ShowSetup(Step.Import);
            };
            AddChild(watch);
        }
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
            "The original's machines sharpened on painted ground: photographed ground and skies, soft shadows and " +
            "glow, new explosions and smoke, weather, trees and rocks.");
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

    /// <summary>
    /// The soundtrack: with the classic look the original's from MP3s of the player's own, whose folder this asks for;
    /// with a look of the remake the remake's own, which comes with the game, the original's folder being only for
    /// playing the original's tracks instead or as well (chosen here or in the options).
    /// </summary>
    private void MusicStep(VBoxContainer page)
    {
        page.AddChild(Top("Music"));
        var remake = GraphicsSettings.Look != Look.Classic;
        Label tracks;
        if (!remake)
        {
            page.AddChild(Words(
                "The remake plays the original soundtrack from MP3s of your own, as the original played its CD: the intro, " +
                "the menu tune, each planet's own track and the victory and defeat tunes.",
                "Pick the folder that holds them (Track 00 - Intro.mp3 and the others). Without it the game plays without music."));
            tracks = MenuTheme.Label("", 24, MenuTheme.Bright);
            ShowTracks(Music.Folder);
            page.AddChild(tracks);
        }
        else
        {
            page.AddChild(Words(
                "The remake has a soundtrack of its own, made for it: it comes with the game and plays over the menus and in every match.",
                "If you have the original soundtrack as MP3s (Track 00 - Intro.mp3 and the others), you can pick their folder " +
                "and play the original's tracks instead, or both mixed. Without them nothing is missing. The options change it at any time."));
            var soundtracks = Enum.GetValues<Soundtrack>();
            var soundtrack = new ChipChoice(soundtracks.Select(s => s.ToString()), Array.IndexOf(soundtracks, Music.Mode))
            {
                CustomMinimumSize = new Vector2(360, 64),
            };
            tracks = MenuTheme.Label("", 24, MenuTheme.Bright);
            soundtrack.ItemSelected += i =>
            {
                Music.Mode = soundtracks[i];
                ShowTracks(Music.Folder);
            };
            var row = Setting("Soundtrack", soundtrack);
            soundtrack.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
            page.AddChild(row);
            ShowTracks(Music.Folder);
            page.AddChild(tracks);
        }
        // Bright where something is missing (with the classic look, until a folder is picked), else quiet.
        void ShowTracks(string? folder)
        {
            var count = Music.TrackCount(folder);
            var (text, missing) = remake
                ? OptionsPage.Tracks(Music.Mode, count)
                : count > 0 ? (Lang.T("{0} tracks found.", count), false) : (Lang.T("No soundtrack picked yet."), true);
            tracks.Text = text;
            tracks.AddThemeColorOverride("font_color", missing || !remake ? MenuTheme.Bright : MenuTheme.Dim);
        }
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
            ShowTracks(folder);
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
        // With the remake's own soundtrack nothing needs picking.
        Focus(Music.Folder is null && !remake ? pick : finish);
    }
}
