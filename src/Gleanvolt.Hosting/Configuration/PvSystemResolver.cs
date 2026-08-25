using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure;

namespace Gleanvolt.Hosting.Configuration;

/// <summary>
/// Turns the <c>Pv</c> section into the one <see cref="PvSystemInfo"/> the process runs on (issue
/// #111).
///
/// <para><b>One source.</b> The installation is described in one section and nowhere else. The older
/// keys that used to hold pieces of it — <c>Solax:Inverter</c>, <c>Solax:EvCharger</c>,
/// <c>Weather:Latitude</c>, <c>Weather:Longitude</c> — are gone, and one that is still set is refused
/// by <see cref="RetiredConfigurationKeys"/> rather than ignored. There is therefore no precedence to
/// reason about here, which was the point of the exercise.</para>
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

    /// <summary>Reads the <c>Pv</c> section into one validated system.</summary>
    /// <exception cref="InvalidOperationException">
    /// The configuration does not describe a usable system. The message lists every problem found, each
    /// naming the configuration key it is about.
    /// </exception>
    public static PvSystemInfo Resolve(IConfiguration configuration)
    {
        var pv = configuration.GetSection(PvSystemOptions.SectionName).Get<PvSystemOptions>() ?? new PvSystemOptions();

        var problems = new List<string>();

        var id = ResolveId(pv, problems);
        var (latitude, longitude) = ResolveLocation(pv, problems);

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
            Inverter: ResolveInverter(pv, problems),
            Chargers: ResolveChargers(pv, problems));

        if (problems.Count > 0)
        {
            throw new InvalidOperationException(
                "The PV system is not configured usably:" + Environment.NewLine
                + string.Join(Environment.NewLine, problems.Select(problem => "  - " + problem)));
        }

        return site;
    }

    // Optional, still. Nothing consumes the id yet -- it becomes the MQTT topic segment and the Home
    // Assistant device identity in the phase that publishes them, and that is the phase in which an
    // anonymous system stops being a describable one.
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

    private static (double? Latitude, double? Longitude) ResolveLocation(PvSystemOptions pv, List<string> problems)
    {
        if (pv.Latitude is null != pv.Longitude is null)
        {
            problems.Add(
                "A latitude without a longitude (or the other way round) describes nowhere. Set both "
                + "Pv:Latitude and Pv:Longitude, or neither.");
            return (null, null);
        }

        Range(pv.Latitude, -90, 90, "Pv:Latitude", problems);
        Range(pv.Longitude, -180, 180, "Pv:Longitude", problems);

        return (pv.Latitude, pv.Longitude);
    }

    private static PvDeviceInfo ResolveInverter(PvSystemOptions pv, List<string> problems)
    {
        var name = pv.Inverter?.Name.Trim() ?? string.Empty;
        var model = pv.Inverter?.Model.Trim() ?? string.Empty;

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

    private static IReadOnlyList<PvDeviceInfo> ResolveChargers(PvSystemOptions pv, List<string> problems)
    {
        if (pv.Chargers.Length == 0)
        {
            problems.Add("No EV charger is configured. Set Pv:Chargers:0:Host.");
            return [];
        }

        // A second charger says what the operator expects rather than what is wired: two listed means two
        // cars are meant to be managed, and one of them would silently not be. Not "the first one wins" --
        // said at startup instead.
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

            if (string.IsNullOrWhiteSpace(charger.Host))
            {
                problems.Add($"{path}:Host is required.");
                continue;
            }

            ValidateConnection(charger, path, problems);

            chargers.Add(new PvDeviceInfo(id, charger.Name.Trim(), charger.Model.Trim(), charger.ToDeviceConfig()));
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

    // -90 and 270 are the same direction, and both are things people write -- Solcast's own rooftop-site
    // definition uses the negative half, so a value copied from there arrives as -180 for due south.
    // Storing one of them means anything reading the site back gets a bearing it can compare without
    // normalising first.
    private static double? Normalise(double? azimuth) => azimuth is { } degrees ? (degrees % 360 + 360) % 360 : null;
}
