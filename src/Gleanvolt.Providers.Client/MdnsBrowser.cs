using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Gleanvolt.Providers.Client;

/// <summary>
/// Asks the LAN once for Gleanvolt's <c>_gleanvolt._tcp</c> service (#459), which its web advertiser announces.
/// A few dozen lines of DNS of its own rather than Gleanvolt.Infrastructure's: the SDK is MIT and must not
/// depend on the application.
///
/// <para>The query goes from an ephemeral port, so it is a legacy unicast query (RFC 6762 §6.7): responders
/// answer this socket directly, and it never has to share UDP 5353 with avahi or Gleanvolt itself.</para>
/// </summary>
internal static class MdnsBrowser
{
    /// <summary>How long a browse listens for answers.</summary>
    internal static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    private const string Service = "_gleanvolt._tcp.local";
    private const string InstanceSuffix = "." + Service;
    private const ushort TypeA = 1;
    private const ushort TypePtr = 12;
    private const ushort TypeSrv = 33;
    private const ushort ResponseFlag = 0x8000;
    private static readonly IPEndPoint Group = new(IPAddress.Parse("224.0.0.251"), 5353);

    /// <summary>Gleanvolt's addresses as the LAN answers within <paramref name="timeout"/>; empty when nothing does.</summary>
    public static async Task<IReadOnlyList<Uri>> BrowseAsync(TimeSpan timeout, CancellationToken cancellationToken)
    {
        var found = new List<Uri>();

        try
        {
            using var socket = new UdpClient(new IPEndPoint(IPAddress.Any, 0));
            await socket.SendAsync(Query((ushort)Random.Shared.Next(1, ushort.MaxValue)), Group, cancellationToken).ConfigureAwait(false);

            using var window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            window.CancelAfter(timeout);

            while (true)
            {
                UdpReceiveResult datagram;
                try
                {
                    datagram = await socket.ReceiveAsync(window.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    break;
                }

                foreach (var address in Parse(datagram.Buffer).Where(address => !found.Contains(address)))
                {
                    found.Add(address);
                }
            }
        }
        catch (SocketException)
        {
            // No multicast route (a container on a bridge network, a machine with no LAN): nothing found, and the
            // configured address and localhost were tried before this anyway.
        }

        return found;
    }

    /// <summary>
    /// The addresses in one answer: each <c>_gleanvolt._tcp</c> service's port on each IPv4 address of its
    /// target, from the answers and the additional records alike. Anything malformed is no address: the LAN is
    /// not a trusted input.
    /// </summary>
    internal static IReadOnlyList<Uri> Parse(ReadOnlySpan<byte> response)
    {
        try
        {
            return ParseCore(response.ToArray());
        }
        catch (FormatException)
        {
            return [];
        }
    }

    private static List<Uri> ParseCore(byte[] data)
    {
        var reader = new Reader(data);
        var flags = reader.UInt16At(2);
        if ((flags & ResponseFlag) == 0)
        {
            return [];
        }

        var questions = reader.UInt16At(4);
        var records = reader.UInt16At(6) + reader.UInt16At(8) + reader.UInt16At(10);
        var offset = 12;

        for (var i = 0; i < questions; i++)
        {
            reader.Name(ref offset);
            offset += 4;
        }

        var services = new List<(string Target, ushort Port)>();
        var addresses = new Dictionary<string, List<IPAddress>>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < records; i++)
        {
            var name = reader.Name(ref offset);
            var type = reader.UInt16At(offset);
            var ttl = reader.UInt32At(offset + 4);
            var length = reader.UInt16At(offset + 8);
            var start = offset + 10;
            offset = start + length;
            reader.Require(offset);

            // A TTL of 0 is a goodbye (RFC 6762 §10.1): that Gleanvolt is leaving.
            if (ttl == 0)
            {
                continue;
            }

            if (type == TypeSrv && name.EndsWith(InstanceSuffix, StringComparison.OrdinalIgnoreCase) && length >= 7)
            {
                var port = reader.UInt16At(start + 4);
                var targetAt = start + 6;
                var target = reader.Name(ref targetAt);
                if (port != 0)
                {
                    services.Add((target, port));
                }
            }
            else if (type == TypeA && length == 4)
            {
                if (!addresses.TryGetValue(name, out var list))
                {
                    addresses[name] = list = [];
                }

                list.Add(new IPAddress(data.AsSpan(start, 4)));
            }
        }

        var found = new List<Uri>();
        foreach (var (target, port) in services)
        {
            foreach (var address in addresses.GetValueOrDefault(target) ?? [])
            {
                var uri = new UriBuilder(Uri.UriSchemeHttp, address.ToString(), port).Uri;
                if (!found.Contains(uri))
                {
                    found.Add(uri);
                }
            }
        }

        return found;
    }

    // A PTR question for the service, class IN with the QU bit clear: the answer comes back to this socket anyway,
    // since a query from a port other than 5353 is answered by unicast.
    private static byte[] Query(ushort id)
    {
        var bytes = new List<byte>(64);
        void Add16(ushort value)
        {
            bytes.Add((byte)(value >> 8));
            bytes.Add((byte)value);
        }

        Add16(id);
        Add16(0);
        Add16(1);
        Add16(0);
        Add16(0);
        Add16(0);
        foreach (var label in Service.Split('.'))
        {
            bytes.Add((byte)label.Length);
            bytes.AddRange(Encoding.ASCII.GetBytes(label));
        }

        bytes.Add(0);
        Add16(TypePtr);
        Add16(1);
        return [.. bytes];
    }

    private readonly struct Reader(byte[] data)
    {
        public void Require(int end)
        {
            if (end > data.Length)
            {
                throw new FormatException("The answer is shorter than it says.");
            }
        }

        public ushort UInt16At(int offset)
        {
            Require(offset + 2);
            return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2));
        }

        public uint UInt32At(int offset)
        {
            Require(offset + 4);
            return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4));
        }

        // A name, following compression pointers (RFC 1035 §4.1.4). Bounded, so a pointer loop is malformed input
        // rather than a hang.
        public string Name(ref int offset)
        {
            var labels = new List<string>();
            var position = offset;
            var jumps = 0;
            int? after = null;

            while (true)
            {
                Require(position + 1);
                var length = data[position];

                if (length == 0)
                {
                    position++;
                    break;
                }

                if ((length & 0xC0) == 0xC0)
                {
                    if (++jumps > 16)
                    {
                        throw new FormatException("Too many compression pointers.");
                    }

                    Require(position + 2);
                    after ??= position + 2;
                    position = ((length & 0x3F) << 8) | data[position + 1];
                    continue;
                }

                if ((length & 0xC0) != 0 || labels.Count > 127)
                {
                    throw new FormatException("Not a name.");
                }

                Require(position + 1 + length);
                labels.Add(Encoding.UTF8.GetString(data, position + 1, length));
                position += 1 + length;
            }

            offset = after ?? position;
            return string.Join('.', labels);
        }
    }
}
