using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Infrastructure.Secrets;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Where the secret store keeps its files, and the store opened before the container exists (issue #215): for the
/// configuration sources that read secrets (<see cref="IntegrationSecrets"/>), the CLI and the data-protection keys.
/// </summary>
public static class SecretStoreLocation
{
    /// <summary>
    /// Where the secret store keeps its files: <c>Secrets:Directory</c>, resolved against the content
    /// root when relative, as the SQLite stores and <c>pv-system.json</c> are. The .deb sets it
    /// absolutely, because its content root is a read-only <c>/opt/gleanvolt</c>.
    /// </summary>
    public static string Directory(SecretsOptions secrets, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(secrets);

        return Path.IsPathRooted(secrets.Directory)
            ? secrets.Directory
            : Path.Combine(contentRoot, secrets.Directory);
    }

    /// <summary>
    /// The store the host will register, opened for a caller that needs it before the container exists.
    /// Silent: the registered instance is the one that logs, and two copies of the same startup line
    /// would only invite the question of which store is which.
    /// </summary>
    public static ISecretStore Open(IConfiguration configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var secrets = configuration.GetSection(SecretsOptions.SectionName).Get<SecretsOptions>() ?? new SecretsOptions();

        var choice = SecretStoreSelection.Choose(
            secrets.Store, OperatingSystem.IsWindows(), SecretStoreSelection.RunningInContainer());

        return SecretStoreSelection.Create(choice, Directory(secrets, contentRoot), NullLoggerFactory.Instance);
    }
}
