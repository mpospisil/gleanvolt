namespace Gleanvolt.Plugin.Volkswagen.Volkswagen;

/// <summary>Which of the four situations the page is in, as the built-in's <c>VehicleSignInState</c> names them.</summary>
public enum SignInStep
{
    /// <summary>Nothing tried since the plugin started.</summary>
    Unknown,

    /// <summary>The account is not entered yet.</summary>
    NotConfigured,

    /// <summary>Signed in.</summary>
    SignedIn,

    /// <summary>Volkswagen has emailed a code.</summary>
    CodeRequired,

    /// <summary>The sign-in did not complete.</summary>
    Failed,
}

/// <summary>What the page shows about the sign-in.</summary>
public sealed record SignInState(SignInStep Step, string? Message = null)
{
    public static readonly SignInState Unknown = new(SignInStep.Unknown);

    public static readonly SignInState NotConfigured = new(SignInStep.NotConfigured, "Enter the VW ID, the password and the VIN, and save.");
}

/// <summary>
/// The volkswagen.de account's sign-in (#170, #410): translates the client's login steps into the four
/// states the page renders, and keeps the last one, so opening the page never starts an attempt;
/// refreshing a browser must never replay a password.
///
/// <para>Copied from the built-in. Changed: its own small state record in place of Gleanvolt's
/// <c>VehicleSignInState</c> and its phrases (the plugin's page is English only); the account comes from
/// <see cref="AccountStore"/>; the messages name the plugin's page, not Gleanvolt's settings keys.</para>
/// </summary>
public sealed class VwWebsiteSignIn(AccountStore account, VwWebsiteClient client)
{
    private SignInState _state = SignInState.Unknown;

    public bool IsConfigured => account.Current.IsComplete;

    public SignInState State => IsConfigured ? _state : SignInState.NotConfigured;

    public async Task<SignInState> SignInAsync(CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return _state = SignInState.NotConfigured;
        }

        var step = await client.SignInAsync(cancellationToken).ConfigureAwait(false);
        return _state = Describe(step, afterCode: false);
    }

    public async Task<SignInState> SubmitAsync(string code, CancellationToken cancellationToken = default)
    {
        if (!IsConfigured)
        {
            return _state = SignInState.NotConfigured;
        }

        if (string.IsNullOrWhiteSpace(code))
        {
            return _state = new SignInState(SignInStep.CodeRequired, "Enter the code from the email.");
        }

        var step = await client.SubmitCodeAsync(code, cancellationToken).ConfigureAwait(false);
        return _state = Describe(step, afterCode: true);
    }

    /// <summary>Forgets the session: the next sign-in starts cold and will probably want a code.</summary>
    public void SignOut()
    {
        client.Forget();
        _state = SignInState.Unknown;
    }

    internal static SignInState Describe(VwWebsiteLoginStep step, bool afterCode) => step switch
    {
        VwWebsiteLoginStep.SignedIn => new(SignInStep.SignedIn,
            "Signed in. This browser is remembered, so a restart should not need another code."),

        // The ordinary path on a cold login, not an error: said plainly so it does not read as one.
        VwWebsiteLoginStep.OneTimeCodeRequired when !afterCode => new(SignInStep.CodeRequired,
            "Volkswagen has emailed a one-time code. Enter it below."),

        VwWebsiteLoginStep.OneTimeCodeRequired => new(SignInStep.CodeRequired,
            "That code was not accepted. Check the newest email (an earlier one will not work) and try again. "
            + "The challenge is still open, so this does not need starting over."),

        VwWebsiteLoginStep.OwnerActionRequired => new(SignInStep.Failed,
            "Volkswagen is showing a consent or terms screen. Open volkswagen.de in a browser, clear it there, "
            + "then sign in here again."),

        VwWebsiteLoginStep.CredentialsRequired => new(SignInStep.Failed,
            "The VW ID or the password was not accepted. Check them above."),

        // Nothing left this machine, so none of the lockout advice below applies.
        VwWebsiteLoginStep.NoChallenge => new(SignInStep.Failed,
            "There is no code challenge open, so that code was not sent anywhere. Press Sign in to start one. "
            + "If Volkswagen does not email a new code, press Forget the session first and then Sign in: that "
            + "discards a challenge left over from an earlier attempt."),

        _ => new(SignInStep.Failed,
            "The sign-in did not complete. If this repeats, wait a few minutes rather than retrying: a replayed "
            + "login at a real identity provider risks locking the account."),
    };
}
