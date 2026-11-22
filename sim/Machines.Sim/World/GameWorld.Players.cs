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
    /// Whether a player may give orders to an actor; <paramref name="issuer"/> null is the race's own authority, which may
    /// order all of it. Nobody orders another race's actors. In a race of several commanders a machine obeys its owner, and
    /// while the owner is away the teammate who keeps their machines; buildings obey everyone of the race. In a race of one
    /// player everyone of the race may order all of it, without the owner being looked at.
    /// </summary>
    /// <remarks>
    /// The race is checked here too (P1 design §0: "race must match"), so that the game may pick what a player commands by
    /// this alone; a command's own race was checked before it reaches <c>Own</c>, so commands are carried out as before.
    /// </remarks>
    public bool MayOrder(PlayerId? issuer, Actor actor)
    {
        if (issuer is not { } player)
            return true;
        if (!Plays(player, actor.Race))
            return false;
        if (!Players.Shared(actor.Race) || actor is not Machine)
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
    /// The players who left, joined or came back in one tick, given in player order, a player's leave before their return. A
    /// join (<see cref="PresenceChange.Joins"/>) first turns the bot's seat into the person's (<see cref="PlayerTable.Join"/>)
    /// and then is a return. Each is first noted as away or present. A race of one player is then handed to the computer or
    /// back one change after the other, as it always was (a bot's race of a battle joined late comes back to its person so);
    /// a race of several players settles once, after all of the tick's changes, in race order (<see cref="Settle"/>), so that
    /// one commander leaving while another comes back hands the race straight from the one to the other, a battle's player who
    /// leaves and comes back within the tick keeps their hero as it was, and a late joiner takes the bot's hero over afresh.
    /// </summary>
    /// <exception cref="KeyNotFoundException">A player the table does not know; then none of the changes is made.</exception>
    /// <exception cref="ArgumentException">A join of a seat that is no bot's, or joined already; then none of the changes is made.</exception>
    public void ChangePresence(IReadOnlyList<PresenceChange> changes)
    {
        var races = changes.Select(change => Players.RaceOf(change.Player)).ToList();
        if (changes.FirstOrDefault(change => change.Joins is not null && !Players.CanJoin(change.Player)) is { Joins: not null } refused)
            throw new ArgumentException($"player {refused.Player} is no bot's seat to join");
        if (changes.Where(change => change.Joins is not null).GroupBy(change => change.Player).FirstOrDefault(twice => twice.Count() > 1) is { } again)
            throw new ArgumentException($"player {again.Key} joins twice");
        var settling = new SortedSet<Race>();
        for (var i = 0; i < changes.Count; i++)
        {
            var (change, race) = (changes[i], races[i]);
            if (change.Joins is { } name)
                Players.Join(change.Player, name);
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

    /// <summary>Whether a race's comings and goings settle once per tick: a race of several players.</summary>
    private bool Settles(Race race) => Players.Shared(race);

    /// <summary>
    /// A race of several players after its players came or went (P1 design §2.8). In a battle each person of the race who
    /// is away has a bot drive their hero and each who is there drives it again (<see cref="AddBot"/>, <see cref="RemoveBot"/>,
    /// in player order), and the race is a player's while one of its people is there, else the computer's. In a Wired run
    /// the heroes stand, as a race of one player's does (the run has no stand-in): an away player's hero lets go of the
    /// wheel rather than drive on with their last input. In a skirmish, while a person of the race is there, the one with
    /// the lowest number keeps the machines of every away player (they may order them, and what they build is theirs), the
    /// race is a player's again and a stand-in AI that played it goes. Once the last person is gone, nobody keeps anything
    /// and the stand-in AI plays the whole race, as it takes over a race of one player; the machines stay whose they were.
    /// A machine driven by someone away, or by someone who may no longer order it, is let go of, as by a
    /// <see cref="DriveCommand"/> letting go of the wheel.
    /// </summary>
    private void Settle(Race race)
    {
        if (Battle is not null)
        {
            SettleBattle(race);
            return;
        }
        if (Wired is not null)
        {
            LetGoOfAwayWheels(race);
            return;
        }
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
                {
                    var stand = standIn();
                    stand.StandsIn = true;
                    AiControllers.Add(stand);
                }
            }
        }
        LetGoOfAwayWheels(race);
    }

    /// <summary>
    /// A player's turns come late (a lag mark, P2 design §2.6): every machine they drive lets go of the wheel, in id order,
    /// as by a <see cref="DriveCommand"/> letting go, so that it does not drive and fire on with their last input; their next
    /// drive takes the wheel again. Draws nothing from <see cref="Random"/>.
    /// </summary>
    public void LetGo(PlayerId player)
    {
        foreach (var machine in Machines.Where(m => m.DrivenByPlayer && m.Driver == player).OrderBy(m => m.Id).ToList())
            LetGoOfWheel(machine);
    }

    /// <summary>Every machine of a race driven by someone away, or by someone who may no longer order it, lets go of the wheel, in id order.</summary>
    private void LetGoOfAwayWheels(Race race)
    {
        foreach (var machine in Machines.Where(m => m.Race == race && m.DrivenByPlayer && m.Driver is { } driver
            && (Players.Away(driver) || !MayOrder(driver, m))).OrderBy(m => m.Id).ToList())
            LetGoOfWheel(machine);
    }

    /// <summary>
    /// A battle's race of several players after its people came or went: the hero of each who is away is the computer's, of
    /// each who is there their own; the race is a player's while one of its people is there. A bot from the start stays.
    /// </summary>
    private void SettleBattle(Race race)
    {
        var people = Players.PlayersOf(race).Where(id => Players[id].Kind == PlayerKind.Human).ToList();
        foreach (var person in people)
        {
            if (Players.Away(person))
                AddBot(person);
            else
                RemoveBot(person);
        }
        if (Races.TryGetValue(race, out var state))
            state.Controller = people.Any(id => !Players.Away(id)) ? Controller.Player : Controller.Ai;
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
