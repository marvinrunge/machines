using System.Net;
using Machines.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Machines.Relay;

/// <summary>
/// The relay's web side, for hosts that speak only HTTP such as Cloud Run: <c>GET /health</c> says the relay is up, its
/// version and how many rooms and players it has; <c>/ws</c> takes WebSockets into the same rooms as the TCP listener
/// (<see cref="RelayServer.Accept"/>). Not used by the relay inside the game, which needs no web server.
/// </summary>
public sealed class RelayHttp : IAsyncDisposable
{
    private readonly WebApplication _app;

    private RelayHttp(WebApplication app, int port) => (_app, Port) = (app, port);

    /// <summary>The port it listens on.</summary>
    public int Port { get; }

    /// <summary>Completes when the host asks the relay to stop (Ctrl+C, or Cloud Run's SIGTERM).</summary>
    public CancellationToken Stopping => _app.Lifetime.ApplicationStopping;

    /// <param name="port">0 picks a free port (tests).</param>
    /// <param name="address">Where to listen; without one, on every address.</param>
    public static async Task<RelayHttp> StartAsync(RelayServer relay, int port, IPAddress? address = null)
    {
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [] });
        // The relay writes its own lines; of the web server's, only what goes wrong.
        builder.Logging.ClearProviders();
        builder.Logging.AddSimpleConsole(options => options.SingleLine = true);
        builder.Logging.SetMinimumLevel(LogLevel.Warning);
        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            if (address is null)
                kestrel.ListenAnyIP(port);
            else
                kestrel.Listen(address, port);
        });
        var app = builder.Build();
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(15) });

        app.MapGet("/", () => Results.Text($"Machines relay {RelayServer.Version}: games connect to {RelayAddress.WebSocketPath}, /health tells how it is.\n"));
        app.MapGet("/health", () =>
        {
            var (rooms, players) = relay.Count();
            return Results.Json(new RelayHealth("ok", RelayServer.Version, NetJson.Protocol, rooms, players));
        });
        app.Map(RelayAddress.WebSocketPath, async context =>
        {
            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                await context.Response.WriteAsync("Machines relay: WebSocket only here.\n");
                return;
            }
            var socket = await context.WebSockets.AcceptWebSocketAsync();
            // The request lasts as long as the socket: Kestrel closes it once this returns.
            await relay.Accept(new WebSocketLink(socket));
        });

        await app.StartAsync();
        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
            .Select(a => new Uri(a.Replace("://+", "://localhost").Replace("://*", "://localhost")).Port).FirstOrDefault() ?? port;
        return new RelayHttp(app, bound);
    }

    public async ValueTask DisposeAsync()
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await _app.StopAsync(timeout.Token);
        await _app.DisposeAsync();
    }
}

/// <summary>What /health answers.</summary>
public sealed record RelayHealth(string Status, string Version, int Protocol, int Rooms, int Players);
