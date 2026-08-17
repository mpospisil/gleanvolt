using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Serves the last-known state of the EV. Implementations are fed asynchronously by whatever source
/// is configured and answer synchronously and cheaply, so callers on hot paths (the polling loop, a
/// Blazor render) never do I/O — the same contract <see cref="ISolarForecastService"/> has, and for
/// the same reason: this data arrives on its own schedule, measured in minutes, and must never pace
/// the control loop.
/// </summary>
public interface IVehicleTelemetry
{
    /// <summary>
    /// The most recent vehicle state, or <c>null</c> when nothing has been received yet — the feed is
    /// disabled, no message has arrived, or every message so far was rejected as unusable.
    ///
    /// <para>A non-null result is <b>not</b> a promise of freshness. Check
    /// <see cref="VehicleState.IsStaleAt"/> before acting on it.</para>
    /// </summary>
    VehicleState? GetCurrentState();
}
