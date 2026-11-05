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
    // One object, swapped whole: a password and the recovery key that resets it change together, and
    // a reader must never see the new password beside the old key.
    private volatile Secrets _current;

    public WebPasswordState(string? configured, string? recoveryKeyHash = null) =>
        _current = new Secrets(configured?.Trim() ?? "", recoveryKeyHash?.Trim() ?? "");

    /// <summary>The hash to verify a sign-in against. Empty when none has been set.</summary>
    public string Hash => _current.Hash;

    /// <summary>The hash of the recovery key (#301). Empty when this password has none.</summary>
    public string RecoveryKeyHash => _current.RecoveryKeyHash;

    /// <summary>Whether a password exists at all. False is what puts the UI into setup.</summary>
    public bool IsSet => _current.Hash.Length > 0;

    /// <summary>
    /// What a sign-in cookie carries to prove it was issued under this password (#301): changing the
    /// password changes the stamp, and every cookie issued before it stops being accepted.
    /// </summary>
    public string Stamp => PasswordStamp.Of(_current.Hash);

    /// <summary>Records a newly set hash. Only ever called after it has been persisted.</summary>
    public void Set(string hash) => _current = new Secrets(hash?.Trim() ?? "", _current.RecoveryKeyHash);

    /// <summary>Records a new password and its recovery key together, after both have been persisted.</summary>
    public void Set(string hash, string recoveryKeyHash) =>
        _current = new Secrets(hash?.Trim() ?? "", recoveryKeyHash?.Trim() ?? "");

    private sealed record Secrets(string Hash, string RecoveryKeyHash);
}
