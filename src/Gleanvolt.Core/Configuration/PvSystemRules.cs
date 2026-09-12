using System.Globalization;
using System.Text.RegularExpressions;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Configuration;

/// <summary>
/// What makes a described PV system a usable one: every rule the <c>Pv</c> section is held to, stated
/// once (issue #111).
///
/// <para><b>Why this is in Core and not in the host.</b> Two things validate an installation and they
/// are not in the same process. The controller validates at startup, where a bad site is a startup
/// failure. The portal validates at save time, where a bad site is a form that will not submit — and
/// it has to, because a configuration accepted in a browser and refused at the next poll takes a house
/// down with the error visible only in a log file in somebody's garage. Two hand-maintained copies of
/// "tilt is 0..90" is one copy too many, and the copy that drifts is the one nobody is running tests
/// against.</para>
///
/// <para><b>Rules here, composition in the host.</b> This type validates and normalises; it does not
/// decide what a Modbus client is registered under. That is why the inverter device id is
/// a parameter rather than a constant read from <c>Gleanvolt.Infrastructure</c> — Core depends on
/// nothing, and it is the dependency-free-ness that lets a cloud portal link it without dragging
/// Modbus, MQTT and a Blazor UI into a container that will never speak to a device.</para>
///
/// <para><b>Every problem, not the first.</b> Problems are collected into the caller's list rather than
/// thrown, so that a caller reporting several sections at once reports them together. Fixing
/// configuration one restart per mistake is a miserable way to spend an evening.</para>
/// </summary>
public static partial class PvSystemRules
{
    /// <summary>The id a charger takes when none was configured.</summary>
    public const string DefaultChargerId = "charger";

    /// <summary>How many chargers the control logic can actually drive. See <see cref="PvSystemOptions.Chargers"/>.</summary>
    public const int SupportedChargerCount = 1;

    [GeneratedRegex("^[a-z0-9][a-z0-9_-]{0,47}$")]
    private static partial Regex Slug();

    /// <summary>
    /// Validates a described system and normalises it into the shape everything downstream reads.
    /// </summary>
    /// <param name="pv">The section as bound. Never null; an empty instance is a describable state.</param>
    /// <param name="inverterDeviceId">
    /// The id to give the resolved inverter — the key its Modbus client is registered under in a host
    /// that has one. A caller that only wants the rules can pass anything; see <see cref="Validate"/>.
    /// </param>
    /// <param name="problems">
    /// Collects every problem found, each naming the configuration key it is about. The caller decides
    /// what a non-empty list means: a startup failure in the controller, a rejected save in the portal.
    /// </param>
    /// <returns>
    /// The resolved system. When <paramref name="problems"/> is non-empty this is a partially-filled
    /// placeholder — it exists so that resolution can continue and report everything, not to be used.
    /// </returns>
    public static PvSystemInfo Resolve(PvSystemOptions pv, string inverterDeviceId, List<string> problems)
    {
        ArgumentNullException.ThrowIfNull(pv);
        ArgumentNullException.ThrowIfNull(problems);

        var id = ResolveId(pv, problems);
        var (latitude, longitude) = ResolveLocation(pv, problems);

        return new PvSystemInfo(
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
            Inverter: ResolveInverter(pv, inverterDeviceId, problems),
            Chargers: ResolveChargers(pv, problems))
        {
            UnmeteredGridPhase = ResolveUnmeteredGridPhase(pv, problems),
        };
    }

    // Empty is the ordinary installation. Both spellings are accepted because the one on the inverter's
    // display and in its register names is SolaX's R/S/T, and the one on an electrician's drawing is L1-L3.
    private static Enums.GridPhase? ResolveUnmeteredGridPhase(PvSystemOptions pv, List<string> problems)
    {
        var value = pv.Inverter?.UnmeteredGridPhase.Trim() ?? string.Empty;

        Enums.GridPhase? phase = value.ToUpperInvariant() switch
        {
            "L1" or "R" => Enums.GridPhase.L1,
            "L2" or "S" => Enums.GridPhase.L2,
            "L3" or "T" => Enums.GridPhase.L3,
            _ => null,
        };

        if (phase is null && value.Length > 0)
        {
            problems.Add(
                $"Pv:Inverter:UnmeteredGridPhase ('{value}') must be L1, L2 or L3 (or SolaX's R, S or T), or "
                + "empty for a grid meter that measures all three phases.");
        }

        return phase;
    }

    /// <summary>
    /// The rules on their own, for a caller that wants to know whether a system is describable rather
    /// than what it resolves to — the portal validating a form before it writes a row.
    /// </summary>
    /// <returns>Every problem found, each naming the configuration key it is about. Empty means usable.</returns>
    public static IReadOnlyList<string> Validate(PvSystemOptions pv)
    {
        var problems = new List<string>();
        Resolve(pv, DefaultInverterDeviceId, problems);
        return problems;
    }

    // Only ever handed to the throwaway Resolve in Validate, whose PvSystemInfo is discarded. The host
    // passes its own; this exists so Validate does not ask its caller for an id it will not look at.
    private const string DefaultInverterDeviceId = "Inverter";

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

    private static PvDeviceInfo ResolveInverter(PvSystemOptions pv, string inverterDeviceId, List<string> problems)
    {
        var name = pv.Inverter?.Name.Trim() ?? string.Empty;
        var model = pv.Inverter?.Model.Trim() ?? string.Empty;

        if (string.IsNullOrWhiteSpace(pv.Inverter?.Host))
        {
            problems.Add("No inverter is configured. Set Pv:Inverter:Host.");

            // A placeholder so the rest of the resolution can be reported too; the throw the caller makes
            // is unconditional once anything has landed in `problems`.
            return new PvDeviceInfo(inverterDeviceId, name, model, new DeviceConfig { Host = string.Empty });
        }

        ValidateConnection(pv.Inverter, "Pv:Inverter", problems);

        return new PvDeviceInfo(inverterDeviceId, name, model, pv.Inverter.ToDeviceConfig());
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
