using System.Globalization;
using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles;

/// <summary>
/// The JSON contract for vehicle telemetry on MQTT, and its parser.
///
/// <para>Deliberately the <b>only</b> shape this codebase understands. Per-car adaptation — mapping
/// each integration's enum spellings and entity names onto these fields — happens in Home Assistant,
/// which is what makes a second vehicle a YAML change rather than a release. See the README.</para>
///
/// <code>
/// {
///   "captured_at":  "2026-08-17T10:44:23+00:00",   // required: the CAR's capture time
///   "soc_percent":  28,                            // optional, 0-100
///   "range_km":     176,                           // optional: the CAR's own range estimate
///   "charge_time_remaining_minutes": 95,           // optional: the CAR's own estimate
///   "charge_state": "charging",                    // optional: idle | charging | complete | unknown
///   "plug_state":   "connected",                   // optional: connected | disconnected | unknown
///   "source":       "id4"                          // optional, for display
/// }
/// </code>
///
/// <para>Pure and static: no MQTT, no I/O, no options. The transport lives in
/// <c>Gleanvolt.Hosting</c>, where the MQTT client already is.</para>
/// </summary>
public static class VehicleTelemetryPayload
{
    public const string CapturedAtProperty = "captured_at";
    public const string SocPercentProperty = "soc_percent";
    public const string RangeKmProperty = "range_km";
    public const string ChargeTimeRemainingProperty = "charge_time_remaining_minutes";
    public const string ChargeStateProperty = "charge_state";
    public const string PlugStateProperty = "plug_state";
    public const string SourceProperty = "source";

    /// <summary>
    /// Parses a received payload into a <see cref="VehicleState"/>.
    ///
    /// <para>The rule for every optional field is: <b>absent is fine, present-but-unusable is
    /// not</b>. A source that simply doesn't report SOC is a supported configuration; a source that
    /// reports <c>"soc_percent": "unavailable"</c> means the Home Assistant template is publishing
    /// junk, and half-trusting that payload is worse than dropping it — the holder then keeps the last
    /// good reading and its age visibly grows, which is a diagnosable state.</para>
    ///
    /// <para>An unrecognised <i>enum</i> value is the one exception: it maps to <c>Unknown</c> rather
    /// than rejecting, because these vocabularies are open-ended by nature and a car reporting a state
    /// we haven't seen should not cost us its SOC.</para>
    /// </summary>
    /// <param name="payload">The raw MQTT payload.</param>
    /// <param name="state">The parsed state, or null when this returns false.</param>
    /// <param name="error">Why it was rejected, for logging, or null on success.</param>
    public static bool TryParse(string? payload, out VehicleState? state, out string? error)
    {
        state = null;

        if (string.IsNullOrWhiteSpace(payload))
        {
            error = "payload was empty";
            return false;
        }

        JsonElement root;
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(payload);
        }
        catch (JsonException ex)
        {
            error = $"payload was not valid JSON ({ex.Message})";
            return false;
        }

        using (document)
        {
            root = document.RootElement;

            if (root.ValueKind != JsonValueKind.Object)
            {
                error = $"payload was a JSON {root.ValueKind}, expected an object";
                return false;
            }

            if (!TryReadCapturedAt(root, out var capturedAt, out error))
            {
                return false;
            }

            if (!TryReadSocPercent(root, out var soc, out error))
            {
                return false;
            }

            if (!TryReadRangeKm(root, out var rangeKm, out error))
            {
                return false;
            }

            if (!TryReadChargeTimeRemaining(root, out var remaining, out error))
            {
                return false;
            }

            state = new VehicleState(
                capturedAt,
                soc,
                rangeKm,
                remaining,
                ReadEnum(root, ChargeStateProperty, VehicleChargeState.Unknown),
                ReadEnum(root, PlugStateProperty, VehiclePlugState.Unknown),
                ReadString(root, SourceProperty));

            error = null;
            return true;
        }
    }

    // Required. Accepts a JSON string in any format DateTimeOffset understands (ISO 8601 with an
    // offset is what the documented automation emits). A number is rejected rather than guessed at:
    // it would be ambiguous between a unix epoch and a mistake, and getting this wrong silently
    // mis-ages every reading.
    private static bool TryReadCapturedAt(JsonElement root, out DateTimeOffset capturedAt, out string? error)
    {
        capturedAt = default;

        if (!root.TryGetProperty(CapturedAtProperty, out var property))
        {
            error = $"'{CapturedAtProperty}' was missing, and staleness cannot be judged without it";
            return false;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            error = $"'{CapturedAtProperty}' was a JSON {property.ValueKind}, expected a timestamp string";
            return false;
        }

        var text = property.GetString();

        if (!DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out capturedAt))
        {
            error = $"'{CapturedAtProperty}' was not a parseable timestamp ('{text}')";
            return false;
        }

        error = null;
        return true;
    }

    // Optional, but a percentage outside 0-100 is a broken publisher rather than an unusual car.
    private static bool TryReadSocPercent(JsonElement root, out double? soc, out string? error)
    {
        soc = null;

        if (!root.TryGetProperty(SocPercentProperty, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            error = null;
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value))
        {
            error = $"'{SocPercentProperty}' was a JSON {property.ValueKind}, expected a number";
            return false;
        }

        if (value is < 0 or > 100 || double.IsNaN(value))
        {
            error = $"'{SocPercentProperty}' was {value}, outside 0-100";
            return false;
        }

        soc = value;
        error = null;
        return true;
    }

    // Optional. Zero is a real reading -- a flat car reports it -- so the only rejections are a
    // non-number and a figure no road car could mean. The ceiling is deliberately generous: it exists
    // to catch a template publishing metres or a "unavailable" that slipped through as a number, not
    // to have an opinion about how far anyone's car goes.
    private static bool TryReadRangeKm(JsonElement root, out double? rangeKm, out string? error)
    {
        rangeKm = null;

        if (!root.TryGetProperty(RangeKmProperty, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            error = null;
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var value))
        {
            error = $"'{RangeKmProperty}' was a JSON {property.ValueKind}, expected a number";
            return false;
        }

        if (value < 0 || value > MaxRangeKm || double.IsNaN(value))
        {
            error = $"'{RangeKmProperty}' was {value}, outside 0-{MaxRangeKm} km";
            return false;
        }

        rangeKm = value;
        error = null;
        return true;
    }

    /// <summary>
    /// Sanity ceiling on a reported range: 2000 km, comfortably past anything on sale and short enough
    /// that a metres-for-kilometres mix-up is caught rather than displayed.
    /// </summary>
    private const double MaxRangeKm = 2000;

    // Optional, in whole or fractional minutes. Absent is a supported configuration -- plenty of cars
    // report SOC and nothing else -- but a negative or absurd figure is a broken template, and a car
    // that claims 40 days of charging left would poison any chart drawn from it.
    private static bool TryReadChargeTimeRemaining(JsonElement root, out TimeSpan? remaining, out string? error)
    {
        remaining = null;

        if (!root.TryGetProperty(ChargeTimeRemainingProperty, out var property) || property.ValueKind == JsonValueKind.Null)
        {
            error = null;
            return true;
        }

        if (property.ValueKind != JsonValueKind.Number || !property.TryGetDouble(out var minutes))
        {
            error = $"'{ChargeTimeRemainingProperty}' was a JSON {property.ValueKind}, expected a number of minutes";
            return false;
        }

        if (minutes < 0 || minutes > MaxChargeTimeRemainingMinutes || double.IsNaN(minutes))
        {
            error = $"'{ChargeTimeRemainingProperty}' was {minutes}, outside 0-{MaxChargeTimeRemainingMinutes} minutes";
            return false;
        }

        remaining = TimeSpan.FromMinutes(minutes);
        error = null;
        return true;
    }

    /// <summary>
    /// Sanity ceiling on a reported remaining time: a week. Even a 2 kW granny lead on the largest
    /// domestic pack is well inside that, so anything beyond it is a unit mix-up (seconds published as
    /// minutes) rather than a patient owner.
    /// </summary>
    private const double MaxChargeTimeRemainingMinutes = 7 * 24 * 60;

    private static TEnum ReadEnum<TEnum>(JsonElement root, string property, TEnum fallback)
        where TEnum : struct, Enum =>
        root.TryGetProperty(property, out var element)
        && element.ValueKind == JsonValueKind.String
        && Enum.TryParse<TEnum>(element.GetString(), ignoreCase: true, out var parsed)
            ? parsed
            : fallback;

    private static string? ReadString(JsonElement root, string property) =>
        root.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;
}
