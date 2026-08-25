using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Fast;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.HomeAssistant;
using Gleanvolt.Hosting.Targeting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// Issue #89 from Home Assistant's side: which topic does what, and what a payload that is not a press
/// does. Driven through the worker's own command routing with no broker anywhere — the MQTT client is
/// never created, so every publish the handlers make is a no-op and only the seam calls are visible,
/// which is exactly the part worth asserting.
/// </summary>
public class HaButtonCommandTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 10, 20, 0, 0, TimeSpan.Zero); // 22:00 Prague

    private static readonly HomeAssistantOptions HaOptions = new()
    {
        BaseTopic = "solax",
        DeviceId = "solax_controller",
        DiscoveryPrefix = "homeassistant",
    };

    private static readonly HaDiscovery Discovery = new(HaOptions, Sites.Home);

    private readonly RecordingChargeActions _actions = new();
    private readonly TargetedChargeSelector _target = new(NullLogger<TargetedChargeSelector>.Instance);
    private readonly FastChargeSelector _fast = new(NullLogger<FastChargeSelector>.Instance);
    private readonly FakeVehicleTelemetry _vehicle = new();

    [Theory]
    [InlineData("start_solar", ChargeControlMode.Solar)]
    [InlineData("start_forecasted", ChargeControlMode.Forecasted)]
    [InlineData("start_fast_no_battery", ChargeControlMode.FastNoBattery)]
    public async Task EachButtonStartsItsOwnStrategy(string objectId, ChargeControlMode expected)
    {
        await Worker().HandleCommandAsync(Discovery.ButtonCommandTopic(objectId), HaDiscovery.PayloadPress);

        Assert.Equal((expected, "Home Assistant"), Assert.Single(_actions.Starts));
    }

    [Fact]
    public async Task TheOffButtonStopsCharging()
    {
        await Worker().HandleCommandAsync(Discovery.ChargeOffCommandTopic, HaDiscovery.PayloadPress);

        Assert.Equal("Home Assistant", Assert.Single(_actions.Stops));
        Assert.Empty(_actions.Starts);
    }

    [Theory]
    [InlineData("press")]        // the rule is exact, not case-insensitive
    [InlineData("ON")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnythingOtherThanAPressIsIgnored(string? payload)
    {
        var worker = Worker();

        await worker.HandleCommandAsync(Discovery.ButtonCommandTopic("start_solar"), payload);
        await worker.HandleCommandAsync(Discovery.ChargeOffCommandTopic, payload);

        Assert.Empty(_actions.Starts);
        Assert.Empty(_actions.Stops);
    }

    [Fact]
    public async Task TheRetiredModeTopicNoLongerDoesAnything()
    {
        // A retained payload can outlive the entity that produced it, and the select's set topic is
        // exactly the kind of thing a broker keeps for months.
        await Worker().HandleCommandAsync("solax/home-roof/charge_mode/set", "FastNoBattery");

        Assert.Empty(_actions.Starts);
    }

    [Fact]
    public async Task ActivateTargetSetsTheRequestAndThenStartsTheMode()
    {
        var worker = Worker();
        await worker.HandleCommandAsync(Discovery.NumberCommandTopic(HaDiscovery.TargetEnergyNumber), "22");
        await worker.HandleCommandAsync(Discovery.TextCommandTopic(HaDiscovery.TargetDepartureText), "07:00");

        await worker.HandleCommandAsync(Discovery.ActivateTargetCommandTopic, HaDiscovery.PayloadPress);

        Assert.Equal(22_000, _target.Request!.RequiredEnergyWh);
        Assert.Equal((ChargeControlMode.Targeted, "Home Assistant"), Assert.Single(_actions.Starts));
    }

    [Fact]
    public async Task ActivateTargetStillRefusesToFireWithNoEnergy()
    {
        var worker = Worker();
        await worker.HandleCommandAsync(Discovery.TextCommandTopic(HaDiscovery.TargetDepartureText), "07:00");

        await worker.HandleCommandAsync(Discovery.ActivateTargetCommandTopic, HaDiscovery.PayloadPress);

        Assert.Null(_target.Request);
        Assert.Empty(_actions.Starts);
    }

    [Fact]
    public async Task ClearingTheDepartureBoxClearsTheDeparture()
    {
        // Issue #117: emptying the box is a thing to mean, and the entity can now express it -- so it
        // has to do something coherent. Activating afterwards must find nothing to activate rather than
        // the departure someone thought they had removed.
        var worker = Worker();
        await worker.HandleCommandAsync(Discovery.NumberCommandTopic(HaDiscovery.TargetEnergyNumber), "22");
        await worker.HandleCommandAsync(Discovery.TextCommandTopic(HaDiscovery.TargetDepartureText), "07:00");

        await worker.HandleCommandAsync(Discovery.TextCommandTopic(HaDiscovery.TargetDepartureText), "");
        await worker.HandleCommandAsync(Discovery.ActivateTargetCommandTopic, HaDiscovery.PayloadPress);

        Assert.Null(_target.Request);
        Assert.Empty(_actions.Starts);
    }

    [Fact]
    public async Task ActivateTargetStillRefusesADepartureAlreadyPast()
    {
        var worker = Worker();
        await worker.HandleCommandAsync(Discovery.NumberCommandTopic(HaDiscovery.TargetEnergyNumber), "22");
        await worker.HandleCommandAsync(
            Discovery.TextCommandTopic(HaDiscovery.TargetDepartureText), "2026-08-10 06:00");

        await worker.HandleCommandAsync(Discovery.ActivateTargetCommandTopic, HaDiscovery.PayloadPress);

        Assert.Null(_target.Request);
        Assert.Empty(_actions.Starts);
    }

    [Fact]
    public async Task AChargerThatRefusesFastLeavesNothingLookingStarted()
    {
        _actions.Failure = "the charger did not answer";

        await Worker().HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_solar"), HaDiscovery.PayloadPress);

        // The press is recorded (it was made) but nothing else happened; the warning is the log's job.
        Assert.Single(_actions.Starts);
        Assert.Empty(_actions.Stops);
    }

    // -- The fast charge's amount (#119). Three entities and the button that was already there.

    [Fact]
    public async Task ChargeFastWithTheBasisLeftAloneStartsAnUnlimitedCharge()
    {
        // The dashboard nobody has touched: Full is the default, and the button does what it always did.
        await Worker().HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_fast_no_battery"), HaDiscovery.PayloadPress);

        Assert.Equal(ChargeControlMode.FastNoBattery, Assert.Single(_actions.Starts).Mode);
        Assert.Null(_fast.Limit);
    }

    [Fact]
    public async Task ChargeFastAppliesTheEnergyBoxUnderTheEnergyBasis()
    {
        var worker = Worker();

        await worker.HandleCommandAsync(Discovery.SelectCommandTopic(HaDiscovery.FastBasisSelect), "Energy");
        await worker.HandleCommandAsync(Discovery.NumberCommandTopic(HaDiscovery.FastEnergyNumber), "20");
        await worker.HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_fast_no_battery"), HaDiscovery.PayloadPress);

        Assert.Equal(20_000, _fast.Limit!.RequiredEnergyWh);
        Assert.Single(_actions.Starts);
    }

    [Fact]
    public async Task ChargeFastConvertsTheSocBoxUnderTheSocBasis()
    {
        _vehicle.State = new VehicleState(Now, SocPercent: 42);
        var worker = Worker(new VehicleOptions { BatteryCapacityKWh = 77, ChargeEfficiency = 0.9 });

        await worker.HandleCommandAsync(Discovery.SelectCommandTopic(HaDiscovery.FastBasisSelect), "Soc");
        await worker.HandleCommandAsync(Discovery.NumberCommandTopic(HaDiscovery.FastTargetSocNumber), "60");
        await worker.HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_fast_no_battery"), HaDiscovery.PayloadPress);

        Assert.Equal(15_400, _fast.Limit!.RequiredEnergyWh, 0);
        Assert.Equal(60, _fast.Limit.TargetSocPercent);
        Assert.Single(_actions.Starts);
    }

    [Fact]
    public async Task ARefusedAmountStopsThePressRatherThanChargingToFull()
    {
        // No capacity configured, so the SOC basis cannot be honoured. Starting anyway would run the
        // charge to full when a number was asked for, which is the opposite of the request.
        _vehicle.State = new VehicleState(Now, SocPercent: 42);
        var worker = Worker();

        await worker.HandleCommandAsync(Discovery.SelectCommandTopic(HaDiscovery.FastBasisSelect), "Soc");
        await worker.HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_fast_no_battery"), HaDiscovery.PayloadPress);

        Assert.Empty(_actions.Starts);
        Assert.Null(_fast.Limit);
    }

    [Fact]
    public async Task GoingBackToFullClearsAnAmountFromAnEarlierCharge()
    {
        var worker = Worker();

        await worker.HandleCommandAsync(Discovery.SelectCommandTopic(HaDiscovery.FastBasisSelect), "Energy");
        await worker.HandleCommandAsync(Discovery.NumberCommandTopic(HaDiscovery.FastEnergyNumber), "20");
        await worker.HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_fast_no_battery"), HaDiscovery.PayloadPress);

        await worker.HandleCommandAsync(Discovery.SelectCommandTopic(HaDiscovery.FastBasisSelect), "Full");
        await worker.HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_fast_no_battery"), HaDiscovery.PayloadPress);

        Assert.Null(_fast.Limit);
    }

    [Fact]
    public async Task AnUnrecognisedBasisIsIgnoredRatherThanGuessedAt()
    {
        var worker = Worker();

        await worker.HandleCommandAsync(Discovery.SelectCommandTopic(HaDiscovery.FastBasisSelect), "Whatever");
        await worker.HandleCommandAsync(Discovery.NumberCommandTopic(HaDiscovery.FastEnergyNumber), "20");
        await worker.HandleCommandAsync(
            Discovery.ButtonCommandTopic("start_fast_no_battery"), HaDiscovery.PayloadPress);

        // Still Full, so still unlimited -- not silently promoted to the energy that happens to be typed.
        Assert.Null(_fast.Limit);
        Assert.Single(_actions.Starts);
    }

    private HomeAssistantMqttWorker Worker(VehicleOptions? vehicleOptions = null) =>
        new(
            Options.Create(HaOptions),
            Options.Create(new BatteryHoldOptions()),
            _actions,
            new BatteryHoldSelector(initialHold: false, NullLogger<BatteryHoldSelector>.Instance),
            new ForecastRuntimeSettings(
                Options.Create(new ForecastChargeOptions()), NullLogger<ForecastRuntimeSettings>.Instance),
            _target,
            _fast,
            new NoopShutdown(),
            new ChargeControlStatusHolder(),
            NullLogger<HomeAssistantMqttWorker>.Instance,
            Options.Create(new TargetedChargeOptions()),
            Options.Create(vehicleOptions ?? new VehicleOptions()),
            Sites.Home,
            _vehicle,
            new FixedTimeProvider(Now, TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague")));

    /// <summary>Records presses without touching a charger; see <see cref="ChargeActionsTests"/> for the real one.</summary>
    private sealed class RecordingChargeActions : IChargeActions
    {
        public List<(ChargeControlMode Mode, string Source)> Starts { get; } = [];

        public List<string> Stops { get; } = [];

        public string? Failure { get; set; }

        public Task<ChargeActionResult> StartAsync(
            ChargeControlMode mode, string source, CancellationToken cancellationToken = default)
        {
            Starts.Add((mode, source));
            return Task.FromResult(Result());
        }

        public Task<ChargeActionResult> StopAsync(string source, CancellationToken cancellationToken = default)
        {
            Stops.Add(source);
            return Task.FromResult(Result());
        }

        private ChargeActionResult Result() =>
            Failure is { } failure ? ChargeActionResult.Failed(failure) : ChargeActionResult.Success;
    }

    private sealed class NoopShutdown : IServiceShutdown
    {
        public void RequestStop(string source) { }
    }

    /// <summary>A clock that does not move, in the site's zone — "07:00" has to mean tomorrow morning.</summary>
    private sealed class FixedTimeProvider(DateTimeOffset now, TimeZoneInfo zone) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;

        public override TimeZoneInfo LocalTimeZone { get; } = zone;
    }

    /// <summary>The car's last reading, or none at all — the two cases a SOC basis has to tell apart.</summary>
    private sealed class FakeVehicleTelemetry : IVehicleTelemetry
    {
        public VehicleState? State { get; set; }

        public VehicleState? GetCurrentState() => State;
    }
}
