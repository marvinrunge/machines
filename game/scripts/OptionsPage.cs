using Godot;
using Machines.Import.Text;

namespace Machines.Game;

/// <summary>
/// The options (MachGuiCtxOptions), the same before a match and over one: the look, the music's and the sounds'
/// volumes, 3D sound, the soundtrack's folder, window or full screen and the menus' size, and the original's gamma,
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
        var look = new ChipChoice(Enum.GetValues<Look>().Select(GraphicsSettings.Name), (int)GraphicsSettings.Look);
        look.ItemSelected += i =>
        {
            GraphicsSettings.Look = (Look)i;
            lookNote.Text = inGame ? "The new look shows from the next match." : Describe((Look)i);
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
            ? $"{count} tracks"
            : "Pick the folder with the soundtrack's MP3s";
        var tracks = MenuTheme.Label(Tracks(Music.Folder), 20, MenuTheme.Dim);
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
        rows.AddChild(Setting("Display", window));

        var size = new ChipChoice(Sizes.Select(s => $"{s * 100:0} %"), Math.Max(0, Array.FindIndex(Sizes, s => Mathf.IsEqualApprox(s, MenuTheme.UiScale))));
        size.ItemSelected += i =>
        {
            MenuTheme.UiScale = Sizes[i];
            Stage.Fit();
        };
        rows.AddChild(Setting("Menu size", size));

        // Depth of field and the film look share a row: one more would push Back off the screen.
        var blur = new ChipChoice(["Off", "Subtle", "Strong"], (int)GameOptions.DepthOfField);
        blur.ItemSelected += i => GameOptions.DepthOfField = (GameOptions.Blur)i;
        var film = Setting("Film look", Switch(GameOptions.FilmLook, on => GameOptions.FilmLook = on), 0);
        rows.AddChild(Setting("Depth of field", Line(28, blur, film)));
        blur.SizeFlagsHorizontal = film.SizeFlagsHorizontal = SizeFlags.ExpandFill;

        columns.AddChild(Controls());

        var backButton = Button("Back", Back);
        backButton.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, backButton));
        SwitchOn();
        Focus(look);
    }

    private static string Describe(Look look) => look switch
    {
        Look.Classic => "The original's models, ground, lighting and effects.",
        Look.Overhauled => "The original sharpened: its ground and models upscaled, with tone mapping, glow, soft shadows and the remake's effects.",
        Look.Hd => "The remade models on the painted ground, where they exist; the rest shows overhauled.",
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
        return _strings.Strings.TryGetValue(id, out var text) && !string.IsNullOrWhiteSpace(text) ? text.Trim() : fallback;
    }

    /// <summary>
    /// The rest of the original's options (MachGuiCtxOptions): the gamma correction beside its calibration picture
    /// (gammacal.bmp of the player's game copy), the up and down keys and the first-person mouse reversed, and the
    /// 2D selection box.
    /// </summary>
    private static ChipPanel Controls()
    {
        var chip = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin, Padding = 28 };
        var rows = Column(20, MenuTheme.Label("CONTROLS AND PICTURE", 24, MenuTheme.Bright));
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

        rows.AddChild(Setting(Text(6995, "Reverse Up/Down Keys"), Switch(GameOptions.ReverseKeys, on => GameOptions.ReverseKeys = on), 260));
        rows.AddChild(Setting(Text(6994, "Reverse F.P. Mouse"), Switch(GameOptions.ReverseMouse, on => GameOptions.ReverseMouse = on), 260));
        rows.AddChild(Setting("F.P. Mouse Speed",
            Slider(GameOptions.MouseSpeed, value => GameOptions.MouseSpeed = value, GameOptions.MouseSpeedLowest, GameOptions.MouseSpeedHighest), 260));
        rows.AddChild(Setting("Classic F.P. Keys", Switch(GameOptions.ClassicDriving, on => GameOptions.ClassicDriving = on), 260));
        rows.AddChild(Setting(Text(6996, "2D Selection box"), Switch(GameOptions.Selection2d, on => GameOptions.Selection2d = on), 260));
        rows.AddChild(MenuTheme.Paragraph(
            "The up and down keys look with Shift in first person, and tilt the ground camera with Ctrl and Shift. " +
            "Unreversed, up looks down as a pilot's stick does. " +
            "First person drives as in a tank game: W and S drive, A and D step sideways (legs, hover, flight) or turn, " +
            "Q and E turn, the mouse looks round and the upper body, or the machine, follows. Classic keys drive as the original did.", 20, MenuTheme.Dim));
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
                : ClassicImport.Imported
                    ? "Imported from your game copy."
                    : "Not imported. You need your own copy of Machines: Wired for War.",
            22, MenuTheme.Text);
        var dialog = new FileDialog
        {
            FileMode = FileDialog.FileModeEnum.OpenDir,
            Access = FileDialog.AccessEnum.Filesystem,
            Title = "Pick the folder Machines is installed in (it holds models.bin)",
            UseNativeDialog = true,
        };
        AddChild(dialog);
        var button = Button(ClassicImport.Imported ? "Import again" : "Import", () => dialog.PopupCentered(new Vector2I(900, 600)), 64, 22);
        button.CustomMinimumSize = new Vector2(240, 64);
        button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        // The importer (tools/classic-export) runs on Windows and comes with the release, not with every checkout.
        button.Visible = ClassicImport.Available;
        dialog.DirSelected += folder =>
        {
            if (ClassicImport.WhyNot(folder) is { } why)
            {
                status.Text = why;
                return;
            }
            button.Disabled = _importing = true;
            Task.Run(() =>
            {
                var error = ClassicImport.Run(folder, line => Callable.From(() => status.Text = line).CallDeferred());
                Callable.From(() =>
                {
                    _importing = false;
                    if (error is null)
                    {
                        // The menus open again so previews and descriptions come from the imported copy.
                        GetTree().ReloadCurrentScene();
                        return;
                    }
                    status.Text = error;
                    button.Disabled = false;
                }).CallDeferred();
            });
        };
        var text = Column(6, MenuTheme.Label("ORIGINAL MODELS AND TERRAIN", 24, MenuTheme.Bright), status);
        text.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        text.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        chip.AddChild(Line(18, text, button));
        return chip;
    }
}
