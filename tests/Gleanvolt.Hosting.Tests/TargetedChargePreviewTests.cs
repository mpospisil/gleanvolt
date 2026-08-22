using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Targeting;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The quote, as against the promise (#99): <see cref="ITargetedChargePreview"/> answers "what would
/// this request do?" for a request nobody has made, so the plan can be put in front of the owner
/// before the button that starts the charger rather than after it.
///
/// <para>What has to be true of it is mostly negative — it must not set a request, must not disturb
/// the delivery metering behind a running target, and must survive being called when nothing has been
/// requested at all — which is exactly why it is worth a suite rather than a line in
/// <see cref="TargetedModeTests"/>.</para>
/// </summary>
public class TargetedChargePreviewTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 8, 22, 0, 0, TimeSpan.Zero);

    // 16A x 230V x 3 phases, as everywhere else in these tests.
    private const double MaxPowerWatts = 11_040;

    private readonly TargetedChargeSelector _selector = new(NullLogger<TargetedChargeSelector>.Instance);
    private readonly TargetedChargeProvider _provider;

    public TargetedChargePreviewTests()
    {
        var chargeControl = new ChargeControlOptions { Phases = 3, MaxChargingCurrentAmps = 16 };
        var forecast = new NoForecastService();
        var power = new ChargePowerConverter(chargeControl.NominalVoltage, chargeControl.Phases);
        var forecastOptions = Options.Create(new ForecastChargeOptions());

        _provider = TargetedCharge.Provider(
            forecast,
            new DayPlanProvider(
                forecast, forecastOptions, Options.Create(chargeControl), power,
                NullLogger<DayPlanProvider>.Instance),
            power,
            chargeControl,
            forecastOptions,
            _selector,
            new TargetedChargeOptions { SafetyMargin = TimeSpan.Zero });
    }

    [Fact]
    public void SaysNothingCanBePlannedBeforeTheFirstPoll()
    {
        // No reading means no battery SOC, no house load and no instant to anchor to. A plan built on
        // zeroes would look like an answer; null is one the caller can put into words.
        Assert.Null(Preview(20_000, Now.AddHours(9)));
    }

    [Fact]
    public void PricesARequestAgainstTheLatestReading()
    {
        _provider.Update(Idle(Now));

        var plan = Preview(20_000, Now.AddHours(9));

        Assert.NotNull(plan);
        Assert.Equal(Now, plan!.Now);
        Assert.Equal(20_000, plan.RequiredEnergyWh);

        // Nothing delivered: the whole request is still ahead of a charge that has not started.
        Assert.Equal(0, plan.DeliveredEnergyWh);
        Assert.Equal(20_000, plan.RemainingEnergyWh);

        // No forecast here, so the mode's grid-only degradation -- which still meets the target.
        Assert.Equal(TargetedChargeStrategy.SolarPlusGrid, plan.Strategy);
        Assert.Equal(0, plan.ShortfallWh);
    }

    [Fact]
    public void PricesTheShortfallSoItCanBeSeenBeforeTheChargerStarts()
    {
        // The case the preview earns its keep on: 25 kWh in two hours is more than the charger can
        // deliver however the plan is arranged, and finding that out afterwards is finding it out at
        // 05:00 with a car that is short.
        _provider.Update(Idle(Now));

        var plan = Preview(25_000, Now.AddHours(2))!;

        Assert.Equal(TargetedChargeStrategy.Maximum, plan.Strategy);
        Assert.Equal(25_000 - (2 * MaxPowerWatts), plan.ShortfallWh, 0);
        Assert.NotNull(plan.FeasibleDeparture);
    }

    [Fact]
    public void PricingIsNotRequesting()
    {
        _provider.Update(Idle(Now));

        Preview(20_000, Now.AddHours(9));
        Preview(30_000, Now.AddHours(4));

        // The whole point of a separate seam: nothing was promised, and the poll loop has nothing to
        // drive.
        Assert.Null(_selector.Request);
        Assert.Null(_provider.Update(Idle(Now.AddMinutes(5))));
    }

    [Fact]
    public void DoesNotDisturbTheMeteringBehindARunningTarget()
    {
        _selector.Set(new TargetedChargeRequest(20_000, Now.AddHours(9), Now), "test");
        // A minute apart: the integrator skips gaps longer than five, which is a stalled poll loop
        // rather than a slow charge.
        _provider.Update(Idle(Now));
        _provider.Update(Drawing(Now.AddMinutes(1), MaxPowerWatts));
        _provider.Update(Drawing(Now.AddMinutes(2), MaxPowerWatts));

        var deliveredBefore = _provider.DeliveredWh;
        Assert.True(deliveredBefore > 0);

        Preview(30_000, Now.AddHours(6));

        // A preview reads the same telemetry the running plan does and writes nothing back to it, so
        // the owner may press it as often as they like while a target is being delivered.
        Assert.Equal(deliveredBefore, _provider.DeliveredWh);

        var running = _provider.Update(Drawing(Now.AddMinutes(2), MaxPowerWatts))!;
        Assert.Equal(20_000, running.RequiredEnergyWh);
        Assert.Equal(deliveredBefore, running.DeliveredEnergyWh, 0);
        Assert.Equal(Now.AddHours(9), running.DepartBy);
    }

    [Fact]
    public void KeepsAnsweringWhileNoTargetIsRunning()
    {
        // Update returns null with no request, and used to return before recording the reading. That
        // left the preview answering "no poll has completed yet" on a service that had been up for
        // hours -- at exactly the moment the owner is deciding whether to trust it.
        Assert.Null(_provider.Update(Idle(Now)));

        Assert.NotNull(Preview(20_000, Now.AddHours(9)));
    }

    private TargetedChargePlan? Preview(double energyWh, DateTimeOffset departBy) =>
        ((ITargetedChargePreview)_provider).Preview(new TargetedChargeRequest(energyWh, departBy, Now));

    private static EnergyState Idle(DateTimeOffset at) =>
        new(at, BatterySocPercent: 90, BatteryPowerWatts: 0, SolarPowerWatts: 0, GridPowerWatts: 300,
            EvChargerStatus.Preparing, EvChargerPowerWatts: 0);

    private static EnergyState Drawing(DateTimeOffset at, double evWatts) =>
        new(at, BatterySocPercent: 90, BatteryPowerWatts: 0, SolarPowerWatts: 0,
            GridPowerWatts: evWatts + 300, EvChargerStatus.Charging, EvChargerPowerWatts: evWatts);
}
