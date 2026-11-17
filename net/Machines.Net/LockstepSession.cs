using Machines.Sim.World;

namespace Machines.Net;

/// <summary>
/// Deterministic lockstep over a <see cref="GameWorld"/>. Every human player sends one <see cref="Turn"/> per tick,
/// scheduled <see cref="InputDelay"/> ticks ahead; a tick runs once every player's turn for it has arrived, with
/// all commands in player order, so every world does exactly the same. A checksum goes out every
/// <see cref="ChecksumInterval"/> ticks for the relay to compare.
/// </summary>
/// <remarks>
/// The human players are the world's own (<see cref="PlayerTable.Humans"/> of <see cref="GameWorld.Players"/>), so a session
/// waits for exactly the people its world was built with. Commands are carried out as they arrive back from serialisation,
/// also the local ones, so every world sees the same bits. A command whose race is not its sender's is dropped, and every
/// other is carried out as its sender's (<see cref="Command.Issuer"/>, stamped here and never sent). A player who leaves is
/// stood in for from the tick of their first missing turn, and plays again from the tick the relay names when they return;
/// no world can pass either tick before hearing of it (it would still be waiting for that player), so all of them hand over
/// alike. A tick's leaves and returns go to the world together (<see cref="GameWorld.ChangePresence"/>).
/// </remarks>
public sealed class LockstepSession
{
    private readonly Dictionary<(long Tick, PlayerId Player), Turn> _turns = [];
    // When each player was away, in the order it happened: from the tick they left until the tick they came back (or for good).
    private readonly SortedDictionary<PlayerId, List<(long From, long Until)>> _absent = [];
    private readonly List<NetMessage> _presence = [];
    private readonly List<Command> _pending = [];
    private readonly List<Turn> _played = [];
    private readonly SortedSet<PlayerId> _humans;
    private long _nextLocalTurn;
    private readonly bool _echoed;

    /// <param name="local">The player this client is, or null for a spectator or a replay.</param>
    /// <param name="firstLocalTurn">The first tick this client sends a turn for: later than the start when it rejoins.</param>
    /// <param name="echoed">Online: the relay sends this client's own turns back, and only those count.</param>
    public LockstepSession(GameWorld world, PlayerId? local, int inputDelay, int checksumInterval = 20,
        long? firstLocalTurn = null, bool echoed = false)
    {
        World = world;
        Local = local;
        _echoed = echoed;
        _humans = new SortedSet<PlayerId>(world.Players.Humans);
        InputDelay = Math.Max(1, inputDelay);
        ChecksumInterval = Math.Max(1, checksumInterval);
        _nextLocalTurn = firstLocalTurn ?? InputDelay;
    }

    public GameWorld World { get; }
    public PlayerId? Local { get; }
    public int InputDelay { get; }

    /// <summary>The people whose turns the session waits for, in player order: the world's human players.</summary>
    public IReadOnlyCollection<PlayerId> Humans => _humans;

    /// <summary>The tick of the local player's next turn: the commands queued now are carried out on it.</summary>
    public long NextLocalTurn => _nextLocalTurn;

    public int ChecksumInterval { get; }

    /// <summary>Messages for the relay: this client's turns and checksums.</summary>
    public event Action<NetMessage>? Outgoing;

    /// <summary>The first tick after which the worlds disagreed, once the relay has said so.</summary>
    public long? DesyncTick { get; private set; }

    /// <summary>Every turn carried out that held commands, as it was received, for a replay.</summary>
    public IReadOnlyList<Turn> Played => _played;

    /// <summary>
    /// Players leaving (<see cref="PlayerLeft"/>: the computer stands in for them) and coming back
    /// (<see cref="PlayerReturned"/>), in the order they happened, for a replay.
    /// </summary>
    public IReadOnlyList<NetMessage> Presence => _presence;

    /// <summary>A command of the local player, carried out <see cref="InputDelay"/> ticks from now.</summary>
    public void Queue(Command command) => _pending.Add(command);

    /// <summary>
    /// Sends the local turns up to <see cref="InputDelay"/> ticks ahead of the world. Call it every time the
    /// local clock passes a tick, whether or not the world can step.
    /// </summary>
    public void Pump()
    {
        if (Local is not { } local)
            return;
        while (_nextLocalTurn <= World.Tick + InputDelay)
        {
            var turn = new Turn(_nextLocalTurn, local, _pending.ToList());
            _pending.Clear();
            _nextLocalTurn++;
            // Keep the turn as everyone else will see it (online, it comes back from the relay).
            if (!_echoed)
            {
                var copy = (Turn)NetJson.Decode(NetJson.Encode(turn));
                _turns[(copy.Tick, local)] = copy;
            }
            Outgoing?.Invoke(turn);
        }
    }

    /// <summary>
    /// Takes what the relay (or a recording) brings: a human player's turn for a tick still to come, a human player leaving
    /// or coming back, and the news that the worlds differ. Anything else, and anything about a player who is no human of
    /// this world, is passed over.
    /// </summary>
    public void Receive(NetMessage message)
    {
        switch (message)
        {
            case Turn turn when _humans.Contains(turn.Player) && turn.Tick >= World.Tick:
                _turns[(turn.Tick, turn.Player)] = turn;
                break;
            case PlayerLeft left when _humans.Contains(left.Player) && Absences(left.Player).All(a => a.Until <= left.FromTick):
                Absences(left.Player).Add((left.FromTick, long.MaxValue));
                _presence.Add(left);
                break;
            case PlayerReturned back when _humans.Contains(back.Player) && Absences(back.Player) is [.., (var from, long.MaxValue)] away
                                          && back.FromTick >= from:
                away[^1] = (from, back.FromTick);
                _presence.Add(back);
                break;
            case Desync desync:
                DesyncTick ??= desync.Tick;
                break;
        }
    }

    private List<(long From, long Until)> Absences(PlayerId player) =>
        _absent.TryGetValue(player, out var list) ? list : _absent[player] = [];

    /// <summary>Whether a player was away at a tick (left and not yet back).</summary>
    public bool Away(PlayerId player, long tick) =>
        _absent.TryGetValue(player, out var list) && list.Any(a => tick >= a.From && tick < a.Until);

    /// <summary>Whether every player's turn for the next tick has arrived.</summary>
    public bool CanStep => Waiting().Count == 0;

    /// <summary>The players whose turn for the next tick is missing.</summary>
    public IReadOnlyList<PlayerId> Waiting() => WaitingAt(World.Tick);

    private List<PlayerId> WaitingAt(long tick) =>
        tick < InputDelay ? [] : _humans.Where(player => !Away(player, tick) && !_turns.ContainsKey((tick, player))).ToList();

    /// <summary>How many ticks in a row could run right now, counting up to <paramref name="limit"/>.</summary>
    public int Ready(int limit)
    {
        var ready = 0;
        while (ready < limit && WaitingAt(World.Tick + ready).Count == 0)
            ready++;
        return ready;
    }

    /// <summary>
    /// Carries out the next tick if every turn for it is in; returns whether it did. The tick's leaves and returns go to the
    /// world first, all in one change (player order, each player's in the order they happened), then every present player's
    /// turn in player order.
    /// </summary>
    public bool TryStep()
    {
        if (!CanStep)
            return false;
        var tick = World.Tick;
        if (PresenceAt(tick) is { Count: > 0 } changes)
            World.ChangePresence(changes);
        foreach (var turn in TurnsFor(tick))
        {
            var race = World.Players.RaceOf(turn.Player);
            foreach (var command in turn.Commands.Where(c => c.Race == race))
                World.Issue(command with { Issuer = turn.Player });
            if (turn.Commands.Count > 0)
                _played.Add(turn);
        }
        World.Step();
        if (World.Tick % ChecksumInterval == 0 && Local is { } local)
            Outgoing?.Invoke(new ChecksumReport(World.Tick, local, World.Checksum()));
        return true;
    }

    /// <summary>
    /// The players who leave or come back at a tick, in player order; a player's own changes in the order they happened, so
    /// that one who leaves and is back within the tick leaves first, and one who is back and leaves again is back first.
    /// </summary>
    private List<PresenceChange> PresenceAt(long tick)
    {
        var changes = new List<PresenceChange>();
        foreach (var (player, absences) in _absent)
        {
            foreach (var (from, until) in absences)
            {
                if (from == tick)
                    changes.Add(new PresenceChange(player, Away: true));
                if (until == tick)
                    changes.Add(new PresenceChange(player, Away: false));
            }
        }
        return changes;
    }

    /// <summary>
    /// The turns a tick is played with, in player order: every present human's, taken out of those received. The one place a
    /// tick's turns come from.
    /// </summary>
    private List<Turn> TurnsFor(long tick)
    {
        var turns = new List<Turn>();
        foreach (var player in _humans)
        {
            if (_turns.Remove((tick, player), out var turn) && !Away(player, tick))
                turns.Add(turn);
        }
        return turns;
    }
}
