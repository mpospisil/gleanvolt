using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// What grid energy costs when (issue #265), from whichever strategy is selected on Settings.
/// Consumers ask for a rate at an instant and when it next changes; which strategy answered is not
/// their business.
/// </summary>
public interface ITariffSchedule
{
    /// <summary>
    /// The low and high stretches from now as far as the selected strategy knows (Table: to the end of
    /// tomorrow), local time, half-open. Null for <i>no tariff restriction</i>, never for <i>always
    /// low</i>: when the selection is <see cref="TariffProviderIds.None"/>, and when the selected
    /// strategy cannot answer, which <see cref="Unavailable"/> then says.
    /// </summary>
    TariffTimeline? Timeline { get; }

    /// <summary>
    /// Why a strategy other than <see cref="TariffProviderIds.None"/> is selected and there is still no
    /// timeline — a strategy this build does not have, one that failed, one with nothing fetched yet —
    /// or null when nothing is wrong.
    /// </summary>
    string? Unavailable { get; }
}
