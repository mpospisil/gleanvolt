using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// Drives the <see cref="ChargeControlMode.SolarGrid"/> mode: the sun first, the grid allowed to help,
/// and only for as long as today's sun lasts.
///
/// <list type="bullet">
/// <item><description>While the <b>smoothed</b> surplus clears the owner's minimum, the car follows it.
/// A surplus that clears the minimum but not the charger's 6 A floor is topped up to the floor from
/// the grid — a <see cref="ChargingControlDecision.GridBridgeWatts"/>, on which the host arms the
/// discharge hold so the home battery never pays for it.</description></item>
/// <item><description>Below the minimum the car pauses, through the same "cloud" machinery the other
/// solar modes use: the moving-average surplus, the resume hysteresis, and the dwell timers that hold
/// a running charge at the floor for its minimum run time and keep a paused one paused for its minimum
/// pause.</description></item>
/// <item><description>What an idle car does next is the forecast's call: sun still to come today means
/// wait, paused or stood down by <see cref="ChargerWait"/>, and none means the mode is over for the day
/// and returns itself to <see cref="ChargeControlMode.Off"/>.</description></item>
/// </list>
///
/// <para>The forecast never stops a car that is getting real sun. It is consulted only once the live
/// gate is already closed, so a forecast that wrote the afternoon off cannot turn away a surplus that
/// is actually there.</para>
///
/// <para>There is no battery-full gate: the car competes with the home battery for the surplus from the
/// first watt that clears the minimum. Pure and side-effect free, like every other strategy, and it
/// only modulates the current while the charger's own use-mode is <see cref="EvChargerMode.Fast"/>.</para>
/// </summary>
public sealed class SolarGridChargingController : IChargingController
{
    private readonly ChargePowerConverter _power;
    private readonly SolarGridChargingOptions _options;
    private readonly int _minAmps;
    private readonly int _maxAmps;

    public SolarGridChargingController(ChargePowerConverter powerConverter, SolarGridChargingOptions options)
    {
        _power = powerConverter ?? throw new ArgumentNullException(nameof(powerConverter));
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (options.CurrentStepAmps <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(options), options.CurrentStepAmps, "Current step must be positive.");
        }

        _minAmps = Math.Clamp(options.MinChargingCurrentAmps, EvChargerLimits.MinCurrentAmps, EvChargerLimits.MaxCurrentAmps);
        _maxAmps = Math.Clamp(options.MaxChargingCurrentAmps, _minAmps, EvChargerLimits.MaxCurrentAmps);
    }

    /// <summary>The charger's minimum viable power — what a grid bridge tops the surplus up to.</summary>
    public double MinChargePowerWatts => _power.AmpsToWatts(_minAmps);

    public ChargingControlDecision Decide(ChargingControlInput input)
    {
        ArgumentNullException.ThrowIfNull(input);

        // A full car must end this mode, not merely pause it: one held at a bridged 6 A keeps the
        // discharge hold armed, and the house on the grid, for nothing.
        if (ChargerOwnership.Check(input, _options.CompletionDwell) is { } ended)
        {
            return ended;
        }

        var outlook = input.Outlook;
        var minimum = Math.Max(0, outlook?.MinSurplusWatts ?? _options.MinSurplusWatts);
        var surplus = input.SurplusWatts;

        // Asymmetric, like every other solar gate: keep charging down to the minimum, but only (re)start
        // a hysteresis margin above it.
        var threshold = input.Charging ? minimum : minimum + _options.ResumeHysteresisWatts;
        var thresholdName = input.Charging ? "minimum" : "start threshold";
        var below = $"Surplus {surplus:F0}W is under the {threshold:F0}W {thresholdName}.";

        // Low tariff only (#274), decided as (a): high tariff pauses the car, sun or not -- the owner's
        // choice over following free surplus, so the mode charges in low tariff and nowhere else. The
        // day's own end still applies, so the option narrows when the mode charges and never keeps it
        // alive past its sun.
        if (TariffWait.For(input, "SolarGrid") is { } wait)
        {
            return surplus < threshold && ChargerWait.DayIsOver(input, below) is { } dayOver ? dayOver : wait;
        }

        if (surplus >= threshold)
        {
            // The restart dwell spares the contactor when a charge this mode has run is flapping on the
            // threshold -- and only then: see RestartDwell for why a first charge never waits.
            if (RestartDwell.Holds(input, _options.MinPauseTime))
            {
                return ChargerWait.For(
                    input,
                    ChargerWait.DwellEnds(input, _options.MinPauseTime),
                    $"Surplus {surplus:F0}W is back over the {threshold:F0}W {thresholdName}, but paused "
                    + $"{input.TimeInCurrentState.TotalMinutes:F0}min of the {_options.MinPauseTime.TotalMinutes:F0}min minimum before restarting.");
            }

            return ChargeFromSurplus(surplus, minimum);
        }

        if (input.Charging && input.TimeInCurrentState < _options.MinRunTime)
        {
            return HoldAtFloor(surplus, below);
        }

        if (outlook is null)
        {
            return ChargerWait.For(input, null, $"{below} Waiting for sun.");
        }

        if (!outlook.ForecastUsable)
        {
            return ChargerWait.For(input, null, $"{below} No usable forecast ({outlook.Reason}), so waiting on live surplus alone.");
        }

        if (ChargerWait.DayIsOver(input, below) is { } over)
        {
            return over;
        }

        if (outlook.NextSunAt is { } next && next > input.State.Timestamp)
        {
            return ChargerWait.For(
                input, next, $"{below} The forecast has the surplus clearing {minimum:F0}W from {next.LocalDateTime:HH:mm}.");
        }

        return ChargerWait.For(
            input,
            input.State.Timestamp,
            $"{below} The forecast still has it clearing {minimum:F0}W until {outlook.SunUntil?.LocalDateTime:HH:mm}; "
            + "waiting for this dip to pass.");
    }

    /// <summary>
    /// Follow the surplus, and bridge from the grid only as far as the charger's floor: a 3 kW surplus
    /// on three phases charges nothing on its own, so it would be exported, while 6 A buys only the
    /// difference. Never a booster — at or above the floor the setpoint is floored to what the sun
    /// covers, and the grid adds nothing.
    /// </summary>
    private ChargingControlDecision ChargeFromSurplus(double surplus, double minimum)
    {
        var amps = Math.Max(ToHardwareCurrent(surplus), _minAmps);
        var bridge = Math.Max(0, _power.AmpsToWatts(amps) - Math.Max(0, surplus));

        return bridge > 0
            ? new ChargingControlDecision(
                ChargingControlAction.Charge,
                amps,
                $"Surplus {surplus:F0}W clears the {minimum:F0}W minimum but not the {MinChargePowerWatts:F0}W the charger needs "
                + $"-> charge at {amps}A, +{bridge:F0}W from the grid.",
                GridBridgeWatts: bridge)
            : new ChargingControlDecision(
                ChargingControlAction.Charge,
                amps,
                $"Surplus {surplus:F0}W clears the {minimum:F0}W minimum -> charge at {amps}A from the sun.");
    }

    /// <summary>
    /// A dip inside the minimum run time: the charge is held at the floor rather than stopped, and what
    /// the sun is no longer covering comes from the grid — reported as a bridge, so the hold stays armed
    /// and the pack stays out of it.
    /// </summary>
    private ChargingControlDecision HoldAtFloor(double surplus, string why)
    {
        var bridge = Math.Max(0, MinChargePowerWatts - Math.Max(0, surplus));

        return new ChargingControlDecision(
            ChargingControlAction.Charge,
            _minAmps,
            $"{why} Holding at {_minAmps}A for the {_options.MinRunTime.TotalMinutes:F0}min minimum run time, +{bridge:F0}W from the grid.",
            GridBridgeWatts: bridge);
    }


    // Whole-amp setpoint the charger accepts: convert (phase-aware), floor to the step, clamp to max.
    private int ToHardwareCurrent(double availableWatts)
    {
        var rawAmps = _power.WattsToAmps(availableWatts);
        var steppedAmps = (int)Math.Floor(rawAmps / _options.CurrentStepAmps) * _options.CurrentStepAmps;
        return Math.Min(steppedAmps, _maxAmps);
    }
}
