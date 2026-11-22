using Machines.Import.Model;
using Machines.Sim.Story;

namespace Machines.Sim.World;

/// <summary>
/// The story played together (docs/STORY.md, "Koop"): up to three people, the host the Locator, Ike and Bob each a person or
/// the computer. A person may join a Grunt's seat the computer plays and takes its hero over as it stands (the same actor, so
/// orders, names and the director's notes about it stay); one who leaves hands it back to the computer. The Locator's seat is
/// the host's: while its person is away its hero stands, as a Wired hero does. Everything here happens on the ticks the
/// comings and goings are given for (<see cref="ChangePresence"/>), so every world of a co-op match agrees.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>
    /// Whether the computer plays a seat of the story now: a Grunt's seat that is a bot's (nobody joined it) or whose person
    /// is away. The Locator's seat never is; false outside a story and for a player who is no seat of it.
    /// </summary>
    public bool StoryBotPlays(PlayerId seat) => Story is { } run && run.Seats.Contains(seat) && seat != run.Locator
        && (Players[seat].Kind == PlayerKind.Bot || Players.Away(seat));

    /// <summary>
    /// The name a seat of the story shows (HUD cards, lines, the lobby): the person's who plays it, else what the host named
    /// the Grunt (<see cref="StoryOptions.IkeName"/>, <see cref="StoryOptions.BobName"/>), so a Grunt carries the host's name
    /// again once its player left (docs/STORY.md: "Steigt er aus, trägt der Grunt wieder den Namen des Hosts"). Null for a
    /// player who is no seat of the story.
    /// </summary>
    public string? StorySeatName(PlayerId seat)
    {
        if (Story is not { } run || run.Seats.IndexOf(seat) is var index && index < 0)
            return null;
        if (index > 0 && StoryBotPlays(seat))
            return index == 1 ? run.Options.IkeName : run.Options.BobName;
        return Players[seat].Name;
    }

    /// <summary>
    /// The story's seats after people came or went (from <see cref="Settle"/>, once a tick, after all of its changes): each
    /// Grunt's seat the computer plays now and did not before goes to the computer (<see cref="HandToCompanion"/>), each a
    /// person plays now and the computer did before goes back to its person (<see cref="TakeFromCompanion"/>), in seat
    /// order; then every hero driven by someone away lets go of the wheel (the Locator's while the host is away). The race
    /// stays the players'.
    /// </summary>
    private void SettleStory(Race race)
    {
        SettleStorySeats(Story!);
        LetGoOfAwayWheels(race);
    }

    /// <summary>Hands each Grunt's seat to the computer or back to its person as <see cref="StoryBotPlays"/> says, in seat order.</summary>
    private void SettleStorySeats(StoryRun run)
    {
        foreach (var seat in run.Seats.Skip(1))
        {
            if (StoryBotPlays(seat))
            {
                if (run.Bots.Add(seat))
                    HandToCompanion(run, seat);
            }
            else if (run.Bots.Remove(seat))
                TakeFromCompanion(seat);
        }
    }

    /// <summary>
    /// The computer takes a Grunt's hero: whoever drove it lets go of the wheel (their last input goes with it), it stands and
    /// then follows the Locator, while the Locator's hero stands; a crate it was pushing is set down. The companions' own mind (plan WP-F, CompanionBot) takes
    /// the seats of <see cref="StoryRun.Bots"/> from here.
    /// </summary>
    private void HandToCompanion(StoryRun run, PlayerId seat)
    {
        if (HeroOf(seat) is not { } grunt)
            return;
        if (grunt.DrivenByPlayer)
            LetGoOfWheel(grunt);
        DropPushed(run.Crew.Of(seat), grunt);
        StartAfresh(grunt);
        if (HeroOf(run.Locator) is { } locator)
            Send(grunt, new FollowOrder(locator.Id), locator.Position);
    }

    /// <summary>A person takes a Grunt's hero from the computer: it drops the computer's order and stands until they drive it.</summary>
    private void TakeFromCompanion(PlayerId seat)
    {
        if (HeroOf(seat) is { } grunt)
            StartAfresh(grunt);
    }
}
