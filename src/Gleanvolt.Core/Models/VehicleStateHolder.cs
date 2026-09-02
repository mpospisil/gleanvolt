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
    /// Offers a reading. Called only for a message that parsed into something usable — a rejected
    /// payload leaves the previous state in place rather than blanking it, so a momentarily broken
    /// feed degrades into "the reading is getting older" instead of "there is no car".
    ///
    /// <para><b>The newest reading wins, whoever produced it</b>, and one older than what is already
    /// held is ignored. That is what lets two sources feed one car without a race: a Home Assistant
    /// integration polling the manufacturer's app API and this controller reading the same
    /// manufacturer's EU Data Act portal describe the same battery at different resolutions and
    /// different lags, and the right answer is simply whichever saw the car most recently. It also
    /// fixes a smaller thing that was always there: a retained MQTT message replayed on reconnect no
    /// longer overwrites a fresher reading with a stale one.</para>
    ///
    /// <para>A feed that stops does not block the other, because its readings stop advancing while
    /// the other's keep arriving — so precedence corrects itself within one reading rather than being
    /// configured.</para>
    /// </summary>
    /// <returns>Whether this reading was taken, i.e. whether it was newer than the one held.</returns>
    public bool Set(VehicleState state)
    {
        var current = _current;

        if (current is not null && state.CapturedAt < current.CapturedAt)
        {
            return false;
        }

        _current = state;
        Updated?.Invoke(state);
        return true;
    }
}
