using System.Runtime.InteropServices;
using Machines.Relay;

// The Machines Remake multiplayer relay:
//   dotnet run --project server/Machines.Relay -- [--port 7777] [--http-port 8080]
// TCP on --port (default 7777) for the LAN and direct addresses; HTTP with /health and WebSockets on /ws on the port of
// the PORT environment variable (Cloud Run sets it) or --http-port, none without either.
var port = 7777;
int? httpPort = int.TryParse(Environment.GetEnvironmentVariable("PORT"), out var fromHost) ? fromHost : null;
for (var i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--port" && int.TryParse(args[i + 1], out var p))
        port = p;
    else if (args[i] == "--http-port" && int.TryParse(args[i + 1], out var h))
        httpPort = h;
}

static void Say(string line) => Console.WriteLine($"{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} {line}");

await using var relay = RelayServer.Start(port);
relay.Log += Say;
RelayHttp? http = null;
if (httpPort is { } web)
    http = await RelayHttp.StartAsync(relay, web);
Say($"Machines relay {RelayServer.Version} listening on TCP port {relay.Port}" +
    (http is null ? "" : $", HTTP port {http.Port} (/health, WebSockets on /ws)") + "; Ctrl+C stops it.");

var stopped = new TaskCompletionSource();
Console.CancelKeyPress += (_, e) =>
{
    e.Cancel = true;
    stopped.TrySetResult();
};
// Cloud Run and Docker stop a container with SIGTERM.
PosixSignalRegistration? terminate = null;
try
{
    terminate = PosixSignalRegistration.Create(PosixSignal.SIGTERM, context =>
    {
        context.Cancel = true;
        stopped.TrySetResult();
    });
}
catch (PlatformNotSupportedException)
{
}
http?.Stopping.Register(() => stopped.TrySetResult());
await stopped.Task;
Say("Machines relay stopping.");
terminate?.Dispose();
if (http is not null)
    await http.DisposeAsync();
