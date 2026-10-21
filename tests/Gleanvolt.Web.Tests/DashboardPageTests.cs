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
public class DashboardPageTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);

    // Vehicle telemetry (#73) is read through the same holder seam. Registered empty by default, so the
    // tests below assert the page's behaviour with no car configured -- which is what an install with
    // Vehicle:Enabled=false looks like.
    private readonly VehicleStateHolder _vehicle = new();

    private readonly FakeVehicleStateRefresh _refresh = new();

    // Holds nothing by default: the install before its first forecast refresh.
    private readonly FakeSolarForecastService _forecast = new();

    // The car the section is about (#124/#140). Unknown by default, which is the install that has
    // never described one; a test that is about the card sets it before rendering.
    private EvInfo _car = EvInfo.Unknown;

    public DashboardPageTests()
    {
        Services.AddSingleton(_holder);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IVehicleTelemetry>(_vehicle);
        Services.AddSingleton<IVehicleStateRefresh>(_refresh);
        Services.AddSingleton<ISolarForecastService>(_forecast);

        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
        Services.AddSingleton(_ => _car);
    }

    private static EvInfo Id4() => new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77_000, 0.9, 3, 6, 16, "gleanvolt/vehicle/id4/state");

    /// <summary>
    /// A manufacturer feed that only ever answers "here is how I am". Fetching throws on purpose: the
    /// dashboard reads <see cref="IVehicleUpdateService.Health"/> on a render and must never do I/O
    /// on one.
    /// </summary>
    private sealed class StubFeed(VehicleSourceHealth health) : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-group";

        public string DisplayName => "Data Act portal";

        public VehicleSourceHealth Health => health;

        public TimeSpan NextDelay => TimeSpan.FromMinutes(15);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private void Feed(VehicleSourceHealth health) =>
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed(health));

    /// <summary>volkswagen.de's shape (#170, #212): the owner's words, and a fix sentence when blocked.</summary>
    private sealed class WebsiteFeed(VehicleSourceHealth? health = null) : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-website";

        public string DisplayName => "volkswagen.de";

        public string? OwnerAction =>
            Health.IsBlocked ? "volkswagen.de wants a one-time code — sign in again on the Car page." : null;

        public VehicleSourceHealth Health => health ?? VehicleSourceHealth.Ok("used while a charge is running");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(1);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private void WebsiteFeedConfigured(VehicleSourceHealth? health = null) =>
        Services.AddSingleton<IVehicleUpdateService>(new WebsiteFeed(health));

    /// <summary>Markup with whitespace collapsed, so a prose assertion need not know where it wrapped.</summary>
    private static string Prose(IRenderedComponent<Dashboard> page) =>
        System.Text.RegularExpressions.Regex.Replace(page.Markup, @"\s+", " ");

    [Fact]
    public void Shows_the_car_and_its_pack_with_no_feed_configured_and_says_nothing_is_wrong()
    {
        // State one of four (#140): the car is configuration, so the card shows it because it is
        // *defined* -- not because something reported on it.
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Contains("The ID.4", page.Markup);
        Assert.Contains("77 kWh usable", page.Markup);
        Assert.Contains("No feed is configured", page.Markup);
        Assert.DoesNotContain("Car battery", page.Markup);
        Assert.DoesNotContain("Sign-in required", page.Markup);
    }

    [Fact]
    public void A_car_described_by_its_pack_alone_still_reads_as_a_sentence()
    {
        // Everything in the Ev section except the pack is optional (#124), and a car with no name and
        // no model must not render as a leading comma.
        _car = EvInfo.Unknown with { BatteryCapacityWh = 77_000 };
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Contains("The car, 77 kWh usable", page.Markup);
    }

    [Fact]
    public void Says_sign_in_is_required_rather_than_stale_when_the_feed_is_blocked()
    {
        // The row this whole card exists for. "Stale" clears itself and "sign-in required" never
        // will, so the two must not read alike -- and the sentence is what says which screen to open.
        _car = Id4();
        Feed(VehicleSourceHealth.NeedsOwner(
            "The portal is showing something only you can answer (consent) -- open it in a browser."));
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Contains("Sign-in required", page.Markup);
        Assert.Contains("open it in a browser", page.Markup);
        Assert.DoesNotContain("stale", page.Markup);
    }

    [Fact]
    public void Keeps_the_ageing_reading_beside_a_sign_in_that_is_required()
    {
        // Blocked with a reading already in hand: both facts are worth having, and the actionable one
        // is the one that has to stand out.
        _car = Id4();
        Feed(VehicleSourceHealth.NeedsOwner("The portal refused the sign-in: check the password."));
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddHours(-20), SocPercent: 41, SourceId: "vw-group ...1234"));

        var page = Render<Dashboard>();

        Assert.Contains("Sign-in required", page.Markup);
        Assert.Contains("check the password", page.Markup);
        Assert.Contains("41%", page.Markup);
        Assert.Contains("stale", page.Markup);
    }

    [Fact]
    public void Says_it_is_waiting_when_a_feed_is_configured_and_has_not_delivered()
    {
        _car = Id4();
        Feed(VehicleSourceHealth.Starting);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Contains("has not delivered a reading yet", page.Markup);
        Assert.Contains("Data Act portal", page.Markup);
        Assert.DoesNotContain("No feed is configured", page.Markup);
        Assert.DoesNotContain("Sign-in required", page.Markup);
    }

    [Fact]
    public void A_healthy_feed_reads_exactly_as_it_did_before_the_card_had_states()
    {
        _car = Id4();
        Feed(VehicleSourceHealth.Ok("The portal answered."));
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-20), SocPercent: 62, SourceId: "vw-group ...1234"));

        var page = Render<Dashboard>();

        Assert.Contains("62%", page.Markup);
        Assert.Contains("20 min", page.Markup);
        Assert.DoesNotContain("Sign-in required", page.Markup);
        Assert.DoesNotContain("No feed is configured", page.Markup);
        Assert.DoesNotContain("has not delivered", page.Markup);
    }

    [Fact]
    public void An_install_that_has_described_no_car_at_all_gains_no_new_text()
    {
        // The guarantee (#140): a car with no update service configured -- here, no car either --
        // behaves exactly as it did before any of this existed. Not one sentence about a feed.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with { CarConnected = true });

        var page = Render<Dashboard>();

        Assert.Contains("Car connected", page.Markup);
        Assert.DoesNotContain("No feed is configured", page.Markup);
        Assert.DoesNotContain("has not delivered", page.Markup);
        Assert.DoesNotContain("Sign-in required", page.Markup);
    }

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = Render<Dashboard>();

        Assert.Contains("No poll has completed yet", page.Markup);
    }

    [Fact]
    public void Groups_what_it_reports_under_the_questions_being_asked()
    {
        // The overview (#255): the headline and the figures carry no heading of their own -- they are
        // the page -- and the rest is the mode, the session, then the car and the forecast as before.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal(
            ["Charge mode", "This session", "Vehicle", "Solar forecast"],
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

        // The strip shows power with the direction as a word (#255); the session card keeps watts
        // and amps, which is where the precision is read.
        Assert.Contains("3.2 kW", page.Find(".figure.solar").TextContent);
        Assert.Contains("surplus 1.8 kW", page.Find(".figure.solar").TextContent);
        Assert.Contains("72%", page.Find(".figure.battery").TextContent);
        Assert.Contains("450 W", page.Find(".figure.battery").TextContent);
        Assert.Contains("charging", page.Find(".figure.battery").TextContent);
        Assert.Contains("300 W", page.Find(".figure.grid-flow").TextContent);
        Assert.Contains("exporting", page.Find(".figure.grid-flow").TextContent);
        Assert.Contains("2,760 W", page.Find("#this-session").TextContent);
        Assert.Contains("12 A", page.Find("#this-session").TextContent);
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

        var session = page.Find("#this-session").TextContent;
        Assert.Contains("Session energy", session);
        Assert.Contains("8.4 kWh", session);
        Assert.Contains("Battery loan power", session);
        Assert.Contains("1,140 W", session);
    }

    [Fact]
    public void Offers_a_way_to_start_instead_of_a_grid_of_dashes_when_nothing_is_charging()
    {
        // Mode Off and an idle charger: there is no session to report, and the useful thing to say is
        // where a session is started from.
        _holder.Set(Statuses.Sample(_time.Now));

        var page = Render<Dashboard>();

        var session = page.Find("#this-session");
        Assert.Contains("Nothing is charging", session.TextContent);
        Assert.DoesNotContain("Session energy", session.TextContent);
        Assert.Contains("/charging-plan", session.QuerySelector("a")!.GetAttribute("href"));
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
        Assert.Contains("3,600 W", page.Markup);
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

        var solar = page.Find(".figure.solar").TextContent;
        Assert.Contains("3.2 kW", solar);
        Assert.Contains("forecast 4.1 kW", solar);
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

        Assert.Contains("forecast 0 W", page.Find(".figure.solar").TextContent);
    }

    [Fact]
    public void Shows_the_house_apart_from_the_car_from_the_live_readings()
    {
        // Solar 5000 plus 300 imported, less 1000 into the battery and 3500 into the car: the house
        // itself is drawing 800 W -- the Energy page's "other loads" residual, live (#255).
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with
        {
            SolarPowerWatts = 5000,
            GridPowerWatts = 300,
            BatteryPowerWatts = 1000,
            EvChargerPowerWatts = 3500,
        });

        var page = Render<Dashboard>();

        Assert.Contains("800 W", page.Find(".figure.house").TextContent);
    }

    [Fact]
    public void Shows_no_house_figure_when_the_sum_comes_out_below_zero()
    {
        // The grid meter is blind on one phase, so while the car draws, its load can be missing from
        // the sum and the house works out negative. That is a measurement gap, not a house exporting.
        _holder.Set(Statuses.Sample(_time.Now) with
        {
            SolarPowerWatts = 1351,
            GridPowerWatts = 2738,
            BatteryPowerWatts = -74,
            EvChargerPowerWatts = 4589,
        });

        var page = Render<Dashboard>();

        var house = page.Find(".figure.house").TextContent;
        Assert.Contains("—", house);
        Assert.Contains("not measurable", house);
        Assert.DoesNotContain("-", house);
    }

    [Fact]
    public void Leads_the_session_card_with_the_power_while_no_energy_is_counted()
    {
        // A car the charger started on plug-in draws power with nothing counted: a large 0.0 kWh would
        // read as broken.
        _holder.Set(Statuses.Sample(_time.Now) with { CarConnected = true, EvChargerPowerWatts = 4589 });

        var page = Render<Dashboard>();

        Assert.Equal("4.6 kW", page.Find(".session-energy").TextContent.Trim());
    }

    [Fact]
    public void Adds_todays_energy_when_the_energy_monitor_is_on()
    {
        // Two quarter hours since local midnight (Prague, so 22:00 UTC the day before).
        var store = new FakeEnergyIntervalStore();
        var midnight = new DateTimeOffset(2026, 8, 11, 22, 0, 0, TimeSpan.Zero);
        store.Intervals.Add(TestIntervals.Sample(midnight.AddHours(6), solarKwh: 1.2, evKwh: 0.5, gridImportKwh: 0.1, gridExportKwh: 0.2));
        store.Intervals.Add(TestIntervals.Sample(midnight.AddHours(7), solarKwh: 2.0, evKwh: 0.3, gridImportKwh: 0.1, gridExportKwh: 0.2));
        Services.AddSingleton<IEnergyIntervalStore>(store);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        page.WaitForAssertion(() => Assert.Contains("3.2 kWh", page.Find(".figure.solar").TextContent));
        Assert.Contains("800 Wh", page.Find(".figure.ev").TextContent);
        Assert.Contains("200 Wh in · 400 Wh out", page.Find(".figure.grid-flow").TextContent);
        Assert.DoesNotContain("needs the energy monitor", page.Markup);
    }

    [Fact]
    public void Says_today_needs_the_energy_monitor_when_it_is_off()
    {
        // The monitor can be switched off on its own; then the figures are power now, and say so
        // rather than showing a zero that reads as a dark day.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Contains("needs the energy monitor", page.Markup);
        Assert.DoesNotContain("kWh since midnight", page.Markup);
    }

    [Fact]
    public void Leaves_today_off_while_nothing_is_recorded_yet()
    {
        // The monitor is on but has no quarter hour for today yet: no line, rather than zeros that
        // read as a dark day -- and no claim that the monitor is off.
        Services.AddSingleton<IEnergyIntervalStore>(new FakeEnergyIntervalStore());
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.DoesNotContain("kWh since midnight", page.Markup);
        Assert.DoesNotContain("needs the energy monitor", page.Markup);
    }

    [Fact]
    public void Marks_the_driving_mode_among_links_into_the_plan_tabs()
    {
        // A way to the controls, not a copy of them (#98): every segment but Off is a link to its tab,
        // and the one driving is marked.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted));

        var page = Render<Dashboard>();

        var links = page.FindAll(".mode-seg a");
        Assert.Equal(
            ["/charging-plan/solar", "/charging-plan/forecasted", "/charging-plan/solar-grid", "/charging-plan/fast", "/charging-plan/targeted"],
            links.Select(a => a.GetAttribute("href")));
        Assert.Equal("Forecasted", page.Find(".mode-seg [aria-current=true]").TextContent.Trim());
    }

    [Fact]
    public void Marks_off_when_no_mode_is_driving()
    {
        _holder.Set(Statuses.Sample(_time.Now));

        var page = Render<Dashboard>();

        Assert.Equal("Off", page.Find(".mode-seg [aria-current=true]").TextContent.Trim());
        Assert.Contains("No mode is driving the charger", page.Find(".mode-summary").TextContent);
    }

    [Fact]
    public void Shows_the_discharge_hold_only_where_it_is_configured()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.FastNoBattery) with
        {
            BatteryHoldEnabled = true,
            BatteryHoldRequested = true,
            BatteryHoldActive = true,
            BatteryHoldTargetWatts = 0,
        });

        var page = Render<Dashboard>();

        var hold = page.Find("#battery-hold").TextContent;
        Assert.Contains("On", hold);
        Assert.Contains("armed on the inverter, holding at 0 W", hold);
    }

    [Fact]
    public void Leaves_the_hold_out_where_it_is_not_configured()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("#battery-hold"));
    }

    [Fact]
    public void Draws_todays_plan_while_forecasted_is_driving()
    {
        JSInterop.Mode = JSRuntimeMode.Loose;
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Forecasted) with { Plan = TestPlans.Usable(_time.Now) });

        var page = Render<Dashboard>();

        var card = page.Find("#todays-plan").TextContent;
        Assert.Contains("4.5 kWh", card);
        Assert.Contains("62%", card);
        Assert.Contains("1.0 kWh", card);
        var chart = Assert.Single(JSInterop.Invocations, i => i.Identifier == "solaxCharts.renderTimelineChart");
        Assert.Equal("overview-plan-chart", chart.Arguments[0]);

        // The plan's sentence leads the headline rather than repeating in the card.
        Assert.Contains("Today gives the car about 4.5 kWh", page.Find("#overview-headline").TextContent);
    }

    [Fact]
    public void Shows_no_plan_card_for_a_mode_that_publishes_no_plan()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Empty(page.FindAll("#todays-plan"));
    }

    /// <summary>The forecast table's cells for the row whose day cell starts with <paramref name="day"/>.</summary>
    private static string[] ForecastRow(IRenderedComponent<Dashboard> page, string day) =>
        [.. page.FindAll("#solar-forecast tbody tr")
            .Single(row => row.QuerySelector("td")!.TextContent.StartsWith(day, StringComparison.Ordinal))
            .QuerySelectorAll("td")
            .Select(cell => cell.TextContent.Trim())];

    [Fact]
    public void Tabulates_today_and_tomorrow_with_their_bands_and_peak()
    {
        _forecast.Days[new DateOnly(2026, 8, 12)] = new(ExpectedWh: 18_400, LowWh: 14_100, HighWh: 21_900, PeakWatts: 5_300);
        _forecast.Days[new DateOnly(2026, 8, 13)] = new(ExpectedWh: 3_100, LowWh: 1_200, HighWh: 6_400, PeakWatts: 1_800);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal(["Today Wed 12 Aug", "18.4", "14.1", "21.9", "5.3"], ForecastRow(page, "Today"));
        Assert.Equal(["Tomorrow Thu 13 Aug", "3.1", "1.2", "6.4", "1.8"], ForecastRow(page, "Tomorrow"));
    }

    [Fact]
    public void Takes_today_and_tomorrow_in_the_sites_zone_rather_than_utc()
    {
        // 22:30 UTC is half past midnight in Prague: the day the owner is living in is already the next.
        _time.Now = new DateTimeOffset(2026, 8, 12, 22, 30, 0, TimeSpan.Zero);
        _forecast.Days[new DateOnly(2026, 8, 12)] = new(18_400, 14_100, 21_900, 5_300);
        _forecast.Days[new DateOnly(2026, 8, 13)] = new(9_700, 8_000, 11_000, 3_200);
        _forecast.Days[new DateOnly(2026, 8, 14)] = new(21_300, 17_000, 24_000, 5_600);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal("9.7", ForecastRow(page, "Today")[1]);
        Assert.Equal("21.3", ForecastRow(page, "Tomorrow")[1]);
    }

    [Fact]
    public void Shows_dashes_for_a_day_no_forecast_reaches()
    {
        // Not 0.0: nothing fetched yet is a very different claim from a day the sun won't come up.
        _forecast.Days[new DateOnly(2026, 8, 12)] = new(18_400, 14_100, 21_900, 5_300);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal(["—", "—", "—", "—"], ForecastRow(page, "Tomorrow")[1..]);
    }

    [Fact]
    public void Picks_up_a_forecast_that_lands_after_the_page_opened()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        var page = Render<Dashboard>();

        _forecast.Days[new DateOnly(2026, 8, 12)] = new(18_400, 14_100, 21_900, 5_300);
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        page.WaitForAssertion(() => Assert.Equal("18.4", ForecastRow(page, "Today")[1]));
    }

    [Fact]
    public void Explains_the_forecast_columns_beneath_the_table()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        var note = page.Find("#solar-forecast").NextElementSibling!;
        Assert.Equal("P", note.TagName);
        Assert.Contains("kWh", note.TextContent);
        Assert.Contains("Low (p10)", note.TextContent);
        Assert.Contains("High (p90)", note.TextContent);
        Assert.Contains("Peak", note.TextContent);
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

    [Fact]
    public void Has_no_per_feed_section_even_when_two_sources_have_reported()
    {
        // #212: one car, one feed. "What each feed says" was the comparison instrument (#141/#180),
        // and the comparison is over.
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-22), SocPercent: 60, SourceId: "mqtt"));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-10), SocPercent: 62, SourceId: "vw-website"));

        var page = Render<Dashboard>();

        Assert.DoesNotContain("What each feed says", page.Markup);
        Assert.DoesNotContain("60%", page.Markup);
        Assert.DoesNotContain("/vehicle-feeds", page.Markup);
    }

    [Fact]
    public void A_lapsed_one_time_code_says_what_fixes_it_and_links_to_the_vehicle_page()
    {
        _car = Id4();
        WebsiteFeedConfigured(VehicleSourceHealth.NeedsOwner("volkswagen.de wants a one-time code."));
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        var blocked = page.Find("#vehicle-feed-blocked");
        Assert.Contains("Sign-in required", blocked.TextContent);
        Assert.Contains("sign in again on the Car page", blocked.TextContent);
        Assert.Equal("/car", blocked.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void A_feed_with_no_fix_sentence_of_its_own_shows_its_health_sentence()
    {
        _car = Id4();
        Feed(VehicleSourceHealth.NeedsOwner("The portal refused the sign-in: check the password."));
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        var blocked = page.Find("#vehicle-feed-blocked");
        Assert.Contains("check the password", blocked.TextContent);
        Assert.Equal("/car", blocked.QuerySelector("a")!.GetAttribute("href"));
    }

    [Fact]
    public void Health_comes_from_the_configured_feed_not_the_first_registered()
    {
        // The sign-in line used to be VehicleFeeds.FirstOrDefault() -- the first *registered* service,
        // which need not be the one behind the reading (#212).
        _car = Id4();
        Feed(VehicleSourceHealth.Ok("The portal answered."));
        Services.AddSingleton(new ConfiguredVehicleFeed(
            new WebsiteFeed(VehicleSourceHealth.NeedsOwner("volkswagen.de wants a one-time code."))));
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Contains("sign in again on the Car page", page.Find("#vehicle-feed-blocked").TextContent);
    }

    [Fact]
    public void Names_the_feed_in_the_owner_s_words()
    {
        _car = Id4();
        WebsiteFeedConfigured();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar) with { EvChargerPowerWatts = 7_000 });
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-5), SocPercent: 62, SourceId: "vw-website"));

        var page = Render<Dashboard>();

        var age = Prose(page);
        Assert.Contains("via volkswagen.de", age);
        Assert.DoesNotContain("via vw-website", age);
    }

    [Fact]
    public void An_old_reading_is_stale_whichever_feed_produced_it()
    {
        _car = Id4();
        Feed(VehicleSourceHealth.Ok("The portal answered."));
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Off));
        _vehicle.Set(new VehicleState(_time.Now.AddHours(-20), SocPercent: 41, SourceId: "vw-group ...1234"));

        var page = Render<Dashboard>();

        var age = page.Find("#vehicle-reading-age").TextContent;
        Assert.Contains("via Data Act portal", System.Text.RegularExpressions.Regex.Replace(age, @"\s+", " "));
        Assert.Contains("stale", age);
    }
}
