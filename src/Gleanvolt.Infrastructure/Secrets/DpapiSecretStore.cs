using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using Gleanvolt.Core.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Gleanvolt.Infrastructure.Secrets;

/// <summary>
/// Secrets sealed by Windows' Data Protection API (issue #215), on top of the same files
/// <see cref="FileSecretStore"/> writes.
///
/// <para><b>Why here and not on Linux.</b> DPAPI is the one option that gives the unattended restart
/// a key the process does not have to keep beside the secret: the key is held by the OS against the
/// signed-in account, so there is no key file to lose, rotate or back up by accident. It earns its
/// place on the zip and winget installs for a reason specific to them — <c>data\</c> sits inside the
/// package directory, which an upgrade may replace and which people copy around wholesale.</para>
///
/// <para><b><see cref="DataProtectionScope.CurrentUser"/>, never
/// <see cref="DataProtectionScope.LocalMachine"/>.</b> Machine scope is decryptable by every account
/// on the box, which on a shared Windows machine is weaker than the file permissions it was meant to
/// improve on.</para>
///
/// <para><b>Not in a Windows container.</b> <c>Dockerfile.windows</c> runs under an ephemeral built-in
/// account, and a secret sealed to that profile is undecryptable the moment the container is
/// recreated — "my session expired" becomes "my session is gone forever". The container selects the
/// file store instead, explicitly; see <see cref="SecretStoreSelection"/>.</para>
///
/// <para><b>It still is not encryption we can promise.</b> Anything running as this user undoes it
/// unattended, because the service must. <see cref="Describe"/> says whose key it is and stops
/// there.</para>
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore : ISecretStore
{
    /// <summary>
    /// Marks a payload as this store's. Without it a file left from before #215 — or from a run that
    /// chose the file store — would be fed to <c>Unprotect</c> and come back as a crypto exception
    /// rather than as the plaintext it plainly is. Versioned so a later format has somewhere to go.
    /// </summary>
    private const string Marker = "GV-DPAPI-1:";

    /// <summary>
    /// Not a key, and not secret. DPAPI mixes it into the derivation, so a payload sealed by this
    /// application cannot be handed to <c>Unprotect</c> by another one running as the same user
    /// without knowing it. Fixed, because a per-install value would be a key file by another name.
    /// </summary>
    private static readonly byte[] Entropy = "Gleanvolt secret store v1"u8.ToArray();

    private readonly FileSecretStore _files;
    private readonly ILogger _logger;

    public DpapiSecretStore(FileSecretStore files, ILogger<DpapiSecretStore>? logger = null)
    {
        _files = files;
        _logger = logger ?? (ILogger)NullLogger.Instance;
    }

    /// <inheritdoc />
    public string? Read(string name)
    {
        if (_files.Read(name) is not { } stored)
        {
            return null;
        }

        if (!stored.StartsWith(Marker, StringComparison.Ordinal))
        {
            // A plaintext file from before this store, or from a run under the file store. Read it and
            // seal it, so that an upgrade never demands a fresh sign-in (#215) and a second start finds
            // it protected. A failure to reseal is not a failure to read: the secret is good.
            _logger.LogInformation(
                "The stored {Secret} was in plain text; it has been re-sealed with DPAPI for this user.", name);

            Write(name, stored);
            return stored;
        }

        try
        {
            var sealedBytes = Convert.FromBase64String(stored[Marker.Length..]);
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            // Truncated, or sealed by a different Windows profile -- a restored backup, a rebuilt
            // machine, a recreated container. Reported as "there is no secret", which every caller
            // already turns into "sign in again", rather than thrown at a start-up path that would
            // otherwise fail with a crypto exception nobody can act on. The message names the secret
            // and never its contents.
            _logger.LogWarning(
                "The stored {Secret} could not be unsealed ({Reason}); it was sealed for a different "
                + "Windows profile, or the file is damaged. Sign in again to replace it.",
                name, ex.GetType().Name);

            return null;
        }
    }

    /// <inheritdoc />
    public bool Write(string name, string value)
    {
        try
        {
            var sealedBytes = ProtectedData.Protect(
                Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);

            return _files.Write(name, Marker + Convert.ToBase64String(sealedBytes));
        }
        catch (CryptographicException ex)
        {
            // No user profile loaded, which is how DPAPI fails under a service account that has never
            // logged on. False is the seam's "it did not stick", and the caller says a restart will
            // ask for the secret again -- the value in memory still works until then.
            _logger.LogWarning(
                "The {Secret} could not be sealed with DPAPI ({Reason}); it is in use but will not "
                + "survive a restart.", name, ex.GetType().Name);

            return false;
        }
    }

    /// <inheritdoc />
    public bool Delete(string name) => _files.Delete(name);

    /// <inheritdoc />
    public string Describe() => "Windows DPAPI, this user";
}
