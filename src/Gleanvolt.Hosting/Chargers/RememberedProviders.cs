using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Providers.Contracts.Charger;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Chargers;

/// <summary>
/// The provider a kind remembers (#440), kept in the overrides file so it survives a restart: for the charger,
/// <c>ExternalCharger:PluginId</c>. Read from the configuration once at the start, so it can also be set in the
/// environment (<c>ExternalCharger__PluginId</c>) to pin a plugin; Forget then lasts until the next start.
///
/// <para>Held in memory as well: a host without an overrides file, or a file that cannot be written, still
/// remembers until it stops, and the write's failure is the caller's to log.</para>
/// </summary>
public sealed class RememberedProviders : IRememberedProviders
{
    /// <summary>Where the charger's plugin id is kept.</summary>
    public const string ChargerKey = ExternalChargerOptions.SectionName + ":PluginId";

    private readonly Lock _gate = new();
    private readonly Dictionary<string, string> _ids = new(StringComparer.OrdinalIgnoreCase);
    private readonly PvSystemOverridesProvider? _overrides;

    public RememberedProviders(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        _overrides = PvSystemOverrides.Find(configuration);

        if (configuration[ChargerKey]?.Trim() is { Length: > 0 } id)
        {
            _ids[ChargerContract.Kind] = id;
        }
    }

    /// <summary>The configuration key <paramref name="kind"/>'s provider is kept under.</summary>
    public static string KeyFor(string kind) =>
        string.Equals(kind, ChargerContract.Kind, StringComparison.OrdinalIgnoreCase)
            ? ChargerKey
            : $"ExternalProviders:{kind}:RememberedId";

    public string? Find(string kind)
    {
        lock (_gate)
        {
            return _ids.TryGetValue(kind, out var id) ? id : null;
        }
    }

    public void Remember(string kind, string providerId)
    {
        lock (_gate)
        {
            _ids[kind] = providerId;
        }

        Save(kind, providerId);
    }

    public void Forget(string kind)
    {
        lock (_gate)
        {
            _ids.Remove(kind);
        }

        Save(kind, null);
    }

    private void Save(string kind, string? providerId)
    {
        if (_overrides is null)
        {
            return;
        }

        using (PvSystemOverrides.Lock(_overrides.Path))
        {
            var stored = PvSystemOverrides.Read(_overrides.Path);

            if (providerId is null)
            {
                stored.Remove(KeyFor(kind));
            }
            else
            {
                stored[KeyFor(kind)] = providerId;
            }

            PvSystemOverrides.Write(_overrides.Path, stored);
        }
    }
}
