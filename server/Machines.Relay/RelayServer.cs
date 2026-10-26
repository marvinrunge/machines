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
    public static RelayServer Start(int port, IPAddress? address = null)
    {
        var listener = new TcpListener(address ?? IPAddress.Any, port);
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
                else if (target.Members.Count >= target.Setup.Slots.Count)
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
                Broadcast(room, room.State());
                break;

            case StartMatch when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    member.Connection.Send(new Refused("only the host starts the match"));
                else if (room.Members.Any(m => !m.Ready))
                    member.Connection.Send(new Refused("not everyone is ready"));
                else
                    Start(room);
                break;

            case Turn turn when member.Room is { Match: not null } room && member.Slot == turn.Race:
                room.LastTurn[turn.Race] = turn.Tick;
                foreach (var other in room.Members.Where(m => m != member))
                    other.Connection.Send(turn);
                break;

            case ChecksumReport report when member.Room is { Match: not null } room && member.Slot == report.Race:
                Compare(room, report);
                break;

            case Chat chat when member.Room is { } room:
                Broadcast(room, chat with { From = member.Name });
                break;
        }
    }

    private void Join(Member member, Room room)
    {
        member.Room = room;
        member.Ready = false;
        member.Colour = Enum.GetValues<Race>().First(c => room.Members.All(m => m.Colour != c));
        room.Members.Add(member);
        Broadcast(room, room.State());
    }

    private void Leave(Member member)
    {
        if (member.Room is not { } room)
            return;
        member.Room = null;
        room.Members.Remove(member);
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
            // Nobody waits for the leaver after its last turn; its machines stay where they are.
            var from = room.LastTurn.TryGetValue(slot, out var last) ? last + 1 : InputDelay;
            room.Left.Add(slot);
            Broadcast(room, new PlayerLeft(slot, from));
        }
    }

    private void Start(Room room)
    {
        var players = new List<MatchPlayer>();
        var slots = room.Setup.Slots.OrderBy(s => s).ToList();
        for (var i = 0; i < room.Members.Count; i++)
        {
            var member = room.Members[i];
            member.Slot = slots[i];
            players.Add(new MatchPlayer(member.Id, member.Name, slots[i], member.Colour, Ai: false));
        }
        if (room.Setup.FillWithAi)
        {
            var colours = Enum.GetValues<Race>().Where(c => players.All(p => p.Colour != c)).ToList();
            foreach (var slot in slots.Skip(room.Members.Count))
            {
                var colour = colours.Contains(slot) ? slot : colours[0];
                colours.Remove(colour);
                players.Add(new MatchPlayer(0, "AI", slot, colour, Ai: true));
            }
        }
        room.Match = new MatchInfo(room.Setup.Scenario, room.Setup.Planet, room.Setup.Options, _random.Next(1, int.MaxValue), InputDelay, players);
        Broadcast(room, new MatchStarted(room.Match));
        Log?.Invoke($"room {room.Id} started {room.Setup.Scenario} with {room.Members.Count} players");
    }

    /// <summary>Once every remaining player reported a tick, they must all agree.</summary>
    private void Compare(Room room, ChecksumReport report)
    {
        if (!room.Checksums.TryGetValue(report.Tick, out var reports))
            room.Checksums[report.Tick] = reports = [];
        reports[report.Race] = report.Value;
        var expected = room.Members.Where(m => m.Slot is not null && !room.Left.Contains(m.Slot.Value)).Select(m => m.Slot!.Value).ToList();
        if (expected.Any(race => !reports.ContainsKey(race)))
            return;
        room.Checksums.Remove(report.Tick);
        if (reports.Values.Distinct().Count() > 1 && !room.Desynced)
        {
            room.Desynced = true;
            Broadcast(room, new Desync(report.Tick));
            Log?.Invoke($"room {room.Id} desynced at tick {report.Tick}");
        }
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
        public Race? Slot { get; set; }
    }

    private sealed class Room(int id, CreateRoom setup, int hostId)
    {
        public int Id { get; } = id;
        public CreateRoom Setup { get; } = setup;
        public int HostId { get; set; } = hostId;
        public List<Member> Members { get; } = [];
        public MatchInfo? Match { get; set; }
        public Dictionary<Race, long> LastTurn { get; } = [];
        public HashSet<Race> Left { get; } = [];
        public Dictionary<long, Dictionary<Race, ulong>> Checksums { get; } = [];
        public bool Desynced { get; set; }

        public RoomSummary Summary() => new(Id, Setup.Name, Setup.Scenario, Setup.Planet, Members.Count, Setup.Slots.Count, Match is not null);

        public RoomState State() => new(Id, Setup.Name, Setup.Scenario, Setup.Planet, Setup.Slots, Setup.Options, Setup.FillWithAi, HostId,
            Members.Select(m => new LobbyPlayer(m.Id, m.Name, m.Colour, m.Ready)).ToList());
    }
}
