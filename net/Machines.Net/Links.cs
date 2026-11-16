using System.Net.Sockets;
using System.Net.WebSockets;

namespace Machines.Net;

/// <summary>
/// One socket carrying whole messages both ways: a TCP stream of frames (<see cref="TcpLink"/>) or a WebSocket of text
/// messages (<see cref="WebSocketLink"/>). One task sends and one receives at a time; a <see cref="Connection"/> may move
/// from one link to the next without the messages above it noticing.
/// </summary>
public abstract class Link : IAsyncDisposable
{
    private readonly TaskCompletionSource _ended = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>What the link runs on, for the relay's log: "TCP" or "WebSocket".</summary>
    public abstract string Kind { get; }

    /// <summary>Completes once the link is closed or cut; the relay's web handler holds a WebSocket open until then.</summary>
    public Task Ended => _ended.Task;

    public abstract Task SendAsync(NetMessage message, CancellationToken cancel);

    /// <returns>The next message, or null when the other side closed the link.</returns>
    public abstract Task<NetMessage?> ReceiveAsync(CancellationToken cancel);

    /// <summary>Cuts the link at once, without a word to the other side, as a broken line would.</summary>
    public void Abort()
    {
        AbortSocket();
        _ended.TrySetResult();
    }

    /// <summary>Closes the link as politely as the socket allows, within a second.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            await CloseSocketAsync();
        }
        catch (Exception exception) when (exception is IOException or SocketException or WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }
        finally
        {
            _ended.TrySetResult();
        }
    }

    protected abstract void AbortSocket();

    protected abstract Task CloseSocketAsync();
}

/// <summary>A TCP socket carrying <see cref="Framing"/>'s frames: the LAN, a host's address, the relay in the game.</summary>
public sealed class TcpLink : Link
{
    private readonly TcpClient _client;
    private readonly NetworkStream _stream;

    public TcpLink(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        _stream = client.GetStream();
    }

    public override string Kind => "TCP";

    public static async Task<TcpLink> ConnectAsync(string host, int port, CancellationToken cancel)
    {
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host, port, cancel);
            return new TcpLink(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public override Task SendAsync(NetMessage message, CancellationToken cancel) => Framing.WriteAsync(_stream, message, cancel);

    public override Task<NetMessage?> ReceiveAsync(CancellationToken cancel) => Framing.ReadAsync(_stream, cancel);

    protected override void AbortSocket()
    {
        // No lingering: the other side sees the line break rather than an orderly close.
        try
        {
            _client.Client.LingerState = new LingerOption(true, 0);
        }
        catch (Exception exception) when (exception is SocketException or ObjectDisposedException)
        {
        }
        _client.Dispose();
    }

    protected override Task CloseSocketAsync()
    {
        _client.Dispose();
        return Task.CompletedTask;
    }
}

/// <summary>
/// A WebSocket carrying one message as one text message of the same JSON <see cref="NetJson"/> writes: the way to a relay
/// on the internet (Cloud Run speaks HTTP only), through proxies and TLS (<c>wss://</c>).
/// </summary>
public sealed class WebSocketLink(WebSocket socket) : Link
{
    private readonly byte[] _buffer = new byte[16 * 1024];

    public override string Kind => "WebSocket";

    /// <summary>The WebSocket underneath, for the relay's web handler.</summary>
    public WebSocket Socket => socket;

    public static async Task<WebSocketLink> ConnectAsync(Uri address, CancellationToken cancel)
    {
        var client = new ClientWebSocket();
        // Pings keep proxies on the way from taking a quiet lobby for a dead one.
        client.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
        try
        {
            await client.ConnectAsync(address, cancel);
            return new WebSocketLink(client);
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    public override Task SendAsync(NetMessage message, CancellationToken cancel) =>
        socket.SendAsync(new ArraySegment<byte>(NetJson.Encode(message)), WebSocketMessageType.Text, endOfMessage: true, cancel);

    public override async Task<NetMessage?> ReceiveAsync(CancellationToken cancel)
    {
        var length = 0;
        byte[]? whole = null;
        while (true)
        {
            ValueWebSocketReceiveResult result;
            try
            {
                result = await socket.ReceiveAsync(_buffer.AsMemory(), cancel);
            }
            catch (WebSocketException exception) when (exception.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
            {
                return null;
            }
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (length + result.Count > Framing.MaxFrame)
                throw new InvalidDataException($"message of more than {Framing.MaxFrame} bytes");
            // Most messages fit the buffer whole; a longer one is gathered.
            if (result.EndOfMessage && whole is null)
                return NetJson.Decode(_buffer.AsSpan(0, result.Count));
            whole ??= new byte[Math.Max(_buffer.Length * 2, 64 * 1024)];
            if (length + result.Count > whole.Length)
                Array.Resize(ref whole, Math.Min(Framing.MaxFrame, Math.Max(whole.Length * 2, length + result.Count)));
            _buffer.AsSpan(0, result.Count).CopyTo(whole.AsSpan(length));
            length += result.Count;
            if (result.EndOfMessage)
                return NetJson.Decode(whole.AsSpan(0, length));
        }
    }

    protected override void AbortSocket() => socket.Abort();

    protected override async Task CloseSocketAsync()
    {
        if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, timeout.Token);
        }
        socket.Dispose();
    }
}
