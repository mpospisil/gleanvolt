using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Gleanvolt.Chargers;

/// <summary>A device the search found.</summary>
/// <param name="Address">Where it answered.</param>
/// <param name="Description">What it said it is, as the plugin's probe words it.</param>
public sealed record FoundCharger(DeviceAddress Address, string Description);

/// <summary>
/// The plugin's own search for its charger: every address of a private network (this machine's own /24s, or one
/// the owner types, no larger than a /22) tried on the charger's port, and each one that accepts a connection asked
/// by the plugin's probe whether it is this charger. Reads only: a probe never writes.
/// </summary>
public static class LanScan
{
    /// <summary>The widest network the search takes: a /22, 1 022 addresses.</summary>
    public const int WidestPrefix = 22;

    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromMilliseconds(400);

    /// <summary>This machine's own private IPv4 networks, as /24s, e.g. <c>192.168.1.0/24</c>.</summary>
    public static IReadOnlyList<string> OwnNetworks() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(card => card.OperationalStatus == OperationalStatus.Up && card.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(card => card.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && IsPrivate(address))
            .Select(address => $"{address.GetAddressBytes()[0]}.{address.GetAddressBytes()[1]}.{address.GetAddressBytes()[2]}.0/24")
            .Distinct()];

    /// <summary>The hosts of <paramref name="network"/>, or null when it is not a private network of a /22 or smaller.</summary>
    public static IReadOnlyList<IPAddress>? Hosts(string network)
    {
        var parts = network.Trim().Split('/');
        if (parts.Length != 2 || !IPAddress.TryParse(parts[0], out var start) || start.AddressFamily != AddressFamily.InterNetwork
            || !int.TryParse(parts[1], out var prefix) || prefix is < WidestPrefix or > 30 || !IsPrivate(start))
        {
            return null;
        }

        var bytes = start.GetAddressBytes();
        var value = (uint)(bytes[0] << 24 | bytes[1] << 16 | bytes[2] << 8 | bytes[3]);
        var mask = uint.MaxValue << (32 - prefix);
        var first = value & mask;
        var count = 1u << (32 - prefix);

        return [.. Enumerable.Range(1, (int)count - 2).Select(offset =>
        {
            var host = first + (uint)offset;
            return new IPAddress([(byte)(host >> 24), (byte)(host >> 16), (byte)(host >> 8), (byte)host]);
        })];
    }

    /// <summary>
    /// Searches <paramref name="hosts"/> on <paramref name="port"/>, at most 64 at a time, and asks
    /// <paramref name="probe"/> about each that answers; its description, or null for "not this charger".
    /// </summary>
    public static async Task<IReadOnlyList<FoundCharger>> SearchAsync(
        IReadOnlyList<IPAddress> hosts,
        int port,
        Func<DeviceAddress, CancellationToken, Task<string?>> probe,
        CancellationToken cancellationToken)
    {
        var found = new List<FoundCharger>();
        var gate = new Lock();

        await Parallel.ForEachAsync(
            hosts,
            new ParallelOptions { MaxDegreeOfParallelism = 64, CancellationToken = cancellationToken },
            async (host, token) =>
            {
                if (!await AcceptsAsync(host, port, token).ConfigureAwait(false))
                {
                    return;
                }

                var address = new DeviceAddress(host.ToString(), port);
                string? description;
                try
                {
                    description = await probe(address, token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    description = null;
                }

                if (description is not null)
                {
                    lock (gate)
                    {
                        found.Add(new FoundCharger(address, description));
                    }
                }
            }).ConfigureAwait(false);

        return [.. found.OrderBy(charger => charger.Address.Host, StringComparer.Ordinal)];
    }

    private static async Task<bool> AcceptsAsync(IPAddress host, int port, CancellationToken cancellationToken)
    {
        using var client = new TcpClient();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ConnectTimeout);

        try
        {
            await client.ConnectAsync(host, port, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception ex) when (ex is SocketException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private static bool IsPrivate(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10
            || (bytes[0] == 172 && bytes[1] is >= 16 and <= 31)
            || (bytes[0] == 192 && bytes[1] == 168);
    }
}
