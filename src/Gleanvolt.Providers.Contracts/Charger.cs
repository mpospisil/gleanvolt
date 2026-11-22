namespace Gleanvolt.Providers.Contracts.Charger;

/// <summary>The charger kind: its contract version, and where its hub is (#435).</summary>
public static class ChargerContract
{
    /// <summary>The kind's id.</summary>
    public const string Kind = "charger";

    /// <summary>Changes only when an older provider would be misread.</summary>
    public const int Major = 1;

    /// <summary>Grows with every addition an older provider can ignore.</summary>
    public const int Minor = 0;

    /// <summary>The hub's path on Gleanvolt's web port.</summary>
    public const string HubPath = "/providers/charger";
}

/// <summary>
/// The charger hub (#435): a provider <b>is</b> the EV charger's driver. It reads the charger when asked
/// and carries out the two commands the control loop has: set the charging current, and allow or stop
/// charging. Unlike every other kind, this one writes to a device, so Gleanvolt keeps the safeguards on its
/// own side: it sends a setpoint only when it moved by the configured threshold, clamps it to the charger's
/// range, and in dry-run sends no command at all.
/// </summary>
public interface IChargerHub : IProviderHub<ChargerWelcome>
{
    /// <summary>
    /// Offers a reading of the charger. It is held when it passes every check; on any problem nothing
    /// changes and the problems come back. Answer every <see cref="IChargerClient.RefreshRequested"/> with
    /// one, and publish unprompted whenever the charger's state changes.
    /// </summary>
    Task<PublishResult> Publish(ChargerReading reading);

    /// <summary>
    /// Says how a command ended: <see cref="IChargerClient.SetCurrent"/> or
    /// <see cref="IChargerClient.SetChargingAllowed"/>, quoting its id. Gleanvolt waits for it.
    /// </summary>
    Task CommandDone(ChargerCommandResult result);
}

/// <summary>What the charger hub may send its provider.</summary>
public interface IChargerClient : IProviderClient
{
    /// <summary>
    /// Gleanvolt wants the charger's state now: every poll of the control loop, every few seconds while it
    /// runs. Answer with <see cref="IChargerHub.Publish"/>, quoting <see cref="RefreshRequest.Id"/> in
    /// <see cref="ChargerReading.InReplyTo"/>, within 5 seconds. A reading that is not answered in time is a
    /// charger Gleanvolt could not read, which it reports as unknown, never as a car that is gone.
    /// </summary>
    Task RefreshRequested(RefreshRequest request);

    /// <summary>
    /// Set the charging current. <see cref="ChargerCurrentCommand.Amps"/> below the charger's own minimum
    /// (0, in practice) <b>pauses</b> charging without ending the session: a charger that takes nothing below
    /// 6 A pauses some other way (its start/stop switch), and remembers that it paused, so the next setpoint
    /// of 6 A or more resumes. Answer with <see cref="IChargerHub.CommandDone"/> within 10 seconds.
    /// </summary>
    Task SetCurrent(ChargerCurrentCommand command);

    /// <summary>
    /// Let the charger charge, or stop it. Unconditional: "stop" stops whatever the charger was doing. Never
    /// touches the current setpoint. A start while paused stays paused until a setpoint resumes it. Answer
    /// with <see cref="IChargerHub.CommandDone"/> within 10 seconds.
    /// </summary>
    Task SetChargingAllowed(ChargerAllowedCommand command);
}

/// <summary>
/// An accepted charger hello: the limits Gleanvolt's charge control works in, so the provider can check the
/// charger fits them and tell the owner when it does not.
/// </summary>
public sealed record ChargerWelcome : Welcome
{
    /// <summary>The charge control's limits as configured.</summary>
    public ChargerLimits Limits { get; init; } = new(6, 16, 3, 230);

    /// <summary>
    /// True when Gleanvolt's charge control is in dry-run: it reads, and sends no command. For the provider's
    /// page and log only; nothing for it to enforce.
    /// </summary>
    public bool DryRun { get; init; }

    /// <summary>The installation's IANA time zone, e.g. "Europe/Prague".</summary>
    public string TimeZone { get; init; } = string.Empty;
}

/// <summary>What Gleanvolt's charge control is configured to do (<c>ChargeControl</c>).</summary>
/// <param name="MinCurrentAmps">The lowest current it charges at; below it, it pauses.</param>
/// <param name="MaxCurrentAmps">The highest current it sets.</param>
/// <param name="Phases">The phases the car charges on.</param>
/// <param name="NominalVoltage">Volts per phase, which amps and watts convert at.</param>
public sealed record ChargerLimits(int MinCurrentAmps, int MaxCurrentAmps, int Phases, double NominalVoltage);

/// <summary>
/// What the charger reports it is doing, in OCPP 1.6's status names. Map the charger's own states onto these,
/// and read the remarks: three of them look like "the car is done", and only the car's own doing counts.
/// </summary>
public enum ChargerState
{
    /// <summary>The charger's answer did not say, or said something the provider does not know.</summary>
    Unknown = 0,

    /// <summary>Ready, with no car plugged in.</summary>
    Available,

    /// <summary>A car is plugged in and waiting; no energy is flowing yet.</summary>
    Preparing,

    /// <summary>Delivering energy to the car.</summary>
    Charging,

    /// <summary>
    /// The <b>car</b> stopped drawing, typically because it reached its own limit. Gleanvolt reads this as
    /// the car being finished.
    /// </summary>
    SuspendedEv,

    /// <summary>
    /// The <b>charger</b> stopped delivering: a pause or a stop, Gleanvolt's or the charger's own. Never read as
    /// the car being finished. A paused charger reports this, not <see cref="SuspendedEv"/>.
    /// </summary>
    SuspendedEvse,

    /// <summary>The session is closing, the car's doing.</summary>
    Finishing,

    /// <summary>The charger reports a fault.</summary>
    Faulted,

    /// <summary>The charger is out of service.</summary>
    Unavailable,
}

/// <summary>One reading of the charger.</summary>
/// <param name="ReadAt">
/// When the provider read the charger, with its offset. No more than 5 minutes ahead of Gleanvolt's clock and
/// no older than 1 minute: a charger is controlled on what it is doing now.
/// </param>
/// <param name="Status">What the charger is doing.</param>
/// <param name="PowerWatts">What it is delivering to the car, all phases together, in watts; 0 to 100 000.</param>
/// <param name="InReplyTo">The request this answers, or null when sent unprompted.</param>
/// <param name="CurrentAmps">
/// The active current setpoint in whole amps, 0 to 80, as Gleanvolt last set it: <b>0 while the provider holds a
/// pause</b>, whatever the charger's own setting says. Null when it could not be read; Gleanvolt then makes
/// no setpoint decision.
/// </param>
/// <param name="ChargingAllowed">
/// Whether the charger is in the state <see cref="IChargerClient.SetChargingAllowed"/> with true puts it in,
/// counting a pause under a start as allowed. Null when it could not be read; Gleanvolt then makes no decision.
/// </param>
/// <param name="MaxCurrentAmps">
/// The highest current the charger itself takes right now, 6 to 80, when it says; Gleanvolt never sends more.
/// Null when the charger does not say.
/// </param>
/// <param name="Mode">The charger's own name for its mode, for display and the log only. At most 50 characters.</param>
public sealed record ChargerReading(
    DateTimeOffset ReadAt,
    ChargerState Status,
    double PowerWatts,
    RequestId? InReplyTo = null,
    int? CurrentAmps = null,
    bool? ChargingAllowed = null,
    int? MaxCurrentAmps = null,
    string? Mode = null);

/// <summary>Set the charging current.</summary>
/// <param name="Id">To quote in <see cref="ChargerCommandResult.InReplyTo"/>.</param>
/// <param name="Amps">Whole amps; below the charger's minimum (0) pauses.</param>
/// <param name="Reason">Why, in English, for the provider's log.</param>
public sealed record ChargerCurrentCommand(RequestId Id, int Amps, string Reason);

/// <summary>Allow charging, or stop it.</summary>
/// <param name="Id">To quote in <see cref="ChargerCommandResult.InReplyTo"/>.</param>
/// <param name="Allowed">True lets the charger charge; false stops it.</param>
/// <param name="Reason">Why, in English, for the provider's log.</param>
public sealed record ChargerAllowedCommand(RequestId Id, bool Allowed, string Reason);

/// <summary>How a command ended.</summary>
/// <param name="InReplyTo">The command's id.</param>
/// <param name="Succeeded">Whether the charger accepted it.</param>
/// <param name="Error">Why not, in English, for Gleanvolt's log; null when it succeeded.</param>
public sealed record ChargerCommandResult(RequestId InReplyTo, bool Succeeded, string? Error = null)
{
    /// <summary>Done.</summary>
    public static ChargerCommandResult Done(RequestId id) => new(id, true);

    /// <summary>Not done, for <paramref name="error"/>.</summary>
    public static ChargerCommandResult Failed(RequestId id, string error) => new(id, false, error);
}
