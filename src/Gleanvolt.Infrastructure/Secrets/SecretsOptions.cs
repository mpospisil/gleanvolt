namespace Gleanvolt.Infrastructure.Secrets;

/// <summary>
/// Where the controller's secrets live and what protects them (issue #215). Bound from
/// <c>Secrets</c>.
/// </summary>
public sealed class SecretsOptions
{
    public const string SectionName = "Secrets";

    /// <summary>
    /// The data directory the secret files live in, resolved against the content root when relative —
    /// the rule the SQLite stores and <c>pv-system.json</c> already follow, so a relative path means
    /// the same thing under <c>dotnet run</c>, the debugger and the container.
    ///
    /// <para>Set absolutely by the .deb's systemd unit, where the content root is a read-only
    /// <c>/opt/gleanvolt</c>. <b>This directory is secret-bearing</b>: whoever can read it reads the
    /// car.</para>
    /// </summary>
    public string Directory { get; init; } = "data";

    /// <summary>
    /// Which store to use. <see cref="SecretStoreKind.Auto"/> is right everywhere; the other two are
    /// for saying otherwise on purpose, and whichever is chosen is named in the startup log.
    /// </summary>
    public SecretStoreKind Store { get; init; } = SecretStoreKind.Auto;
}

/// <summary>Which implementation of the secret store a deployment uses.</summary>
public enum SecretStoreKind
{
    /// <summary>
    /// Decided from the platform: DPAPI on Windows outside a container, owner-only files everywhere
    /// else. The decision is logged, so "auto" never means "unknown".
    /// </summary>
    Auto,

    /// <summary>Owner-only files. Linux, Docker, the .deb, and the Windows container.</summary>
    File,

    /// <summary>Windows DPAPI at <c>CurrentUser</c> scope. The Windows zip and winget installs.</summary>
    Dpapi,
}
