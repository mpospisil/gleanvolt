using Gleanvolt.Core.Enums;

namespace Gleanvolt.Api.Contracts;

/// <summary>
/// Start controlled charging in one of the modes.
///
/// <para>One call does what one button does: the charger is put into its Fast use-mode and the mode is
/// then selected, so a press works on a charger sitting in Green rather than waiting for somebody to
/// have set the wallbox by hand. A charger that refuses leaves the mode exactly where it was.</para>
/// </summary>
/// <param name="Mode">
/// Which strategy to run. <c>solar</c> follows live surplus once the home battery is full;
/// <c>forecasted</c> lets today's forecast decide how much of the sun the car may have;
/// <c>fastNoBattery</c> charges flat out from PV and grid with the home battery held out of it, and
/// ends itself when the car is full; <c>targeted</c> delivers a stated amount by a stated time and
/// needs <c>target</c>. <c>off</c> is not a mode to start — use the stop endpoint.
/// </param>
/// <param name="Target">
/// The target, required for <c>targeted</c> and rejected for every other mode. The same shape the
/// preview takes, so what was quoted is what is committed: it is set as the active request before the
/// mode is selected, and dropped again if the charger refuses.
/// </param>
public sealed record StartChargingRequest(ChargeControlMode Mode, TargetedChargeRequestBody? Target = null);

/// <summary>Arm or release the battery discharge hold.</summary>
/// <param name="Hold">
/// True stops the home battery serving house load, so the car charges from PV and grid while the pack
/// still charges from surplus. Orthogonal to the charge mode: either can be on without the other.
/// </param>
public sealed record BatteryHoldRequest(bool Hold);

/// <summary>
/// What an action did, and what the controller looks like afterwards — so a caller never has to poll
/// to find out whether the thing it asked for happened.
/// </summary>
/// <param name="Succeeded">
/// Whether the action was carried out. False is a real answer, not an error: a hardware write can fail,
/// and the mode is then left exactly as it was.
/// </param>
/// <param name="Message">Why it failed, when it did. Null on success.</param>
/// <param name="Target">The targeted request now in force, or null when there is none.</param>
/// <param name="Status">
/// The controller's state after the action. Note that the fields the poll loop owns — powers, the
/// charger's read-back current — are from the <em>last</em> poll and will not reflect this action until
/// the next one completes. Null only when no poll has completed since startup.
/// </param>
public sealed record ControlActionResponse(
    bool Succeeded,
    string? Message,
    TargetedRequestResponse? Target,
    StatusResponse? Status);

/// <summary>
/// Whether the controller is alive and what it can currently see. The endpoint to poll when the
/// question is "is this thing working?" rather than "what is the roof doing?".
/// </summary>
/// <param name="Ok">
/// Whether a poll has completed recently enough to trust everything else being reported. False means
/// the figures on <c>/status</c> are stale or absent — the inverter is unreachable, or the service has
/// only just started.
/// </param>
/// <param name="Version">The running build.</param>
/// <param name="Now">The controller's own clock, so a caller can tell a stale reading from a skewed clock.</param>
/// <param name="TimeZoneId">The zone every local-time decision here is made in.</param>
/// <param name="LastPollAt">When the last poll completed, or null when none has since startup.</param>
/// <param name="LastPollAgeSeconds">How long ago that was.</param>
/// <param name="Mode">The mode currently selected, or null when no poll has completed.</param>
/// <param name="DryRun">Whether charge control is deciding without writing to the charger.</param>
/// <param name="ForecastAvailable">Whether a usable solar forecast is in hand.</param>
/// <param name="ForecastRetrievedAt">When it was fetched.</param>
/// <param name="WeatherConfigured">Whether a weather provider is configured at all.</param>
/// <param name="VehicleAvailable">Whether any vehicle reading has arrived.</param>
/// <param name="VehicleAgeSeconds">How old that reading is.</param>
/// <param name="VehicleStale">Whether it is past the configured maximum age — a dead feed rather than an old number.</param>
/// <param name="EnergyHistoryAvailable">Whether the energy-interval store answered a probe query.</param>
/// <param name="SessionHistoryAvailable">Whether the charging-session store answered a probe query.</param>
public sealed record HealthResponse(
    bool Ok,
    string Version,
    string SystemId,
    string SystemName,
    DateTimeOffset Now,
    string TimeZoneId,
    DateTimeOffset? LastPollAt,
    double? LastPollAgeSeconds,
    ChargeControlMode? Mode,
    bool? DryRun,
    bool ForecastAvailable,
    DateTimeOffset? ForecastRetrievedAt,
    bool WeatherConfigured,
    bool VehicleAvailable,
    double? VehicleAgeSeconds,
    bool VehicleStale,
    bool EnergyHistoryAvailable,
    bool SessionHistoryAvailable);

/// <summary>
/// What this API is, and how to use it — the answer at <c>/api/v1/</c>, which is the first place
/// anybody looks.
///
/// <para>The one endpoint that needs no key. It carries nothing an unauthenticated caller could act
/// on: which operations exist, where the document is, and how to authenticate.</para>
/// </summary>
/// <param name="Name">The product this is the API of.</param>
/// <param name="Version">The API version, which is also the path segment everything sits under.</param>
/// <param name="Build">The running build of the controller.</param>
/// <param name="Documentation">Where the OpenAPI document is served. It needs no key either.</param>
/// <param name="Authentication">How to present a key on every other endpoint.</param>
/// <param name="Operations">Every operation this build serves, path first.</param>
public sealed record ApiIndexResponse(
    string Name,
    string Version,
    string Build,
    string Documentation,
    string Authentication,
    IReadOnlyList<ApiOperationResponse> Operations);

/// <summary>One operation, as the index lists it.</summary>
/// <param name="Method">The HTTP method.</param>
/// <param name="Path">The route.</param>
/// <param name="Summary">What it answers, in a line. The same summary the OpenAPI document carries.</param>
public sealed record ApiOperationResponse(string Method, string Path, string? Summary);
