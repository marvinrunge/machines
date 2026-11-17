using Machines.Net;

namespace Machines.Tests.Net;

/// <summary>How fast a client runs the bundles it has in hand (P2 design §4.3).</summary>
public class BundlePacerTests
{
    [Theory]
    [InlineData(-1, 0)]
    [InlineData(0, 0)]
    [InlineData(1, 1)]
    [InlineData(3, 1)]
    [InlineData(4, 2)]
    [InlineData(5, 3)]
    [InlineData(59, 3)]
    [InlineData(60, 3)]
    [InlineData(599, 3)]
    [InlineData(600, 3)]
    public void A_slot_runs_one_bundle_within_the_buffer_and_up_to_three_beyond_it(int ready, int ticks) =>
        Assert.Equal(ticks, BundlePacer.TicksForSlot(ready));

    [Theory]
    [InlineData(0, 0)]
    [InlineData(3, 0)]
    [InlineData(5, 0)]
    [InlineData(59, 0)]
    [InlineData(60, 30)]
    [InlineData(599, 30)]
    [InlineData(600, 100)]
    [InlineData(6000, 100)]
    public void A_frame_catches_up_without_drawing_from_sixty_bundles_in_hand_and_longer_from_six_hundred(int ready, int milliseconds) =>
        Assert.Equal(TimeSpan.FromMilliseconds(milliseconds), BundlePacer.CatchUpBudget(ready));

    [Fact]
    public void A_client_a_few_ticks_behind_works_its_backlog_off_and_keeps_its_buffer()
    {
        // Twenty bundles behind, one more coming each slot: the slots run three until the buffer is all that is left.
        var ready = 20;
        var slots = 0;
        while (ready > BundlePacer.Buffer + 1)
        {
            ready += 1 - BundlePacer.TicksForSlot(ready);
            slots++;
        }
        Assert.Equal(BundlePacer.Buffer + 1, ready);
        Assert.InRange(slots, 8, 10);
        // From there one bundle comes and one runs each slot.
        Assert.Equal(1, BundlePacer.TicksForSlot(ready));
    }
}
