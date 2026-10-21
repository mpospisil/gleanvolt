using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web;

/// <summary>
/// The tariff schedule for a host that has no tariff wired up at all (issue #265), on the pattern of
/// <see cref="NoVehicleRefresh"/>: the Overview reads the schedule, and should render in a host without
/// <c>Gleanvolt.Hosting</c>'s registrations rather than fail to construct. Hosting's real one takes
/// precedence when it is present. No timeline is no restriction, which the Overview says nothing about.
/// </summary>
internal sealed class NoTariffSchedule : ITariffSchedule
{
    public TariffTimeline? Timeline => null;

    public string? Unavailable => null;

    public string Currency => string.Empty;
}
