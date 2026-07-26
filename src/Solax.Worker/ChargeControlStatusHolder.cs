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
public readonly record struct ChargeControlCycleResult(
    Solax.Core.Enums.ChargeControlState State,
    double? SurplusWatts,
    int? TargetCurrentAmps,
    bool HoldingControl);
