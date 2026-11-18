using Machines.Sim.World;

namespace Machines.Net;

/// <summary>When a <see cref="LockstepSession"/> may run a tick (P2 design §4.1).</summary>
public enum SessionMode
{
    /// <summary>
    /// Once every present human's turn for the tick is in: offline play, local co-op, replays and saves, and every golden
    /// and regression test.
    /// </summary>
    Turns,

    /// <summary>
    /// Once the relay's <see cref="Bundle"/> for the tick is in, whoever's turns it holds: online, where the relay keeps the
    /// beat and nobody is waited for.
    /// </summary>
    Bundles,
}

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
/// <para>
/// In <see cref="SessionMode.Bundles"/> (online, P2 design §4.1) a tick runs once the relay's bundle for it is in: the bundle
/// carries every turn placed on the tick, the local player's too, and the tick's comings and goings, so a world never waits
/// for a person, and the relay's numbering alone says what every world runs. Both modes run a tick with the same body
/// (<see cref="TryStep"/>): the tick's comings and goings, the lag marks' letting go, the turns in player order, the step and
/// the checksum. A player who joins late (<see cref="PlayerJoined"/>) is a human of the session from the moment it is known,
/// held out of the world until the join's tick by that tick alone, never by an absence, so that no world marks the bot's row
/// away whenever it learns of the join.
/// </para>
/// </remarks>
public sealed class LockstepSession
{
    private readonly Dictionary<(long Tick, PlayerId Player), Turn> _turns = [];
    // When each player was away, in the order it happened: from the tick they left until the tick they came back (or for good).
    private readonly SortedDictionary<PlayerId, List<(long From, long Until)>> _absent = [];
    // The tick each late joiner takes their seat from, and the name they take it under.
    private readonly SortedDictionary<PlayerId, (long From, string Name)> _joinAt = [];
    // The lag marks of each player, in the order they came: from which tick on they lag or no longer do.
    private readonly SortedDictionary<PlayerId, List<(long From, bool Lagging)>> _marks = [];
    // The comings and goings still to be applied, by their tick, in the order they came; for PresenceApplied.
    private readonly SortedDictionary<long, List<NetMessage>> _notices = [];
    private readonly List<NetMessage> _presence = [];
    private readonly List<Command> _pending = [];
    private readonly List<Turn> _played = [];
    private readonly SortedSet<PlayerId> _humans;
    private long _nextLocalTurn;
    private readonly SessionMode _mode;

    // Bundle mode: the tick of the next bundle the relay sends; whether a gap in its numbering stopped taking any.
    private long _nextBundle;
    private bool _broken, _plain;
    // Bundle mode: the sealed segments of the relay's log still compressed, oldest first, decoded when the world reaches them;
    // the pieces of the one coming in; and the bundles that came after them, taken once every segment before is decoded.
    private readonly Queue<(long From, long To, byte[] Data)> _sealed = [];
    private List<LogPart>? _gathering;
    private readonly Queue<Bundle> _waiting = [];
    // Bundle mode: the stamps of the local player's turns with commands that have not landed in a bundle yet, in order.
    private readonly List<long> _onTheWay = [];

    /// <param name="local">The player this client is, or null for a spectator or a replay.</param>
    /// <param name="firstLocalTurn">Turn mode: the first tick this client sends a turn for (a saved game carried on).</param>
    /// <param name="mode">
    /// When a tick may run: once every turn for it is in (offline, recordings, saves), or once the relay's bundle is (online,
    /// where the bundle carries this client's own turns as everyone else's).
    /// </param>
    public LockstepSession(GameWorld world, PlayerId? local, int inputDelay, int checksumInterval = 20,
        long? firstLocalTurn = null, SessionMode mode = SessionMode.Turns)
    {
        World = world;
        Local = local;
        _mode = mode;
        _humans = new SortedSet<PlayerId>(world.Players.Humans);
        InputDelay = Math.Max(1, inputDelay);
        ChecksumInterval = Math.Max(1, checksumInterval);
        _nextLocalTurn = firstLocalTurn ?? InputDelay;
    }

    public GameWorld World { get; }
    public PlayerId? Local { get; }

    /// <summary>When a tick may run.</summary>
    public SessionMode Mode => _mode;

    /// <summary>
    /// Ticks between giving a command and every world carrying it out. In bundle mode the relay's, as the newest bundle in
    /// hand carries it (it adapts to the players' lines, P2 design §2.7).
    /// </summary>
    public int InputDelay { get; private set; }

    /// <summary>The people whose turns the session waits for, in player order: the world's human players, and late joiners once known.</summary>
    public IReadOnlyCollection<PlayerId> Humans => _humans;

    /// <summary>The tick of the local player's next turn: the commands queued now are carried out on it.</summary>
    public long NextLocalTurn => _nextLocalTurn;

    public int ChecksumInterval { get; }

    /// <summary>Bundle mode: the tick of the newest bundle in hand, −1 before the first.</summary>
    public long NewestBundle => _nextBundle - 1;

    /// <summary>Messages for the relay: this client's turns and checksums.</summary>
    public event Action<NetMessage>? Outgoing;

    /// <summary>
    /// A coming or going applied to the world, raised in <see cref="TryStep"/> after the tick's change of presence and lag
    /// marks, for each entry of the tick in player order: the world then is as the change left it (P2 design §4.1).
    /// </summary>
    public event Action<NetMessage>? PresenceApplied;

    /// <summary>
    /// Bundle mode: a bundle broke the relay's numbering (the tick expected, the tick that came). The session takes no bundle
    /// after it; the game rejoins.
    /// </summary>
    public event Action<long, long>? Gap;

    /// <summary>
    /// Bundle mode: the local player's turn stamped for a tick (the first) landed on a bundle (the second), where its commands
    /// are carried out; the same for an on-time turn, later for a late one (P2 design §4.4).
    /// </summary>
    public event Action<long, long>? Landed;

    /// <summary>Every turn carried out that held commands, at the tick it ran on, for a replay.</summary>
    public IReadOnlyList<Turn> Played => _played;

    /// <summary>
    /// Players leaving (<see cref="PlayerLeft"/>: the computer stands in for them), coming back
    /// (<see cref="PlayerReturned"/>), joining late (<see cref="PlayerJoined"/>) and lagging (<see cref="PlayerLagging"/>), in
    /// the order they happened, for a replay.
    /// </summary>
    public IReadOnlyList<NetMessage> Presence => _presence;

    /// <summary>
    /// A command of the local player, carried out <see cref="InputDelay"/> ticks from now. In bundle mode a drive replaces the
    /// one still queued for the same machine: the game queues one every slot while the world waits for bundles.
    /// </summary>
    public void Queue(Command command)
    {
        if (_mode == SessionMode.Bundles && command is DriveCommand drive)
            _pending.RemoveAll(queued => queued is DriveCommand earlier && earlier.Machine == drive.Machine);
        _pending.Add(command);
    }

    /// <summary>
    /// Sends the local turns up to <see cref="InputDelay"/> ticks ahead of the world. Call it every time the
    /// local clock passes a tick, whether or not the world can step.
    /// </summary>
    /// <remarks>
    /// In bundle mode each call sends at most one turn, stamped <see cref="InputDelay"/> ticks past the world, and nothing when
    /// a turn for that tick or later is gone already (P2 design §4.2): ticks the world went past get none, which is nothing
    /// new, and the stamps rise.
    /// </remarks>
    public void Pump()
    {
        if (Local is not { } local)
            return;
        if (_mode == SessionMode.Bundles)
        {
            var target = World.Tick + (long)InputDelay;
            if (target < _nextLocalTurn)
                return;
            var stamped = new Turn(target, local, _pending.ToList(), Seen: World.Tick - 1L, Held: (int)Math.Max(0, _nextBundle - World.Tick));
            _pending.Clear();
            _nextLocalTurn = target + 1;
            if (stamped.Commands.Count > 0)
                _onTheWay.Add(target);
            Outgoing?.Invoke(stamped);
            return;
        }
        while (_nextLocalTurn <= World.Tick + InputDelay)
        {
            var turn = new Turn(_nextLocalTurn, local, _pending.ToList());
            _pending.Clear();
            _nextLocalTurn++;
            // Keep the turn as everyone else will see it: through JSON, as the others (and a recording) get it.
            var copy = (Turn)NetJson.Decode(NetJson.Encode(turn));
            _turns[(copy.Tick, local)] = copy;
            Outgoing?.Invoke(turn);
        }
    }

    /// <summary>
    /// Takes what the relay (or a recording) brings. In turn mode: a human player's turn for a tick still to come, a human
    /// player leaving or coming back, a bot's seat joined late, a lag mark. In bundle mode only the relay's bundles and the
    /// pieces of its log count, each in the relay's numbering. Anything else, and anything about a player who is no human of
    /// this world, is passed over: a world the relay finds out of step is rebuilt from its log in a new session (P2 design §5).
    /// </summary>
    public void Receive(NetMessage message)
    {
        switch (message)
        {
            case Bundle bundle when _mode == SessionMode.Bundles:
                ReceiveBundle(bundle);
                break;
            case LogPart part when _mode == SessionMode.Bundles:
                Gather(part);
                break;
            case Turn turn when _mode == SessionMode.Turns && _humans.Contains(turn.Player) && turn.Tick >= World.Tick:
                _turns[(turn.Tick, turn.Player)] = turn;
                break;
            case PlayerLeft or PlayerReturned or PlayerJoined or PlayerLagging when _mode == SessionMode.Turns:
                Note(message);
                break;
        }
    }

    /// <summary>A coming or going, from a recording or a bundle: kept for the tick it happens at, and for the recording.</summary>
    private void Note(NetMessage message)
    {
        switch (message)
        {
            case PlayerLeft left when _humans.Contains(left.Player) && Absences(left.Player).All(a => a.Until <= left.FromTick)
                                      && (!_joinAt.TryGetValue(left.Player, out var joined) || left.FromTick >= joined.From):
                Absences(left.Player).Add((left.FromTick, long.MaxValue));
                Keep(left, left.FromTick);
                break;
            case PlayerReturned back when _humans.Contains(back.Player) && Absences(back.Player) is [.., (var from, long.MaxValue)] away
                                          && back.FromTick >= from:
                away[^1] = (from, back.FromTick);
                Keep(back, back.FromTick);
                break;
            // Only a bot's seat that has not joined: a world that already turned the row into a person's reads it as one.
            case PlayerJoined join when World.Players.Contains(join.Player) && World.Players[join.Player].Kind == PlayerKind.Bot
                                        && !_joinAt.ContainsKey(join.Player):
                _humans.Add(join.Player);
                _joinAt[join.Player] = (join.FromTick, join.Name);
                Keep(join, join.FromTick);
                break;
            case PlayerLagging lagging when _humans.Contains(lagging.Player):
                Marks(lagging.Player).Add((lagging.FromTick, lagging.Lagging));
                Keep(lagging, lagging.FromTick);
                break;
        }
    }

    private void Keep(NetMessage change, long tick)
    {
        _presence.Add(change);
        if (!_notices.TryGetValue(tick, out var at))
            _notices[tick] = at = [];
        at.Add(change);
    }

    private List<(long From, long Until)> Absences(PlayerId player) =>
        _absent.TryGetValue(player, out var list) ? list : _absent[player] = [];

    private List<(long From, bool Lagging)> Marks(PlayerId player) =>
        _marks.TryGetValue(player, out var list) ? list : _marks[player] = [];

    /// <summary>A bundle of the relay: the next in its numbering, or a gap that stops the session taking any more.</summary>
    private void ReceiveBundle(Bundle bundle)
    {
        if (_broken)
            return;
        if (bundle.Tick != _nextBundle || _gathering is not null)
        {
            Break(bundle.Tick);
            return;
        }
        _nextBundle++;
        _plain = true;
        if (bundle.Delay > 0)
            InputDelay = bundle.Delay;
        // After a segment still compressed, its comings and goings must be noted first.
        if (_sealed.Count > 0)
            _waiting.Enqueue(bundle);
        else
            Take(bundle);
    }

    private void Break(long got)
    {
        _broken = true;
        Gap?.Invoke(_nextBundle, got);
    }

    /// <summary>The comings and goings and the turns of a bundle, for the tick it closed.</summary>
    private void Take(Bundle bundle)
    {
        foreach (var change in bundle.Presence ?? [])
            Note(change);
        foreach (var turn in bundle.Turns)
        {
            if (_humans.Contains(turn.Player))
                _turns[(bundle.Tick, turn.Player)] = turn.Tick == bundle.Tick ? turn : turn with { Tick = bundle.Tick };
        }
        Land(bundle);
    }

    /// <summary>
    /// The local player's stamps that landed on the bundle: every one up to the newest the relay merged into the local turn
    /// there (<see cref="Turn.Asked"/>), which tells an earlier late turn that landed here from a later one placed further on.
    /// Where the player is away at the bundle, the relay dropped their commands, and the stamps up to it never land.
    /// </summary>
    private void Land(Bundle bundle)
    {
        if (Local is not { } me || _onTheWay.Count == 0)
            return;
        if (bundle.Turns.FirstOrDefault(t => t.Player == me) is { Asked: >= 0 } own)
        {
            var landed = _onTheWay.TakeWhile(stamp => stamp <= own.Asked).ToList();
            _onTheWay.RemoveRange(0, landed.Count);
            foreach (var stamp in landed)
                Landed?.Invoke(stamp, bundle.Tick);
        }
        else if (Away(me, bundle.Tick))
            _onTheWay.RemoveAll(stamp => stamp <= bundle.Tick);
    }

    /// <summary>
    /// A piece of a sealed segment of the relay's log (a rebuild, P2 design §5.3): once the last is in, the segment's ticks are
    /// in hand, kept compressed until the world reaches them.
    /// </summary>
    private void Gather(LogPart part)
    {
        if (_broken)
            return;
        // The log comes before any plain bundle of a rebuild, its pieces in order, each segment going on where the last ended.
        var first = _gathering is null;
        if (first ? _plain || part.Part != 0 || part.Parts < 1 || part.From != _nextBundle || part.To < part.From
                : part.From != _gathering![0].From || part.To != _gathering[0].To || part.Part != _gathering.Count || part.Parts != _gathering[0].Parts)
        {
            Break(part.From);
            return;
        }
        (_gathering ??= []).Add(part);
        if (_gathering.Count < part.Parts)
            return;
        _sealed.Enqueue((part.From, part.To, BundleSegments.Join(_gathering)));
        _gathering = null;
        _nextBundle = part.To + 1;
    }

    /// <summary>
    /// The segments the world has reached, decoded and taken; once none is left compressed, the bundles that came after them.
    /// So at most one decoded segment's turns are held at a time.
    /// </summary>
    private void Unpack()
    {
        while (_sealed.TryPeek(out var segment) && segment.From <= World.Tick)
        {
            _sealed.Dequeue();
            foreach (var bundle in BundleSegments.Decode(segment.Data).Bundles)
                Take(bundle);
        }
        if (_sealed.Count == 0)
        {
            while (_waiting.TryDequeue(out var bundle))
                Take(bundle);
        }
    }

    /// <summary>Whether a player was away at a tick (left and not yet back, or not yet joined).</summary>
    public bool Away(PlayerId player, long tick) =>
        _joinAt.TryGetValue(player, out var join) && tick < join.From
        || _absent.TryGetValue(player, out var list) && list.Any(a => tick >= a.From && tick < a.Until);

    /// <summary>
    /// Whether a player lags as the world stands (<see cref="PlayerLagging"/>): the newest mark the world has applied is on,
    /// and the player has not left since; a mark from before a reload counts as much as one that came live.
    /// </summary>
    public bool Lagging(PlayerId player)
    {
        if (!_marks.TryGetValue(player, out var marks) || marks.LastOrDefault(m => m.From < World.Tick) is not { Lagging: true } mark)
            return false;
        return !(_absent.TryGetValue(player, out var absences) && absences.Any(a => a.From >= mark.From && a.From < World.Tick));
    }

    /// <summary>Whether every player's turn for the next tick has arrived; in bundle mode, whether its bundle has.</summary>
    public bool CanStep => _mode == SessionMode.Bundles ? World.Tick < _nextBundle : Waiting().Count == 0;

    /// <summary>The players whose turn for the next tick is missing; in bundle mode nobody is waited for.</summary>
    public IReadOnlyList<PlayerId> Waiting() => _mode == SessionMode.Bundles ? [] : WaitingAt(World.Tick);

    private List<PlayerId> WaitingAt(long tick) =>
        tick < InputDelay ? [] : _humans.Where(player => !Away(player, tick) && !_turns.ContainsKey((tick, player))).ToList();

    /// <summary>How many ticks in a row could run right now, counting up to <paramref name="limit"/>; in bundle mode, the bundles in hand.</summary>
    public int Ready(int limit)
    {
        if (_mode == SessionMode.Bundles)
            return (int)Math.Clamp(_nextBundle - World.Tick, 0, Math.Max(0, limit));
        var ready = 0;
        while (ready < limit && WaitingAt(World.Tick + ready).Count == 0)
            ready++;
        return ready;
    }

    /// <summary>
    /// Carries out the next tick if every turn for it is in (in bundle mode, its bundle); returns whether it did. The tick's
    /// leaves, joins and returns go to the world first, all in one change (player order, each player's in the order they
    /// happened), then the lag marks of the tick let go of the wheels, then every present player's turn in player order.
    /// </summary>
    public bool TryStep()
    {
        if (!CanStep)
            return false;
        if (_mode == SessionMode.Bundles)
            Unpack();
        var tick = World.Tick;
        if (PresenceAt(tick) is { Count: > 0 } changes)
            World.ChangePresence(changes);
        foreach (var (player, marks) in _marks)
        {
            if (marks.Any(m => m.From == tick && m.Lagging))
                World.LetGo(player);
        }
        Announce(tick);
        foreach (var turn in TurnsFor(tick))
        {
            var race = World.Players.RaceOf(turn.Player);
            foreach (var command in turn.Commands.Where(c => c.Race == race))
                World.Issue(command with { Issuer = turn.Player });
            // Recorded as turn mode records it: what the relay's pacing said of the turn is no part of the match.
            if (turn.Commands.Count > 0)
                _played.Add(turn is { Seen: -1, Held: 0, Asked: -1 } ? turn : turn with { Seen = -1, Held = 0, Asked = -1 });
        }
        World.Step();
        if (World.Tick % ChecksumInterval == 0 && Local is { } local)
            Outgoing?.Invoke(new ChecksumReport(World.Tick, local, World.Checksum()));
        return true;
    }

    /// <summary>The tick's comings and goings, now applied, to whoever listens, in player order; and every older one let go of.</summary>
    private void Announce(long tick)
    {
        while (_notices.Count > 0 && _notices.First() is var (at, changes) && at <= tick)
        {
            _notices.Remove(at);
            if (at < tick || PresenceApplied is not { } applied)
                continue;
            foreach (var change in changes.OrderBy(PlayerOf))
                applied(change);
        }
    }

    private static PlayerId PlayerOf(NetMessage change) => change switch
    {
        PlayerLeft left => left.Player,
        PlayerReturned back => back.Player,
        PlayerJoined join => join.Player,
        PlayerLagging lagging => lagging.Player,
        _ => default,
    };

    /// <summary>
    /// The players who leave, join or come back at a tick, in player order; a player's own changes in the order they
    /// happened, so that one who leaves and is back within the tick leaves first, and one who is back and leaves again is back
    /// first. A join comes before any later absence of its player.
    /// </summary>
    private List<PresenceChange> PresenceAt(long tick)
    {
        var changes = new List<PresenceChange>();
        foreach (var player in _joinAt.Keys.Union(_absent.Keys).Order())
        {
            if (_joinAt.TryGetValue(player, out var join) && join.From == tick)
                changes.Add(new PresenceChange(player, Away: false, Joins: join.Name));
            foreach (var (from, until) in _absent.GetValueOrDefault(player) ?? [])
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
