using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Signing the controller in to a manufacturer account that wants a person (issue #170).
///
/// <para>The seam lives in Core because the web UI drives it and the web UI sees only Core — the
/// arrangement <see cref="IChargeActions"/> has.</para>
///
/// <para><b>Why a person is unavoidable here.</b> volkswagen.de's cold login always demands a
/// one-time code emailed to the account owner — verified against the live account, not assumed. That
/// makes it hostile to a background service and perfectly ordinary at the moment a plan is being
/// prepared, because the owner is already at the screen. So this is driven from a page, once, and the
/// session it establishes is what the polling then runs on.</para>
/// </summary>
public interface IVehicleAccountSignIn
{
    /// <summary>Which account this signs in to, for the page to name.</summary>
    string AccountName { get; }

    /// <summary>Whether this is configured at all. False leaves the page saying so and offering nothing.</summary>
    bool IsConfigured { get; }

    /// <summary>Where the last attempt got to, so a page can render without starting one.</summary>
    VehicleSignInState State { get; }

    /// <summary>
    /// Tries the saved session first, then the credentials. Returns what it needs next — most often
    /// a one-time code.
    /// </summary>
    Task<VehicleSignInState> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>Answers the code challenge. A wrong code leaves the challenge open to try again.</summary>
    Task<VehicleSignInState> SubmitCodeAsync(string code, CancellationToken cancellationToken = default);

    /// <summary>Forgets the saved session, so the next sign-in starts cold.</summary>
    void SignOut();
}
