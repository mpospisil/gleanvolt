using Gleanvolt.Core.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// The web UI's edits to the car (issue #214): <see cref="PvSystemEditor"/>'s arrangement, applied to
/// the other side of the cable. The same overrides file, the same precedence, the same provenance. No
/// secret: since #477 a car plugin keeps the manufacturer account on its own page. See <see cref="IEvEditor"/>
/// for the contract.
///
/// <para><b>Why a second editor rather than more keys on the first.</b> Each refuses a key that is not
/// on its own allowlist, and the two lists are what say where the boundary of each page is. One
/// editor with both lists would make <c>/pv-system</c> able to write the car's keys by construction.</para>
///
/// <para><b>The manufacturer choice writes keys; it is not read back as one.</b> A save enables one
/// feed's section and disables the others' in the same write. Nothing reads <c>Ev:Vehicles:0:Make</c>, here or
/// anywhere.</para>
/// </summary>
public sealed class EvEditor : IEvEditor
{
    private readonly IConfigurationRoot? _configuration;
    private readonly PvSystemOverridesProvider? _overrides;
    private readonly EvRunningState _running;
    private readonly int _chargerMinAmps;
    private readonly int _chargerMaxAmps;
    private readonly ILogger<EvEditor> _logger;

    // Two browser tabs saving at once must not interleave a read-modify-write of the same file.
    // The file's own lock, shared with every other writer of it (#301).
    private static Lock _gate => PvSystemOverrides.Gate;

    /// <param name="configuration">The process's configuration, whose providers are walked.</param>
    /// <param name="running">The car as this process started, taken before anything could change it.</param>
    /// <param name="chargerMinAmps">The installation's floor, for the band check a save runs.</param>
    /// <param name="chargerMaxAmps">The installation's ceiling, for the same check.</param>
    /// <param name="logger">Where saves are recorded — by key, never by value.</param>
    public EvEditor(
        IConfiguration configuration,
        EvRunningState running,
        int chargerMinAmps,
        int chargerMaxAmps,
        ILogger<EvEditor> logger)
    {
        _configuration = configuration as IConfigurationRoot;
        _overrides = PvSystemOverrides.Find(configuration);
        _running = running;
        _chargerMinAmps = chargerMinAmps;
        _chargerMaxAmps = chargerMaxAmps;
        _logger = logger;
    }

    /// <summary>
    /// The car as <paramref name="configuration"/> has it now, for the <c>running</c> argument. Taken at startup, so
    /// that "running" means what this process started with rather than whatever the file says later.
    /// </summary>
    public static EvRunningState Snapshot(IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(configuration);

        var values = EvSettingKeys.All.ToDictionary(key => key, key => configuration[key], StringComparer.OrdinalIgnoreCase);

        // A built-in moved to a plugin at this start (#477) runs the plugin's feed, whether or not the overrides file
        // has been read back since it was written.
        var moved = Vehicles.RetiredCarFeed.Moving(configuration) is not null;

        return new EvRunningState(
            values,
            VehicleFeeds.Selected(key => Flag(configuration[key]) || (moved && key == EvSettingKeys.ExternalEnabled)));
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
            return SettingsSaveResult.Refused(ConfigurationProblems.NotEditable([key]));
        }

        return Change(stored => stored.Remove(canonical), "reverted");
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
                ConfigurationProblems.NotEditable(refused));
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
            "saved");
    }

    // One read-modify-write of the file: apply the change, check the result would start, write it.
    private SettingsSaveResult Change(Action<Dictionary<string, string>> change, string verb)
    {
        if (_configuration is null || _overrides is null)
        {
            return SettingsSaveResult.Refused(
                ConfigurationProblems.NoOverridesFile());
        }

        using (PvSystemOverrides.Lock(_overrides.Path))
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

            if (unchanged)
            {
                return SettingsSaveResult.Success;
            }

            var problems = Check(stored);

            if (problems.Count > 0)
            {
                return SettingsSaveResult.Refused(problems);
            }

            try
            {
                PvSystemOverrides.Write(_overrides.Path, stored);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return SettingsSaveResult.Refused(ConfigurationProblems.NotWritten(_overrides.Path, ex.Message));
            }

            _logger.LogInformation(
                "Vehicle configuration {Verb} from the web UI in {Path}; it takes effect on the next start. "
                + "Keys now overridden: {Keys}.",
                verb,
                _overrides.Path,
                stored.Count == 0 ? "none" : string.Join(", ", stored.Keys));

            return SettingsSaveResult.Success;
        }
    }

    // Everything a start would refuse, run against the configuration the next start would read. The
    // Ev section goes through the startup resolver itself.
    private ConfigurationProblems Check(IReadOnlyDictionary<string, string> stored)
    {
        var problems = new ConfigurationProblems();
        var candidate = Candidate(stored);

        try
        {
            EvResolver.Resolve(candidate, _chargerMinAmps, _chargerMaxAmps);
        }
        catch (ConfigurationRefusedException refused)
        {
            problems = refused.Problems;
        }
        catch (InvalidOperationException ex)
        {
            // The binder, for a value that is not a number at all, says one thing; the resolver's own
            // refusal, a list under a heading, is the case above.
            var lines = ex.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var listed = lines.Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..]).ToList();
            problems.AddRange(listed.Count > 0 ? listed : [ex.Message]);
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

        return new EvSettings(
            _overrides?.Path ?? string.Empty,
            settings,
            VehicleFeeds.Selected(key => Flag(Next(key, stored)?.Value)),
            _running.Feed,
            _chargerMinAmps,
            _chargerMaxAmps,
            unavailable);
    }

    private EvSettings Unavailable(string reason) => new(
        _overrides?.Path ?? string.Empty,
        [
            .. EvSettingKeys.All.Select(key => new ConfiguredSetting(
                key,
                _running.Values.GetValueOrDefault(key),
                _running.Values.GetValueOrDefault(key),
                SettingSource.Default,
                string.Empty,
                _running.Values.GetValueOrDefault(key))),
        ],
        _running.Feed,
        _running.Feed,
        _chargerMinAmps,
        _chargerMaxAmps,
        reason);

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
/// The car as this process started: what every editable key was, and which feed was selected. Taken once at startup, because "running" has to mean what the process is
/// actually using and not what the file says the next start will.
/// </summary>
/// <param name="Values">Every editable key's value, or null where it was set nowhere.</param>
/// <param name="Feed">The feed this process started.</param>
public sealed record EvRunningState(
    IReadOnlyDictionary<string, string?> Values,
    VehicleFeed Feed);
