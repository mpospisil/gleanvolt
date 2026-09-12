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

        // The per-feed sections read the holder's own tally (#141), so it has to be the same object
        // the tests push readings into -- a second comparison would see nothing.
        Services.AddSingleton(_vehicle.Comparison);
        Services.AddSingleton(new VehicleDisplayOptions(TimeSpan.FromHours(12)));
        Services.AddSingleton(_ => _car);
    }

    private static EvInfo Id4() => new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77, 0.9, 3, 6, 16, "gleanvolt/vehicle/id4/state");

    /// <summary>
    /// A manufacturer feed that only ever answers "here is how I am". Fetching throws on purpose: the
    /// dashboard reads <see cref="IVehicleUpdateService.Health"/> on a render and must never do I/O
    /// on one.
    /// </summary>
    private sealed class StubFeed(VehicleSourceHealth health) : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-group";

        public VehicleSourceHealth Health => health;

        public TimeSpan NextDelay => TimeSpan.FromMinutes(15);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private void Feed(VehicleSourceHealth health) =>
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed(health));

    /// <summary>volkswagen.de's shape: asked only while a charge runs (#170/#180).</summary>
    private sealed class ChargeGatedFeed : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-website";

        public VehicleSourceHealth Health => VehicleSourceHealth.Ok("used while a charge is running");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(1);

        public bool DeliversOnlyWhileCharging => true;

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private void ChargeGatedFeedConfigured() =>
        Services.AddSingleton<IVehicleUpdateService>(new ChargeGatedFeed());

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
        _car = EvInfo.Unknown with { BatteryCapacityKWh = 77 };
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
        Assert.Contains("vw-group", page.Markup);
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

    /// <summary>Today's whole-day forecast tile, by its label.</summary>
    private static AngleSharp.Dom.IElement TodayForecastTile(IRenderedComponent<Dashboard> page) =>
        page.FindAll(".stat").Single(s => s.QuerySelector(".label")!.TextContent == "Forecast solar today");

    [Fact]
    public void Shows_the_whole_of_todays_forecast_in_kilowatt_hours()
    {
        _forecast.DayTotals[new DateOnly(2026, 8, 12)] = 18_400;
        _forecast.DayTotals[new DateOnly(2026, 8, 13)] = 3_100;
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal("18.4 kWh", TodayForecastTile(page).QuerySelector(".value")!.TextContent);
    }

    [Fact]
    public void Takes_today_in_the_sites_zone_rather_than_utc()
    {
        // 22:30 UTC is half past midnight in Prague: the day the owner is living in is already the next.
        _time.Now = new DateTimeOffset(2026, 8, 12, 22, 30, 0, TimeSpan.Zero);
        _forecast.DayTotals[new DateOnly(2026, 8, 12)] = 18_400;
        _forecast.DayTotals[new DateOnly(2026, 8, 13)] = 9_700;
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal("9.7 kWh", TodayForecastTile(page).QuerySelector(".value")!.TextContent);
    }

    [Fact]
    public void Shows_a_dash_for_today_while_no_forecast_is_held()
    {
        // Not 0 kWh: nothing fetched yet is a very different claim from a day the sun won't come up.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        var page = Render<Dashboard>();

        Assert.Equal("—", TodayForecastTile(page).QuerySelector(".value")!.TextContent);
    }

    [Fact]
    public void Picks_up_a_forecast_that_lands_after_the_page_opened()
    {
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        var page = Render<Dashboard>();

        _forecast.DayTotals[new DateOnly(2026, 8, 12)] = 18_400;
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        page.WaitForAssertion(() =>
            Assert.Equal("18.4 kWh", TodayForecastTile(page).QuerySelector(".value")!.TextContent));
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
    public void Says_nothing_per_feed_while_only_one_has_ever_reported()
    {
        // With one feed the card above already is that feed. A section repeating it would be the
        // dashboard saying the same thing twice.
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-10), SocPercent: 62, SourceId: "id4"));

        var page = Render<Dashboard>();

        Assert.DoesNotContain("What each feed says", page.Markup);
    }

    [Fact]
    public void Gives_each_feed_its_own_section_once_two_have_reported()
    {
        // The reason this exists: the holder keeps one state, so the feed that came second leaves
        // nothing behind but a count -- and "the portal is healthy" and "the portal is delivering"
        // are then indistinguishable from the card above.
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-10), SocPercent: 62, SourceId: "id4"));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-22), SocPercent: 60, SourceId: "vw-group"));

        var page = Render<Dashboard>();

        Assert.Contains("What each feed says", page.Markup);
        Assert.Contains("id4", page.Markup);
        Assert.Contains("vw-group", page.Markup);

        // Both accounts are on the page, including the one the holder threw away.
        Assert.Contains("62%", page.Markup);
        Assert.Contains("60%", page.Markup);
    }

    [Fact]
    public void Names_the_feed_the_card_above_is_quoting()
    {
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-10), SocPercent: 62, SourceId: "id4"));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-22), SocPercent: 60, SourceId: "vw-group"));

        var page = Render<Dashboard>();

        Assert.Contains("the reading shown above", page.Markup);
        Assert.Contains("held back as older", page.Markup);
    }

    [Fact]
    public void A_feed_that_has_gone_quiet_keeps_its_section_and_shows_its_age_growing()
    {
        // The one thing worth looking at after a feed stops: its own last reading, ageing, beside the
        // other feed's fresh one. A section that vanished would take the evidence with it.
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddHours(-9), SocPercent: 55, SourceId: "vw-group"));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-5), SocPercent: 62, SourceId: "id4"));

        var page = Render<Dashboard>();

        Assert.Contains("What each feed says", page.Markup);
        Assert.Contains("9.0 h", page.Markup);
    }

    [Fact]
    public void Shows_a_field_one_feed_carries_and_the_other_does_not()
    {
        // Coverage, on the card rather than only in the week's tally: the portal carries a
        // charge-time-remaining for the reference car and the MQTT feed does not, and a dash beside a
        // figure is how that is seen at a glance.
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-10), SocPercent: 62, SourceId: "id4"));
        _vehicle.Set(new VehicleState(
            _time.Now.AddMinutes(-22),
            SocPercent: 60,
            ChargeTimeRemaining: TimeSpan.FromMinutes(95),
            SourceId: "vw-group"));

        var page = Render<Dashboard>();

        Assert.Contains("Time left", page.Markup);
        Assert.Contains("95 min", page.Markup);
    }

    /// <summary>
    /// The re-framing #180 asks for. "What each feed says" was written for portal-against-MQTT,
    /// where the open question was which of the two was right. Portal-against-volkswagen.de is a
    /// different comparison: they carry different fields on different clocks, and one is silent
    /// unless the car is charging — so a section that still invited the old reading would have an
    /// owner diagnosing a dropout every time the car sat on the drive.
    /// </summary>
    [Fact]
    public void Presents_the_per_feed_sections_as_complementary_rather_than_a_contest()
    {
        _car = Id4();
        ChargeGatedFeedConfigured();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddHours(-9), SocPercent: 55, SourceId: "vw-website"));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-5), SocPercent: 62, SourceId: "vw-group …1234"));

        var page = Render<Dashboard>();

        Assert.Contains("What each feed says", page.Markup);
        Assert.Contains("Not a contest between them", Prose(page));

        // The nine-hour age is the car being parked, and the marker is what says so.
        Assert.Contains("while charging only", Prose(page));
        Assert.DoesNotContain("which is right is what a week of both answers", Prose(page));
    }

    /// <summary>
    /// The safe way round: a feed on its own clock gets no marker, because its growing age really is
    /// something to look into.
    /// </summary>
    [Fact]
    public void Leaves_a_feed_on_its_own_clock_unmarked()
    {
        _car = Id4();
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));
        _vehicle.Set(new VehicleState(_time.Now.AddHours(-9), SocPercent: 55, SourceId: "vw-group"));
        _vehicle.Set(new VehicleState(_time.Now.AddMinutes(-5), SocPercent: 62, SourceId: "id4"));

        var page = Render<Dashboard>();

        Assert.Contains("What each feed says", page.Markup);
        Assert.DoesNotContain("while charging only", Prose(page));
    }
}
