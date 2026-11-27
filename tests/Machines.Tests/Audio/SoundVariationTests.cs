using Machines.Game;
using Xunit;

namespace Machines.Tests.Audio;

/// <summary>Every sound effect a little higher or lower each time it plays (SoundVariation, user 2026-11-27).</summary>
public class SoundVariationTests
{
    [Fact]
    public void EveryOneShotStraysALittleInPitch()
    {
        Assert.Equal(1 - SoundVariation.PitchVary, SoundVariation.Varied(1, 0), 5);
        Assert.Equal(1, SoundVariation.Varied(1, 0.5f), 5);
        Assert.Equal(1 + SoundVariation.PitchVary, SoundVariation.Varied(1, 1), 5);
        // Around a pitch of its own (a whine played high), and never past the stray for a unit out of range.
        Assert.Equal(1.3f * (1 + SoundVariation.PitchVary), SoundVariation.Varied(1.3f, 1), 5);
        Assert.Equal(1 + SoundVariation.PitchVary, SoundVariation.Varied(1, 7), 5);
        // A slight stray: about half a semitone either way.
        Assert.InRange(SoundVariation.PitchVary, 0.02f, 0.06f);
    }
}
