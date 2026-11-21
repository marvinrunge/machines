using Godot;

namespace Machines.Game;

/// <summary>
/// The board with a chip saying which match is loading (<see cref="MatchRequest.Current"/>), over everything from the
/// moment the match is asked for until it is readied (<see cref="Warmup"/>) and has run its first frames, then fading into
/// it, with a bar of LEDs for how far it has come (<see cref="Progress"/>). Building a match holds the
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

    /// <summary>How far the match is loaded, 0 to 1, and what it is loading (untranslated), for the bar under the card.</summary>
    public static float Progress { get; set; }

    public static string Step { get; set; } = "";

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
        (Progress, Step) = (0, "Map");
        var screen = new LoadingScreen(title, detail, change ?? (() => tree.ChangeSceneToFile(MatchScene))) { _from = tree.CurrentScene };
        tree.Root.CallDeferred(Node.MethodName.AddChild, screen);
    }

    /// <summary>
    /// The match's scenario, and its planet when that is another name, as the screens print it; a battle's map by its
    /// name in the player's language, a story's chapter by its title ("PROLOGUE – FAULT").
    /// </summary>
    public static string MapName(MatchRequest request) => request.Options.Story is { } story ? StoryProgress.Heading(story.Chapter) :
        (request.Options.Battle is not null && Machines.Sim.Survival.BattleCatalogue.Map(request.Scenario) is { } battle ? BattlePod.MapName(battle)
            : request.Scenario.Equals(request.Planet, StringComparison.OrdinalIgnoreCase) ? request.Scenario : $"{request.Scenario}   ·   {request.Planet}")
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
        if (_fading || (arrived ? _shown < ShownFrames : _frames < GiveUpFrames) || scene is Game { Hidden: true })
            return;
        if (scene is Game game && !_mapped && MiniMaps.Missing(MatchRequest.Current.Scenario))
        {
            _mapped = _mapping = true;
            MiniMaps.Take(game, MatchRequest.Current.Scenario, () => _mapping = false);
        }
        if (_mapping)
            return;
        _fading = true;
        (Progress, Step) = (1, "Ready");
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
            column.AddChild(new Bar());
            var chip = new ChipPanel { Padding = 40 };
            chip.AddChild(column);
            var centre = new CenterContainer();
            centre.AddChild(chip);
            AddChild(centre);
        }
    }

    /// <summary>
    /// How far the match is loaded, as a part of the board: a row of SMD LEDs set into a groove, lit from the left as
    /// far as it has come, with a blip running along the lit ones as along a bus, wired to a solder pad at either end;
    /// under it what is loading and how much of it is done.
    /// </summary>
    private partial class Bar : Control
    {
        private const int Leds = 24;
        private const float Groove = 30, PadRadius = 8, Ends = 34;
        private static readonly Color Copper = new(0.64f, 0.56f, 0.22f), Off = new(0.06f, 0.16f, 0.07f), Lit = new(0.25f, 0.85f, 0.16f);
        private float _shown, _time;

        public Bar() => CustomMinimumSize = new Vector2(620, Groove + 46);

        public override void _Process(double delta)
        {
            _time += (float)delta;
            // Eased toward where loading is, never back.
            _shown = Mathf.Max(_shown, Mathf.Lerp(_shown, Mathf.Clamp(Progress, 0, 1), 1 - Mathf.Exp(-6 * (float)delta)));
            QueueRedraw();
        }

        public override void _Draw()
        {
            var middle = Groove / 2;
            // The pads and the traces from them into the groove.
            foreach (var x in new[] { PadRadius + 2, Size.X - PadRadius - 2 })
            {
                var inner = x < Size.X / 2 ? Ends : Size.X - Ends;
                DrawLine(new Vector2(x, middle), new Vector2(inner, middle), Copper, 3);
                DrawCircle(new Vector2(x, middle), PadRadius, Copper);
                DrawCircle(new Vector2(x, middle), PadRadius * 0.45f, MenuTheme.Well);
            }
            var groove = new Rect2(Ends, 0, Size.X - 2 * Ends, Groove);
            DrawStyleBox(new StyleBoxFlat
            {
                BgColor = MenuTheme.Well,
                BorderColor = MenuTheme.Edge,
                BorderWidthLeft = 2, BorderWidthTop = 2, BorderWidthRight = 2, BorderWidthBottom = 2,
                CornerRadiusTopLeft = 6, CornerRadiusTopRight = 6, CornerRadiusBottomLeft = 6, CornerRadiusBottomRight = 6,
            }, groove);

            // The LEDs: lit as far as loading has come, the last one partly; the blip runs along the lit ones.
            var inside = groove.Grow(-7);
            const float gap = 4;
            var width = (inside.Size.X - gap * (Leds - 1)) / Leds;
            var lit = _shown * Leds;
            var blip = lit >= 1 ? _time * 18 % (lit + 4) : -10;
            for (var i = 0; i < Leds; i++)
            {
                var led = new Rect2(inside.Position.X + i * (width + gap), inside.Position.Y, width, inside.Size.Y);
                var on = Mathf.Clamp(lit - i, 0, 1);
                var hot = Mathf.Clamp(1 - Mathf.Abs(i - blip) / 2.5f, 0, 1) * on;
                if (on > 0)
                    DrawRect(led.Grow(3), new Color(MenuTheme.Glow, 0.16f * on + 0.2f * hot));
                DrawRect(led, Off.Lerp(Lit, on).Lerp(new Color(0.85f, 1, 0.7f), hot * 0.7f));
                // The light on the lens's top.
                DrawRect(new Rect2(led.Position + new Vector2(1, 1), new Vector2(led.Size.X - 2, led.Size.Y * 0.3f)),
                    new Color(1, 1, 1, 0.04f + 0.22f * on));
            }

            var caption = Step.Length > 0 ? $"{Lang.T(Step).ToUpperInvariant()}   ·   {Mathf.RoundToInt(_shown * 100)} %" : $"{Mathf.RoundToInt(_shown * 100)} %";
            DrawString(MenuTheme.Font, new Vector2(0, Groove + 34), caption, HorizontalAlignment.Center, Size.X, 20, MenuTheme.Dim);
        }
    }
}
