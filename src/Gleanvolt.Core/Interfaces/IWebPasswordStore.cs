namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// Persists the web UI's password so that setting one survives a restart.
///
/// <para>Declared here and implemented by the host for the same reason as
/// <see cref="IVehicleAccountSignIn"/>: <c>Gleanvolt.Web</c> owns the page that collects the password
/// and knows nothing about where a given deployment keeps its settings. The host, which owns the
/// overrides file, knows exactly that.</para>
///
/// <para>The value handed over is already a hash. A plain password never reaches an implementation of
/// this interface, and never reaches disk.</para>
/// </summary>
public interface IWebPasswordStore
{
    /// <summary>
    /// Writes <paramref name="passwordHash"/> where the next start will read it as
    /// <c>Web:PasswordHash</c>. False when this deployment has nowhere to put it, which the caller
    /// must surface rather than swallow: a password that silently fails to persist is a UI that locks
    /// itself open again on the next restart.
    /// </summary>
    bool Save(string passwordHash);
}
