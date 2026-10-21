using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// What grid energy costs when (issue #265), from whichever provider is configured. Consumers ask for
/// a rate at an instant and when it next changes; where the schedule came from is not their business.
/// </summary>
public interface ITariffSchedule
{
    /// <summary>
    /// The low and high stretches from the start of today to at least the end of tomorrow, local time,
    /// half-open. Null when the provider is <see cref="TariffProvider.None"/>, which every consumer
    /// reads as <i>no tariff restriction</i>, never as <i>always low</i>.
    /// </summary>
    TariffTimeline? Timeline { get; }
}
