using Godot;
using Machines.Import.Text;

namespace Machines.Game;

/// <summary>
/// The options (MachGuiCtxOptions), the same before a match and over one: the look, the music's and the sounds'
/// volumes, 3D sound, the soundtrack's folder, window or full screen, the menus' and the HUD's size, and the original's gamma,
/// reversed keys and mouse and 2D selection box (<see cref="GameOptions"/>). Before a match it also imports the
/// original models and terrain from the player's own copy of the game (<see cref="ClassicImport"/>). Every change
/// takes at once and is kept in <c>user://settings.cfg</c>.
/// </summary>
public partial class OptionsPage(Action back, bool inGame) : MenuPage(back)
{
    private const string Settings = "user://settings.cfg";

    private static readonly float[] Sizes = [0.8f, 0.9f, 1f, 1.1f, 1.25f];

    private readonly Action _back = back;
    private bool _importing;

    /// <summary>Whether the game fills the screen; it opens in a window otherwise.</summary>
    public static bool FullScreen
    {
        get
        {
            var config = new ConfigFile();
            return config.Load(Settings) == Error.Ok && (bool)config.GetValue("display", "full_screen", false);
        }
        set
        {
            var config = new ConfigFile();
            config.Load(Settings);
            config.SetValue("display", "full_screen", value);
            config.Save(Settings);
            ApplyWindow();
            // The next start opens the window so from its splash on.
            BootSplash.Write();
        }
    }

    /// <summary>Puts the window the way the player last chose.</summary>
    public static void ApplyWindow()
    {
        var full = DisplayServer.WindowGetMode() is DisplayServer.WindowMode.Fullscreen or DisplayServer.WindowMode.ExclusiveFullscreen;
        if (FullScreen != full)
            DisplayServer.WindowSetMode(FullScreen ? DisplayServer.WindowMode.Fullscreen : DisplayServer.WindowMode.Windowed);
    }

    public override void Back()
    {
        if (!_importing)
            base.Back();
    }

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        // The import beside the heading: under the controls it pushed Back off the screen.
        page.AddChild(inGame ? Heading("Options") : Line(Gap, Heading("Options"), Import()));
        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var chip = new ChipPanel { CustomMinimumSize = new Vector2(820, 0), Padding = 28, SizeFlagsVertical = SizeFlags.ShrinkBegin };
        var rows = Column(20);
        chip.AddChild(rows);
        columns.AddChild(chip);

        var lookNote = MenuTheme.Paragraph("", 20, MenuTheme.Dim);
        var looks = GraphicsSettings.Looks;
        var look = new ChipChoice(looks.Select(GraphicsSettings.Name), Array.IndexOf(looks, GraphicsSettings.Look));
        look.ItemSelected += i =>
        {
            GraphicsSettings.Look = looks[i];
            lookNote.Text = inGame ? "The new look shows from the next match." : Describe(looks[i]);
        };
        lookNote.Text = inGame ? "" : Describe(GraphicsSettings.Look);
        // Each part of the look on a page of its own; Back there comes back here.
        var stage = Stage;
        var customize = Button("Customize", () => stage.Show(new GraphicsPage(() => stage.Show(new OptionsPage(_back, inGame)), inGame)), 64, 22);
        customize.CustomMinimumSize = new Vector2(220, 64);
        rows.AddChild(Setting("Look", Line(18, look, customize)));
        look.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        rows.AddChild(lookNote);

        rows.AddChild(Setting("Music", Slider(Music.Volume, value => Music.Volume = value)));
        rows.AddChild(Setting("Sounds", Slider(GameAudio.SoundVolume, value => GameAudio.SoundVolume = value)));
        rows.AddChild(Setting(Text(7098, "3D Sound"), Switch(GameOptions.Sound3d, on => GameOptions.Sound3d = on)));

        static string Tracks(string? folder) => Music.TrackCount(folder) is var count and > 0
            ? Lang.T("{0} tracks", count)
            : Lang.T("Pick the folder with the soundtrack's MP3s");
        // Two lines at most: on one it pushed the other column off the screen until a folder was picked, on three the page.
        var tracks = MenuTheme.Label(Tracks(Music.Folder), 20, MenuTheme.Dim);
        tracks.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        tracks.MaxLinesVisible = 2;
        tracks.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        tracks.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var music = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Pick the folder with the soundtrack (Track 00 - Intro.mp3 ...)",
            UseNativeDialog = true,
        };
        AddChild(music);
        music.DirSelected += folder =>
        {
            Music.Folder = folder;
            tracks.Text = Tracks(folder);
        };
        var pick = Button("Pick folder", () => music.PopupCentered(new Vector2I(900, 600)), 64, 22);
        pick.CustomMinimumSize = new Vector2(240, 64);
        rows.AddChild(Setting("Soundtrack", Line(18, pick, tracks)));
        tracks.SizeFlagsVertical = SizeFlags.ShrinkCenter;

        var window = new ChipChoice(["Window", "Full screen"], FullScreen ? 1 : 0);
        window.ItemSelected += i => FullScreen = i == 1;
        // The language shares the display's row, or Back would go off the screen; a new one builds the page again in it.
        var language = new ChipChoice(Lang.Names, Array.IndexOf(Lang.Codes, Lang.Code));
        language.ItemSelected += i =>
        {
            Lang.Code = Lang.Codes[i];
            stage.Show(new OptionsPage(_back, inGame));
        };
        var languageRow = Setting("Language", language, 0);
        rows.AddChild(Setting("Display", Line(28, window, languageRow)));
        window.SizeFlagsHorizontal = languageRow.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        var size = new ChipChoice(Sizes.Select(s => $"{s * 100:0} %"), Math.Max(0, Array.FindIndex(Sizes, s => Mathf.IsEqualApprox(s, MenuTheme.UiScale))));
        size.ItemSelected += i =>
        {
            MenuTheme.UiScale = Sizes[i];
            Stage.Fit();
        };
        // The menus' size and their transitions share a row: one more would push Back off the screen.
        var transitions = Setting(Text(7099, "Menu Transitions"), Switch(GameOptions.Transitions, on => GameOptions.Transitions = on), 0);
        rows.AddChild(Setting("Menu size", Line(28, size, transitions)));
        size.SizeFlagsHorizontal = transitions.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        // Depth of field and the film look share a row: one more would push Back off the screen.
        var blur = new ChipChoice(["Off", "Subtle", "Strong"], (int)GameOptions.DepthOfField);
        blur.ItemSelected += i => GameOptions.DepthOfField = (GameOptions.Blur)i;
        var film = Setting("Film look", Switch(GameOptions.FilmLook, on => GameOptions.FilmLook = on), 0);
        rows.AddChild(Setting("Depth of field", Line(28, blur, film)));
        blur.SizeFlagsHorizontal = film.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        columns.AddChild(Controls(() => stage.Show(new KeysPage(() => stage.Show(new OptionsPage(_back, inGame)))), HudSize()));

        var backButton = Button("Back", Back);
        backButton.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, backButton));
        SwitchOn();
        Focus(look);
    }

    /// <summary>
    /// How large the HUD is drawn (<see cref="GameOptions.HudSize"/>): over a match it shows as the match goes on, its
    /// art made ready for the new size meanwhile.
    /// </summary>
    private Control HudSize()
    {
        var sizes = GameOptions.HudSizes;
        var choice = new ChipChoice(sizes.Select(s => $"{s * 100:0} %"), Math.Max(0, Array.FindIndex(sizes, s => Mathf.IsEqualApprox(s, GameOptions.HudSize))));
        choice.ItemSelected += i =>
        {
            GameOptions.HudSize = sizes[i];
            if (inGame)
                HudArt.Prepare(HudScale.Of(GetViewportRect().Size));
        };
        return Setting("HUD size", choice, 0);
    }

    private static string Describe(Look look) => look switch
    {
        Look.Classic => "The original's models, ground, lighting and effects.",
        Look.Overhauled => "The original's models sharpened on the painted ground, with tone mapping, glow, soft shadows and the remake's effects.",
        Look.Hd => "Still in the making: the remade models on the painted ground, where they exist; the rest shows overhauled.",
        _ => "Each part of the look as chosen under Customize.",
    };

    private static HSlider Slider(float value, Action<float> changed, float lowest = 0, float highest = 1)
    {
        var slider = new HSlider { MinValue = lowest, MaxValue = highest, Step = 0.05, Value = value, CustomMinimumSize = new Vector2(0, 40) };
        slider.ValueChanged += v => changed((float)v);
        return slider;
    }

    private static ChipChoice Switch(bool on, Action<bool> changed)
    {
        var choice = new ChipChoice(["Off", "On"], on ? 1 : 0);
        choice.ItemSelected += i => changed(i == 1);
        return choice;
    }

    private static StringTable? _strings;

    // A label of the original's options screen (machstrg.xml), or the fallback without it.
    private static string Text(int id, string fallback)
    {
        var path = System.IO.Path.Combine(Paths.OriginalData, "machstrg.xml");
        _strings ??= File.Exists(path) ? StringTableParser.Load(path) : new StringTable(new Dictionary<int, string>());
        return Lang.Original(_strings.Strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback, fallback);
    }

    /// <summary>
    /// The rest of the original's options (MachGuiCtxOptions): the gamma correction beside its calibration picture
    /// (gammacal.bmp of the player's game copy), the up and down keys and the first-person mouse reversed, and the
    /// 2D selection box; and beside its title the button to the <paramref name="keys"/> page; beside the mouse speed the
    /// <paramref name="hudSize"/>.
    /// </summary>
    private static ChipPanel Controls(Action keys, Control hudSize)
    {
        var chip = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin, Padding = 28 };
        var title = MenuTheme.Label("CONTROLS AND PICTURE", 24, MenuTheme.Bright);
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        title.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var change = Button("Change keys", keys, 56, 22);
        change.CustomMinimumSize = new Vector2(240, 56);
        var rows = Column(20, Line(18, title, change));
        chip.AddChild(rows);

        var gamma = Slider(GameOptions.Gamma, value => GameOptions.Gamma = value, GameOptions.GammaLowest, GameOptions.GammaHighest);
        var picture = new ClassicAssets(Paths.Root).GameFolder is { } game
            && System.IO.Path.Combine(game, "gui", "menu", "gammacal.bmp") is var file && File.Exists(file)
            && ClassicAssets.DecodeBmp(File.ReadAllBytes(file)) is { } image
            ? new TextureRect { Texture = ImageTexture.CreateFromImage(image), CustomMinimumSize = new Vector2(image.GetWidth(), image.GetHeight()) * 1.5f, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, TextureFilter = CanvasItem.TextureFilterEnum.Nearest }
            : null;
        rows.AddChild(Setting(Text(6991, "Gamma"), picture is null ? gamma : Line(18, gamma, picture), 260));
        gamma.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        gamma.SizeFlagsVertical = SizeFlags.ShrinkCenter;

        // Side by side, or Back would leave the screen.
        var reverseMouse = Setting(Text(6994, "Reverse F.P. Mouse"), Switch(GameOptions.ReverseMouse, on => GameOptions.ReverseMouse = on), 0);
        rows.AddChild(Setting(Text(6995, "Reverse Up/Down Keys"), Line(28, Switch(GameOptions.ReverseKeys, on => GameOptions.ReverseKeys = on), reverseMouse), 260));
        // The HUD's size shares the mouse speed's row: one more would push Back off the screen.
        var mouseSpeed = Slider(GameOptions.MouseSpeed, value => GameOptions.MouseSpeed = value, GameOptions.MouseSpeedLowest, GameOptions.MouseSpeedHighest);
        rows.AddChild(Setting("F.P. Mouse Speed", Line(28, mouseSpeed, hudSize), 260));
        mouseSpeed.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        mouseSpeed.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        var weaponButtons = Setting("Mouse button per weapon", Switch(GameOptions.WeaponButtons, on => GameOptions.WeaponButtons = on), 0);
        var fireWaits = Setting("Fire waits for the gun", Switch(GameOptions.FireWaitsForGun, on => GameOptions.FireWaitsForGun = on), 0);
        rows.AddChild(Setting("Classic F.P. Keys", Line(28, Switch(GameOptions.ClassicDriving, on => GameOptions.ClassicDriving = on), weaponButtons), 260));
        rows.AddChild(Setting(Text(6996, "2D Selection box"), Line(28, Switch(GameOptions.Selection2d, on => GameOptions.Selection2d = on), fireWaits), 260));
        var classicKeys = Setting("Classic Keys", Switch(GameOptions.ClassicKeys, on => GameOptions.ClassicKeys = on), 0);
        rows.AddChild(Setting("Classic Mouse", Line(28, Switch(GameOptions.ClassicMouse, on => GameOptions.ClassicMouse = on), classicKeys), 260));
        // The story's: its lines at the foot of the view, and its frights made milder (less static, softer stings).
        var subtitles = new ChipChoice(["Off", "Small", "Normal", "Large"], (int)GameOptions.Subtitles);
        subtitles.ItemSelected += i => GameOptions.Subtitles = (GameOptions.Captions)i;
        var softScares = Setting("Soften scares", Switch(GameOptions.SoftScares, on => GameOptions.SoftScares = on), 0);
        rows.AddChild(Setting("Subtitles", Line(28, subtitles, softScares), 260));
        subtitles.SizeFlagsHorizontal = softScares.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        // The keys themselves are on their own page.
        rows.AddChild(MenuTheme.Paragraph("Classic F.P. Keys drive as the original did, Classic Mouse gives orders with the left button, Classic Keys puts commands on their letters.", 20, MenuTheme.Dim));
        rows.AddChild(MenuTheme.Paragraph("Mouse button per weapon: left, right and the wheel each fire a weapon; always on in Wired.", 20, MenuTheme.Dim));
        return chip;
    }

    /// <summary>
    /// Whether the original models and terrain are imported from the player's own copy of the game, and the import.
    /// Without them the game shows simple shapes. It takes a minute, off the main thread; the menus open again from the imported copy.
    /// </summary>
    private ChipPanel Import()
    {
        var chip = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 8 };
        var status = MenuTheme.Paragraph(ClassicImport.Outdated
                ? "Imported by an older version. Import again for the right lighting."
                : ClassicImport.Imported && !HdTextures.Made
                    ? "Imported. Import again for the remake's sharper textures."
                : ClassicImport.Imported && ClassicImport.FilmsMissing
                    ? "Imported from CD 1. Put in CD 2 and press From CD for the campaign's films."
                : ClassicImport.Imported
                    ? "Imported from your game copy."
                    : "Not imported. You need your own copy of Machines: Wired for War: its CD, a disc image of it or the folder it is installed in.",
            22, MenuTheme.Text);
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Pick the folder Machines is installed in (it holds models.bin)",
            UseNativeDialog = true,
        };
        // The picker opens at the copy found on this computer, if there is one.
        if (ClassicImport.Available && GameLocator.Find().FirstOrDefault() is { } found)
            dialog.CurrentDir = found;
        AddChild(dialog);
        var image = ImageDialog();
        AddChild(image);
        ChipButton? disc = null;
        var button = Button(ClassicImport.Imported ? "Import again" : "Import", () => dialog.PopupCentered(new Vector2I(900, 600)), 64, 22);
        void Busy(bool busy) => button.Disabled = disc!.Disabled = _importing = busy;
        // From the CD in a drive or a disc image found on this computer (CD 2 when only its films are missing), else a
        // disc image the player picks.
        disc = Button("From CD", () =>
        {
            var kind = ClassicImport.FilmsMissing ? GameDisc.Kind.Two : GameDisc.Kind.One;
            if (GameDisc.First(GameDisc.Find(), kind) is { } found)
                RunImport(this, found, status, Busy);
            else
                image.PopupCentered(new Vector2I(900, 600));
        }, 64, 22);
        disc.TooltipText = Lang.T("Import from the Machines CD in a drive or from a disc image of it (.iso)");
        foreach (var each in new[] { button, disc })
        {
            each.CustomMinimumSize = new Vector2(240, 64);
            each.SizeFlagsVertical = SizeFlags.ShrinkCenter;
            // The importer (tools/classic-export) runs on Windows and comes with the release, not with every checkout.
            each.Visible = ClassicImport.Available;
        }
        dialog.DirSelected += folder => RunImport(this, folder, status, Busy);
        image.FileSelected += file => RunImport(this, file, status, Busy);
        var text = Column(6, MenuTheme.Label("ORIGINAL MODELS AND TERRAIN", 24, MenuTheme.Bright), status);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        chip.AddChild(Line(18, text, disc, button));
        return chip;
    }

    /// <summary>A picker for a disc image of one of the game's CDs.</summary>
    public static FileDialog ImageDialog()
    {
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenFile,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Pick a disc image of a Machines CD (.iso)",
            Filters = ["*.iso ; Disc images"],
            UseNativeDialog = true,
        };
        if (Directory.Exists(Path.Combine(GameLocator.Home, "Downloads")))
            dialog.CurrentDir = Path.Combine(GameLocator.Home, "Downloads");
        return dialog;
    }

    /// <summary>
    /// Imports from the game copy in <paramref name="folder"/> off the main thread, telling its progress or why not in
    /// <paramref name="status"/>; <paramref name="busy"/> hears when it starts and when it fails. Once it is done the
    /// menus open again, so previews and descriptions come from the imported copy.
    /// </summary>
    public static void RunImport(Node owner, string folder, Label status, Action<bool> busy)
    {
        if (ClassicImport.WhyNotSource(folder) is { } why)
        {
            status.Text = why;
            return;
        }
        busy(true);
        Task.Run(() =>
        {
            var error = ClassicImport.FromSource(folder, line => Callable.From(() => status.Text = line).CallDeferred());
            Callable.From(() =>
            {
                if (error is null)
                {
                    owner.GetTree().ReloadCurrentScene();
                    return;
                }
                status.Text = error;
                busy(false);
            }).CallDeferred();
        });
    }
}
