using Machines.Game;
using Machines.Import.Model;
using Machines.Sim.Combat;
using Xunit;

namespace Machines.Tests.Sim;

/// <summary>Which weapons a held trigger fires: by mounting, not by place in the list, and never none on a one-gun machine.</summary>
public class WeaponTriggersTests
{
    private static IReadOnlyList<WeaponMount> Guns(params Mounting[] mountings) =>
        mountings.Select(mounting => new WeaponMount(mounting, new WeaponSpec { TypeName = "TEST", Damage = 1, Range = 1, ProjectileSpeed = 1, BurstRounds = 1, BurstDuration = 1, ReloadTime = 1, Presentation = [] })).ToList();

    private static int Mask(IReadOnlyList<WeaponMount> guns, params Mounting[] held) =>
        WeaponTriggers.Mask(guns, held.Contains);

    [Fact]
    public void Each_trigger_fires_its_own_mounting()
    {
        var guns = Guns(Mounting.Left, Mounting.Right, Mounting.Top);
        Assert.Equal(0b001, Mask(guns, Mounting.Left));
        Assert.Equal(0b010, Mask(guns, Mounting.Right));
        Assert.Equal(0b100, Mask(guns, Mounting.Top));
        Assert.Equal(0, Mask(guns));
    }

    [Fact]
    public void Held_triggers_fire_together()
    {
        var guns = Guns(Mounting.Left, Mounting.Right, Mounting.Top);
        Assert.Equal(0b011, Mask(guns, Mounting.Left, Mounting.Right));
        Assert.Equal(0b111, Mask(guns, Mounting.Left, Mounting.Right, Mounting.Top));
    }

    [Fact]
    public void It_goes_by_mounting_not_by_the_place_in_the_list()
    {
        // The list leaves empty places out: the right gun of a hero with no left one stands first.
        var guns = Guns(Mounting.Right, Mounting.Top);
        Assert.Equal(0b01, Mask(guns, Mounting.Right));
        Assert.Equal(0b10, Mask(guns, Mounting.Top));
    }

    [Theory]
    [InlineData(Mounting.Left)]
    [InlineData(Mounting.Right)]
    [InlineData(Mounting.Top)]
    public void A_machine_with_one_gun_fires_it_with_left_too(Mounting mounting)
    {
        var guns = Guns(mounting);
        Assert.Equal(1, Mask(guns, Mounting.Left));
        Assert.Equal(1, Mask(guns, mounting));
    }

    [Fact]
    public void A_lone_gun_answers_only_to_left_and_its_own_trigger()
    {
        var guns = Guns(Mounting.Right);
        Assert.Equal(0, Mask(guns, Mounting.Top));
    }

    [Fact]
    public void An_empty_left_place_leaves_nobody_without_a_left_trigger()
    {
        // The Reaper with the old Bolter on the right and the left place not yet filled: any mounting, spec 6.4.
        var guns = Guns(Mounting.Right);
        Assert.Equal(1, Mask(guns, Mounting.Left));
        Assert.Equal(1, Mask(guns, Mounting.Right));
    }

    [Fact]
    public void Two_guns_keep_to_their_own_triggers_though_a_third_place_is_empty()
    {
        var guns = Guns(Mounting.Right, Mounting.Top);
        Assert.Equal(0, Mask(guns, Mounting.Left));
    }

    [Fact]
    public void The_triggers_of_a_gun_say_what_the_display_shows()
    {
        Assert.Equal([Mounting.Left], WeaponTriggers.Of(Mounting.Left, 1));
        Assert.Equal([Mounting.Left, Mounting.Top], WeaponTriggers.Of(Mounting.Top, 1));
        Assert.Equal([Mounting.Top], WeaponTriggers.Of(Mounting.Top, 3));
    }

    // ---- the hands on the triggers -----------------------------------------------------------------

    private static bool Up(Mounting _) => false;

    [Fact]
    public void A_click_shorter_than_a_tick_still_reaches_the_gun()
    {
        var (hands, guns) = (new TriggerHands(), Guns(Mounting.Left, Mounting.Right));
        hands.Press(Mounting.Right, 10);
        hands.Release(Mounting.Right);
        Assert.Equal(0b10, hands.Mask(guns, Up, 10.05));
    }

    [Fact]
    public void A_press_is_kept_until_the_gun_has_opened_fire()
    {
        var (hands, guns) = (new TriggerHands(), Guns(Mounting.Left));
        hands.Press(Mounting.Left, 10);
        hands.Release(Mounting.Left);
        Assert.Equal(1, hands.Mask(guns, Up, 10.05));
        guns[0].RoundsLeft = 1;
        Assert.Equal(0, hands.Mask(guns, Up, 10.1));
        guns[0].RoundsLeft = 0;
        Assert.Equal(0, hands.Mask(guns, Up, 10.15));
    }

    [Fact]
    public void A_press_on_a_gun_still_reloading_does_not_wait_for_ever()
    {
        var (hands, guns) = (new TriggerHands(), Guns(Mounting.Left));
        hands.Press(Mounting.Left, 10);
        hands.Release(Mounting.Left);
        Assert.Equal(1, hands.Mask(guns, Up, 10 + TriggerHands.Patience));
        Assert.Equal(0, hands.Mask(guns, Up, 10.01 + TriggerHands.Patience));
    }

    [Fact]
    public void A_gun_opening_fire_serves_its_own_press_and_no_other()
    {
        var (hands, guns) = (new TriggerHands(), Guns(Mounting.Left, Mounting.Right));
        hands.Press(Mounting.Left, 10);
        hands.Press(Mounting.Right, 10);
        guns[0].RoundsLeft = 1;
        Assert.Equal(0b10, hands.Mask(guns, Up, 10.05));
    }

    [Fact]
    public void A_lone_gun_serves_a_press_of_the_left_trigger()
    {
        var (hands, guns) = (new TriggerHands(), Guns(Mounting.Right));
        hands.Press(Mounting.Left, 10);
        Assert.Equal(1, hands.Mask(guns, Up, 10.05));
        guns[0].RoundsLeft = 1;
        Assert.Equal(0, hands.Mask(guns, Up, 10.1));
    }

    [Fact]
    public void A_held_trigger_goes_on_firing_after_its_press_is_served()
    {
        var (hands, guns) = (new TriggerHands(), Guns(Mounting.Left));
        hands.Press(Mounting.Left, 10);
        guns[0].RoundsLeft = 1;
        Assert.Equal(1, hands.Mask(guns, hands.Down, 10.1));
        Assert.Equal(1, hands.Mask(guns, hands.Down, 20));
    }

    [Fact]
    public void Letting_go_forgets_what_is_held_and_owed()
    {
        var (hands, guns) = (new TriggerHands(), Guns(Mounting.Left));
        hands.Press(Mounting.Left, 10);
        hands.LetGo();
        Assert.False(hands.Down(Mounting.Left));
        Assert.Equal(0, hands.Mask(guns, hands.Down, 10.05));
    }
}
