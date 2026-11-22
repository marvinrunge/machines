namespace Machines.Game;

/// <summary>
/// A damaged lamp's flicker (the story's trio: their lamps came through the update badly): mostly a steady, slightly unsteady
/// glow, and now and then, every 3 to 10 seconds at irregular times, a short dip, a stutter of a few quick flickers, or rarely a
/// cut-out of up to a second that sputters back. Each lamp has its own rhythm (seeded by its actor's id), so the three never
/// flicker together. The view's alone: the sim's lamps, and the hunters' sight that reads them, do not flicker.
/// </summary>
public sealed class LampFlicker
{
    private enum Kind { Dip, Stutter, CutOut }

    private readonly Random _random;
    private double _next, _start, _length;
    private Kind _kind;
    private float _depth;
    private readonly double _hum;

    public LampFlicker(int seed)
    {
        _random = new Random(unchecked(seed * 7919 + 17));
        _hum = _random.NextDouble() * 10;
        _next = 1 + _random.NextDouble() * 4;
    }

    /// <summary>How bright the lamp is at a time of the view's clock (seconds), as a share of its full brightness: 0 to 1.</summary>
    public float At(double now)
    {
        if (now >= _next)
            Schedule(now);
        // A faint unsteadiness all the time: two slow waves and a quick one.
        var hum = 0.9f + 0.05f * (float)Math.Sin(now * 2.3 + _hum) + 0.03f * (float)Math.Sin(now * 7.9 + 2 * _hum)
            + 0.02f * (float)Math.Sin(now * 31 + _hum);
        var into = now - _start;
        if (into < 0 || into >= _length)
            return hum;
        var share = (float)(into / _length);
        return _kind switch
        {
            // Sags and comes back.
            Kind.Dip => hum * (1 - _depth * (float)Math.Sin(share * Math.PI)),
            // Quick flickers, harder at first.
            Kind.Stutter => ((int)(into * 22) % 3 == 0 ? 0.08f : hum * (0.55f + 0.45f * share)),
            // Out, then it sputters back on.
            _ => share < 0.7f ? 0.02f : ((int)(into * 30) % 2 == 0 ? 0.15f : hum * 0.8f),
        };
    }

    private void Schedule(double now)
    {
        var roll = _random.NextDouble();
        (_kind, _length, _depth) = roll switch
        {
            < 0.55 => (Kind.Dip, 0.12 + _random.NextDouble() * 0.3, 0.4f + (float)_random.NextDouble() * 0.45f),
            < 0.9 => (Kind.Stutter, 0.25 + _random.NextDouble() * 0.45, 0f),
            _ => (Kind.CutOut, 0.5 + _random.NextDouble() * 0.7, 1f),
        };
        _start = now;
        // The next one comes after a quiet while: 3 to 10 s, sometimes another right after (a bad contact).
        _next = now + _length + (_random.NextDouble() < 0.2 ? 0.2 + _random.NextDouble() * 0.5 : 3 + _random.NextDouble() * 7);
    }
}
