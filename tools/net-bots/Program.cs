using System.Globalization;
using Machines.Net;
using Machines.Sim;
using Machines.Sim.Survival;
using Machines.Tests.Net;

// Net bots: people's clients of a relay without the game, to fill seats of a room the game hosts (P2 design §9, S6).
//   dotnet run --project tools/net-bots -- --relay 127.0.0.1:7777 [--count 5] [--seats 2,3,4,6,7] [--name Bot] [--room <id>]
//                                          [--data data/original] [--out <folder>] [--ticks <n>]
// Each bot says hello under "<name> <n>", joins the room (the one named, else the first that has not started), takes the
// seat named for it, brings a hero of the room's tier and is ready; once the host starts, it plays the match as the game
// does (a world and a session on the relay's bundles, both of the pacer's rules on a thread of its own), its hero driven by
// a sim battle bot through its turns. It sends no ground fingerprint, so a room with bots plays without the classic ground.
// It writes the game's log lines (checksums, pace every 100 ticks, catch-ups, rebuilds, comings and goings) to <out>/<name>.log
// (else to the console), and at the end its recording to <out>/<name>.mreplay, which it plays back in turn mode against its
// own checksums. It ends at --ticks, once the match is decided and its last bundle ran, or when the relay takes it out.
//   dotnet run --project tools/net-bots -- compare <relay log> [--room <id>] [--from <tick>] [--at-speed <log>,...] <log>...
// compares the logs' checksum lines and sets their pace lines against the relay's clock (Compare.cs).

if (args.Length > 0 && args[0] == "compare")
    return Compare.Run(args[1..]);

var options = new Dictionary<string, string>();
for (var i = 0; i + 1 < args.Length; i += 2)
    options[args[i].TrimStart('-')] = args[i + 1];
string? Option(string key) => options.GetValueOrDefault(key);
if (Option("relay") is not { } relay)
{
    Console.Error.WriteLine("net-bots --relay <host:port | ws://host:port> [--count n] [--seats a,b,..] [--name Bot] [--room id] [--data folder] [--out folder] [--ticks n]");
    return 2;
}
var count = int.Parse(Option("count") ?? "1", CultureInfo.InvariantCulture);
var seats = (Option("seats") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(s => int.Parse(s, CultureInfo.InvariantCulture)).ToList();
var prefix = Option("name") ?? "Bot";
int? roomAsked = Option("room") is { } r ? int.Parse(r, CultureInfo.InvariantCulture) : null;
long? ticks = Option("ticks") is { } t ? long.Parse(t, CultureInfo.InvariantCulture) : null;
var outFolder = Option("out");
if (outFolder is not null)
    Directory.CreateDirectory(outFolder);
var data = GameData.Load(Option("data") ?? DataFolder());

using var stop = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stop.Cancel();
};
using var timer = new FineTimer();
var bots = new List<(RelayClient Client, Action<string> Log)>();
for (var i = 0; i < count; i++)
{
    var name = $"{prefix} {i + 1}";
    var client = await RelayClient.Connect(relay, name, data);
    bots.Add((client, Writer(name)));
    bots[^1].Log($"net-bots: {name} connected to {relay} as client {client.Id}");
}

// The room: the one asked for, else the first that has not started.
var host = bots[0].Client;
RoomSummary? found = null;
while (found is null && !stop.IsCancellationRequested)
{
    host.Connection.Send(new ListRooms());
    if (await Read<RoomList>(host, TimeSpan.FromSeconds(2)) is { } list)
        found = list.Rooms.FirstOrDefault(room => roomAsked is { } id ? room.Id == id : !room.Started);
    if (found is null)
        await Task.Delay(500);
}
if (found is null)
    return 1;

// Each bot joins, takes its seat, brings a hero of the room's tier and is ready.
var states = new System.Collections.Concurrent.ConcurrentDictionary<RelayClient, RoomState>();
for (var i = 0; i < bots.Count; i++)
{
    var (client, log) = bots[i];
    client.Connection.Send(new JoinRoom(found.Id));
    var state = await client.Next<RoomState>(s => s.Players.Any(p => p.ClientId == client.Id));
    if (i < seats.Count)
    {
        client.Connection.Send(new TakeSeat(seats[i]));
        state = await client.Next<RoomState>(s => s.Seats.ElementAtOrDefault(seats[i])?.ClientId == client.Id);
    }
    states[client] = state;
    Ready(client, state, i);
    log($"net-bots: {client.Name} in room {state.Id} at seat {state.Seats.ToList().FindIndex(s => s.ClientId == client.Id)}, ready");
}

// Until the host starts: a bot whose readiness the room took back (the host changed the room) says it again.
var waits = bots.Select(async (bot, i) =>
{
    var client = bot.Client;
    while (true)
    {
        var message = await client.Connection.Inbox.ReadAsync(stop.Token);
        switch (message)
        {
            case RoomState state when state.Players.SingleOrDefault(p => p.ClientId == client.Id) is { Ready: false }:
                states[client] = state;
                Ready(client, state, i);
                break;
            case RoomState state:
                states[client] = state;
                break;
            case Refused refused:
                bot.Log($"net-bots: refused: {refused.Reason}");
                break;
            case MatchStarted started:
                client.RoomId = states[client].Id;
                client.Log = bot.Log;
                client.Begin(started.Match);
                client.Script = new BattleBotScript(client.Me);
                client.SaysDecided = true;
                bot.Log($"net-bots: the match began: player {client.Me}, {client.Race}, delay {started.Match.InputDelay}");
                client.Run();
                return;
        }
    }
}).ToList();
await Task.WhenAll(waits);

// Until each is done: at the tick asked for, out of the match, its line gone, or the match decided and its last bundle run.
var lastBundle = bots.ToDictionary(b => b.Client, _ => (Head: -1L, At: DateTime.UtcNow));
bool Done(RelayClient client)
{
    if (!client.InMatch || client.Connection.Closed || ticks is { } most && client.Tick >= most)
        return true;
    var head = client.Session.NewestBundle;
    if (head != lastBundle[client].Head)
        lastBundle[client] = (head, DateTime.UtcNow);
    return client.Ended is not null && client.Tick > head && DateTime.UtcNow - lastBundle[client].At > TimeSpan.FromSeconds(10);
}
while (!stop.IsCancellationRequested && !bots.All(b => Done(b.Client)))
    await Task.Delay(250);

var failed = 0;
foreach (var (client, log) in bots)
{
    client.Halt();
    log($"net-bots: stopped at tick {client.Tick}{(client.Out is { } left ? $", taken out: {left.Reason}" : "")}; {client.Received / 1024.0:F0} KiB of bundles taken");
    if (client.Failure is not null)
    {
        log($"net-bots: failed: {client.Failure}");
        failed++;
    }
    if (client.Session is null)
        continue;
    // The recording, as the game keeps it, played back in turn mode against the bot's own checksums.
    var recording = new Replay(client.Match, client.Session.Played, client.World.Tick, client.Session.Presence);
    if (outFolder is not null)
        recording.Save(Path.Combine(outFolder, $"{client.Name}{Replay.Extension}"));
    var playback = recording.Playback(Replay.BuildWorld(data, recording.Match));
    var (equal, differ) = (0, new List<long>());
    while (playback.World.Tick < recording.Ticks && playback.TryStep())
    {
        if (client.Sums.TryGetValue(playback.World.Tick, out var sum))
        {
            if (sum == playback.World.Checksum())
                equal++;
            else
                differ.Add(playback.World.Tick);
        }
    }
    var same = playback.World.Tick == recording.Ticks && playback.World.Checksum() == client.World.Checksum();
    log($"replay: {equal} checksums equal, {differ.Count} differ{(differ.Count > 0 ? $" (first at tick {differ[0]})" : "")}; " +
        $"to tick {playback.World.Tick} of {recording.Ticks}, final checksum {(same ? "equal" : "differs")}");
    if (differ.Count > 0 || !same)
        failed++;
}
foreach (var (client, _) in bots)
    await client.Leave();
return failed == 0 ? 0 : 1;

// A bot's colour as the room shows it, a hero of the room's tier for its seat's race, ready, no ground.
static void Ready(RelayClient client, RoomState state, int index)
{
    var me = state.Players.Single(p => p.ClientId == client.Id);
    var race = state.Seats.FirstOrDefault(s => s.ClientId == client.Id)?.Race ?? me.Colour;
    var hero = state.Options.Battle is { } battle
        ? BattleBots.Bot(state.Scenario, battle.Tier, race, index) with { Name = client.Name, Bot = false }
        : null;
    client.Connection.Send(new SetPlayer(me.Colour, Ready: true, Terrain: null, hero));
}

// The next message of a kind within a time, or null; the others are dropped (before the match).
static async Task<T?> Read<T>(RelayClient client, TimeSpan within) where T : NetMessage
{
    using var timeout = new CancellationTokenSource(within);
    try
    {
        while (true)
        {
            if (await client.Connection.Inbox.ReadAsync(timeout.Token) is T typed)
                return typed;
        }
    }
    catch (OperationCanceledException)
    {
        return null;
    }
}

// A bot's log: <out>/<name>.log with the time of day, else the console under its name.
Action<string> Writer(string name)
{
    if (outFolder is null)
        return line => Console.WriteLine($"{DateTime.Now:HH:mm:ss.fff} [{name}] {line}");
    var writer = new StreamWriter(Path.Combine(outFolder, $"{name}.log"), append: false) { AutoFlush = true };
    var gate = new object();
    return line =>
    {
        lock (gate)
            writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
    };
}

// data/original of the repository this runs in.
static string DataFolder()
{
    foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
    {
        for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
        {
            var candidate = Path.Combine(dir.FullName, "data", "original");
            if (Directory.Exists(candidate))
                return candidate;
        }
    }
    throw new DirectoryNotFoundException("data/original not found; give --data");
}
