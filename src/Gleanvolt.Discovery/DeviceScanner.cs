using System.Collections.Concurrent;
using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Modbus;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Discovery;

/// <summary>What to scan: the addresses, and how to describe them in the report.</summary>
/// <param name="Hosts">Every address to probe.</param>
/// <param name="Networks">The subnets or hosts they came from, as text.</param>
/// <param name="Warnings">Anything about how they were chosen, carried into the report.</param>
public sealed record ScanPlan(IReadOnlyList<string> Hosts, IReadOnlyList<string> Networks, IReadOnlyList<string> Warnings)
{
    /// <summary>Every host address in <paramref name="subnets"/>, once each.</summary>
    public static ScanPlan ForSubnets(IEnumerable<Ipv4Subnet> subnets, IReadOnlyList<string>? warnings = null)
    {
        var list = subnets.ToList();
        return new ScanPlan(
            [.. list.SelectMany(subnet => subnet.Hosts()).Select(address => address.ToString()).Distinct()],
            [.. list.Select(subnet => subnet.ToString())],
            warnings ?? []);
    }

    /// <summary>Exactly <paramref name="hosts"/>.</summary>
    public static ScanPlan ForHosts(IEnumerable<string> hosts)
    {
        var list = hosts.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        return new ScanPlan(list, list, []);
    }
}

/// <summary>
/// Finds the SolaX devices on a network (issue #267): a TCP connect to every host on the candidate
/// ports, then Modbus to whatever is open, then the serial number and the role check to say what each
/// box is. Read-only by construction: every client it speaks through is a
/// <see cref="ReadOnlyModbusClient"/>, whatever the factory hands it, so a scan of someone's LAN cannot
/// write to a device even by mistake.
/// </summary>
public sealed class DeviceScanner
{
    private readonly DiscoveryOptions _options;
    private readonly IPortProbe _portProbe;
    private readonly IWebProbe _webProbe;
    private readonly Func<DeviceConfig, IModbusClient> _clientFactory;
    private readonly TimeProvider _timeProvider;

    /// <param name="options">How to probe.</param>
    /// <param name="portProbe">The TCP connect; <see cref="TcpPortProbe"/> by default.</param>
    /// <param name="clientFactory">
    /// Builds the Modbus client for one host, port and unit id; a <see cref="ModbusTcpClient"/> with the
    /// scan's timeouts and no retries by default. Whatever it returns is wrapped read-only.
    /// </param>
    /// <param name="webProbe">The web page read on <see cref="DiscoveryOptions.WebPorts"/>; <see cref="HttpWebProbe"/> by default.</param>
    /// <param name="timeProvider">The clock requests are paced on.</param>
    public DeviceScanner(
        DiscoveryOptions? options = null,
        IPortProbe? portProbe = null,
        Func<DeviceConfig, IModbusClient>? clientFactory = null,
        IWebProbe? webProbe = null,
        TimeProvider? timeProvider = null)
    {
        _options = options ?? new DiscoveryOptions();
        _portProbe = portProbe ?? new TcpPortProbe();
        _webProbe = webProbe ?? new HttpWebProbe();
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientFactory = clientFactory ?? (device => new ModbusTcpClient(
            device, _timeProvider, _options.ConnectTimeout, _options.RequestTimeout, retries: 0));
    }

    /// <summary>
    /// Scans <paramref name="plan"/>. Cancelling stops the scan and returns what it had found so far,
    /// with <see cref="DiscoveryReport.Cancelled"/> set, rather than throwing it away.
    /// </summary>
    /// <param name="plan">What to scan.</param>
    /// <param name="found">Told of each result as it is found, from whichever thread found it.</param>
    /// <param name="cancellationToken">Stops the scan.</param>
    public async Task<DiscoveryReport> ScanAsync(
        ScanPlan plan,
        Action<DiscoveredDevice>? found = null,
        CancellationToken cancellationToken = default)
    {
        var results = new ConcurrentBag<DiscoveredDevice>();
        var warnings = new ConcurrentQueue<string>(plan.Warnings);
        var cancelled = false;

        void Report(DiscoveredDevice device)
        {
            results.Add(device);
            found?.Invoke(device);
        }

        try
        {
            var open = await FindOpenPortsAsync(plan.Hosts, cancellationToken).ConfigureAwait(false);

            await Parallel.ForEachAsync(
                open,
                new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrentHosts, CancellationToken = cancellationToken },
                async (host, token) => await ScanHostAsync(host.Key, host.Value, Report, warnings, token).ConfigureAwait(false))
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            cancelled = true;
        }

        return new DiscoveryReport(
            plan.Networks,
            plan.Hosts.Count,
            [.. results.OrderBy(device => SortKey(device.Host)).ThenBy(device => device.Host, StringComparer.Ordinal)
                .ThenBy(device => _options.Ports.ToList().IndexOf(device.Port)).ThenBy(device => device.UnitId)],
            [.. warnings],
            cancelled);
    }

    /// <summary>The client one unit id is spoken to through, made read-only here and nowhere else.</summary>
    internal IModbusClient OpenClient(string host, int port, byte unitId) =>
        new ReadOnlyModbusClient(
            _clientFactory(new DeviceConfig
            {
                Host = host,
                Port = port,
                UnitId = unitId,
                MinRequestInterval = _options.MinRequestInterval,
            }),
            NullLogger<ReadOnlyModbusClient>.Instance);

    // Every host and port pair at once, bounded, each closed as soon as it has answered.
    private async Task<IReadOnlyDictionary<string, List<int>>> FindOpenPortsAsync(
        IReadOnlyList<string> hosts,
        CancellationToken cancellationToken)
    {
        var open = new ConcurrentDictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
        var probes = hosts.SelectMany(host => _options.Ports.Select(port => (Host: host, Port: port)));

        await Parallel.ForEachAsync(
            probes,
            new ParallelOptions { MaxDegreeOfParallelism = _options.MaxConcurrency, CancellationToken = cancellationToken },
            async (probe, token) =>
            {
                if (await _portProbe.IsOpenAsync(probe.Host, probe.Port, _options.ConnectTimeout, token).ConfigureAwait(false))
                {
                    var ports = open.GetOrAdd(probe.Host, _ => []);

                    lock (ports)
                    {
                        ports.Add(probe.Port);
                    }
                }
            }).ConfigureAwait(false);

        return open;
    }

    private async Task ScanHostAsync(
        string host,
        List<int> openPorts,
        Action<DiscoveredDevice> report,
        ConcurrentQueue<string> warnings,
        CancellationToken cancellationToken)
    {
        using var hostTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        hostTimeout.CancelAfter(_options.HostTimeout);

        // One pacer for the host, not per port: a bridge on 8899 and the device's own 502 can be the
        // same box.
        var pacer = new HostPacer(_options.MinRequestInterval, _timeProvider);
        var identifier = new ModbusIdentifier(_options, OpenClient);

        try
        {
            foreach (var port in _options.Ports.Where(openPorts.Contains))
            {
                if (_options.WebPorts.Contains(port))
                {
                    var page = await _webProbe.ReadAsync(host, port, _options.RequestTimeout, hostTimeout.Token)
                        .ConfigureAwait(false);

                    if (SolaxWebPage.IsDongle(page))
                    {
                        report(new DiscoveredDevice(
                            host, port, null, DiscoveryFinding.SolaxDongle, null, "SolaX dongle", null, null, null, null,
                            "Looks like a SolaX Wi-Fi/LAN dongle: no Modbus here. Gleanvolt needs Modbus TCP: the "
                            + "inverter's own LAN port, or a Pocket WiFi 3.0+ with Modbus TCP enabled."));
                    }

                    continue;
                }

                foreach (var device in await identifier.IdentifyAsync(host, port, pacer, hostTimeout.Token).ConfigureAwait(false))
                {
                    report(device);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            warnings.Enqueue(
                $"{host} ran out of time after {_options.HostTimeout.TotalSeconds:0} s; its results may be incomplete. "
                + "Raise --host-timeout, or narrow --unit-ids.");
        }
    }

    private static long SortKey(string host) =>
        IPAddress.TryParse(host, out var address) && address.GetAddressBytes() is { Length: 4 } bytes
            ? ((long)bytes[0] << 24) | ((long)bytes[1] << 16) | ((long)bytes[2] << 8) | bytes[3]
            : long.MaxValue;
}
