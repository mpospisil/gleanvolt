using System.Buffers.Binary;
using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Sockets;

namespace Gleanvolt.Discovery;

/// <summary>An IPv4 network in CIDR form, and the host addresses in it.</summary>
public readonly record struct Ipv4Subnet
{
    private Ipv4Subnet(uint network, int prefixLength)
    {
        Network = network;
        PrefixLength = prefixLength;
    }

    /// <summary>The network address, host order.</summary>
    public uint Network { get; }

    /// <summary>The prefix length, 0–32.</summary>
    public int PrefixLength { get; }

    /// <summary>The subnet of <paramref name="prefixLength"/> bits that <paramref name="address"/> is in.</summary>
    public static Ipv4Subnet Containing(IPAddress address, int prefixLength)
    {
        if (address.AddressFamily != AddressFamily.InterNetwork)
        {
            throw new ArgumentException($"{address} is not an IPv4 address.", nameof(address));
        }

        ArgumentOutOfRangeException.ThrowIfLessThan(prefixLength, 0);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prefixLength, 32);

        return new Ipv4Subnet(ToUInt32(address) & Mask(prefixLength), prefixLength);
    }

    /// <summary>Parses <c>192.168.1.0/24</c>, or a bare address as a <c>/32</c>.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out Ipv4Subnet? subnet)
    {
        subnet = null;

        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var parts = text.Trim().Split('/');
        var prefixLength = 32;

        if (parts.Length > 2
            || !IPAddress.TryParse(parts[0], out var address)
            || address.AddressFamily != AddressFamily.InterNetwork
            || parts[0].Count(character => character == '.') != 3
            || (parts.Length == 2 && (!int.TryParse(parts[1], out prefixLength) || prefixLength is < 0 or > 32)))
        {
            return false;
        }

        subnet = Containing(address, prefixLength);
        return true;
    }

    /// <summary>How many addresses <see cref="Hosts"/> yields.</summary>
    public long HostCount => PrefixLength switch
    {
        32 => 1,
        31 => 2,
        _ => (1L << (32 - PrefixLength)) - 2,
    };

    /// <summary>
    /// The host addresses, without the network and broadcast addresses — except on a <c>/31</c> and a
    /// <c>/32</c>, which have none to spare.
    /// </summary>
    public IEnumerable<IPAddress> Hosts()
    {
        var size = 1L << (32 - PrefixLength);
        var (first, last) = PrefixLength >= 31 ? (0L, size - 1) : (1L, size - 2);

        for (var offset = first; offset <= last; offset++)
        {
            yield return FromUInt32((uint)(Network + offset));
        }
    }

    /// <summary>Whether <paramref name="address"/> is in this subnet.</summary>
    public bool Contains(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork && (ToUInt32(address) & Mask(PrefixLength)) == Network;

    /// <summary>Whether <paramref name="address"/> is in RFC 1918 private space: 10/8, 172.16/12, 192.168/16.</summary>
    public static bool IsPrivate(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetwork
        && (Containing(IPAddress.Parse("10.0.0.0"), 8).Contains(address)
            || Containing(IPAddress.Parse("172.16.0.0"), 12).Contains(address)
            || Containing(IPAddress.Parse("192.168.0.0"), 16).Contains(address));

    public override string ToString() => $"{FromUInt32(Network)}/{PrefixLength}";

    private static uint Mask(int prefixLength) => prefixLength == 0 ? 0u : uint.MaxValue << (32 - prefixLength);

    private static uint ToUInt32(IPAddress address) => BinaryPrimitives.ReadUInt32BigEndian(address.GetAddressBytes());

    private static IPAddress FromUInt32(uint value)
    {
        var bytes = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(bytes, value);
        return new IPAddress(bytes);
    }
}
