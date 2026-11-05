using Gleanvolt.Core.Interfaces;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Saves the web UI's password hash into the same overrides file the PV system and vehicle editors
/// write to, so the next start reads it as <c>Web:PasswordHash</c> like any other setting.
///
/// <para>The overrides file rather than the secret store, deliberately. What is written is a hash, not
/// a secret: it is already useless to anyone who reads it, and it belongs with the settings a restart
/// reads rather than with the credentials the controller presents to somebody else. The file is
/// written owner-only either way.</para>
/// </summary>
public sealed class WebPasswordStore(IConfiguration configuration, ILogger<WebPasswordStore>? logger = null)
    : IWebPasswordStore
{
    /// <summary>The configuration key the next start binds <c>WebOptions.PasswordHash</c> from.</summary>
    public const string Key = "Web:PasswordHash";

    /// <summary>Where the recovery key's hash is kept, beside the password's (#301).</summary>
    public const string RecoveryKeyHashKey = "Web:RecoveryKeyHash";

    // Without a key of its own, a new password takes away the old one's: a recovery key that outlived
    // its password would reset the next one.
    public bool Save(string passwordHash) => Write(passwordHash, recoveryKeyHash: null);

    public bool Save(string passwordHash, string recoveryKeyHash) => Write(passwordHash, recoveryKeyHash);

    private bool Write(string passwordHash, string? recoveryKeyHash)
    {
        if (string.IsNullOrWhiteSpace(passwordHash))
        {
            return false;
        }

        var overrides = PvSystemOverrides.Find(configuration);

        if (overrides is null)
        {
            logger?.LogError(
                "The web UI password could not be saved: this host has no overrides file configured, "
                + "so there is nowhere for it to survive a restart. Set Web__PasswordHash directly.");
            return false;
        }

        try
        {
            // Read-modify-write rather than a blind write: the file also holds the site's device
            // addresses and whatever the car editor has saved, and none of that may be lost to a
            // password being set.
            lock (PvSystemOverrides.Gate)
            {
                var values = PvSystemOverrides.Read(overrides.Path);
                values[Key] = passwordHash;

                if (string.IsNullOrWhiteSpace(recoveryKeyHash))
                {
                    values.Remove(RecoveryKeyHashKey);
                }
                else
                {
                    values[RecoveryKeyHashKey] = recoveryKeyHash;
                }

                PvSystemOverrides.Write(overrides.Path, values);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            logger?.LogError(ex, "The web UI password could not be written to {Path}.", overrides.Path);
            return false;
        }

        logger?.LogInformation("A web UI password was set and saved to {Path}.", overrides.Path);
        return true;
    }
}
