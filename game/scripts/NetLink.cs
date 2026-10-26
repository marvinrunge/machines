using Machines.Net;

namespace Machines.Game;

/// <summary>The connection to the multiplayer relay; it lives across scenes, from the lobby into the match.</summary>
public static class NetLink
{
    public static Connection? Connection { get; private set; }
    public static int ClientId { get; private set; }
    public static string Name { get; private set; } = "Player";

    public static bool Connected => Connection is { Closed: false };

    /// <summary>The relay this game hosts for the others, if it hosts one.</summary>
    public static Machines.Relay.RelayServer? HostedRelay { get; private set; }

    /// <summary>
    /// Runs the relay inside this game on <paramref name="port"/> and connects to it; the other players connect
    /// to this computer's address. Returns why it failed, or null.
    /// </summary>
    public static async Task<string?> HostAsync(int port, string name)
    {
        if (HostedRelay is null)
        {
            try
            {
                HostedRelay = Machines.Relay.RelayServer.Start(port);
            }
            catch (System.Net.Sockets.SocketException exception)
            {
                return $"cannot open port {port}: {exception.Message}";
            }
        }
        return await ConnectAsync($"127.0.0.1:{HostedRelay.Port}", name);
    }

    /// <summary>Connects and introduces the player; returns why it failed, or null.</summary>
    public static async Task<string?> ConnectAsync(string address, string name)
    {
        await DisconnectAsync();
        var (host, port) = Split(address);
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            var connection = await Connection.ConnectAsync(host, port, timeout.Token);
            connection.Send(new Hello(name, NetJson.Protocol));
            var reply = await connection.Inbox.ReadAsync(timeout.Token);
            if (reply is not Welcome welcome)
            {
                await connection.DisposeAsync();
                return reply is Refused refused ? refused.Reason : "the relay did not answer";
            }
            Connection = connection;
            ClientId = welcome.ClientId;
            Name = name;
            return null;
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or OperationCanceledException or IOException)
        {
            return $"cannot reach {host}:{port} ({exception.Message})";
        }
    }

    public static async Task DisconnectAsync()
    {
        if (Connection is { } connection)
        {
            Connection = null;
            await connection.DisposeAsync();
        }
    }

    public static void Send(NetMessage message) => Connection?.Send(message);

    /// <summary>"host:port", the port defaulting to the relay's 7777.</summary>
    private static (string Host, int Port) Split(string address)
    {
        var text = address.Trim();
        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out var port) ? (text[..colon], port) : (text, 7777);
    }
}
