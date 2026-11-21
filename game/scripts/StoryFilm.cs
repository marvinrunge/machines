using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// The film a story chapter ends with, played live after the trio got away: the prologue's "Abwurf" outside (its script's
/// part "aussen", data/planets/prolog/abwurf.cut) on <paramref name="Scenario"/> and <paramref name="Planet"/> (for now
/// m_desert at dusk, until the crash planet of act 1 exists), then the chapter's card (<see cref="StoryDoneCard"/>) and the
/// way back to the menus' story page (the rooms, when the chapter was played online). MACHINES_STORY_FILM=prolog plays it
/// on its own.
/// </summary>
public sealed record StoryFilm(string Chapter, string Cutscene, string Scenario, string Planet, bool Online)
{
    /// <summary>The film a chapter ends with, or null when it has none.</summary>
    public static StoryFilm? Of(string chapter, bool online) =>
        chapter == StoryOptions.Prologue ? new StoryFilm(chapter, "abwurf", "prolog-abwurf", "m_desert", online) : null;

    /// <summary>
    /// The chapter's end on to its film: the match (a story's, which is over) is left, online its room too, and the film's
    /// map comes up behind a black screen, as the bay's part of the cutscene ended in black.
    /// </summary>
    public static void Play(SceneTree tree, StoryFilm film)
    {
        MatchRequest.Current = MatchRequest.ForStoryFilm(film);
        LoadingScreen.ShowBlack(tree);
    }
}

/// <summary>
/// The card after the film a chapter ends with: black, the chapter done ("PROLOGUE COMPLETE") over its name, a line on what
/// comes, and the key to go on, which leaves for the menus (or does so by itself after a while).
/// </summary>
public partial class StoryDoneCard : Control
{
    /// <summary>How long the card stands before it goes on by itself, seconds.</summary>
    public const double Stands = 15;

    private readonly StoryFilm _film;
    private readonly Action _onward;
    private HudInk _ink = null!;
    private double _time;
    private bool _gone;

    public StoryDoneCard(StoryFilm film, Action onward)
    {
        (_film, _onward) = (film, onward);
        Name = "StoryDoneCard";
        MouseFilter = MouseFilterEnum.Stop;
    }

    public override void _Ready()
    {
        SetAnchorsPreset(LayoutPreset.FullRect);
        _ink = new HudInk(this);
    }

    /// <summary>What the card says: the chapter done, its name, and what comes after it.</summary>
    public static (string Title, string Name, string Next) Words(string chapter) => chapter == StoryOptions.Prologue
        ? (Lang.T("Prologue complete"), StoryProgress.Subtitle(chapter), Lang.T("Act 1 is still to come."))
        : (Lang.T("Chapter complete"), StoryProgress.Title(chapter), "");

    public override void _Process(double delta)
    {
        _time += delta;
        if (_time >= Stands)
            GoOn();
        QueueRedraw();
    }

    public override void _Draw()
    {
        var size = GetViewportRect().Size;
        var u = HudInk.ScaleFor(size);
        _ink.U = u;
        var fade = Mathf.Clamp((float)_time / 1.2f, 0, 1);
        DrawRect(new Rect2(Vector2.Zero, size), Colors.Black);
        var middle = size.X / 2;
        var (title, name, next) = Words(_film.Chapter);
        _ink.Say(new Vector2(middle, size.Y * 0.42f), title.Caps(), 54, new Color(MenuTheme.Bright, fade), 0.5f);
        if (name.Length > 0)
            _ink.Say(new Vector2(middle, size.Y * 0.42f + 62 * u), $"„{name}“", 30, new Color(MenuTheme.Glow, fade), 0.5f);
        if (next.Length > 0)
            _ink.Say(new Vector2(middle, size.Y * 0.42f + 120 * u), next, 22, new Color(MenuTheme.Dim, fade), 0.5f);
        if (_time > 2)
        {
            var beat = 0.75f + 0.25f * MathF.Sin((float)_time * 4);
            _ink.Prompt(new Vector2(middle, size.Y * 0.78f), Lang.T("Continue ({0})", Lang.T("Space")), new Color(MenuTheme.Glow, beat * fade), 24);
        }
    }

    public override void _Input(InputEvent @event)
    {
        if (_time < 1.5 || @event is not (InputEventKey { Pressed: true, Echo: false } or InputEventMouseButton { Pressed: true }))
            return;
        GetViewport().SetInputAsHandled();
        if (@event is InputEventKey key && key.Keycode is not (Key.Space or Key.Enter or Key.KpEnter or Key.Escape))
            return;
        GoOn();
    }

    private void GoOn()
    {
        if (_gone)
            return;
        _gone = true;
        _onward();
    }
}
