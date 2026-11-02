using System.Net;
using System.Net.Sockets;
using System.Text;
using Machines.Net;

namespace Machines.Tests.Net;

/// <summary>Opening the hosted relay's port on a router (UPnP), against a router made up for the test.</summary>
public class PortMappingTests
{
    /// <summary>
    /// A router on this computer that answers its description and its actions as a home router does, and keeps what
    /// it was asked; <see cref="Answer"/> lets a test give another answer to an action.
    /// </summary>
    private sealed class FakeRouter : IAsyncDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource _stop = new();
        private readonly Task _serving;

        public FakeRouter(bool firewall = true)
        {
            Firewall = firewall;
            _listener.Start();
            _serving = Serve();
        }

        public bool Firewall { get; }
        public string ExternalAddress { get; set; } = "93.184.216.34";
        public Func<string, Dictionary<string, string>, string?> Answer { get; set; } = (_, _) => null;
        public List<(string Action, Dictionary<string, string> Arguments)> Calls { get; } = [];

        public Uri Location => new($"http://127.0.0.1:{((IPEndPoint)_listener.LocalEndpoint).Port}/igd.xml");

        private async Task Serve()
        {
            try
            {
                while (true)
                {
                    using var client = await _listener.AcceptTcpClientAsync(_stop.Token);
                    await Handle(client.GetStream());
                }
            }
            catch (OperationCanceledException)
            {
            }
        }

        private async Task Handle(NetworkStream stream)
        {
            // Headers up to the blank line, then as much body as they say.
            var buffer = new List<byte>();
            var one = new byte[1];
            while (!Encoding.ASCII.GetString(buffer.ToArray()).EndsWith("\r\n\r\n") && await stream.ReadAsync(one, _stop.Token) > 0)
                buffer.Add(one[0]);
            var head = Encoding.ASCII.GetString(buffer.ToArray());
            var length = head.Split("\r\n").Where(l => l.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase))
                .Select(l => int.Parse(l[15..].Trim())).FirstOrDefault();
            var body = new byte[length];
            for (var read = 0; read < length;)
                read += await stream.ReadAsync(body.AsMemory(read), _stop.Token);

            var (status, text) = head.StartsWith("GET") ? ("200 OK", Description()) : Act(head, Encoding.UTF8.GetString(body));
            var bytes = Encoding.UTF8.GetBytes(text);
            await stream.WriteAsync(Encoding.ASCII.GetBytes(
                $"HTTP/1.1 {status}\r\nContent-Type: text/xml\r\nContent-Length: {bytes.Length}\r\nConnection: close\r\n\r\n"), _stop.Token);
            await stream.WriteAsync(bytes, _stop.Token);
        }

        private string Description() => $"""
            <?xml version="1.0"?>
            <root xmlns="urn:schemas-upnp-org:device-1-0"><device>
              <deviceType>urn:schemas-upnp-org:device:InternetGatewayDevice:2</deviceType>
              <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANDevice:2</deviceType>
                <deviceList><device><deviceType>urn:schemas-upnp-org:device:WANConnectionDevice:2</deviceType>
                  <serviceList>
                    <service><serviceType>urn:schemas-upnp-org:service:WANIPConnection:1</serviceType><controlURL>/ctl/ip</controlURL></service>
                    {(Firewall ? "<service><serviceType>urn:schemas-upnp-org:service:WANIPv6FirewallControl:1</serviceType><controlURL>/ctl/fw</controlURL></service>" : "")}
                  </serviceList>
                </device></deviceList>
              </device></deviceList>
            </device></root>
            """;

        private (string Status, string Body) Act(string head, string body)
        {
            var action = head.Split("\r\n").First(l => l.StartsWith("SOAPAction:", StringComparison.OrdinalIgnoreCase)).Split('#')[1].Trim('"', ' ');
            var envelope = System.Xml.Linq.XDocument.Parse(body);
            var arguments = envelope.Descendants().First(e => e.Name.LocalName == action).Elements().ToDictionary(e => e.Name.LocalName, e => e.Value);
            Calls.Add((action, arguments));
            if (Answer(action, arguments) is { } fault)
                return ("500 Internal Server Error", Envelope($"""
                    <s:Fault><faultcode>s:Client</faultcode><faultstring>UPnPError</faultstring><detail>
                    <UPnPError xmlns="urn:schemas-upnp-org:control-1-0"><errorCode>{fault.Split(' ')[0]}</errorCode>
                    <errorDescription>{fault[(fault.IndexOf(' ') + 1)..]}</errorDescription></UPnPError></detail></s:Fault>
                    """));
            var values = action switch
            {
                "GetExternalIPAddress" => $"<NewExternalIPAddress>{ExternalAddress}</NewExternalIPAddress>",
                "AddPinhole" => "<UniqueID>17</UniqueID>",
                _ => "",
            };
            return ("200 OK", Envelope($"<u:{action}Response xmlns:u=\"urn:schemas-upnp-org:service:WANIPConnection:1\">{values}</u:{action}Response>"));
        }

        private static string Envelope(string inner) =>
            $"<?xml version=\"1.0\"?><s:Envelope xmlns:s=\"http://schemas.xmlsoap.org/soap/envelope/\"><s:Body>{inner}</s:Body></s:Envelope>";

        public async ValueTask DisposeAsync()
        {
            _stop.Cancel();
            _listener.Stop();
            await _serving;
        }
    }

    private static readonly IPAddress V6 = IPAddress.Parse("2001:db8::5");

    [Fact]
    public async Task The_router_maps_the_port_to_this_computer_and_opens_its_ipv6_firewall()
    {
        await using var router = new FakeRouter();
        var mapping = await PortMapping.OpenAtAsync(7777, router.Location, V6);

        Assert.Null(mapping.Problem);
        Assert.Equal(IPAddress.Parse("93.184.216.34"), mapping.PublicV4);
        Assert.True(mapping.V6Open);
        var add = router.Calls.Single(c => c.Action == "AddPortMapping").Arguments;
        Assert.Equal("7777", add["NewExternalPort"]);
        Assert.Equal("7777", add["NewInternalPort"]);
        Assert.Equal("TCP", add["NewProtocol"]);
        Assert.Equal("127.0.0.1", add["NewInternalClient"]);
        Assert.Equal(((int)PortMapping.Lease.TotalSeconds).ToString(), add["NewLeaseDuration"]);
        var pinhole = router.Calls.Single(c => c.Action == "AddPinhole").Arguments;
        Assert.Equal("2001:db8::5", pinhole["InternalClient"]);
        Assert.Equal("6", pinhole["Protocol"]);

        await mapping.CloseAsync();
        Assert.Equal("7777", router.Calls.Single(c => c.Action == "DeletePortMapping").Arguments["NewExternalPort"]);
        Assert.Equal("17", router.Calls.Single(c => c.Action == "DeletePinhole").Arguments["UniqueID"]);
    }

    [Fact]
    public async Task A_router_that_keeps_only_endless_mappings_gets_one()
    {
        await using var router = new FakeRouter { Answer = (action, args) => action == "AddPortMapping" && args["NewLeaseDuration"] != "0" ? "725 OnlyPermanentLeasesSupported" : null };
        var mapping = await PortMapping.OpenAtAsync(7777, router.Location, null);

        Assert.Null(mapping.Problem);
        Assert.Equal(new[] { "14400", "0" }, router.Calls.Where(c => c.Action == "AddPortMapping").Select(c => c.Arguments["NewLeaseDuration"]));
        Assert.NotNull(mapping.PublicV4);
    }

    [Fact]
    public async Task A_port_taken_by_another_computer_is_told()
    {
        await using var router = new FakeRouter { Answer = (action, _) => action == "AddPortMapping" ? "718 ConflictInMappingEntry" : null };
        var mapping = await PortMapping.OpenAtAsync(7777, router.Location, null);

        Assert.Null(mapping.PublicV4);
        Assert.Contains("another computer", mapping.Problem);
    }

    [Fact]
    public async Task A_router_that_may_not_open_ports_says_where_to_allow_it()
    {
        await using var router = new FakeRouter { Answer = (action, _) => action == "AddPortMapping" ? "606 Action not authorized" : null };
        var mapping = await PortMapping.OpenAtAsync(7777, router.Location, V6);

        Assert.Null(mapping.PublicV4);
        Assert.Contains("FRITZ!Box", mapping.Problem);
        Assert.True(mapping.V6Open);
    }

    [Fact]
    public async Task A_router_behind_the_providers_nat_leaves_only_ipv6()
    {
        await using var router = new FakeRouter(firewall: false) { ExternalAddress = "100.64.12.7" };
        var mapping = await PortMapping.OpenAtAsync(7777, router.Location, V6);

        Assert.Null(mapping.PublicV4);
        Assert.Contains("IPv6", mapping.Problem);
        Assert.Equal(V6, mapping.PublicV6);
        Assert.False(mapping.V6Open);
    }

    [Fact]
    public async Task A_router_that_does_not_answer_is_told()
    {
        var unused = new TcpListener(IPAddress.Loopback, 0);
        unused.Start();
        var port = ((IPEndPoint)unused.LocalEndpoint).Port;
        unused.Stop();
        var mapping = await PortMapping.OpenAtAsync(7777, new Uri($"http://127.0.0.1:{port}/igd.xml"), null);

        Assert.Null(mapping.PublicV4);
        Assert.NotNull(mapping.Problem);
    }

    [Theory]
    [InlineData("93.184.216.34", true)]
    [InlineData("192.168.178.20", false)]
    [InlineData("10.0.0.2", false)]
    [InlineData("172.20.1.1", false)]
    [InlineData("100.64.0.1", false)]
    [InlineData("2a02:810d:1:2::5", true)]
    [InlineData("fd00::1", false)]
    [InlineData("fe80::1", false)]
    public void Only_addresses_the_internet_reaches_count_as_public(string address, bool reachable) =>
        Assert.Equal(reachable, PortMapping.IsPublic(IPAddress.Parse(address)));

    [Fact]
    public void A_gateways_ssdp_answer_gives_its_description()
    {
        const string answer = "HTTP/1.1 200 OK\r\nCACHE-CONTROL: max-age=1800\r\nLOCATION: http://192.168.178.1:49000/igddesc.xml\r\n" +
                              "SERVER: FRITZ!Box UPnP/1.0\r\nST: urn:schemas-upnp-org:device:InternetGatewayDevice:1\r\n\r\n";
        Assert.Equal(new Uri("http://192.168.178.1:49000/igddesc.xml"), PortMapping.Location(answer));
        Assert.Null(PortMapping.Location(answer.Replace("InternetGatewayDevice", "MediaServer")));
    }
}
