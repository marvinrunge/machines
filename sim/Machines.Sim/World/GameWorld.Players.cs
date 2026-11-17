using Machines.Import.Model;

namespace Machines.Sim.World;

/// <summary>
/// The players of a match apart from their races (online plan P1): the table the match was built from, who may order what,
/// and players leaving and coming back. With one player per race every rule here gives what the race alone gave before.
/// </summary>
public sealed partial class GameWorld
{
    /// <summary>The players of the match and who of them is away; empty for a world built without <see cref="MatchSetup"/>.</summary>
    public PlayerTable Players { get; internal set; } = PlayerTable.Empty;

    /// <summary>
    /// Whether a player may give orders to an actor of their race; <paramref name="issuer"/> null is the race's own authority,
    /// which may order all of it. The race itself is checked where the command is carried out.
    /// </summary>
    /// <remarks>Every player of a race may order all of it until the rules of several commanders come (P1 design §2.2, slice S2).</remarks>
    public bool MayOrder(PlayerId? issuer, Actor actor) => true;

    /// <summary>
    /// Whose squadrons a player's machines of a race are in: the player's own, or null for the whole race's. The whole race's
    /// until squadrons are kept per commander (P1 design §2.3, slice S2).
    /// </summary>
    public PlayerId? SquadScope(Race race, PlayerId owner) => null;

    /// <summary>A player left (a network match): the computer takes over what they played.</summary>
    public void HandOverToAi(PlayerId player) => ChangePresence([new PresenceChange(player, Away: true)]);

    /// <summary>A player is back (they rejoined a network match) and takes over again what the computer played for them.</summary>
    public void HandBackFromAi(PlayerId player) => ChangePresence([new PresenceChange(player, Away: false)]);

    /// <summary>
    /// The players who left or came back in one tick, given in player order, a player's leave before their return. Each is
    /// noted as away or present, and its race handed to the computer or back, one change after the other, as a race with one
    /// player always was (a race of several settles once for all of them from P1 slice S2 on).
    /// </summary>
    /// <exception cref="KeyNotFoundException">A player the table does not know.</exception>
    public void ChangePresence(IReadOnlyList<PresenceChange> changes)
    {
        foreach (var change in changes)
        {
            var race = Players.RaceOf(change.Player);
            Players.SetAway(change.Player, change.Away);
            if (change.Away)
                LeaveRace(race);
            else
                ReturnRace(race);
        }
    }

    /// <summary>
    /// Puts a machine on a factory's queue for <paramref name="owner"/>; whose it is is kept only for a race of several
    /// players, so a race of one keeps a queue exactly as before.
    /// </summary>
    internal void Enqueue(Construction factory, ResearchKey machine, PlayerId owner) =>
        factory.Enqueue(machine, Players.Shared(factory.Race) ? owner : null);

    /// <summary>Takes an entry off a factory's queue, and its owner with it.</summary>
    internal static void Dequeue(Construction factory, int index) => factory.Dequeue(index);
}
