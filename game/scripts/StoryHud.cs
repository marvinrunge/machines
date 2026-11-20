using Godot;
using Machines.Sim.World;

namespace Machines.Game;

/// <summary>
/// The screen of a story chapter, a placeholder until the story's own plates are drawn (plan WP-G): the chapter and its
/// checkpoint, and the Locator's and the Grunts' health, in the top left corner. Wired's plates (waves, bank, pod) mean
/// nothing in the story, so a story match has this in their place.
/// </summary>
public partial class StoryHud : Control
{
    private readonly Game _game;
    private float _u = 1;

    public StoryHud(Game game)
    {
        _game = game;
        Name = "StoryHud";
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public override void _Ready() => SetAnchorsPreset(LayoutPreset.FullRect);

    public override void _Process(double delta)
    {
        _u = Mathf.Max(GetViewportRect().Size.Y / 1080f, 0.6f) * GameOptions.HudSize;
        QueueRedraw();
    }

    public override void _Draw()
    {
        if (_game.World.Story is not { } story)
            return;
        var font = MenuTheme.Font;
        var px = Math.Max(9, Mathf.RoundToInt(16 * _u));
        var at = new Vector2(16, 28) * _u;
        Line(font, ref at, px, Lang.T("Story: {0}, checkpoint {1}", story.Options.Chapter, story.Checkpoint), new Color(0.8f, 0.85f, 0.9f));
        foreach (var seat in story.Seats)
        {
            var name = _game.World.Players[seat].Name;
            var text = _game.World.HeroOf(seat) is { } hero ? $"{name}  {hero.HitPoints}/{hero.MaxHitPoints}" : $"{name}  —";
            Line(font, ref at, px, text, seat == Game.Me ? new Color(1, 0.95f, 0.7f) : new Color(0.75f, 0.8f, 0.85f));
        }
        if (story.Ending is { } ending)
            Line(font, ref at, px, Lang.T("Chapter over: {0}", ending), new Color(1, 0.45f, 0.4f));
    }

    private void Line(Font font, ref Vector2 at, int px, string text, Color colour)
    {
        DrawStringOutline(font, at, text, HorizontalAlignment.Left, -1, px, Math.Max(2, Mathf.RoundToInt(4 * _u)), new Color(0, 0, 0, 0.85f));
        DrawString(font, at, text, HorizontalAlignment.Left, -1, px, colour);
        at.Y += px * 1.4f;
    }
}
