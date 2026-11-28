using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Gleanvolt.Plugins;

/// <summary>This machine's own addresses, for the waiting view and a kind's LAN search.</summary>
internal static class NetworkAddresses
{
    /// <summary>This machine's own IPv4 addresses on the interfaces that are up, loopback left out.</summary>
    internal static IReadOnlyList<IPAddress> OwnAddresses() =>
        [.. NetworkInterface.GetAllNetworkInterfaces()
            .Where(card => card.OperationalStatus == OperationalStatus.Up && card.NetworkInterfaceType != NetworkInterfaceType.Loopback)
            .SelectMany(card => card.GetIPProperties().UnicastAddresses)
            .Select(unicast => unicast.Address)
            .Where(address => address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(address))
            .Distinct()];
}
