using Godot;

namespace Machines.Game;

/// <summary>
/// What the original showed once when the game started, before the main menu (MachGuiStartupScreens): the loading
/// picture (<c>gui/menu/wait.bmp</c>) with the short film played on it after loading (<c>flics/postload.smk</c>, at
/// 32,129 of the 640×480 screen), then the intro film (<c>flics/animatic.smk</c>) on black. A click or Escape skips
/// the film playing. Played straight from the player's own copy of the game; without it there is nothing to show.
/// The remake's own trailer follows when it lies beside the game (<c>films/trailer.ogv</c>, Ogg Theora).
/// </summary>
public partial class StartupFilms : Control
{
    private static readonly Vector2 Screen = new(640, 480);

    private static bool _played;

    private readonly string _gameFolder;
    private readonly Action _done;
    private Control? _frame;
    private SmackerView? _playing;
    private TextureRect? _picture;
    private VideoStreamPlayer? _trailer;
    private bool _finished;

    private static string TrailerFile => System.IO.Path.Combine(Paths.Root, "films", "trailer.ogv");

    private StartupFilms(string gameFolder, Action done)
    {
        (_gameFolder, _done) = (gameFolder, done);
        MouseFilter = MouseFilterEnum.Stop;
    }

    /// <summary>
    /// The films, the first time the menus open in this run of the game; <paramref name="done"/> follows once they
    /// have played or been skipped, and the films then leave. Null when they have played already or the game copy
    /// has no intro.
    /// </summary>
    public static StartupFilms? Create(string? gameFolder, Action done)
    {
        if (_played || gameFolder is null || !File.Exists(System.IO.Path.Combine(gameFolder, "flics", "animatic.smk")))
            return null;
        _played = true;
        return new StartupFilms(gameFolder, done);
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        var black = new ColorRect { Color = Colors.Black, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(black);
        black.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        Resized += Fit;
        LoadingFilm();
    }

    public override void _Process(double delta)
    {
        if (_trailer is not null && _picture!.Texture != _trailer.GetVideoTexture())
            _picture.Texture = _trailer.GetVideoTexture();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { Pressed: true })
        {
            AcceptEvent();
            if (_trailer is not null)
                Finish();
            else
                _playing?.Skip();
        }
    }

    public override void _Input(InputEvent @event)
    {
        // The films skip themselves on Escape; the trailer is played here.
        if (_trailer is not null && @event.IsActionPressed("ui_cancel"))
        {
            GetViewport().SetInputAsHandled();
            Finish();
        }
    }

    /// <summary>The loading picture, with the film after loading played on it.</summary>
    private void LoadingFilm()
    {
        var picture = System.IO.Path.Combine(_gameFolder, "gui", "menu", "wait.bmp");
        var film = System.IO.Path.Combine(_gameFolder, "flics", "postload.smk");
        if (!File.Exists(picture) || !File.Exists(film) || ClassicAssets.DecodeBmp(File.ReadAllBytes(picture)) is not { } image)
        {
            IntroFilm();
            return;
        }
        // The original's screen, scaled to the window whole (see Fit).
        _frame = new Control { Size = Screen, MouseFilter = MouseFilterEnum.Ignore };
        AddChild(_frame);
        _frame.AddChild(new TextureRect
        {
            Texture = ImageTexture.CreateFromImage(image),
            Size = Screen,
            MouseFilter = MouseFilterEnum.Ignore,
        });
        var view = Play(new SmackerView(film), IntroFilm);
        _frame.AddChild(view);
        view.Position = new Vector2(32, 129);
        view.Size = view.VideoSize;
        Fit();
    }

    /// <summary>The intro film, as wide as the window allows.</summary>
    private void IntroFilm()
    {
        _frame?.QueueFree();
        _frame = null;
        var view = Play(new SmackerView(System.IO.Path.Combine(_gameFolder, "flics", "animatic.smk")), Trailer);
        AddChild(view);
        view.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // The film has its own sound: the menus start with the menu tune, not the intro's.
        Music.IntroFilmPlayed();
    }

    /// <summary>The remake's trailer, as wide as the window allows, when it is there.</summary>
    private void Trailer()
    {
        if (!File.Exists(TrailerFile))
        {
            Finish();
            return;
        }
        _picture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore,
        };
        AddChild(_picture);
        _picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _trailer = new VideoStreamPlayer
        {
            Stream = new VideoStreamTheora { File = TrailerFile },
            Bus = GameAudio.EffectsBus,
            Visible = false,
        };
        _trailer.Finished += Finish;
        _picture.AddChild(_trailer);
        _trailer.Play();
    }

    private void Finish()
    {
        if (_finished)
            return;
        _finished = true;
        _trailer = null;
        QueueFree();
        _done();
    }

    private SmackerView Play(SmackerView view, Action next)
    {
        view.Skippable = true;
        view.Finished += () =>
        {
            _playing = null;
            view.QueueFree();
            next();
        };
        _playing = view;
        return view;
    }

    /// <summary>Scales the 640×480 screen to fit the window, in the middle.</summary>
    private void Fit()
    {
        if (_frame is null || Size.X <= 0 || Size.Y <= 0)
            return;
        var scale = Mathf.Min(Size.X / Screen.X, Size.Y / Screen.Y);
        _frame.Scale = new Vector2(scale, scale);
        _frame.Position = (Size - Screen * scale) / 2;
    }
}
