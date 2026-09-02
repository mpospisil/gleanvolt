using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// An <see cref="IVehicleUpdateService"/> that can also say how its running is going — how often it
/// has asked, when it will ask next, and how long it has held its session (issue #140).
///
/// <para><b>A second interface rather than five more members on the first.</b> The update contract
/// stays as small as #140 left it: what every feed must answer is whether it can produce a reading,
/// and nothing else. A session age and a next-due time are facts about a <i>polling</i> feed with a
/// <i>held</i> session — a push-based one has neither — so they are offered by the services that have
/// them and asked for with a type test by the one page that cares.</para>
/// </summary>
public interface IVehicleFeedDiagnostics
{
    /// <summary>How the feed's own running is going. Cheap and synchronous, like <see cref="IVehicleUpdateService.Health"/>.</summary>
    VehicleFeedDiagnostics Diagnostics { get; }
}
