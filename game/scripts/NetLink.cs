using Machines.Net;

namespace Machines.Game;

/// <summary>The connection to the multiplayer relay; it lives across scenes, from the lobby into the match.</summary>
public static class NetLink
{
    /// <summary>
    /// The public relay this build offers when the settings and MACHINES_RELAY name none (see
    /// <see cref="GameOptions.PublicRelay"/>): the address Cloud Run gives server/Machines.Relay/deploy-cloudrun.sh, as
    /// <c>wss://…</c>. Empty until one is deployed.
    /// </summary>
    public const string PublicRelay = "";

    /// <summary>How long a web address may take to answer: a relay asleep on Cloud Run first has to wake up.</summary>
    public static readonly TimeSpan WebTimeout = TimeSpan.FromSeconds(30);

    /// <summary>How long a TCP address may take to answer.</summary>
    public static readonly TimeSpan TcpTimeout = TimeSpan.FromSeconds(5);

    private static string? _name;
    private static Godot.Node? _closer;

    public static Connection? Connection { get; private set; }
    public static int ClientId { get; private set; }

    /// <summary>The player's name online: the one last used, kept between sessions (<see cref="GameOptions.PlayerName"/>).</summary>
    public static string Name
    {
        get => _name ??= GameOptions.PlayerName ?? "Player";
        private set => _name = value;
    }

    /// <summary>The version of the relay connected to, as its welcome said; null for a relay too old to say.</summary>
    public static string? RelayVersion { get; private set; }

    /// <summary>The player's name in a match without the relay: one never given reads in the player's language ("Spieler").</summary>
    public static string OfflineName => Name == "Player" ? Lang.T("Player") : Name;

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
    public static async Task<string?> HostAsync(int port, string name, bool keepName = true)
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
        return await ConnectAsync($"127.0.0.1:{HostedRelay.Port}", name, keepName);
    }

    /// <summary>
    /// Connects to a relay by what the player typed (a web address by WebSocket, "host:port" over TCP, see
    /// <see cref="RelayAddress"/>) and introduces the player, asking to keep the seat over a dropped line; returns why it
    /// failed, or null. With <paramref name="keepName"/> the name is kept for the next session (automated checks leave
    /// the player's own alone).
    /// </summary>
    public static async Task<string?> ConnectAsync(string address, string name, bool keepName = true)
    {
        await DisconnectAsync();
        var web = RelayAddress.IsWebSocket(address);
        // Until the relay's welcome: a connection given up on before it is closed below, or it would linger (and a late
        // welcome would make it resumable, keeping a seat nobody plays alive with its heartbeat).
        Connection? connecting = null;
        try
        {
            using var timeout = new CancellationTokenSource(web ? WebTimeout : TcpTimeout);
            var connection = connecting = await Connection.ConnectAsync(address, timeout.Token);
            connection.Send(new Hello(name, NetJson.Protocol, Resume: true));
            var reply = await connection.Inbox.ReadAsync(timeout.Token);
            if (reply is not Welcome welcome)
                return reply is Refused refused ? refused.Reason : "the relay did not answer";
            connecting = null;
            Connection = connection;
            ClientId = welcome.ClientId;
            RelayVersion = welcome.Relay;
            CloseWithTheGame();
            Name = name;
            if (keepName)
                GameOptions.PlayerName = name;
            return null;
        }
        catch (UriFormatException)
        {
            return Lang.T("{0} is not a relay's address", address.Trim());
        }
        catch (Exception exception) when (exception is System.Net.Sockets.SocketException or System.Net.WebSockets.WebSocketException
                                              or OperationCanceledException or IOException or System.Threading.Channels.ChannelClosedException)
        {
            var why = exception is OperationCanceledException ? Lang.T("no answer") : exception.Message;
            if (web)
                return Lang.T("cannot reach {0} ({1})", address.Trim(), why);
            var (host, port) = RelayAddress.Split(address);
            return Lang.T("cannot reach {0}:{1} ({2})", host, port, why);
        }
        finally
        {
            if (connecting is not null)
                await connecting.DisposeAsync();
        }
    }

    /// <summary>
    /// A node on the tree's root closes the connection as the game ends: it says goodbye, so the others are not kept
    /// waiting the minute the relay holds a dropped player's seat for.
    /// </summary>
    private static void CloseWithTheGame()
    {
        if (_closer is not null && Godot.GodotObject.IsInstanceValid(_closer) || Godot.Engine.GetMainLoop() is not Godot.SceneTree tree)
            return;
        _closer = new Godot.Node { Name = "RelayCloser" };
        _closer.TreeExiting += () => Task.Run(DisconnectAsync).Wait(TimeSpan.FromSeconds(2));
        tree.Root.CallDeferred(Godot.Node.MethodName.AddChild, _closer);
    }

    public static async Task DisconnectAsync()
    {
        if (Connection is { } connection)
        {
            Connection = null;
            RelayVersion = null;
            await connection.DisposeAsync();
        }
    }

    public static void Send(NetMessage message) => Connection?.Send(message);

    /// <summary>An address and port as the others type it: an IPv6 address in brackets.</summary>
    public static string Join(System.Net.IPAddress address, int port) =>
        address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6 ? $"[{address}]:{port}" : $"{address}:{port}";
}
