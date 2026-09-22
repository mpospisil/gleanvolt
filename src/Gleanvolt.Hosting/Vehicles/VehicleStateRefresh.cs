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
    ILogger<VehicleStateRefresh>? logger = null) : IVehicleStateRefresh
{
    public bool CanRefresh => feed.Service is not null;

    public async Task<VehicleRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (feed.Service is not { } service)
        {
            return VehicleRefreshResult.NoFeed;
        }

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
}
