using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Relay;

/// <summary>
/// The multiplayer relay: players meet in rooms, the host starts the match, and from then on the relay keeps the match's
/// beat (P2 design §2): it places every player's turns on a tick and sends everyone one <see cref="Bundle"/> a tick on its
/// own clock, waiting for nobody, takes late players out of the way and back, keeps the log a returning player's world is
/// rebuilt from, and compares the players' checksums. It never runs the simulation; every client does (deterministic
/// lockstep). One lock guards all room state; messages are tiny.
/// </summary>
/// <remarks>
/// Players come over TCP (the listener here: the LAN, a host's address, the relay inside the game) or over WebSocket
/// (<see cref="RelayHttp"/>: the public relay on Cloud Run); both feed the same rooms through <see cref="Accept"/>. A
/// client that asks for it gets a session to come back with over a new socket when its line drops or its host ends the
/// socket (see <see cref="Connection"/>): its seat waits for it, and the match never notices. One thread of the relay's own
/// closes the bundles of every room when they are due (<see cref="Beat"/>).
/// </remarks>
public sealed class RelayServer : IAsyncDisposable
{
    /// <summary>The relay's version, told to every client in its <see cref="Welcome"/> and shown by /health. Raise it when the relay changes.</summary>
    public const string Version = "1.2";

    /// <summary>How long a new socket may stay silent before its first message.</summary>
    private static readonly TimeSpan FirstMessage = TimeSpan.FromSeconds(30);

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, Member> _clients = [];
    private readonly Dictionary<int, Room> _rooms = [];
    private readonly Dictionary<string, Member> _sessions = [];
    private readonly Random _random = new();
    private readonly List<Task> _tasks = [];
    private readonly LinkTimings _timings;
    private readonly BeatTimings _beat;
    private readonly long _origin = Stopwatch.GetTimestamp();
    // The beat thread waits on it until the earliest bundle of any room is due; a turn, a load or a leave that makes one due
    // earlier wakes it.
    private readonly AutoResetEvent _wake = new(false);
    private readonly Thread _beatThread;
    private volatile bool _beating = true;
    // When the beat thread means to wake next (null: only when woken), under the lock.
    private TimeSpan? _planned;
    private bool _fineTimer;
    private int _disposed;
    private int _nextClient = 1, _nextRoom = 1;
    private Task _accepting = Task.CompletedTask;

    private RelayServer(TcpListener listener, LinkTimings timings, BeatTimings beat)
    {
        (_listener, _timings, _beat) = (listener, timings, beat);
        _beatThread = new Thread(BeatLoop) { IsBackground = true, Name = "relay beat" };
    }

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <param name="port">0 picks a free port (tests).</param>
    /// <param name="address">Where to listen; without one, on every address, IPv6 as well as IPv4 where the computer has it.</param>
    /// <param name="timings">How long a dropped player's seat waits, and the other clocks of a resumable connection (tests shorten them).</param>
    /// <param name="beat">The clocks and limits of the matches' beat (P2 design §2.1); without them, the relay's own.</param>
    public static RelayServer Start(int port, IPAddress? address = null, LinkTimings? timings = null, BeatTimings? beat = null)
    {
        var listener = new TcpListener(address ?? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any), port);
        if (address is null && Socket.OSSupportsIPv6)
            listener.Server.DualMode = true;
        listener.Start();
        var server = new RelayServer(listener, timings ?? LinkTimings.Default, beat ?? BeatTimings.Default);
        // Windows wakes a waiting thread on its 15.6 ms timer unless asked for 1 ms: the bundles would go out up to 16 ms late
        // (P2 design §2.2). The request is counted per process, so the relay inside the game asking again does no harm.
        if (OperatingSystem.IsWindows())
            server._fineTimer = WindowsTimer.Begin(1) == 0;
        // On the thread pool, whatever thread starts it: a game's main thread (busy with its frames) or a test runner's
        // few threads would otherwise run every message the relay handles, and hold up the other players behind them.
        server._accepting = Task.Run(server.AcceptLoop);
        server._beatThread.Start();
        return server;
    }

    public event Action<string>? Log;

    /// <summary>The relay's clock, which every beat's times come from.</summary>
    internal TimeSpan Now => Stopwatch.GetElapsedTime(_origin);

    /// <summary>How many rooms are open and how many players are connected, for /health.</summary>
    public (int Rooms, int Players) Count()
    {
        lock (_lock)
            return (_rooms.Count, _clients.Count);
    }

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                Track(Accept(new TcpLink(client)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Track(Task task)
    {
        lock (_lock)
        {
            _tasks.RemoveAll(t => t.IsCompleted);
            _tasks.Add(task);
        }
    }

    /// <summary>
    /// Takes a new socket: its first message either brings back a session whose line dropped (<see cref="ResumeLink"/>)
    /// or begins a new client. Completes when this socket ends; the client may live on over a later one.
    /// </summary>
    public async Task Accept(Link link)
    {
        NetMessage? first;
        try
        {
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            wait.CancelAfter(FirstMessage);
            first = await link.ReceiveAsync(wait.Token);
        }
        catch (Exception exception) when (exception is IOException or SocketException or System.Net.WebSockets.WebSocketException
                                              or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or InvalidDataException)
        {
            first = null;
        }
        if (first is null)
        {
            await link.DisposeAsync();
            return;
        }
        if (first is ResumeLink resume)
        {
            Member? member;
            lock (_lock)
                _sessions.TryGetValue(resume.Session, out member);
            if (member is null || !member.Connection.Resume(link, resume.Received))
            {
                try
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                    await link.SendAsync(new Refused("that connection is over"), timeout.Token);
                }
                catch (Exception exception) when (exception is IOException or SocketException or System.Net.WebSockets.WebSocketException
                                                      or OperationCanceledException or ObjectDisposedException)
                {
                }
                await link.DisposeAsync();
                Log?.Invoke($"refused to resume an unknown connection over {link.Kind}");
                return;
            }
            Log?.Invoke($"{member.Name} ({member.Id}) carries on over a new {link.Kind} socket");
        }
        else
            Track(Serve(new Connection(link, first, _timings), link.Kind));
        await link.Ended;
    }

    private async Task Serve(Connection connection, string kind)
    {
        Member? member = null;
        try
        {
            await foreach (var message in connection.Inbox.ReadAllAsync(_stop.Token))
            {
                lock (_lock)
                {
                    if (member is null)
                    {
                        if (message is not Hello hello)
                            continue;
                        if (hello.Protocol != NetJson.Protocol)
                        {
                            connection.Send(new Refused($"protocol {hello.Protocol}, the relay speaks {NetJson.Protocol}"));
                            break;
                        }
                        member = new Member(_nextClient++, string.IsNullOrWhiteSpace(hello.Name) ? "Player" : hello.Name.Trim(), connection);
                        _clients[member.Id] = member;
                        if (hello.Resume)
                        {
                            // The key to come back with: whoever holds it takes the seat, so it cannot be guessed.
                            member.Session = NewKey();
                            _sessions[member.Session] = member;
                            connection.MakeResumable(member.Session);
                        }
                        connection.Send(new Welcome(member.Id, member.Session, Version));
                        Log?.Invoke($"{member.Name} ({member.Id}) connected over {kind}");
                        continue;
                    }
                    Handle(member, message);
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            lock (_lock)
            {
                if (member is not null)
                {
                    Leave(member);
                    _clients.Remove(member.Id);
                    if (member.Session is not null)
                        _sessions.Remove(member.Session);
                    Log?.Invoke($"{member.Name} ({member.Id}) left");
                }
            }
            await connection.DisposeAsync();
        }
    }

    /// <summary>Cuts every player's socket as a broken line would, as Cloud Run does after its request timeout (tests).</summary>
    internal void CutLines()
    {
        lock (_lock)
        {
            foreach (var member in _clients.Values)
                member.Connection.Cut();
        }
    }

    private void Handle(Member member, NetMessage message)
    {
        switch (message)
        {
            case ListRooms:
                SendTo(member, new RoomList(_rooms.Values.Select(r => r.Summary()).ToList()));
                // Who comes back to the room from setting it up finds it as it is.
                if (member.Room is { Match: null } waiting)
                    SendTo(member, waiting.State());
                break;

            case CreateRoom create:
            {
                if (WhyNotRoom(create.Scenario, create.Slots, create.Options, create.SeatsPerSlot) is { } why)
                {
                    SendTo(member, new Refused(why));
                    break;
                }
                Leave(member);
                var room = new Room(_nextRoom++, AsBattle(create), member.Id);
                _rooms[room.Id] = room;
                Join(member, room);
                break;
            }

            case JoinRoom join:
                if (!_rooms.TryGetValue(join.RoomId, out var target))
                    SendTo(member, new Refused("no such room"));
                else if (target.Match is not null)
                    JoinLate(member, target, join);
                else if (!target.Seats.Any(s => s.IsFree))
                    SendTo(member, new Refused("that room is full"));
                else
                {
                    Leave(member);
                    Join(member, target);
                }
                break;

            case LeaveRoom:
                Leave(member);
                break;

            case SetPlayer set when member.Room is { Match: null } room:
                // The colour is the race's: no other race may show it, and every teammate shows it too.
                if (room.Members.Any(m => m.Race != member.Race && m.Colour == set.Colour))
                {
                    SendTo(member, new Refused($"{set.Colour} is taken"));
                    break;
                }
                // In a battle room a player brings a hero that may play in the room's bracket, and is ready only with one.
                BattleHero? hero = null;
                if (room.Setup.Options.Battle is { } battle)
                {
                    var why = set.Hero is { } chosen ? WhyNotHero(chosen, battle.Tier)
                        : set.Ready ? "choose a hero in your pod first" : null;
                    if (why is not null)
                    {
                        SendTo(member, new Refused(why));
                        break;
                    }
                    // Which seat a hero plays is the seat's to say, never the hero's (StartBattle).
                    hero = set.Hero is { } brought ? brought with { Name = HeroName(brought.Name, member.RoomName), Bot = false, Player = null } : null;
                }
                foreach (var mate in room.Members.Where(m => m.Race == member.Race))
                    mate.Colour = set.Colour;
                member.Ready = set.Ready;
                member.Terrain = set.Terrain;
                member.Hero = hero;
                Broadcast(room, room.State());
                break;

            case SetSeat seat when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    SendTo(member, new Refused("only the host changes the seats"));
                else if (seat.Seat >= 0 && seat.Seat < room.Seats.Count && room.Seats[seat.Seat].Member != member)
                {
                    // A battle has a hero for every seat of its teams: a seat nobody plays is the computer's.
                    room.Seats[seat.Seat].Use = room.Setup.Options.Battle is not null && seat.Use == SeatUse.Closed ? SeatUse.Computer : seat.Use;
                    if (room.Seats[seat.Seat].Member is { } sitting)
                        TakeOut(sitting, room);
                    else
                        Broadcast(room, room.State());
                }
                break;

            case TakeSeat take when member.Room is { Match: null } room:
                if (take.Seat < 0 || take.Seat >= room.Seats.Count)
                    SendTo(member, new Refused("no such seat"));
                else if (room.Seats[take.Seat].Member != member)
                {
                    if (!room.Seats[take.Seat].IsFree)
                        SendTo(member, new Refused("that seat is not free"));
                    else
                    {
                        Sit(room, member, room.Seats[take.Seat], keepColour: true);
                        Broadcast(room, room.State());
                    }
                }
                break;

            case ChangeRoom change when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    SendTo(member, new Refused("only the host changes the room"));
                else if (WhyNotRoom(change.Scenario, change.Slots, change.Options, change.SeatsPerSlot) is { } why)
                    SendTo(member, new Refused(why));
                else
                {
                    foreach (var removed in room.Change(AsBattle(change)))
                        TakeOut(removed, room);
                    Broadcast(room, room.State());
                }
                break;

            case StartMatch when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    SendTo(member, new Refused("only the host starts the match"));
                else if (room.Members.Any(m => !m.Ready))
                    SendTo(member, new Refused("not everyone is ready"));
                else if (room.Setup.Options.Battle is { } fight
                         && room.Members.Select(m => m.Hero is { } hero ? WhyNotHero(hero, fight.Tier) : "not everyone has chosen a hero")
                             .FirstOrDefault(why => why is not null) is { } unfit)
                    SendTo(member, new Refused(unfit));
                else
                    Start(room);
                break;

            case RejoinMatch rejoin:
                Rejoin(member, rejoin);
                break;

            // A player's own turns only: the beat stamps them, and places their commands on a tick (P2 design §2.3).
            case Turn turn when member.Room is { Beat: { } beat } room && member.Player is { } player && player == turn.Player:
                beat.Offer(turn, Now);
                Prompt(room);
                break;

            case MatchLoaded when member.Room is { Beat: { } beat } room && member.Player is { } loaded:
                beat.Loaded(loaded, Now);
                Prompt(room);
                break;

            case MatchDecided decided when member.Room is { Beat: { } beat } room && member.Player is { } decider:
                beat.Decided(decider, decided.Tick, Now);
                Notices(room, beat);
                Prompt(room);
                break;

            case ChecksumReport report when member.Room is { Beat: { } beat } room && member.Player == report.Player && Reports(beat, report.Player):
                Compare(room, report);
                break;

            // To the others only, as the original's broker sent it; one for a single race reaches every player of that race.
            case Chat chat when member.Room is { } room && chat.Text.Trim() is { Length: > 0 } text:
            {
                var said = chat with { From = member.RoomName, Text = text.Length > Chat.MaxText ? text[..Chat.MaxText] : text };
                foreach (var other in room.Members.Where(m => m != member && (chat.To is null || m.Race == chat.To)))
                    SendTo(other, said);
                break;
            }
        }
    }

    /// <summary>
    /// A new member takes the first free open seat in the room's fill order (<see cref="Room.FillOrder"/>: every race's first
    /// seat before any race's second), under a name no other member of the room shows, in the colour of the seat's race if a
    /// teammate shows one, else the first colour no other race shows.
    /// </summary>
    private void Join(Member member, Room room)
    {
        member.Room = room;
        member.Ready = false;
        member.Hero = null;
        member.Player = null;
        member.RoomName = room.UniqueName(member.Name);
        room.Members.Add(member);
        Sit(room, member, room.FillOrder().First(s => s.IsFree), keepColour: false);
        Broadcast(room, room.State());
    }

    /// <summary>
    /// A member sits at a seat (and at no other): they play its race, in the colour a teammate there shows, else (with
    /// <paramref name="keepColour"/>) their own while no other race shows it, else the first colour no other race shows.
    /// </summary>
    private static void Sit(Room room, Member member, Seat seat, bool keepColour)
    {
        foreach (var other in room.Seats.Where(s => s.Member == member))
            other.Member = null;
        seat.Member = member;
        member.Race = seat.Race;
        member.Colour = room.ColourFor(member, room.Members.Where(m => m != member), keepColour);
    }

    /// <summary>The host took a player out of the room: they are back in the lobby and told why.</summary>
    private void TakeOut(Member member, Room room)
    {
        Leave(member);
        SendTo(member, room.State());
        SendTo(member, new Refused("The host took you out of the room."));
    }

    /// <summary>
    /// A member leaves their room: in the lobby the others see it, in a running match their seat is gone from the next bundle
    /// on (<see cref="Beat.Leave"/>: a <see cref="PlayerLeft"/> at <see cref="Beat.NextTick"/>), and they may come back. The
    /// last member out drops the room, its beat and its log.
    /// </summary>
    private void Leave(Member member)
    {
        if (member.Room is not { } room)
            return;
        var player = member.Player;
        member.Room = null;
        (member.Race, member.Player, member.RoomName, member.Allowance) = (null, null, member.Name, 0);
        room.Members.Remove(member);
        foreach (var seat in room.Seats.Where(s => s.Member == member))
            seat.Member = null;
        if (player is { } leaver && room.Beat is { } beat)
        {
            beat.Leave(leaver);
            // The ticks only the leaver had still to report are compared now, among those still playing.
            SettlePending(room);
            _wake.Set();
        }
        if (room.Members.Count == 0)
        {
            // Settling what the leaver left may have taken the room's other members out, and dropped it already.
            if (_rooms.Remove(room.Id) && room.Beat is { Stopped: false } running)
                Log?.Invoke($"room {room.Id}: dropped with its last member; {running.Summary()}");
            return;
        }
        if (room.Match is null)
        {
            if (room.HostId == member.Id)
                room.HostId = room.Members[0].Id;
            Broadcast(room, room.State());
        }
    }

    /// <summary>
    /// The relay took a member out of the running match (<see cref="LeftMatch"/>): they are back in the lobby. Their seat's
    /// state is the beat's business, which made it so or is told by whoever calls this; nothing here queues a leave for it.
    /// </summary>
    private void TakeOutOfMatch(Member member, string reason)
    {
        SendTo(member, new LeftMatch(reason));
        member.Player = null;
        Leave(member);
    }

    /// <summary>
    /// A player comes back to a running match (P2 design §6.1). With a seat key: the seat that key is for, the player given,
    /// unless they were taken out of the match; from the connection that holds the seat it is a plain rebuild of its world,
    /// from another the seat changes hands and the old connection is told so. Without a key the relay knows them by the name
    /// they connected with: it takes the gone person of the match who connected under that name, the one
    /// <see cref="RejoinMatch.Player"/> names when it names one, else the lowest numbered, and gives the seat a new key.
    /// Either way the world is rebuilt from the relay's log (<see cref="Rebuild"/>) while the others play on.
    /// </summary>
    private void Rejoin(Member member, RejoinMatch rejoin)
    {
        if (!_rooms.TryGetValue(rejoin.RoomId, out var room) || room.Match is null || room.Beat is not { } beat)
        {
            SendTo(member, new Refused("that match is not running"));
            return;
        }
        if (rejoin.Key is { } key)
        {
            if (rejoin.Player is not { } player || !room.SeatKeys.TryGetValue(player, out var seatKey) || !SameKey(seatKey, key))
            {
                SendTo(member, new Refused("that seat key is not this match's"));
                return;
            }
            if (beat.Seat(player) is not { } seat || seat.State == SeatState.Barred)
            {
                SendTo(member, new Refused("you were taken out of that match"));
                return;
            }
            var holder = room.Members.FirstOrDefault(m => m.Player == player);
            if (holder == member)
            {
                Rebuild(member, room, player, ResumeReason.Rejoin, newKey: null);
                return;
            }
            if (member.Room is not null)
                Leave(member);
            // Leaving the seat it held may have left the room without members, and so dropped it with its beat and log.
            if (!_rooms.ContainsKey(room.Id))
            {
                SendTo(member, new Refused("that match is not running"));
                return;
            }
            Rebuild(member, room, player, ResumeReason.Rejoin, newKey: null);
            if (holder is not null)
                TakeOutOfMatch(holder, "you came back to this match from another connection");
            return;
        }
        // The seats of the match's people (and of its late joiners), by the name they connected with.
        var named = beat.Seats.Where(s => s.State is SeatState.Gone or SeatState.Barred && room.HelloNames.GetValueOrDefault(s.Player) == member.Name
                                          && (rejoin.Player is null || rejoin.Player == s.Player)).ToList();
        if (named.Where(s => s.State == SeatState.Gone).MinBy(s => s.Player.Value) is not { } gone)
        {
            SendTo(member, new Refused(named.Count > 0 ? "you were taken out of that match" : $"no player called {member.Name} left that match"));
            return;
        }
        Leave(member);
        if (!_rooms.ContainsKey(room.Id))
        {
            SendTo(member, new Refused("that match is not running"));
            return;
        }
        var fresh = NewKey();
        room.SeatKeys[gone.Player] = fresh;
        Rebuild(member, room, gone.Player, ResumeReason.Rejoin, fresh);
    }

    /// <summary>
    /// A player joins a running battle late, in a seat a bot has played since the start (P2 design §6.2): the seat
    /// <see cref="JoinRoom.Seat"/> names, else one on the team with fewer people (team A on a tie), the bot's seat there with the
    /// lowest player. Only a battle whose beat runs and whose end is not agreed takes one, and only with the battle's ground
    /// (<see cref="MatchInfo.Terrain"/>, when it has one). The joiner's world is rebuilt from the log while the bot plays on; once
    /// it is on time every world turns the bot's row into the joiner's (<see cref="PlayerJoined"/>), who takes over the hero as
    /// it stands.
    /// </summary>
    private void JoinLate(Member member, Room room, JoinRoom join)
    {
        // A member of the match itself comes back to it by RejoinMatch.
        if (room.Match!.Options.Battle is not { } battle || member.Room == room)
        {
            SendTo(member, new Refused("that match has already started"));
            return;
        }
        if (Joinable(room) == 0)
        {
            SendTo(member, new Refused("no seat to join in that battle"));
            return;
        }
        var beat = room.Beat!;
        PlayerId? named = null;
        if (join.Seat is { } index)
        {
            if (index < 0 || index >= room.Seats.Count)
            {
                SendTo(member, new Refused("no such seat"));
                return;
            }
            named = room.Seats[index].Player ?? PlayerId.Of(room.Seats[index].Race);
            if (beat.Seat(named.Value)?.State != SeatState.Bot)
            {
                SendTo(member, new Refused("that seat is not free"));
                return;
            }
        }
        // Without the battle's ground the joiner's shots would go through hills its world does not have: out of step, three
        // resyncs, and out.
        if (room.Match.Terrain is { } ground && join.Terrain != ground)
        {
            SendTo(member, new Refused("your game's ground differs from that battle's"));
            return;
        }
        if (member.Room is not null)
            Leave(member);
        // Leaving its own seat may have changed the seats (a joiner joining again), or dropped the room it was the last of.
        var player = named is { } seat && beat.Seat(seat)?.State == SeatState.Bot ? seat : named is null ? JoinSeat(room, battle) : null;
        if (!_rooms.ContainsKey(room.Id) || player is null)
        {
            SendTo(member, new Refused(_rooms.ContainsKey(room.Id) ? "no seat to join in that battle" : "that match is not running"));
            return;
        }
        var key = NewKey();
        room.SeatKeys[player.Value] = key;
        Rebuild(member, room, player.Value, ResumeReason.LateJoin, key, room.UniqueName(member.Name));
    }

    /// <summary>
    /// The seat a late joiner takes (P2 design §6.2): on the team with fewer people's seats (of people in any state, joiners
    /// included), team A on a tie, the bot's seat with the lowest player; on the other team when that one has none. Null when no
    /// seat is a bot's.
    /// </summary>
    private static PlayerId? JoinSeat(Room room, BattleOptions battle)
    {
        var beat = room.Beat!;
        var teams = BattleSeats.Of(battle).Select(s => (s.Team, Seat: beat.Seat(s.Player))).Where(s => s.Seat is not null).ToList();
        return teams.GroupBy(s => s.Team)
            .OrderBy(team => team.Count(s => s.Seat!.State != SeatState.Bot)).ThenBy(team => team.Key)
            .Select(team => team.Where(s => s.Seat!.State == SeatState.Bot).Select(s => (PlayerId?)s.Seat!.Player).Min())
            .FirstOrDefault(player => player is not null);
    }

    /// <summary>
    /// How many seats of a running battle a player may join late (<see cref="RoomSummary.Joinable"/>): the bots' seats, while
    /// the beat runs and the end is not agreed; none in any other room.
    /// </summary>
    private static int Joinable(Room room) =>
        room is { Match.Options.Battle: not null, Beat: { Anchor: not null, Stopped: false, DecidedAt: null } beat, Failed: false }
            ? beat.Seats.Count(s => s.State == SeatState.Bot)
            : 0;

    /// <summary>
    /// A player's world is rebuilt from the relay's log (P2 design §5.3): their seat stops playing at the next bundle (held,
    /// or in a battle handed to a bot) and returns once its world is built and on time (<see cref="Beat.Rebuild"/>); the
    /// member takes the seat; and before the next bundle it gets <see cref="MatchResumed"/>, the pieces of every sealed
    /// segment of the log (the same objects for every rebuild), and every bundle since as a plain one. What its old world
    /// reported and the relay has not compared yet is forgotten: from now on only its new world's reports count.
    /// </summary>
    /// <param name="name">A late joiner's name in the room, which every world gives the seat when it joins.</param>
    private void Rebuild(Member member, Room room, PlayerId player, ResumeReason reason, string? newKey, string? name = null)
    {
        var beat = room.Beat!;
        beat.Rebuild(player, name);
        var seat = beat.Seat(player)!;
        var row = room.Match!.Players.First(r => r.Player == player);
        member.Room = room;
        (member.Race, member.Player, member.RoomName, member.Colour, member.Ready) = (row.Slot, player, seat.Name ?? row.Name, row.Colour, true);
        if (!room.Members.Contains(member))
            room.Members.Add(member);
        room.HelloNames[player] = member.Name;
        Forget(room, player);
        var log = room.Bundles!;
        var parts = log.Segments.SelectMany(s => s.Parts).ToList();
        var plain = log.Plain().ToList();
        SendTo(member, new MatchResumed(room.Match, player, beat.NextTick, reason, parts.Count, newKey, seat.Resyncs));
        foreach (var part in parts)
            SendTo(member, part);
        foreach (var bundle in plain)
            SendTo(member, bundle);
        // A world rebuilt once the end is agreed hears of it as every member did (it may not have been one then, or its game
        // left that message behind with the world it rebuilds), after the log that leads to it.
        if (beat.DecidedAt is { } decided)
            SendTo(member, new MatchEnded(decided));
        // The package goes out at once: it may hold more than the backlog a member may have, and is no sign of a slow line; nor
        // is what the member had not acknowledged before it (a line just cut for its backlog acknowledges what it caught up
        // on only within a second). From what it has now on, its backlog may grow by MostBacklog as anyone's may.
        member.Allowance = member.Connection.Backlog;
        var what = reason switch
        {
            ResumeReason.Resync => $"resync {seat.Resyncs}/{_beat.MostResyncs}",
            ResumeReason.LateJoin => "late join",
            _ => "rejoin",
        };
        Log?.Invoke($"room {room.Id}: {what} of {member.Name} (player {player}) from tick {beat.NextTick}: log {parts.Sum(p => (long)p.Data.Length)} bytes in {parts.Count} parts and {plain.Count} bundles");
        _wake.Set();
    }

    /// <summary>
    /// Starts a normal room: a row for every seated member in seat order, each playing their seat's race, then one for the
    /// computer of every race nobody sits at whose seats ask for it (a computer seat, or an open one in a room filled with the
    /// AI), in race order; a race nobody plays is left out. The players are numbered by <see cref="PlayerTable.Number"/> over
    /// the rows in that order, so with one person a race every player has the race's own number, as the rows always had.
    /// Every row of a race shows the race's colour.
    /// </summary>
    private void Start(Room room)
    {
        if (room.Setup.Options.Battle is { } battle)
        {
            StartBattle(room, battle);
            return;
        }
        var rows = new List<MatchPlayer>();
        var people = new List<Member>();
        var colours = new Dictionary<Race, Race>();
        foreach (var seat in room.Seats.Where(s => s.Member is not null))
        {
            var member = seat.Member!;
            colours.TryAdd(seat.Race, member.Colour);
            rows.Add(new MatchPlayer(member.Id, member.RoomName, seat.Race, colours[seat.Race], Ai: false));
            people.Add(member);
        }
        var free = Enum.GetValues<Race>().Where(c => !colours.ContainsValue(c)).ToList();
        foreach (var race in room.Seats.Select(s => s.Race).Distinct().Order())
        {
            if (colours.ContainsKey(race)
                || !room.Seats.Any(s => s.Race == race && (s.Use == SeatUse.Computer || s.Use == SeatUse.Open && room.Setup.FillWithAi)))
                continue;
            var colour = free.Contains(race) ? race : free[0];
            free.Remove(colour);
            rows.Add(new MatchPlayer(0, "AI", race, colour, Ai: true));
        }
        var ids = PlayerTable.Number([.. rows.Select(r => r.Slot)]);
        rows = [.. rows.Select((row, i) => row with { Player = ids[i] })];
        for (var i = 0; i < people.Count; i++)
        {
            people[i].Player = ids[i];
            room.HelloNames[ids[i]] = people[i].Name;
        }
        // The ground goes into the match only when every player has the same (MatchInfo.Terrain).
        var terrain = room.Members.Select(m => m.Terrain).Distinct().ToList() is [{ } same] ? same : (long?)null;
        room.Match = new MatchInfo(room.Setup.Scenario, room.Setup.Planet, room.Setup.Options, _random.Next(1, int.MaxValue), StartDelay, rows, terrain);
        Broadcast(room, new MatchStarted(room.Match));
        Log?.Invoke($"room {room.Id} started {room.Setup.Scenario} with {room.Members.Count} players");
        BeginBeat(room);
    }

    /// <summary>
    /// Starts a battle room: each seated player is their seat's player (<see cref="BattleSeats"/>) and plays its race with the
    /// hero they brought, whatever player or race the hero named; every other seat is a bot hero of the computer, chosen for
    /// the map by the mirror rule (<see cref="BattleBots.Compose"/>) and named in its row after its hero. The teams are the
    /// map's (with a team size its two anchors), so every client gets the whole battle, every hero listed, in the match's
    /// options. Every row of a race shows the race's colour.
    /// </summary>
    private void StartBattle(Room room, BattleOptions battle)
    {
        var map = BattleCatalogue.Map(room.Setup.Scenario)!;
        var (teamA, teamB) = BattleSeats.Teams(battle with { TeamA = [], TeamB = [] }, map);
        var rows = new List<MatchPlayer>();
        var heroes = new List<BattleHero>();
        var colours = new Dictionary<Race, Race>();
        foreach (var seat in room.Seats.Where(s => s.Member is not null))
        {
            var member = seat.Member!;
            var player = seat.Player ?? PlayerId.Of(seat.Race);
            member.Player = player;
            room.HelloNames[player] = member.Name;
            colours.TryAdd(seat.Race, member.Colour);
            rows.Add(new MatchPlayer(member.Id, member.RoomName, seat.Race, colours[seat.Race], Ai: false, player));
            heroes.Add(member.Hero! with { Race = seat.Race, Player = player, Bot = false });
        }
        var composed = BattleBots.Compose(battle with { TeamA = teamA, TeamB = teamB, Heroes = [] }, heroes, map.Scenario);
        // Whatever the seat was set to, nobody's seat is left empty: a battle has a hero for every seat of its teams.
        var free = Enum.GetValues<Race>().Where(c => !colours.ContainsValue(c)).ToList();
        foreach (var seat in room.Seats.Where(s => s.Member is null))
        {
            if (!colours.TryGetValue(seat.Race, out var colour))
            {
                colour = colours[seat.Race] = free.Contains(seat.Race) ? seat.Race : free[0];
                free.Remove(colour);
            }
            var player = seat.Player ?? PlayerId.Of(seat.Race);
            var name = composed.Heroes.FirstOrDefault(h => (h.Player ?? PlayerId.Of(h.Race)) == player)?.Name ?? "AI";
            rows.Add(new MatchPlayer(0, name, seat.Race, colour, Ai: true, player));
        }
        var options = room.Setup.Options with { Battle = composed };
        var terrain = room.Members.Select(m => m.Terrain).Distinct().ToList() is [{ } same] ? same : (long?)null;
        room.Match = new MatchInfo(room.Setup.Scenario, room.Setup.Planet, options, _random.Next(1, int.MaxValue), StartDelay, rows, terrain);
        Broadcast(room, new MatchStarted(room.Match));
        Log?.Invoke($"room {room.Id} started a battle on {room.Setup.Scenario} at tier {battle.Tier} with {room.Members.Count} players");
        BeginBeat(room);
    }

    /// <summary>The most people commanding one race together in a normal room.</summary>
    public const int MostSeatsPerSlot = 2;

    /// <returns>
    /// Why a room cannot be opened or changed so, or null: it needs two start positions or more, each a race there is; a
    /// battle must be one the battle maps fight (<see cref="WhyNotBattle"/>); a normal room seats one or two people a race;
    /// and no room has more than <see cref="PlayerId.MostPlayers"/> seats.
    /// </returns>
    private static string? WhyNotRoom(string scenario, IReadOnlyList<Race> slots, MatchOptions options, int seatsPerSlot)
    {
        if (slots.Any(slot => !Enum.IsDefined(slot)))
            return "no such start position";
        if (slots.Distinct().Count() < 2)
            return "a room needs a map with at least two start positions";
        if (WhyNotBattle(scenario, options) is { } notBattle)
            return notBattle;
        if (Battled(options).Battle is null)
        {
            if (seatsPerSlot < 1)
                return "every side needs a seat";
            if (seatsPerSlot > MostSeatsPerSlot)
                return "two commanders per side at most";
        }
        var setup = new CreateRoom("", scenario, "", slots, options, false, seatsPerSlot);
        return Room.SeatPlan(AsBattle(setup)).Count > PlayerId.MostPlayers ? $"a room has at most {PlayerId.MostPlayers} seats" : null;
    }

    /// <returns>
    /// Why a room cannot be a battle so, or null; null for every room that is no battle. A battle is fought on a map of
    /// the battle catalogue, in a bracket whose top is a tier there is, by teams of today's layout or of a size up to
    /// <see cref="BattleBalance.MostTeamSize"/>.
    /// </returns>
    private static string? WhyNotBattle(string scenario, MatchOptions options) => Battled(options).Battle switch
    {
        null => null,
        _ when BattleCatalogue.Map(scenario) is null => "battles are fought only on the battle maps",
        { Tier: < 1 or > BattleCatalogue.MostTier } => "no such tier",
        { TeamSize: < 0 } => "no such team size",
        { TeamSize: > BattleBalance.MostTeamSize } => $"a team has at most {BattleBalance.MostTeamSize} heroes",
        _ => null,
    };

    /// <summary>
    /// Options asking for the battle victory without a battle's settings are a battle with the default ones, as
    /// <see cref="MatchSetup"/> plays them (<see cref="MatchOptions.IsBattle"/>); so such a room is checked, set up and started
    /// as the battle it is.
    /// </summary>
    private static MatchOptions Battled(MatchOptions options) =>
        options.IsBattle && options.Battle is null ? options with { Battle = new BattleOptions() } : options;

    /// <summary>
    /// A battle room as it will be played, so the room shows what the match will be: its map's planet, its teams' races as
    /// start positions (with a team size only the two anchors, <see cref="BattleSeats.Teams"/>), heroes driven in first
    /// person, and its settings within what the sim plays (lives 1 to 5, no negative wait or time, a victory there is). The
    /// teams' heroes are the players' own until the start composes them; a battle seats its team size, not
    /// <see cref="CreateRoom.SeatsPerSlot"/>. Only called once <see cref="WhyNotBattle"/> let the room be.
    /// </summary>
    private static CreateRoom AsBattle(CreateRoom create) => Battled(create.Options) is { Battle: { } battle } options && BattleCatalogue.Map(create.Scenario) is { } map
        ? create with { Planet = map.Planet, Slots = BattleSlots(battle, map), Options = AsBattle(options, battle, map), SeatsPerSlot = 1 }
        : create;

    /// <inheritdoc cref="AsBattle(CreateRoom)"/>
    private static ChangeRoom AsBattle(ChangeRoom change) => Battled(change.Options) is { Battle: { } battle } options && BattleCatalogue.Map(change.Scenario) is { } map
        ? change with { Planet = map.Planet, Slots = BattleSlots(battle, map), Options = AsBattle(options, battle, map), SeatsPerSlot = 1 }
        : change;

    private static MatchOptions AsBattle(MatchOptions options, BattleOptions battle, BattleMap map)
    {
        var (teamA, teamB) = BattleSeats.Teams(battle with { TeamA = [], TeamB = [] }, map);
        return options with
        {
            Victory = VictoryCondition.Battle,
            Wired = null,
            DisableFirstPerson = false,
            Battle = battle with
            {
                Lives = Math.Clamp(battle.Lives, 1, 5),
                RespawnWait = MathF.Max(0, battle.RespawnWait),
                TimeLimit = Math.Max(1, battle.TimeLimit),
                Win = Enum.IsDefined(battle.Win) ? battle.Win : BattleWin.Both,
                TeamA = teamA,
                TeamB = teamB,
                Heroes = [],
            },
        };
    }

    /// <summary>The start positions of a battle room: the races of the map's teams (with a team size, the anchors), in race order.</summary>
    private static List<Race> BattleSlots(BattleOptions battle, BattleMap map)
    {
        var (teamA, teamB) = BattleSeats.Teams(battle with { TeamA = [], TeamB = [] }, map);
        return teamA.Concat(teamB).Order().ToList();
    }

    /// <summary>The name a hero plays under: the one the player gave it, else the player's, at most <see cref="BattleHero.MostName"/> characters.</summary>
    private static string HeroName(string? given, string player)
    {
        var name = string.IsNullOrWhiteSpace(given) ? player.Trim() : given.Trim();
        if (name.Length <= BattleHero.MostName)
            return name;
        // Never cut a character drawn with two UTF-16 units in half.
        return name[..(char.IsHighSurrogate(name[BattleHero.MostName - 1]) ? BattleHero.MostName - 1 : BattleHero.MostName)];
    }

    /// <returns>
    /// Why a hero a player brings may not play in the bracket whose top is <paramref name="tier"/>, or null. The relay
    /// cannot tell whether the player owns it (that is the player's profile); it only keeps out what the bracket does not
    /// allow and what does not fit together, so that a room does not start with a hero the sim would have to clamp.
    /// </returns>
    private static string? WhyNotHero(BattleHero hero, int tier)
    {
        // A hand-made message may leave out what a hero must have.
        if (hero.Chassis is null || hero.Items is null || hero.Items.Any(i => i is null || i.Catalogue is null))
            return "that hero does not fit together";
        if (!BattleCatalogue.InBracket(hero.Chassis, tier))
            return "that hero is not in the room's bracket";
        return BattleBots.WhyNot(hero, tier) is null ? null : "that hero does not fit together";
    }

    /// <summary>The input delay a match starts with (<see cref="MatchInfo.InputDelay"/>), within the bounds the beat adapts it in.</summary>
    private int StartDelay => Math.Clamp(_beat.StartDelay, _beat.MinDelay, _beat.MaxDelay);

    /// <summary>
    /// The match's beat begins (P2 design §2.8): each person gets the key to take their seat back with (<see cref="SeatKey"/>),
    /// and the beat waits for everyone's <see cref="MatchLoaded"/>, or <see cref="BeatTimings.LoadTimeout"/>, before bundle 0.
    /// </summary>
    private void BeginBeat(Room room)
    {
        var beat = new Beat(_beat, room.Match!, Now);
        beat.Log += line => Log?.Invoke($"room {room.Id}: {line}");
        room.Beat = beat;
        room.Bundles = new BundleLog();
        foreach (var member in room.Members)
        {
            var player = member.Player!.Value;
            room.SeatKeys[player] = NewKey();
            SendTo(member, new SeatKey(room.Id, player, room.SeatKeys[player]));
        }
        _wake.Set();
    }

    /// <summary>
    /// The relay's beat thread (P2 design §2.2): under the lock it closes, room by room, every bundle that is due, and then
    /// waits until the earliest next one of any room, or until something makes one due earlier.
    /// </summary>
    private void BeatLoop()
    {
        while (_beating)
        {
            TimeSpan? wake = null;
            lock (_lock)
            {
                var now = Now;
                foreach (var room in _rooms.Values.Where(r => r.Beat is not null).ToList())
                {
                    if (!_rooms.ContainsKey(room.Id) || room.Failed)
                        continue;
                    // Only a room with something due is gone through (or one whose beat stopped, for its summary); the others'
                    // next times are only looked at. Going through every room at every bundle of any cost a relay of 200 rooms
                    // more than half a core.
                    TimeSpan? next;
                    try
                    {
                        next = room.Beat!.NextWake(now);
                    }
                    catch (Exception exception)
                    {
                        Fail(room, exception);
                        continue;
                    }
                    if (next is { } due && due <= now || room.Beat.Stopped && !room.Summarised)
                        next = Advance(room, now);
                    if (next is { } then && (wake is null || then < wake))
                        wake = then;
                }
                _planned = wake;
            }
            var wait = wake is { } at ? (int)Math.Clamp(Math.Ceiling((at - Now).TotalMilliseconds), 0, int.MaxValue) : Timeout.Infinite;
            if (wait != 0)
                _wake.WaitOne(wait);
        }
    }

    /// <summary>
    /// Closes every bundle of the room that may close now and sends each to every member, in order, onto the log; passes on
    /// what the beat has for the members; takes out every member whose line cannot carry the match; and compares the ticks
    /// whose reports are all in now that fewer may play. Returns when the room's beat wants to be called again, null for never.
    /// </summary>
    private TimeSpan? Advance(Room room, TimeSpan now)
    {
        if (room.Beat is not { } beat || room.Failed)
            return null;
        room.Advanced++;
        try
        {
            var bundles = beat.Close(now);
            // The beat starts here, or in the last MatchLoaded, after which this runs at once. Its time in UTC lets the clients'
            // pace lines, which carry theirs, be set against the relay's clock (P2 design §5.6).
            if (room.AnchorTicks < 0 && beat.Anchor is { } anchor)
            {
                Volatile.Write(ref room.AnchorTicks, anchor.Ticks);
                Log?.Invoke($"room {room.Id}: bundle 0 was due at {(DateTimeOffset.UtcNow - (Now - anchor)).ToUnixTimeMilliseconds()} ms UTC");
            }
            foreach (var bundle in bundles)
            {
                if (room.Bundles!.Add(bundle, _beat.SegmentTicks) is { } run)
                    Seal(room, room.Bundles, run);
                Broadcast(room, bundle);
            }
            Notices(room, beat);
            Backlogs(room);
            if (bundles.Count > 0)
                SettlePending(room);
            if (beat.Stopped && !room.Summarised)
            {
                room.Summarised = true;
                Log?.Invoke($"room {room.Id}: {beat.Summary()}");
            }
            return _rooms.ContainsKey(room.Id) ? beat.NextWake(now) : null;
        }
        catch (Exception exception)
        {
            Fail(room, exception);
            return null;
        }
    }

    /// <summary>
    /// A fault in one match must not stop the others' beat, nor the relay's thread (an exception there would end the relay's
    /// process): this one stands still, and its players can leave.
    /// </summary>
    private void Fail(Room room, Exception exception)
    {
        room.Failed = true;
        Log?.Invoke($"room {room.Id}: the beat failed and stands still: {exception}");
    }

    /// <summary>
    /// After something moved the room's next bundle earlier (a turn, a load, a decision): closes it now when it is due, else
    /// wakes the beat thread when that is before the time it means to wake.
    /// </summary>
    private void Prompt(Room room)
    {
        if (room.Beat is not { } beat)
            return;
        var now = Now;
        if (beat.NextWake(now) is { } wake && wake <= now)
            Advance(room, now);
        if (_rooms.ContainsKey(room.Id) && beat.NextWake(now) is { } next && (_planned is not { } planned || next < planned))
            _wake.Set();
    }

    /// <summary>
    /// What the beat has for the members: the agreed end to everyone, and to a player it took out of the match
    /// (<see cref="LeftMatch"/>, its seat already gone) the reason, after which that member is back in the lobby.
    /// </summary>
    private void Notices(Room room, Beat beat)
    {
        foreach (var notice in beat.Drain())
        {
            if (notice.To is not { } to)
                Broadcast(room, notice.Message);
            else if (room.Members.FirstOrDefault(m => m.Player == to) is { } member)
            {
                if (notice.Message is LeftMatch left)
                    TakeOutOfMatch(member, left.Reason);
                else
                    SendTo(member, notice.Message);
            }
        }
    }

    /// <summary>
    /// A member whose connection holds more than <see cref="BeatTimings.MostBacklog"/> messages it has not had acknowledged
    /// (beyond those it had with a rebuild package sent at once) cannot carry the match's traffic (P2 design §2.4 step 7): it is told
    /// so and leaves as a goodbye does; its seat is gone, and it may come back with its seat key.
    /// </summary>
    private void Backlogs(Room room)
    {
        foreach (var member in room.Members.Where(m => m.Player is not null).ToList())
        {
            // What is left of a rebuild package not acknowledged yet is in the backlog, so the allowance shrinks as the backlog
            // does, never below what the package may still hold: a package smaller than the limit stays allowed until an
            // acknowledgement (once a second) takes it off, rather than counting from the first look at it.
            var backlog = member.Connection.Backlog;
            member.Allowance = Math.Min(member.Allowance, backlog);
            if (backlog > _beat.MostBacklog + member.Allowance)
            {
                Log?.Invoke($"room {room.Id}: {member.Name} (player {member.Player}) has {backlog} messages not acknowledged: taken out of the match");
                SendTo(member, new LeftMatch("Your connection cannot keep up with this match."));
                Leave(member);
            }
        }
    }

    /// <summary>
    /// A run of the log is sealed on the thread pool, outside the lock (P2 design §2.10): its bundles with something in them
    /// as gzipped JSON in pieces; under the lock again the pieces join the sealed segments once every older run's have.
    /// </summary>
    /// <remarks>
    /// A run whose sealing fails is tried once more; failing again, it stays in the log as plain bundles, which every rebuild
    /// is sent instead, and so do the runs after it (which wait for it). Nothing of the match is lost either way.
    /// </remarks>
    private void Seal(Room room, BundleLog log, SealRun run)
    {
        var partBytes = _beat.PartBytes;
        var hook = BeforeSeal;
        Track(Task.Run(async () =>
        {
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    if (hook is not null)
                        await hook(run.From, run.To);
                    var parts = BundleSegments.Seal(run.From, run.To, run.Bundles, partBytes);
                    lock (_lock)
                        log.Sealed(run, parts);
                    return;
                }
                catch (Exception exception)
                {
                    lock (_lock)
                        Log?.Invoke($"room {room.Id}: sealing ticks {run.From}–{run.To} failed{(attempt < 2 ? ", trying again" : "; they stay plain bundles")}: {exception.Message}");
                    if (attempt >= 2)
                        return;
                }
            }
        }));
    }

    /// <summary>
    /// Whether a player's checksum counts: a playing person's, or a returning one's once its world is built (a lagged one's
    /// always); a rebuilding or joining world's before its <see cref="MatchLoaded"/> is the old world's.
    /// </summary>
    private static bool Reports(Beat beat, PlayerId player) => beat.Seat(player) is { } seat
        && (seat.State == SeatState.Playing || seat.State == SeatState.Returning && (seat.Loaded || seat.Returning == ReturnKind.Lagged));

    /// <summary>
    /// A player's checksum of a tick (P2 design §5.2): one of a tick already agreed (a straggler, or a returning world catching
    /// up) is checked against it at once; any other waits with the tick's reports until a value is agreed.
    /// </summary>
    private void Compare(Room room, ChecksumReport report)
    {
        if (room.Agreed.TryGetValue(report.Tick, out var agreed))
        {
            if (agreed != report.Value)
                OutOfStep(room, report.Player, report.Tick, $"{report.Value:X16} against the agreed {agreed:X16}");
            return;
        }
        if (!room.Checksums.TryGetValue(report.Tick, out var reports))
            room.Checksums[report.Tick] = reports = [];
        reports[report.Player] = report.Value;
        Settle(room, report.Tick);
    }

    /// <summary>Every tick still waiting for reports is settled again: fewer may be playing now, or a returning player plays again.</summary>
    private void SettlePending(Room room)
    {
        if (room.Checksums.Count == 0)
            return;
        foreach (var tick in room.Checksums.Keys.Order().ToList())
        {
            // A world taken out of the match may have been the room's last member.
            if (!_rooms.ContainsKey(room.Id))
                return;
            Settle(room, tick);
        }
    }

    /// <summary>
    /// A tick's reports are settled (P2 design §5.2) as soon as more than half of the persons playing (reported or not)
    /// reported one value: it is agreed, and every reporter with another value, playing or returning, is out of step. When
    /// every person playing reported the tick without such a value, every reporter of it is out of step (a 1 vs 1 that
    /// differs rebuilds both) and nothing is agreed. Reports of seats no longer reporting (gone, taken out, rebuilt) do not count.
    /// </summary>
    private void Settle(Room room, long tick)
    {
        var beat = room.Beat!;
        if (!room.Checksums.TryGetValue(tick, out var all))
            return;
        var reports = all.Where(r => Reports(beat, r.Key)).OrderBy(r => r.Key.Value).ToList();
        var playing = beat.Seats.Where(s => s.State == SeatState.Playing).Select(s => s.Player).ToList();
        if (playing.Count == 0)
            return;
        var votes = reports.Where(r => playing.Contains(r.Key)).ToList();
        if (votes.GroupBy(r => r.Value).FirstOrDefault(g => g.Count() * 2 > playing.Count) is { } majority)
        {
            room.Checksums.Remove(tick);
            room.Agreed[tick] = majority.Key;
            foreach (var (player, value) in reports.Where(r => r.Value != majority.Key))
                OutOfStep(room, player, tick, $"{value:X16} against {majority.Key:X16}, agreed by {majority.Count()} of {playing.Count} playing");
        }
        else if (votes.Count == playing.Count)
        {
            room.Checksums.Remove(tick);
            foreach (var (player, value) in reports)
                OutOfStep(room, player, tick, $"{value:X16}, no value agreed by more than half of {playing.Count} playing");
        }
    }

    /// <summary>
    /// A player's world was found out of step at a tick (P2 design §5.2, §5.5), at most once a tick for each world it builds:
    /// up to <see cref="BeatTimings.MostResyncs"/> times it is rebuilt from the log (a resync), the next time the player is
    /// taken out of the match. Once the end is agreed it is only logged: that world's battle does not count (§2.8).
    /// </summary>
    private void OutOfStep(Room room, PlayerId player, long tick, string values)
    {
        var beat = room.Beat!;
        if (!room.OutOfStepAt.Add((player, tick)))
            return;
        var member = room.Members.FirstOrDefault(m => m.Player == player);
        var name = member?.Name ?? room.HelloNames.GetValueOrDefault(player) ?? "nobody";
        if (beat.DecidedAt is { } decided)
        {
            Log?.Invoke($"room {room.Id}: {name} (player {player}) out of step at tick {tick}: {values}; the match is decided at tick {decided}: nothing done");
            return;
        }
        if (member is null)
            return;
        var count = beat.OutOfStep(player);
        Log?.Invoke($"room {room.Id}: {name} (player {player}) out of step at tick {tick}: {values}");
        if (count <= _beat.MostResyncs)
        {
            Rebuild(member, room, player, ResumeReason.Resync, newKey: null);
            return;
        }
        beat.Bar(player);
        Log?.Invoke($"room {room.Id}: {name} (player {player}) out of step {count} times: taken out of the match at tick {beat.NextTick}");
        TakeOutOfMatch(member, "Your game kept going out of step with the others, so it has left the match.");
        _wake.Set();
    }

    /// <summary>
    /// A player's world is about to be rebuilt: what its old world reported and the relay has not settled yet no longer counts,
    /// and its new world may be found out of step at the same ticks again.
    /// </summary>
    private static void Forget(Room room, PlayerId player)
    {
        foreach (var (tick, reports) in room.Checksums.ToList())
        {
            if (reports.Remove(player) && reports.Count == 0)
                room.Checksums.Remove(tick);
        }
        room.OutOfStepAt.RemoveWhere(o => o.Player == player);
    }

    private void Broadcast(Room room, NetMessage message)
    {
        foreach (var member in room.Members)
            SendTo(member, message);
    }

    private void SendTo(Member member, NetMessage message)
    {
        member.Connection.Send(message);
        Sent?.Invoke(member.Id, message);
    }

    /// <summary>A key nobody can guess: 128 random bits in hex.</summary>
    private static string NewKey() => Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

    private static bool SameKey(string a, string b) => CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));

    /// <summary>Tests: every message the relay sends, with the id of the client it goes to, under the lock.</summary>
    internal Action<int, NetMessage>? Sent { get; set; }

    /// <summary>Tests: awaited on the thread pool before a run of the log from the first tick to the second is sealed.</summary>
    internal Func<long, long, Task>? BeforeSeal { get; set; }

    /// <summary>Tests: the relay stands still for a while under its lock, as a stalled computer would.</summary>
    internal void HoldStill(TimeSpan time)
    {
        lock (_lock)
            Thread.Sleep(time);
    }

    /// <summary>Tests: reads a running match's beat under the lock.</summary>
    internal T WithBeat<T>(int room, Func<Beat, T> read)
    {
        lock (_lock)
            return read(_rooms[room].Beat ?? throw new InvalidOperationException($"room {room} runs no match"));
    }

    /// <summary>
    /// Tests: the relay's clock in a match's ticks (the bundle due now on its schedule), read without the lock; null before the
    /// beat started.
    /// </summary>
    internal Func<long?> Clock(int room)
    {
        Room target;
        lock (_lock)
            target = _rooms[room];
        var interval = _beat.Interval.Ticks;
        return () => Volatile.Read(ref target.AnchorTicks) is var anchor && anchor >= 0 ? (Now.Ticks - anchor) / interval : null;
    }

    /// <summary>Tests: the shape of a match's log: its sealed segments, the runs still being sealed (and whether done), and the tail.</summary>
    internal (List<(long From, long To, int Parts)> Segments, List<(long From, long To, bool Done)> Sealing, long TailFrom, int Tail) LogShape(int room)
    {
        lock (_lock)
        {
            var log = _rooms[room].Bundles ?? throw new InvalidOperationException($"room {room} runs no match");
            return (log.Segments.Select(s => (s.From, s.To, s.Parts.Count)).ToList(), log.Sealing.Select(r => (r.From, r.To, r.Parts is not null)).ToList(),
                log.TailFrom, log.Tail.Count);
        }
    }

    /// <summary>Tests: the checksums of a tick reported and not yet compared, by player; null when none wait.</summary>
    internal IReadOnlyDictionary<PlayerId, ulong>? Pending(int room, long tick)
    {
        lock (_lock)
            return _rooms[room].Checksums.TryGetValue(tick, out var reports) ? new Dictionary<PlayerId, ulong>(reports) : null;
    }

    /// <summary>Tests: how often the relay went through a room's beat to close what was due, and how many bundles it closed.</summary>
    internal (int Advanced, long Bundles) Beats(int room)
    {
        lock (_lock)
            return (_rooms[room].Advanced, _rooms[room].Beat?.NextTick ?? 0);
    }

    /// <summary>Tests: the checksum every player agreed on for a tick, or null.</summary>
    internal ulong? Agreed(int room, long tick)
    {
        lock (_lock)
            return _rooms[room].Agreed.TryGetValue(tick, out var value) ? value : null;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;
        _stop.Cancel();
        _listener.Stop();
        _beating = false;
        _wake.Set();
        _beatThread.Join();
        await _accepting;
        Task[] running;
        lock (_lock)
            running = _tasks.ToArray();
        await Task.WhenAll(running);
        if (_fineTimer)
            WindowsTimer.End(1);
        _wake.Dispose();
    }

    /// <summary>Windows' timer resolution (winmm.dll), asked for 1 ms while the relay runs.</summary>
    private static class WindowsTimer
    {
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        public static extern uint Begin(uint period);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        public static extern uint End(uint period);
    }

    private sealed class Member(int id, string name, Connection connection)
    {
        public int Id { get; } = id;

        /// <summary>The name the player connected with (<see cref="Hello.Name"/>), by which they rejoin a match.</summary>
        public string Name { get; } = name;

        public Connection Connection { get; } = connection;

        /// <summary>The key the player's connection comes back with over a new socket; null for a client that did not ask.</summary>
        public string? Session { get; set; }

        public Room? Room { get; set; }

        /// <summary>The name the room shows for the player: their own, with a number when another member already shows it.</summary>
        public string RoomName { get; set; } = name;

        /// <summary>The colour of the player's race, alike for every teammate.</summary>
        public Race Colour { get; set; }

        public bool Ready { get; set; }
        public long? Terrain { get; set; }

        /// <summary>The race of the player's seat; null outside a room.</summary>
        public Race? Race { get; set; }

        /// <summary>The player they are in the room's match, from its start or their return; null before and outside one.</summary>
        public PlayerId? Player { get; set; }

        /// <summary>In a battle room, the hero the player brings; null until they chose one, and in every other room.</summary>
        public BattleHero? Hero { get; set; }

        /// <summary>
        /// The backlog the member had once the rebuild package sent last was on its way, which it may hold on top of its limit;
        /// it shrinks with the backlog as acknowledgements take the package off.
        /// </summary>
        public int Allowance { get; set; }
    }

    /// <summary>
    /// A seat of a room: the race it plays and its place among that race's seats, and in a battle the player it is
    /// (<see cref="BattleSeats"/>); in a normal room the players are numbered at the start, from who sits where.
    /// </summary>
    private sealed class Seat(Race race, int place, PlayerId? player)
    {
        public Race Race { get; } = race;
        public int Place { get; } = place;
        public PlayerId? Player { get; } = player;
        public Member? Member { get; set; }
        public SeatUse Use { get; set; }
        public bool IsFree => Member is null && Use == SeatUse.Open;
    }

    /// <summary>
    /// A run of the log being sealed: its first and last tick and every bundle between (P2 design §2.10); the pieces once sealed.
    /// </summary>
    private sealed class SealRun(long from, long to, IReadOnlyList<Bundle> bundles)
    {
        public long From { get; } = from;
        public long To { get; } = to;
        public IReadOnlyList<Bundle> Bundles { get; } = bundles;
        public IReadOnlyList<LogPart>? Parts { get; set; }
    }

    /// <summary>A sealed segment of the log: the bundles from one tick to another, as the pieces every rebuild is sent.</summary>
    private sealed record SealedSegment(long From, long To, IReadOnlyList<LogPart> Parts);

    /// <summary>
    /// A running match's log (P2 design §2.10): the sealed segments in order, the runs still being sealed (oldest first; one
    /// that finishes before an older one waits for it), and every bundle since, empty ones too. A rebuild gets the segments'
    /// pieces, then the bundles of the runs and the tail as plain bundles, so the numbering runs on without a gap.
    /// </summary>
    private sealed class BundleLog
    {
        private List<Bundle> _tail = [];

        public long TailFrom { get; private set; }
        public IReadOnlyList<Bundle> Tail => _tail;
        public List<SealRun> Sealing { get; } = [];
        public List<SealedSegment> Segments { get; } = [];

        /// <summary>A bundle goes onto the tail; every <paramref name="segmentTicks"/> bundles the tail becomes a run to seal, returned.</summary>
        public SealRun? Add(Bundle bundle, int segmentTicks)
        {
            _tail.Add(bundle);
            if (_tail.Count < Math.Max(1, segmentTicks))
                return null;
            var run = new SealRun(TailFrom, bundle.Tick, _tail);
            Sealing.Add(run);
            (_tail, TailFrom) = ([], bundle.Tick + 1);
            return run;
        }

        /// <summary>A run is sealed: it and every run after it that is sealed too join the segments, once every older run has.</summary>
        public void Sealed(SealRun run, IReadOnlyList<LogPart> parts)
        {
            run.Parts = parts;
            while (Sealing.Count > 0 && Sealing[0].Parts is { } done)
            {
                Segments.Add(new SealedSegment(Sealing[0].From, Sealing[0].To, done));
                Sealing.RemoveAt(0);
            }
        }

        /// <summary>The bundles after the sealed segments: every run still being sealed, then the tail.</summary>
        public IEnumerable<Bundle> Plain() => Sealing.SelectMany(r => r.Bundles).Concat(_tail);
    }

    private sealed class Room(int id, CreateRoom setup, int hostId)
    {
        public int Id { get; } = id;
        public CreateRoom Setup { get; private set; } = setup;
        public int HostId { get; set; } = hostId;
        public List<Member> Members { get; } = [];
        public List<Seat> Seats { get; private set; } = SeatPlan(setup);
        public MatchInfo? Match { get; set; }

        /// <summary>The running match's beat, from its start.</summary>
        public Beat? Beat { get; set; }

        /// <summary>The running match's log of bundles, for rebuilds.</summary>
        public BundleLog? Bundles { get; set; }

        /// <summary>The key each person of the match takes their seat back with (<see cref="SeatKey"/>).</summary>
        public Dictionary<PlayerId, string> SeatKeys { get; } = [];

        /// <summary>When bundle 0 was due on the relay's clock, in ticks of <see cref="TimeSpan"/>; −1 before. Read without the lock (tests).</summary>
        public long AnchorTicks = -1;

        /// <summary>Whether the beat's summary went to the log.</summary>
        public bool Summarised { get; set; }

        /// <summary>Whether the beat failed and stands still.</summary>
        public bool Failed { get; set; }

        /// <summary>How often the relay went through the room's beat to close what was due (tests).</summary>
        public int Advanced { get; set; }

        /// <summary>The checksums reported of each tick not settled yet, by player.</summary>
        public Dictionary<long, Dictionary<PlayerId, ulong>> Checksums { get; } = [];

        /// <summary>The checksum agreed for a tick (P2 design §5.2): more than half of the persons playing reported it.</summary>
        public Dictionary<long, ulong> Agreed { get; } = [];

        /// <summary>The ticks at which each player's present world was found out of step: once a tick and world is enough.</summary>
        public HashSet<(PlayerId Player, long Tick)> OutOfStepAt { get; } = [];

        /// <summary>The name each person of the match connected with, by which they rejoin it.</summary>
        public Dictionary<PlayerId, string> HelloNames { get; } = [];

        /// <summary>
        /// The seats of a room: in a normal room and a battle of today's layout the start positions in race order, as many
        /// seats each as <see cref="CreateRoom.SeatsPerSlot"/> says (a battle one); in a battle with a team size the seats of
        /// <see cref="BattleSeats.Of"/>, that many of team A's anchor, then as many of team B's.
        /// </summary>
        public static List<Seat> SeatPlan(CreateRoom setup)
        {
            if (setup.Options.Battle is { } battle && BattleSeats.Size(battle) > 0)
                return [.. BattleSeats.Of(battle).Select(s => new Seat(s.Race, s.Place, s.Player))];
            var battleRoom = setup.Options.Battle is not null;
            var each = battleRoom ? 1 : Math.Max(1, setup.SeatsPerSlot);
            return [.. setup.Slots.Distinct().Order().SelectMany(race => Enumerable.Range(0, each)
                .Select(place => new Seat(race, place, battleRoom ? PlayerId.Of(race) : null)))];
        }

        /// <summary>The seats in the order members take them: by their place in their race, then in seat order, so every race's first seat is taken before any race's second.</summary>
        public IEnumerable<Seat> FillOrder() => Seats.Select((seat, index) => (seat, index)).OrderBy(s => s.seat.Place).ThenBy(s => s.index).Select(s => s.seat);

        /// <summary>A name for a member that no other member shows: their own, else with the first free number from 2.</summary>
        public string UniqueName(string name)
        {
            var shown = Members.Select(m => m.RoomName).ToHashSet(StringComparer.Ordinal);
            if (!shown.Contains(name))
                return name;
            for (var number = 2; ; number++)
            {
                if (!shown.Contains($"{name} {number}"))
                    return $"{name} {number}";
            }
        }

        /// <summary>
        /// The colour a member shows among <paramref name="others"/>: a teammate's, else (with <paramref name="keepColour"/>)
        /// their own while no other race shows it, else the first colour no other race shows.
        /// </summary>
        public Race ColourFor(Member member, IEnumerable<Member> others, bool keepColour)
        {
            var list = others.ToList();
            if (list.FirstOrDefault(m => m.Race == member.Race) is { } mate)
                return mate.Colour;
            var taken = list.Where(m => m.Race != member.Race).Select(m => m.Colour).ToHashSet();
            if (keepColour && !taken.Contains(member.Colour))
                return member.Colour;
            return Enum.GetValues<Race>().Where(c => !taken.Contains(c)).Cast<Race?>().FirstOrDefault() ?? member.Colour;
        }

        public RoomSummary Summary() => new(Id, Setup.Name, Setup.Scenario, Setup.Planet, Members.Count,
            Seats.Count(s => s.Member is not null || s.Use == SeatUse.Open), Match is not null,
            Setup.Options.Battle is null ? null : RoomSummary.Battle, Setup.Options.Battle?.Tier ?? 0, Joinable(this));

        public RoomState State() => new(Id, Setup.Name, Setup.Scenario, Setup.Planet, Setup.Slots, Setup.Options, Setup.FillWithAi, HostId,
            Members.Select(m => new LobbyPlayer(m.Id, m.RoomName, m.Colour, m.Ready, m.Terrain, m.Hero)).ToList(),
            Seats.Select(s => new RoomSeat(s.Member?.Id, s.Use, s.Race)).ToList(), Setup.SeatsPerSlot);

        /// <summary>
        /// The room on a new map or with new seats, its players at the new seats in the fill order, the host first, each in
        /// the colour of their new race as <see cref="ColourFor"/> gives it; returns those it has no seat for. The seats nobody
        /// sits at keep what they were for: a seat of a race and place the room had free what that one was for, the others
        /// what the remaining free seats were for, in order. While the seats stay the same (only a setting changed), everyone
        /// stays where they sat and every seat nobody sits at stays what it was for. The heroes players brought stay only while
        /// the room stays a battle on the same map and bracket; a battle has no closed seats.
        /// </summary>
        public List<Member> Change(ChangeRoom change)
        {
            var heroesStay = Setup.Options.Battle is { } was && change.Options.Battle is { } now && was.Tier == now.Tier && Setup.Scenario == change.Scenario;
            var people = Seats.Where(s => s.Member is not null).Select(s => s.Member!).OrderByDescending(m => m.Id == HostId).ToList();
            var free = Seats.Where(s => s.Member is null).ToList();
            Setup = Setup with
            {
                Scenario = change.Scenario, Planet = change.Planet, Slots = change.Slots, Options = change.Options, FillWithAi = change.FillWithAi,
                SeatsPerSlot = change.SeatsPerSlot,
            };
            var plan = SeatPlan(Setup);
            // A seat a player chose (TakeSeat) or the host set (SetSeat) is not undone by a change of the settings alone.
            if (!plan.Select(s => (s.Race, s.Place, s.Player)).SequenceEqual(Seats.Select(s => (s.Race, s.Place, s.Player))))
            {
                Seats = plan;
                var seated = new List<Member>();
                foreach (var (seat, member) in FillOrder().Zip(people))
                {
                    seat.Member = member;
                    member.Race = seat.Race;
                    member.Colour = ColourFor(member, seated, keepColour: true);
                    seated.Add(member);
                }
                // A free seat of a race and place the room had free keeps what that one was for (a side the host gave the
                // computer stays the computer's when the room takes a second commander a side or five heroes a team); the
                // others take what the remaining free seats were for, in order.
                var unmatched = new List<Seat>();
                foreach (var seat in Seats.Where(s => s.Member is null))
                {
                    if (free.FirstOrDefault(old => old.Race == seat.Race && old.Place == seat.Place) is { } same)
                    {
                        seat.Use = same.Use;
                        free.Remove(same);
                    }
                    else
                        unmatched.Add(seat);
                }
                foreach (var (seat, i) in unmatched.Select((seat, i) => (seat, i)))
                    seat.Use = i < free.Count ? free[i].Use : SeatUse.Open;
            }
            foreach (var member in Members)
                (member.Ready, member.Terrain) = (false, null);
            if (change.Options.Battle is not null)
            {
                foreach (var seat in Seats.Where(s => s.Use == SeatUse.Closed))
                    seat.Use = SeatUse.Computer;
            }
            if (!heroesStay)
            {
                foreach (var member in Members)
                    member.Hero = null;
            }
            return people.Skip(Seats.Count).ToList();
        }
    }
}
