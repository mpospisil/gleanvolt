using Gleanvolt.Core.Interfaces;

namespace Gleanvolt.Core.Models;

/// <summary>
/// Holds the latest <see cref="VehicleState"/> so the transport that receives it and the surfaces that
/// display it stay decoupled. Singleton.
///
/// <para>Same shape and the same reasoning as <see cref="ChargeControlStatusHolder"/>: it lives in
/// Core because it has more than one consumer — the self-hosted web UI (its own assembly, which must
/// not depend on the host) and, in a later phase, the charging strategies. A plain holder with an
/// event has no framework dependency, so Core's layering rules are satisfied.</para>
///
/// <para>It implements <see cref="IVehicleTelemetry"/> so consumers can depend on the read side alone
/// and be handed a stub in tests, while only the transport holds the reference that can
/// <see cref="Set"/>.</para>
/// </summary>
public sealed class VehicleStateHolder : IVehicleTelemetry
{
    private volatile VehicleState? _current;

    /// <inheritdoc />
    public VehicleState? GetCurrentState() => _current;

    /// <summary>Raised whenever a new state is set.</summary>
    public event Action<VehicleState>? Updated;

    /// <summary>
    /// Replaces the current state. Called only for a message that parsed into something usable —
    /// a rejected payload leaves the previous state in place rather than blanking it, so a momentarily
    /// broken feed degrades into "the reading is getting older" instead of "there is no car".
    /// </summary>
    public void Set(VehicleState state)
    {
        _current = state;
        Updated?.Invoke(state);
    }
}
