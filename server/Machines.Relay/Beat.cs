using System.Globalization;
using Machines.Net;
using Machines.Sim.World;

namespace Machines.Relay;

/// <summary>The clocks and limits of the relay's beat (P2 design §2.1); tests shorten them.</summary>
public sealed record BeatTimings
{
    /// <summary>The relay's own: 20 bundles a second on the clock alone.</summary>
    public static BeatTimings Default { get; } = new();

    /// <summary>
    /// Scripted relay tests: a bundle closes as soon as the turn for it of every playing person, and of every returning one
    /// whose world is built, is in, and the delay stays at 4, so that every turn lands on its own tick, as today, and the
    /// tests run fast. Else a bundle waits for its due time on the anchored schedule of a second a bundle: once the beat has
    /// run ahead of that schedule by closing early, a player who stays silent (without leaving) holds the next bundle back
    /// until the schedule catches up, which may be minutes.
    /// </summary>
    public static BeatTimings Scripted { get; } = new()
    {
        Interval = TimeSpan.FromSeconds(1),
        EarlyBy = TimeSpan.FromDays(1),
        MinDelay = 4,
        MaxDelay = 4,
        RelayLate = TimeSpan.FromDays(1),
        LoadTimeout = TimeSpan.FromSeconds(10),
        DecidedHold = TimeSpan.FromSeconds(5),
    };

    /// <summary>Tests of lateness on the real clock: marks, handovers and returns within seconds.</summary>
    public static BeatTimings Quick { get; } = new()
    {
        DelayUpAfter = 4,
        DelayDownAfter = 20,
        LagMark = 6,
        LagLeave = 20,
        BackAfter = 4,
        LaggedOut = 60,
        MostBacklog = 100,
        LoadTimeout = TimeSpan.FromSeconds(2),
        DecidedHold = TimeSpan.FromSeconds(2),
        SegmentTicks = 100,
        PartBytes = 64 * 1024,
        MostResyncs = 1,
    };

    /// <summary>Time between two bundles' due times.</summary>
    public TimeSpan Interval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// How long before its due time a bundle may close once every playing person's turn for it is in. 0 makes the relay a pure
    /// clock (§2.4): a bundle closed early would let the relay run ahead, and every hitch of one client hold the next back.
    /// </summary>
    public TimeSpan EarlyBy { get; init; } = TimeSpan.Zero;

    /// <summary>The first input delay, the one the match starts with (<see cref="MatchInfo.InputDelay"/>).</summary>
    public int StartDelay { get; init; } = 4;

    /// <summary>The bounds of the adaptive delay.</summary>
    public int MinDelay { get; init; } = 3;

    /// <inheritdoc cref="MinDelay"/>
    public int MaxDelay { get; init; } = 8;

    /// <summary>Turnaround samples kept per player.</summary>
    public int DelayWindow { get; init; } = 100;

    /// <summary>Bundles in a row with the delay's target above it before it rises to the target.</summary>
    public int DelayUpAfter { get; init; } = 20;

    /// <summary>Bundles in a row with the delay's target below it before it drops by one.</summary>
    public int DelayDownAfter { get; init; } = 100;

    /// <summary>
    /// A bundle sent later than this after its due time was the relay's own lateness: the bundles it would have let a client
    /// stamp count nobody late (§2.6).
    /// </summary>
    public TimeSpan RelayLate { get; init; } = TimeSpan.FromMilliseconds(100);

    /// <summary>Late bundles in a row before the lag mark (3 s).</summary>
    public int LagMark { get; init; } = 60;

    /// <summary>Late bundles in a row before the handover (30 s); also the longest a held rebuild stays held.</summary>
    public int LagLeave { get; init; } = 600;

    /// <summary>On-time bundles in a row before a mark goes or a returning player is back (1 s).</summary>
    public int BackAfter { get; init; } = 20;

    /// <summary>Bundles a seat may stay lagged (handed over, never on time again) before its member is taken out (5 min).</summary>
    public int LaggedOut { get; init; } = 6000;

    /// <summary>
    /// Bundles a rebuilt or joining seat may take to be back (5 min): one whose client never loads or never gets on time is
    /// taken out after them, a joiner's seat going back to its bot.
    /// </summary>
    public int StuckOut { get; init; } = 6000;

    /// <summary>Messages sent to a member and not yet acknowledged before it is taken out (60 s of bundles).</summary>
    public int MostBacklog { get; init; } = 1200;

    /// <summary>The longest wait for every person's <see cref="MatchLoaded"/> before the beat starts.</summary>
    public TimeSpan LoadTimeout { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>How long the beat runs on after the decision is agreed.</summary>
    public TimeSpan DecidedHold { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>Bundles per sealed segment of the log.</summary>
    public int SegmentTicks { get; init; } = 600;

    /// <summary>The largest piece of a segment in one <see cref="LogPart"/> (base64 makes 512 KiB 683 KiB, under <see cref="Framing.MaxFrame"/>).</summary>
    public int PartBytes { get; init; } = 512 * 1024;

    /// <summary>Resyncs a player gets; the next time they are out of step they are taken out.</summary>
    public int MostResyncs { get; init; } = 3;
}

/// <summary>Where a seat of a running match stands at the relay (P2 design §2.5).</summary>
public enum SeatState
{
    /// <summary>Present in the world: their turns are placed, their lateness counted.</summary>
    Playing,

    /// <summary>Connected but not playing (<see cref="ReturnKind"/>): back once on time for <see cref="BeatTimings.BackAfter"/> bundles.</summary>
    Returning,

    /// <summary>No connection: away in the world, until a rejoin.</summary>
    Gone,

    /// <summary>Taken out of the match for good.</summary>
    Barred,

    /// <summary>The computer's hero since the start, until a person joins it late.</summary>
    Bot,
}

/// <summary>Why a seat is <see cref="SeatState.Returning"/>.</summary>
public enum ReturnKind
{
    /// <summary>Handed over after <see cref="BeatTimings.LagLeave"/> late bundles; needs no load.</summary>
    Lagged,

    /// <summary>Its world is rebuilt from the log (a resync or a rejoin of a seat that has joined).</summary>
    Rebuilding,

    /// <summary>A late joiner's: the seat is still the bot's in every world until <see cref="PlayerJoined"/>.</summary>
    Joining,
}

/// <summary>What the beat knows of one seat of the match (P2 design §2.5): read by the relay and the tests, changed by the <see cref="Beat"/> alone.</summary>
public sealed class BeatSeat
{
    internal BeatSeat(PlayerId player, bool person)
    {
        Player = player;
        State = person ? SeatState.Playing : SeatState.Bot;
        Joined = person;
    }

    public PlayerId Player { get; }

    public SeatState State { get; internal set; }

    /// <summary>Why the seat is returning; meaningful only while <see cref="State"/> is <see cref="SeatState.Returning"/>.</summary>
    public ReturnKind Returning { get; internal set; }

    /// <summary>Whether the seat is a person's in every world: from the start, or since its <see cref="PlayerJoined"/>.</summary>
    public bool Joined { get; internal set; }

    /// <summary>Whether the seat's client has said <see cref="MatchLoaded"/> since the start or since its rebuild began.</summary>
    public bool Loaded { get; internal set; }

    /// <summary>Whether the seat is lag-marked: every world let go of its wheels, and it gets <c>PlayerLagging(false)</c> when on time again.</summary>
    public bool Marked { get; internal set; }

    /// <summary>A held rebuild: present in every world but let go of, no stand-in AI and no keeper, until back or <see cref="BeatTimings.LagLeave"/>.</summary>
    public bool Held { get; internal set; }

    /// <summary>The tick of the newest turn taken from the seat's client: the one thing lateness looks at.</summary>
    public long Stamped { get; internal set; } = -1;

    /// <summary>Late bundles in a row while playing.</summary>
    public int LateStreak { get; internal set; }

    /// <summary>On-time bundles in a row.</summary>
    public int OnTime { get; internal set; }

    /// <summary>The bundle the seat's returning began at.</summary>
    public long Since { get; internal set; }

    /// <summary>The tick the seat's world decided the match at (<see cref="MatchDecided"/>), while it plays.</summary>
    public long? Decided { get; internal set; }

    /// <summary>The name a late joiner takes the seat under.</summary>
    public string? Name { get; internal set; }

    /// <summary>Late bundles counted for the seat over the match, for the summary.</summary>
    public int Late { get; internal set; }

    /// <summary>
    /// How often the world of the seat's person was found out of step with the others (<see cref="Beat.OutOfStep"/>): each
    /// time up to <see cref="BeatTimings.MostResyncs"/> it is rebuilt from the log, the next time they are taken out of the
    /// match. A late joiner taking a bot's seat starts with none: those of a joiner lost before joining it were that one's.
    /// </summary>
    public int Resyncs { get; internal set; }

    /// <summary>The bundle the newest drive of the seat's player was placed on since it last began playing, −1 for none.</summary>
    internal long DroveAt { get; set; } = -1;

    /// <summary>The bundle of the newest <c>PlayerLagging(true)</c> for the seat since it last began playing, −1 for none.</summary>
    internal long LetGoAt { get; set; } = -1;

    /// <summary>Counted late bundles in a row after the newest drive of the seat's player ran.</summary>
    internal int Stale { get; set; }

    /// <summary>
    /// Whether a drive placed since the seat's last let-go may hold a wheel in every world: a marked player's late drive
    /// takes it again (a drive on the let-go's own tick too, as the turns run after the let-go).
    /// </summary>
    internal bool TookTheWheel => DroveAt >= 0 && DroveAt >= LetGoAt;

    /// <summary>Forgets what the seat drove and was let go of: it begins playing, or stops.</summary>
    internal void ClearWheel() => (DroveAt, LetGoAt, Stale) = (-1, -1, 0);

    /// <summary>The newest turnaround samples (ms), oldest first.</summary>
    internal Queue<double> Window { get; } = new();
}

/// <summary>Something the beat has for a member: to every member of the room, or with <paramref name="To"/> to that player's alone.</summary>
/// <remarks>A <see cref="LeftMatch"/> to a player means the relay takes that member out of the room.</remarks>
public sealed record BeatNotice(NetMessage Message, PlayerId? To = null);

/// <summary>What became of a turn offered to the beat (<see cref="Beat.Offer"/>).</summary>
public enum Offered
{
    /// <summary>Its commands go on the tick it asked for.</summary>
    Placed,

    /// <summary>Its commands go on a later tick: the one it asked for had closed.</summary>
    Moved,

    /// <summary>Only its stamp counts: it had no commands, the player does not play now, or the match is decided.</summary>
    Stamped,

    /// <summary>Nothing of it counts: no seat of the match that may send now (a rebuild's world before it is built).</summary>
    Ignored,

    /// <summary>Dropped: its tick is not past the player's last.</summary>
    WentBack,

    /// <summary>Dropped: its tick is more than <see cref="BeatTimings.MaxDelay"/> past the next bundle.</summary>
    TooFar,

    /// <summary>
    /// Its commands dropped, its stamp counted: with them its bundle would be larger than a frame a client reads
    /// (<see cref="Beat.MostTurnBytes"/>).
    /// </summary>
    TooLarge,
}

/// <summary>
/// The beat of one running match (P2 design §2): the relay places every turn on a tick and closes one <see cref="Bundle"/> a
/// tick on an anchored schedule, waiting for nobody; it counts who is late and marks, hands over and takes back players by
/// that, adapts the input delay to the players' lines, and agrees the end. Pure: the time comes in as a parameter, nothing
/// here touches a socket or a thread, and the relay calls it under its lock.
/// </summary>
/// <remarks>
/// The relay owns the thread that calls <see cref="Close"/> when <see cref="NextWake"/> says, the members, the log and the
/// sending: it sends every bundle <see cref="Close"/> returns to every member of the room, and what <see cref="Drain"/> gives.
/// </remarks>
public sealed class Beat
{
    /// <summary>How many bundles' sending times are kept, for the turnaround and the lateness gate.</summary>
    private const int SentRing = 400;

    /// <summary>The fewest turnaround samples a player's need is reckoned from.</summary>
    public const int MinSamples = 20;

    /// <summary>
    /// The most bytes of turns one bundle carries, counted as each turn placed on it encodes alone: a frame
    /// (<see cref="Framing.MaxFrame"/>) less room for the bundle's own fields and its comings and goings. Each turn a player
    /// sends fits in a frame, but the turns of several players, or the late turns of one merged on the next open tick, need
    /// not; a bundle larger than a frame no client could read, nor after a reconnect, which sends it again.
    /// </summary>
    public const int MostTurnBytes = Framing.MaxFrame - 64 * 1024;

    private readonly SortedDictionary<PlayerId, BeatSeat> _seats = [];
    // The commands placed on each tick still to close, by player, with the newest stamp merged into each.
    private readonly SortedDictionary<long, SortedDictionary<PlayerId, Bucket>> _buckets = [];
    // Players who stopped playing since the last close: a leave and why, or null for held.
    private readonly SortedDictionary<PlayerId, LeaveReason?> _queued = [];
    private readonly (long Tick, TimeSpan At)[] _sent = new (long, TimeSpan)[SentRing];
    private readonly List<BeatNotice> _notices = [];
    private readonly Dictionary<(PlayerId, Offered), TimeSpan> _dropNoted = [];
    private readonly SortedDictionary<int, long> _delays = [];
    // How often each seat's worlds were out of step over the match, whoever played it, for the summary.
    private readonly SortedDictionary<PlayerId, int> _outOfStep = [];
    private int _above, _below;
    private TimeSpan? _agreedAt;
    private long _uncounted, _lateCloses;

    /// <param name="match">The match as it started: its people play, its bots are bots' seats, the computer's races have no seat.</param>
    /// <param name="started">When the match started, on the clock every later call's time comes from.</param>
    public Beat(BeatTimings timings, MatchInfo match, TimeSpan started)
    {
        Timings = timings;
        Battle = match.Options.IsBattle;
        Started = started;
        Delay = Math.Clamp(timings.StartDelay, timings.MinDelay, timings.MaxDelay);
        foreach (var row in match.Table().ById.Where(r => r.Kind != PlayerKind.Ai))
            _seats[row.Id] = new BeatSeat(row.Id, row.Kind == PlayerKind.Human);
        Array.Fill(_sent, (-1L, TimeSpan.Zero));
    }

    public BeatTimings Timings { get; }

    /// <summary>Whether the match is a battle: a rebuild there hands the hero to a bot at once; elsewhere it is held.</summary>
    public bool Battle { get; }

    public TimeSpan Started { get; }

    /// <summary>When bundle 0 was due: once every person loaded or was gone, or <see cref="BeatTimings.LoadTimeout"/> after the start. Null before.</summary>
    public TimeSpan? Anchor { get; private set; }

    /// <summary>The tick of the next bundle to close.</summary>
    public long NextTick { get; private set; }

    /// <summary>The input delay in force: the newest bundle carries it.</summary>
    public int Delay { get; private set; }

    /// <summary>How many turns' commands went on a later tick than they asked for.</summary>
    public long Moved { get; private set; }

    /// <summary>The tick the match was agreed decided at, once more than half of the playing persons reported it.</summary>
    public long? DecidedAt { get; private set; }

    /// <summary>Whether the beat is over: every playing person reported the end, or <see cref="BeatTimings.DecidedHold"/> passed after it.</summary>
    public bool Stopped { get; private set; }

    /// <summary>Every seat of a person or a bot, in player order.</summary>
    public IReadOnlyCollection<BeatSeat> Seats => _seats.Values;

    /// <summary>What the beat says, for the relay's log.</summary>
    public event Action<string>? Log;

    /// <summary>A seat of the match, or null.</summary>
    public BeatSeat? Seat(PlayerId player) => _seats.GetValueOrDefault(player);

    /// <summary>When bundle <paramref name="tick"/> is due.</summary>
    /// <exception cref="InvalidOperationException">The beat has not started.</exception>
    public TimeSpan Due(long tick) =>
        (Anchor ?? throw new InvalidOperationException("the beat has not started")) + TimeSpan.FromTicks(Timings.Interval.Ticks * tick);

    /// <summary>When bundle <paramref name="tick"/> went out, while the beat still knows (the last 400).</summary>
    public TimeSpan? SentAt(long tick) => tick >= 0 && _sent[tick % SentRing] is var (sent, at) && sent == tick ? at : null;

    /// <summary>
    /// A player's client says its world is built (<see cref="MatchLoaded"/>): at the start, the beat starts once every person
    /// has or is gone; in a rebuild, its turns and reports count from now on, its stamps afresh.
    /// </summary>
    public void Loaded(PlayerId player, TimeSpan now)
    {
        if (!_seats.TryGetValue(player, out var seat) || seat.State is not (SeatState.Playing or SeatState.Returning))
            return;
        seat.Loaded = true;
        if (seat.State == SeatState.Returning && seat.Returning != ReturnKind.Lagged)
            (seat.Stamped, seat.OnTime) = (-1, 0);
        TryAnchor(now);
    }

    /// <summary>
    /// A turn of the player's own (P2 design §2.3): it stamps how far the player's world is, gives a turnaround sample, and
    /// its commands go on the bundle of the tick it asked for, or of the next to close where that one has.
    /// </summary>
    public Offered Offer(Turn turn, TimeSpan now)
    {
        if (!_seats.TryGetValue(turn.Player, out var seat))
            return Offered.Ignored;
        // A rebuild's turns before its world is built are the old, diverged world's, still on their way.
        var counts = seat.State == SeatState.Playing
                     || seat.State == SeatState.Returning && (seat.Returning == ReturnKind.Lagged || seat.Loaded);
        if (!counts)
            return Offered.Ignored;
        if (turn.Tick <= seat.Stamped)
            return Dropped(seat, Offered.WentBack, turn, now);
        if (turn.Tick > NextTick + Timings.MaxDelay)
            return Dropped(seat, Offered.TooFar, turn, now);
        seat.Stamped = turn.Tick;
        Sample(seat, turn, now);
        if (seat.State != SeatState.Playing || turn.Commands.Count == 0)
            return Offered.Stamped;
        var tick = Math.Max(turn.Tick, NextTick);
        if (DecidedAt is { } decided && tick >= decided)
            return Offered.Stamped;
        if (!_buckets.TryGetValue(tick, out var bucket))
            _buckets[tick] = bucket = [];
        // Counted as the turn encodes alone, which is more than its commands add to the bundle's turn of the player.
        var bytes = NetJson.Encode(turn).Length;
        if (bucket.Values.Sum(b => b.Bytes) + bytes > MostTurnBytes)
            return Dropped(seat, Offered.TooLarge, turn, now, tick);
        if (!bucket.TryGetValue(seat.Player, out var placed))
            bucket[seat.Player] = placed = new Bucket();
        placed.Commands.AddRange(turn.Commands);
        placed.Asked = turn.Tick;
        placed.Bytes += bytes;
        if (turn.Commands.Any(c => c is DriveCommand))
            (seat.DroveAt, seat.Stale) = (Math.Max(seat.DroveAt, tick), 0);
        if (tick == turn.Tick)
            return Offered.Placed;
        Moved++;
        return Offered.Moved;
    }

    private Offered Dropped(BeatSeat seat, Offered why, Turn turn, TimeSpan now, long bundle = -1)
    {
        if (!_dropNoted.TryGetValue((seat.Player, why), out var noted) || now - noted >= TimeSpan.FromMinutes(1))
        {
            _dropNoted[(seat.Player, why)] = now;
            Note(why switch
            {
                Offered.WentBack => $"player {seat.Player}'s turn for tick {turn.Tick} dropped: not past its last, {seat.Stamped}",
                Offered.TooLarge => $"player {seat.Player}'s turn for tick {turn.Tick} dropped: its commands would make bundle {bundle} larger than {MostTurnBytes} bytes",
                _ => $"player {seat.Player}'s turn for tick {turn.Tick} dropped: more than {Timings.MaxDelay} past the next bundle, {NextTick}",
            });
        }
        return why;
    }

    /// <summary>
    /// A turnaround sample (P2 design §2.7): from the bundle the turn's world had run to the turn, only from a playing,
    /// unmarked player, from a turn made without a backlog. Late or not: a line a little slower than the delay makes every
    /// turn late, and only its samples can raise the delay before the mark (a backlog, which also makes turns late, gives
    /// none by <see cref="Turn.Held"/>). None while the relay's own lateness lies within the turnaround (§2.2): a turn that
    /// waited at a stalled relay measures the stall, not the line, and a few of them would raise everyone's delay to the most.
    /// </summary>
    private void Sample(BeatSeat seat, Turn turn, TimeSpan now)
    {
        if (seat.State != SeatState.Playing || seat.Marked || turn.Held > BundlePacer.Buffer + 1 || turn.Seen < 0
            || SentAt(turn.Seen) is not { } sent || RelayLateSince(turn.Seen, now))
            return;
        seat.Window.Enqueue((now - sent).TotalMilliseconds);
        while (seat.Window.Count > Timings.DelayWindow)
            seat.Window.Dequeue();
    }

    /// <summary>
    /// Whether the relay was late by more than <see cref="BeatTimings.RelayLate"/> since bundle <paramref name="seen"/> went out:
    /// a bundle after it went out that late, or the next one is that overdue now (the relay takes the turn before it closes
    /// the bundles a stall held up).
    /// </summary>
    private bool RelayLateSince(long seen, TimeSpan now)
    {
        if (now - Due(NextTick) > Timings.RelayLate)
            return true;
        for (var tick = Math.Max(seen + 1, NextTick - SentRing); tick < NextTick; tick++)
        {
            if (_sent[tick % SentRing] is var (sent, at) && sent == tick && at - Due(tick) > Timings.RelayLate)
                return true;
        }
        return false;
    }

    /// <summary>
    /// When the beat wants to be called again: the start (once everyone loaded, else at the load timeout), the next bundle's
    /// due time (earlier by <see cref="BeatTimings.EarlyBy"/> once every playing person's turn for it is in), or the end of the
    /// hold after the decision. Null once the beat stopped. It may lie in the past: then <see cref="Close"/> is due now.
    /// </summary>
    public TimeSpan? NextWake(TimeSpan now)
    {
        if (Stopped)
            return null;
        if (Anchor is null)
            return AllLoaded() ? now : Started + Timings.LoadTimeout;
        var due = Due(NextTick);
        var wake = Timings.EarlyBy > TimeSpan.Zero && AllIn(NextTick) ? due - Timings.EarlyBy : due;
        if (_agreedAt is { } agreed && agreed + Timings.DecidedHold < wake)
            wake = agreed + Timings.DecidedHold;
        return wake;
    }

    /// <summary>
    /// Closes every bundle that may close now, in order (P2 design §2.4): each one due, or with every playing person's turn in
    /// and within <see cref="BeatTimings.EarlyBy"/> of its due time. A relay that fell behind closes every overdue one at once,
    /// never skipping a tick. Returns them, for the relay to log and send to every member.
    /// </summary>
    public IReadOnlyList<Bundle> Close(TimeSpan now)
    {
        var closed = new List<Bundle>();
        if (Stopped)
            return closed;
        TryAnchor(now);
        if (Anchor is null)
            return closed;
        Agree(now);
        var behind = Stopped ? TimeSpan.Zero : now - Due(NextTick);
        while (!Stopped)
        {
            var may = MayClose(NextTick, now);
            // The hold after the decision is over: what was due within it still goes out, then the beat stops.
            if (_agreedAt is { } agreed && now >= agreed + Timings.DecidedHold && (!may || Due(NextTick) > agreed + Timings.DecidedHold))
            {
                Stop("the hold after the decision is over");
                break;
            }
            if (!may)
                break;
            closed.Add(CloseOne(now));
        }
        if (behind > Timings.RelayLate && closed.Count > 0)
            Note($"the beat fell behind by {behind.TotalMilliseconds:F0} ms: {closed.Count} bundles closed at once, to tick {NextTick - 1}");
        return closed;
    }

    private bool MayClose(long tick, TimeSpan now)
    {
        var due = Due(tick);
        return now >= due || Timings.EarlyBy > TimeSpan.Zero && now >= due - Timings.EarlyBy && AllIn(tick);
    }

    /// <summary>
    /// The players a bundle waits for to close early (<see cref="BeatTimings.EarlyBy"/>, tests only): the playing persons and
    /// the returning ones whose world is built. A returning player is back only once on time; were the bundles closed the
    /// moment the last playing person's turn is in, a returning player's turn for it would always come after, and nobody
    /// rebuilt in a scripted test would ever be back.
    /// </summary>
    private List<BeatSeat> Expected() =>
        _seats.Values.Where(s => s.State == SeatState.Playing || s.State == SeatState.Returning && (s.Loaded || s.Returning == ReturnKind.Lagged)).ToList();

    /// <summary>Whether every expected player's turn for the tick, or a later one, is in; never with nobody expected.</summary>
    private bool AllIn(long tick) => Expected() is { Count: > 0 } expected && expected.All(s => s.Stamped >= tick);

    private Bundle CloseOne(TimeSpan now)
    {
        var tick = NextTick;
        var presence = new List<NetMessage>();
        var returning = _seats.Values.Where(s => s.State == SeatState.Returning).ToList();

        // Who stopped playing since the last close.
        foreach (var (player, leaves) in _queued)
        {
            presence.Add(leaves is { } why ? new PlayerLeft(player, tick, why) : new PlayerLagging(player, tick, true));
            if (leaves is null)
                _seats[player].LetGoAt = tick;
        }
        _queued.Clear();

        // A tick counts only when the relay sent on time the bundle a client must have run to stamp it: a stall of the relay
        // is nobody's lateness.
        var counts = Counts(tick);
        if (!counts)
            _uncounted++;
        if (counts && DecidedAt is null)
        {
            foreach (var seat in _seats.Values.Where(s => s.State == SeatState.Playing && s.Decided is null).ToList())
                CountPlaying(seat, tick, presence);
        }
        // Once the end is agreed nobody's lateness counts (§2.8): nobody returns, a held rebuild is not handed over and no
        // lagged seat is taken out, so the comings and goings after the end are only those of people who quit.
        if (DecidedAt is null)
        {
            foreach (var seat in returning)
                CountReturning(seat, tick, counts, presence);
        }

        AdaptDelay();

        var turns = _buckets.Remove(tick, out var bucket)
            ? bucket.Select(placed => new Turn(tick, placed.Key, placed.Value.Commands, Asked: placed.Value.Asked)).ToList()
            : [];
        var ordered = presence.OrderBy(PlayerOf).ThenBy(Rank).ToList();
        var bundle = new Bundle(tick, turns, ordered.Count > 0 ? ordered : null, Delay);

        _sent[tick % SentRing] = (tick, now);
        NextTick = tick + 1;
        _delays[Delay] = _delays.GetValueOrDefault(Delay) + 1;
        if (now - Due(tick) > TimeSpan.FromMilliseconds(20))
            _lateCloses++;
        return bundle;
    }

    /// <summary>
    /// A playing person's lateness at a counted tick (§2.4 step 2): the mark at <see cref="BeatTimings.LagMark"/>, the handover
    /// at <see cref="BeatTimings.LagLeave"/>, the mark gone after <see cref="BeatTimings.BackAfter"/>. A marked player whose late
    /// drive took a wheel again since the mark and who then sent no drive for <see cref="BeatTimings.LagMark"/> late bundles is
    /// let go of again (<c>PlayerLagging(true)</c> once more), so that the wheel does not drive on with that input until the
    /// handover; one who drives on with late turns keeps it.
    /// </summary>
    private void CountPlaying(BeatSeat seat, long tick, List<NetMessage> presence)
    {
        var late = seat.Stamped < tick;
        if (late)
        {
            seat.LateStreak++;
            seat.OnTime = 0;
            seat.Late++;
            if (tick > seat.DroveAt)
                seat.Stale++;
        }
        else
        {
            seat.LateStreak = 0;
            seat.OnTime++;
            seat.Stale = 0;
        }
        if (late && seat.LateStreak == Timings.LagLeave)
        {
            presence.Add(new PlayerLeft(seat.Player, tick, LeaveReason.Lagged));
            StopPlaying(seat, ReturnKind.Lagged, tick);
            seat.Loaded = true;
            Note($"player {seat.Player} late {Timings.LagLeave} bundles in a row: handed over at tick {tick}");
        }
        else if (late && seat.LateStreak == Timings.LagMark && !seat.Marked)
        {
            presence.Add(new PlayerLagging(seat.Player, tick, true));
            (seat.Marked, seat.LetGoAt) = (true, tick);
            Note($"player {seat.Player} late {Timings.LagMark} bundles in a row: marked at tick {tick}");
        }
        else if (late && seat.Marked && seat.TookTheWheel && seat.Stale >= Timings.LagMark)
        {
            presence.Add(new PlayerLagging(seat.Player, tick, true));
            seat.LetGoAt = tick;
            Note($"player {seat.Player}, marked, drove again and then nothing for {Timings.LagMark} late bundles: let go of again at tick {tick}");
        }
        else if (!late && seat.Marked && seat.OnTime == Timings.BackAfter)
        {
            presence.Add(new PlayerLagging(seat.Player, tick, false));
            seat.Marked = false;
            Note($"player {seat.Player} on time again: mark gone at tick {tick}");
        }
    }

    /// <summary>
    /// A returning person at a tick (§2.4 step 3): a held rebuild handed over after <see cref="BeatTimings.LagLeave"/>, a lagged
    /// seat taken out after <see cref="BeatTimings.LaggedOut"/>, a rebuilt or joining one never back after
    /// <see cref="BeatTimings.StuckOut"/> taken out too (a joiner's seat the bot's again), and once its world is built and on
    /// time for <see cref="BeatTimings.BackAfter"/> counted bundles in a row, back: joined, no longer held, or returned.
    /// </summary>
    private void CountReturning(BeatSeat seat, long tick, bool counts, List<NetMessage> presence)
    {
        if (seat.State != SeatState.Returning)
            return;
        if (seat.Held && tick - seat.Since >= Timings.LagLeave)
        {
            presence.Add(new PlayerLeft(seat.Player, tick, LeaveReason.Lagged));
            (seat.Held, seat.Marked) = (false, false);
            Note($"player {seat.Player}'s rebuild held since tick {seat.Since}: handed over at tick {tick}");
            return;
        }
        if (seat.Returning == ReturnKind.Lagged && tick - seat.Since >= Timings.LaggedOut)
        {
            Forget(seat, SeatState.Gone);
            _notices.Add(new BeatNotice(new LeftMatch(LeftMatch.CannotKeepUp), seat.Player));
            Note($"player {seat.Player} lagged since tick {seat.Since}: taken out of the match at tick {tick}");
            return;
        }
        // A client whose rebuild or late join never loads or never gets on time (stuck behind its loading screen, say) would hold
        // its seat for the rest of the match: the seat is gone, or for one who never joined the bot's as it was.
        if (seat.Returning != ReturnKind.Lagged && tick - seat.Since >= Timings.StuckOut)
        {
            var joined = seat.Joined;
            if (seat.Held)
                presence.Add(new PlayerLeft(seat.Player, tick, LeaveReason.Lagged));
            Forget(seat, joined ? SeatState.Gone : SeatState.Bot);
            _notices.Add(new BeatNotice(new LeftMatch(LeftMatch.CannotKeepUp), seat.Player));
            Note($"player {seat.Player} {(joined ? "rebuilding" : "joining")} since tick {seat.Since} and not back: taken out of the match at tick {tick}" +
                 (joined ? "" : ", the seat a bot's again"));
            return;
        }
        if (!counts || !(seat.Loaded || seat.Returning == ReturnKind.Lagged))
            return;
        seat.OnTime = seat.Stamped >= tick ? seat.OnTime + 1 : 0;
        if (seat.OnTime < Timings.BackAfter)
            return;
        NetMessage back = !seat.Joined ? new PlayerJoined(seat.Player, tick, seat.Name ?? "")
            : seat.Held ? new PlayerLagging(seat.Player, tick, false)
            : new PlayerReturned(seat.Player, tick);
        presence.Add(back);
        Note($"player {seat.Player} back at tick {tick} ({(back is PlayerJoined ? "joined" : seat.Held ? "no longer held" : "returned")})");
        seat.Joined = true;
        seat.State = SeatState.Playing;
        (seat.LateStreak, seat.OnTime, seat.Marked, seat.Held) = (0, 0, false, false);
        seat.ClearWheel();
        seat.Window.Clear();
    }

    /// <summary>A playing seat stops playing: its commands from <paramref name="tick"/> on are taken out.</summary>
    private void StopPlaying(BeatSeat seat, ReturnKind kind, long tick)
    {
        seat.State = SeatState.Returning;
        seat.Returning = kind;
        seat.Since = tick;
        (seat.LateStreak, seat.OnTime, seat.Marked, seat.Held) = (0, 0, false, false);
        seat.ClearWheel();
        RemoveBuckets(seat.Player, tick);
    }

    /// <summary>Whether a closing tick counts for lateness: the bundle a client must have run to stamp it went out on time.</summary>
    private bool Counts(long tick)
    {
        var needed = tick - Delay - 1;
        if (needed < 0)
            return true;
        var (sent, at) = _sent[needed % SentRing];
        return sent != needed || at - Due(needed) <= Timings.RelayLate;
    }

    /// <summary>
    /// The adaptive delay (P2 design §2.7): the target is the worst need of the playing, unmarked players with enough samples,
    /// each the ticks their 95th percentile of turnaround takes; up to it after <see cref="BeatTimings.DelayUpAfter"/> bundles
    /// above, down by one after <see cref="BeatTimings.DelayDownAfter"/> below. With nobody to go by it stays.
    /// </summary>
    private void AdaptDelay()
    {
        var needs = _seats.Values.Where(s => s.State == SeatState.Playing && !s.Marked && s.Window.Count >= MinSamples)
            .Select(s => (Seat: s, P95: P95(s.Window))).ToList();
        if (needs.Count == 0)
        {
            (_above, _below) = (0, 0);
            return;
        }
        var worst = needs.MaxBy(n => n.P95);
        var target = Math.Clamp((int)Math.Ceiling(worst.P95 / Timings.Interval.TotalMilliseconds), Timings.MinDelay, Timings.MaxDelay);
        if (target > Delay)
        {
            _below = 0;
            if (++_above < Timings.DelayUpAfter)
                return;
            Note($"delay {Delay} → {target} at tick {NextTick}: player {worst.Seat.Player}'s turnaround P95 {worst.P95:F0} ms");
            (Delay, _above) = (target, 0);
        }
        else if (target < Delay)
        {
            _above = 0;
            if (++_below < Timings.DelayDownAfter)
                return;
            Note($"delay {Delay} → {Delay - 1} at tick {NextTick}: the worst turnaround P95 is player {worst.Seat.Player}'s {worst.P95:F0} ms");
            (Delay, _below) = (Delay - 1, 0);
        }
        else
            (_above, _below) = (0, 0);
    }

    /// <summary>The 95th percentile of a window, by nearest rank.</summary>
    private static double P95(IEnumerable<double> window)
    {
        var sorted = window.Order().ToList();
        return sorted[Math.Max(0, (int)Math.Ceiling(0.95 * sorted.Count) - 1)];
    }

    /// <summary>
    /// A player's connection ended (a goodbye, a line that did not come back, a backlog it cannot carry): a playing seat
    /// leaves at the next bundle; a held rebuild is handed over then; a late joiner who had not joined leaves the seat a bot's,
    /// as nothing happened in any world.
    /// </summary>
    public void Leave(PlayerId player)
    {
        if (!_seats.TryGetValue(player, out var seat))
            return;
        switch (seat.State)
        {
            case SeatState.Playing:
                Queue(player, LeaveReason.Left);
                RemoveBuckets(player, NextTick);
                break;
            case SeatState.Returning when !seat.Joined:
                Forget(seat, SeatState.Bot);
                Note($"player {player} lost before joining: the seat is a bot's again");
                return;
            case SeatState.Returning:
                if (seat.Held)
                    Queue(player, LeaveReason.Left);
                break;
            default:
                return;
        }
        Forget(seat, SeatState.Gone);
        Note($"player {player} gone from tick {NextTick}");
    }

    /// <summary>
    /// A player's world is rebuilt from the log (P2 design §5.3): a resync, a rejoin, or with <paramref name="name"/> a late
    /// join into a bot's seat. A playing seat stops playing at the next bundle: in a battle a bot drives the hero meanwhile
    /// (<see cref="PlayerLeft"/>), elsewhere it is held (<see cref="PlayerLagging"/>, unless marked already). The seat returns
    /// as <see cref="ReturnKind.Joining"/> while it has not joined, else <see cref="ReturnKind.Rebuilding"/>, whatever the reason;
    /// its turns count again once its client says <see cref="MatchLoaded"/>.
    /// </summary>
    /// <exception cref="KeyNotFoundException">No seat of the match.</exception>
    /// <exception cref="InvalidOperationException">The seat was taken out of the match.</exception>
    public ReturnKind Rebuild(PlayerId player, string? name = null)
    {
        var seat = _seats[player];
        switch (seat.State)
        {
            case SeatState.Barred:
                throw new InvalidOperationException($"player {player} was taken out of the match");
            // A late joiner takes the seat: the resyncs of a joiner lost before joining it are not theirs (§5.5). Without a name
            // it is that joiner's own seat key bringing them back, resyncs and all.
            case SeatState.Bot:
                if (name is not null)
                    (seat.Name, seat.Resyncs) = (name, 0);
                seat.Since = NextTick;
                break;
            case SeatState.Playing when Battle:
                Queue(player, LeaveReason.Rebuilding);
                StopPlaying(seat, ReturnKind.Rebuilding, NextTick);
                break;
            // A marked seat's wheels were let go of at the mark, unless a late drive took one again since.
            case SeatState.Playing:
                if (!seat.Marked || seat.TookTheWheel)
                    Queue(player, null);
                StopPlaying(seat, ReturnKind.Rebuilding, NextTick);
                (seat.Held, seat.Marked) = (true, true);
                break;
            // A held rebuild stays held, its time running on; a lagged seat is away already, and so is one rebuilding or
            // joining, whose time to be back starts again.
            case SeatState.Returning when seat.Returning == ReturnKind.Lagged || !seat.Held:
            case SeatState.Gone:
                seat.Since = NextTick;
                break;
        }
        seat.State = SeatState.Returning;
        seat.Returning = seat.Joined ? ReturnKind.Rebuilding : ReturnKind.Joining;
        (seat.Loaded, seat.Stamped, seat.LateStreak, seat.OnTime, seat.Decided) = (false, -1, 0, 0, null);
        Note($"player {player} {(seat.Returning == ReturnKind.Joining ? "joining" : seat.Held ? "rebuilding, held" : "rebuilding")} from tick {NextTick}");
        return seat.Returning;
    }

    /// <summary>
    /// A player's world was found out of step with the others (P2 design §5.2): counted, and the count returned. Up to
    /// <see cref="BeatTimings.MostResyncs"/> the relay rebuilds that world from the log (<see cref="Rebuild"/>, a resync); the
    /// next time it takes the player out of the match (<see cref="Bar"/>, §5.5).
    /// </summary>
    /// <exception cref="KeyNotFoundException">No seat of the match.</exception>
    public int OutOfStep(PlayerId player)
    {
        var seat = _seats[player];
        _outOfStep[player] = _outOfStep.GetValueOrDefault(player) + 1;
        return ++seat.Resyncs;
    }

    /// <summary>
    /// A player is taken out of the match for good (out of step too often, P2 design §5.5): a playing seat or a held rebuild
    /// leaves at the next bundle, after which the computer plays for them.
    /// </summary>
    public void Bar(PlayerId player)
    {
        if (!_seats.TryGetValue(player, out var seat) || seat.State is SeatState.Barred or SeatState.Bot)
            return;
        if (seat.State == SeatState.Playing || seat.State == SeatState.Returning && seat.Held)
        {
            Queue(player, LeaveReason.Left);
            RemoveBuckets(player, NextTick);
        }
        Forget(seat, SeatState.Barred);
        Note($"player {player} taken out of the match from tick {NextTick}");
    }

    /// <summary>
    /// A player's world decided the match at <paramref name="tick"/> (<see cref="MatchDecided"/>, P2 design §2.8). Only a
    /// playing person's report counts; once more than half of the playing persons report the same tick, the decision holds:
    /// <see cref="MatchEnded"/> goes to everyone, commands from that tick on are dropped, and nobody's lateness counts any more.
    /// </summary>
    public void Decided(PlayerId player, long tick, TimeSpan now)
    {
        if (!_seats.TryGetValue(player, out var seat))
            return;
        if (seat.State != SeatState.Playing)
        {
            Note($"player {player} (not playing) says the match was decided at tick {tick}: only noted");
            return;
        }
        Note($"player {player} says the match was decided at tick {tick}");
        if (DecidedAt is { } decided && decided != tick)
            Note($"player {player}'s world decided the match at tick {tick}, the others' at {decided}: out of step");
        seat.Decided = tick;
        Agree(now);
    }

    private void Agree(TimeSpan now)
    {
        var playing = _seats.Values.Where(s => s.State == SeatState.Playing).ToList();
        if (DecidedAt is null)
        {
            if (playing.Where(s => s.Decided is not null).GroupBy(s => s.Decided!.Value).OrderBy(g => g.Key)
                    .FirstOrDefault(g => g.Count() * 2 > playing.Count) is not { } agreed)
                return;
            DecidedAt = agreed.Key;
            _agreedAt = now;
            foreach (var later in _buckets.Keys.Where(t => t >= agreed.Key).ToList())
                _buckets.Remove(later);
            _notices.Add(new BeatNotice(new MatchEnded(agreed.Key)));
            Note($"the match is decided at tick {agreed.Key}: {agreed.Count()} of {playing.Count} playing agree");
            // A world that decided at another tick went its own way; once the end is agreed nobody is resynced (§5.2), so the
            // log says so, and that world's battle does not count (§2.8).
            foreach (var other in playing.Where(s => s.Decided is { } own && own != agreed.Key))
                Note($"player {other.Player}'s world decided the match at tick {other.Decided}, the others' at {agreed.Key}: out of step");
        }
        if (playing.All(s => s.Decided is not null))
            Stop("every playing person reported the end");
    }

    private void Stop(string why)
    {
        if (Stopped)
            return;
        Stopped = true;
        Note($"the beat stops at tick {NextTick}: {why}");
    }

    /// <summary>What the beat has for the members since it was last asked, in order (the end, players taken out).</summary>
    public IReadOnlyList<BeatNotice> Drain()
    {
        var drained = _notices.ToList();
        _notices.Clear();
        return drained;
    }

    /// <summary>The match in one line, for the relay's log at its end (P2 design §5.6).</summary>
    public string Summary()
    {
        var late = string.Join(", ", _seats.Values.Where(s => s.Joined).Select(s => $"{s.Player} {s.Late}"));
        var delays = string.Join(", ", _delays.Select(d => $"{d.Key}: {d.Value}"));
        // Out of step by player (each time up to MostResyncs a resync, the next a bar; a seat's late joiners together), only in
        // a match where a world was.
        var resyncs = _outOfStep.Count > 0 ? $"; out of step by player: {string.Join(", ", _outOfStep.Select(o => $"{o.Key} {o.Value}"))}" : "";
        return string.Create(CultureInfo.InvariantCulture,
            $"{NextTick} ticks, {Moved} turns moved, late bundles by player: {late}; delay (bundles): {delays}; {_lateCloses} closed over 20 ms late, {_uncounted} counted nobody{resyncs}");
    }

    /// <summary>The player stops playing at the next close: leaves (and why), or with null is held; a leave outweighs a hold.</summary>
    private void Queue(PlayerId player, LeaveReason? leaves) => _queued[player] = leaves ?? _queued.GetValueOrDefault(player);

    private void RemoveBuckets(PlayerId player, long from)
    {
        foreach (var (tick, bucket) in _buckets)
        {
            if (tick >= from)
                bucket.Remove(player);
        }
    }

    /// <summary>A seat that no longer plays and is not coming back on its own: everything counted for it goes.</summary>
    private static void Forget(BeatSeat seat, SeatState state)
    {
        seat.State = state;
        (seat.Loaded, seat.Marked, seat.Held, seat.LateStreak, seat.OnTime, seat.Decided) = (false, false, false, 0, 0, null);
        seat.ClearWheel();
        seat.Window.Clear();
    }

    private void TryAnchor(TimeSpan now)
    {
        if (Anchor is not null)
            return;
        var all = AllLoaded();
        if (!all && now - Started < Timings.LoadTimeout)
            return;
        Anchor = now;
        var persons = _seats.Values.Where(s => s.State != SeatState.Bot).ToList();
        Note($"the beat starts: {persons.Count(s => s.Loaded)} of {persons.Count} loaded{(all ? "" : $" after {Timings.LoadTimeout.TotalSeconds:F0} s")}");
    }

    private bool AllLoaded() => _seats.Values.Where(s => s.State != SeatState.Bot).All(s => s.Loaded || s.State is SeatState.Gone or SeatState.Barred);

    private void Note(string line) => Log?.Invoke(line);

    private static PlayerId PlayerOf(NetMessage change) => change switch
    {
        PlayerLeft left => left.Player,
        PlayerJoined join => join.Player,
        PlayerReturned back => back.Player,
        PlayerLagging lagging => lagging.Player,
        _ => default,
    };

    /// <summary>A player's entries in a bundle go left, joined, returned, lagging (P2 design §2.4 step 5).</summary>
    private static int Rank(NetMessage change) => change switch
    {
        PlayerLeft => 0,
        PlayerJoined => 1,
        PlayerReturned => 2,
        _ => 3,
    };

    /// <summary>A player's commands placed on one tick, in arrival order, and the newest stamp merged in.</summary>
    private sealed class Bucket
    {
        public List<Command> Commands { get; } = [];
        public long Asked { get; set; } = -1;

        /// <summary>The placed turns' bytes, each as it encodes alone (<see cref="MostTurnBytes"/>).</summary>
        public int Bytes { get; set; }
    }
}
