using System.Net;
using System.Net.Sockets;
using Gleanvolt.Infrastructure.Mdns;

namespace Gleanvolt.Infrastructure.Tests.Mdns;

/// <summary>
/// The responder over real UDP, on loopback (issue #363): a query gets the SRV, TXT and A records back.
/// A free port rather than 5353, and no multicast at all, so a test run sends nothing to the LAN it is on
/// and does not depend on what else on the machine holds 5353.
/// </summary>
public class UdpMdnsTransportTests
{
    private static readonly MdnsTimings Fast = new(TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(10), TimeSpan.FromHours(1));

    [Fact]
    public async Task A_query_on_loopback_gets_the_service_and_the_address_back()
    {
        await using var transport = new UdpMdnsTransport(port: 0, multicast: false);
        var advertisement = new MdnsAdvertisement(
            "gleanvolt", "Gleanvolt", 8090, ["_http._tcp", "_gleanvolt._tcp"], ["path=/", "version=1.2.3", "setup=pending"]);
        var responder = new MdnsResponder(transport, advertisement, timings: Fast);

        using var stop = new CancellationTokenSource();
        var running = responder.RunAsync(stop.Token);
        await responder.FirstClaim.WaitAsync(TimeSpan.FromSeconds(5));

        using var client = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var query = new DnsMessage { Id = 99, Questions = [new DnsQuestion("_gleanvolt._tcp.local", DnsType.Ptr)] }.Encode();
        await client.SendAsync(query, new IPEndPoint(IPAddress.Loopback, transport.Port));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var received = await client.ReceiveAsync(timeout.Token);
        var response = DnsMessage.TryParse(received.Buffer);

        Assert.NotNull(response);
        Assert.Equal(99, response.Id);
        Assert.Equal(new PtrRData("Gleanvolt._gleanvolt._tcp.local"), Assert.Single(response.Answers).Data);

        var service = Assert.Single(response.Additionals, record => record.Type == DnsType.Srv);
        Assert.Equal(8090, ((SrvRData)service.Data).Port);

        var text = Assert.Single(response.Additionals, record => record.Type == DnsType.Txt);
        Assert.Contains("setup=pending", ((TxtRData)text.Data).Strings);

        // The address of the interface the query arrived on: loopback here, the LAN in real life.
        Assert.Contains(response.Additionals, record =>
            record.Type == DnsType.A && ((AddressRData)record.Data).Address.Equals(IPAddress.Loopback));

        await stop.CancelAsync();
        await running;
    }
}
