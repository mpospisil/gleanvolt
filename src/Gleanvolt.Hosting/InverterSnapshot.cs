using System.Globalization;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Infrastructure;

namespace Gleanvolt.Hosting;

/// <summary>
/// <see cref="IInverterSnapshot"/> on the running inverter (issue #319): the ranges the configured driver
/// names, read once on the inverter's keyed client, the serial number cut to its prefix.
/// </summary>
public sealed class InverterSnapshot : IInverterSnapshot
{
    // Two or three requests, each paced by the client; generous, so a slow link is not reported as a dead one.
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    private readonly IModbusClient _inverter;
    private readonly PvSystemInfo _site;
    private readonly IEnumerable<IInverterDriverFactory> _drivers;
    private readonly TimeProvider _time;
    private readonly ILogger<InverterSnapshot> _logger;

    public InverterSnapshot(
        [FromKeyedServices(ModbusClientKeys.Inverter)] IModbusClient inverter,
        PvSystemInfo site,
        IEnumerable<IInverterDriverFactory> drivers,
        TimeProvider time,
        ILogger<InverterSnapshot> logger)
    {
        _inverter = inverter;
        _site = site;
        _drivers = drivers;
        _time = time;
        _logger = logger;
    }

    public async Task<RegisterSnapshotFile> TakeAsync(string note, CancellationToken cancellationToken = default)
    {
        var driver = _drivers.FirstOrDefault(candidate => candidate.Id == _site.InverterDriver)
            ?? throw new InvalidOperationException($"No inverter driver '{_site.InverterDriver}' is registered.");

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(Timeout);

        var now = _time.GetUtcNow();
        var dump = await RegisterSnapshot.TakeAsync(
            _inverter,
            driver.Snapshot,
            new RegisterDump
            {
                Driver = driver.Id,
                Family = _site.Inverter.Model,
                GleanvoltVersion = BuildInfo.Version,
                CapturedAt = now,
                Note = note.Trim(),
            },
            timeout.Token).ConfigureAwait(false);

        _logger.LogInformation(
            "Register snapshot of the inverter taken: {Holding} holding and {Input} input registers ({Driver}).",
            dump.Holding.Count, dump.Input.Count, driver.Id);

        var stamp = now.ToString("yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        return new RegisterSnapshotFile($"gleanvolt-registers-{driver.Id}-{stamp}.json", dump.ToJson());
    }
}
