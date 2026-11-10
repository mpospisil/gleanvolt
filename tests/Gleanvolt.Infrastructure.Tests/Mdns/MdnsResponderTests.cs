using System.Net;
using Gleanvolt.Infrastructure.Mdns;

namespace Gleanvolt.Infrastructure.Tests.Mdns;

/// <summary>
/// The responder on an in-memory LAN: the name it claims, what it answers, and how it gives way to a
/// device that already holds the name (issue #363).
/// </summary>
public class MdnsResponderTests
{
    private static readonly MdnsTimings Fast = new(TimeSpan.FromMilliseconds(15), TimeSpan.FromMilliseconds(15), TimeSpan.FromHours(1));
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private static MdnsAdvertisement Gleanvolt(string setup = "done") => new(
        "gleanvolt",
        "Gleanvolt",
        8090,
        ["_http._tcp", "_gleanvolt._tcp"],
        ["path=/", "version=1.2.3", $"setup={setup}"]);

    private sealed class Running : IAsyncDisposable
    {
        private readonly CancellationTokenSource _stop = new();

        public Running(IMdnsTransport transport, MdnsAdvertisement advertisement)
        {
            Responder = new MdnsResponder(transport, advertisement, timings: Fast);
            Task = Responder.RunAsync(_stop.Token);
        }

        public MdnsResponder Responder { get; }

        public Task Task { get; }

        public async Task StopAsync()
        {
            await _stop.CancelAsync();
            await Task;
        }

        public async ValueTask DisposeAsync()
        {
            if (!Task.IsCompleted)
            {
                await StopAsync();
            }

            _stop.Dispose();
        }
    }

    [Fact]
    public async Task Claims_its_own_name_and_is_not_put_off_by_its_own_echo()
    {
        var lan = new InMemoryLan();
        await using var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt());

        Assert.Equal("gleanvolt.local", await gleanvolt.Responder.FirstClaim.WaitAsync(Wait));
        Assert.Equal("Gleanvolt", gleanvolt.Responder.InstanceName);
    }

    [Fact]
    public async Task A_browser_finds_the_service_with_its_port_text_and_address()
    {
        var lan = new InMemoryLan();
        await using var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt("pending"));
        await gleanvolt.Responder.FirstClaim.WaitAsync(Wait);

        var browser = lan.Join("192.168.1.50");
        await browser.SendMulticastAsync(
            DnsMessage.Query(new DnsQuestion("_gleanvolt._tcp.local", DnsType.Ptr)).Encode(), 1, CancellationToken.None);

        var response = await NextAnswerAsync(browser, "_gleanvolt._tcp.local");

        var pointer = Assert.Single(response.Answers);
        Assert.Equal(new PtrRData("Gleanvolt._gleanvolt._tcp.local"), pointer.Data);

        var service = Assert.Single(response.Additionals, record => record.Type == DnsType.Srv);
        Assert.Equal(new SrvRData(0, 0, 8090, "gleanvolt.local"), service.Data);

        var text = Assert.Single(response.Additionals, record => record.Type == DnsType.Txt);
        Assert.Equal(["path=/", "version=1.2.3", "setup=pending"], ((TxtRData)text.Data).Strings);

        var address = Assert.Single(response.Additionals, record => record.Type == DnsType.A);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), ((AddressRData)address.Data).Address);
    }

    [Fact]
    public async Task Answers_the_host_name_with_its_address()
    {
        var lan = new InMemoryLan();
        await using var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        await gleanvolt.Responder.FirstClaim.WaitAsync(Wait);

        var laptop = lan.Join("192.168.1.51");
        await laptop.SendMulticastAsync(
            DnsMessage.Query(new DnsQuestion("GleanVolt.local", DnsType.A)).Encode(), 1, CancellationToken.None);

        var response = await NextAnswerAsync(laptop, "gleanvolt.local");

        var address = Assert.Single(response.Answers);
        Assert.Equal(DnsType.A, address.Type);
        Assert.Equal(IPAddress.Parse("192.168.1.20"), ((AddressRData)address.Data).Address);
        Assert.True(address.CacheFlush);
        Assert.Equal(120u, address.Ttl);
    }

    [Fact]
    public async Task A_second_instance_on_the_lan_becomes_gleanvolt_2_and_the_first_keeps_its_name()
    {
        var lan = new InMemoryLan();
        await using var first = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        Assert.Equal("gleanvolt.local", await first.Responder.FirstClaim.WaitAsync(Wait));

        await using var second = new Running(lan.Join("192.168.1.21"), Gleanvolt());
        Assert.Equal("gleanvolt-2.local", await second.Responder.FirstClaim.WaitAsync(Wait));
        Assert.Equal("Gleanvolt (2)", second.Responder.InstanceName);

        // Neither broke the other: each answers for its own name, with its own address.
        var laptop = lan.Join("192.168.1.51");
        await laptop.SendMulticastAsync(
            DnsMessage.Query(new DnsQuestion("gleanvolt.local", DnsType.A), new DnsQuestion("gleanvolt-2.local", DnsType.A)).Encode(),
            1,
            CancellationToken.None);

        var addresses = new Dictionary<string, IPAddress>(StringComparer.OrdinalIgnoreCase);
        while (addresses.Count < 2)
        {
            var response = await laptop.NextResponseAsync(Wait);
            foreach (var record in response.Answers.Where(record => record.Type == DnsType.A))
            {
                addresses[record.Name] = ((AddressRData)record.Data).Address;
            }
        }

        Assert.Equal(IPAddress.Parse("192.168.1.20"), addresses["gleanvolt.local"]);
        Assert.Equal(IPAddress.Parse("192.168.1.21"), addresses["gleanvolt-2.local"]);
        Assert.Equal("gleanvolt.local", first.Responder.HostName);
    }

    [Fact]
    public async Task A_second_instance_on_the_same_machine_is_not_mistaken_for_an_echo()
    {
        // Same machine, same address: the newcomer's probes are byte-for-byte the ones the first sent
        // when it started. They must not be taken for the first one's own echo.
        var lan = new InMemoryLan();
        await using var first = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        Assert.Equal("gleanvolt.local", await first.Responder.FirstClaim.WaitAsync(Wait));

        await Task.Delay(TimeSpan.FromSeconds(1.2)); // an instance started later, not in the same instant

        await using var second = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        Assert.Equal("gleanvolt-2.local", await second.Responder.FirstClaim.WaitAsync(Wait));
        Assert.Equal("gleanvolt.local", first.Responder.HostName);
    }

    [Fact]
    public async Task Another_device_claiming_the_name_later_makes_it_move_on()
    {
        var lan = new InMemoryLan();
        await using var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        await gleanvolt.Responder.FirstClaim.WaitAsync(Wait);

        // A device that does not probe, announcing the same name with another address.
        var rogue = lan.Join("192.168.1.99");
        var announcement = DnsMessage.Response();
        announcement.Answers.Add(DnsRecord.Address("gleanvolt.local", IPAddress.Parse("192.168.1.99"), 120));
        await rogue.SendMulticastAsync(announcement.Encode(), 1, CancellationToken.None);

        var deadline = DateTime.UtcNow + Wait;
        while (gleanvolt.Responder.HostName != "gleanvolt-2.local" && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10);
        }

        Assert.Equal("gleanvolt-2.local", gleanvolt.Responder.HostName);
    }

    [Fact]
    public async Task A_legacy_resolver_gets_its_id_and_question_back_with_short_ttls()
    {
        var lan = new InMemoryLan();
        await using var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        await gleanvolt.Responder.FirstClaim.WaitAsync(Wait);

        // A plain resolver asks from an ordinary port, e.g. `dig -p 5353 @224.0.0.251`.
        var resolver = lan.Join("192.168.1.52", port: 40000);
        var query = new DnsMessage { Id = 4242, Questions = [new DnsQuestion("gleanvolt.local", DnsType.A)] };
        await resolver.SendMulticastAsync(query.Encode(), 1, CancellationToken.None);

        var response = await resolver.NextResponseAsync(Wait);

        Assert.Equal(4242, response.Id);
        Assert.Equal("gleanvolt.local", Assert.Single(response.Questions).Name);
        var address = Assert.Single(response.Answers);
        Assert.Equal(10u, address.Ttl);
        Assert.False(address.CacheFlush);
    }

    [Fact]
    public async Task What_the_asker_already_knows_is_not_sent_again()
    {
        var lan = new InMemoryLan();
        await using var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        await gleanvolt.Responder.FirstClaim.WaitAsync(Wait);

        var browser = lan.Join("192.168.1.50");
        await Task.Delay(100); // past the second announcement
        var before = Answering(lan);

        var query = DnsMessage.Query(new DnsQuestion("_http._tcp.local", DnsType.Ptr));
        query.Answers.Add(DnsRecord.Pointer("_http._tcp.local", "Gleanvolt._http._tcp.local", 4500));
        await browser.SendMulticastAsync(query.Encode(), 1, CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(before, Answering(lan));
    }

    [Fact]
    public async Task Stopping_says_goodbye_with_a_zero_ttl()
    {
        var lan = new InMemoryLan();
        var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        await gleanvolt.Responder.FirstClaim.WaitAsync(Wait);

        await gleanvolt.StopAsync();
        await gleanvolt.DisposeAsync();

        DnsMessage goodbye;
        lock (lan.Multicast)
        {
            goodbye = lan.Multicast.Last();
        }

        Assert.True(goodbye.IsResponse);
        Assert.NotEmpty(goodbye.Answers);
        Assert.All(goodbye.Answers, record => Assert.Equal(0u, record.Ttl));
        Assert.Contains(goodbye.Answers, record => record.Type == DnsType.A && DnsMessage.SameName(record.Name, "gleanvolt.local"));
    }

    [Fact]
    public async Task Answers_nothing_for_names_that_are_not_its_own()
    {
        var lan = new InMemoryLan();
        await using var gleanvolt = new Running(lan.Join("192.168.1.20"), Gleanvolt());
        await gleanvolt.Responder.FirstClaim.WaitAsync(Wait);

        // Both announcements out before the count is taken. A fixed delay was not enough on a slow CI
        // runner: the second one landed after the baseline and was counted as an answer to the laptop.
        await UntilAsync(() => Answering(lan) >= 2).WaitAsync(Wait);
        var before = Answering(lan);

        var laptop = lan.Join("192.168.1.51");
        await laptop.SendMulticastAsync(
            DnsMessage.Query(new DnsQuestion("printer.local", DnsType.A), new DnsQuestion("_ipp._tcp.local", DnsType.Ptr)).Encode(),
            1,
            CancellationToken.None);
        await Task.Delay(100);

        Assert.Equal(before, Answering(lan));
    }

    private static async Task UntilAsync(Func<bool> condition)
    {
        while (!condition())
        {
            await Task.Delay(5);
        }
    }

    private static int Answering(InMemoryLan lan)
    {
        lock (lan.Multicast)
        {
            return lan.Multicast.Count(message => message.IsResponse);
        }
    }

    private static async Task<DnsMessage> NextAnswerAsync(InMemoryLan.Endpoint client, string name)
    {
        while (true)
        {
            var response = await client.NextResponseAsync(Wait);
            // Only an answer to the question: an announcement also names it, among everything else.
            if (response.Answers.Count > 0
                && response.Answers.All(record => DnsMessage.SameName(record.Name, name))
                && response.Questions.Count == 0)
            {
                return response;
            }
        }
    }
}
