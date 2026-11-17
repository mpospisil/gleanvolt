using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Gleanvolt.Providers.Client;

/// <summary>
/// Where a provider keeps its own secrets (#416): an account's password, a session's cookies, an API
/// key. One value per name, as text.
/// </summary>
public interface IProviderSecretStore
{
    /// <summary>The directory the secrets are in.</summary>
    string Directory { get; }

    /// <summary>
    /// True when the values are encrypted at rest (DPAPI on Windows); false when only the files'
    /// permissions protect them (0600 on Linux and macOS). Say which to the owner.
    /// </summary>
    bool IsEncrypted { get; }

    /// <summary>The value, or null when there is none or it can't be read: treat both as "ask the owner again".</summary>
    string? Read(string name);

    /// <summary>Stores the value, replacing any; false when it couldn't be written. Never leaves half a value.</summary>
    bool Write(string name, string value);

    /// <summary>Removes the value; true when it is gone, including when there was none.</summary>
    bool Delete(string name);
}

/// <summary>
/// The SDK's secret store (#416), what Gleanvolt's own store does for its built-in feeds:
/// <list type="bullet">
/// <item>Linux and macOS: a file per name, readable and writable by the provider's user alone (0600), in
/// a directory only that user can enter (0700). Not encrypted: the permissions, and whoever can read the
/// volume or the disk, are what protect it.</item>
/// <item>Windows: a file per name, encrypted with DPAPI for the current user.</item>
/// </list>
/// Every write goes to a temporary file first and replaces the old one in one step, so a crash leaves
/// the old value, never half a new one. A name is a file name of letters, digits, dots, dashes and
/// underscores, so no name can reach outside the directory.
/// </summary>
public sealed partial class ProviderSecretStore : IProviderSecretStore
{
    // Binds a value to this use: DPAPI output from another program, for the same user, does not open here.
    private static readonly byte[] Entropy = "Gleanvolt provider secret store v1"u8.ToArray();

    private const UnixFileMode OwnerFile = UnixFileMode.UserRead | UnixFileMode.UserWrite;
    private const UnixFileMode OwnerDirectory = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;

    private readonly Lock _gate = new();

    /// <param name="directory">Where the files go; created on the first write. Relative paths are taken from the current directory.</param>
    public ProviderSecretStore(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        Directory = Path.GetFullPath(directory);
    }

    public string Directory { get; }

    public bool IsEncrypted => OperatingSystem.IsWindows();

    public string? Read(string name)
    {
        var path = PathOf(name);

        lock (_gate)
        {
            try
            {
                if (!File.Exists(path))
                {
                    return null;
                }

                return OperatingSystem.IsWindows() ? Unseal(File.ReadAllBytes(path)) : File.ReadAllText(path);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                // Unreadable is the same answer as absent: ask the owner again. A value sealed for another
                // Windows user, or written before encryption, is one of these.
                return null;
            }
        }
    }

    public bool Write(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        var path = PathOf(name);

        lock (_gate)
        {
            var temporary = path + ".tmp";

            try
            {
                EnsureDirectory();

                if (OperatingSystem.IsWindows())
                {
                    File.WriteAllBytes(temporary, Seal(value));
                }
                else
                {
                    using (var stream = new FileStream(temporary, new FileStreamOptions
                    {
                        Mode = FileMode.Create,
                        Access = FileAccess.Write,
                        UnixCreateMode = OwnerFile,
                    }))
                    using (var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false)))
                    {
                        writer.Write(value);
                    }

                    // UnixCreateMode covers a new file only; a temporary file left by a crash keeps its mode.
                    File.SetUnixFileMode(temporary, OwnerFile);
                }

                File.Move(temporary, path, overwrite: true);
                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
            {
                TryDelete(temporary);
                return false;
            }
        }
    }

    public bool Delete(string name)
    {
        var path = PathOf(name);

        lock (_gate)
        {
            try
            {
                // File.Delete throws when the directory isn't there yet; absent is deleted all the same.
                if (File.Exists(path))
                {
                    File.Delete(path);
                }

                return true;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return false;
            }
        }
    }

    private string PathOf(string name)
    {
        if (name is null || !SafeName().IsMatch(name) || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "A secret's name is a file name of letters, digits, dots, dashes and underscores, starting with a letter or digit.", nameof(name));
        }

        return Path.Combine(Directory, name);
    }

    private void EnsureDirectory()
    {
        if (System.IO.Directory.Exists(Directory))
        {
            return;
        }

        if (OperatingSystem.IsWindows())
        {
            System.IO.Directory.CreateDirectory(Directory);
        }
        else
        {
            System.IO.Directory.CreateDirectory(Directory, OwnerDirectory);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A leftover temporary file is replaced by the next write.
        }
    }

    [SupportedOSPlatform("windows")]
    private static byte[] Seal(string value) =>
        ProtectedData.Protect(Encoding.UTF8.GetBytes(value), Entropy, DataProtectionScope.CurrentUser);

    [SupportedOSPlatform("windows")]
    private static string Unseal(byte[] sealedBytes) =>
        Encoding.UTF8.GetString(ProtectedData.Unprotect(sealedBytes, Entropy, DataProtectionScope.CurrentUser));

    [GeneratedRegex("^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$")]
    private static partial Regex SafeName();
}

/// <summary>Registration of the secret store.</summary>
public static class ProviderSecretStoreExtensions
{
    /// <summary>
    /// Registers <see cref="IProviderSecretStore"/>, keeping its files in <paramref name="directory"/>
    /// (#416). Give the directory a volume of its own in a container, and don't share it.
    /// </summary>
    public static IServiceCollection AddGleanvoltProviderSecrets(this IServiceCollection services, string directory)
    {
        var store = new ProviderSecretStore(directory);
        services.TryAddSingleton<IProviderSecretStore>(store);
        return services;
    }
}
