using System.Net;
using Gleanvolt.Infrastructure.Mdns;

namespace Gleanvolt.Infrastructure.Tests.Mdns;

/// <summary>The DNS wire format the responder writes, and what it reads from other responders.</summary>
public class DnsMessageTests
{
    [Fact]
    public void Every_record_type_survives_a_round_trip()
    {
        var message = DnsMessage.Response(id: 7);
        message.Questions.Add(new DnsQuestion("gleanvolt.local", DnsType.Any, UnicastResponse: true));
        message.Answers.Add(DnsRecord.Address("gleanvolt.local", IPAddress.Parse("192.168.1.20"), 120));
        message.Answers.Add(DnsRecord.Address("gleanvolt.local", IPAddress.Parse("fe80::1"), 120));
        message.Answers.Add(DnsRecord.Pointer("_http._tcp.local", "Gleanvolt._http._tcp.local", 4500));
        message.Additionals.Add(DnsRecord.Service("Gleanvolt._http._tcp.local", 8090, "gleanvolt.local", 120));
        message.Additionals.Add(DnsRecord.Text("Gleanvolt._http._tcp.local", ["path=/", "setup=done"], 4500));

        var parsed = DnsMessage.TryParse(message.Encode());

        Assert.NotNull(parsed);
        Assert.True(parsed.IsResponse);
        Assert.Equal(7, parsed.Id);
        Assert.Equal(message.Questions, parsed.Questions);
        Assert.Equal(message.Answers, parsed.Answers);
        Assert.Equal(message.Additionals, parsed.Additionals);
        Assert.Equal(DnsType.Aaaa, parsed.Answers[1].Type);
    }

    [Fact]
    public void A_query_has_no_flags_and_marks_a_unicast_question_in_the_class()
    {
        var bytes = DnsMessage.Query(new DnsQuestion("gleanvolt.local", DnsType.A, UnicastResponse: true)).Encode();

        Assert.Equal([0, 0, 0, 0], bytes[..4]);          // id 0, flags 0 (RFC 6762 §18)
        Assert.Equal([0x80, 0x01], bytes[^2..]);          // class IN with the QU bit
    }

    [Fact]
    public void Compressed_names_from_other_responders_are_read()
    {
        // A response with one PTR record whose owner and target both point back into the question.
        byte[] bytes =
        [
            0, 0, 0x84, 0, 0, 1, 0, 1, 0, 0, 0, 0,
            // question: _http._tcp.local PTR IN
            5, (byte)'_', (byte)'h', (byte)'t', (byte)'t', (byte)'p', 4, (byte)'_', (byte)'t', (byte)'c', (byte)'p',
            5, (byte)'l', (byte)'o', (byte)'c', (byte)'a', (byte)'l', 0, 0, 12, 0, 1,
            // answer: name -> offset 12, PTR, IN, TTL 4500, rdata "Printer" + pointer to offset 12
            0xC0, 12, 0, 12, 0, 1, 0, 0, 0x11, 0x94, 0, 10,
            7, (byte)'P', (byte)'r', (byte)'i', (byte)'n', (byte)'t', (byte)'e', (byte)'r', 0xC0, 12,
        ];

        var parsed = DnsMessage.TryParse(bytes);

        Assert.NotNull(parsed);
        var record = Assert.Single(parsed.Answers);
        Assert.Equal("_http._tcp.local", record.Name);
        Assert.Equal(new PtrRData("Printer._http._tcp.local"), record.Data);
        Assert.Equal(4500u, record.Ttl);
    }

    [Theory]
    [InlineData(new byte[] { 0, 0 })]                                            // shorter than a header
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 5, (byte)'a' })] // a label that runs out
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 1, 0, 0, 0, 0, 0, 0, 0xC0, 12 })]    // a pointer to itself
    public void Malformed_input_is_ignored_rather_than_thrown(byte[] bytes)
    {
        Assert.Null(DnsMessage.TryParse(bytes));
    }

    [Fact]
    public void Names_compare_without_case_or_a_trailing_dot()
    {
        Assert.True(DnsMessage.SameName("GleanVolt.local.", "gleanvolt.local"));
        Assert.False(DnsMessage.SameName("gleanvolt-2.local", "gleanvolt.local"));
    }
}
