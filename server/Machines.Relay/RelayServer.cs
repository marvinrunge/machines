using System.Net;
using System.Net.Sockets;
using Machines.Import.Model;
using Machines.Net;

namespace Machines.Relay;

/// <summary>
/// The multiplayer relay: players meet in rooms, the host starts the match, and from then on the relay forwards
/// every player's turns to the others and compares their checksums. It never runs the simulation; every client
/// does (deterministic lockstep). One lock guards all room state; messages are tiny.
/// </summary>
public sealed class RelayServer : IAsyncDisposable
{
    /// <summary>Ticks between a command and every world carrying it out: 200 ms at 20 ticks a second.</summary>
    public const int InputDelay = 4;

    private readonly TcpListener _listener;
    private readonly CancellationTokenSource _stop = new();
    private readonly object _lock = new();
    private readonly Dictionary<int, Member> _clients = [];
    private readonly Dictionary<int, Room> _rooms = [];
    private readonly Random _random = new();
    private readonly List<Task> _tasks = [];
    private int _nextClient = 1, _nextRoom = 1;
    private Task _accepting = Task.CompletedTask;

    private RelayServer(TcpListener listener) => _listener = listener;

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <param name="port">0 picks a free port (tests).</param>
    /// <param name="address">Where to listen; without one, on every address, IPv6 as well as IPv4 where the computer has it.</param>
    public static RelayServer Start(int port, IPAddress? address = null)
    {
        var listener = new TcpListener(address ?? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any), port);
        if (address is null && Socket.OSSupportsIPv6)
            listener.Server.DualMode = true;
        listener.Start();
        var server = new RelayServer(listener);
        server._accepting = server.AcceptLoop();
        return server;
    }

    public event Action<string>? Log;

    private async Task AcceptLoop()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                lock (_lock)
                    _tasks.Add(Serve(new Connection(client)));
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private async Task Serve(Connection connection)
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
                        connection.Send(new Welcome(member.Id));
                        Log?.Invoke($"{member.Name} ({member.Id}) connected");
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
                    Log?.Invoke($"{member.Name} ({member.Id}) left");
                }
            }
            await connection.DisposeAsync();
        }
    }

    private void Handle(Member member, NetMessage message)
    {
        switch (message)
        {
            case ListRooms:
                member.Connection.Send(new RoomList(_rooms.Values.Select(r => r.Summary()).ToList()));
                // Who comes back to the room from setting it up finds it as it is.
                if (member.Room is { Match: null } waiting)
                    member.Connection.Send(waiting.State());
                break;

            case CreateRoom create:
            {
                if (create.Slots.Count < 2)
                {
                    member.Connection.Send(new Refused("a room needs a map with at least two start positions"));
                    break;
                }
                Leave(member);
                var room = new Room(_nextRoom++, create, member.Id);
                _rooms[room.Id] = room;
                Join(member, room);
                break;
            }

            case JoinRoom join:
                if (!_rooms.TryGetValue(join.RoomId, out var target))
                    member.Connection.Send(new Refused("no such room"));
                else if (target.Match is not null)
                    member.Connection.Send(new Refused("that match has already started"));
                else if (!target.Seats.Any(s => s.IsFree))
                    member.Connection.Send(new Refused("that room is full"));
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
                if (room.Members.Any(m => m != member && m.Colour == set.Colour))
                {
                    member.Connection.Send(new Refused($"{set.Colour} is taken"));
                    break;
                }
                member.Colour = set.Colour;
                member.Ready = set.Ready;
                member.Terrain = set.Terrain;
                Broadcast(room, room.State());
                break;

            case SetSeat seat when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    member.Connection.Send(new Refused("only the host changes the seats"));
                else if (seat.Seat >= 0 && seat.Seat < room.Seats.Count && room.Seats[seat.Seat].Player != member)
                {
                    room.Seats[seat.Seat].Use = seat.Use;
                    if (room.Seats[seat.Seat].Player is { } sitting)
                        TakeOut(sitting, room);
                    else
                        Broadcast(room, room.State());
                }
                break;

            case ChangeRoom change when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    member.Connection.Send(new Refused("only the host changes the room"));
                else if (change.Slots.Count < 2)
                    member.Connection.Send(new Refused("a room needs a map with at least two start positions"));
                else
                {
                    foreach (var removed in room.Change(change))
                        TakeOut(removed, room);
                    Broadcast(room, room.State());
                }
                break;

            case StartMatch when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    member.Connection.Send(new Refused("only the host starts the match"));
                else if (room.Members.Any(m => !m.Ready))
                    member.Connection.Send(new Refused("not everyone is ready"));
                else
                    Start(room);
                break;

            case RejoinMatch rejoin:
                Rejoin(member, rejoin);
                break;

            case Turn turn when member.Room is { Match: not null } room && member.Slot == turn.Race
                                && turn.Tick >= room.FirstTurn.GetValueOrDefault(turn.Race):
                room.LastTurn[turn.Race] = turn.Tick;
                room.Log.Add(turn);
                // Back to its sender too: a world runs a tick only once the relay has seen every turn for it, so the
                // tick a returning player takes over from is always still ahead of every world.
                Broadcast(room, turn);
                break;

            case ChecksumReport report when member.Room is { Match: not null } room && member.Slot == report.Race:
                Compare(room, report);
                break;

            // To the others only, as the original's broker sent it; one for a single player reaches only that race's.
            case Chat chat when member.Room is { } room && chat.Text.Trim() is { Length: > 0 } text:
            {
                var said = chat with { From = member.Name, Text = text.Length > Chat.MaxText ? text[..Chat.MaxText] : text };
                foreach (var other in room.Members.Where(m => m != member && (chat.To is null || m.Slot == chat.To)))
                    other.Connection.Send(said);
                break;
            }
        }
    }

    private void Join(Member member, Room room)
    {
        member.Room = room;
        member.Ready = false;
        member.Colour = Enum.GetValues<Race>().First(c => room.Members.All(m => m.Colour != c));
        room.Members.Add(member);
        room.Seats.First(s => s.IsFree).Player = member;
        Broadcast(room, room.State());
    }

    /// <summary>The host took a player out of the room: they are back in the lobby and told why.</summary>
    private void TakeOut(Member member, Room room)
    {
        Leave(member);
        member.Connection.Send(room.State());
        member.Connection.Send(new Refused("The host took you out of the room."));
    }

    private void Leave(Member member)
    {
        if (member.Room is not { } room)
            return;
        member.Room = null;
        room.Members.Remove(member);
        foreach (var seat in room.Seats.Where(s => s.Player == member))
            seat.Player = null;
        if (room.Members.Count == 0)
        {
            _rooms.Remove(room.Id);
            return;
        }
        if (room.Match is null)
        {
            if (room.HostId == member.Id)
                room.HostId = room.Members[0].Id;
            Broadcast(room, room.State());
        }
        else if (member.Slot is { } slot)
        {
            // Nobody waits for the leaver after its last turn; from the next tick on, the AI plays its race.
            var from = room.LastTurn.TryGetValue(slot, out var last) ? last + 1 : InputDelay;
            room.Left.Add(slot);
            var left = new PlayerLeft(slot, from);
            room.Log.Add(left);
            Broadcast(room, left);
        }
    }

    /// <summary>
    /// A player who dropped out of a running match takes their race back. The others are told the tick it
    /// changes hands: one after every turn forwarded so far, so no world can have passed it. The returning player
    /// gets the match and everything forwarded since its start, rebuilds the world from it and catches up; the
    /// others wait for its turns from that tick on.
    /// </summary>
    private void Rejoin(Member member, RejoinMatch rejoin)
    {
        if (!_rooms.TryGetValue(rejoin.RoomId, out var room) || room.Match is null)
        {
            member.Connection.Send(new Refused("that match is not running"));
            return;
        }
        var seat = room.Match.Players.FirstOrDefault(p => !p.Ai && room.Left.Contains(p.Slot) && p.Name == member.Name);
        if (seat is null)
        {
            member.Connection.Send(new Refused($"no player called {member.Name} left that match"));
            return;
        }
        Leave(member);

        var slot = seat.Slot;
        var leftAt = room.Log.OfType<PlayerLeft>().Last(l => l.Race == slot).FromTick;
        var from = Math.Max(leftAt, room.LastTurn.Values.DefaultIfEmpty(0).Max() + 1);
        var back = new PlayerReturned(slot, from);
        room.Log.Add(back);
        room.Left.Remove(slot);
        room.FirstTurn[slot] = from;
        room.LastTurn[slot] = from - 1;
        Broadcast(room, back);

        member.Room = room;
        member.Slot = slot;
        member.Colour = seat.Colour;
        member.Ready = true;
        member.Connection.Send(new MatchResumed(room.Match, slot, from));
        foreach (var message in room.Log)
            member.Connection.Send(message);
        room.Members.Add(member);
        Log?.Invoke($"{member.Name} rejoined room {room.Id} as {slot} from tick {from}");
    }

    private void Start(Room room)
    {
        var players = new List<MatchPlayer>();
        var slots = room.Setup.Slots.OrderBy(s => s).ToList();
        var seats = room.Seats.Zip(slots).ToList();
        foreach (var (seat, slot) in seats.Where(s => s.First.Player is not null))
        {
            var member = seat.Player!;
            member.Slot = slot;
            players.Add(new MatchPlayer(member.Id, member.Name, slot, member.Colour, Ai: false));
        }
        var colours = Enum.GetValues<Race>().Where(c => players.All(p => p.Colour != c)).ToList();
        foreach (var (_, slot) in seats.Where(s => s.First.Player is null && (s.First.Use == SeatUse.Computer || s.First.Use == SeatUse.Open && room.Setup.FillWithAi)))
        {
            var colour = colours.Contains(slot) ? slot : colours[0];
            colours.Remove(colour);
            players.Add(new MatchPlayer(0, "AI", slot, colour, Ai: true));
        }
        // The ground goes into the match only when every player has the same (MatchInfo.Terrain).
        var terrain = room.Members.Select(m => m.Terrain).Distinct().ToList() is [{ } same] ? same : (long?)null;
        room.Match = new MatchInfo(room.Setup.Scenario, room.Setup.Planet, room.Setup.Options, _random.Next(1, int.MaxValue), InputDelay, players, terrain);
        Broadcast(room, new MatchStarted(room.Match));
        Log?.Invoke($"room {room.Id} started {room.Setup.Scenario} with {room.Members.Count} players");
    }

    /// <summary>Once every remaining player reported a tick, they must all agree.</summary>
    private void Compare(Room room, ChecksumReport report)
    {
        // A returning player catching up reports ticks the others agreed on long ago.
        if (room.Agreed.TryGetValue(report.Tick, out var agreed))
        {
            if (agreed != report.Value)
                Desynced(room, report.Tick);
            return;
        }
        if (!room.Checksums.TryGetValue(report.Tick, out var reports))
            room.Checksums[report.Tick] = reports = [];
        reports[report.Race] = report.Value;
        var expected = room.Members.Where(m => m.Slot is not null && !room.Left.Contains(m.Slot.Value)).Select(m => m.Slot!.Value).ToList();
        if (expected.Any(race => !reports.ContainsKey(race)))
            return;
        room.Checksums.Remove(report.Tick);
        if (reports.Values.Distinct().Count() > 1)
            Desynced(room, report.Tick);
        else
            room.Agreed[report.Tick] = report.Value;
    }

    private void Desynced(Room room, long tick)
    {
        if (room.Desynced)
            return;
        room.Desynced = true;
        Broadcast(room, new Desync(tick));
        Log?.Invoke($"room {room.Id} desynced at tick {tick}");
    }

    private static void Broadcast(Room room, NetMessage message)
    {
        foreach (var member in room.Members)
            member.Connection.Send(message);
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Stop();
        await _accepting;
        Task[] running;
        lock (_lock)
            running = _tasks.ToArray();
        await Task.WhenAll(running);
    }

    private sealed class Member(int id, string name, Connection connection)
    {
        public int Id { get; } = id;
        public string Name { get; } = name;
        public Connection Connection { get; } = connection;
        public Room? Room { get; set; }
        public Race Colour { get; set; }
        public bool Ready { get; set; }
        public long? Terrain { get; set; }
        public Race? Slot { get; set; }
    }

    /// <summary>A start position of a room, in the order of the map's races.</summary>
    private sealed class Seat
    {
        public Member? Player { get; set; }
        public SeatUse Use { get; set; }
        public bool IsFree => Player is null && Use == SeatUse.Open;
    }

    private sealed class Room(int id, CreateRoom setup, int hostId)
    {
        public int Id { get; } = id;
        public CreateRoom Setup { get; private set; } = setup;
        public int HostId { get; set; } = hostId;
        public List<Member> Members { get; } = [];
        public List<Seat> Seats { get; private set; } = setup.Slots.Select(_ => new Seat()).ToList();
        public MatchInfo? Match { get; set; }
        public Dictionary<Race, long> LastTurn { get; } = [];
        public HashSet<Race> Left { get; } = [];
        public Dictionary<long, Dictionary<Race, ulong>> Checksums { get; } = [];
        public bool Desynced { get; set; }

        /// <summary>Checksums every player reported alike, by tick.</summary>
        public Dictionary<long, ulong> Agreed { get; } = [];

        /// <summary>Every turn, departure and return forwarded since the match started, for a returning player.</summary>
        public List<NetMessage> Log { get; } = [];

        /// <summary>The first tick a returning player's turns count from.</summary>
        public Dictionary<Race, long> FirstTurn { get; } = [];

        public RoomSummary Summary() => new(Id, Setup.Name, Setup.Scenario, Setup.Planet, Members.Count,
            Seats.Count(s => s.Player is not null || s.Use == SeatUse.Open), Match is not null);

        public RoomState State() => new(Id, Setup.Name, Setup.Scenario, Setup.Planet, Setup.Slots, Setup.Options, Setup.FillWithAi, HostId,
            Members.Select(m => new LobbyPlayer(m.Id, m.Name, m.Colour, m.Ready, m.Terrain)).ToList(),
            Seats.Select(s => new RoomSeat(s.Player?.Id, s.Use)).ToList());

        /// <summary>The room on a new map, its players at the new seats, the host first; returns those it has no seat for.</summary>
        public List<Member> Change(ChangeRoom change)
        {
            Setup = Setup with { Scenario = change.Scenario, Planet = change.Planet, Slots = change.Slots, Options = change.Options, FillWithAi = change.FillWithAi };
            var people = Seats.Where(s => s.Player is not null).Select(s => s.Player!).OrderByDescending(m => m.Id == HostId).ToList();
            var uses = Seats.Where(s => s.Player is null).Select(s => s.Use).ToList();
            Seats = change.Slots.Select((_, i) => i < people.Count
                ? new Seat { Player = people[i] }
                : new Seat { Use = i - people.Count < uses.Count ? uses[i - people.Count] : SeatUse.Open }).ToList();
            foreach (var member in Members)
                (member.Ready, member.Terrain) = (false, null);
            return people.Skip(change.Slots.Count).ToList();
        }
    }
}
