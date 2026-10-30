using Godot;

namespace Machines.Game;

/// <summary>
/// A panel of the menus: a chip on the board (<see cref="ChipArt"/>) whose screen holds its children, laid out
/// inside the frame with some padding, like a <see cref="PanelContainer"/>. It can switch its screen on as an old
/// LCD does, with a flicker, its contents appearing with it.
/// </summary>
public partial class ChipPanel : Container
{
    private float _power = 1;

    /// <summary>Menu pixels per pixel of the chip texture: 0.5 draws it at its own size, less gives finer pins.</summary>
    public float ArtScale { get; set; } = 0.5f;

    public float Padding { get; set; } = 18;

    public float Power
    {
        get => _power;
        set
        {
            _power = value;
            foreach (var child in GetChildren().OfType<Control>())
                child.Modulate = new Color(1, 1, 1, value);
            QueueRedraw();
        }
    }

    public override void _Ready() => TextureFilter = TextureFilterEnum.LinearWithMipmaps;

    private float Inner => ChipArt.FrameWidth(ArtScale) + Padding;

    public override void _Notification(int what)
    {
        if (what != NotificationSortChildren)
            return;
        var content = new Rect2(Vector2.Zero, Size).Grow(-Inner);
        foreach (var child in GetChildren().OfType<Control>())
            if (child.Visible)
                FitChildInRect(child, content);
    }

    public override Vector2 _GetMinimumSize()
    {
        var size = Vector2.Zero;
        foreach (var child in GetChildren().OfType<Control>())
            if (child.Visible)
                size = size.Max(child.GetCombinedMinimumSize());
        return size + new Vector2(2 * Inner, 2 * Inner);
    }

    public override void _Draw() => ChipArt.Draw(this, new Rect2(Vector2.Zero, Size), ArtScale, power: _power);

    /// <summary>Switches the screen on after the delay, flickering on its way.</summary>
    public void SwitchOn(double delay)
    {
        Power = 0;
        var tween = CreateTween();
        tween.TweenInterval(delay);
        foreach (var (level, time) in new[] { (0.55f, 0.05), (0.1f, 0.06), (0.8f, 0.04), (0.35f, 0.07), (1f, 0.12) })
            tween.TweenProperty(this, "Power", level, time);
    }
}
