using System.Net;
using System.Threading.Channels;
using Gleanvolt.Infrastructure.Mdns;

namespace Gleanvolt.Infrastructure.Tests.Mdns;

/// <summary>
/// A LAN in memory: every endpoint is one host on interface 1. Multicast reaches every endpoint,
/// the sender included — loopback is on, as on a real host — and unicast reaches the one endpoint
/// with that address and port.
/// </summary>
internal sealed class InMemoryLan
{
    private readonly List<Endpoint> _endpoints = [];

    /// <summary>Everything multicast so far, for asserting on what was announced.</summary>
    public List<DnsMessage> Multicast { get; } = [];

    public Endpoint Join(string address, int port = UdpMdnsTransport.MdnsPort)
    {
        var endpoint = new Endpoint(this, new IPEndPoint(IPAddress.Parse(address), port));
        lock (_endpoints)
        {
            _endpoints.Add(endpoint);
        }

        return endpoint;
    }

    private void DeliverMulticast(byte[] data, IPEndPoint from)
    {
        lock (_endpoints)
        {
            if (DnsMessage.TryParse(data) is { } message)
            {
                lock (Multicast)
                {
                    Multicast.Add(message);
                }
            }

            foreach (var endpoint in _endpoints.Where(endpoint => endpoint.Address.Port == UdpMdnsTransport.MdnsPort))
            {
                endpoint.Inbox.Writer.TryWrite(new MdnsDatagram(data, from, 1));
            }
        }
    }

    private void DeliverUnicast(byte[] data, IPEndPoint from, IPEndPoint to)
    {
        lock (_endpoints)
        {
            foreach (var endpoint in _endpoints.Where(endpoint => endpoint.Address.Equals(to)))
            {
                endpoint.Inbox.Writer.TryWrite(new MdnsDatagram(data, from, 1));
            }
        }
    }

    internal sealed class Endpoint(InMemoryLan lan, IPEndPoint address) : IMdnsTransport
    {
        public IPEndPoint Address { get; } = address;

        public Channel<MdnsDatagram> Inbox { get; } = Channel.CreateUnbounded<MdnsDatagram>();

        public IReadOnlyList<MdnsInterface> Interfaces() => [new MdnsInterface(1, "lan0", [Address.Address])];

        public IReadOnlyList<IPAddress> AddressesOf(int interfaceIndex) => interfaceIndex == 1 ? [Address.Address] : [];

        public ValueTask SendMulticastAsync(ReadOnlyMemory<byte> data, int interfaceIndex, CancellationToken cancellationToken)
        {
            lan.DeliverMulticast(data.ToArray(), Address);
            return ValueTask.CompletedTask;
        }

        public ValueTask SendUnicastAsync(ReadOnlyMemory<byte> data, IPEndPoint destination, CancellationToken cancellationToken)
        {
            lan.DeliverUnicast(data.ToArray(), Address, destination);
            return ValueTask.CompletedTask;
        }

        public ValueTask<MdnsDatagram> ReceiveAsync(CancellationToken cancellationToken) =>
            Inbox.Reader.ReadAsync(cancellationToken);

        /// <summary>Waits for the next datagram that parses as a response, for a test client.</summary>
        public async Task<DnsMessage> NextResponseAsync(TimeSpan timeout)
        {
            using var cancellation = new CancellationTokenSource(timeout);
            while (true)
            {
                var datagram = await Inbox.Reader.ReadAsync(cancellation.Token);
                if (DnsMessage.TryParse(datagram.Data) is { IsResponse: true } message)
                {
                    return message;
                }
            }
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}
