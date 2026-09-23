using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Secrets;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// The manufacturer account's password, from the secret store into the configuration the feeds are
/// built from (issues #214, #215).
///
/// <para><b>Why a configuration source rather than a lookup at each feed.</b> The password is one of
/// the things that decides whether a feed is configured at all, and that decision is taken in the
/// composition root before any service exists to ask. Supplying it the way every other setting is
/// supplied means <c>VwWebsiteOptions.IsConfigured</c>, <c>VwGroupPortalOptionsResolver</c> and the
/// startup log all go on working unchanged, instead of each learning about a second place a password
/// can come from.</para>
///
/// <para><b>One password, because it is one account.</b> volkswagen.de and the EU Data Act portal are
/// both entered with the owner's VW ID, so <see cref="SecretNames.VehicleAccountPassword"/> fills
/// <c>Vehicle:Website:Password</c> and <c>Vehicle:DataAct:Password</c> alike. The MyŠkoda feed takes
/// no password at all — it takes a key, pasted on <c>/car</c> and kept under its own name.</para>
///
/// <para><b>Where it sits in the order.</b> Beside the overrides file: after the environment, so that a
/// password typed on <c>/car</c> wins over a stale <c>.env</c>, and before the command line. Anything
/// else and saving a password on an installation whose <c>.env</c> already carries one would silently
/// do nothing — which is every installation that has ever had this feed working.</para>
/// </summary>
public static class VehicleAccountSecret
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

    /// <summary>
    /// Adds the stored password to <paramref name="configuration"/> under both account keys, when there
    /// is one. Adds nothing at all when there is not, so a key set in <c>.env</c> is left exactly as it
    /// was rather than overwritten with an empty string.
    /// </summary>
    public static void AddVehicleAccountPassword(this IConfigurationManager configuration, string contentRoot)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var password = Open(configuration, contentRoot).Read(SecretNames.VehicleAccountPassword);

        if (string.IsNullOrWhiteSpace(password))
        {
            return;
        }

        var source = new MemoryConfigurationSource
        {
            InitialData =
            [
                new(EvSettingKeys.WebsitePassword, password),
                new(EvSettingKeys.DataActPassword, password),
            ],
        };

        var commandLine = configuration.Sources
            .Select((candidate, index) => (candidate, index))
            .LastOrDefault(entry => entry.candidate is CommandLineConfigurationSource);

        if (commandLine.candidate is null)
        {
            configuration.Sources.Add(source);
        }
        else
        {
            configuration.Sources.Insert(commandLine.index, source);
        }
    }
}
