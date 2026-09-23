using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Web;

/// <summary>
/// The sign-in for a host with no manufacturer account wired up (issue #170).
///
/// <para>The UI can be hosted without <c>Gleanvolt.Hosting</c>'s registrations, so a page that offers
/// a sign-in must still construct where nothing can answer. Hosting's real one takes precedence when
/// it is present.</para>
///
/// <para><see cref="Explanation"/> is a real sentence rather than <c>string.Empty</c> (issue #227).
/// It is what <c>/car</c>'s account section shows for the case this object is actually reached in
/// anger: a feed that wants an account — volkswagen.de, MyŠkoda — switched on with not enough of its
/// fields filled in for the host to have registered one. An empty string there would leave that
/// installation reading the same silence #227 exists to remove.</para>
/// </summary>
internal sealed class NoVehicleAccountSignIn : IVehicleAccountSignIn
{
    public string AccountName => "no manufacturer account";

    public string Explanation =>
        "No manufacturer account is wired up, so there is nothing to sign in to. A feed that needs one "
        + "brings its own sign-in as soon as every field it requires is filled in and the controller "
        + "has restarted into them.";

    public bool IsConfigured => false;

    public VehicleSignInState State => VehicleSignInState.NotConfigured;

    public Task<VehicleSignInState> SignInAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(VehicleSignInState.NotConfigured);

    public Task<VehicleSignInState> SubmitAsync(
        string answer, CancellationToken cancellationToken = default) =>
        Task.FromResult(VehicleSignInState.NotConfigured);

    public void SignOut()
    {
    }
}
