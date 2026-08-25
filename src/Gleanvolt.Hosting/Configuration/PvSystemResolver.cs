using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure;
using Gleanvolt.Infrastructure.OpenWeather;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Turns configuration into the one <see cref="PvSystemInfo"/> the process runs on (issue #111).
///
/// <para><b>Two sources, one answer.</b> The <c>Pv</c> section is where an installation is described
/// from now on; <c>Solax:Inverter</c>, <c>Solax:EvCharger</c>, <c>Weather:Latitude</c> and
/// <c>Weather:Longitude</c> are where it used to be. While both exist, <b>the older key wins wherever
/// it is set</b> — the deliberate choice for the additive phase, because it makes an upgrade with an
/// untouched <c>.env</c> behave exactly as the previous build did, and reduces this whole change to
/// something that cannot alter a running site's behaviour. Each such win is reported as a deprecation
/// so the log doubles as the migration checklist, and the keys go away in the phase that removes
/// them.</para>
///
/// <para><b>Validated here, not at first use.</b> A site that cannot be described is a startup failure
/// naming the key, because every alternative is worse: a missing inverter address surfaces as a
/// connection error minutes later, and a half-set pair of coordinates silently moves the site into the
/// Atlantic. All problems are collected and reported together — fixing configuration one restart per
/// mistake is a miserable way to spend an evening.</para>
/// </summary>
public static partial class PvSystemResolver
{
    /// <summary>The id a charger takes when none was configured.</summary>
    public const string DefaultChargerId = "charger";

    /// <summary>How many chargers the control logic can actually drive. See <see cref="PvSystemOptions.Chargers"/>.</summary>
    public const int SupportedChargerCount = 1;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,47}$")]
    private static partial Regex Slug();

    /// <summary>
    /// Reads the <c>Pv</c> section and its deprecated predecessors and reconciles them into one site.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The configuration does not describe a usable system. The message lists every problem found, each
    /// naming the configuration key it is about.
    /// </exception>
    public static PvSystemResolution Resolve(IConfiguration configuration)
    {
        var pv = configuration.GetSection(PvSystemOptions.SectionName).Get<PvSystemOptions>() ?? new PvSystemOptions();
        var solax = configuration.GetSection(SolaxOptions.SectionName).Get<SolaxOptions>() ?? new SolaxOptions();
        var weather = configuration.GetSection(WeatherOptions.SectionName).Get<WeatherOptions>() ?? new WeatherOptions();

        var problems = new List<string>();
        var deprecations = new List<string>();

        var id = ResolveId(pv, problems);
        var (latitude, longitude) = ResolveLocation(pv, weather, problems, deprecations);
        var inverter = ResolveInverter(pv, solax, problems, deprecations);
        var chargers = ResolveChargers(pv, solax, problems, deprecations);

        var site = new PvSystemInfo(
            Id: id,
            Name: string.IsNullOrWhiteSpace(pv.Name) ? id : pv.Name.Trim(),
            Address: pv.Address.Trim(),
            Latitude: latitude,
            Longitude: longitude,
            AzimuthDegrees: Normalise(Range(pv.AzimuthDegrees, -360, 360, "Pv:AzimuthDegrees", problems)),
            TiltDegrees: Range(pv.TiltDegrees, 0, 90, "Pv:TiltDegrees", problems),
            CapacityKwp: Positive(pv.CapacityKwp, "Pv:CapacityKwp", problems),
            InverterCapacityKw: Positive(pv.InverterCapacityKw, "Pv:InverterCapacityKw", problems),
            LossFactor: Fraction(pv.LossFactor, "Pv:LossFactor", problems),
            InstallDate: ParseInstallDate(pv.InstallDate, problems),
            Inverter: inverter,
            Chargers: chargers);

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The PV system is not configured usably:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem)));
        }

        return new PvSystemResolution(site, deprecations);
    }

    // Optional in this phase, and deliberately so. Nothing yet depends on the id -- it becomes the MQTT
    // topic segment and the Home Assistant device identity in the phase that publishes them, and that
    // is the phase in which an anonymous system stops being a describable one. Requiring it now would
    // stop every existing deployment on a value that would change nothing about how it ran.
    private static string ResolveId(PvSystemOptions pv, List<string> problems)
    {
        var id = pv.Id.Trim();

        if (id.Length == 0)
        {
            return string.Empty;
        }

        if (!Slug().IsMatch(id))
        {
            problems.Add(
                $"Pv:Id ('{id}') must be a slug: lower-case letters, digits, '-' and '_', starting with a "
                + "letter or digit, at most 48 characters. It becomes an MQTT topic segment and a Home "
                + "Assistant object id.");
        }

        return id;
    }

    private static (double? Latitude, double? Longitude) ResolveLocation(
        PvSystemOptions pv,
        WeatherOptions weather,
        List<string> problems,
        List<string> deprecations)
    {
        var latitude = pv.Latitude;
        var longitude = pv.Longitude;

        if (weather.Latitude is not null || weather.Longitude is not null)
        {
            deprecations.Add(
                "Weather:Latitude/Weather:Longitude are deprecated and are being used instead of "
                + "Pv:Latitude/Pv:Longitude. Move the coordinates to the Pv section.");

            latitude = weather.Latitude ?? latitude;
            longitude = weather.Longitude ?? longitude;
        }

        if (latitude is null != longitude is null)
        {
            problems.Add(
                "A latitude without a longitude (or the other way round) describes nowhere. Set both "
                + "Pv:Latitude and Pv:Longitude, or neither.");
            return (null, null);
        }

        Range(latitude, -90, 90, "Pv:Latitude", problems);
        Range(longitude, -180, 180, "Pv:Longitude", problems);

        return (latitude, longitude);
    }

    private static PvDeviceInfo ResolveInverter(
        PvSystemOptions pv,
        SolaxOptions solax,
        List<string> problems,
        List<string> deprecations)
    {
        // The identity always comes from the Pv section, whichever section supplies the address: what the
        // box is called and which model it is are things the older keys cannot express at all, so there
        // is nothing to be deprecated about reading them from the only place that has them.
        var name = pv.Inverter?.Name.Trim() ?? string.Empty;
        var model = pv.Inverter?.Model.Trim() ?? string.Empty;

        if (!string.IsNullOrWhiteSpace(solax.Inverter?.Host))
        {
            deprecations.Add(
                "Solax:Inverter is deprecated and is being used instead of Pv:Inverter. Move the "
                + "inverter's address to the Pv section.");

            return new PvDeviceInfo(ModbusClientKeys.Inverter, name, model, solax.Inverter);
        }

        if (string.IsNullOrWhiteSpace(pv.Inverter?.Host))
        {
            problems.Add("No inverter is configured. Set Pv:Inverter:Host.");

            // A placeholder so the rest of the resolution can be reported too; the throw the caller makes
            // is unconditional once anything has landed in `problems`.
            return new PvDeviceInfo(ModbusClientKeys.Inverter, name, model, new DeviceConfig { Host = string.Empty });
        }

        ValidateConnection(pv.Inverter, "Pv:Inverter", problems);

        return new PvDeviceInfo(ModbusClientKeys.Inverter, name, model, pv.Inverter.ToDeviceConfig());
    }

    private static IReadOnlyList<PvDeviceInfo> ResolveChargers(
        PvSystemOptions pv,
        SolaxOptions solax,
        List<string> problems,
        List<string> deprecations)
    {
        var usingDeprecatedAddress = !string.IsNullOrWhiteSpace(solax.EvCharger?.Host);

        if (usingDeprecatedAddress)
        {
            deprecations.Add(
                "Solax:EvCharger is deprecated and is being used instead of Pv:Chargers. Move the "
                + "charger's address to the Pv section.");
        }
        else if (pv.Chargers.Length == 0)
        {
            problems.Add("No EV charger is configured. Set Pv:Chargers:0:Host.");
            return [];
        }

        // Checked whichever section supplies the address, because a second entry says what the operator
        // expects rather than what is wired: two chargers listed means two cars are meant to be managed,
        // and one of them would silently not be. Not "the first one wins" — said at startup instead.
        if (pv.Chargers.Length > SupportedChargerCount)
        {
            problems.Add(
                $"Only {SupportedChargerCount} EV charger is supported; Pv:Chargers has {pv.Chargers.Length}. "
                + "The configuration can express more so that it need not change when the control logic can "
                + "drive more, which it cannot yet.");
        }

        var chargers = new List<PvDeviceInfo>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        for (var index = 0; index < pv.Chargers.Length; index++)
        {
            var charger = pv.Chargers[index];
            var path = $"Pv:Chargers:{index}";
            var id = string.IsNullOrWhiteSpace(charger.Id) ? DefaultChargerId : charger.Id.Trim();

            if (!Slug().IsMatch(id))
            {
                problems.Add($"{path}:Id ('{id}') must be a slug: lower-case letters, digits, '-' and '_'.");
            }

            if (!seen.Add(id))
            {
                problems.Add($"{path}:Id ('{id}') is used by more than one charger; each id must be unique.");
            }

            // The identity is the Pv section's even when the address is not — see the inverter above.
            if (usingDeprecatedAddress)
            {
                chargers.Add(new PvDeviceInfo(id, charger.Name.Trim(), charger.Model.Trim(), solax.EvCharger!));
                break;
            }

            if (string.IsNullOrWhiteSpace(charger.Host))
            {
                problems.Add($"{path}:Host is required.");
                continue;
            }

            ValidateConnection(charger, path, problems);

            chargers.Add(new PvDeviceInfo(id, charger.Name.Trim(), charger.Model.Trim(), charger.ToDeviceConfig()));
        }

        // The older key names a charger the Pv section says nothing about: one charger, at that address,
        // under the default id.
        if (usingDeprecatedAddress && chargers.Count == 0)
        {
            chargers.Add(new PvDeviceInfo(DefaultChargerId, string.Empty, string.Empty, solax.EvCharger!));
        }

        return chargers;
    }

    private static void ValidateConnection(PvDeviceOptions device, string path, List<string> problems)
    {
        if (device.Port is { } port && port is < 1 or > 65535)
        {
            problems.Add($"{path}:Port ({port}) is not a TCP port.");
        }

        if (device.MinRequestInterval is { } interval && interval < TimeSpan.Zero)
        {
            problems.Add($"{path}:MinRequestInterval ({interval}) cannot be negative.");
        }
    }

    private static DateOnly? ParseInstallDate(string value, List<string> problems)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateOnly.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
        {
            return date;
        }

        problems.Add($"Pv:InstallDate ('{value}') is not a date. Write it as yyyy-MM-dd.");
        return null;
    }

    private static double? Range(double? value, double min, double max, string key, List<string> problems)
    {
        if (value is { } number && (number < min || number > max))
        {
            problems.Add($"{key} ({number}) is outside {min}..{max}.");
        }

        return value;
    }

    private static double? Positive(double? value, string key, List<string> problems)
    {
        if (value is { } number && number <= 0)
        {
            problems.Add($"{key} ({number}) must be greater than zero.");
        }

        return value;
    }

    private static double? Fraction(double? value, string key, List<string> problems)
    {
        if (value is { } number && (number <= 0 || number > 1))
        {
            problems.Add($"{key} ({number}) must be a fraction in (0, 1].");
        }

        return value;
    }

    // -90 and 270 are the same direction, and both are things people write. Storing one of them means
    // anything reading the site back gets a bearing it can compare without normalising first.
    private static double? Normalise(double? azimuth) => azimuth is { } degrees ? (degrees % 360 + 360) % 360 : null;
}

/// <summary>
/// The outcome of reading the configuration: the system, and what the operator should change about how
/// it was described. The deprecations are carried rather than logged in place because resolution
/// happens while the service collection is being built, which is before there is a logger to log to.
/// </summary>
/// <param name="Site">The installation, validated.</param>
/// <param name="Deprecations">
/// One message per older key that supplied a value, in configuration order. Empty once a deployment has
/// moved everything into the <c>Pv</c> section.
/// </param>
public sealed record PvSystemResolution(PvSystemInfo Site, IReadOnlyList<string> Deprecations);
