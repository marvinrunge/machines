using System.Net;
using System.Net.Sockets;
using Machines.Import.Model;
using Machines.Net;
using Machines.Sim.Survival;
using Machines.Sim.World;

namespace Machines.Relay;

/// <summary>
/// The multiplayer relay: players meet in rooms, the host starts the match, and from then on the relay forwards
/// every player's turns to the others and compares their checksums. It never runs the simulation; every client
/// does (deterministic lockstep). One lock guards all room state; messages are tiny.
/// </summary>
/// <remarks>
/// Players come over TCP (the listener here: the LAN, a host's address, the relay inside the game) or over WebSocket
/// (<see cref="RelayHttp"/>: the public relay on Cloud Run); both feed the same rooms through <see cref="Accept"/>. A
/// client that asks for it gets a session to come back with over a new socket when its line drops or its host ends the
/// socket (see <see cref="Connection"/>): its seat waits for it, and the match never notices.
/// </remarks>
public sealed class RelayServer : IAsyncDisposable
{
    /// <summary>Ticks between a command and every world carrying it out: 200 ms at 20 ticks a second.</summary>
    public const int InputDelay = 4;

    /// <summary>The relay's version, told to every client in its <see cref="Welcome"/> and shown by /health. Raise it when the relay changes.</summary>
    public const string Version = "1.1";

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
    private int _nextClient = 1, _nextRoom = 1;
    private Task _accepting = Task.CompletedTask;

    private RelayServer(TcpListener listener, LinkTimings timings) => (_listener, _timings) = (listener, timings);

    public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

    /// <param name="port">0 picks a free port (tests).</param>
    /// <param name="address">Where to listen; without one, on every address, IPv6 as well as IPv4 where the computer has it.</param>
    /// <param name="timings">How long a dropped player's seat waits, and the other clocks of a resumable connection (tests shorten them).</param>
    public static RelayServer Start(int port, IPAddress? address = null, LinkTimings? timings = null)
    {
        var listener = new TcpListener(address ?? (Socket.OSSupportsIPv6 ? IPAddress.IPv6Any : IPAddress.Any), port);
        if (address is null && Socket.OSSupportsIPv6)
            listener.Server.DualMode = true;
        listener.Start();
        var server = new RelayServer(listener, timings ?? LinkTimings.Default);
        server._accepting = server.AcceptLoop();
        return server;
    }

    public event Action<string>? Log;

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
                            member.Session = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(16));
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
                member.Connection.Send(new RoomList(_rooms.Values.Select(r => r.Summary()).ToList()));
                // Who comes back to the room from setting it up finds it as it is.
                if (member.Room is { Match: null } waiting)
                    member.Connection.Send(waiting.State());
                break;

            case CreateRoom create:
            {
                if (WhyNotRoom(create.Scenario, create.Slots, create.Options, create.SeatsPerSlot) is { } why)
                {
                    member.Connection.Send(new Refused(why));
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
                // The colour is the race's: no other race may show it, and every teammate shows it too.
                if (room.Members.Any(m => m.Race != member.Race && m.Colour == set.Colour))
                {
                    member.Connection.Send(new Refused($"{set.Colour} is taken"));
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
                        member.Connection.Send(new Refused(why));
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
                    member.Connection.Send(new Refused("only the host changes the seats"));
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
                    member.Connection.Send(new Refused("no such seat"));
                else if (room.Seats[take.Seat].Member != member)
                {
                    if (!room.Seats[take.Seat].IsFree)
                        member.Connection.Send(new Refused("that seat is not free"));
                    else
                    {
                        Sit(room, member, room.Seats[take.Seat], keepColour: true);
                        Broadcast(room, room.State());
                    }
                }
                break;

            case ChangeRoom change when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    member.Connection.Send(new Refused("only the host changes the room"));
                else if (WhyNotRoom(change.Scenario, change.Slots, change.Options, change.SeatsPerSlot) is { } why)
                    member.Connection.Send(new Refused(why));
                else
                {
                    foreach (var removed in room.Change(AsBattle(change)))
                        TakeOut(removed, room);
                    Broadcast(room, room.State());
                }
                break;

            case StartMatch when member.Room is { Match: null } room:
                if (room.HostId != member.Id)
                    member.Connection.Send(new Refused("only the host starts the match"));
                else if (room.Members.Any(m => !m.Ready))
                    member.Connection.Send(new Refused("not everyone is ready"));
                else if (room.Setup.Options.Battle is { } fight
                         && room.Members.Select(m => m.Hero is { } hero ? WhyNotHero(hero, fight.Tier) : "not everyone has chosen a hero")
                             .FirstOrDefault(why => why is not null) is { } unfit)
                    member.Connection.Send(new Refused(unfit));
                else
                    Start(room);
                break;

            case RejoinMatch rejoin:
                Rejoin(member, rejoin);
                break;

            // A player's own turns only, and a returning player's only from the tick they play again.
            case Turn turn when member.Room is { Match: not null } room && member.Player is { } player && player == turn.Player
                                && turn.Tick >= room.FirstTurn.GetValueOrDefault(player):
                room.LastTurn[player] = turn.Tick;
                room.Log.Add(turn);
                // Back to its sender too: a world runs a tick only once the relay has seen every turn for it, so the
                // tick a returning player takes over from is always still ahead of every world.
                Broadcast(room, turn);
                break;

            case ChecksumReport report when member.Room is { Match: not null } room && member.Player == report.Player:
                Compare(room, report);
                break;

            // To the others only, as the original's broker sent it; one for a single race reaches every player of that race.
            case Chat chat when member.Room is { } room && chat.Text.Trim() is { Length: > 0 } text:
            {
                var said = chat with { From = member.RoomName, Text = text.Length > Chat.MaxText ? text[..Chat.MaxText] : text };
                foreach (var other in room.Members.Where(m => m != member && (chat.To is null || m.Race == chat.To)))
                    other.Connection.Send(said);
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
        member.Connection.Send(room.State());
        member.Connection.Send(new Refused("The host took you out of the room."));
    }

    private void Leave(Member member)
    {
        if (member.Room is not { } room)
            return;
        var player = member.Player;
        member.Room = null;
        (member.Race, member.Player, member.RoomName) = (null, null, member.Name);
        room.Members.Remove(member);
        foreach (var seat in room.Seats.Where(s => s.Member == member))
            seat.Member = null;
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
        else if (player is { } leaver)
        {
            // Nobody waits for the leaver after their last turn; from the next tick on, the computer stands in for them.
            var from = room.LastTurn.TryGetValue(leaver, out var last) ? last + 1 : InputDelay;
            room.Left.Add(leaver);
            var left = new PlayerLeft(leaver, from);
            room.Log.Add(left);
            Broadcast(room, left);
        }
    }

    /// <summary>
    /// A player who dropped out of a running match plays again. The relay knows them by the name they connected with: it
    /// takes the left person of the match who connected under that name, the one <see cref="RejoinMatch.Player"/> names when
    /// it names one, else the lowest numbered. The others are told the tick it changes hands: one after every turn forwarded
    /// so far, so no world can have passed it. The returning player gets the match and everything forwarded since its start,
    /// rebuilds the world from it and catches up; the others wait for their turns from that tick on.
    /// </summary>
    private void Rejoin(Member member, RejoinMatch rejoin)
    {
        if (!_rooms.TryGetValue(rejoin.RoomId, out var room) || room.Match is null)
        {
            member.Connection.Send(new Refused("that match is not running"));
            return;
        }
        var seat = room.Match.Players
            .Where(p => !p.Ai && p.Player is { } id && room.Left.Contains(id) && room.HelloNames.GetValueOrDefault(id) == member.Name
                        && (rejoin.Player is null || rejoin.Player == id))
            .MinBy(p => p.Player!.Value);
        if (seat is null)
        {
            member.Connection.Send(new Refused($"no player called {member.Name} left that match"));
            return;
        }
        Leave(member);

        var player = seat.Player!.Value;
        var leftAt = room.Log.OfType<PlayerLeft>().Last(l => l.Player == player).FromTick;
        var from = Math.Max(leftAt, room.LastTurn.Values.DefaultIfEmpty(0).Max() + 1);
        var back = new PlayerReturned(player, from);
        room.Log.Add(back);
        room.Left.Remove(player);
        room.FirstTurn[player] = from;
        room.LastTurn[player] = from - 1;
        Broadcast(room, back);

        member.Room = room;
        (member.Race, member.Player, member.RoomName) = (seat.Slot, player, seat.Name);
        member.Colour = seat.Colour;
        member.Ready = true;
        member.Connection.Send(new MatchResumed(room.Match, player, from));
        foreach (var message in room.Log)
            member.Connection.Send(message);
        room.Members.Add(member);
        Log?.Invoke($"{member.Name} rejoined room {room.Id} as player {player} ({seat.Slot}) from tick {from}");
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
        room.Match = new MatchInfo(room.Setup.Scenario, room.Setup.Planet, room.Setup.Options, _random.Next(1, int.MaxValue), InputDelay, rows, terrain);
        Broadcast(room, new MatchStarted(room.Match));
        Log?.Invoke($"room {room.Id} started {room.Setup.Scenario} with {room.Members.Count} players");
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
        room.Match = new MatchInfo(room.Setup.Scenario, room.Setup.Planet, options, _random.Next(1, int.MaxValue), InputDelay, rows, terrain);
        Broadcast(room, new MatchStarted(room.Match));
        Log?.Invoke($"room {room.Id} started a battle on {room.Setup.Scenario} at tier {battle.Tier} with {room.Members.Count} players");
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

    /// <summary>Once every person still playing reported a tick, they must all agree: teammates as well as sides.</summary>
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
        reports[report.Player] = report.Value;
        var expected = room.Members.Select(m => m.Player).OfType<PlayerId>().Where(player => !room.Left.Contains(player)).ToList();
        if (expected.Any(player => !reports.ContainsKey(player)))
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

        /// <summary>The player they are in the room's match, from its start; null before and outside one.</summary>
        public PlayerId? Player { get; set; }

        /// <summary>In a battle room, the hero the player brings; null until they chose one, and in every other room.</summary>
        public BattleHero? Hero { get; set; }
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

    private sealed class Room(int id, CreateRoom setup, int hostId)
    {
        public int Id { get; } = id;
        public CreateRoom Setup { get; private set; } = setup;
        public int HostId { get; set; } = hostId;
        public List<Member> Members { get; } = [];
        public List<Seat> Seats { get; private set; } = SeatPlan(setup);
        public MatchInfo? Match { get; set; }
        public Dictionary<PlayerId, long> LastTurn { get; } = [];
        public HashSet<PlayerId> Left { get; } = [];
        public Dictionary<long, Dictionary<PlayerId, ulong>> Checksums { get; } = [];
        public bool Desynced { get; set; }

        /// <summary>Checksums every player reported alike, by tick.</summary>
        public Dictionary<long, ulong> Agreed { get; } = [];

        /// <summary>Every turn, departure and return forwarded since the match started, for a returning player.</summary>
        public List<NetMessage> Log { get; } = [];

        /// <summary>The first tick a returning player's turns count from.</summary>
        public Dictionary<PlayerId, long> FirstTurn { get; } = [];

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
            Setup.Options.Battle is null ? null : RoomSummary.Battle, Setup.Options.Battle?.Tier ?? 0);

        public RoomState State() => new(Id, Setup.Name, Setup.Scenario, Setup.Planet, Setup.Slots, Setup.Options, Setup.FillWithAi, HostId,
            Members.Select(m => new LobbyPlayer(m.Id, m.RoomName, m.Colour, m.Ready, m.Terrain, m.Hero)).ToList(),
            Seats.Select(s => new RoomSeat(s.Member?.Id, s.Use, s.Race)).ToList(), Setup.SeatsPerSlot);

        /// <summary>
        /// The room on a new map or with new seats, its players at the new seats in the fill order, the host first, each in
        /// the colour of their new race as <see cref="ColourFor"/> gives it; returns those it has no seat for. The seats nobody
        /// sits at keep what they were for, in order. While the seats stay the same (only a setting changed), everyone stays
        /// where they sat and every seat nobody sits at stays what it was for. The heroes players brought stay only while the
        /// room stays a battle on the same map and bracket; a battle has no closed seats.
        /// </summary>
        public List<Member> Change(ChangeRoom change)
        {
            var heroesStay = Setup.Options.Battle is { } was && change.Options.Battle is { } now && was.Tier == now.Tier && Setup.Scenario == change.Scenario;
            var people = Seats.Where(s => s.Member is not null).Select(s => s.Member!).OrderByDescending(m => m.Id == HostId).ToList();
            var uses = Seats.Where(s => s.Member is null).Select(s => s.Use).ToList();
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
                foreach (var (seat, use) in Seats.Where(s => s.Member is null).Select((seat, i) => (seat, i < uses.Count ? uses[i] : SeatUse.Open)))
                    seat.Use = use;
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
