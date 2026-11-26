using System.Net;
using Gleanvolt.Infrastructure.Mdns;
using Gleanvolt.Providers.Client;

namespace Gleanvolt.Providers.Tests;

/// <summary>The SDK's own reading of the answer Gleanvolt's responder gives to a browse for <c>_gleanvolt._tcp</c>.</summary>
public sealed class MdnsBrowserTests
{
    private static byte[] GleanvoltAnswers(ushort port = 8090)
    {
        // What MdnsResponder sends a legacy (unicast) PTR query: the pointer, then the service and the address.
        var response = DnsMessage.Response(0x1234);
        response.Answers.Add(DnsRecord.Pointer("_gleanvolt._tcp.local", "Gleanvolt._gleanvolt._tcp.local", 10));
        response.Additionals.Add(DnsRecord.Service("Gleanvolt._gleanvolt._tcp.local", port, "gleanvolt.local", 10));
        response.Additionals.Add(DnsRecord.Text("Gleanvolt._gleanvolt._tcp.local", ["path=/", "setup=done"], 10));
        response.Additionals.Add(DnsRecord.Address("gleanvolt.local", IPAddress.Parse("192.168.1.5"), 10));
        return response.Encode();
    }

    [Fact]
    public void ParsesTheServiceGleanvoltAdvertises()
    {
        var found = MdnsBrowser.Parse(GleanvoltAnswers());

        Assert.Equal([new Uri("http://192.168.1.5:8090/")], found);
    }

    [Fact]
    public void AnotherServiceIsNotGleanvolt()
    {
        var response = DnsMessage.Response();
        response.Answers.Add(DnsRecord.Pointer("_http._tcp.local", "Printer._http._tcp.local", 10));
        response.Additionals.Add(DnsRecord.Service("Printer._http._tcp.local", 80, "printer.local", 10));
        response.Additionals.Add(DnsRecord.Address("printer.local", IPAddress.Parse("192.168.1.9"), 10));

        Assert.Empty(MdnsBrowser.Parse(response.Encode()));
    }

    [Fact]
    public void AMalformedAnswerIsNothing()
    {
        var answers = GleanvoltAnswers();

        for (var length = 0; length < answers.Length; length++)
        {
            // Every truncation is a LAN packet we did not ask for; none may throw.
            _ = MdnsBrowser.Parse(answers.AsSpan(0, length));
        }

        Assert.Empty(MdnsBrowser.Parse([0, 0, 0x84, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0xC0, 12]));
    }
}
