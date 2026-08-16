using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Tests;

/// <summary>Records current-setpoint writes and reflects them back as the new "current", like hardware.</summary>
internal sealed class FakeEvChargerControl : IEvChargerControl
{
    public EvChargerSettings CurrentSettings { get; set; } = new(EvChargerMode.Fast, 0);

    /// <summary>Every SetCurrentAsync call, in order.</summary>
    public List<(int Active, int Target, string Reason)> CurrentWrites { get; } = [];

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
