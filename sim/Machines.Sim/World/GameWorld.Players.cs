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
    /// which may order all of it. In a race of several commanders a machine obeys its owner, and while the owner is away the
    /// teammate who keeps their machines; buildings obey everyone of the race. In a race of one player everyone of the race
    /// may order all of it, without the owner being looked at. The race itself is checked where the command is carried out.
    /// </summary>
    public bool MayOrder(PlayerId? issuer, Actor actor)
    {
        if (issuer is not { } player || !Players.Shared(actor.Race) || actor is not Machine)
            return true;
        return actor.Owner == player || Players.KeeperOf(actor.Owner) == player;
    }

    /// <summary>
    /// Whose squadrons a player's machines of a race are in: the player's own in a race of several commanders that no
    /// computer plays as a whole, else null for the whole race's (a race of one player, and a shared race the stand-in AI
    /// took over, whose squadrons hold every machine of the race whatever its owner).
    /// </summary>
    public PlayerId? SquadScope(Race race, PlayerId owner) =>
        Players.Shared(race) && !AiControllers.Any(ai => ai.Race == race) ? owner : null;

    /// <summary>A player left (a network match): the computer takes over what they played.</summary>
    public void HandOverToAi(PlayerId player) => ChangePresence([new PresenceChange(player, Away: true)]);

    /// <summary>A player is back (they rejoined a network match) and takes over again what the computer played for them.</summary>
    public void HandBackFromAi(PlayerId player) => ChangePresence([new PresenceChange(player, Away: false)]);

    /// <summary>
    /// The players who left or came back in one tick, given in player order, a player's leave before their return. Each is
    /// first noted as away or present. A race of one player is then handed to the computer or back one change after the
    /// other, as it always was; a skirmish race of several commanders settles once, after all of the tick's changes, in race
    /// order (<see cref="Settle"/>), so that one commander leaving while another comes back hands the race straight from the
    /// one to the other. A battle's or Wired run's race of several players is still handed over one change after the other
    /// until heroes are kept by player (P1 design §2.8, slice S3).
    /// </summary>
    /// <exception cref="KeyNotFoundException">A player the table does not know; then none of the changes is made.</exception>
    public void ChangePresence(IReadOnlyList<PresenceChange> changes)
    {
        var races = changes.Select(change => Players.RaceOf(change.Player)).ToList();
        var settling = new SortedSet<Race>();
        for (var i = 0; i < changes.Count; i++)
        {
            var (change, race) = (changes[i], races[i]);
            Players.SetAway(change.Player, change.Away);
            if (Settles(race))
                settling.Add(race);
            else if (change.Away)
                LeaveRace(race);
            else
                ReturnRace(race);
        }
        foreach (var race in settling)
            Settle(race);
    }

    /// <summary>Whether a race's comings and goings settle once per tick: a skirmish race of several commanders.</summary>
    private bool Settles(Race race) => Players.Shared(race) && Battle is null && Wired is null;

    /// <summary>
    /// A skirmish race of several commanders after its players came or went (P1 design §2.8). While a person of the race is
    /// there, the one with the lowest number keeps the machines of every away player (they may order them, and what they
    /// build is theirs), the race is a player's again and a stand-in AI that played it goes. Once the last person is gone,
    /// nobody keeps anything and the stand-in AI plays the whole race, as it takes over a race of one player; the machines
    /// stay whose they were. A machine driven by someone away, or by someone who may no longer order it, is let go of, as
    /// by a <see cref="DriveCommand"/> letting go of the wheel.
    /// </summary>
    private void Settle(Race race)
    {
        var players = Players.PlayersOf(race);
        var present = players.Where(id => Players[id].Kind == PlayerKind.Human && !Players.Away(id)).ToList();
        PlayerId? keeper = present.Count > 0 ? present[0] : null;
        foreach (var player in players)
            Players.SetKeeper(player, Players.Away(player) ? keeper : null);
        if (Races.TryGetValue(race, out var state))
        {
            if (keeper is not null)
            {
                if (state.Controller == Controller.Ai)
                {
                    state.Controller = Controller.Player;
                    AiControllers.RemoveAll(ai => ai.Race == race);
                }
            }
            else if (state.Controller != Controller.Ai && StandIns.TryGetValue(race, out var standIn))
            {
                state.Controller = Controller.Ai;
                if (!AiControllers.Any(ai => ai.Race == race))
                    AiControllers.Add(standIn());
            }
        }
        foreach (var machine in Machines.Where(m => m.Race == race && m.DrivenByPlayer && m.Driver is { } driver
            && (Players.Away(driver) || !MayOrder(driver, m))).OrderBy(m => m.Id).ToList())
            LetGoOfWheel(machine);
    }

    /// <summary>Whether a player plays the race: a row of it, or, for a number from 0 to 3 without a row, the race of that number.</summary>
    private bool Plays(PlayerId player, Race race) =>
        Players.Contains(player) ? Players[player].Race == race : player.Value == (int)race;

    /// <summary>
    /// Puts a machine on a factory's queue for <paramref name="owner"/>; whose it is is kept only for a race of several
    /// players, so a race of one keeps a queue exactly as before.
    /// </summary>
    internal void Enqueue(Construction factory, ResearchKey machine, PlayerId owner) =>
        factory.Enqueue(machine, Players.Shared(factory.Race) ? owner : null);

    /// <summary>Takes an entry off a factory's queue, and its owner with it.</summary>
    internal static void Dequeue(Construction factory, int index) => factory.Dequeue(index);
}
