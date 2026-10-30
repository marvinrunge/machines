using Godot;

namespace Machines.Game;

/// <summary>
/// The board with a chip saying which match is loading (<see cref="MatchRequest.Current"/>), over everything from the
/// moment the match is asked for until it has drawn its first frames, then fading into it. Building a match holds the
/// game up for some seconds, so the screen comes up first, is drawn, and only then is the scene changed; it lies on
/// the tree's root, above the scenes, and stays through the change. Under it the match takes its map for the look
/// chosen when it has none yet (<see cref="MiniMaps"/>).
/// </summary>
public partial class LoadingScreen : CanvasLayer
{
    public const string MatchScene = "res://scenes/Main.tscn";

    // Frames drawn before the change; frames the new scene draws before the fade; frames after which a change that
    // left the scene as it was has failed.
    private const int DrawnFrames = 2, ShownFrames = 3, GiveUpFrames = 30;

    private readonly string _title, _detail;
    private readonly Action _change;
    private Node? _from;
    private bool _changed, _mapped, _mapping, _fading;
    private int _frames, _shown;

    /// <summary>Whether a loading screen is up; a second press of Start meanwhile does nothing.</summary>
    public static bool Busy { get; private set; }

    private LoadingScreen(string title, string detail, Action change)
    {
        (_title, _detail, _change) = (title, detail, change);
        Layer = 100;
    }

    /// <summary>
    /// Puts the loading screen up with <paramref name="title"/> over <paramref name="detail"/>, and once it is on the
    /// screen does <paramref name="change"/>, by default changing to the match scene.
    /// </summary>
    public static void Show(SceneTree tree, string title, string detail, Action? change = null)
    {
        if (Busy)
            return;
        Busy = true;
        var screen = new LoadingScreen(title, detail, change ?? (() => tree.ChangeSceneToFile(MatchScene))) { _from = tree.CurrentScene };
        tree.Root.CallDeferred(Node.MethodName.AddChild, screen);
    }

    /// <summary>The match's scenario, and its planet when that is another name, as the screens print it.</summary>
    public static string MapName(MatchRequest request) =>
        (request.Scenario.Equals(request.Planet, StringComparison.OrdinalIgnoreCase) ? request.Scenario : $"{request.Scenario}   ·   {request.Planet}")
            .ToUpperInvariant();

    public override void _Ready()
    {
        var stage = new MenuStage();
        AddChild(stage);
        var request = MatchRequest.Current;
        stage.Show(new Card(_title, _detail,
            MiniMaps.For(request.Scenario) ?? new ClassicAssets(Paths.Root).PlanetMap(request.Planet)));
    }

    public override void _ExitTree() => Busy = false;

    public override void _Process(double delta)
    {
        _frames++;
        if (!_changed)
        {
            if (_frames < DrawnFrames)
                return;
            // For automated checks: MACHINES_LOADING_SCREENSHOT=<png> saves the screen as the player sees it while the
            // match loads.
            if (System.Environment.GetEnvironmentVariable("MACHINES_LOADING_SCREENSHOT") is { Length: > 0 } shot)
                GetViewport().GetTexture().GetImage().SavePng(shot);
            _changed = true;
            _frames = 0;
            _change();
            return;
        }
        // The new scene is in once the current one is another; a few frames later it has drawn itself.
        var scene = GetTree().CurrentScene;
        var arrived = scene is not null && scene != _from;
        _shown = arrived ? _shown + 1 : 0;
        if (_fading || (arrived ? _shown < ShownFrames : _frames < GiveUpFrames))
            return;
        if (scene is Game game && !_mapped && MiniMaps.Missing(MatchRequest.Current.Scenario))
        {
            _mapped = _mapping = true;
            MiniMaps.Take(game, MatchRequest.Current.Scenario, () => _mapping = false);
        }
        if (_mapping)
            return;
        _fading = true;
        var stage = GetChild<Control>(0);
        stage.MouseFilter = Control.MouseFilterEnum.Ignore;
        stage.SetProcessUnhandledInput(false);
        var tween = CreateTween();
        tween.TweenProperty(stage, "modulate:a", 0f, 0.45);
        tween.TweenCallback(Callable.From(QueueFree));
    }

    /// <summary>The chip in the middle: LOADING, the match's name, the planet's overview and what is loading.</summary>
    private partial class Card(string title, string detail, Texture2D? picture) : MenuPage(null)
    {
        // Built whole and lit at once, not switched on: it has to be on the screen before the match holds the game up.
        protected override void Build()
        {
            var column = Column(16, MenuTheme.Label("LOADING", 28, MenuTheme.Dim, HorizontalAlignment.Center),
                MenuTheme.Label(title, 46, MenuTheme.Bright, HorizontalAlignment.Center));
            column.CustomMinimumSize = new Vector2(620, 0);
            if (picture is not null)
                column.AddChild(new TextureRect
                {
                    Texture = picture,
                    ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                    StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                    CustomMinimumSize = new Vector2(0, 340),
                });
            if (detail.Length > 0)
                column.AddChild(MenuTheme.Label(detail, 24, MenuTheme.Text, HorizontalAlignment.Center));
            var chip = new ChipPanel { Padding = 40 };
            chip.AddChild(column);
            var centre = new CenterContainer();
            centre.AddChild(chip);
            AddChild(centre);
        }
    }
}
