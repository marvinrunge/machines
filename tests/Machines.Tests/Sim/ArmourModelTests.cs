using Machines.Sim.Combat;

namespace Machines.Tests.Sim;

public class ArmourModelTests
{
    [Fact]
    public void Armour_absorbs_all_but_one_point()
    {
        // A Scout (12 HP, 10 armour) hit by a Bolter round (4 damage).
        var after = ArmourModel.ApplyHit(new Durability(12, 10), 4);
        Assert.Equal(new Durability(11, 7), after);
    }

    [Fact]
    public void Hit_points_take_what_armour_cannot()
    {
        // A Knight Templar (90 HP, 80 armour) hit by a Vortex (150 damage).
        var after = ArmourModel.ApplyHit(new Durability(90, 80), 150);
        Assert.Equal(new Durability(20, 0), after);
    }

    [Fact]
    public void Many_small_hits_strip_armour_first()
    {
        var scout = new Durability(12, 10);
        for (var i = 0; i < 4; i++)
            scout = ArmourModel.ApplyHit(scout, 4);

        // Three hits absorb 3 each (9 armour) and cost 1 HP each; the fourth absorbs the last
        // armour point and costs 3 HP. From then on every hit lands in full.
        Assert.Equal(new Durability(6, 0), scout);
        scout = ArmourModel.ApplyHit(scout, 4);
        Assert.False(scout.IsDead);
        Assert.True(ArmourModel.ApplyHit(scout, 4).IsDead);
    }

    [Fact]
    public void Zero_damage_keeps_the_original_quirk()
    {
        Assert.Equal(new Durability(11, 11), ArmourModel.ApplyHit(new Durability(12, 10), 0));
    }

    [Fact]
    public void Armour_regenerates_up_to_its_maximum()
    {
        Assert.Equal(5.5f, ArmourModel.Regenerate(5f, 10, repairRatePerSecond: 0.25f, seconds: 2f));
        Assert.Equal(10f, ArmourModel.Regenerate(9f, 10, repairRatePerSecond: 0.25f, seconds: 60f));
    }
}
