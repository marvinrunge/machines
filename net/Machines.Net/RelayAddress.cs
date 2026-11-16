namespace Machines.Net;

/// <summary>
/// What a player types to reach a relay. A web address (<c>wss://relay.example.com</c>, <c>ws://…</c>, or the
/// <c>https://…</c> Cloud Run shows) is reached by WebSocket on the relay's <see cref="WebSocketPath"/>; anything else is
/// "host:port" over TCP, as on the LAN or to a host's address.
/// </summary>
public static class RelayAddress
{
    /// <summary>The relay's TCP port when an address names none.</summary>
    public const int DefaultPort = 7777;

    /// <summary>Where the relay takes WebSockets.</summary>
    public const string WebSocketPath = "/ws";

    /// <summary>Whether the address is a web address, reached by WebSocket.</summary>
    public static bool IsWebSocket(string address)
    {
        var text = address.Trim();
        return text.StartsWith("ws://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("wss://", StringComparison.OrdinalIgnoreCase)
            || text.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || text.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The WebSocket address of a web address: http becomes ws and https wss, and an address without a path gets the
    /// relay's <see cref="WebSocketPath"/>. Throws <see cref="UriFormatException"/> for one that is no address.
    /// </summary>
    public static Uri WebSocketUri(string address)
    {
        var original = new Uri(address.Trim(), UriKind.Absolute);
        var builder = new UriBuilder(original)
        {
            Scheme = original.Scheme.ToLowerInvariant() switch
            {
                "http" or "ws" => "ws",
                "https" or "wss" => "wss",
                var other => throw new UriFormatException($"{other} is not a WebSocket address"),
            },
        };
        // The default port of the new scheme, not the old one's number written out.
        if (original.IsDefaultPort)
            builder.Port = -1;
        if (builder.Path is "" or "/")
            builder.Path = WebSocketPath;
        return builder.Uri;
    }

    /// <summary>"host:port", the port defaulting to the relay's 7777; an IPv6 address in brackets before a port ("[2001:db8::1]:7777").</summary>
    public static (string Host, int Port) Split(string address)
    {
        var text = address.Trim();
        if (text.StartsWith('[') && text.IndexOf(']') is var close and > 0)
            return (text[1..close], text[(close + 1)..].TrimStart(':') is { Length: > 0 } after && int.TryParse(after, out var p) ? p : DefaultPort);
        // More than one colon without brackets: an IPv6 address alone.
        if (text.Count(c => c == ':') > 1)
            return (text, DefaultPort);
        var colon = text.LastIndexOf(':');
        return colon > 0 && int.TryParse(text[(colon + 1)..], out var port) ? (text[..colon], port) : (text, DefaultPort);
    }
}
