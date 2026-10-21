using System.Text.Json;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// What grid energy costs when (issue #265): the selected tariff strategy's timeline, the rate now and
/// when it changes. Prices are per kWh in <see cref="Currency"/>, the one figure here that is not a
/// base unit: it is money, and the installation's currency is configuration, not a conversion.
///
/// <para><b>No timeline is no restriction</b>, never "always low": <see cref="Windows"/> is empty and
/// <see cref="Now"/> null when the provider is <c>None</c>, and when the selected one cannot answer,
/// which <see cref="Unavailable"/> then says.</para>
/// </summary>
/// <param name="Provider">The selected strategy's id, as GET /tariff/providers lists them.</param>
/// <param name="ProviderName">Its display name, or null when this build does not have it.</param>
/// <param name="Currency">What every price here is in: an ISO 4217 code, <c>Tariff:Currency</c>.</param>
/// <param name="Unavailable">Why a selected strategy other than <c>None</c> has no timeline, or null.</param>
/// <param name="Now">The rate at the moment of the request, or null with no timeline.</param>
/// <param name="Windows">
/// Alternating low and high stretches, half-open, tiling the timeline without a gap: for the
/// <c>Table</c> strategy, from the start of today to the end of tomorrow in local time.
/// </param>
/// <param name="LowPricePerKwh">The shared low-tariff price, or null when not entered.</param>
/// <param name="HighPricePerKwh">The shared high-tariff price, or null when not entered.</param>
public sealed record TariffResponse(
    string Provider,
    string? ProviderName,
    string Currency,
    string? Unavailable,
    TariffNowResponse? Now,
    IReadOnlyList<TariffWindowResponse> Windows,
    decimal? LowPricePerKwh,
    decimal? HighPricePerKwh)
{
    internal static TariffResponse From(ITariffSchedule schedule, ITariffEditor editor, DateTimeOffset now)
    {
        var settings = editor.Read();
        var timeline = schedule.Timeline;
        var provider = editor.Providers.FirstOrDefault(candidate => settings.Selects(candidate.Id));

        return new TariffResponse(
            provider?.Id ?? settings.Provider,
            provider?.Name,
            schedule.Currency,
            schedule.Unavailable,
            timeline?.WindowAt(now) is { } current
                ? new TariffNowResponse(current.Rate, timeline.PriceAt(now), timeline.NextChangeAfter(now))
                : null,
            [
                .. (timeline?.Windows ?? []).Select(window => new TariffWindowResponse(
                    window.Window.Start, window.Window.End, window.Rate, timeline!.PriceAt(window.Window.Start))),
            ],
            settings.LowPricePerKwh,
            settings.HighPricePerKwh);
    }
}

/// <summary>The rate at one moment.</summary>
/// <param name="Rate">Which rate.</param>
/// <param name="PricePerKwh">What a grid kWh costs now, or null without prices.</param>
/// <param name="Until">When the rate next changes, or null when that is beyond what the strategy knows.</param>
public sealed record TariffNowResponse(TariffRate Rate, decimal? PricePerKwh, DateTimeOffset? Until);

/// <summary>One stretch of a single rate, <c>[start, end)</c>.</summary>
/// <param name="Start">When it begins.</param>
/// <param name="End">When it ends; the next window's start.</param>
/// <param name="Rate">Which rate.</param>
/// <param name="PricePerKwh">The price throughout it: the window's own, or its rate's shared one; null without either.</param>
public sealed record TariffWindowResponse(DateTimeOffset Start, DateTimeOffset End, TariffRate Rate, decimal? PricePerKwh);

/// <summary>A tariff strategy this build has.</summary>
/// <param name="Id">What <c>provider</c> in the settings is set to, to select it.</param>
/// <param name="Name">What Settings shows.</param>
/// <param name="Selected">Whether it is the one in use.</param>
public sealed record TariffProviderResponse(string Id, string Name, bool Selected);

/// <summary>
/// The tariff settings to store (issue #265), as Settings saves them: the selection, the shared prices,
/// and strategies' own settings under their keys. Applied at once, with no restart.
/// </summary>
/// <param name="Provider">The strategy to select, by id: <c>None</c> for no tariff.</param>
/// <param name="LowPricePerKwh">The low-tariff price per kWh, or null for none.</param>
/// <param name="HighPricePerKwh">The high-tariff price per kWh, or null for none.</param>
/// <param name="Settings">
/// Strategies' own settings by key (<c>table</c> for <c>Table</c>: <c>weekday</c> and <c>weekend</c>
/// lists of <c>{ "start": "HH:mm", "end": "HH:mm" }</c>, with an end of <c>24:00</c> allowed). A key
/// given replaces that strategy's settings; one left out is kept as it is, so selecting a strategy never
/// costs another one its settings.
/// </param>
public sealed record TariffSettingsRequest(
    string Provider,
    decimal? LowPricePerKwh,
    decimal? HighPricePerKwh,
    IReadOnlyDictionary<string, JsonElement>? Settings);

/// <summary>The tariff settings as stored.</summary>
/// <param name="Provider">The selected strategy's id.</param>
/// <param name="LowPricePerKwh">The low-tariff price per kWh, or null.</param>
/// <param name="HighPricePerKwh">The high-tariff price per kWh, or null.</param>
/// <param name="Settings">Every strategy's settings by key, including those of strategies this build lacks.</param>
/// <param name="Warnings">What a save went through despite: odd, not unusable. Empty on a read.</param>
public sealed record TariffSettingsResponse(
    string Provider,
    decimal? LowPricePerKwh,
    decimal? HighPricePerKwh,
    IReadOnlyDictionary<string, JsonElement> Settings,
    IReadOnlyList<TariffProblemResponse> Warnings)
{
    internal static TariffSettingsResponse From(TariffSettings settings, IEnumerable<TariffProblem>? warnings = null) => new(
        settings.Provider,
        settings.LowPricePerKwh,
        settings.HighPricePerKwh,
        settings.Sections.ToDictionary(entry => entry.Key, entry => entry.Value),
        [.. (warnings ?? []).Select(TariffProblemResponse.From)]);
}

/// <summary>One thing wrong with tariff settings, where it is, and whether it stopped the save.</summary>
/// <param name="Field">
/// What it is about: <c>provider</c>, <c>lowPricePerKwh</c>, <c>highPricePerKwh</c>, or a path into a
/// strategy's settings such as <c>table.weekday</c>.
/// </param>
/// <param name="Row">The row of a list field, counted from zero, or null.</param>
/// <param name="Message">What is wrong, as Settings says it.</param>
/// <param name="IsWarning">True when the settings were saved regardless.</param>
public sealed record TariffProblemResponse(string Field, int? Row, string Message, bool IsWarning)
{
    internal static TariffProblemResponse From(TariffProblem problem) =>
        new(problem.Field, problem.Row, problem.Message, problem.IsWarning);
}
