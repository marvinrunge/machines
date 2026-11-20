using Godot;

namespace Machines.Game;

/// <summary>
/// A bar as a part of the board: a row of SMD LEDs set into a groove, lit from the left as far as its value has come, with
/// a blip running along the lit ones as along a bus, wired to a solder pad at either end; under it a caption and the share
/// in per cent. The loading screen's (<see cref="LoadingScreen"/>), and the story's: the hack's trace-back, which runs
/// green, amber, red (<see cref="Heat"/>), the core's "37 %" and the pod's countdown. The value is read from
/// <see cref="Source"/> each frame when one is given, else from <see cref="Value"/>; it is eased toward, never back when
/// <see cref="OnlyForward"/>.
/// </summary>
public partial class LedBar : Control
{
    private const float PadRadius = 8, Ends = 34;
    private static readonly Color Copper = new(0.64f, 0.56f, 0.22f), Off = new(0.06f, 0.16f, 0.07f);

    /// <summary>The lit colour of a plain bar.</summary>
    public static readonly Color Green = new(0.25f, 0.85f, 0.16f);

    private static readonly Color Amber = new(1f, 0.68f, 0.12f), Red = new(1f, 0.22f, 0.12f);

    private float _shown, _time;

    public LedBar(float width = 620, float groove = 30)
    {
        Groove = groove;
        CustomMinimumSize = new Vector2(width, groove + 46);
        MouseFilter = MouseFilterEnum.Ignore;
    }

    /// <summary>How many LEDs the groove holds.</summary>
    public int Leds { get; init; } = 24;

    public float Groove { get; }

    /// <summary>Where the bar stands, 0 to 1, when no <see cref="Source"/> is given.</summary>
    public float Value { get; set; }

    /// <summary>Read each frame for the value, in place of <see cref="Value"/>.</summary>
    public Func<float>? Source { get; init; }

    /// <summary>The caption under the bar (already translated), in front of the share; none when it gives an empty text.</summary>
    public Func<string>? Caption { get; init; }

    /// <summary>The caption's colour.</summary>
    public Color CaptionColour { get; set; } = MenuTheme.Dim;

    /// <summary>Eased toward the value and never back, as loading only goes on.</summary>
    public bool OnlyForward { get; init; }

    /// <summary>Lit green up to half, amber up to three quarters, red beyond (the hack's trace-back).</summary>
    public bool Heat { get; init; }

    /// <summary>The lit colour of a bar without <see cref="Heat"/>.</summary>
    public Color Lit { get; set; } = Green;

    /// <summary>Whether the share in per cent is written after the caption.</summary>
    public bool ShowShare { get; init; } = true;

    /// <summary>How far it shows lit now, eased.</summary>
    public float Shown => _shown;

    /// <summary>Jumps to the value without easing (a fresh bar for a new board).</summary>
    public void Snap() => _shown = Mathf.Clamp(Source?.Invoke() ?? Value, 0, 1);

    public override void _Process(double delta)
    {
        _time += (float)delta;
        var target = Mathf.Clamp(Source?.Invoke() ?? Value, 0, 1);
        var eased = Mathf.Lerp(_shown, target, 1 - Mathf.Exp(-6 * (float)delta));
        _shown = OnlyForward ? Mathf.Max(_shown, eased) : eased;
        QueueRedraw();
    }

    private Color LitColour => !Heat ? Lit : _shown < 0.5f ? Green : _shown < 0.75f ? Green.Lerp(Amber, (_shown - 0.5f) / 0.25f) : Amber.Lerp(Red, Mathf.Min(1, (_shown - 0.75f) / 0.2f));

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

        // The LEDs: lit as far as the value has come, the last one partly; the blip runs along the lit ones.
        var inside = groove.Grow(-Mathf.Min(7, Groove * 0.23f));
        const float gap = 4;
        var width = (inside.Size.X - gap * (Leds - 1)) / Leds;
        var lit = _shown * Leds;
        var blip = lit >= 1 ? _time * 18 % (lit + 4) : -10;
        var colour = LitColour;
        var glow = Heat ? colour : MenuTheme.Glow;
        for (var i = 0; i < Leds; i++)
        {
            var led = new Rect2(inside.Position.X + i * (width + gap), inside.Position.Y, width, inside.Size.Y);
            var on = Mathf.Clamp(lit - i, 0, 1);
            var hot = Mathf.Clamp(1 - Mathf.Abs(i - blip) / 2.5f, 0, 1) * on;
            if (on > 0)
                DrawRect(led.Grow(3), new Color(glow, 0.16f * on + 0.2f * hot));
            DrawRect(led, Off.Lerp(colour, on).Lerp(new Color(0.85f, 1, 0.7f), hot * 0.7f));
            // The light on the lens's top.
            DrawRect(new Rect2(led.Position + new Vector2(1, 1), new Vector2(led.Size.X - 2, led.Size.Y * 0.3f)),
                new Color(1, 1, 1, 0.04f + 0.22f * on));
        }

        var share = $"{Mathf.RoundToInt(_shown * 100)} %";
        var caption = Caption?.Invoke() ?? "";
        var text = caption.Length > 0 ? ShowShare ? $"{caption.ToUpperInvariant()}   ·   {share}" : caption.ToUpperInvariant() : ShowShare ? share : "";
        if (text.Length > 0)
            DrawString(MenuTheme.Font, new Vector2(0, Groove + 34), text, HorizontalAlignment.Center, Size.X, 20, CaptionColour);
    }
}
