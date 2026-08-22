using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The dashboard reports and no longer decides (#98): the controls that used to sit under these
/// numbers moved to <see cref="ChargingPlanPageTests"/>, and what is left is grouped as Energy,
/// Vehicle and Charging session. Follows the same holder-subscription seam
/// <see cref="HealthPageTests"/> already covers.
/// </summary>
public class DashboardPageTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);

    // Vehicle telemetry (#73) is read through the same holder seam. Registered empty by default, so the
    // tests below assert the page's behaviour with no car configured -- which is what an install with
    // Vehicle:Enabled=false looks like.
    private readonly VehicleStateHolder _vehicle = new();

    public DashboardPageTests()
    {
        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IVehicleTelemetry>(_vehicle);
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
    }

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = Render<Dashboard>();

        Assert.Contains("No poll has completed yet", page.Markup);
    }

    [Fact]
    public void Groups_what_it_reports_under_the_three_questions_being_asked()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal(
            ["Energy", "Vehicle", "Charging session"],
            page.FindAll("h2").Select(h => h.TextContent.Trim()));
    }

    [Fact]
    public void Hides_the_cloud_telemetry_entirely_when_no_car_has_reported()
    {
        // Vehicle:Enabled=false must not leave an empty card on the dashboard of an install that has
        // no car configured at all -- but the charger's own view of the socket is still worth having.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with { CarConnected = true });

        var page = Render<Dashboard>();

        Assert.DoesNotContain("Car battery", page.Markup);
        Assert.DoesNotContain("Car reading age", page.Markup);
        Assert.Contains("Car connected", page.Markup);
    }

    [Fact]
    public void Shows_the_cars_soc_alongside_the_age_of_the_reading()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(
            _time.Now.AddHours(-3.5),
            SocPercent: 28,
            ChargeState: VehicleChargeState.Idle,
            PlugState: VehiclePlugState.Disconnected,
            SourceId: "id4"));

        var page = Render<Dashboard>();

        Assert.Contains("Car battery", page.Markup);
        Assert.Contains("28%", page.Markup);
        Assert.Contains("3.5 h", page.Markup);
        Assert.Contains("id4", page.Markup);
        Assert.Contains("Disconnected", page.Markup);
    }

    [Fact]
    public void Marks_a_reading_older_than_the_configured_age_as_stale()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(
            _time.Now.AddHours(-20),
            SocPercent: 41,
            ChargeState: VehicleChargeState.Unknown,
            PlugState: VehiclePlugState.Unknown,
            SourceId: "id4"));

        var page = Render<Dashboard>();

        Assert.Contains("stale", page.Markup);
    }

    [Fact]
    public void Leaves_a_fresh_reading_unmarked()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(
            _time.Now.AddHours(-2),
            SocPercent: 41,
            ChargeState: VehicleChargeState.Charging,
            PlugState: VehiclePlugState.Connected,
            SourceId: "id4"));

        var page = Render<Dashboard>();

        Assert.DoesNotContain("stale", page.Markup);
    }

    [Fact]
    public void Reports_a_car_that_says_nothing_about_itself_as_unknown()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(
            _time.Now.AddMinutes(-20),
            SocPercent: null,
            ChargeState: VehicleChargeState.Unknown,
            PlugState: VehiclePlugState.Unknown,
            SourceId: null));

        var page = Render<Dashboard>();

        Assert.Contains("Car battery", page.Markup);
        Assert.Contains("20 min", page.Markup);
        Assert.Contains("Unknown", page.Markup);
    }

    [Fact]
    public void Shows_the_core_telemetry_fields()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 3200,
            SurplusWatts = 1800,
            BatterySocPercent = 72,
            BatteryPowerWatts = 450,
            GridPowerWatts = -300,
            EvChargerPowerWatts = 2760,
            EvChargingCurrentAmps = 12,
            TargetCurrentAmps = 12,
            ActiveCurrentAmps = 12,
            ChargerStatus = EvChargerStatus.Charging,
            CarConnected = true,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Solar", page.Markup);
        Assert.Contains("3200 W", page.Markup);
        Assert.Contains("1800 W", page.Markup);
        Assert.Contains("72%", page.Markup);
        Assert.Contains("450 W", page.Markup);
        Assert.Contains("-300 W", page.Markup);
        Assert.Contains("2760 W", page.Markup);
        Assert.Contains("12 A", page.Markup);
        Assert.Contains("Charging", page.Markup);
        Assert.Contains("Yes", page.Markup);
    }

    [Fact]
    public void Reports_the_session_energy_while_a_mode_is_driving()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with
        {
            SessionEnergyWh = 8_400,
            LoanPowerWatts = 1_140,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Session energy", page.Markup);
        Assert.Contains("8.4 kWh", page.Markup);
        Assert.Contains("Battery loan power", page.Markup);
        Assert.Contains("1140 W", page.Markup);
    }

    [Fact]
    public void Offers_a_way_to_start_instead_of_a_grid_of_dashes_when_nothing_is_charging()
    {
        // Mode Off and an idle charger: there is no session to report, and the useful thing to say is
        // where a session is started from.
        _holder.Set(Statuses.Sample(_time.Now));

        var page = Render<Dashboard>();

        Assert.Contains("Nothing is charging", page.Markup);
        Assert.DoesNotContain("Session energy", page.Markup);
        Assert.Contains("/charging-plan", page.Find("h2 + p a").GetAttribute("href"));
    }

    [Fact]
    public void Still_reports_a_session_the_controller_is_not_driving()
    {
        // Somebody put the charger into Fast by hand. Mode is Off, the car is drawing, and hiding that
        // behind "nothing is charging" would be a lie about the one thing worth knowing.
        _holder.Set(Statuses.Sample(_time.Now) with
        {
            CarConnected = true,
            ChargerStatus = EvChargerStatus.Charging,
            EvChargerPowerWatts = 3_600,
        });

        var page = Render<Dashboard>();

        Assert.DoesNotContain("Nothing is charging", page.Markup);
        Assert.Contains("3600 W", page.Markup);
    }

    [Fact]
    public void Blanks_the_surplus_and_current_fields_when_the_mode_isnt_deciding_on_them()
    {
        _holder.Set(Statuses.Sample(_time.Now) with
        {
            SurplusWatts = null,
            TargetCurrentAmps = null,
            ActiveCurrentAmps = null,
        });

        var page = Render<Dashboard>();

        Assert.Contains("—", page.Markup);
    }

    [Fact]
    public void Follows_the_holder_instead_of_sampling_it_once()
    {
        var page = Render<Dashboard>();
        Assert.Contains("No poll has completed yet", page.Markup);

        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted));

        page.WaitForAssertion(() => Assert.Contains("Forecasted", page.Markup));
    }

    [Fact]
    public void Stops_following_the_holder_once_the_circuit_is_gone()
    {
        var page = Render<Dashboard>();
        page.Dispose();

        var exception = Record.Exception(() => _holder.Set(Statuses.Sample(_time.Now)));

        Assert.Null(exception);
    }

    [Fact]
    public void Shows_the_forecast_solar_power_beside_the_measured_one()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 3200,
            ForecastSolarPowerWatts = 4100,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Forecast solar power", page.Markup);
        Assert.Contains("3200 W", page.Markup);
        Assert.Contains("4100 W", page.Markup);
    }

    [Fact]
    public void Shows_a_zero_forecast_when_none_covers_this_moment()
    {
        // No forecast fetched yet is not an error state and must not blank the tile -- the pair is
        // meant to be readable at a glance, including before the first fetch of the day lands.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 3200,
            ForecastSolarPowerWatts = 0,
        });

        var page = Render<Dashboard>();

        Assert.Contains("Forecast solar power", page.Markup);
        Assert.Contains("0 W", page.Markup);
    }

    [Fact]
    public void Carries_no_charging_controls_at_all()
    {
        // #98: every control moved to the charging-plan page. A button or a number here would mean
        // two places to look for the same decision, which is what that page exists to end.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with { BatteryHoldEnabled = true });

        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("button"));
        Assert.Empty(page.FindAll("input"));
        Assert.Empty(page.FindAll("select"));
    }
}
