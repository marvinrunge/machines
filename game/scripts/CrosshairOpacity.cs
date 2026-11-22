using System.Globalization;

namespace Machines.Game;

/// <summary>
/// How strongly the first-person sight is drawn (Crosshair opacity, not in the original): from 10 to 100 %, in steps of 5 %,
/// half at first, so the view behind the sight shows through. <c>GameOptions.CrosshairOpacity</c> keeps it as
/// <c>display/crosshair_opacity</c>; settings saved before it was there read the default. Godot-free, so its rules are tested.
/// </summary>
public static class CrosshairOpacity
{
    /// <summary>The faintest and the strongest the sight can be drawn, the default, and the slider's step.</summary>
    public const float Lowest = 0.1f, Highest = 1f, Default = 0.5f, Step = 0.05f;

    /// <summary>A share within the range, on a step; anything that is not a number is the default.</summary>
    public static float Clamp(float value) =>
        float.IsFinite(value) ? Math.Clamp(MathF.Round(value / Step) * Step, Lowest, Highest) : Default;

    /// <summary>
    /// A share as written for one run (MACHINES_OPTIONS <c>crosshair_opacity=</c>): 0.5, or above 1 as percent, 50 or 50 %;
    /// null when it is no number.
    /// </summary>
    public static float? Parse(string text)
    {
        var trimmed = text.Trim().TrimEnd('%').Trim();
        if (!float.TryParse(trimmed, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) || !float.IsFinite(value))
            return null;
        return Clamp(value > Highest ? value / 100 : value);
    }

    /// <summary>The share as the options page shows it: "50 %".</summary>
    public static string Percent(float value) => $"{MathF.Round(Clamp(value) * 100).ToString(CultureInfo.InvariantCulture)} %";
}
