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

    /// <summary>What the router opened to the hosted relay, once <see cref="OpenPortAsync"/> asked it.</summary>
    public static PortMapping? Mapping { get; private set; }

    /// <summary>
    /// Asks the router to let the others in to the hosted relay over the internet (see <see cref="PortMapping"/>); a
    /// node on the tree's root takes it back when the game ends.
    /// </summary>
    public static async Task<PortMapping?> OpenPortAsync(Godot.SceneTree tree)
    {
        if (HostedRelay is null)
            return null;
        if (Mapping is not null)
            return Mapping;
        var mapping = Mapping = await PortMapping.OpenAsync(HostedRelay.Port);
        // It leaves the tree as the game ends, and takes the router's mapping back; the lease ends it otherwise.
        var closer = new Godot.Node { Name = "PortCloser" };
        closer.TreeExiting += () => Task.Run(() => mapping.CloseAsync()).Wait(TimeSpan.FromSeconds(3));
        tree.Root.AddChild(closer);
        return Mapping;
    }

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
                return Lang.T("cannot open port {0}: {1}", port, exception.Message);
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
            return Lang.T("cannot reach {0}:{1} ({2})", host, port, exception.Message);
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

    /// <summary>"host:port", the port defaulting to the relay's 7777; an IPv6 address in brackets before a port ("[2001:db8::1]:7777").</summary>
    public static (string Host, int Port) Split(string address)
    {
        var text = address.Trim();
        if (text.StartsWith('[') && text.IndexOf(']') is var close and > 0)
            return (text[1..close], text[(close + 1)..].TrimStart(':') is { Length: > 0 } after && int.TryParse(after, out var p) ? p : 7777);
        // More than one colon without brackets: an IPv6 address alone.
        if (text.Count(c => c == ':') > 1)
            return (text, 7777);
        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out var port) ? (text[..colon], port) : (text, 7777);
    }

    /// <summary>An address and port as the others type it: an IPv6 address in brackets.</summary>
    public static string Join(System.Net.IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
}
