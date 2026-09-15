namespace Gleanvolt.Core.Enums;

public static class ChargingSessionEndReasonExtensions
{
    /// <summary>
    /// What ended the session, as a lower-case clause that reads inside a sentence: "Charge control mode
    /// set to Off by SolarGrid (no sun was left to charge on today)". One wording, shared by the log, the
    /// session's end event and the session page, so the three never disagree about what happened.
    /// </summary>
    public static string Describe(this ChargingSessionEndReason reason) => reason switch
    {
        ChargingSessionEndReason.ModeOff => "charge control returned to Off",
        ChargingSessionEndReason.SessionComplete => "the car finished charging",
        ChargingSessionEndReason.CarUnplugged => "the car was unplugged",
        ChargingSessionEndReason.ServiceStopped => "the service stopped",
        ChargingSessionEndReason.Interrupted => "the session was interrupted",
        ChargingSessionEndReason.TargetReached => "the energy asked for was delivered",
        ChargingSessionEndReason.DeparturePassed => "the departure time passed",
        ChargingSessionEndReason.NoSunLeftToday => "no sun was left to charge on today",
        ChargingSessionEndReason.ChargerTakenOver => "the charger was switched out of Fast",
        _ => throw new ArgumentOutOfRangeException(nameof(reason), reason, "No description for this end reason."),
    };

    /// <summary>The same clause as <see cref="Describe"/>, capitalised to start a sentence.</summary>
    public static string DescribeSentence(this ChargingSessionEndReason reason)
    {
        var clause = reason.Describe();
        return string.Concat(char.ToUpperInvariant(clause[0]).ToString(), clause.AsSpan(1));
    }
}
