using Solax.Core.Models;

namespace Solax.Worker;

/// <summary>
/// Holds the latest <see cref="ChargeControlStatus"/> so the reporting layer (e.g. the Home
/// Assistant integration) can publish it without being coupled to the polling loop. Singleton.
/// </summary>
public sealed class ChargeControlStatusHolder
{
    private volatile ChargeControlStatus? _current;

    /// <summary>The most recent status, or null before the first poll.</summary>
    public ChargeControlStatus? Current => _current;

    /// <summary>Raised whenever a new status is set.</summary>
    public event Action<ChargeControlStatus>? Updated;

    public void Set(ChargeControlStatus status)
    {
        _current = status;
        Updated?.Invoke(status);
    }
}

/// <summary>The outcome of one charge-control cycle, used to assemble a <see cref="ChargeControlStatus"/>.</summary>
/// <param name="LoanPowerWatts">
/// How much of the commanded charge the home battery is currently lending, bridging a sub-minimum
/// surplus up to the charger's floor. Zero outside the forecast-driven mode.
/// </param>
/// <param name="SessionComplete">
/// The controller reported that the car has finished (or gone away) and the mode should end itself.
/// Only the fast mode ever sets it; the poll loop answers by returning the mode to Off.
/// </param>
public readonly record struct ChargeControlCycleResult(
    Solax.Core.Enums.ChargeControlState State,
    double? SurplusWatts,
    int? TargetCurrentAmps,
    bool HoldingControl,
    double LoanPowerWatts = 0,
    bool SessionComplete = false);
