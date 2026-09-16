using System.Globalization;
using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Infrastructure.Vehicles.Skoda;

/// <summary>
/// The MyŠkoda Public API's <c>VehicleResponse</c>, mapped onto <see cref="VehicleState"/> (issue #193).
///
/// <para>Pure: JSON in, a reading out, no HTTP anywhere near it — the discipline
/// <c>VwWebsiteChargingStatus</c> follows.</para>
///
/// <para><b>Built from the published spec, not yet from a car.</b> Every field read here has a type and
/// an example in <c>/v3/api-docs</c>; the <c>state</c> vocabulary is the list the spec's description
/// gives, and what <c>CONNECT_CABLE</c> and <c>CONSERVING</c> mean at a real charger is one of the
/// questions only a Škoda owner can settle.</para>
/// </summary>
public static class SkodaVehicleResponse
{
    /// <summary>
    /// Sanity ceiling on a reported range: the same 2000 km <c>VehicleTelemetryPayload</c> uses, so a
    /// metres-for-kilometres slip is caught rather than displayed.
    /// </summary>
    private const double MaxRangeKm = 2000;

    /// <summary>
    /// Reads <c>vehicle.charging</c>, or returns null with a reason.
    ///
    /// <para>#73's rule: <b>absent is fine, present-but-unusable is not.</b> A missing field leaves its
    /// value null; a percentage outside 0–100 refuses the reading whole. A missing <c>charging</c> part
    /// is a refusal too, with the <c>errors</c> entry that explains it — the API reports partial data
    /// as a 200, and a reading with no battery and no age is not one.</para>
    /// </summary>
    public static VehicleState? Parse(string? json, string? sourceId, out string? error)
    {
        error = null;

        if (!TryVehicle(json, out var root, out var vehicle, out error))
        {
            return null;
        }

        if (Section(vehicle, "charging") is not { } charging)
        {
            var reasons = Errors(root).Where(type => type.StartsWith("CHARGING", StringComparison.Ordinal)).ToList();
            error = reasons.Count > 0
                ? $"it sent no charging data ({string.Join(", ", reasons)})"
                : "it sent no charging data";
            return null;
        }

        // The car's own capture time, and the reason a reading can be aged at all -- required rather
        // than defaulted to "now", which would make a three-hour-old number look fresh.
        if (Text(charging, "carCapturedTimestamp") is not { } capturedText
            || !DateTimeOffset.TryParse(
                capturedText, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var capturedAt))
        {
            error = "the charging data carried no carCapturedTimestamp, so the reading has no age";
            return null;
        }

        var status = Section(charging, "status");
        var battery = Section(status, "battery");

        var soc = Number(battery, "stateOfChargeInPercent");

        if (soc is < 0 or > 100)
        {
            error = $"stateOfChargeInPercent was {soc}, outside 0-100";
            return null;
        }

        var rangeKm = Number(battery, "remainingCruisingRangeInMeters") / 1000;

        if (rangeKm is < 0 or > MaxRangeKm)
        {
            error = $"remainingCruisingRangeInMeters was {rangeKm * 1000}, outside 0-{MaxRangeKm} km";
            return null;
        }

        var state = Text(status, "state");

        return new VehicleState(
            capturedAt,
            SocPercent: soc,
            RangeKm: rangeKm,
            ChargeTimeRemaining: Minutes(status, "remainingTimeToFullyChargedInMinutes"),
            ChargeState: ChargeState(state),
            PlugState: PlugState(state),
            SourceId: sourceId);
    }

    /// <summary>The car's name as the owner set it in the app, or its model when they did not.</summary>
    public static string? Name(string? json) =>
        TryVehicle(json, out _, out var vehicle, out _) ? Text(vehicle, "name") : null;

    /// <summary>
    /// <c>charging.status.state</c> onto <see cref="VehicleChargeState"/>. The spec says new values may
    /// appear and clients must tolerate them, so anything unrecognised is <c>Unknown</c> rather than a
    /// refused reading — the rule <c>VehicleTelemetryPayload.ReadEnum</c> already follows.
    /// </summary>
    public static VehicleChargeState ChargeState(string? state) => state?.ToUpperInvariant() switch
    {
        "CHARGING" => VehicleChargeState.Charging,
        // The car holding its target: done, which #73 distinguishes from idle.
        "CONSERVING" => VehicleChargeState.Complete,
        "READY_FOR_CHARGING" or "CHARGING_INTERRUPTED" or "CONNECT_CABLE" => VehicleChargeState.Idle,
        _ => VehicleChargeState.Unknown,
    };

    /// <summary>
    /// The plug, derived: the API has no plug field, but <c>CONNECT_CABLE</c> is the car asking for one,
    /// and every charging-side state implies a cable. <c>DISCHARGING</c> says nothing reliable either
    /// way, so it stays unknown.
    /// </summary>
    public static VehiclePlugState PlugState(string? state) => state?.ToUpperInvariant() switch
    {
        "CONNECT_CABLE" => VehiclePlugState.Disconnected,
        "CHARGING" or "CONSERVING" or "READY_FOR_CHARGING" or "CHARGING_INTERRUPTED" => VehiclePlugState.Connected,
        _ => VehiclePlugState.Unknown,
    };

    private static bool TryVehicle(string? json, out JsonElement root, out JsonElement vehicle, out string? error)
    {
        root = default;
        vehicle = default;

        if (string.IsNullOrWhiteSpace(json))
        {
            error = "the response was empty";
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(json);

            if (document.RootElement.ValueKind != JsonValueKind.Object
                || !document.RootElement.TryGetProperty("vehicle", out var element)
                || element.ValueKind != JsonValueKind.Object)
            {
                error = "the response carried no 'vehicle' object";
                return false;
            }

            root = document.RootElement.Clone();
            vehicle = element.Clone();
            error = null;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"the response was not JSON ({ex.Message})";
            return false;
        }
    }

    private static IEnumerable<string> Errors(JsonElement root) =>
        root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array
            ? errors.EnumerateArray()
                .Select(error => Text(error, "type"))
                .OfType<string>()
                .ToList()
            : [];

    private static JsonElement? Section(JsonElement? parent, string name) =>
        parent is { } element
        && element.TryGetProperty(name, out var section)
        && section.ValueKind == JsonValueKind.Object
            ? section
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

    /// <summary>A remaining time. Zero is the car saying it is done, and distinct from absent.</summary>
    private static TimeSpan? Minutes(JsonElement? section, string name) =>
        Number(section, name) is { } minutes && minutes >= 0 && minutes <= 7 * 24 * 60
            ? TimeSpan.FromMinutes(minutes)
            : null;
}
