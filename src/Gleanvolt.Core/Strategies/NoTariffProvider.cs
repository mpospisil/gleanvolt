using System.Text.Json;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Strategies;

/// <summary>
/// The tariff strategy that knows of no tariff (issue #265): no timeline, which every consumer reads as
/// <i>no restriction</i>. A strategy like the others rather than a null check, so that "nothing selected"
/// and "something selected" take the same path through the schedule.
/// </summary>
public sealed class NoTariffProvider : ITariffProvider
{
    public string Id => TariffProviderIds.None;

    public string Name => "None";

    public IReadOnlyList<TariffProblem> Check(JsonElement settings) => [];

    public TariffTimeline? Expand(TariffContext context) => null;
}
