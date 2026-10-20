using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Gleanvolt.Discovery;

/// <summary>One IPv4 address on one of this machine's interfaces.</summary>
/// <param name="Name">The interface's name, e.g. <c>eth0</c> or <c>docker0</c>.</param>
/// <param name="Address">The address.</param>
/// <param name="PrefixLength">Its subnet's prefix length.</param>
/// <param name="IsUp">Whether the interface is up.</param>
/// <param name="IsLoopback">Whether it is the loopback interface.</param>
public sealed record InterfaceAddress(string Name, IPAddress Address, int PrefixLength, bool IsUp, bool IsLoopback);

/// <summary>What a default scan covers, and anything about that choice a human should know.</summary>
public sealed record NetworkSelection(IReadOnlyList<Ipv4Subnet> Subnets, IReadOnlyList<string> Warnings)
{
    /// <summary>
    /// The widest subnet a default scan covers: 1,022 hosts. A misconfigured <c>/16</c> would otherwise
    /// be 65,000 hosts and an hour of connects; the owner can still ask for it with <c>--subnet</c>.
    /// </summary>
    public const int WidestDefaultPrefix = 22;

    // Interfaces a container runtime or a hypervisor creates on the host. Their subnets hold
    // containers and VMs, never the inverter.
    private static readonly string[] VirtualBridgePrefixes = ["docker", "br-", "veth", "virbr", "cni", "flannel", "cali", "podman"];

    /// <summary>
    /// Every IPv4 interface that is up, not loopback, and has a private address, each as its own
    /// subnet capped at a <see cref="WidestDefaultPrefix"/>.
    /// </summary>
    /// <param name="interfaces">This machine's addresses (see <see cref="LocalInterfaces"/>).</param>
    /// <param name="inContainer">Whether this process runs in a container (see <see cref="RunningInContainer"/>).</param>
    public static NetworkSelection FromInterfaces(IEnumerable<InterfaceAddress> interfaces, bool inContainer)
    {
        var subnets = new List<Ipv4Subnet>();
        var warnings = new List<string>();

        foreach (var entry in interfaces)
        {
            if (!entry.IsUp || entry.IsLoopback || entry.Address.AddressFamily != AddressFamily.InterNetwork
                || !Ipv4Subnet.IsPrivate(entry.Address))
            {
                continue;
            }

            var actual = Ipv4Subnet.Containing(entry.Address, entry.PrefixLength);

            if (VirtualBridgePrefixes.Any(prefix => entry.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
            {
                warnings.Add($"Skipped {entry.Name} ({actual}): a container or VM bridge, not your LAN.");
                continue;
            }

            var subnet = actual;

            if (entry.PrefixLength < WidestDefaultPrefix)
            {
                subnet = Ipv4Subnet.Containing(entry.Address, WidestDefaultPrefix);
                warnings.Add(
                    $"{entry.Name} is on {actual}, which is {actual.HostCount:N0} hosts; scanning only {subnet}, "
                    + $"the /{WidestDefaultPrefix} around this machine. Pass --subnet {actual} to scan all of it.");
            }

            if (inContainer)
            {
                warnings.Add(
                    $"This looks like a container, so {subnet} on {entry.Name} is most likely the container's own "
                    + "network rather than your LAN, and the devices will not be on it. Pass --subnet with your "
                    + "LAN's range (e.g. --subnet 192.168.1.0/24), and run the container with --network host if "
                    + "it cannot reach the LAN at all.");
            }

            if (!subnets.Contains(subnet))
            {
                subnets.Add(subnet);
            }
        }

        if (subnets.Count == 0)
        {
            warnings.Add("Found no network interface with a private IPv4 address to scan. Pass --subnet or --host.");
        }

        return new NetworkSelection(subnets, warnings);
    }

    /// <summary>This machine's IPv4 addresses.</summary>
    public static IEnumerable<InterfaceAddress> LocalInterfaces()
    {
        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            IPInterfaceProperties properties;

            try
            {
                properties = networkInterface.GetIPProperties();
            }
            catch (NetworkInformationException)
            {
                continue;
            }

            foreach (var unicast in properties.UnicastAddresses)
            {
                if (unicast.Address.AddressFamily == AddressFamily.InterNetwork)
                {
                    yield return new InterfaceAddress(
                        networkInterface.Name,
                        unicast.Address,
                        unicast.PrefixLength,
                        networkInterface.OperationalStatus == OperationalStatus.Up,
                        networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback);
                }
            }
        }
    }

    /// <summary>
    /// Whether this process looks like it runs in a container: Docker's marker file, or the variable
    /// Microsoft's .NET images set.
    /// </summary>
    public static bool RunningInContainer() =>
        File.Exists("/.dockerenv")
        || string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);
}
