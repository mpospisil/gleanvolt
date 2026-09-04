using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web;

/// <summary>
/// The refresh for a host that has no vehicle feeds wired up at all (issue #168).
///
/// <para>The UI can be hosted without <c>Gleanvolt.Hosting</c>'s registrations — the authentication
/// tests do exactly that, and so would any host that serves the dashboard and nothing else. A page
/// that injects <see cref="IVehicleStateRefresh"/> should render there rather than fail to
/// construct, so the Web project supplies a null object and Hosting's real one takes precedence when
/// it is present.</para>
///
/// <para><see cref="CanRefresh"/> is false, which is the same state an installation with no feed
/// configured is in — the page already knows how to say "there is nothing to ask".</para>
/// </summary>
internal sealed class NoVehicleRefresh : IVehicleStateRefresh
{
    public bool CanRefresh => false;

    public Task<VehicleRefreshResult> RefreshAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(VehicleRefreshResult.NoFeed);
}
