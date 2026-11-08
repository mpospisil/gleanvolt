using Gleanvolt.Devices;

namespace Gleanvolt.Hosting;

/// <summary>
/// The battery hold of an inverter family that has none (<see cref="IInverterDriver.BatteryHold"/> is
/// null). It never writes and always reports the battery released, and says so once in the log when
/// somebody asks it to hold, so a switch that does nothing does not do nothing silently.
/// </summary>
public sealed class NoBatteryHold : IBatteryDischargeControl
{
    private readonly ILogger<NoBatteryHold> _logger;
    private bool _warned;

    public NoBatteryHold(ILogger<NoBatteryHold> logger)
    {
        _logger = logger;
    }

    public Task<BatteryHoldState> ApplyAsync(
        bool hold,
        double activePowerTargetWatts,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        if (hold && !_warned)
        {
            _warned = true;
            _logger.LogWarning("The battery hold is on, but this inverter has no way to hold the battery; it is left alone.");
        }

        return Task.FromResult(new BatteryHoldState(Held: false, null, null, Wrote: false));
    }
}
