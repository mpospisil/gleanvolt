using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure;
using Gleanvolt.Web;

namespace Gleanvolt.Hosting.Setup;

/// <summary>
/// Computes <see cref="SetupStatus"/> from configuration (#301). The rules are the resolver's own --
/// <see cref="PvSystemRules"/>, the retired keys, a time zone that resolves -- plus the web UI's
/// password. A location and a time zone are asked for too, but only recommended: the controller runs
/// without them, and the devices are what it cannot run without.
/// </summary>
public static class SetupStatusEvaluator
{
    /// <summary>
    /// Evaluates <paramref name="configuration"/>. Pure: reads nothing but its arguments, so the same
    /// configuration always gives the same answer, at startup and on every page load after it.
    /// </summary>
    /// <param name="configuration">The configuration to judge, overrides file included.</param>
    /// <param name="host">What the host itself contributes: its own time zone and platform.</param>
    /// <param name="optional">
    /// Which optional settings are in place, where that takes more than configuration to know (a
    /// Solcast key saved in the secret store, a tariff saved to its file). Null judges them from
    /// configuration alone.
    /// </param>
    public static SetupStatus Evaluate(IConfiguration configuration, SetupHost host, OptionalSettings? optional = null)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(host);

        var required = new List<SetupProblem>();
        var recommended = new List<SetupProblem>();

        // Before anything is bound, as RetiredConfigurationKeys always has been: a moved key would
        // otherwise be silently ignored, and the controller run against whatever replaced it.
        foreach (var (english, phrase) in RetiredConfigurationKeys.Find(configuration))
        {
            required.Add(new SetupProblem(SetupArea.Configuration, english, phrase));
        }

        Password(configuration, required);

        var pv = configuration.GetSection(PvSystemOptions.SectionName).Get<PvSystemOptions>() ?? new PvSystemOptions();
        var pvProblems = new ConfigurationProblems();
        PvSystemRules.Resolve(pv, ModbusClientKeys.Inverter, pvProblems);

        var phrases = pvProblems.Phrases;
        for (var index = 0; index < pvProblems.Count; index++)
        {
            required.Add(new SetupProblem(AreaOf(pvProblems[index]), pvProblems[index], phrases[index]));
        }

        // No location, or half of one (which the rules read as none): nothing the controller needs to
        // run depends on a place, so a first run asks for it but does not wait for it -- without one the
        // weather is simply not fetched.
        if (pv.Latitude is null != pv.Longitude is null)
        {
            recommended.Add(new SetupProblem(
                SetupArea.Location,
                "A latitude without a longitude (or the other way round) is not a location. Set both "
                + "Pv:Latitude and Pv:Longitude, or neither.",
                Phrase.Of("Config.PvLocation")));
        }
        else if (pv.Latitude is null && pv.Longitude is null)
        {
            recommended.Add(new SetupProblem(
                SetupArea.Location,
                "The site's location is not set. Set Pv:Latitude and Pv:Longitude.",
                Phrase.Of("Config.SetupNoLocation")));
        }

        TimeZone(configuration, host, required, recommended);

        return new SetupStatus(
            required,
            Optional(configuration, optional ?? OptionalSettings.FromConfiguration(configuration)),
            recommended);
    }

    // The password the UI is locked with. Not needed where nothing serves the UI, and not needed
    // where the operator has said Web:RequireAuthentication=false: SetupGate honours that choice, and
    // so does this.
    private static void Password(IConfiguration configuration, List<SetupProblem> required)
    {
        var web = configuration.GetSection(WebOptions.SectionName).Get<WebOptions>() ?? new WebOptions();

        if (!web.Enabled || web.RequireAuthentication == false || !string.IsNullOrWhiteSpace(web.PasswordHash))
        {
            return;
        }

        required.Add(new SetupProblem(
            SetupArea.Password,
            "No web UI password is set. Choose one on the setup page.",
            Phrase.Of("Config.SetupNoPassword")));
    }

    // Set means one of two things: Controller:TimeZone names a zone that exists, or it is empty and
    // the host's own zone can be believed. On Linux the container's TZ sets that; on Windows .NET
    // ignores TZ, and a host that reports bare UTC has most likely never been told its zone at all.
    // A household on UTC proper is not ruled out -- it says so with Controller:TimeZone=Etc/UTC.
    // A zone that does not resolve is required: ZonedTimeProvider would stop the host at startup. One
    // that is not set is only recommended: the controller runs on the host's zone (UTC) and says so.
    private static void TimeZone(
        IConfiguration configuration, SetupHost host, List<SetupProblem> required, List<SetupProblem> recommended)
    {
        var configured = configuration.GetSection(ControllerOptions.SectionName)[nameof(ControllerOptions.TimeZone)];

        if (!string.IsNullOrWhiteSpace(configured))
        {
            try
            {
                ZonedTimeProvider.Resolve(configured);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                required.Add(new SetupProblem(
                    SetupArea.TimeZone,
                    $"Controller:TimeZone ('{configured.Trim()}') is not a time zone this host knows.",
                    Phrase.Of("Config.SetupBadTimeZone", new PhraseArg.Text(configured.Trim()))));
            }

            return;
        }

        if (host.IsWindows || IsBareUtc(host.LocalZone))
        {
            recommended.Add(new SetupProblem(
                SetupArea.TimeZone,
                "The site's time zone is not set. Set Controller:TimeZone (e.g. Europe/Prague).",
                Phrase.Of("Config.SetupNoTimeZone")));
        }
    }

    private static bool IsBareUtc(TimeZoneInfo zone) =>
        zone.BaseUtcOffset == TimeSpan.Zero
        && !zone.SupportsDaylightSavingTime
        && zone.Id is "UTC" or "Etc/UTC" or "Etc/UCT" or "Etc/Universal" or "Etc/Zulu" or "Universal" or "Zulu"
            or "Coordinated Universal Time";

    private static List<OptionalSetting> Optional(IConfiguration configuration, OptionalSettings present)
    {
        var missing = new List<OptionalSetting>();

        if (!present.Solcast)
        {
            missing.Add(OptionalSetting.Solcast);
        }

        if (!present.Ev)
        {
            missing.Add(OptionalSetting.Ev);
        }

        if (!present.Tariff)
        {
            missing.Add(OptionalSetting.Tariff);
        }

        if (!configuration.GetSection(HomeAssistantOptions.SectionName).GetValue<bool>(nameof(HomeAssistantOptions.Enabled)))
        {
            missing.Add(OptionalSetting.Mqtt);
        }

        if (!present.Weather)
        {
            missing.Add(OptionalSetting.Weather);
        }

        return missing;
    }

    // Which step of a setup a resolver problem belongs to, from the key it names. Coarse on purpose:
    // the wizard needs to know which page to send someone to, not to parse the sentence.
    private static SetupArea AreaOf(string problem) =>
        problem.Contains("Pv:Inverter", StringComparison.Ordinal) ? SetupArea.Inverter
        : problem.Contains("Pv:Chargers", StringComparison.Ordinal) || problem.Contains("EV charger", StringComparison.Ordinal) ? SetupArea.Charger
        : problem.Contains("Pv:Latitude", StringComparison.Ordinal) || problem.Contains("Pv:Longitude", StringComparison.Ordinal) ? SetupArea.Location
        : SetupArea.Site;
}

/// <summary>What the host contributes to the time-zone question: its own zone, and whether it is Windows.</summary>
public sealed record SetupHost(TimeZoneInfo LocalZone, bool IsWindows)
{
    /// <summary>The machine this process is running on.</summary>
    public static SetupHost Current => new(TimeZoneInfo.Local, OperatingSystem.IsWindows());
}

/// <summary>Which optional settings are in place.</summary>
public sealed record OptionalSettings(bool Solcast, bool Ev, bool Tariff, bool Weather = false)
{
    /// <summary>
    /// Judged from configuration alone: a Solcast key and site in the environment, a described car, a
    /// weather key in the environment.
    /// The tariff lives only in its own file, so configuration cannot see one.
    /// </summary>
    public static OptionalSettings FromConfiguration(IConfiguration configuration)
    {
        var solcast = !string.IsNullOrWhiteSpace(configuration["Solcast:ApiKey"])
            && configuration["Solcast:ResourceId"] is { } resource
            && !string.IsNullOrWhiteSpace(resource)
            && resource != "your-solcast-resource-id";

        var ev = !string.IsNullOrWhiteSpace(configuration[EvSettingKeys.Id])
            || double.TryParse(configuration[EvSettingKeys.BatteryCapacityKWh], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var capacity) && capacity > 0;

        var weather = !string.IsNullOrWhiteSpace(configuration["Weather:ApiKey"])
            && configuration["Weather:ApiKey"] != "your-openweathermap-api-key";

        return new OptionalSettings(solcast, ev, Tariff: false, weather);
    }
}
