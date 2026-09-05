using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web;

/// <summary>
/// The sign-in for a host with no manufacturer account wired up (issue #170).
///
/// <para>The UI can be hosted without <c>Gleanvolt.Hosting</c>'s registrations, so a page that offers
/// a sign-in must still construct where nothing can answer. Hosting's real one takes precedence when
/// it is present.</para>
/// </summary>
internal sealed class NoVehicleAccountSignIn : IVehicleAccountSignIn
{
    public string AccountName => "no manufacturer account";

    public bool IsConfigured => false;

    public VehicleSignInState State => VehicleSignInState.NotConfigured;

    public Task<VehicleSignInState> SignInAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(VehicleSignInState.NotConfigured);

    public Task<VehicleSignInState> SubmitCodeAsync(
        string code, CancellationToken cancellationToken = default) =>
        Task.FromResult(VehicleSignInState.NotConfigured);

    public void SignOut()
    {
    }
}
