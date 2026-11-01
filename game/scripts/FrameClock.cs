using Godot;

namespace Machines.Game;

/// <summary>
/// Seconds for what is seen and heard (a shaking view, a sound heard once in a burst): the wall clock, or the frames'
/// own when Godot draws at a fixed rate (<c>--fixed-fps</c>, <c>--write-movie</c>), where a frame may take far longer
/// to draw than it lasts in the film.
/// </summary>
public static class FrameClock
{
    private static double _frames;
    private static ulong _counted;

    public static double Now
    {
        get
        {
            if (Rate == 0)
                return Time.GetTicksMsec() / 1000.0;
            // Each frame drawn lasts 1/fps of the film's time, slowed with the engine's time scale.
            var drawn = Engine.GetProcessFrames();
            if (drawn != _counted)
            {
                _frames += (drawn - _counted) * Engine.TimeScale / Rate;
                _counted = drawn;
            }
            return _frames;
        }
    }

    // The fixed rate Godot runs at, from its command line; 0 for real time.
    private static int Rate => _rate ??= ReadRate();
    private static int? _rate;

    private static int ReadRate()
    {
        var arguments = OS.GetCmdlineArgs();
        var at = Array.IndexOf(arguments, "--fixed-fps");
        return at >= 0 && at + 1 < arguments.Length && int.TryParse(arguments[at + 1], out var fps) ? fps : 0;
    }
}
