using Gleanvolt.Devices;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The charger as the poll reads it, without a plugin behind it: a reading, or the exception a read fails with.
/// </summary>
internal sealed class FakeCharger : IEvChargerDriver
{
    /// <summary>What a read throws, or null for a charger that answers.</summary>
    public Func<Exception>? Failure { get; set; }

    public EvChargerReading Reading { get; set; } = new(EvChargerStatus.Available, 0, 16, null);

    public EvChargerCapabilities Capabilities { get; } = new(6, 32, 0, 3, false, false);

    /// <summary>A charger that cannot be reached at all.</summary>
    public static FakeCharger Unreachable(Func<Exception>? failure = null) =>
        new() { Failure = failure ?? (() => new IOException("no route to host")) };

    public Task<EvChargerReading> ReadAsync(CancellationToken cancellationToken = default) =>
        Failure is { } failure ? throw failure() : Task.FromResult(Reading);

    public Task<EvChargerSettings> ReadSettingsAsync(CancellationToken cancellationToken = default) =>
        Failure is { } failure ? throw failure() : Task.FromResult(new EvChargerSettings(true, Reading.CurrentAmps ?? 0));

    public Task SetCurrentAsync(int activeAmps, int targetAmps, string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The poll never commands the charger.");

    public Task SetChargingAllowedAsync(bool allowed, string reason, CancellationToken cancellationToken = default) =>
        throw new NotSupportedException("The poll never commands the charger.");
}
