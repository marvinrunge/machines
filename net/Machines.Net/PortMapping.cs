using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Xml.Linq;

namespace Machines.Net;

/// <summary>
/// Asks the home router to let the others in to a relay hosted on this computer, the way games do so the player need
/// not forward a port by hand (UPnP Internet Gateway Device): a port mapping from the router's public IPv4 address
/// (WANIPConnection or WANPPPConnection), and where the router has one, a pinhole in its IPv6 firewall to this
/// computer's own public IPv6 address (WANIPv6FirewallControl, which lines with no public IPv4, such as DS-Lite,
/// need). Both run out after <see cref="Lease"/> and are taken back by <see cref="CloseAsync"/>. A router with UPnP
/// off, or one behind the provider's own NAT, cannot help; <see cref="Problem"/> says why.
/// </summary>
public sealed class PortMapping
{
    /// <summary>How long the router keeps the mapping and the pinhole, unless they are taken back before.</summary>
    public static readonly TimeSpan Lease = TimeSpan.FromHours(4);

    private const string Description = "Machines Remake";
    private static readonly IPEndPoint Ssdp = new(IPAddress.Parse("239.255.255.250"), 1900);

    private static readonly string[] Searches =
    [
        "urn:schemas-upnp-org:device:InternetGatewayDevice:1",
        "urn:schemas-upnp-org:device:InternetGatewayDevice:2",
        "urn:schemas-upnp-org:service:WANIPConnection:1",
    ];

    private readonly Uri? _wan, _firewall;
    private readonly string? _wanType;
    private string? _pinhole;

    // Whether the router made the mapping, which CloseAsync takes back even where it is no use from outside.
    private bool _mapped;

    private PortMapping(int port, Uri? wan, string? wanType, Uri? firewall)
    {
        Port = port;
        (_wan, _wanType, _firewall) = (wan, wanType, firewall);
    }

    public int Port { get; }

    /// <summary>The router's public IPv4 address, when the port is mapped on it.</summary>
    public IPAddress? PublicV4 { get; private set; }

    /// <summary>This computer's public IPv6 address, when it has one; <see cref="V6Open"/> tells whether the router's firewall lets the port in.</summary>
    public IPAddress? PublicV6 { get; private set; }

    public bool V6Open => _pinhole is not null;

    /// <summary>Why the others may not get in over the internet, or null when they can.</summary>
    public string? Problem => ProblemText is null ? null : string.Format(ProblemText, ProblemValues);

    /// <summary><see cref="Problem"/> in English with {0}, {1} ... for <see cref="ProblemValues"/>, for a translation of it.</summary>
    public string? ProblemText { get; private set; }

    public object?[] ProblemValues { get; private set; } = [];

    /// <summary>
    /// Finds the router and opens <paramref name="port"/> (TCP) to this computer on it. Never throws: what failed is
    /// in <see cref="Problem"/>; without a router that answers at all there is only this computer's IPv6 address.
    /// </summary>
    public static async Task<PortMapping> OpenAsync(int port, CancellationToken cancel = default)
    {
        var v6 = OwnPublicV6();
        Uri? location = null;
        try
        {
            location = await DiscoverAsync(TimeSpan.FromSeconds(2.5), cancel);
        }
        catch (Exception exception) when (exception is SocketException or IOException)
        {
        }
        if (location is null)
            return new PortMapping(port, null, null, null)
            {
                PublicV6 = v6,
                ProblemText = "The router did not answer (its UPnP may be off): forward port {0} on it by hand.",
                ProblemValues = [port],
            };

        return await OpenAtAsync(port, location, v6, cancel);
    }

    /// <summary>
    /// Opens <paramref name="port"/> on the router whose description is at <paramref name="location"/>, to this
    /// computer on the router's network and to its public IPv6 address <paramref name="v6"/>, if it has one.
    /// </summary>
    public static async Task<PortMapping> OpenAtAsync(int port, Uri location, IPAddress? v6, CancellationToken cancel = default)
    {
        PortMapping mapping;
        try
        {
            mapping = await FromDescriptionAsync(port, location, cancel);
            mapping.PublicV6 = v6;
            await mapping.MapAsync(location, cancel);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException or System.Xml.XmlException or SocketException)
        {
            return new PortMapping(port, null, null, null) { PublicV6 = v6, ProblemText = "The router could not be asked ({0}).", ProblemValues = [exception.Message] };
        }
        return mapping;
    }

    /// <summary>Reads the router's description at <paramref name="location"/>; the services it offers make the mapping.</summary>
    private static async Task<PortMapping> FromDescriptionAsync(int port, Uri location, CancellationToken cancel)
    {
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var xml = XDocument.Parse(await http.GetStringAsync(location, cancel));
        var root = xml.Root?.Elements().FirstOrDefault(e => e.Name.LocalName == "URLBase")?.Value is { Length: > 0 } urlBase
            && Uri.TryCreate(urlBase, UriKind.Absolute, out var b) ? b : location;
        var services = xml.Descendants().Where(e => e.Name.LocalName == "service")
            .Select(s => (Type: Child(s, "serviceType"), Control: Child(s, "controlURL")))
            .Where(s => s.Type is not null && s.Control is not null)
            .ToList();
        var wan = services.FirstOrDefault(s => s.Type!.Contains(":WANIPConnection:")) is { Type: not null } ip ? ip
            : services.FirstOrDefault(s => s.Type!.Contains(":WANPPPConnection:"));
        var firewall = services.FirstOrDefault(s => s.Type!.Contains(":WANIPv6FirewallControl:"));
        return new PortMapping(port,
            wan.Control is null ? null : new Uri(root, wan.Control), wan.Type,
            firewall.Control is null ? null : new Uri(root, firewall.Control));
    }

    private static string? Child(XElement element, string name) =>
        element.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value.Trim();

    private async Task MapAsync(Uri location, CancellationToken cancel)
    {
        if (_wan is null)
        {
            ProblemText = "The router offers no port mappings.";
        }
        else
        {
            var local = LocalAddressTowards(location);
            var lease = ((int)Lease.TotalSeconds).ToString();
            string[] Arguments(string seconds) =>
            [
                "NewRemoteHost", "", "NewExternalPort", Port.ToString(), "NewProtocol", "TCP", "NewInternalPort", Port.ToString(),
                "NewInternalClient", local.ToString(), "NewEnabled", "1", "NewPortMappingDescription", Description, "NewLeaseDuration", seconds,
            ];
            var (_, fault) = await CallAsync(_wan, _wanType!, "AddPortMapping", Arguments(lease), cancel);
            // 725: the router keeps only mappings without an end; CloseAsync takes it back.
            if (fault?.Code == 725)
                (_, fault) = await CallAsync(_wan, _wanType!, "AddPortMapping", Arguments("0"), cancel);
            if (fault is not null)
            {
                (ProblemText, ProblemValues) = fault.Code switch
                {
                    718 => ("Port {0} on the router already leads to another computer.", new object?[] { Port }),
                    606 => ("The router does not let programs open ports: allow it for this computer (on a FRITZ!Box: \"Selbstständige Portfreigaben\").", Array.Empty<object?>()),
                    _ => ("The router would not open the port ({0} {1}).", new object?[] { fault.Code, fault.Text }),
                };
            }
            else
            {
                _mapped = true;
                var (values, _) = await CallAsync(_wan, _wanType!, "GetExternalIPAddress", [], cancel);
                PublicV4 = values.TryGetValue("NewExternalIPAddress", out var text) && IPAddress.TryParse(text, out var address) ? address : null;
                if (PublicV4 is null || !IsPublic(PublicV4))
                {
                    ProblemText = "The router has no public IPv4 address of its own (the provider shares one): the others get in only over IPv6.";
                    PublicV4 = null;
                }
            }
        }

        if (PublicV6 is not null && _firewall is not null)
        {
            var (values, fault) = await CallAsync(_firewall, "urn:schemas-upnp-org:service:WANIPv6FirewallControl:1", "AddPinhole",
            [
                "RemoteHost", "", "RemotePort", "0", "InternalClient", PublicV6.ToString(), "InternalPort", Port.ToString(),
                "Protocol", "6", "LeaseTime", ((int)Lease.TotalSeconds).ToString(),
            ], cancel);
            if (fault is null && values.TryGetValue("UniqueID", out var id))
                _pinhole = id;
        }
    }

    /// <summary>Takes the mapping and the pinhole back from the router.</summary>
    public async Task CloseAsync(CancellationToken cancel = default)
    {
        try
        {
            if (_mapped && _wan is not null)
                await CallAsync(_wan, _wanType!, "DeletePortMapping", ["NewRemoteHost", "", "NewExternalPort", Port.ToString(), "NewProtocol", "TCP"], cancel);
            if (_pinhole is { } id && _firewall is not null)
                await CallAsync(_firewall, "urn:schemas-upnp-org:service:WANIPv6FirewallControl:1", "DeletePinhole", ["UniqueID", id], cancel);
        }
        catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
        {
            // The router forgets them when the lease runs out.
        }
        PublicV4 = null;
        _mapped = false;
        _pinhole = null;
    }

    /// <summary>A UPnP error the router answered with.</summary>
    public sealed record Fault(int Code, string Text);

    /// <summary>Calls one of the router's actions; returns its answer's values, or the error it gave.</summary>
    private static async Task<(Dictionary<string, string> Values, Fault? Fault)> CallAsync(Uri control, string service, string action,
        string[] arguments, CancellationToken cancel)
    {
        var body = new StringBuilder();
        body.Append("<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\" ")
            .Append("s:encodingStyle=\"http://schemas.xmlsoap.org/soap/encoding/\"><s:Body>")
            .Append($"<u:{action} xmlns:u=\"{service}\">");
        for (var i = 0; i < arguments.Length; i += 2)
            body.Append($"<{arguments[i]}>{System.Security.SecurityElement.Escape(arguments[i + 1])}</{arguments[i]}>");
        body.Append($"</u:{action}></s:Body></s:Envelope>");

        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        using var request = new HttpRequestMessage(HttpMethod.Post, control) { Content = new StringContent(body.ToString(), Encoding.UTF8, "text/xml") };
        request.Headers.TryAddWithoutValidation("SOAPAction", $"\"{service}#{action}\"");
        using var response = await http.SendAsync(request, cancel);
        var text = await response.Content.ReadAsStringAsync(cancel);
        XDocument answer;
        try
        {
            answer = XDocument.Parse(text);
        }
        catch (System.Xml.XmlException)
        {
            return ([], new Fault((int)response.StatusCode, response.ReasonPhrase ?? "no answer"));
        }
        if (answer.Descendants().FirstOrDefault(e => e.Name.LocalName == "UPnPError") is { } error)
            return ([], new Fault(int.TryParse(Child(error, "errorCode"), out var code) ? code : 0, Child(error, "errorDescription") ?? ""));
        if (!response.IsSuccessStatusCode)
            return ([], new Fault((int)response.StatusCode, response.ReasonPhrase ?? ""));
        var values = answer.Descendants().FirstOrDefault(e => e.Name.LocalName == action + "Response")?.Elements()
            .ToDictionary(e => e.Name.LocalName, e => e.Value.Trim()) ?? [];
        return (values, null);
    }

    /// <summary>Asks the network for an internet gateway (SSDP); returns where its description is, or null.</summary>
    private static async Task<Uri?> DiscoverAsync(TimeSpan wait, CancellationToken cancel)
    {
        using var udp = new UdpClient(AddressFamily.InterNetwork);
        foreach (var search in Searches)
        {
            var request = Encoding.ASCII.GetBytes(
                $"M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1900\r\nMAN: \"ssdp:discover\"\r\nMX: 2\r\nST: {search}\r\n\r\n");
            await udp.SendAsync(request, Ssdp, cancel);
        }
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        timeout.CancelAfter(wait);
        try
        {
            while (true)
            {
                var reply = await udp.ReceiveAsync(timeout.Token);
                if (Location(Encoding.ASCII.GetString(reply.Buffer)) is { } location)
                    return location;
            }
        }
        catch (OperationCanceledException) when (!cancel.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>The LOCATION of an SSDP answer from a gateway, or null.</summary>
    public static Uri? Location(string answer)
    {
        var lines = answer.Split("\r\n");
        if (!lines.Any(l => l.StartsWith("ST:", StringComparison.OrdinalIgnoreCase) && Searches.Any(s => l.Contains(s, StringComparison.OrdinalIgnoreCase))))
            return null;
        var line = lines.FirstOrDefault(l => l.StartsWith("LOCATION:", StringComparison.OrdinalIgnoreCase));
        return line is not null && Uri.TryCreate(line["LOCATION:".Length..].Trim(), UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>This computer's address on the network the router is on.</summary>
    private static IPAddress LocalAddressTowards(Uri router)
    {
        using var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
        // Connecting a datagram socket sends nothing; it only picks the way there.
        socket.Connect(router.Host, router.Port);
        return ((IPEndPoint)socket.LocalEndPoint!).Address;
    }

    /// <summary>Whether others on the internet can reach the IPv4 address: not a home network's, nor a provider's shared one.</summary>
    public static bool IsPublic(IPAddress address)
    {
        if (address.AddressFamily == AddressFamily.InterNetworkV6)
            return (address.GetAddressBytes()[0] & 0xE0) == 0x20;
        var b = address.GetAddressBytes();
        return !(b[0] is 0 or 10 or 127 || b[0] >= 224
                 || b[0] == 100 && b[1] is >= 64 and < 128
                 || b[0] == 169 && b[1] == 254
                 || b[0] == 172 && b[1] is >= 16 and < 32
                 || b[0] == 192 && b[1] == 168);
    }

    /// <summary>This computer's own public IPv6 address, the longest-lived where there are several, or null.</summary>
    public static IPAddress? OwnPublicV6()
    {
        try
        {
            return NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(n => n.GetIPProperties().UnicastAddresses)
                .Where(a => a.Address.AddressFamily == AddressFamily.InterNetworkV6 && IsPublic(a.Address))
                .OrderByDescending(Lifetime)
                .Select(a => a.Address)
                .FirstOrDefault();
        }
        catch (NetworkInformationException)
        {
            return null;
        }

        // Windows tells how long each address lasts; elsewhere they count alike.
        static long Lifetime(UnicastIPAddressInformation address) => OperatingSystem.IsWindows() ? address.AddressPreferredLifetime : 0;
    }
}
