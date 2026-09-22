using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Vehicles;

/// <summary>
/// Asks the car's feed for its state, now (issue #168).
///
/// <para>Calls <see cref="IVehicleUpdateService.AskAsync"/> rather than the polling worker's
/// <see cref="IVehicleUpdateService.FetchAsync"/>: the same request, but one a charge-gated feed
/// answers while the car is parked (issue #212). That is the whole of the on-demand idea: nothing new
/// to fetch with, only a different reason to fetch.</para>
///
/// <para><b>One feed.</b> This used to ask every registered feed and keep the newest answer, back when
/// an ID.4 ran the Data Act portal beside volkswagen.de. An installation now has exactly one
/// (<see cref="ConfiguredVehicleFeed"/>), so there is nothing to arbitrate.</para>
/// </summary>
public sealed class VehicleStateRefresh(
    ConfiguredVehicleFeed feed,
    VehicleStateHolder holder,
    ILogger<VehicleStateRefresh>? logger = null,
    TimeProvider? time = null) : IVehicleStateRefresh
{
    /// <summary>
    /// How long an ask — or a reading the car captured — is good enough to plan from (#212). A preview
    /// is meant to be pressed several times over ("what if I leave at eight?"), and each press sending
    /// a request would spend a volkswagen.de session, or MyŠkoda's twenty an hour, on the same answer.
    /// </summary>
    public static readonly TimeSpan PlanningReuse = TimeSpan.FromMinutes(5);

    /// <summary>
    /// The longest a plan waits for the car before going on with the held reading. A sign-in to
    /// volkswagen.de can take several round trips, and a form that hangs is worse than one that says
    /// how old its figure is.
    /// </summary>
    public static readonly TimeSpan PlanningWait = TimeSpan.FromSeconds(20);

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly SemaphoreSlim _planning = new(1, 1);
    private DateTimeOffset? _lastAskedAt;

    public bool CanRefresh => feed.Service is not null;

    public async Task<VehicleRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (feed.Service is not { } service)
        {
            return VehicleRefreshResult.NoFeed;
        }

        _lastAskedAt = _time.GetUtcNow();
        string failure;

        try
        {
            var state = await service.AskAsync(cancellationToken).ConfigureAwait(false);

            if (state is not null)
            {
                holder.Set(state);

                var source = state.SourceId ?? service.Manufacturer;
                logger?.LogInformation(
                    "Asked the car: {Source} answered, captured {CapturedAt:u}.", source, state.CapturedAt);

                return VehicleRefreshResult.Fresh(state, source);
            }

            failure = $"{service.DisplayName} had nothing to give — {service.Health.Message}";
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger?.LogWarning(ex, "Asking {Manufacturer} for the car failed.", service.Manufacturer);
            failure = $"{service.DisplayName}: {ex.Message}";
        }

        // Nothing answered. The last known reading goes back with the failure so a caller that can
        // use an old number -- a plan, which would rather be built on something than nothing -- can,
        // and one that cannot simply ignores it.
        return VehicleRefreshResult.Failed(failure, holder.GetCurrentState());
    }

    public async Task PrepareForPlanningAsync(CancellationToken cancellationToken = default)
    {
        // A feed waiting on its owner is not asked on the owner's behalf: replaying a password nobody
        // is there to follow with a code is how accounts get locked. The card already says what to do.
        if (feed.Service is not { Health.IsBlocked: false })
        {
            return;
        }

        // One ask at a time, so two tabs opening together share it rather than sending two.
        await _planning.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            var now = _time.GetUtcNow();

            if (now - _lastAskedAt < PlanningReuse
                || now - holder.GetCurrentState()?.CapturedAt < PlanningReuse)
            {
                return;
            }

            // On the injected clock, so the wait is the same clock everything else here is measured on.
            using var timeout = new CancellationTokenSource(PlanningWait, _time);
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);

            try
            {
                var result = await RefreshAsync(wait.Token).ConfigureAwait(false);

                if (!result.Succeeded)
                {
                    logger?.LogInformation(
                        "Asked the car before planning and it did not answer ({Reason}); planning from the "
                        + "reading held.", result.Message);
                }
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                logger?.LogInformation(
                    "The car did not answer within {Wait} before planning; planning from the reading held.",
                    PlanningWait);
            }
        }
        finally
        {
            _planning.Release();
        }
    }
}
