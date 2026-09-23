using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Vehicles.VwGroup;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// The web UI's edits to the car (issue #214): <see cref="PvSystemEditor"/>'s arrangement, applied to
/// the other side of the cable. The same overrides file, the same precedence, the same provenance —
/// and one thing that file must never hold, the manufacturer account's password, which goes to
/// <see cref="ISecretStore"/> instead. See <see cref="IEvEditor"/> for the contract.
///
/// <para><b>Why a second editor rather than more keys on the first.</b> Each refuses a key that is not
/// on its own allowlist, and the two lists are what say where the boundary of each page is. One
/// editor with both lists would make <c>/pv-system</c> able to write a VW password by construction,
/// and the fact that it cannot is worth keeping in the type system rather than in the markup.</para>
///
/// <para><b>The manufacturer choice writes keys; it is not read back as one.</b> A save enables one
/// feed's section and disables the others' in the same write, because a half-applied switch — two live
/// feeds — is exactly the state the composition root throws on. Nothing reads
/// <c>Ev:Vehicles:0:Make</c>, here or anywhere.</para>
/// </summary>
public sealed class EvEditor : IEvEditor
{
    private readonly IConfigurationRoot? _configuration;
    private readonly PvSystemOverridesProvider? _overrides;
    private readonly EvRunningState _running;
    private readonly ISecretStore _secrets;
    private readonly int _chargerMinAmps;
    private readonly int _chargerMaxAmps;
    private readonly ILogger<EvEditor> _logger;

    // Two browser tabs saving at once must not interleave a read-modify-write of the same file.
    private readonly Lock _gate = new();

    /// <param name="configuration">The process's configuration, whose providers are walked.</param>
    /// <param name="running">The car as this process started, taken before anything could change it.</param>
    /// <param name="secrets">Where a password typed on the page is kept — never the overrides file.</param>
    /// <param name="chargerMinAmps">The installation's floor, for the band check a save runs.</param>
    /// <param name="chargerMaxAmps">The installation's ceiling, for the same check.</param>
    /// <param name="logger">Where saves are recorded — by key, never by value.</param>
    public EvEditor(
        IConfiguration configuration,
        EvRunningState running,
        ISecretStore secrets,
        int chargerMinAmps,
        int chargerMaxAmps,
        ILogger<EvEditor> logger)
    {
        _configuration = configuration as IConfigurationRoot;
        _overrides = PvSystemOverrides.Find(configuration);
        _running = running;
        _secrets = secrets;
        _chargerMinAmps = chargerMinAmps;
        _chargerMaxAmps = chargerMaxAmps;
        _logger = logger;
    }

    /// <summary>
    /// The car as <paramref name="configuration"/> and <paramref name="secrets"/> have it now, for the
    /// <c>running</c> argument. Taken at startup, so that "running" means what this process started
    /// with rather than whatever the file says later.
    /// </summary>
    public static EvRunningState Snapshot(IConfiguration configuration, ISecretStore secrets)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(secrets);

        var values = EvSettingKeys.All
            .Where(key => !EvSettingKeys.IsSecret(key))
            .ToDictionary(key => key, key => configuration[key], StringComparer.OrdinalIgnoreCase);

        // Whether a password was available, never which one. A bool is all any caller of this ever
        // needs, and a string here would be a copy of the secret living for the life of the process.
        var held = !string.IsNullOrWhiteSpace(secrets.Read(SecretNames.VehicleAccountPassword));

        var secretsSet = EvSettingKeys.Secrets.ToDictionary(
            key => key,
            key => held || !string.IsNullOrWhiteSpace(configuration[key]),
            StringComparer.OrdinalIgnoreCase);

        return new EvRunningState(
            values,
            secretsSet,
            VehicleFeeds.Selected(key => Flag(configuration[key])));
    }

    public EvSettings Read()
    {
        if (_configuration is null || _overrides is null)
        {
            return Unavailable(
                "This host does not read a web UI overrides file, so an edit here would never be applied.");
        }

        lock (_gate)
        {
            Dictionary<string, string> stored;

            try
            {
                stored = PvSystemOverrides.Read(_overrides.Path);
            }
            catch (InvalidOperationException ex)
            {
                return Describe(new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase), ex.Message);
            }

            return Describe(stored, null);
        }
    }

    public SettingsSaveResult Save(IReadOnlyDictionary<string, string> values) => Apply(feed: null, values);

    public SettingsSaveResult SaveFeed(VehicleFeed feed, IReadOnlyDictionary<string, string> values) =>
        Apply(feed, values);

    public SettingsSaveResult Revert(string key)
    {
        if (EvSettingKeys.Canonical(key) is not { } canonical)
        {
            return SettingsSaveResult.Refused([$"{key} cannot be edited from the web UI."]);
        }

        if (EvSettingKeys.IsSecret(canonical))
        {
            // Removing means removing: the value is gone from the store, and the key falls back to
            // whatever the environment says -- which the page's provenance line then reports, rather
            // than claiming a password has been cleared when .env still holds one.
            if (!_secrets.Delete(SecretNames.VehicleAccountPassword))
            {
                return SettingsSaveResult.Refused(
                    ["The saved password could not be removed. Check the data directory's permissions."]);
            }

            _logger.LogInformation("The vehicle account password was removed from the secret store by the web UI.");
            return SettingsSaveResult.Success;
        }

        return Change(stored => stored.Remove(canonical), password: null, "reverted");
    }

    // One save, whether or not it also switches feed: a switch and the new feed's fields have to be
    // one write, because a file holding two enabled feeds does not start.
    private SettingsSaveResult Apply(VehicleFeed? feed, IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);

        var refused = values.Keys.Where(key => !EvSettingKeys.IsEditable(key)).ToList();

        if (refused.Count > 0)
        {
            return SettingsSaveResult.Refused(
                [.. refused.Select(key => $"{key} cannot be edited from the web UI.")]);
        }

        // An empty secret means "unchanged", never "clear": a password box renders empty on every
        // load, so submitting the form without retyping must not wipe the account.
        var secrets = values
            .Where(entry => EvSettingKeys.IsSecret(entry.Key) && !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => entry.Value)
            .ToList();

        if (secrets.Count > 1 && secrets.Distinct(StringComparer.Ordinal).Count() > 1)
        {
            // One account, one stored password (SecretNames.VehicleAccountPassword): volkswagen.de and
            // the Data Act portal are entered with the same VW ID. Two different ones in one save is a
            // form bug rather than something to resolve by picking one.
            return SettingsSaveResult.Refused(
                ["Two different passwords were submitted at once; only one vehicle account is kept."]);
        }

        return Change(
            stored =>
            {
                if (feed is { } chosen)
                {
                    foreach (var candidate in VehicleFeeds.All)
                    {
                        if (VehicleFeeds.EnabledKey(candidate) is { } key)
                        {
                            SetFlag(stored, key, candidate == chosen);
                        }
                    }
                }

                foreach (var (key, raw) in values)
                {
                    var canonical = EvSettingKeys.Canonical(key)!;

                    if (EvSettingKeys.IsSecret(canonical))
                    {
                        continue;
                    }

                    var value = raw.Trim();

                    // The same as what the key would be anyway is not an edit: storing it would pin a
                    // value the environment could otherwise still change, and the file is meant to list
                    // only real differences.
                    if (string.Equals(value, Underlying(canonical) ?? string.Empty, StringComparison.Ordinal))
                    {
                        stored.Remove(canonical);
                    }
                    else
                    {
                        stored[canonical] = value;
                    }
                }
            },
            secrets.Count > 0 ? secrets[0] : null,
            "saved");
    }

    // One read-modify-write of the file: apply the change, check the result would start, write it --
    // and only then the secret, so that a refused save leaves nothing behind anywhere.
    private SettingsSaveResult Change(Action<Dictionary<string, string>> change, string? password, string verb)
    {
        if (_configuration is null || _overrides is null)
        {
            return SettingsSaveResult.Refused(
                ["This host does not read a web UI overrides file, so an edit here would never be applied."]);
        }

        lock (_gate)
        {
            Dictionary<string, string> stored;

            try
            {
                stored = PvSystemOverrides.Read(_overrides.Path);
            }
            catch (InvalidOperationException ex)
            {
                return SettingsSaveResult.Refused([ex.Message]);
            }

            var before = new Dictionary<string, string>(stored, StringComparer.OrdinalIgnoreCase);
            change(stored);

            var unchanged = stored.Count == before.Count
                && stored.All(entry => before.TryGetValue(entry.Key, out var old) && old == entry.Value);

            if (unchanged && password is null)
            {
                return SettingsSaveResult.Success;
            }

            var problems = Check(stored, password);

            if (problems.Count > 0)
            {
                return SettingsSaveResult.Refused(problems);
            }

            if (!unchanged)
            {
                try
                {
                    PvSystemOverrides.Write(_overrides.Path, stored);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    return SettingsSaveResult.Refused([$"Could not write {_overrides.Path}: {ex.Message}"]);
                }
            }

            if (password is not null && !_secrets.Write(SecretNames.VehicleAccountPassword, password))
            {
                return SettingsSaveResult.Refused(
                    [$"The password could not be stored ({_secrets.Describe()}). Nothing else was changed."]);
            }

            _logger.LogInformation(
                "Vehicle configuration {Verb} from the web UI in {Path}; it takes effect on the next start. "
                + "Keys now overridden: {Keys}.{Password}",
                verb,
                _overrides.Path,
                stored.Count == 0 ? "none" : string.Join(", ", stored.Keys),
                password is null ? string.Empty : " The account password was stored separately.");

            return SettingsSaveResult.Success;
        }
    }

    // Everything a start would refuse, run against the configuration the next start would read. The
    // Ev section goes through the startup resolver itself; the feed rules are stated here because the
    // composition root states them as a throw rather than as a validator.
    private List<string> Check(IReadOnlyDictionary<string, string> stored, string? password)
    {
        var problems = new List<string>();
        var candidate = Candidate(stored);

        try
        {
            EvResolver.Resolve(candidate, _chargerMinAmps, _chargerMaxAmps);
        }
        catch (InvalidOperationException ex)
        {
            // The resolver lists its problems one per line under a heading; the binder, for a value
            // that is not a number at all, says one thing.
            var lines = ex.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var listed = lines.Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..]).ToList();
            problems.AddRange(listed.Count > 0 ? listed : [ex.Message]);
        }

        var enabled = VehicleFeeds.All
            .Where(feed => VehicleFeeds.EnabledKey(feed) is { } key && Flag(Next(key, stored)?.Value))
            .ToList();

        // The startup exception, refused at the form instead: two live feeds read two different cars
        // and an installation has one.
        if (enabled.Contains(VehicleFeed.Volkswagen) && enabled.Contains(VehicleFeed.Skoda))
        {
            problems.Add(
                "Vehicle:Website and Vehicle:Skoda would both be enabled, and they read two different "
                + "cars (volkswagen.de and the MyŠkoda API). An installation has one car.");
        }

        foreach (var feed in enabled)
        {
            foreach (var key in VehicleFeeds.Required(feed))
            {
                var present = EvSettingKeys.IsSecret(key)
                    ? password is not null || HasSecret(key, stored)
                    : !string.IsNullOrWhiteSpace(Next(key, stored)?.Value);

                if (!present)
                {
                    problems.Add($"{key} is needed by the feed that is switched on, and is empty.");
                }
            }
        }

        if (enabled.Contains(VehicleFeed.DataAct))
        {
            var brand = Next(EvSettingKeys.DataActBrand, stored)?.Value;
            var clientId = Next(EvSettingKeys.DataActClientId, stored)?.Value;

            if (string.IsNullOrWhiteSpace(clientId) && !VwGroupBrands.IsKnown(brand))
            {
                problems.Add(
                    $"Vehicle:DataAct:Brand ('{brand}') is not one of {VwGroupBrands.Known}. Pick one, or "
                    + "state Vehicle:DataAct:ClientId outright if yours is missing.");
            }
        }

        return problems;
    }

    // The configuration the next start would read, as far as the two sections this page owns: this
    // process's own providers, with the overrides file read from `stored` rather than from the copy
    // loaded at startup.
    private IConfiguration Candidate(IReadOnlyDictionary<string, string> stored)
    {
        var keys = _configuration!.GetSection(EvOptions.SectionName).AsEnumerable()
            .Concat(_configuration.GetSection("Vehicle").AsEnumerable())
            .Where(entry => entry.Value is not null)
            .Select(entry => entry.Key)
            .Concat(stored.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            // A password is not put into this: the checks that need one ask whether there IS one, and
            // a copy of a secret in a throwaway configuration is a copy nobody asked for.
            if (EvSettingKeys.IsSecret(key))
            {
                continue;
            }

            if (Next(key, stored) is { } found)
            {
                values[key] = found.Value;
            }
        }

        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private EvSettings Describe(IReadOnlyDictionary<string, string> stored, string? unavailable)
    {
        var settings = EvSettingKeys.All
            .Where(key => !EvSettingKeys.IsSecret(key))
            .Select(key =>
            {
                var next = Next(key, stored);
                return new ConfiguredSetting(
                    key,
                    next?.Value,
                    _running.Values.GetValueOrDefault(key),
                    next?.Source ?? SettingSource.Default,
                    next?.Detail ?? string.Empty,
                    Underlying(key));
            })
            .ToList();

        var held = !string.IsNullOrWhiteSpace(_secrets.Read(SecretNames.VehicleAccountPassword));

        var secrets = EvSettingKeys.Secrets
            .Select(key =>
            {
                var fromConfiguration = Next(key, stored);
                var set = held || !string.IsNullOrWhiteSpace(fromConfiguration?.Value);

                return new EvSecret(
                    key,
                    set,
                    _running.Secrets.GetValueOrDefault(key),
                    held ? SettingSource.SecretStore : fromConfiguration?.Source ?? SettingSource.Default,
                    held ? _secrets.Describe() : fromConfiguration?.Detail ?? string.Empty);
            })
            .ToList();

        return new EvSettings(
            _overrides?.Path ?? string.Empty,
            settings,
            secrets,
            VehicleFeeds.Selected(key => Flag(Next(key, stored)?.Value)),
            _running.Feed,
            VwGroupBrands.Catalog,
            _secrets.Describe(),
            _chargerMinAmps,
            _chargerMaxAmps,
            unavailable);
    }

    private EvSettings Unavailable(string reason) => new(
        _overrides?.Path ?? string.Empty,
        [
            .. EvSettingKeys.All.Where(key => !EvSettingKeys.IsSecret(key)).Select(key => new ConfiguredSetting(
                key,
                _running.Values.GetValueOrDefault(key),
                _running.Values.GetValueOrDefault(key),
                SettingSource.Default,
                string.Empty,
                _running.Values.GetValueOrDefault(key))),
        ],
        [
            .. EvSettingKeys.Secrets.Select(key => new EvSecret(
                key,
                _running.Secrets.GetValueOrDefault(key),
                _running.Secrets.GetValueOrDefault(key),
                SettingSource.Default,
                string.Empty)),
        ],
        _running.Feed,
        _running.Feed,
        VwGroupBrands.Catalog,
        _secrets.Describe(),
        _chargerMinAmps,
        _chargerMaxAmps,
        reason);

    // Whether a password would be available to the next start for `key` -- from the store, or from the
    // environment beside it. Never what it is.
    private bool HasSecret(string key, IReadOnlyDictionary<string, string> stored) =>
        !string.IsNullOrWhiteSpace(_secrets.Read(SecretNames.VehicleAccountPassword))
        || !string.IsNullOrWhiteSpace(Next(key, stored)?.Value);

    // A feed's Enabled key, written only when it would actually change what the next start reads: with
    // nothing underneath it, "false" is noise in a file that is meant to list exceptions.
    private void SetFlag(Dictionary<string, string> stored, string key, bool value)
    {
        if (Flag(Underlying(key)) == value)
        {
            stored.Remove(key);
        }
        else
        {
            stored[key] = value ? "true" : "false";
        }
    }

    private static bool Flag(string? value) => bool.TryParse(value, out var parsed) && parsed;

    // What the key would be with no overrides file at all.
    private string? Underlying(string key) =>
        Next(key, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase))?.Value;

    // The value the next start reads for `key`, and which provider supplies it: the last one that has
    // it wins, as in ConfigurationRoot itself.
    private (string? Value, SettingSource Source, string Detail)? Next(string key, IReadOnlyDictionary<string, string> stored)
    {
        foreach (var provider in _configuration!.Providers.Reverse())
        {
            if (provider is PvSystemOverridesProvider)
            {
                if (stored.TryGetValue(key, out var saved))
                {
                    return (saved, SettingSource.WebUi, _overrides!.Path);
                }

                continue;
            }

            if (provider.TryGet(key, out var value))
            {
                return provider switch
                {
                    EnvironmentVariablesConfigurationProvider =>
                        (value, SettingSource.Environment, EvSettingKeys.EnvironmentVariable(key)),
                    CommandLineConfigurationProvider => (value, SettingSource.CommandLine, "command line"),
                    FileConfigurationProvider file =>
                        (value, SettingSource.AppSettings, Path.GetFileName(file.Source.Path) ?? "a settings file"),
                    _ => (value, SettingSource.AppSettings, provider.ToString() ?? string.Empty),
                };
            }
        }

        return null;
    }
}

/// <summary>
/// The car as this process started: what every editable key was, whether a password was available, and
/// which feed was selected. Taken once at startup, because "running" has to mean what the process is
/// actually using and not what the file says the next start will.
/// </summary>
/// <param name="Values">Every non-secret editable key's value, or null where it was set nowhere.</param>
/// <param name="Secrets">Whether each secret key had a value. Never the value itself.</param>
/// <param name="Feed">The feed this process started.</param>
public sealed record EvRunningState(
    IReadOnlyDictionary<string, string?> Values,
    IReadOnlyDictionary<string, bool> Secrets,
    VehicleFeed Feed);
