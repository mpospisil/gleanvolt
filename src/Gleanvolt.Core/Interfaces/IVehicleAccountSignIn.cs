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
///
/// <para><b>Two services now argue with it</b> (issue #193). MyŠkoda's public API wants no session
/// and no code, only an API key the owner creates in the app — so the contract grew by exactly what a
/// key needs: a <see cref="VehicleSignInStatus.KeyRequired"/> state, one <see cref="SubmitAsync"/>
/// that answers whichever of the two the state asked for, and the explanation the page shows. No
/// credential-kind enum and no auth-model taxonomy: the state is what tells the page which box to
/// draw.</para>
/// </summary>
public interface IVehicleAccountSignIn
{
    /// <summary>Which account this signs in to, for the page to name.</summary>
    string AccountName { get; }

    /// <summary>
    /// A sentence or two under the heading saying what signing in here is for and why a person has to
    /// do it. The sign-in's rather than the page's, so a Škoda installation does not read about the
    /// code Volkswagen emails.
    /// </summary>
    string Explanation { get; }

    /// <summary>Whether this is configured at all. False leaves the page saying so and offering nothing.</summary>
    bool IsConfigured { get; }

    /// <summary>Where the last attempt got to, so a page can render without starting one.</summary>
    VehicleSignInState State { get; }

    /// <summary>
    /// Tries the saved session first, then the credentials. Returns what it needs next — most often
    /// a one-time code.
    /// </summary>
    Task<VehicleSignInState> SignInAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Answers what <see cref="State"/> asked for: the one-time code when it wants a code, the API key
    /// when it wants a key. A wrong answer leaves the question open to try again.
    /// </summary>
    Task<VehicleSignInState> SubmitAsync(string answer, CancellationToken cancellationToken = default);

    /// <summary>Forgets the saved session or key, so the next sign-in starts cold.</summary>
    void SignOut();
}
