using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.VwWebsite;

/// <summary>
/// The <c>charging/status</c> payload from volkswagen.de's authproxy, mapped onto
/// <see cref="VehicleState"/> (issue #170).
///
/// <para>Pure: JSON in, a reading out, no HTTP anywhere near it — the discipline
/// <c>VwGroupReportBundle</c> already follows, and the reason that parser stayed debuggable.</para>
///
/// <para><b>Why this source exists beside the EU Data Act portal.</b> One reading, taken on
/// 2026-09-05 from the same car within a minute: this endpoint reported a state of charge captured at
/// 08:39 that morning, while the portal was still serving one captured at 21:16 the evening before.
/// The car had reported; the portal had not yet published it. Measured separately, portal publication
/// runs between 1h48m and 7h16m behind the car. A charging session would be over before its first
/// in-charge reading appeared there, which is the whole reason for this client.</para>
///
/// <para><b>The two are complementary, not redundant.</b> The captured payload has
/// <c>navigationTargetSOC_pct: null</c> while the portal carries <c>settings.target_soc = 80</c> for
/// the same car at the same moment — so neither is a superset, and the portal stays.</para>
/// </summary>
public static class VwWebsiteChargingStatus
{
    /// <summary>
    /// Reads the payload, or returns null with a reason.
    ///
    /// <para>#73's rule, inherited: <b>absent is fine, present-but-unusable is not.</b> A missing
    /// section leaves its fields null; a body that is not the expected shape is refused whole rather
    /// than half-trusted, so the last good reading keeps its place with a visibly growing age.</para>
    /// </summary>
    public static VehicleState? Parse(string? json, string? sourceId, out string? error)
    {
        error = null;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "the response was empty";
            return null;
        }

        JsonElement data;

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("data", out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                error = "the response carried no 'data' object";
                return null;
            }

            data = element.Clone();
        }
        catch (JsonException ex)
        {
            error = $"the response was not JSON ({ex.Message})";
            return null;
        }

        var battery = Section(data, "batteryStatus");
        var charging = Section(data, "chargingStatus");
        var plug = Section(data, "plugStatus");

        // The car's own capture time, and the reason this source is worth having -- so it is required
        // rather than defaulted to "now". Every section carries the same one; the battery's is taken
        // because that is the field the reading is mostly about.
        var capturedAt = Captured(battery) ?? Captured(charging) ?? Captured(plug);

        if (capturedAt is null)
        {
            error = "no section carried a carCapturedTimestamp, so the reading has no age";
            return null;
        }

        return new VehicleState(
            capturedAt.Value,
            SocPercent: Number(battery, "currentSOC_pct"),
            RangeKm: Number(battery, "cruisingRangeElectric_km"),
            ChargeTimeRemaining: Minutes(charging, "remainingChargingTimeToComplete_min"),
            ChargeState: ChargeState(Text(charging, "chargingState")),
            PlugState: PlugState(Text(plug, "plugConnectionState")),
            SourceId: sourceId);
    }

    /// <summary>The car's own charge limit, when it names one. Null unless a charge plan is active.</summary>
    public static double? TargetSocPercent(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return null;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            return document.RootElement.TryGetProperty("data", out var data)
                ? Number(Section(data, "batteryStatus"), "navigationTargetSOC_pct")
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? Section(JsonElement data, string name) =>
        data.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object
            ? section
            : null;

    private static DateTimeOffset? Captured(JsonElement? section) =>
        Text(section, "carCapturedTimestamp") is { } text
        && DateTimeOffset.TryParse(
            text, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;

    private static string? Text(JsonElement? section, string name) =>
        section is { } element
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static double? Number(JsonElement? section, string name) =>
        section is { } element
        && element.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    /// <summary>
    /// A remaining time. Zero is a fact -- the car saying it is done -- and distinct from absent, so
    /// it is kept rather than folded into null.
    /// </summary>
    private static TimeSpan? Minutes(JsonElement? section, string name) =>
        Number(section, name) is { } minutes && minutes >= 0 && minutes <= 7 * 24 * 60
            ? TimeSpan.FromMinutes(minutes)
            : null;

    /// <summary>
    /// The vocabulary is the app API's, not the portal's: <c>notReadyForCharging</c> here where the
    /// Data Act bundle says <c>CHARGE_STATE_NOT_READY_FOR_CHARGING</c>. Unrecognised maps to
    /// <see cref="VehicleChargeState.Unknown"/> rather than costing the whole reading.
    /// </summary>
    private static VehicleChargeState ChargeState(string? value) => value?.ToLowerInvariant() switch
    {
        "charging" => VehicleChargeState.Charging,
        // "Charge purpose reached" is the car saying it is done, which is Complete rather than Idle:
        // #73 draws that distinction and a fast charge's completion dwell reads it.
        "chargepurposereachedandnotconservationcharging" or "chargepurposereachedandconservation"
            => VehicleChargeState.Complete,
        "readyforcharging" or "notreadyforcharging" or "off" or "error" or "conservation"
            => VehicleChargeState.Idle,
        _ => VehicleChargeState.Unknown,
    };

    private static VehiclePlugState PlugState(string? value) => value?.ToLowerInvariant() switch
    {
        "connected" => VehiclePlugState.Connected,
        "disconnected" => VehiclePlugState.Disconnected,
        _ => VehiclePlugState.Unknown,
    };
}
