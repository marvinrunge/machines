using Godot;

namespace Machines.Game;

/// <summary>
/// The options (MachGuiCtxOptions), the same before a match and over one: the look, the music's and the sounds'
/// volumes, the soundtrack's folder, window or full screen and the menus' size. Before a match it also imports the
/// original models and terrain from the player's own copy of the game (<see cref="ClassicImport"/>). Every change
/// takes at once and is kept in <c>user://settings.cfg</c>.
/// </summary>
public partial class OptionsPage(Action back, bool inGame) : MenuPage(back)
{
    private const string Settings = "user://settings.cfg";

    private static readonly float[] Sizes = [0.8f, 0.9f, 1f, 1.1f, 1.25f];

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
        page.AddChild(Heading("Options"));
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
        rows.AddChild(Setting("Look", look));
        rows.AddChild(lookNote);

        rows.AddChild(Setting("Music", Slider(Music.Volume, value => Music.Volume = value)));
        rows.AddChild(Setting("Sounds", Slider(GameAudio.SoundVolume, value => GameAudio.SoundVolume = value)));

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

        columns.AddChild(inGame ? Spring() : Import());

        var backButton = Button("Back", Back);
        backButton.CustomMinimumSize = new Vector2(400, 76);
        page.AddChild(Line(Gap, backButton));
        SwitchOn();
        Focus(look);
    }

    private static string Describe(Look look) => look switch
    {
        Look.Classic => "The original's models, textures and lighting.",
        Look.Overhauled => "Its models with rounded edges, HD textures, tone mapping, glow, ambient occlusion and soft shadows.",
        _ => "The remade models as well, where they exist; the rest shows overhauled.",
    };

    private static HSlider Slider(float value, Action<float> changed)
    {
        var slider = new HSlider { MinValue = 0, MaxValue = 1, Step = 0.05, Value = value, CustomMinimumSize = new Vector2(0, 40) };
        slider.ValueChanged += v => changed((float)v);
        return slider;
    }

    /// <summary>
    /// Whether the original models and terrain are imported from the player's own copy of the game, and the import.
    /// Without them the game shows simple shapes. It takes a minute, off the main thread; the menus open again from the imported copy.
    /// </summary>
    private ChipPanel Import()
    {
        var chip = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkBegin, Padding = 28 };
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
        var button = Button(ClassicImport.Imported ? "Import again" : "Import", () => dialog.PopupCentered(new Vector2I(900, 600)));
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
        chip.AddChild(Column(18, MenuTheme.Label("ORIGINAL MODELS AND TERRAIN", 24, MenuTheme.Bright), status, button));
        return chip;
    }
}
