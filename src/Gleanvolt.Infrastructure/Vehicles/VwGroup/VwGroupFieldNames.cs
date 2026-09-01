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
/// <para><b>Provisional until a real bundle says otherwise.</b> These lists were written from what
/// #137 and #139 record of the portal's shape plus the one value this codebase has already seen
/// spelled out (<c>CHARGE_STATE_CHARGING_HV_BATTERY</c>, in <see cref="VehicleChargeState"/>'s own
/// documentation). A name that is wrong costs nothing but the field it names — the mapper reports
/// what it did not recognise, and the harness prints it — so correcting them is an edit here rather
/// than a redesign anywhere.</para>
/// </summary>
public static class VwGroupFieldNames
{
    /// <summary>State of charge, as a percentage.</summary>
    public static readonly string[] StateOfCharge =
    [
        "stateOfChargeInPercent", "currentSocPercentage", "stateOfCharge", "socPercentage", "soc",
    ];

    /// <summary>The car's own range estimate, in kilometres.</summary>
    public static readonly string[] RangeKm =
    [
        "cruisingRangeElectricInMeters", "electricRange", "remainingRangeElectricKm", "rangeInKm", "range",
    ];

    /// <summary>
    /// Range fields whose unit is metres rather than kilometres. The portal names the unit in the
    /// field, which is the only reason this can be done reliably rather than guessed from magnitude.
    /// </summary>
    public static readonly string[] RangeInMetres = ["cruisingRangeElectricInMeters"];

    /// <summary>How much longer the car reckons it needs, in minutes.</summary>
    public static readonly string[] ChargeTimeRemainingMinutes =
    [
        "remainingChargingTimeToCompleteInMinutes", "remainingChargingTimeInMinutes",
        "remainingChargingTime", "chargingTimeRemaining",
    ];

    /// <summary>What the car says it is doing.</summary>
    public static readonly string[] ChargeState = ["chargingState", "chargeState", "chargingStatus"];

    /// <summary>Whether a cable is in the car's socket.</summary>
    public static readonly string[] PlugState = ["plugConnectionState", "plugConnectionStatus", "plugState"];

    /// <summary>
    /// The odometer. Not part of <see cref="Core.Models.VehicleState"/> — it is here because it is the
    /// monotonic field the tie-break rule exists for, and #139 names it explicitly.
    /// </summary>
    public static readonly string[] Odometer = ["mileageInKm", "odometerInKm", "mileage", "odometer"];

    /// <summary>
    /// The car's own charging target. Nothing reads it yet: #101 deferred the impossible-target gate
    /// because no feed carried this, and #137's point is that this one does. Recognised so that the
    /// harness can answer "does it actually arrive?" without a code change.
    /// </summary>
    public static readonly string[] TargetSoc =
    [
        "target_soc", "targetSoc", "targetStateOfChargeInPercent", "remaining_charging_time_target_soc",
    ];

    /// <summary>
    /// Fields the mapper knows about but does not put on a <c>VehicleState</c>. Listed so that
    /// "unmapped" means "nobody has looked at this" rather than "we chose not to".
    /// </summary>
    public static readonly string[][] KnownButUnused = [Odometer, TargetSoc];

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
