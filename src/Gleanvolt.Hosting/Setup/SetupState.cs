using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Tariff;
using Gleanvolt.Infrastructure.Solcast;
using Gleanvolt.Web.Auth;

namespace Gleanvolt.Hosting.Setup;

/// <summary>
/// This run's setup mode, and the setup as saved since (#301). See <see cref="ISetupState"/>.
///
/// <para><see cref="Current"/> re-reads what the web UI writes -- the overrides file, the password in
/// force, the Solcast account, the tariff -- because none of that reaches <see cref="IConfiguration"/>
/// until a restart. Everything else comes from configuration as it is now.</para>
/// </summary>
public sealed class SetupState : ISetupState
{
    private readonly IConfiguration _configuration;
    private readonly SetupHost _host;
    private readonly IServiceProvider _services;

    public SetupState(IConfiguration configuration, SetupStatus atStartup, SetupHost host, IServiceProvider services)
    {
        _configuration = configuration;
        _host = host;
        _services = services;
        AtStartup = atStartup;
    }

    public bool SetupMode => !AtStartup.Usable;

    public SetupStatus AtStartup { get; }

    public SetupStatus Current()
    {
        var fresh = Fresh(_configuration);

        // The password as set in this process, which /setup changes without a restart. Added on top
        // rather than trusted from the file alone, so a password set by hand in the environment counts.
        var password = _services.GetService<WebPasswordState>();
        if (password is { IsSet: true })
        {
            fresh = new ConfigurationBuilder()
                .AddConfiguration(fresh)
                .AddInMemoryCollection([new(WebPasswordStore.Key, password.Hash)])
                .Build();
        }

        var fromConfiguration = OptionalSettings.FromConfiguration(fresh);
        var solcast = _services.GetService<SolcastAccountStore>()?.Current is not null || fromConfiguration.Solcast;
        var tariff = _services.GetService<TariffSettingsFile>()?.Read().Provider is { } provider
            && provider != TariffProviderIds.None;

        return SetupStatusEvaluator.Evaluate(fresh, _host, fromConfiguration with { Solcast = solcast, Tariff = tariff });
    }

    /// <summary>
    /// <paramref name="configuration"/> with the overrides file read as it is on disk now. The file's
    /// provider is replaced rather than added to: a key reverted from the file must disappear, not linger
    /// from the copy loaded at startup.
    /// </summary>
    public static IConfiguration Fresh(IConfiguration configuration)
    {
        if (configuration is not IConfigurationRoot root
            || PvSystemOverrides.Find(configuration) is not { } overrides)
        {
            return configuration;
        }

        var providers = root.Providers
            .Select(provider => ReferenceEquals(provider, overrides)
                ? new PvSystemOverridesProvider(overrides.Path)
                : provider)
            .ToList();

        // The other providers are already loaded and are not reloaded: the environment and
        // appsettings.json are what they were at startup, and only the file changes underneath. The
        // new file provider is the one ConfigurationRoot loads.
        return new ConfigurationRoot(providers.Select(provider =>
            provider is PvSystemOverridesProvider ? provider : new LoadedProvider(provider)).ToList());
    }

    // ConfigurationRoot loads every provider it is handed. These already are, and reloading them would
    // re-read appsettings.json and the environment for a page render; this hands their data over as is.
    private sealed class LoadedProvider(IConfigurationProvider inner) : IConfigurationProvider
    {
        public IEnumerable<string> GetChildKeys(IEnumerable<string> earlierKeys, string? parentPath) =>
            inner.GetChildKeys(earlierKeys, parentPath);

        public Microsoft.Extensions.Primitives.IChangeToken GetReloadToken() => inner.GetReloadToken();

        public void Load()
        {
        }

        public void Set(string key, string? value) => inner.Set(key, value);

        public bool TryGet(string key, out string? value) => inner.TryGet(key, out value);
    }
}
