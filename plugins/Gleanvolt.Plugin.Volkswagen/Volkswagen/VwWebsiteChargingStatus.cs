using System.Globalization;
using System.Text.Json;
using Gleanvolt.Providers.Contracts.Vehicle;

namespace Gleanvolt.Plugin.Volkswagen.Volkswagen;

/// <summary>
/// The <c>charging/status</c> payload from volkswagen.de's authproxy, mapped onto the provider contract's
/// <see cref="VehicleReading"/> (#170, #410).
///
/// <para>Pure: JSON in, a reading out, no HTTP anywhere near it.</para>
///
/// <para>Copied from the built-in, which maps onto Gleanvolt's own <c>VehicleState</c>. Changed: the
/// contract's enums; no source id (Gleanvolt names the reading after the provider); and a remaining
/// charge time over the contract's 48 hours is left out rather than sent, where the built-in kept up to
/// 7 days. Gleanvolt refuses a whole reading over 48 hours, and the built-in's holder never checked.</para>
/// </summary>
public static class VwWebsiteChargingStatus
{
    /// <summary>The longest remaining time a reading may carry: <c>VehicleReadingValidator</c>'s limit.</summary>
    public static readonly TimeSpan MaxChargeTimeRemaining = TimeSpan.FromHours(48);

    /// <summary>
    /// Reads the payload, or returns null with a reason.
    ///
    /// <para>#73's rule, inherited: <b>absent is fine, present-but-unusable is not.</b> A missing
    /// section leaves its fields null; a body that is not the expected shape is refused whole.</para>
    /// </summary>
    public static VehicleReading? Parse(string? json, out string? error)
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

        // The car's own capture time, and the reason this source is worth having: required rather than
        // defaulted to "now". Gleanvolt judges the reading's age by it.
        var capturedAt = Captured(battery) ?? Captured(charging) ?? Captured(plug);

        if (capturedAt is null)
        {
            error = "no section carried a carCapturedTimestamp, so the reading has no age";
            return null;
        }

        return new VehicleReading(
            capturedAt.Value,
            SocPercent: Number(battery, "currentSOC_pct"),
            RangeMetres: Number(battery, "cruisingRangeElectric_km") * 1000,
            ChargeTimeRemaining: Minutes(charging, "remainingChargingTimeToComplete_min"),
            ChargeState: ChargeState(Text(charging, "chargingState")),
            PlugState: PlugState(Text(plug, "plugConnectionState")));
    }

    private static JsonElement? Section(JsonElement data, string name) =>
        data.TryGetProperty(name, out var section) && section.ValueKind == JsonValueKind.Object
            ? section
            : null;

    private static DateTimeOffset? Captured(JsonElement? section) =>
        Text(section, "carCapturedTimestamp") is { } text
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)
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
    /// A remaining time. Zero is a fact (the car saying it is done) and distinct from absent, so it is
    /// kept rather than folded into null.
    /// </summary>
    private static TimeSpan? Minutes(JsonElement? section, string name) =>
        Number(section, name) is { } minutes && minutes >= 0 && TimeSpan.FromMinutes(minutes) <= MaxChargeTimeRemaining
            ? TimeSpan.FromMinutes(minutes)
            : null;

    /// <summary>
    /// The app API's vocabulary. Unrecognised maps to <see cref="CarChargeState.Unknown"/> rather than
    /// costing the whole reading.
    /// </summary>
    internal static CarChargeState ChargeState(string? value) => value?.ToLowerInvariant() switch
    {
        "charging" => CarChargeState.Charging,
        // "Charge purpose reached" is the car saying it is done: Complete rather than Idle.
        "chargepurposereachedandnotconservationcharging" or "chargepurposereachedandconservation"
            => CarChargeState.Complete,
        "readyforcharging" or "notreadyforcharging" or "off" or "error" or "conservation"
            => CarChargeState.Idle,
        _ => CarChargeState.Unknown,
    };

    internal static CarPlugState PlugState(string? value) => value?.ToLowerInvariant() switch
    {
        "connected" => CarPlugState.Connected,
        "disconnected" => CarPlugState.Disconnected,
        _ => CarPlugState.Unknown,
    };
}
