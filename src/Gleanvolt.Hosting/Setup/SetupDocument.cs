using System.Globalization;
using System.Text.Json;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure;
using Gleanvolt.Infrastructure.Solcast;
using Gleanvolt.Web.Auth;

namespace Gleanvolt.Hosting.Setup;

/// <summary>
/// A set of setup values in the shape of <c>appsettings.json</c> (#301): what the web wizard saves
/// step by step and what <c>gleanvolt setup --file</c> reads, through one path with one set of rules.
///
/// <para><b>Only setup keys.</b> A document holds the keys a first run collects and nothing else,
/// checked against <see cref="AllowedKeys"/>; anything outside it is refused by name rather than
/// written, so a setup file can never reach a key the UI cannot edit.</para>
///
/// <para><b>Where each value goes.</b> Non-secret settings go to the overrides file the Settings page
/// already writes, secrets to <see cref="ISecretStore"/>, the password through
/// <see cref="WebPasswordService"/> (which hashes it and issues the recovery key). Never to
/// <c>.env</c> or <c>appsettings.json</c>.</para>
///
/// <para><b>A step at a time.</b> <see cref="Validate"/> judges the document against the
/// configuration it would be applied over, so a page that saves only the devices is checked against
/// the location saved before it; it does not demand that everything required is present. Whether the
/// result is usable is <see cref="ISetupState.Current"/>'s question.</para>
/// </summary>
public sealed class SetupDocument
{
    /// <summary>The web UI's password, in plain text. Hashed on the way in; never stored as given.</summary>
    public const string WebPassword = "Web:Password";

    /// <summary>The Solcast API key: a secret, stored with its resource id as one account.</summary>
    public const string SolcastApiKey = "Solcast:ApiKey";

    /// <summary>The Solcast rooftop site, stored with the key.</summary>
    public const string SolcastResourceId = "Solcast:ResourceId";

    /// <summary>The site's time zone.</summary>
    public const string TimeZone = "Controller:TimeZone";

    /// <summary>The home battery's usable capacity.</summary>
    public const string HomeBatteryCapacityKWh = "ChargeControl:Forecast:BatteryCapacityKWh";

    /// <summary>Every key a setup document may hold.</summary>
    public static IReadOnlyList<string> AllowedKeys { get; } =
    [
        WebPassword,
        .. PvSystemSettingKeys.All,
        TimeZone,
        HomeBatteryCapacityKWh,
        SolcastApiKey,
        SolcastResourceId,
        .. EvSettingKeys.All,
    ];

    /// <summary>The keys whose values are secrets: never written to the overrides file.</summary>
    public static IReadOnlyList<string> SecretKeys { get; } =
        [WebPassword, SolcastApiKey, EvSettingKeys.WebsitePassword, EvSettingKeys.DataActPassword];

    private SetupDocument(IReadOnlyDictionary<string, string> values) => Values = values;

    /// <summary>The values, by configuration key.</summary>
    public IReadOnlyDictionary<string, string> Values { get; }

    /// <summary>A document from flat configuration keys (<c>Pv:Inverter:Host</c>).</summary>
    public static SetupDocument From(IReadOnlyDictionary<string, string> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        return new SetupDocument(new Dictionary<string, string>(values, StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// A document from JSON in the shape of <c>appsettings.json</c>. Malformed JSON is a
    /// <see cref="FormatException"/>, so the CLI can report it as the file's problem.
    /// </summary>
    public static SetupDocument FromJson(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        try
        {
            using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(json));
            var parsed = new ConfigurationBuilder().AddJsonStream(stream).Build();

            return From(parsed.AsEnumerable()
                .Where(entry => entry.Value is not null)
                .ToDictionary(entry => entry.Key, entry => entry.Value!, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex) when (ex is JsonException or InvalidDataException)
        {
            throw new FormatException($"The setup document is not valid JSON: {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Every problem with applying this document over <paramref name="current"/>: keys outside the
    /// allowlist, and values the resolver, the car's rules or this class's own checks refuse. Empty
    /// means <see cref="Apply"/> will write it.
    /// </summary>
    public ConfigurationProblems Validate(IConfiguration current)
    {
        ArgumentNullException.ThrowIfNull(current);

        var refused = Values.Keys.Where(key => !AllowedKeys.Contains(key, StringComparer.OrdinalIgnoreCase)).ToList();
        if (refused.Count > 0)
        {
            return ConfigurationProblems.NotEditable(refused);
        }

        var problems = new ConfigurationProblems();
        var candidate = Candidate(current);

        if (Values.Keys.Any(PvSystemSettingKeys.IsEditable))
        {
            // Wrong values are refused; absent ones are not this document's business unless it is the
            // one that emptied them. A step that saves the site's name before the devices exist is
            // fine -- whether everything is there is ISetupState's question, not a save's.
            var pvProblems = new ConfigurationProblems();
            Collect(pvProblems, () =>
            {
                PvSystemResolver.Resolve(candidate);
                return [];
            });

            var phrases = pvProblems.Phrases;
            for (var index = 0; index < pvProblems.Count; index++)
            {
                if (!IsAbsence(phrases[index]))
                {
                    Add(problems, pvProblems[index], phrases[index]);
                }
            }
        }

        if (Values.Keys.Any(EvSettingKeys.IsEditable))
        {
            var chargeControl = candidate.GetSection(ChargeControlOptions.SectionName).Get<ChargeControlOptions>()
                ?? new ChargeControlOptions();

            Collect(problems, () => EvRules.Validate(
                candidate.GetSection(EvOptions.SectionName).Get<EvOptions>() ?? new EvOptions(),
                chargeControl.MinChargingCurrentAmps,
                chargeControl.MaxChargingCurrentAmps));
        }

        if (Values.TryGetValue(TimeZone, out var zone) && !string.IsNullOrWhiteSpace(zone))
        {
            try
            {
                ZonedTimeProvider.Resolve(zone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                problems.Add(
                    $"Controller:TimeZone ('{zone.Trim()}') is not a time zone this host knows.",
                    Phrase.Of("Config.SetupBadTimeZone", new PhraseArg.Text(zone.Trim())));
            }
        }

        if (Values.TryGetValue(HomeBatteryCapacityKWh, out var capacity) && !string.IsNullOrWhiteSpace(capacity)
            && !(double.TryParse(capacity, NumberStyles.Float, CultureInfo.InvariantCulture, out var kwh) && kwh > 0))
        {
            problems.Add(
                $"{HomeBatteryCapacityKWh} ('{capacity}') must be a positive number of kWh.",
                Phrase.Of("Config.SetupBadBatteryCapacity", new PhraseArg.Text(capacity)));
        }

        if (Values.TryGetValue(WebPassword, out var password) && password.Length < WebPasswordService.MinimumLength)
        {
            problems.Add(
                $"The web UI password must be at least {WebPasswordService.MinimumLength} characters.",
                Phrase.Of("Config.SetupPasswordTooShort", new PhraseArg.Number(WebPasswordService.MinimumLength)));
        }

        var hasKey = Values.TryGetValue(SolcastApiKey, out var apiKey) && !string.IsNullOrWhiteSpace(apiKey);
        var hasSite = Values.TryGetValue(SolcastResourceId, out var resource) && !string.IsNullOrWhiteSpace(resource);
        if (hasKey != hasSite)
        {
            problems.Add(
                "A Solcast account needs both Solcast:ApiKey and Solcast:ResourceId.",
                Phrase.Of("Config.SetupSolcastIncomplete"));
        }

        return problems;
    }

    /// <summary>
    /// Validates, then writes: non-secret values to the overrides file in one read-modify-write (an
    /// empty value removes the key, so it falls back to <c>.env</c> and <c>appsettings.json</c>),
    /// secrets to the secret store, the password through the password service.
    /// </summary>
    public SetupApplyResult Apply(SetupTarget target)
    {
        ArgumentNullException.ThrowIfNull(target);

        var problems = Validate(target.Configuration);
        if (problems.Count > 0)
        {
            return SetupApplyResult.Refused(problems);
        }

        var plain = Values
            .Where(entry => !SecretKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)
                && !entry.Key.Equals(SolcastResourceId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        if (plain.Count > 0)
        {
            try
            {
                lock (PvSystemOverrides.Gate)
                {
                    var stored = PvSystemOverrides.Read(target.OverridesPath);

                    foreach (var (key, value) in plain)
                    {
                        if (string.IsNullOrWhiteSpace(value))
                        {
                            stored.Remove(key);
                        }
                        else
                        {
                            stored[key] = value.Trim();
                        }
                    }

                    PvSystemOverrides.Write(target.OverridesPath, stored);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                return SetupApplyResult.Refused(ConfigurationProblems.NotWritten(target.OverridesPath, ex.Message));
            }
        }

        if (Values.TryGetValue(SolcastApiKey, out var apiKey) && !string.IsNullOrWhiteSpace(apiKey))
        {
            var account = new SolcastAccount(apiKey.Trim(), Values[SolcastResourceId].Trim());
            var saved = target.Solcast?.Save(account)
                ?? target.Secrets.Write(SecretNames.SolcastAccount, JsonSerializer.Serialize(account));

            if (!saved)
            {
                return SetupApplyResult.Refused(SecretNotSaved("Solcast"));
            }
        }

        var vehiclePassword = Values
            .Where(entry => entry.Key.Equals(EvSettingKeys.WebsitePassword, StringComparison.OrdinalIgnoreCase)
                || entry.Key.Equals(EvSettingKeys.DataActPassword, StringComparison.OrdinalIgnoreCase))
            .Select(entry => entry.Value)
            .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));

        if (vehiclePassword is not null && !target.Secrets.Write(SecretNames.VehicleAccountPassword, vehiclePassword))
        {
            return SetupApplyResult.Refused(SecretNotSaved("vehicle account"));
        }

        string? recoveryKey = null;

        if (Values.TryGetValue(WebPassword, out var password))
        {
            if (target.Password is null)
            {
                return SetupApplyResult.Refused(new ConfigurationProblems(
                    "This host cannot set the web UI password.", Phrase.Of("Config.SetupNoPasswordService")));
            }

            var changed = target.Password.SetInitial(password);
            if (!changed.Succeeded)
            {
                return SetupApplyResult.Refused(changed.Error == PasswordError.AlreadySet
                    ? new ConfigurationProblems(
                        "A web UI password is already set. Change it on Settings, or reset it from the login page.",
                        Phrase.Of("Config.SetupPasswordAlreadySet"))
                    : new ConfigurationProblems(
                        "The web UI password could not be saved.", Phrase.Of("Config.SetupPasswordNotSaved")));
            }

            recoveryKey = changed.RecoveryKey;
        }

        return new SetupApplyResult(true, [], recoveryKey);
    }

    // The configuration this document would produce: the current one, with the document's non-secret
    // values over it. The password stands in as a hash-shaped placeholder so the password check sees
    // one; nothing reads it as a hash.
    private IConfiguration Candidate(IConfiguration current)
    {
        // An empty value removes the key from the overrides file, which leaves whatever .env says; the
        // current configuration is the closest thing to that, so an empty value adds nothing here.
        var overlay = Values
            .Where(entry => !SecretKeys.Contains(entry.Key, StringComparer.OrdinalIgnoreCase)
                && !string.IsNullOrWhiteSpace(entry.Value))
            .Select(entry => new KeyValuePair<string, string?>(entry.Key, entry.Value.Trim()))
            .ToList();

        return new ConfigurationBuilder().AddConfiguration(current).AddInMemoryCollection(overlay).Build();
    }

    // A device the configuration does not have yet, as opposed to one it has wrong.
    private static bool IsAbsence(Phrase? phrase) =>
        phrase?.Key is "Config.PvNoInverter" or "Config.PvNoCharger" or "Config.PvHostRequired";

    // The resolver throws its problems; the car's rules return them; a value that is not a number at
    // all is the binder's own InvalidOperationException. All three end up in one list.
    private static void Collect(ConfigurationProblems problems, Func<ConfigurationProblems> check)
    {
        try
        {
            var found = check();
            var phrases = found.Phrases;
            for (var index = 0; index < found.Count; index++)
            {
                Add(problems, found[index], phrases[index]);
            }
        }
        catch (ConfigurationRefusedException refused)
        {
            var phrases = refused.Problems.Phrases;
            for (var index = 0; index < refused.Problems.Count; index++)
            {
                Add(problems, refused.Problems[index], phrases[index]);
            }
        }
        catch (InvalidOperationException ex)
        {
            problems.Add(ex.Message);
        }
    }

    private static void Add(ConfigurationProblems problems, string english, Phrase? phrase)
    {
        if (phrase is null)
        {
            problems.Add(english);
        }
        else
        {
            problems.Add(english, phrase);
        }
    }

    private static ConfigurationProblems SecretNotSaved(string what) => new(
        $"The {what} secret could not be saved to the secret store. Check the data directory's permissions.",
        Phrase.Of("Config.SetupSecretNotSaved", new PhraseArg.Text(what)));
}

/// <summary>Where <see cref="SetupDocument.Apply"/> writes, and what it validates against.</summary>
/// <param name="Configuration">The configuration the document is applied over.</param>
/// <param name="OverridesPath">The overrides file non-secret values go to.</param>
/// <param name="Secrets">The secret store secrets go to.</param>
/// <param name="Password">Sets the web UI password; null on a host that has none.</param>
/// <param name="Solcast">The running Solcast account, so a saved key is used without a restart; null writes the store directly.</param>
public sealed record SetupTarget(
    IConfiguration Configuration,
    string OverridesPath,
    ISecretStore Secrets,
    WebPasswordService? Password = null,
    SolcastAccountStore? Solcast = null);

/// <summary>What applying a document came to. <see cref="RecoveryKey"/> is set when it set the password.</summary>
public sealed record SetupApplyResult(bool Applied, IReadOnlyList<string> Problems, string? RecoveryKey)
{
    public static SetupApplyResult Refused(ConfigurationProblems problems) => new(false, problems, null);
}
