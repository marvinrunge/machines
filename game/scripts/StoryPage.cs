using Godot;
using Machines.Sim.Story;

namespace Machines.Game;

/// <summary>
/// The story's page in the menus: the chapter played last and the checkpoint reached in it, the names the two Grunts wake
/// with, and Continue (from that checkpoint), New (the prologue from its start), Chapters (a chapter begun, from its start)
/// and Back. The Locator is always L-7. MACHINES_MENU_SCREENSHOT=&lt;png&gt;,story shows it.
/// </summary>
public partial class StoryPage(MenuScreens screens) : MenuPage(screens.ShowMain)
{
    private readonly StoryProgress _progress = StoryProgress.Load();
    private LineEdit _ike = null!, _bob = null!;

    protected override void Build()
    {
        var page = Column(Gap);
        AddChild(page);
        page.AddChild(Heading("Story"));
        var columns = Line(Gap);
        columns.SizeFlagsVertical = SizeFlags.ExpandFill;
        page.AddChild(columns);

        var chapter = _progress.Chapter ?? StoryOptions.Prologue;
        var about = new ChipPanel { SizeFlagsHorizontal = SizeFlags.ExpandFill, Padding = 28 };
        var status = _progress.Ended ? StoryDoneCard.Words(chapter).Title
            : _progress.CanContinue
            ? _progress.Checkpoint > 0 ? Lang.T("Last checkpoint reached: {0}", _progress.Checkpoint) : Lang.T("Begun, no checkpoint reached yet")
            : Lang.T("Not begun yet");
        about.AddChild(Column(22,
            MenuTheme.Label(StoryProgress.Heading(chapter), 34, MenuTheme.Bright),
            MenuTheme.Paragraph(Lang.T("A maintenance unit wakes in its clamp aboard a ship of the machines. Its update is pending. Two Grunts in the bays beside it wake with it."), 24, MenuTheme.Text),
            MenuTheme.Label(status, 24, _progress.CanContinue || _progress.Ended ? MenuTheme.Glow : MenuTheme.Dim)));
        // What L-7 marked in the armoury's wrecks for later (package W; act 2 reads StoryProgress.Salvaged).
        if (_progress.Salvaged.Count > 0)
            about.GetChild(0).AddChild(MenuTheme.Label(Lang.T("Marked in the wreck: {0}", string.Join(", ", _progress.Salvaged.Select(Salvage))), 20, MenuTheme.Dim));
        columns.AddChild(about);

        var trio = new ChipPanel { CustomMinimumSize = new Vector2(600, 0), Padding = 28 };
        _ike = NameField(_progress.IkeName, "Ike");
        _bob = NameField(_progress.BobName, "Bob");
        trio.AddChild(Column(22,
            MenuTheme.Label(Lang.T("Grunts").ToUpperInvariant(), 26, MenuTheme.Bright),
            Setting(Lang.T("First Grunt"), _ike),
            Setting(Lang.T("Second Grunt"), _bob),
            MenuTheme.Paragraph(Lang.T("The names the two Grunts wake with. The Locator is always {0}.", StoryReadout.LocatorName), 21, MenuTheme.Dim)));
        columns.AddChild(trio);

        var back = Button("Back", Back);
        back.CustomMinimumSize = new Vector2(300, 76);
        var chapters = Button("Chapters", ShowChapters);
        chapters.CustomMinimumSize = new Vector2(300, 76);
        chapters.Disabled = _progress.Begun.Count == 0;
        var fresh = Button("New", New);
        fresh.CustomMinimumSize = new Vector2(300, 76);
        var go = Button("Continue", Continue, 76, 30);
        go.CustomMinimumSize = new Vector2(420, 76);
        go.Disabled = !_progress.CanContinue;
        page.AddChild(Line(Gap, back, Spring(), chapters, fresh, go));

        SwitchOn();
        Focus(_progress.CanContinue ? go : fresh);
    }

    private static LineEdit NameField(string name, string fallback) =>
        new() { Text = name, PlaceholderText = fallback, MaxLength = 16, ContextMenuEnabled = false, SizeFlagsHorizontal = SizeFlags.ExpandFill };

    public override void Back()
    {
        Remember();
        _progress.Save();
        base.Back();
    }

    // The names typed, or the Grunts' own where none is.
    private void Remember() =>
        (_progress.IkeName, _progress.BobName) = (StorySeats.Named(_ike.Text, "Ike"), StorySeats.Named(_bob.Text, "Bob"));

    /// <summary>The chapter played last, from the checkpoint reached.</summary>
    private void Continue()
    {
        if (_progress.Chapter is not { } chapter)
            return;
        Play(chapter, _progress.Checkpoint);
    }

    /// <summary>The prologue from its start; asked first when that gives up a checkpoint reached.</summary>
    private void New()
    {
        if (_progress.CanContinue && _progress.Checkpoint > 0)
            Stage.Message(Lang.T("Start the prologue anew? The checkpoint reached is given up."),
                (Lang.T("New"), () => Play(StoryOptions.Prologue, 0)), (Lang.T("Cancel"), null));
        else
            Play(StoryOptions.Prologue, 0);
    }

    /// <summary>The chapters begun, each to play again from its start.</summary>
    private void ShowChapters()
    {
        var begun = StoryProgress.Chapters.Where(_progress.Begun.Contains)
            .Select<string, (string Text, Action? Act)>(chapter => (StoryProgress.Heading(chapter), () => Play(chapter, 0)))
            .Append((Text: Lang.T("Cancel"), Act: (Action?)null)).ToArray();
        Stage.Message(Lang.T("Which chapter, from its start?"), begun);
    }

    private void Play(string chapter, int checkpoint)
    {
        Remember();
        StoryProgress.Play(GetTree(), _progress.Options(chapter, checkpoint));
    }

    /// <summary>A salvaged part's name on the page.</summary>
    private static string Salvage(string id) => id switch
    {
        Machines.Sim.Story.StoryWaves.Grunt2Drive => Lang.T("Grunt 2 drive"),
        Machines.Sim.Story.StoryWaves.ElectricCharge => Lang.T("electric charge"),
        _ => id,
    };
}
