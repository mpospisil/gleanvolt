using System.Net;

namespace Gleanvolt.Discovery.Tests;

public class NetworkSelectionTests
{
    private static InterfaceAddress Interface(string name, string address, int prefix, bool up = true, bool loopback = false) =>
        new(name, IPAddress.Parse(address), prefix, up, loopback);

    [Fact]
    public void Each_private_interface_that_is_up_is_scanned_as_its_own_subnet()
    {
        var selection = NetworkSelection.FromInterfaces(
        [
            Interface("lo", "127.0.0.1", 8, loopback: true),
            Interface("eth0", "192.168.1.23", 24),
            Interface("wlan0", "10.0.5.7", 24),
            Interface("eth1", "192.168.9.1", 24, up: false),
            Interface("wan", "84.42.1.10", 24),
        ], inContainer: false);

        Assert.Equal(["192.168.1.0/24", "10.0.5.0/24"], selection.Subnets.Select(subnet => subnet.ToString()));
        Assert.Empty(selection.Warnings);
    }

    [Fact]
    public void A_subnet_wider_than_a_slash_22_is_capped_around_this_machine_and_says_so()
    {
        var selection = NetworkSelection.FromInterfaces([Interface("eth0", "172.20.130.5", 16)], inContainer: false);

        Assert.Equal("172.20.128.0/22", Assert.Single(selection.Subnets).ToString());
        Assert.Contains(selection.Warnings, warning => warning.Contains("--subnet 172.20.0.0/16", StringComparison.Ordinal));
    }

    [Fact]
    public void A_docker_bridge_on_the_host_is_skipped_and_named()
    {
        var selection = NetworkSelection.FromInterfaces(
        [
            Interface("docker0", "172.17.0.1", 16),
            Interface("br-3f2a9c", "172.18.0.1", 16),
            Interface("eth0", "192.168.1.23", 24),
        ], inContainer: false);

        Assert.Equal("192.168.1.0/24", Assert.Single(selection.Subnets).ToString());
        Assert.Contains(selection.Warnings, warning => warning.Contains("docker0", StringComparison.Ordinal));
    }

    [Fact]
    public void Inside_a_container_the_default_subnet_is_flagged_as_the_containers_own()
    {
        var selection = NetworkSelection.FromInterfaces([Interface("eth0", "172.18.0.4", 16)], inContainer: true);

        Assert.Contains(selection.Warnings, warning => warning.Contains("--subnet", StringComparison.Ordinal)
            && warning.Contains("container", StringComparison.Ordinal));
    }

    [Fact]
    public void No_interface_to_scan_says_what_to_pass_instead()
    {
        var selection = NetworkSelection.FromInterfaces([Interface("lo", "127.0.0.1", 8, loopback: true)], inContainer: false);

        Assert.Empty(selection.Subnets);
        Assert.Contains(selection.Warnings, warning => warning.Contains("--host", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("192.168.1.0/24", "192.168.1.0/24", 254)]
    [InlineData("192.168.1.77/24", "192.168.1.0/24", 254)]
    [InlineData("192.168.1.50", "192.168.1.50/32", 1)]
    [InlineData("10.0.0.0/31", "10.0.0.0/31", 2)]
    [InlineData("10.0.0.0/22", "10.0.0.0/22", 1022)]
    public void A_subnet_parses_and_counts_its_hosts(string text, string normalised, long hosts)
    {
        Assert.True(Ipv4Subnet.TryParse(text, out var subnet));
        Assert.Equal(normalised, subnet.Value.ToString());
        Assert.Equal(hosts, subnet.Value.HostCount);
        Assert.Equal(hosts, subnet.Value.Hosts().LongCount());
    }

    [Theory]
    [InlineData("")]
    [InlineData("192.168.1.0/33")]
    [InlineData("192.168.1/24")]
    [InlineData("fe80::1/64")]
    [InlineData("router.local")]
    public void Anything_else_is_not_a_subnet(string text)
    {
        Assert.False(Ipv4Subnet.TryParse(text, out _));
    }

    [Fact]
    public void Hosts_leave_out_the_network_and_broadcast_addresses()
    {
        Assert.True(Ipv4Subnet.TryParse("192.168.1.0/30", out var subnet));

        Assert.Equal(["192.168.1.1", "192.168.1.2"], subnet.Value.Hosts().Select(address => address.ToString()));
    }
}
