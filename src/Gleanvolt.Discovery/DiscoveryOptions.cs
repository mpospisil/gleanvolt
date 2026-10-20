namespace Gleanvolt.Discovery;

/// <summary>How a scan probes: which ports and unit ids, how fast, and how long it waits.</summary>
public sealed record DiscoveryOptions
{
    /// <summary>
    /// The candidate ports, in the order a host's are tried. <c>502</c> is Modbus TCP's default and
    /// what <c>DeviceConfig.Port</c> defaults to; <c>8899</c> is the common default of the RS485 to
    /// Wi-Fi/Ethernet bridges people put in front of a SolaX (Elfin EW11 and similar); <c>80</c> is
    /// the SolaX dongle's web page, which is not Modbus but is the most common reason an owner finds
    /// none. Confirm against real hardware before relying on any of them; the list is meant to grow
    /// from what the scan finds in the field.
    /// </summary>
    public IReadOnlyList<int> Ports { get; init; } = [502, 8899, 80];

    /// <summary>Ports that are probed as a web page rather than spoken Modbus to.</summary>
    public IReadOnlyList<int> WebPorts { get; init; } = [80];

    /// <summary>
    /// The unit ids tried on each open Modbus port, in order. <c>1</c> first because it is what both
    /// SolaX devices default to and what the reference installation uses.
    /// </summary>
    public IReadOnlyList<byte> UnitIds { get; init; } = [1, 0, 2, 3, 4, 5, 247];

    /// <summary>
    /// Try every unit id even after one has answered: an inverter and a charger on one RS485 bus behind
    /// one bridge share a host and port, and differ only here.
    /// </summary>
    public bool AllUnitIds { get; init; }

    /// <summary>How long one TCP connect may take — the port probe and each Modbus connection.</summary>
    public TimeSpan ConnectTimeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How long one Modbus request may wait for its reply.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// How long one host may take altogether, across its ports and unit ids. A host that runs out is
    /// reported with what it had answered so far.
    /// </summary>
    public TimeSpan HostTimeout { get; init; } = TimeSpan.FromSeconds(45);

    /// <summary>
    /// The gap between two Modbus requests to one host — <c>DeviceConfig.MinRequestInterval</c>'s
    /// default, for the same reason. The scan is parallel across hosts and never within one.
    /// </summary>
    public TimeSpan MinRequestInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>How many port probes are in flight at once.</summary>
    public int MaxConcurrency { get; init; } = 64;

    /// <summary>How many hosts are spoken Modbus to at once.</summary>
    public int MaxConcurrentHosts { get; init; } = 16;
}
