using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// The volkswagen.de account behind <see cref="IVehicleAccountSignIn"/> (issue #170).
///
/// <para>Translates the client's login steps into the four states a page has to render, and keeps the
/// last one so a page can be opened without starting an attempt — refreshing a browser must never
/// replay a password.</para>
/// </summary>
public sealed class VwWebsiteSignIn(VwWebsiteOptions options, VwWebsiteClient client) : IVehicleAccountSignIn
{
    private VehicleSignInState _state = VehicleSignInState.Unknown;

    public string AccountName => "volkswagen.de";

    public bool IsConfigured => options.IsConfigured;

    public VehicleSignInState State => IsConfigured ? _state : VehicleSignInState.NotConfigured;

    public async Task<VehicleSignInState> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return _state = VehicleSignInState.NotConfigured;
        }

        var step = await client.SignInAsync(cancellationToken).ConfigureAwait(false);
        return _state = Describe(step, afterCode: false);
    }

    public async Task<VehicleSignInState> SubmitCodeAsync(
        string code, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return _state = VehicleSignInState.NotConfigured;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return _state = VehicleSignInState.CodeRequired("Enter the code from the email.");
        }

        var step = await client.SubmitCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return _state = Describe(step, afterCode: true);
    }

    public void SignOut()
    {
        client.Forget();
        _state = VehicleSignInState.Unknown;
    }

    private static VehicleSignInState Describe(VwWebsiteLoginStep step, bool afterCode) => step switch
    {
        VwWebsiteLoginStep.SignedIn => VehicleSignInState.SignedIn(
            "Signed in. This browser is remembered, so a restart should not need another code."),

        // The ordinary path on a cold login, not an error -- said plainly so it does not read as one.
        VwWebsiteLoginStep.OneTimeCodeRequired when !afterCode => VehicleSignInState.CodeRequired(
            "Volkswagen has emailed a one-time code. Enter it below."),

        VwWebsiteLoginStep.OneTimeCodeRequired => VehicleSignInState.CodeRequired(
            "That code was not accepted. Check the newest email — an earlier one will not work — and "
            + "try again. The challenge is still open, so this does not need starting over."),

        VwWebsiteLoginStep.OwnerActionRequired => VehicleSignInState.Failed(
            "Volkswagen is showing a consent or terms screen. Open volkswagen.de in a browser, clear "
            + "it there, then sign in here again."),

        VwWebsiteLoginStep.CredentialsRequired => VehicleSignInState.Failed(
            "The credentials were not accepted. Check Vehicle:Website:Username and :Password."),

        _ => VehicleSignInState.Failed(
            "The sign-in did not complete. If this repeats, wait a few minutes rather than retrying: "
            + "a replayed login at a real identity provider risks locking the account."),
    };
}
