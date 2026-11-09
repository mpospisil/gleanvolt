using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Devices;

namespace Gleanvolt.Infrastructure;

/// <summary>
/// One telemetry poll, composed (issues #317, #348): the inverter through its driver, the charger through
/// its own, best-effort, the correction for a grid meter blind on one phase, and the <see cref="EnergyState"/> the
/// rest of the controller reads. Nothing here knows either brand.
/// </summary>
public sealed class EnergyStateReader : IEnergyStateReader
{
    private readonly IInverterDriver _inverter;
    private readonly IEvChargerDriver _evCharger;
    private readonly ILogger<EnergyStateReader> _logger;

    // The phase the grid meter cannot see, or null for a meter that sees all three. Corrected here, once,
    // rather than in each consumer: surplus, house profile, battery hold, energy intervals and Home
    // Assistant then all read the same number without knowing there was anything to correct.
    private readonly GridPhase? _unmeteredGridPhase;
    private readonly int _evPhases;

    // Whether the last poll reached the charger. Only used to keep the log to one line per outage
    // rather than one per poll; this service is a singleton, so it survives between polls.
    private bool _evChargerReachable = true;

    /// <param name="inverter">The inverter's driver.</param>
    /// <param name="evCharger">The charger's driver.</param>
    /// <param name="site">The installation, for its unmetered grid phase. Null reads the meter as it is.</param>
    /// <param name="limits">The car's phase count, for how its load divides between phases. Null assumes three.</param>
    public EnergyStateReader(
        IInverterDriver inverter,
        IEvChargerDriver evCharger,
        ILogger<EnergyStateReader> logger,
        PvSystemInfo? site = null,
        ChargingLimits? limits = null)
    {
        _inverter = inverter;
        _evCharger = evCharger;
        _logger = logger;
        _unmeteredGridPhase = site?.UnmeteredGridPhase;
        _evPhases = limits?.Phases ?? 3;

        // The correction needs the inverter's output per phase. Only SolaX exists today and it reports
        // one, so this cannot fire yet; once the driver is chosen in configuration (#318), the rules
        // refuse the combination before it gets here.
        if (_unmeteredGridPhase is { } blind && !inverter.Capabilities.HasPhaseOutput)
        {
            _logger.LogWarning(
                "The grid meter is configured as blind on {Phase}, but this inverter does not report its output per phase, so the meter is read as it is.",
                blind);
        }
    }

    public async Task<EnergyState> ReadAsync(CancellationToken cancellationToken = default)
    {
        var inverter = await _inverter.ReadAsync(cancellationToken).ConfigureAwait(false);
        var evCharger = await ReadEvChargerAsync(cancellationToken).ConfigureAwait(false);

        var state = new EnergyState(
            DateTimeOffset.UtcNow,
            inverter.BatterySocPercent,
            inverter.BatteryWatts,
            inverter.PvWatts,
            inverter.GridWatts,
            evCharger.Status,
            evCharger.PowerWatts)
        {
            ChargeMode = evCharger.Mode,
            ChargeCurrentAmps = evCharger.CurrentAmps,
        };

        return _unmeteredGridPhase is { } blind && inverter.InverterOutputPerPhase is { } inverterOutput
            ? WithUnmeteredPhaseEstimated(state, inverterOutput, blind)
            : state;
    }

    private EnergyState WithUnmeteredPhaseEstimated(EnergyState state, PhaseWatts inverterOutput, GridPhase blind)
    {
        var estimate = UnmeteredGridPhaseEstimate.GridPowerWatts(
            state.GridPowerWatts, blind, inverterOutput, state.EvChargerPowerWatts, _evPhases);

        return state with
        {
            GridPowerWatts = Math.Round(estimate),
            MeteredGridPowerWatts = state.GridPowerWatts,
        };
    }

    // The charger is optional in a way the inverter is not: it can be switched off, moved to another
    // address by DHCP, or simply absent from the installation -- and none of that should cost us the
    // inverter telemetry that was just read successfully. So everything charger-shaped is
    // best-effort, the connect included: a charger that cannot be read is reported Unknown, which is
    // the absence of information and never a car that is gone (IsCarKnownDisconnected).
    private async Task<EvChargerReading> ReadEvChargerAsync(CancellationToken cancellationToken)
    {
        try
        {
            var reading = await _evCharger.ReadAsync(cancellationToken).ConfigureAwait(false);

            if (!_evChargerReachable)
            {
                _evChargerReachable = true;
                _logger.LogInformation("EV charger is reachable again; its telemetry is live.");
            }

            return reading;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // One line per outage, not one per poll: this runs on the polling interval, and a charger
            // that is switched off stays switched off for hours. The recovery above closes the pair,
            // so the log says when it went and when it came back.
            if (_evChargerReachable)
            {
                _evChargerReachable = false;
                _logger.LogWarning(
                    ex,
                    "EV charger is unreachable; reporting it as {Status} and continuing with inverter telemetry.",
                    EvChargerStatus.Unknown);
            }

            return Unreachable;
        }
    }

    private static readonly EvChargerReading Unreachable =
        new(EvChargerStatus.Unknown, PowerWatts: 0, CurrentAmps: null, Mode: null);
}
