using Machines.Import.Model;
using Machines.Sim.World;

namespace Machines.Net;

/// <summary>
/// Deterministic lockstep over a <see cref="GameWorld"/>. Every human player sends one <see cref="Turn"/> per tick,
/// scheduled <see cref="InputDelay"/> ticks ahead; a tick runs once every player's turn for it has arrived, with
/// all commands in race order, so every world does exactly the same. A checksum goes out every
/// <see cref="ChecksumInterval"/> ticks for the relay to compare.
/// </summary>
/// <remarks>
/// Commands are carried out as they arrive back from serialisation, also the local ones, so every world sees
/// the same bits. A command whose race is not its sender's is dropped. A player who leaves hands their race to
/// the AI from the tick of their first missing turn, and takes it back from the tick the relay names when they
/// return; no world can pass either tick before hearing of it (it would still be waiting for that player), so
/// all of them hand over alike.
/// </remarks>
public sealed class LockstepSession
{
    private readonly Dictionary<(long Tick, Race Race), Turn> _turns = [];
    // When each player was away: from the tick they left until the tick they came back (or for good).
    private readonly Dictionary<Race, List<(long From, long Until)>> _absent = [];
    private readonly List<NetMessage> _presence = [];
    private readonly List<Command> _pending = [];
    private readonly List<Turn> _played = [];
    private readonly SortedSet<Race> _humans;
    private long _nextLocalTurn;
    private readonly bool _echoed;

    /// <param name="local">The race this client plays, or null for a spectator or a replay.</param>
    /// <param name="firstLocalTurn">The first tick this client sends a turn for: later than the start when it rejoins.</param>
    /// <param name="echoed">Online: the relay sends this client's own turns back, and only those count.</param>
    public LockstepSession(GameWorld world, Race? local, IEnumerable<Race> humans, int inputDelay, int checksumInterval = 20,
        long? firstLocalTurn = null, bool echoed = false)
    {
        World = world;
        Local = local;
        _echoed = echoed;
        _humans = new SortedSet<Race>(humans);
        InputDelay = Math.Max(1, inputDelay);
        ChecksumInterval = Math.Max(1, checksumInterval);
        _nextLocalTurn = firstLocalTurn ?? InputDelay;
    }

    public GameWorld World { get; }
    public Race? Local { get; }
    public int InputDelay { get; }
    public int ChecksumInterval { get; }

    /// <summary>Messages for the relay: this client's turns and checksums.</summary>
    public event Action<NetMessage>? Outgoing;

    /// <summary>The first tick after which the worlds disagreed, once the relay has said so.</summary>
    public long? DesyncTick { get; private set; }

    /// <summary>Every turn carried out that held commands, for a replay.</summary>
    public IReadOnlyList<Turn> Played => _played;

    /// <summary>
    /// Players leaving (<see cref="PlayerLeft"/>: the AI takes their race over) and coming back
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

    public void Receive(NetMessage message)
    {
        switch (message)
        {
            case Turn turn when _humans.Contains(turn.Race) && turn.Tick >= World.Tick:
                _turns[(turn.Tick, turn.Race)] = turn;
                break;
            case PlayerLeft left when Absences(left.Race).All(a => a.Until <= left.FromTick):
                Absences(left.Race).Add((left.FromTick, long.MaxValue));
                _presence.Add(left);
                break;
            case PlayerReturned back when Absences(back.Race) is [.., (var from, long.MaxValue)] away && back.FromTick >= from:
                away[^1] = (from, back.FromTick);
                _presence.Add(back);
                break;
            case Desync desync:
                DesyncTick ??= desync.Tick;
                break;
        }
    }

    private List<(long From, long Until)> Absences(Race race) =>
        _absent.TryGetValue(race, out var list) ? list : _absent[race] = [];

    /// <summary>Whether a player was away at a tick (left and not yet back).</summary>
    public bool Away(Race race, long tick) =>
        _absent.TryGetValue(race, out var list) && list.Any(a => tick >= a.From && tick < a.Until);

    /// <summary>Whether every player's turn for the next tick has arrived.</summary>
    public bool CanStep => Waiting().Count == 0;

    /// <summary>The players whose turn for the next tick is missing.</summary>
    public IReadOnlyList<Race> Waiting() => WaitingAt(World.Tick);

    private List<Race> WaitingAt(long tick) =>
        tick < InputDelay ? [] : _humans.Where(race => !Away(race, tick) && !_turns.ContainsKey((tick, race))).ToList();

    /// <summary>How many ticks in a row could run right now, counting up to <paramref name="limit"/>.</summary>
    public int Ready(int limit)
    {
        var ready = 0;
        while (ready < limit && WaitingAt(World.Tick + ready).Count == 0)
            ready++;
        return ready;
    }

    /// <summary>Carries out the next tick if every turn for it is in; returns whether it did.</summary>
    public bool TryStep()
    {
        if (!CanStep)
            return false;
        var tick = World.Tick;
        foreach (var (race, absences) in _absent.OrderBy(a => a.Key))
        {
            if (absences.Any(a => a.From == tick))
                World.HandOverToAi(race);
            if (absences.Any(a => a.Until == tick))
                World.HandBackFromAi(race);
        }
        foreach (var race in _humans)
        {
            if (!_turns.Remove((tick, race), out var turn) || Away(race, tick))
                continue;
            foreach (var command in turn.Commands.Where(c => c.Race == race))
                World.Issue(command);
            if (turn.Commands.Count > 0)
                _played.Add(turn);
        }
        World.Step();
        if (World.Tick % ChecksumInterval == 0 && Local is { } local)
            Outgoing?.Invoke(new ChecksumReport(World.Tick, local, World.Checksum()));
        return true;
    }
}
