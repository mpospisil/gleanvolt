using Gleanvolt.Core.Configuration;
using Microsoft.Extensions.Configuration.CommandLine;
using Microsoft.Extensions.Configuration.EnvironmentVariables;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Devices;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Http;
using Gleanvolt.Infrastructure.Modbus;
using Gleanvolt.Infrastructure.RegisterMaps;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// The web UI's edits to the installation (issue #204), kept in <see cref="PvSystemOverrides"/>'s file
/// and checked by <see cref="PvSystemResolver"/> before anything is written. See
/// <see cref="IPvSystemEditor"/> for the contract.
///
/// <para><b>How the next start is predicted.</b> By walking this process's own configuration
/// providers in reverse order, as the configuration system does, with one change: where the
/// overrides file sits, the file is read afresh from disk instead of taken from the copy loaded at
/// startup. Everything else — <c>appsettings.json</c>, the environment, the command line — is what the
/// next start will read too, so the answer is exact rather than a model of the precedence rules.</para>
/// </summary>
public sealed class PvSystemEditor : IPvSystemEditor
{
    // One read is all a probe does, so a device that does not answer should not hold the Save button
    // for longer than the Modbus client's own connect timeout plus one request.
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);

    private readonly IConfigurationRoot? _configuration;
    private readonly PvSystemOverridesProvider? _overrides;
    private readonly IReadOnlyDictionary<string, string?> _running;
    private readonly Func<DeviceConfig, IModbusClient> _clientFactory;
    private readonly Func<DeviceConfig, HttpClient> _httpClientFactory;
    private readonly IReadOnlyList<IInverterDriverFactory> _inverterDrivers;
    private readonly IReadOnlyList<IEvChargerDriverFactory> _chargerDrivers;
    private readonly ILogger<PvSystemEditor> _logger;

    // Two browser tabs saving at once must not interleave a read-modify-write of the same file.
    // The file's own lock, shared with every other writer of it (#301).
    private static Lock _gate => PvSystemOverrides.Gate;

    /// <param name="configuration">The process's configuration, whose providers are walked.</param>
    /// <param name="running">Each editable key's value as this process started, taken before anything could change.</param>
    /// <param name="logger">Where saves are recorded.</param>
    /// <param name="clientFactory">Builds the Modbus client a probe reads through; a real TCP client by default.</param>
    public PvSystemEditor(
        IConfiguration configuration,
        IReadOnlyDictionary<string, string?> running,
        ILogger<PvSystemEditor> logger,
        Func<DeviceConfig, IModbusClient>? clientFactory = null,
        IReadOnlyList<IInverterDriverFactory>? inverterDrivers = null,
        IReadOnlyList<IEvChargerDriverFactory>? chargerDrivers = null,
        Func<DeviceConfig, HttpClient>? httpClientFactory = null)
    {
        _configuration = configuration as IConfigurationRoot;
        _overrides = PvSystemOverrides.Find(configuration);
        _running = running;
        _logger = logger;
        _clientFactory = clientFactory ?? (device => new ModbusTcpClient(device));

        // Read-only, as the Modbus probe's client is: an address nobody has confirmed is never written to.
        _httpClientFactory = httpClientFactory
            ?? (device => ChargerHttpClients.Create(device, writable: false, NullLogger<ReadOnlyHttpHandler>.Instance));
        _inverterDrivers = inverterDrivers ?? [.. Gleanvolt.Hosting.InverterDrivers.Factories.Select(make => make(null))];
        InverterDrivers = [.. _inverterDrivers.Select(InverterDriverInfo.Of)];
        _chargerDrivers = chargerDrivers ?? [.. Gleanvolt.Hosting.ChargerDrivers.Factories.Select(make => make(null))];
        ChargerDrivers = [.. _chargerDrivers.Select(EvChargerDriverInfo.Of)];
    }

    /// <summary>Each editable key's value in <paramref name="configuration"/>, for the <c>running</c> argument.</summary>
    public static IReadOnlyDictionary<string, string?> Snapshot(IConfiguration configuration) =>
        PvSystemSettingKeys.All.ToDictionary(key => key, key => configuration[key], StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<InverterDriverInfo> InverterDrivers { get; }

    public IReadOnlyList<EvChargerDriverInfo> ChargerDrivers { get; }

    public PvSystemSettings Read()
    {
        if (_configuration is null || _overrides is null)
        {
            return new PvSystemSettings(
                _overrides?.Path ?? string.Empty,
                [.. PvSystemSettingKeys.All.Select(key => new ConfiguredSetting(
                    key, _running[key], _running[key], SettingSource.Default, string.Empty, _running[key]))],
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
                return new PvSystemSettings(_overrides.Path, Describe(new Dictionary<string, string>()), ex.Message);
            }

            return new PvSystemSettings(_overrides.Path, Describe(stored));
        }
    }

    public SettingsSaveResult Save(IReadOnlyDictionary<string, string> values)
    {
        var refused = values.Keys.Where(key => !PvSystemSettingKeys.IsEditable(key)).ToList();

        if (refused.Count > 0)
        {
            return SettingsSaveResult.Refused(
                ConfigurationProblems.NotEditable(refused));
        }

        return Change(stored =>
        {
            foreach (var (key, raw) in values)
            {
                var value = raw.Trim();
                var canonical = PvSystemSettingKeys.All.First(
                    candidate => string.Equals(candidate, key, StringComparison.OrdinalIgnoreCase));

                // The same as what the key would be anyway is not an edit: storing it would pin a value
                // the environment could otherwise still change, and the file is meant to list only
                // real differences.
                if (string.Equals(value, Underlying(canonical) ?? string.Empty, StringComparison.Ordinal))
                {
                    stored.Remove(canonical);
                }
                else
                {
                    stored[canonical] = value;
                }
            }
        }, "saved");
    }

    public SettingsSaveResult Revert(string key)
    {
        if (!PvSystemSettingKeys.IsEditable(key))
        {
            return SettingsSaveResult.Refused(ConfigurationProblems.NotEditable([key]));
        }

        return Change(stored => stored.Remove(key), "reverted");
    }

    public async Task<DeviceProbeResult> ProbeAsync(
        PvDeviceRole role,
        string host,
        int port,
        byte unitId,
        string? driver = null,
        string? inverterTopology = null,
        CancellationToken cancellationToken = default)
    {
        var address = $"{host}:{port}";

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        try
        {
            // A charger reached over HTTP (go-e, #352) is probed over HTTP, on a client that passes only reads.
            if (role == PvDeviceRole.Charger && ChargerDriverFactory(driver) is { Transport: EvChargerTransport.Http } httpFamily)
            {
                using var http = _httpClientFactory(new DeviceConfig { Host = host, Port = port, UnitId = unitId });
                var apiProbe = await httpFamily.Create(EvChargerConnection.Over(http), new EvChargerDriverOptions { DryRun = true })
                    .ProbeAsync(timeout.Token).ConfigureAwait(false);
                var answer = (long)Math.Round(apiProbe.Reading);

                return apiProbe.Recognised
                    ? new DeviceProbeResult(true, true, $"{address} answered: the charger's API reports car state {answer}.",
                        Phrase.Of("Probe.ChargerApiOk", new PhraseArg.Text(address), new PhraseArg.Number(answer)))
                    : new DeviceProbeResult(true, false,
                        $"{address} answered (HTTP {answer}), but not with the selected charger's API. "
                        + "Is its local HTTP API switched on?",
                        Phrase.Of("Probe.ChargerApiImplausible", new PhraseArg.Text(address), new PhraseArg.Number(answer)));
            }

            await using var client = _clientFactory(new DeviceConfig { Host = host, Port = port, UnitId = unitId });
            await client.ConnectAsync(timeout.Token).ConfigureAwait(false);

            if (role == PvDeviceRole.Inverter)
            {
                // Through the driver, so "Test connection" asks whatever the family's own probe asks. The
                // client refuses writes, and the probe makes none: an address nobody has confirmed yet is
                // never written to.
                var inverter = InverterDriverFactory(driver).Create(
                    new ReadOnlyModbusClient(client, NullLogger<ReadOnlyModbusClient>.Instance),
                    new InverterDriverOptions { DryRun = true, Topology = KnownTopology(driver, inverterTopology) });
                var probe = await inverter.ProbeAsync(timeout.Token).ConfigureAwait(false);
                var reading = (long)Math.Round(probe.Reading);

                return probe.Recognised
                    ? new DeviceProbeResult(true, true, $"{address} answered: battery at {reading} %.",
                        Phrase.Of("Probe.InverterOk", new PhraseArg.Text(address), new PhraseArg.Number(reading)))
                    : new DeviceProbeResult(true, false,
                        $"{address} answered, but it does not look like an inverter of the selected brand "
                        + $"(battery register: {reading}). Is this the charger's address?",
                        Phrase.Of("Probe.InverterImplausible", new PhraseArg.Text(address), new PhraseArg.Number(reading)));
            }

            // The charger's probe is its driver's too (#349), on a client that cannot write.
            var charger = ChargerDriverFactory(driver).Create(
                EvChargerConnection.Over(new ReadOnlyModbusClient(client, NullLogger<ReadOnlyModbusClient>.Instance)),
                new EvChargerDriverOptions { DryRun = true });
            return ChargerProbeResult(address, await charger.ProbeAsync(timeout.Token).ConfigureAwait(false));
        }
        catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return new DeviceProbeResult(false, false, $"{address} did not answer ({ex.Message}).",
                Phrase.Of("Probe.NoAnswer", new PhraseArg.Text(address), new PhraseArg.Text(ex.Message)));
        }
    }

    private static DeviceProbeResult ChargerProbeResult(string address, EvChargerProbeResult chargerProbe)
    {
        var value = (long)Math.Round(chargerProbe.Reading);

        return chargerProbe.Recognised
            ? new DeviceProbeResult(true, true, $"{address} answered: charger run mode {value}.",
                Phrase.Of("Probe.ChargerOk", new PhraseArg.Text(address), new PhraseArg.Number(value)))
            : new DeviceProbeResult(true, false,
                $"{address} answered, but its run-mode register reads {value}, which no charger "
                + "reports. Is this the inverter's address?",
                Phrase.Of("Probe.ChargerImplausible", new PhraseArg.Text(address), new PhraseArg.Number(value)));
    }

    // The driver the form has selected. An id this host does not have probes with the default: the save
    // refuses that id with a message naming the known ones, which says more than a probe could.
    private IInverterDriverFactory InverterDriverFactory(string? id)
    {
        var wanted = string.IsNullOrWhiteSpace(id) ? PvSystemRules.DefaultInverterDriver : id.Trim().ToLowerInvariant();
        return _inverterDrivers.FirstOrDefault(candidate => candidate.Id == wanted)
            ?? _inverterDrivers.FirstOrDefault(candidate => candidate.Id == PvSystemRules.DefaultInverterDriver)
            ?? _inverterDrivers[0];
    }

    // As for the inverter: an id this host does not have probes with the default, and the save says why.
    private IEvChargerDriverFactory ChargerDriverFactory(string? id)
    {
        var wanted = string.IsNullOrWhiteSpace(id) ? PvSystemRules.DefaultChargerDriver : id.Trim().ToLowerInvariant();
        return _chargerDrivers.FirstOrDefault(candidate => candidate.Id == wanted)
            ?? _chargerDrivers.FirstOrDefault(candidate => candidate.Id == PvSystemRules.DefaultChargerDriver)
            ?? _chargerDrivers[0];
    }

    // The topology the form has selected, if the driver has it; anything else probes the default, and the
    // save refuses it with a message naming the ones there are.
    private string KnownTopology(string? driverId, string? topology)
    {
        var wanted = topology?.Trim().ToLowerInvariant() ?? string.Empty;
        return InverterDriverFactory(driverId).Topologies.Any(candidate => candidate.Id == wanted) ? wanted : string.Empty;
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

            if (stored.Count == before.Count
                && stored.All(entry => before.TryGetValue(entry.Key, out var old) && old == entry.Value))
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
                "PV system configuration {Verb} from the web UI in {Path}; it takes effect on the next start. "
                + "Keys now overridden: {Keys}.",
                verb,
                _overrides.Path,
                stored.Count == 0 ? "none" : string.Join(", ", stored.Keys));

            return SettingsSaveResult.Success;
        }
    }

    // The startup resolver, run on the configuration the next start would read with `stored` as the
    // file. Its messages are the ones a failed start would print, one per problem.
    private ConfigurationProblems Check(IReadOnlyDictionary<string, string> stored)
    {
        var keys = _configuration!.GetSection(PvSystemOptions.SectionName).AsEnumerable()
            .Where(entry => entry.Value is not null)
            .Select(entry => entry.Key)
            .Concat(stored.Keys)
            .Distinct(StringComparer.OrdinalIgnoreCase);

        var candidate = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        foreach (var key in keys)
        {
            if (Next(key, stored) is { } found)
            {
                candidate[key] = found.Value;
            }
        }

        var site = CheckSite(stored);

        try
        {
            PvSystemResolver.Resolve(new ConfigurationBuilder().AddInMemoryCollection(candidate).Build());
            return site;
        }
        catch (ConfigurationRefusedException refused)
        {
            var problems = refused.Problems;
            var phrases = site.Phrases;
            for (var index = 0; index < site.Count; index++)
            {
                problems.Add(site[index], phrases[index]!);
            }

            return problems;
        }
        catch (InvalidOperationException ex)
        {
            // The resolver lists its problems one per line under a heading; the binder, for a value that
            // is not a number at all, says one thing.
            var lines = ex.Message.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            var listed = lines.Where(line => line.StartsWith("- ", StringComparison.Ordinal)).Select(line => line[2..]).ToList();
            return [.. listed.Count > 0 ? listed : [ex.Message]];
        }
    }

    // The keys outside the Pv section (#301), judged as the next start would read them: the zone must be
    // one this host knows, the pack a positive size, the efficiency a fraction. Empty is fine for all
    // three -- unset is what setup mode and the forecast modes already know how to say.
    private ConfigurationProblems CheckSite(IReadOnlyDictionary<string, string> stored)
    {
        var problems = new ConfigurationProblems();

        if (Next(PvSystemSettingKeys.TimeZone, stored)?.Value?.Trim() is { Length: > 0 } zone)
        {
            try
            {
                ZonedTimeProvider.Resolve(zone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                problems.Add(
                    $"Controller:TimeZone ('{zone}') is not a time zone this host knows.",
                    Phrase.Of("Config.SetupBadTimeZone", new PhraseArg.Text(zone)));
            }
        }

        if (Next(PvSystemSettingKeys.HomeBatteryCapacityKWh, stored)?.Value?.Trim() is { Length: > 0 } capacity
            && !(Number(capacity) is { } kwh && kwh > 0))
        {
            problems.Add(
                $"{PvSystemSettingKeys.HomeBatteryCapacityKWh} ('{capacity}') must be a positive number of kWh.",
                Phrase.Of("Config.SetupBadBatteryCapacity", new PhraseArg.Text(capacity)));
        }

        if (Next(PvSystemSettingKeys.HomeBatteryChargeEfficiency, stored)?.Value?.Trim() is { Length: > 0 } efficiency
            && !(Number(efficiency) is { } fraction && fraction is > 0 and <= 1))
        {
            problems.Add(
                $"{PvSystemSettingKeys.HomeBatteryChargeEfficiency} ('{efficiency}') must be more than 0 and at most 1.",
                Phrase.Of("Config.BadBatteryEfficiency", new PhraseArg.Text(efficiency)));
        }

        return problems;
    }

    private static double? Number(string value) =>
        double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var number)
            ? number
            : null;

    private List<ConfiguredSetting> Describe(IReadOnlyDictionary<string, string> stored) =>
    [
        .. PvSystemSettingKeys.All.Select(key =>
        {
            var next = Next(key, stored);
            return new ConfiguredSetting(
                key,
                next?.Value,
                _running[key],
                next?.Source ?? SettingSource.Default,
                next?.Detail ?? string.Empty,
                Underlying(key));
        }),
    ];

    // What the key would be with no overrides file at all.
    private string? Underlying(string key) => Next(key, new Dictionary<string, string>())?.Value;

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
                        (value, SettingSource.Environment, PvSystemSettingKeys.EnvironmentVariable(key)),
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
