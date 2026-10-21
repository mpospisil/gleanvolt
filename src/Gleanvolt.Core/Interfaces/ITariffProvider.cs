using System.Text.Json;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// One way of knowing when the grid is cheap (issue #265): a strategy, selected on Settings by
/// <see cref="Id"/> and switched at runtime. <see cref="ITariffSchedule"/> asks whichever one is
/// selected, and nothing that reads the schedule knows which one that is.
///
/// <para><b>Adding one</b> is an implementation registered as <see cref="ITariffProvider"/>, its
/// settings under its own key in <c>tariff.json</c> and, if it has any, a panel registered against its
/// id in the web UI. If it has to fetch, it implements <see cref="IRefreshingTariffProvider"/> too.</para>
///
/// <para><b>The contract</b>, which a shared test holds every strategy to: windows in order, half-open,
/// tiling without a gap, no two neighbours on the same rate; and <see cref="Expand"/> neither throws on
/// settings it cannot use nor does any I/O. The timeline may stop short of tomorrow when the strategy
/// does not know that far.</para>
/// </summary>
public interface ITariffProvider
{
    /// <summary>What <c>tariff.json</c> stores and Settings selects by: <see cref="TariffProviderIds"/>, or a new strategy's own.</summary>
    string Id { get; }

    /// <summary>What the Settings control shows.</summary>
    string Name { get; }

    /// <summary>
    /// Everything wrong with <paramref name="settings"/>, its section of <c>tariff.json</c>, before a
    /// save. An undefined element means none saved yet and must be accepted as the defaults.
    /// </summary>
    IReadOnlyList<TariffProblem> Check(JsonElement settings);

    /// <summary>
    /// <paramref name="settings"/> as it is to be stored, for a strategy that keeps them in a canonical
    /// form. Only called on settings <see cref="Check"/> found no error in.
    /// </summary>
    JsonElement Normalize(JsonElement settings) => settings;

    /// <summary>
    /// The windows from what this strategy already holds, or null for <i>no restriction</i>. Called by
    /// the poll loop, so synchronous and without I/O.
    /// </summary>
    TariffTimeline? Expand(TariffContext context);
}

/// <summary>
/// A strategy that has to fetch: a distributor's feed, a price API, another system's entity. One
/// worker refreshes the <i>selected</i> strategy only, when it is selected or saved and then every
/// <see cref="RefreshInterval"/>; <see cref="ITariffProvider.Expand"/> answers from the last good fetch,
/// and with nothing good to answer from it returns null.
/// </summary>
public interface IRefreshingTariffProvider : ITariffProvider
{
    /// <summary>How long a fetch is good for before the next one.</summary>
    TimeSpan RefreshInterval { get; }

    /// <summary>
    /// Fetches what <see cref="ITariffProvider.Expand"/> will answer from. Throws when the fetch failed;
    /// the worker logs it and keeps whatever the last good fetch left.
    /// </summary>
    Task RefreshAsync(JsonElement settings, CancellationToken cancellationToken);
}
