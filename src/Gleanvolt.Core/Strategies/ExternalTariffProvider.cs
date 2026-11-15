using System.Text.Json;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The tariff strategy an external provider feeds (#401): the windows it last published over the
/// provider hub, kept here as one immutable snapshot. Nothing of it is in <c>tariff.json</c>; the
/// provider's token is in the secret store.
///
/// <para><b>Answers until its data runs out.</b> A tariff stays true after its provider disconnects,
/// so a disconnect changes nothing until the last window ends. After that the timeline is null and the
/// schedule says the provider has nothing to answer from, which is no restriction.</para>
///
/// <para><b>Asks without waiting.</b> <see cref="RefreshAsync"/> sends the request and returns; the
/// answer arrives as a publish of its own. The refresh worker is never held by a provider.</para>
///
/// <para>Kept across a restart by Hosting's <c>ExternalTariffFile</c>, which restores the last
/// windows before anything reads them and saves every publish: a restarted controller answers from
/// them until the provider is back, as it does after a disconnect.</para>
/// </summary>
public sealed class ExternalTariffProvider : IRefreshingTariffProvider
{
    private readonly IExternalTariffRequests? _requests;
    private volatile Snapshot? _snapshot;

    /// <param name="requests">How to ask the provider for fresh data; null where nothing can be asked.</param>
    public ExternalTariffProvider(IExternalTariffRequests? requests = null)
    {
        _requests = requests;
    }

    public string Id => TariffProviderIds.External;

    public string Name => "External";

    /// <summary>Hourly, in case the provider's data changed without it publishing.</summary>
    public TimeSpan RefreshInterval => TimeSpan.FromHours(1);

    /// <summary>Raised after new windows are stored, so the schedule can drop what it expanded.</summary>
    public event Action? Published;

    /// <summary>The last accepted windows, or null when nothing has been published since the start.</summary>
    public IReadOnlyList<TariffWindow>? Windows => _snapshot?.Windows;

    /// <summary>Who published them and when.</summary>
    public TariffOrigin? Origin => _snapshot?.Origin;

    /// <summary>Nothing to set: the selection is the switch, and the token is in the secret store.</summary>
    public IReadOnlyList<TariffProblem> Check(JsonElement settings) => [];

    /// <summary>
    /// Replaces the windows with <paramref name="windows"/>, which <see cref="Configuration.ExternalTariffRules"/>
    /// has accepted and joined.
    /// </summary>
    public void Publish(IReadOnlyList<TariffWindow> windows, TariffOrigin origin)
    {
        _snapshot = new Snapshot([.. windows], origin);
        Published?.Invoke();
    }

    public TariffTimeline? Expand(TariffContext context)
    {
        if (_snapshot is not { } snapshot)
        {
            return null;
        }

        // From the start of today, like Table: the day's earlier windows are still worth a chart.
        var startOfToday = new DateTimeOffset(context.Today.ToDateTime(TimeOnly.MinValue),
            context.Zone.GetUtcOffset(context.Today.ToDateTime(TimeOnly.MinValue)));

        // Every window that ends after the start of today: the earlier ones still read on a chart of
        // the day. Null once none is left to come, which the schedule reports as nothing to answer from.
        if (!snapshot.Windows.Any(window => window.Window.End > context.Now))
        {
            return null;
        }

        var windows = snapshot.Windows
            .Where(window => window.Window.End > startOfToday)
            .Select(window => window with
            {
                Window = new TimeWindow(
                    TimeZoneInfo.ConvertTime(window.Window.Start, context.Zone),
                    TimeZoneInfo.ConvertTime(window.Window.End, context.Zone)),
            });

        return new TariffTimeline([.. windows], context.Prices, Id, snapshot.Origin);
    }

    public async Task RefreshAsync(JsonElement settings, CancellationToken cancellationToken)
    {
        if (_requests is not null)
        {
            await _requests.RequestRefreshAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed record Snapshot(IReadOnlyList<TariffWindow> Windows, TariffOrigin Origin);
}
