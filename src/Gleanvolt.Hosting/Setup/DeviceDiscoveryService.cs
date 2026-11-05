using System.Collections.Concurrent;
using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Discovery;

namespace Gleanvolt.Hosting.Setup;

/// <summary>
/// <see cref="IDeviceDiscovery"/> over the same <see cref="DeviceScanner"/> the command-line tool uses
/// (#302): one scanner, one set of identification rules, wherever the scan is started from.
///
/// <para><b>What may be scanned.</b> Private IPv4 only, and nothing wider than a /22 -- the same bounds
/// the scanner chooses for itself. The page that asks is behind the sign-in, but a signed-in owner
/// still has no reason to point this at someone else's network.</para>
/// </summary>
public sealed class DeviceDiscoveryService : IDeviceDiscovery, IDisposable
{
    private readonly Func<IPortProbe, DeviceScanner> _scanner;
    private readonly IPortProbe _probe;
    private readonly Func<IEnumerable<InterfaceAddress>> _interfaces;
    private readonly bool _inContainer;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceDiscoveryService> _logger;
    private readonly Lock _gate = new();

    private DiscoveryScan? _current;
    private CancellationTokenSource? _running;
    private int _updates;

    public DeviceDiscoveryService(ILogger<DeviceDiscoveryService> logger, TimeProvider? time = null)
        : this(
            logger,
            probe => new DeviceScanner(portProbe: probe),
            new TcpPortProbe(),
            NetworkSelection.LocalInterfaces,
            NetworkSelection.RunningInContainer(),
            time)
    {
    }

    /// <summary>For tests: the scanner, the interfaces and the container question supplied.</summary>
    public DeviceDiscoveryService(
        ILogger<DeviceDiscoveryService> logger,
        Func<IPortProbe, DeviceScanner> scanner,
        IPortProbe probe,
        Func<IEnumerable<InterfaceAddress>> interfaces,
        bool inContainer,
        TimeProvider? time = null)
    {
        _logger = logger;
        _scanner = scanner;
        _probe = probe;
        _interfaces = interfaces;
        _inContainer = inContainer;
        _time = time ?? TimeProvider.System;
    }

    public event Action? Changed;

    public DiscoveryScan? Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public DiscoverySuggestion Suggest()
    {
        var selection = NetworkSelection.FromInterfaces(_interfaces(), _inContainer);

        // The scanner's note on each container or VM bridge it passes over is for the command line, where
        // it explains a subnet missing from the list. In the wizard, which shows the list, it is noise --
        // and the Pi, running Docker, always has one.
        return new DiscoverySuggestion(
            [.. selection.Subnets.Select(subnet => subnet.ToString())],
            [.. selection.Warnings.Where(warning => !warning.StartsWith("Skipped ", StringComparison.Ordinal))]);
    }

    public DiscoveryStart Start(string? subnet)
    {
        ScanPlan plan;

        if (string.IsNullOrWhiteSpace(subnet))
        {
            var selection = NetworkSelection.FromInterfaces(_interfaces(), _inContainer);
            if (selection.Subnets.Count == 0)
            {
                return new DiscoveryStart(false, "No local network to scan was found. Enter your LAN's range, e.g. 192.168.1.0/24.",
                    Phrase.Of("Discovery.NoNetwork"));
            }

            plan = ScanPlan.ForSubnets(selection.Subnets, selection.Warnings);
        }
        else if (Parse(subnet.Trim()) is { } parsed)
        {
            plan = parsed;
        }
        else
        {
            return new DiscoveryStart(
                false,
                $"'{subnet.Trim()}' is not a private network of at most {(1 << (32 - NetworkSelection.WidestDefaultPrefix)) - 2} hosts "
                + "(e.g. 192.168.1.0/24) or a single private address.",
                Phrase.Of("Discovery.BadSubnet", new PhraseArg.Text(subnet.Trim())));
        }

        CancellationTokenSource cancel;
        var options = new DiscoveryOptions();

        lock (_gate)
        {
            if (_current is { Running: true })
            {
                return new DiscoveryStart(false, "A scan is already running.", Phrase.Of("Discovery.AlreadyRunning"));
            }

            cancel = new CancellationTokenSource();
            _running?.Dispose();
            _running = cancel;
            _current = new DiscoveryScan(
                plan.Networks, plan.Hosts.Count * options.Ports.Count, 0, true, false, [], plan.Warnings, _time.GetUtcNow(), null);
        }

        _logger.LogInformation(
            "Device discovery started from the web UI: {Networks}, {Hosts} addresses.", string.Join(", ", plan.Networks), plan.Hosts.Count);

        Notify();
        _ = RunAsync(plan, cancel.Token);
        return new DiscoveryStart(true);
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _running?.Cancel();
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _running?.Cancel();
            _running?.Dispose();
            _running = null;
        }
    }

    private async Task RunAsync(ScanPlan plan, CancellationToken cancellationToken)
    {
        var found = new ConcurrentQueue<FoundDevice>();
        var probe = new CountingProbe(_probe, () => Update(scan => scan with { Probed = scan.Probed + 1 }, notifyEvery: 32));

        DiscoveryReport report;

        try
        {
            report = await _scanner(probe).ScanAsync(
                plan,
                device =>
                {
                    found.Enqueue(Convert(device));
                    Update(scan => scan with { Devices = [.. found] }, notifyEvery: 1);
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Device discovery failed.");
            Update(scan => scan with { Running = false, FinishedAt = _time.GetUtcNow(), Warnings = [.. scan.Warnings, ex.Message] }, 1);
            return;
        }

        Update(
            scan => scan with
            {
                Running = false,
                Cancelled = report.Cancelled,
                Probed = scan.ToProbe,
                Devices = [.. report.Devices.Select(Convert)],
                Warnings = report.Warnings,
                FinishedAt = _time.GetUtcNow(),
            },
            notifyEvery: 1);

        _logger.LogInformation(
            "Device discovery finished: {Count} answering addresses{Cancelled}.",
            report.Devices.Count,
            report.Cancelled ? " (cancelled)" : string.Empty);
    }

    // Every change goes through here; a page is told about every device found and about every so many
    // probes, not each of the thousands of connects a /24 takes.
    private void Update(Func<DiscoveryScan, DiscoveryScan> change, int notifyEvery)
    {
        lock (_gate)
        {
            if (_current is null)
            {
                return;
            }

            _current = change(_current);
        }

        if (notifyEvery <= 1 || Interlocked.Increment(ref _updates) % notifyEvery == 0)
        {
            Notify();
        }
    }

    private void Notify()
    {
        try
        {
            Changed?.Invoke();
        }
        catch (Exception ex)
        {
            // A page that went away mid-render must not stop the scan for everybody else.
            _logger.LogDebug(ex, "A discovery listener failed.");
        }
    }

    private static FoundDevice Convert(DiscoveredDevice device) =>
        new(
            device.Host,
            device.Port,
            device.UnitId,
            device.Role,
            device.Model,
            device.SerialNumber,
            device.Finding == DiscoveryFinding.SolaxDevice
                && device.Tier is SupportTier.Verified or SupportTier.Expected or SupportTier.ReadingsAndChargingOnly,
            device.Note);

    // A private subnet no wider than the scanner's own default, or one private address.
    private static ScanPlan? Parse(string text)
    {
        if (IPAddress.TryParse(text, out var single))
        {
            return Ipv4Subnet.IsPrivate(single) ? ScanPlan.ForHosts([single.ToString()]) : null;
        }

        if (Ipv4Subnet.TryParse(text, out var subnet)
            && subnet.Value.PrefixLength >= NetworkSelection.WidestDefaultPrefix
            && subnet.Value.Hosts().FirstOrDefault() is { } first
            && Ipv4Subnet.IsPrivate(first))
        {
            return ScanPlan.ForSubnets([subnet.Value]);
        }

        return null;
    }

    // The scanner's port probe, counted: how far the long first part of a scan has got.
    private sealed class CountingProbe(IPortProbe inner, Action probed) : IPortProbe
    {
        public async Task<bool> IsOpenAsync(string host, int port, TimeSpan timeout, CancellationToken cancellationToken)
        {
            try
            {
                return await inner.IsOpenAsync(host, port, timeout, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                probed();
            }
        }
    }
}
