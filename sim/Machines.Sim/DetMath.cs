namespace Machines.Sim;

/// <summary>
/// Trigonometry that gives the same bits on every machine. <c>MathF.Sin</c>, <c>Cos</c>, <c>Atan2</c> and
/// <c>Acos</c> call the platform's C library, which may round differently on Windows and Linux, and lockstep
/// multiplayer needs every player's simulation to agree exactly. These use only +, −, ×, ÷ and square roots,
/// which IEEE 754 defines exactly, accurate to a few parts in ten million.
/// </summary>
public static class DetMath
{
    public const float Pi = 3.14159265f;
    public const float Tau = 2 * Pi;
    private const float HalfPi = Pi / 2;
    private const float QuarterPi = Pi / 4;
    private const float TanEighthPi = 0.41421356f;

    public static float Sin(float x)
    {
        // Into [−π, π], then [−π/2, π/2] by symmetry; there a Taylor series to x¹¹ is good to 1e-7.
        x -= Tau * MathF.Round(x / Tau);
        if (x > HalfPi)
            x = Pi - x;
        else if (x < -HalfPi)
            x = -Pi - x;
        var x2 = x * x;
        return x * (1 - x2 / 6 * (1 - x2 / 20 * (1 - x2 / 42 * (1 - x2 / 72 * (1 - x2 / 110)))));
    }

    public static float Cos(float x) => Sin(x + HalfPi);

    public static float Atan(float x)
    {
        var negative = x < 0;
        if (negative)
            x = -x;
        float result;
        if (x > 1)
            result = HalfPi - SmallAtan(1 / x);
        else
            result = SmallAtan(x);
        return negative ? -result : result;
    }

    public static float Atan2(float y, float x)
    {
        if (x > 0)
            return Atan(y / x);
        if (x < 0)
            return y >= 0 ? Atan(y / x) + Pi : Atan(y / x) - Pi;
        return y > 0 ? HalfPi : y < 0 ? -HalfPi : 0;
    }

    public static float Acos(float x)
    {
        x = Math.Clamp(x, -1, 1);
        return Atan2(MathF.Sqrt(1 - x * x), x);
    }

    /// <summary>atan for 0 ≤ x ≤ 1: above tan(π/8) shifted by π/4, then a series to x¹⁵.</summary>
    private static float SmallAtan(float x)
    {
        var offset = 0f;
        if (x > TanEighthPi)
        {
            x = (x - 1) / (x + 1);
            offset = QuarterPi;
        }
        var x2 = x * x;
        var series = x * (1 - x2 * (1f / 3 - x2 * (1f / 5 - x2 * (1f / 7 - x2 * (1f / 9 - x2 * (1f / 11 - x2 * (1f / 13 - x2 / 15)))))));
        return offset + series;
    }
}
