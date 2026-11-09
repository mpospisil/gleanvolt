using System.Globalization;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Infrastructure;

namespace Gleanvolt.Hosting;

/// <summary>
/// <see cref="IDeviceSnapshot"/> on the running devices (issues #319, #350): the ranges the configured driver
/// names, read once on the device's keyed client, the serial number cut to its prefix.
/// </summary>
public sealed class DeviceSnapshot : IDeviceSnapshot
{
    // A handful of requests, each paced by the client; generous, so a slow link is not reported as a dead one.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IModbusClient _inverter;
    private readonly IModbusClient _charger;
    private readonly PvSystemInfo _site;
    private readonly IEnumerable<IInverterDriverFactory> _inverterDrivers;
    private readonly IEnumerable<IEvChargerDriverFactory> _chargerDrivers;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceSnapshot> _logger;

    public DeviceSnapshot(
        [FromKeyedServices(ModbusClientKeys.Inverter)] IModbusClient inverter,
        [FromKeyedServices(ModbusClientKeys.EvCharger)] IModbusClient charger,
        PvSystemInfo site,
        IEnumerable<IInverterDriverFactory> inverterDrivers,
        IEnumerable<IEvChargerDriverFactory> chargerDrivers,
        TimeProvider time,
        ILogger<DeviceSnapshot> logger)
    {
        _inverter = inverter;
        _charger = charger;
        _site = site;
        _inverterDrivers = inverterDrivers;
        _chargerDrivers = chargerDrivers;
        _time = time;
        _logger = logger;
    }

    public Task<RegisterSnapshotFile> TakeAsync(PvDeviceRole role, string note, CancellationToken cancellationToken = default) =>
        role switch
        {
            PvDeviceRole.Inverter => TakeInverterAsync(note, cancellationToken),
            PvDeviceRole.Charger => TakeChargerAsync(note, cancellationToken),
            _ => throw new ArgumentOutOfRangeException(nameof(role), role, "Not a device role."),
        };

    private Task<RegisterSnapshotFile> TakeInverterAsync(string note, CancellationToken cancellationToken)
    {
        var driver = _inverterDrivers.FirstOrDefault(candidate => candidate.Id == _site.InverterDriver)
            ?? throw new InvalidOperationException($"No inverter driver '{_site.InverterDriver}' is registered.");

        return TakeAsync(
            "inverter",
            _inverter,
            driver.SnapshotFor(new InverterDriverOptions { Topology = _site.InverterTopology }),
            driver.Id,
            _site.Inverter.Model,
            $"gleanvolt-registers-{driver.Id}",
            note,
            cancellationToken);
    }

    private Task<RegisterSnapshotFile> TakeChargerAsync(string note, CancellationToken cancellationToken)
    {
        // The controlled charger: the one the charger client is keyed to. Setup mode has none, and its
        // client refuses every call anyway.
        if (_site.Chargers is not [var charger, ..])
        {
            throw new InvalidOperationException("No charger is configured.");
        }

        var id = charger.Driver.Length > 0 ? charger.Driver : ChargerDrivers.DefaultId;
        var driver = _chargerDrivers.FirstOrDefault(candidate => candidate.Id == id)
            ?? throw new InvalidOperationException($"No charger driver '{id}' is registered.");

        // A family reached over HTTP keeps no registers; its recording is of responses, and comes with its
        // driver (go-e, #352).
        var spec = driver.Snapshot
            ?? throw new InvalidOperationException($"The {driver.DisplayName} charger is reached over {driver.Transport}, not Modbus: it has no registers to save.");

        return TakeAsync(
            "charger",
            _charger,
            spec,
            driver.Id,
            charger.Model,
            $"gleanvolt-registers-{driver.Id}-charger",
            note,
            cancellationToken);
    }

    private async Task<RegisterSnapshotFile> TakeAsync(
        string device,
        IModbusClient client,
        RegisterSnapshotSpec spec,
        string driverId,
        string family,
        string fileStem,
        string note,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var now = _time.GetUtcNow();
        var dump = await RegisterSnapshot.TakeAsync(
            client,
            spec,
            new RegisterDump
            {
                Driver = driverId,
                Family = family,
                GleanvoltVersion = BuildInfo.Version,
                CapturedAt = now,
                Note = note.Trim(),
            },
            timeout.Token).ConfigureAwait(false);

        _logger.LogInformation(
            "Register snapshot of the {Device} taken: {Holding} holding and {Input} input registers ({Driver}).",
            device, dump.Holding.Count, dump.Input.Count, driverId);

        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new RegisterSnapshotFile($"{fileStem}-{stamp}.json", dump.ToJson());
    }
}
