using Microsoft.Extensions.Logging;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Vehicles;

/// <summary>
/// Asks every configured feed for the car's state, now, and keeps the best answer (issue #168).
///
/// <para>The same <see cref="IVehicleUpdateService.FetchAsync"/> the polling worker calls on a clock
/// — called instead because somebody asked. That is the whole of the on-demand idea: nothing new to
/// fetch with, only a different reason to fetch.</para>
///
/// <para><b>Every feed is asked, not the first that answers.</b> Feeds carry different fields — the
/// portal's bundles routinely omit the plug state entirely — and the holder already keeps the newest
/// reading rather than the newest source. Asking all of them costs one round trip more and is the
/// difference between a card with a battery and a card with a battery and everything else.</para>
/// </summary>
public sealed class VehicleStateRefresh(
    IEnumerable<IVehicleUpdateService> services,
    VehicleStateHolder holder,
    ILogger<VehicleStateRefresh>? logger = null) : IVehicleStateRefresh
{
    private readonly List<IVehicleUpdateService> _services = services.ToList();

    public bool CanRefresh => _services.Count > 0;

    public async Task<VehicleRefreshResult> RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRefresh)
        {
            return VehicleRefreshResult.NoFeed;
        }

        VehicleState? newest = null;
        string? source = null;
        var failures = new List<string>();

        foreach (var service in _services)
        {
            try
            {
                var state = await service.FetchAsync(cancellationToken).ConfigureAwait(false);

                if (state is null)
                {
                    failures.Add($"{service.Manufacturer} had nothing to give");
                    continue;
                }

                holder.Set(state);

                if (newest is null || state.CapturedAt > newest.CapturedAt)
                {
                    newest = state;
                    source = state.SourceId ?? service.Manufacturer;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                // One feed failing must not cost the answer another one gave. The reason is collected
                // and reported only if nothing answered at all.
                logger?.LogWarning(ex, "Asking {Manufacturer} for the car failed.", service.Manufacturer);
                failures.Add($"{service.Manufacturer}: {ex.Message}");
            }
        }

        if (newest is not null)
        {
            logger?.LogInformation(
                "Asked the car: {Source} answered, captured {CapturedAt:u}.", source, newest.CapturedAt);

            return VehicleRefreshResult.Fresh(newest, source);
        }

        // Nothing answered. The last known reading goes back with the failure so a caller that can
        // use an old number -- a plan, which would rather be built on something than nothing -- can,
        // and one that cannot simply ignores it.
        return VehicleRefreshResult.Failed(
            failures.Count > 0 ? string.Join("; ", failures) : "no feed answered",
            holder.GetCurrentState());
    }
}
