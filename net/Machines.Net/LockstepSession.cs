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
/// the same bits. A command whose race is not its sender's is dropped.
/// </remarks>
public sealed class LockstepSession
{
    private readonly Dictionary<(long Tick, Race Race), Turn> _turns = [];
    private readonly Dictionary<Race, long> _leftAt = [];
    private readonly List<Command> _pending = [];
    private readonly List<Turn> _played = [];
    private readonly SortedSet<Race> _humans;
    private long _nextLocalTurn;

    /// <param name="local">The race this client plays, or null for a spectator or a replay.</param>
    public LockstepSession(GameWorld world, Race? local, IEnumerable<Race> humans, int inputDelay, int checksumInterval = 20)
    {
        World = world;
        Local = local;
        _humans = new SortedSet<Race>(humans);
        InputDelay = Math.Max(1, inputDelay);
        ChecksumInterval = Math.Max(1, checksumInterval);
        _nextLocalTurn = InputDelay;
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
            // Keep the turn as everyone else will see it.
            var copy = (Turn)NetJson.Decode(NetJson.Encode(turn));
            _turns[(copy.Tick, local)] = copy;
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
            case PlayerLeft left:
                _leftAt[left.Race] = left.FromTick;
                break;
            case Desync desync:
                DesyncTick ??= desync.Tick;
                break;
        }
    }

    /// <summary>Whether every player's turn for the next tick has arrived.</summary>
    public bool CanStep => Waiting().Count == 0;

    /// <summary>The players whose turn for the next tick is missing.</summary>
    public IReadOnlyList<Race> Waiting()
    {
        var tick = World.Tick;
        if (tick < InputDelay)
            return [];
        return _humans.Where(race => !(_leftAt.TryGetValue(race, out var left) && tick >= left) && !_turns.ContainsKey((tick, race))).ToList();
    }

    /// <summary>Carries out the next tick if every turn for it is in; returns whether it did.</summary>
    public bool TryStep()
    {
        if (!CanStep)
            return false;
        var tick = World.Tick;
        foreach (var race in _humans)
        {
            if (!_turns.Remove((tick, race), out var turn))
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
