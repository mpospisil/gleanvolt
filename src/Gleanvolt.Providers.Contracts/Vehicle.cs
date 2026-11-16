namespace Gleanvolt.Providers.Contracts.Vehicle;

/// <summary>The car's kind: its contract version, and where its hub is (#408).</summary>
public static class VehicleContract
{
    /// <summary>The kind's id.</summary>
    public const string Kind = "vehicle";

    /// <summary>Changes only when an older provider would be misread.</summary>
    public const int Major = 1;

    /// <summary>Grows with every addition an older provider can ignore.</summary>
    public const int Minor = 1;

    /// <summary>The hub's path on Gleanvolt's web port.</summary>
    public const string HubPath = "/providers/vehicle";
}

/// <summary>
/// The car's hub: a provider publishes what the car last said about itself. Gleanvolt keeps the newest
/// reading and shows its age, connected or not. Read-only: nothing here sends the car a command.
/// </summary>
public interface IVehicleHub : IProviderHub<VehicleWelcome>
{
    /// <summary>
    /// Offers a reading. It is stored when it passes every check; on any problem nothing changes and
    /// the problems come back. Publish whenever the car has something new: plugging in, a charge
    /// starting or ending, a step in SOC. Gleanvolt does not poll.
    /// </summary>
    Task<PublishResult> Publish(VehicleReading reading);
}

/// <summary>What the car's hub may send its provider.</summary>
public interface IVehicleClient : IProviderClient
{
    /// <summary>
    /// Somebody asked the car (<i>Ask the car</i>), or a plan wants a fresh reading. Answer with
    /// <see cref="IVehicleHub.Publish"/>, quoting <see cref="RefreshRequest.Id"/> in
    /// <see cref="VehicleReading.InReplyTo"/>, within a minute. A car that has not reported since may
    /// answer with the reading it already sent.
    /// </summary>
    Task RefreshRequested(RefreshRequest request);

    /// <summary>
    /// The car as Gleanvolt knows it changed while the session was attached. Readings are for
    /// <paramref name="car"/> from now on.
    /// </summary>
    Task VehicleChanged(VehicleInfo car);
}

/// <summary>An accepted vehicle hello: the car Gleanvolt is configured for, so it is not configured twice.</summary>
public sealed record VehicleWelcome : Welcome
{
    /// <summary>The car as Gleanvolt knows it.</summary>
    public VehicleInfo Car { get; init; } = new(string.Empty, string.Empty, string.Empty, string.Empty, null, string.Empty);
}

/// <summary>
/// The car as configured in Gleanvolt. A provider that serves one of several cars on an account picks
/// the right one by these. Anything the owner has not entered is empty or null.
/// </summary>
/// <param name="Id">The owner's id for the car, e.g. "id4".</param>
/// <param name="Name">What the owner calls it.</param>
/// <param name="Make">E.g. "Volkswagen".</param>
/// <param name="Model">E.g. "ID.4".</param>
/// <param name="BatteryCapacityKWh">The usable pack, in kWh. Gleanvolt keeps it; a provider never supplies it.</param>
/// <param name="TimeZone">The installation's IANA time zone, e.g. "Europe/Prague".</param>
public sealed record VehicleInfo(
    string Id,
    string Name,
    string Make,
    string Model,
    double? BatteryCapacityKWh,
    string TimeZone);

/// <summary>What the car says it is doing.</summary>
public enum CarChargeState
{
    /// <summary>The car does not say.</summary>
    Unknown,

    /// <summary>Not charging.</summary>
    Idle,

    /// <summary>Charging.</summary>
    Charging,

    /// <summary>Charged to its own limit.</summary>
    Complete,
}

/// <summary>Whether the car says a cable is connected.</summary>
public enum CarPlugState
{
    /// <summary>The car does not say.</summary>
    Unknown,

    /// <summary>No cable.</summary>
    Disconnected,

    /// <summary>A cable is in.</summary>
    Connected,
}

/// <summary>
/// What the car last said about itself. Everything but <see cref="CapturedAt"/> is optional, and absent
/// means unknown, never zero; at least one of SOC, range, charge state or plug state must be there.
/// </summary>
/// <param name="CapturedAt">
/// When the <b>car</b> produced the reading, with its offset: not when the provider sent it. Gleanvolt
/// judges the reading's age by it. No more than 5 minutes ahead of Gleanvolt's clock, no older than 30
/// days, and not older than the reading Gleanvolt holds. The same time as that one is accepted.
/// </param>
/// <param name="InReplyTo">The request this answers, or null when sent unprompted.</param>
/// <param name="SocPercent">State of charge, 0 to 100.</param>
/// <param name="RangeMetres">The car's own range estimate, 0 to 2 000 km, in metres.</param>
/// <param name="ChargeTimeRemaining">How long the car says it still needs, 0 to 48 hours, e.g. "01:30:00".</param>
/// <param name="ChargeState">What the car says it is doing.</param>
/// <param name="PlugState">Whether the car says a cable is in.</param>
public sealed record VehicleReading(
    DateTimeOffset CapturedAt,
    RequestId? InReplyTo = null,
    double? SocPercent = null,
    double? RangeMetres = null,
    TimeSpan? ChargeTimeRemaining = null,
    CarChargeState? ChargeState = null,
    CarPlugState? PlugState = null);
