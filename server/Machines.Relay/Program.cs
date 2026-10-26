using Machines.Relay;

// The Machines Remake multiplayer relay: dotnet run --project server/Machines.Relay -- [--port 7777]
var port = 7777;
for (var i = 0; i < args.Length - 1; i++)
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
        port = p;

await using var relay = RelayServer.Start(port);
relay.Log += line => Console.WriteLine($"{DateTime.Now:HH:mm:ss} {line}");
Console.WriteLine($"Machines relay listening on port {relay.Port}; Ctrl+C stops it.");

var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.TrySetResult();
};
await stopped.Task;
