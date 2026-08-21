using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Targeting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>Records both kinds of write and reflects them back as the new settings, like hardware.</summary>
internal sealed class FakeEvChargerControl : IEvChargerControl
{
    public EvChargerSettings CurrentSettings { get; set; } = new(EvChargerMode.Fast, 0);

    /// <summary>Every SetCurrentAsync call, in order.</summary>
    public List<(int Active, int Target, string Reason)> CurrentWrites { get; } = [];

    /// <summary>Every SetModeAsync call, in order — the use-mode writes an action made.</summary>
    public List<(EvChargerMode Mode, string Reason)> ModeWrites { get; } = [];

    /// <summary>Makes every use-mode write fail, the way a charger that has stopped answering does.</summary>
    public string? ModeWriteFailure { get; set; }

    /// <summary>The target amps of the last write, or null if none.</summary>
    public int? LastTarget => CurrentWrites.Count == 0 ? null : CurrentWrites[^1].Target;

    public Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(CurrentSettings);

    public Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default)
    {
        CurrentWrites.Add((activeAmps, targetAmps, reason));
        CurrentSettings = CurrentSettings with { ChargeCurrentAmps = targetAmps };
        return Task.CompletedTask;
    }

    public Task SetModeAsync(EvChargerMode mode, string reason, CancellationToken cancellationToken = default)
    {
        if (ModeWriteFailure is { } failure)
        {
            return Task.FromException(new InvalidOperationException(failure));
        }

        ModeWrites.Add((mode, reason));
        CurrentSettings = CurrentSettings with { Mode = mode };
        return Task.CompletedTask;
    }
}

/// <summary>Returns a canned decision and captures the last input the coordinator built.</summary>
internal sealed class StubChargingController : IChargingController
{
    public ChargingControlDecision NextDecision { get; set; } =
        new(ChargingControlAction.None, null, "stub");

    public ChargingControlInput? LastInput { get; private set; }

    public ChargingControlDecision Decide(ChargingControlInput input)
    {
        LastInput = input;
        return NextDecision;
    }
}

/// <summary>
/// A <see cref="TargetedChargeProvider"/> built the way the host builds it. Every poll-loop test needs
/// one whether or not it exercises the targeted mode, so the assembly lives in one place.
/// </summary>
internal static class TargetedCharge
{
    public static TargetedChargeProvider Provider(
        ISolarForecastService forecast,
        DayPlanProvider dayPlan,
        ChargePowerConverter power,
        ChargeControlOptions chargeControl,
        IOptions<ForecastChargeOptions> forecastOptions,
        ITargetedChargeSelector? selector = null,
        TargetedChargeOptions? options = null) =>
        new(
            selector ?? new TargetedChargeSelector(NullLogger<TargetedChargeSelector>.Instance),
            forecast,
            dayPlan,
            power,
            Options.Create(chargeControl),
            forecastOptions,
            Options.Create(options ?? new TargetedChargeOptions()),
            NullLogger<TargetedChargeProvider>.Instance);
}
