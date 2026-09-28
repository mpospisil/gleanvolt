namespace Gleanvolt.Web.Auth;

/// <summary>
/// The password hash actually in force, as opposed to the one configuration held at startup.
///
/// <para>It exists because <see cref="WebOptions"/> is an <c>IOptions&lt;&gt;</c> snapshot: setting a
/// password through the setup page would otherwise not be visible until a restart, and the window
/// between saving and restarting is precisely the window this whole feature exists to close. The
/// setup gate and the login page both read this, so a password becomes effective the instant it is
/// set.</para>
///
/// <para>Seeded from configuration at startup, so an operator who configured
/// <c>Web__PasswordHash</c> the old way is unaffected and never sees a setup page.</para>
/// </summary>
public sealed class WebPasswordState
{
    private volatile string _hash;

    public WebPasswordState(string? configured) => _hash = configured?.Trim() ?? "";

    /// <summary>The hash to verify a sign-in against. Empty when none has been set.</summary>
    public string Hash => _hash;

    /// <summary>Whether a password exists at all. False is what puts the UI into setup.</summary>
    public bool IsSet => _hash.Length > 0;

    /// <summary>Records a newly set hash. Only ever called after it has been persisted.</summary>
    public void Set(string hash) => _hash = hash?.Trim() ?? "";
}
