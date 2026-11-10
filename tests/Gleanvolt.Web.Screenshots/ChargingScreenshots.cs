using Bunit;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Web.Components.Pages;
using Gleanvolt.Web.Tests;

namespace Gleanvolt.Web.Screenshots;

/// <summary>
/// The manual's "Using Gleanvolt" pictures (#329): the Overview and each tab of Charging plan, on a sunny
/// June Sunday late in the morning. The home battery is at 64 %, and the ID.4 is plugged in at 58 %.
/// Each picture sets the mode it is about; the Overview shows the car charging in Solar + grid, as the
/// issue asks.
/// </summary>
public sealed class ChargingScreenshots : ScreenshotTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    /// <summary>11:30 in Prague.</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 9, 30, 0, TimeSpan.Zero);

    private static readonly EvInfo Id4 =
        new("id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77_000, 0.9, 3, 6, 16, "gleanvolt/vehicle/id4/state");

    private readonly ChargeControlStatusHolder _status = new();
    private readonly FakeChargeControlModeSelector _mode = new();
    private readonly FakeTargetedChargePreview _preview = new(OvernightPlan());
    private readonly VehicleStateHolder _car = new();
    private readonly FakeSolarForecastService _forecast = new();

    public ChargingScreenshots()
    {
        Services.AddSingleton(_status);

        _car.Set(new VehicleState(
            Now.AddMinutes(-1),
            SocPercent: 58,
            RangeMetres: 241_000,
            PlugState: VehiclePlugState.Connected,
            ChargeState: VehicleChargeState.Charging,
            SourceId: "volkswagen.de"));

        // The morning so far, in quarter hours from 06:00: the sun climbing, the battery filling from 31 %
        // to 64 %, and the car plugged in at 10:15.
        var energy = new FakeEnergyIntervalStore();
        var soc = 31.0;
        for (var start = new DateTimeOffset(2026, 6, 14, 4, 0, 0, TimeSpan.Zero); start < Now; start = start.AddMinutes(15))
        {
            var hours = (start - new DateTimeOffset(2026, 6, 14, 4, 0, 0, TimeSpan.Zero)).TotalHours;
            var solar = Math.Round(1.7 * Math.Sin(Math.Min(hours, 5.5) / 5.5 * Math.PI / 2), 2);
            var car = start >= Now.AddHours(-1.25) ? 1.06 : 0;
            var battery = Math.Max(0, Math.Round(solar - 0.45 - car, 2));
            energy.Intervals.Add(TestIntervals.Sample(
                start, solarKwh: solar, forecastSolarKwh: solar, gridImportKwh: solar < 0.45 ? 0.1 : 0,
                gridExportKwh: 0, evKwh: car, batteryChargeKwh: battery, socStartPercent: Math.Round(soc), socEndPercent: Math.Round(soc + 1.5)));
            soc = Math.Min(64, soc + 1.5);
        }

        Services.AddSingleton<IEnergyIntervalStore>(energy);

        _forecast.Days[new DateOnly(2026, 6, 14)] = new SolarDayForecastSummary(48_200, 39_500, 53_100, 7_600);
        _forecast.Days[new DateOnly(2026, 6, 15)] = new SolarDayForecastSummary(31_400, 22_800, 38_000, 6_100);

        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        Services.AddSingleton(Id4);
        Services.AddSingleton<IChargeControlModeSelector>(_mode);
        Services.AddSingleton<IChargeActions>(new FakeChargeActions(_mode));
        Services.AddSingleton<IBatteryHoldSelector>(new FakeBatteryHoldSelector());
        Services.AddSingleton<IForecastRuntimeSettings>(new FakeForecastRuntimeSettings());
        Services.AddSingleton<ISolarGridSettings>(new FakeSolarGridSettings());
        Services.AddSingleton<ILowTariffSettings>(new FakeLowTariffSettings());
        Services.AddSingleton<IUntilFullSettings>(new FakeUntilFullSettings());
        Services.AddSingleton<ITariffSchedule>(new FakeTariffSchedule());
        Services.AddSingleton<ITargetedChargeSelector>(new FakeTargetedChargeSelector());
        Services.AddSingleton<IFastChargeSelector>(new FakeFastChargeSelector());
        Services.AddSingleton<ITargetedChargePreview>(_preview);
        Services.AddSingleton<IVehicleTelemetry>(_car);
        Services.AddSingleton<IVehicleStateRefresh>(new FakeVehicleStateRefresh());
        Services.AddSingleton<ISolarForecastService>(_forecast);
        Services.AddSingleton(new TargetedDisplayOptions(TimeSpan.FromHours(36)));
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12), BatteryCapacityWh: 77_000, ChargeEfficiency: 0.9));
        SignedIn();
    }

    [Fact]
    public void The_overview_while_charging_in_solar_and_grid()
    {
        Running(ChargeControlMode.SolarGrid, Charging());

        Capture(RenderPage<Dashboard>("/"), "overview", selector: Main);
    }

    [Fact]
    public void The_solar_tab_before_anything_runs()
    {
        _status.Set(Statuses.Sample(Now) with { BatterySocPercent = 64, SolarPowerWatts = 6_800, SurplusWatts = 4_900, GridPowerWatts = -1_200 });

        Capture(Tab("solar"), "plan-solar", selector: Main);
    }

    [Fact]
    public void The_forecasted_tab_with_its_day_plan()
    {
        Running(ChargeControlMode.Forecasted, Charging() with { Plan = SunnyDayPlan() });

        Capture(Tab("forecasted"), "plan-forecasted", selector: Main);
    }

    [Fact]
    public void The_solar_and_grid_tab()
    {
        Running(ChargeControlMode.SolarGrid, Charging());

        Capture(Tab("solar-grid"), "plan-solar-grid", selector: Main);
    }

    [Fact]
    public void The_fast_tab_with_an_amount_and_a_departure()
    {
        _status.Set(Statuses.Sample(Now) with { BatterySocPercent = 64, CarConnected = true });
        var page = Tab("fast");

        page.Find("#fast-basis").Change("Soc");
        page.Find("#fast-soc").Change("80");
        page.Find("#fast-departure").Change("2026-06-15T07:00:00");

        Capture(page, "plan-fast", selector: Main);
    }

    [Fact]
    public void The_targeted_tab_and_its_preview()
    {
        _status.Set(Statuses.Sample(Now) with { BatterySocPercent = 64, CarConnected = true });
        var page = Tab("targeted");

        page.Find("#target-energy").Change("22");
        page.Find("#target-departure").Change("2026-06-15T07:00:00");
        Capture(page, "plan-targeted", selector: Main);

        page.Find("#preview-target").Click();
        Capture(page, "plan-targeted-preview", selector: "#target-preview");
    }

    private const string Main = "main.content";

    private IRenderedComponent<LayoutView> Tab(string tab) =>
        RenderPage<ChargingPlan>("/charging-plan/" + tab, new Dictionary<string, object?> { [nameof(ChargingPlan.Tab)] = tab });

    private void Running(ChargeControlMode mode, ChargeControlStatus status)
    {
        _mode.Set(mode, "picture");
        _status.Set(status with { Mode = mode });
    }

    // A clear late morning: 6.8 kW of sun, the house on 1.9 kW, the car taking 4.8 kW at 7 A and the
    // home battery the rest.
    private static ChargeControlStatus Charging() => Statuses.Sample(Now) with
    {
        State = ChargeControlState.Charging,
        SurplusWatts = 4_900,
        TargetCurrentAmps = 7,
        ActiveCurrentAmps = 7,
        BatterySocPercent = 64,
        ChargerStatus = EvChargerStatus.Charging,
        CarConnected = true,
        SolarPowerWatts = 6_800,
        ForecastSolarPowerWatts = 7_100,
        EvChargerPowerWatts = 4_830,
        EvChargingCurrentAmps = 7,
        BatteryPowerWatts = 120,
        GridPowerWatts = 50,
        SessionEnergyWh = 5_300,
        BatteryHoldEnabled = true,
        TomorrowForecastWh = 31_400,
    };

    // 22 kWh by 07:00 tomorrow, asked for at 11:30: the sun can bring 14.6 kWh this afternoon, paced at
    // about 3.7 kW, and the grid the last 7.4 kWh from 04:00.
    private static TargetedChargePlan OvernightPlan()
    {
        var departure = new DateTimeOffset(2026, 6, 15, 5, 0, 0, TimeSpan.Zero);
        var gridStart = new DateTimeOffset(2026, 6, 15, 2, 0, 0, TimeSpan.Zero);
        return new TargetedChargePlan(
            Strategy: TargetedChargeStrategy.SolarPlusGrid,
            Now: Now,
            DepartBy: departure,
            Deadline: departure.AddMinutes(-15),
            RequiredEnergyWh: 22_000,
            DeliveredEnergyWh: 0,
            RemainingEnergyWh: 22_000,
            SolarEnergyWh: 14_600,
            ForecastSurplusWh: 21_300,
            RequiredPaceWatts: 3_650,
            GridEnergyWh: 7_400,
            CeilingEnergyWh: 214_000,
            ExpectedEnergyWh: 22_000,
            ShortfallWh: 0,
            GridStart: gridStart,
            FeasibleDeparture: null,
            SocFloorPercent: 50,
            BatteryToFullWh: 4_200,
            Blocks:
            [
                new TargetedChargeBlock(Now, Now.AddHours(4), TargetedChargeSource.Solar, 3_650, 14_600),
                new TargetedChargeBlock(gridStart, departure.AddMinutes(-15), TargetedChargeSource.Grid, 2_960, 7_400),
            ],
            ForecastAsOf: Now.AddHours(-1),
            IsUsable: true,
            TailEnergyWh: 0,
            HoldUntil: null,
            Reason: "22 kWh by 07:00: 14.6 kWh of sun this afternoon, 7.4 kWh from the grid from 04:00.");
    }

    // The rest of today in half hours: the surplus rises to midday and falls away by evening, the car's
    // window is where it clears the charger's 4.1 kW floor, and the battery's floor climbs to 100 % by 19:00.
    private static SolarDayPlan SunnyDayPlan()
    {
        var timeline = new List<SolarDayPlanTimelinePoint>();
        for (var start = Now; start < Now.AddHours(9); start = start.AddMinutes(30))
        {
            var hoursFromNoon = (start - Now.AddHours(1.5)).TotalHours;
            var surplus = Math.Max(0, 6_200 * Math.Cos(hoursFromNoon / 6.5 * Math.PI / 2));
            var floor = Math.Min(100, 50 + (start - Now).TotalHours * 5.5);
            timeline.Add(new SolarDayPlanTimelinePoint(start, start.AddMinutes(30), Math.Round(surplus), surplus >= 4_140, Math.Round(floor)));
        }

        return TestPlans.Usable(Now, (Now, Now.AddHours(4.5)), timeline) with
        {
            RemainingPvWh = 38_400,
            ExpectedHouseWh = 9_800,
            BatteryToFullWh = 4_200,
            EvBudgetWh = 24_400,
            FeasibleEvEnergyWh = 18_600,
            RequiredSocFloorPercent = 50,
            TrajectorySocFloorPercent = 38,
            ShortfallWh = 0,
            Deadline = new DateTimeOffset(2026, 6, 14, 17, 0, 0, TimeSpan.Zero),
            Reason = "Good day: 18.6 kWh for the car, window now until 16:00, SOC floor 50%.",
        };
    }
}
