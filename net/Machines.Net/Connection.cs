using System.Buffers.Binary;
using System.Net.Sockets;
using System.Threading.Channels;

namespace Machines.Net;

/// <summary>Frames on a stream: a 4-byte little-endian length, then that many bytes of JSON.</summary>
public static class Framing
{
    /// <summary>Largest frame either side accepts (a turn is a few hundred bytes).</summary>
    public const int MaxFrame = 1 << 20;

    public static async Task WriteAsync(Stream stream, NetMessage message, CancellationToken cancel)
    {
        var body = NetJson.Encode(message);
        var header = new byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(header, body.Length);
        await stream.WriteAsync(header, cancel);
        await stream.WriteAsync(body, cancel);
        await stream.FlushAsync(cancel);
    }

    /// <returns>The next message, or null when the other side closed the stream.</returns>
    public static async Task<NetMessage?> ReadAsync(Stream stream, CancellationToken cancel)
    {
        var header = new byte[4];
        if (!await ReadExactlyAsync(stream, header, cancel))
            return null;
        var length = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (length is <= 0 or > MaxFrame)
            throw new InvalidDataException($"frame of {length} bytes");
        var body = new byte[length];
        if (!await ReadExactlyAsync(stream, body, cancel))
            return null;
        return NetJson.Decode(body);
    }

    private static async Task<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancel)
    {
        var read = 0;
        while (read < buffer.Length)
        {
            var n = await stream.ReadAsync(buffer.AsMemory(read), cancel);
            if (n == 0)
                return false;
            read += n;
        }
        return true;
    }
}

/// <summary>
/// One TCP connection carrying messages both ways. Sends are queued and written by one loop, so any thread may
/// send; received messages wait in <see cref="Inbox"/> until the owner reads them.
/// </summary>
public sealed class Connection : IAsyncDisposable
{
    private readonly TcpClient _client;
    private readonly Channel<NetMessage> _outbox = Channel.CreateUnbounded<NetMessage>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Channel<NetMessage> _inbox = Channel.CreateUnbounded<NetMessage>(new UnboundedChannelOptions { SingleWriter = true });
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _writing;
    private readonly Task _reading;

    public Connection(TcpClient client)
    {
        _client = client;
        _client.NoDelay = true;
        var stream = client.GetStream();
        _writing = Task.Run(async () =>
        {
            try
            {
                await foreach (var message in _outbox.Reader.ReadAllAsync(_stop.Token))
                    await Framing.WriteAsync(stream, message, _stop.Token);
            }
            catch (Exception) when (_stop.IsCancellationRequested || !_client.Connected)
            {
            }
            catch (IOException)
            {
            }
        });
        _reading = Task.Run(async () =>
        {
            try
            {
                while (await Framing.ReadAsync(stream, _stop.Token) is { } message)
                    await _inbox.Writer.WriteAsync(message, _stop.Token);
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or ObjectDisposedException or System.Text.Json.JsonException or InvalidDataException)
            {
            }
            finally
            {
                _inbox.Writer.TryComplete();
            }
        });
    }

    public static async Task<Connection> ConnectAsync(string host, int port, CancellationToken cancel)
    {
        var client = new TcpClient();
        await client.ConnectAsync(host, port, cancel);
        return new Connection(client);
    }

    /// <summary>Messages received, in order; completes when the connection closes.</summary>
    public ChannelReader<NetMessage> Inbox => _inbox.Reader;

    public bool Closed => _inbox.Reader.Completion.IsCompleted;

    public void Send(NetMessage message) => _outbox.Writer.TryWrite(message);

    public async ValueTask DisposeAsync()
    {
        _outbox.Writer.TryComplete();
        try
        {
            await _writing.WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (TimeoutException)
        {
        }
        _stop.Cancel();
        _client.Dispose();
        try
        {
            await _reading;
        }
        catch (OperationCanceledException)
        {
        }
    }
}
