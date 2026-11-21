using Godot;

namespace Machines.Game;

/// <summary>
/// How a ship in distress leans the view (the remake's own): a steady <see cref="Roll"/> and <see cref="Pitch"/> in degrees
/// (roll positive to the right, pitch positive nose up) and a slow sway of up to <see cref="Sway"/> degrees either way over
/// about <see cref="Period"/> seconds. The level itself never tilts; the camera does, the panel stays level.
/// </summary>
public readonly record struct CameraTilt(float Roll, float Pitch, float Sway = 0, float Period = 7)
{
    public bool Level => Roll == 0 && Pitch == 0 && Sway == 0;

    /// <summary>The turn it gives a camera at <paramref name="time"/> seconds, in the camera's own frame.</summary>
    public Basis At(double time)
    {
        var period = Mathf.Max(Period, 0.5f);
        var swing = Sway * Mathf.Sin(Mathf.Tau * (float)(time / period));
        var nod = Sway * 0.4f * Mathf.Sin(Mathf.Tau * (float)(time / (period * 1.618)) + 1.3f);
        return new Basis(Quaternion.FromEuler(new Vector3(Mathf.DegToRad(Pitch + nod), 0, Mathf.DegToRad(-(Roll + swing)))));
    }

    /// <summary>Part of the way from <paramref name="from"/> to <paramref name="to"/>.</summary>
    public static CameraTilt Lerp(CameraTilt from, CameraTilt to, float share) => new(
        Mathf.Lerp(from.Roll, to.Roll, share), Mathf.Lerp(from.Pitch, to.Pitch, share), Mathf.Lerp(from.Sway, to.Sway, share),
        share < 1 && from.Sway > 0 ? from.Period : to.Period);
}
