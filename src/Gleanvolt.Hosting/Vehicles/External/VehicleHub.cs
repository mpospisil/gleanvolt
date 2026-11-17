using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Tariff.External;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Vehicles.External;

/// <summary>
/// The car's hub (#408): a provider publishes what the car last said, and once it passes
/// <see cref="VehicleReadingValidator"/> it is written to <see cref="VehicleStateHolder"/>, where every
/// page, the planner and Home Assistant read it. Nothing here waits for the provider.
///
/// <para>On while <see cref="ExternalVehicleFeed"/> is this installation's feed: the feed choice on
/// <c>/car</c> is the switch, and like every feed choice it takes a restart.</para>
/// </summary>
public sealed class VehicleHub(
    ExternalProviderRegistry registry,
    ProviderHostInfo host,
    VehicleStateHolder holder,
    ConfiguredVehicleFeed configured,
    EvInfo car,
    ChargingSessionNotifier session,
    TimeProvider time)
    : ProviderHub<IVehicleClient, VehicleWelcome>(registry, host), IVehicleHub
{
    /// <summary>The kind, as registered.</summary>
    public static ProviderKind Kind { get; } = new(
        VehicleContract.Kind,
        "Car",
        VehicleContract.Major,
        VehicleContract.Minor,
        VehicleContract.HubPath,
        services => services.GetRequiredService<ConfiguredVehicleFeed>().Service is ExternalVehicleFeed);

    /// <summary>The car as a provider is told it.</summary>
    public static VehicleInfo Info(EvInfo car, TimeZoneInfo zone) => new(
        car.Id,
        car.Name,
        car.Make,
        car.Model,
        car.BatteryCapacityWh > 0 ? car.BatteryCapacityWh / 1000 : null,
        TariffHub.IanaId(zone));

    protected override VehicleWelcome CreateWelcome() => new() { Car = Info(car, time.LocalTimeZone), Session = session.Current };

    public Task<PublishResult> Publish(VehicleReading reading)
    {
        RequireAttached();

        var feed = configured.Service as ExternalVehicleFeed;
        var name = Sessions.Status.ProviderName ?? Sessions.Status.ProviderId ?? ExternalVehicleFeed.Id;
        var state = reading is null ? new VehicleState(default, SourceId: name) : ToState(reading, name);

        var problems = VehicleReadingValidator.Check(state, holder.GetCurrentState(), time.GetUtcNow());

        if (problems.Count > 0)
        {
            Sessions.DataRefused(reading?.InReplyTo, problems, problems.Phrases);
            feed?.Refused(reading?.InReplyTo);
            return Task.FromResult(PublishResult.Refused([.. problems]));
        }

        // The holder takes the same capture time again (a car that has not reported since); only an
        // older one is turned away, and the validator has already refused that against this holder.
        holder.Set(state);
        Sessions.DataAccepted(reading!.InReplyTo);
        feed?.Accepted(reading.InReplyTo, state);
        return Task.FromResult(PublishResult.Ok);
    }

    /// <summary>A published reading as Gleanvolt holds it, named after <paramref name="name"/>, the provider.</summary>
    internal static VehicleState ToState(VehicleReading reading, string name) => new(
        reading.CapturedAt,
        reading.SocPercent,
        reading.RangeMetres,
        reading.ChargeTimeRemaining,
        Map(reading.ChargeState),
        Map(reading.PlugState),
        name);

    // By name, not by number: the two enums are separate contracts that happen to agree today. A value
    // neither knows stays out of range, and the validator refuses it.
    private static VehicleChargeState Map(CarChargeState? state) => state switch
    {
        null or CarChargeState.Unknown => VehicleChargeState.Unknown,
        CarChargeState.Idle => VehicleChargeState.Idle,
        CarChargeState.Charging => VehicleChargeState.Charging,
        CarChargeState.Complete => VehicleChargeState.Complete,
        _ => (VehicleChargeState)(int)state.Value,
    };

    private static VehiclePlugState Map(CarPlugState? state) => state switch
    {
        null or CarPlugState.Unknown => VehiclePlugState.Unknown,
        CarPlugState.Disconnected => VehiclePlugState.Disconnected,
        CarPlugState.Connected => VehiclePlugState.Connected,
        _ => (VehiclePlugState)(int)state.Value,
    };
}
