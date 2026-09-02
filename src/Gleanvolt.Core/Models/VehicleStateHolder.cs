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

    /// <param name="time">
    /// The clock the comparison's observation window is measured against. Optional so that the many
    /// tests which simply <c>new</c> this up keep working.
    /// </param>
    public VehicleStateHolder(TimeProvider? time = null) => Comparison = new VehicleFeedComparison(time);

    /// <inheritdoc />
    public VehicleState? GetCurrentState() => _current;

    /// <summary>
    /// What every feed has offered, counted (issue #141).
    ///
    /// <para><b>Owned by the holder rather than registered beside it</b>, for one reason: this is the
    /// only place a reading that <i>lost</i> is still visible. <see cref="Set"/> discards an older
    /// reading and returns false, so a feed that is consistently second leaves nothing behind
    /// anywhere else — and whether one feed is consistently second is precisely what the week of
    /// running both is meant to find out. Owning it here also means it cannot be missed by being
    /// resolved from the container too late to see the first readings.</para>
    ///
    /// <para>Observation only. Nothing reads it but the page that reports it, and it is on no
    /// hardware path.</para>
    /// </summary>
    public VehicleFeedComparison Comparison { get; }

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
    ///
    /// <para>Every offer is counted into <see cref="Comparison"/> first, taken or not — the losing
    /// readings are the ones that say which feed is actually leading (issue #141).</para>
    /// </summary>
    /// <returns>Whether this reading was taken, i.e. whether it was newer than the one held.</returns>
    public bool Set(VehicleState state)
    {
        var current = _current;

        if (current is not null && state.CapturedAt < current.CapturedAt)
        {
            Comparison.Record(state, taken: false);
            return false;
        }

        _current = state;
        Comparison.Record(state, taken: true);
        Updated?.Invoke(state);
        return true;
    }
}
