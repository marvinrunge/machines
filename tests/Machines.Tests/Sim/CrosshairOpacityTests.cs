using Machines.Game;
using Xunit;

namespace Machines.Tests.Sim;

/// <summary>
/// The crosshair's opacity (CrosshairOpacity, kept by GameOptions as display/crosshair_opacity): half at first, 10 to 100 %
/// in steps of 5, and whatever was saved or asked for comes back within that.
/// </summary>
public class CrosshairOpacityTests
{
    [Fact]
    public void HalfAtFirst()
    {
        Assert.Equal(0.5f, CrosshairOpacity.Default);
        Assert.Equal("50 %", CrosshairOpacity.Percent(CrosshairOpacity.Default));
        // Settings saved before the key was there, or with something else under it, read the default (GameOptions.Read).
        Assert.Equal(CrosshairOpacity.Default, CrosshairOpacity.Clamp(float.NaN));
        Assert.Equal(CrosshairOpacity.Default, CrosshairOpacity.Clamp(float.PositiveInfinity));
    }

    [Theory]
    [InlineData(0f, 0.1f)]
    [InlineData(-3f, 0.1f)]
    [InlineData(0.04f, 0.1f)]
    [InlineData(0.1f, 0.1f)]
    [InlineData(0.5f, 0.5f)]
    [InlineData(0.73f, 0.75f)]
    [InlineData(1f, 1f)]
    [InlineData(7.5f, 1f)]
    public void StaysWithinTheRangeOnAStep(float asked, float kept)
    {
        Assert.Equal(kept, CrosshairOpacity.Clamp(asked), 4);
    }

    [Fact]
    public void AValueSavedComesBackAsItWas()
    {
        // GameOptions writes the clamped share and reads it back through Clamp: every step of the slider survives the trip.
        for (var percent = 10; percent <= 100; percent += 5)
        {
            var share = CrosshairOpacity.Clamp(percent / 100f);
            var saved = (double)share;
            Assert.Equal(share, CrosshairOpacity.Clamp((float)saved));
            Assert.Equal($"{percent} %", CrosshairOpacity.Percent(share));
        }
    }

    [Theory]
    [InlineData("0.5", 0.5f)]
    [InlineData("1", 1f)]
    [InlineData("80", 0.8f)]
    [InlineData("25 %", 0.25f)]
    [InlineData("0.02", 0.1f)]
    [InlineData("400", 1f)]
    public void ARunCanAskForOne(string text, float share)
    {
        Assert.Equal(share, CrosshairOpacity.Parse(text)!.Value, 4);
    }

    [Theory]
    [InlineData("")]
    [InlineData("half")]
    [InlineData("NaN")]
    public void NoNumberIsNoOpacity(string text)
    {
        Assert.Null(CrosshairOpacity.Parse(text));
    }
}
