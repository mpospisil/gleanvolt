using Gleanvolt.Core.Enums;

namespace Gleanvolt.Infrastructure.Vehicles.VwGroup;

/// <summary>
/// The portal's vocabulary: which of its field names mean which part of a
/// <see cref="Core.Models.VehicleState"/>, and which of its words mean which enum member.
///
/// <para><b>This file is the vendor's dialect, and it is data rather than logic.</b> Keeping it apart
/// from the mapper is what makes a second manufacturer cheap: the next portal reuses the tie-break
/// rules, the sentinel filtering and the shape, and shares none of the strings below.</para>
///
/// <para><b>Candidates are matched on the last dotted segment as well as the whole name</b>, which is
/// how one list serves both layouts: the ID.x / MEB export says
/// <c>battery.stateOfChargeInPercent</c> and an older PHEV export says
/// <c>stateOfChargeInPercent</c>, and neither needs its own table.</para>
///
/// <para><b>Taken from VW's own Data Dictionary V5.0 (Continuous Data)</b>, by way of the
/// transcription evcc keeps at <c>vehicle/vw/eudataact/datadictionary.json</c> — 1,142 documented
/// fields. The names below are the portal's, not guesses.</para>
///
/// <para>That distinction matters because the first draft of this file <b>was</b> guesses, and they
/// were wrong in a way no amount of care would have caught: they were camelCase
/// (<c>stateOfChargeInPercent</c>) and the portal's vocabulary is snake_case (<c>hv_soc</c>). Nothing
/// would have matched, and every reading would have come back empty with the mapper politely listing
/// what it did not recognise. The camelCase spellings are kept below as trailing fallbacks — they
/// cost nothing and some exports may yet use them — but the snake_case names lead.</para>
///
/// <para>The dictionary also settles the one value this codebase had already seen spelled out:
/// <c>CHARGE_STATE_CHARGING_HV_BATTERY</c> belongs to
/// <c>charging_state_report.current_charge_state</c>.</para>
/// </summary>
public static class VwGroupFieldNames
{
    /// <summary>State of charge, as a percentage.</summary>
    public static readonly string[] StateOfCharge =
    [
        // Both confirmed present in a real ID.4 bundle, agreeing at 57: battery_level_HV.value comes
        // with its own .state ("VALID"), battery_state_report.soc is the integer. hv_soc is documented
        // but this car does not send it.
        "battery_level_HV.value", "battery_state_report.soc", "hv_soc", "battery_charging_status_soc",
        "stateOfChargeInPercent", "currentSocPercentage", "stateOfCharge", "socPercentage", "soc",
    ];

    /// <summary>The car's own range estimate, in kilometres.</summary>
    public static readonly string[] RangeKm =
    [
        // Documented in kilometres, and "combined" first: on a BEV it is the only engine.
        "cruising_range_combined", "cruising_range_primary_engine", "estimatedcruisingrangeprimary.value",
        "cruisingRangeElectricInMeters", "electricRange", "remainingRangeElectricKm", "rangeInKm", "range",
    ];

    /// <summary>
    /// Range fields whose unit is metres rather than kilometres. The portal names the unit in the
    /// field, which is the only reason this can be done reliably rather than guessed from magnitude.
    /// </summary>
    public static readonly string[] RangeInMetres = ["cruisingRangeElectricInMeters"];

    /// <summary>How much longer the car reckons it needs. The value names its own unit: a real
/// bundle carries "9900s" while the dictionary documents remaining_charging_time in minutes.</summary>
    public static readonly string[] ChargeTimeRemaining =
    [
        // "Charging time left, min" -- the plain one is already in minutes.
        "remaining_charging_time", "battery_state_report.remaining_charging_time_complete",
        "remainingChargingTimeToCompleteInMinutes", "remainingChargingTimeInMinutes",
        "remainingChargingTime", "chargingTimeRemaining",
    ];

    /// <summary>What the car says it is doing.</summary>
    public static readonly string[] ChargeState =
    [
        // current_charge_state is where CHARGE_STATE_* lives; charging_state is the coarser sibling
        // ("invalid, unsupported, off, ...").
        "charging_state_report.current_charge_state", "charging_state",
        "chargingState", "chargeState", "chargingStatus",
    ];

    /// <summary>Whether a cable is in the car's socket.</summary>
    public static readonly string[] PlugState =
    [
        // plug1 is the only socket on a single-inlet car; plug_state is the summary field.
        "plug_state", "plug_connection_state", "charging_plug1_connectionstate",
        "plugConnectionState", "plugConnectionStatus", "plugState",
    ];

    /// <summary>
    /// The odometer. Not part of <see cref="Core.Models.VehicleState"/> — it is here because it is the
    /// monotonic field the tie-break rule exists for, and #139 names it explicitly.
    /// </summary>
    public static readonly string[] Odometer =
    [
        // mileage.value in the real bundle (with mileage.state alongside), not a bare "mileage".
        "mileage.value", "mileage", "long_trip.overall_mileage",
        "mileageInKm", "odometerInKm", "odometer",
    ];

    /// <summary>
    /// The car's own charging target. Nothing reads it yet: #101 deferred the impossible-target gate
    /// because no feed carried this, and #137's point is that this one does. Recognised so that the
    /// harness can answer "does it actually arrive?" without a code change.
    /// </summary>
    public static readonly string[] TargetSoc =
    [
        // settings.target_soc is the one #101 wanted: "possible values 10-100; the charging will be
        // completed in the defined SOC". active_target_soc is what is in force right now.
        "settings.target_soc", "active_target_soc",
        "target_soc", "targetSoc", "targetStateOfChargeInPercent", "remaining_charging_time_target_soc",
    ];

    /// <summary>
    /// The HV battery's energy content, as the portal reports it — what is in the pack now, and what
    /// the pack holds full.
    ///
    /// <para><b>Recognised so it can be seen, not yet read.</b> The reference ID.4's delivery carries
    /// no state of charge at all, and these two are the only battery figures in it: a percentage
    /// follows from the pair by division, and nothing else in the bundle offers one. Whether to make
    /// that division is a decision about what <c>SocPercent</c> means — the car's own number against
    /// one we computed — and it wants the values in front of somebody first, which is exactly what
    /// listing them here achieves.</para>
    /// </summary>
    public static readonly string[] EnergyContentKWh =
    [
        "energy_contents.current_energy_content.physical_value",
        "energy_contents.maximal_energy_content.physical_value",
        "current_energy_content", "maximal_energy_content",
    ];

    /// <summary>
    /// Fields the mapper knows about but does not put on a <c>VehicleState</c>. Listed so that
    /// "unmapped" means "nobody has looked at this" rather than "we chose not to".
    /// </summary>
    public static readonly string[][] KnownButUnused = [Odometer, TargetSoc, EnergyContentKWh];

    /// <summary>
    /// Values that mean "no reading", whatever field they appear in.
    ///
    /// <para>Sentinel filtering is the first of #139's tie-breaks and the one that matters most: a
    /// bundle carries several snapshots precisely because some of them are blank, and a blank that
    /// reaches the mapper as a value would beat a real reading taken an hour earlier simply by being
    /// later. Numeric sentinels are not listed here — a <c>-1</c> is excluded by each field's own
    /// range instead, which is the rule #73 already established.</para>
    /// </summary>
    public static readonly string[] Sentinels =
    [
        "", "null", "n/a", "na", "-", "--", "unknown", "unavailable", "undefined", "invalid", "none",
    ];

    /// <summary>
    /// The portal's charge-state words. <c>CHARGE_STATE_CHARGING_HV_BATTERY</c> is the one this
    /// codebase has already recorded seeing; the rest follow its shape.
    ///
    /// <para>Anything not here maps to <see cref="VehicleChargeState.Unknown"/> rather than rejecting
    /// the snapshot — #73's single exception, because these vocabularies are open-ended and an
    /// unfamiliar state must not cost us the SOC.</para>
    /// </summary>
    public static readonly (string Value, VehicleChargeState State)[] ChargeStates =
    [
        ("CHARGE_STATE_CHARGING_HV_BATTERY", VehicleChargeState.Charging),
        ("CHARGE_STATE_CHARGING", VehicleChargeState.Charging),
        ("CHARGING", VehicleChargeState.Charging),
        ("CHARGE_STATE_CONSERVATION", VehicleChargeState.Complete),
        ("CHARGE_STATE_COMPLETED", VehicleChargeState.Complete),
        ("CHARGE_STATE_READY_FOR_CHARGING", VehicleChargeState.Idle),
        ("CHARGE_STATE_NOT_READY_FOR_CHARGING", VehicleChargeState.Idle),
        ("CHARGE_STATE_OFF", VehicleChargeState.Idle),
        ("COMPLETED", VehicleChargeState.Complete),
        ("READYFORCHARGING", VehicleChargeState.Idle),
        ("NOTREADYFORCHARGING", VehicleChargeState.Idle),
        ("OFF", VehicleChargeState.Idle),
        ("IDLE", VehicleChargeState.Idle),
    ];

    /// <summary>The portal's plug words, on the same terms.</summary>
    public static readonly (string Value, VehiclePlugState State)[] PlugStates =
    [
        ("PLUG_CONNECTION_STATE_CONNECTED", VehiclePlugState.Connected),
        ("PLUG_CONNECTION_STATE_DISCONNECTED", VehiclePlugState.Disconnected),
        ("CONNECTED", VehiclePlugState.Connected),
        ("DISCONNECTED", VehiclePlugState.Disconnected),
        ("PLUGGED", VehiclePlugState.Connected),
        ("UNPLUGGED", VehiclePlugState.Disconnected),
    ];

    /// <summary>Whether a raw value is one of the portal's ways of saying nothing.</summary>
    public static bool IsSentinel(string? value) =>
        value is null || Sentinels.Contains(value.Trim(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether a portal field name is one of the given candidates, comparing the whole name and its
    /// last dotted segment — which is what lets one list read both the dotted and the flat layout.
    /// </summary>
    public static bool Matches(string fieldName, string[] candidates)
    {
        var leaf = Leaf(fieldName);

        return candidates.Any(candidate =>
            string.Equals(fieldName, candidate, StringComparison.OrdinalIgnoreCase)
            || string.Equals(leaf, candidate, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The last dotted segment of a field name, which is the part the two layouts share.</summary>
    public static string Leaf(string fieldName)
    {
        var cut = fieldName.LastIndexOf('.');
        return cut >= 0 && cut < fieldName.Length - 1 ? fieldName[(cut + 1)..] : fieldName;
    }
}
