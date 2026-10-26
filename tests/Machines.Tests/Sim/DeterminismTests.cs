using Machines.Import.Model;
using Machines.Sim;
using Machines.Sim.World;

namespace Machines.Tests.Sim;

/// <summary>What lockstep needs: platform-independent maths and identical worlds from identical inputs.</summary>
public class DeterminismTests
{
    private static readonly Lazy<GameData> DataLoader = new(() => GameData.Load(RepoPaths.OriginalDataFolder()));

    [Fact]
    public void Deterministic_trigonometry_matches_the_library_closely()
    {
        for (var i = -2000; i <= 2000; i++)
        {
            var x = i * 0.0137f;
            Assert.Equal(Math.Sin(x), DetMath.Sin(x), 5e-6);
            Assert.Equal(Math.Cos(x), DetMath.Cos(x), 5e-6);
            Assert.Equal(Math.Atan(x), DetMath.Atan(x), 5e-6);
        }
        for (var i = 0; i < 360; i++)
        {
            var a = i * MathF.PI / 180;
            var (y, x) = (MathF.Sin(a) * 3, MathF.Cos(a) * 3);
            Assert.Equal(Math.Atan2(y, x), DetMath.Atan2(y, x), 1e-5);
        }
        for (var i = -100; i <= 100; i++)
            Assert.Equal(Math.Acos(i / 100f), DetMath.Acos(i / 100f), 5e-4);
    }

    [Fact]
    public void Two_worlds_from_the_same_seed_and_commands_keep_the_same_checksum()
    {
        GameWorld New() => MatchSetup.Create(DataLoader.Value, "m_desert", "m_desert",
            new Dictionary<Race, Controller> { [Race.Red] = Controller.Ai, [Race.Blue] = Controller.Ai }, seed: 11);
        var a = New();
        var b = New();
        Assert.Equal(a.Checksum(), b.Checksum());
        for (var i = 0; i < 6000; i++)
        {
            a.Step();
            b.Step();
            if (i % 500 == 0)
                Assert.Equal(a.Checksum(), b.Checksum());
        }
        Assert.Equal(a.Checksum(), b.Checksum());

        // A single changed hit point shows.
        var any = a.Actors.First();
        any.HitPoints--;
        Assert.NotEqual(a.Checksum(), b.Checksum());
    }
}
