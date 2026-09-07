using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Phase 4 (#49): the session detail page -- the per-source split and, since #175, the chart of every
/// meter the session sampled. JSInterop is Loose: the chart itself is rendered by vendored JS (uPlot)
/// this suite cannot see, so these tests cover the data and markup around it, not the rendered pixels.
/// The arithmetic behind the picture is SessionChartSeriesTests' business.
/// </summary>
public class SessionDetailPageTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeChargingSessionStore _store = new();

    public SessionDetailPageTests()
    {
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IChargingSessionStore>(_store);
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private IRenderedComponent<SessionDetail> RenderFor(Guid id) =>
        Render<SessionDetail>(parameters => parameters.Add(p => p.Id, id));

    [Fact]
    public void Says_so_when_the_session_doesnt_exist()
    {
        var page = RenderFor(Guid.NewGuid());

        page.WaitForAssertion(() => Assert.Contains("No such session", page.Markup));
    }

    [Fact]
    public void Reports_when_the_store_is_unavailable_rather_than_throwing()
    {
        _store.Unavailable = true;

        var page = RenderFor(Guid.NewGuid());

        page.WaitForAssertion(() => Assert.Contains("isn't available right now", page.Markup));
    }

    [Fact]
    public void Shows_both_weather_readings_and_the_daylight_window()
    {
        var started = new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero);
        var session = TestSessions.Sample(startedAt: started, endedAt: started.AddHours(4)) with
        {
            WeatherAtStart = new WeatherObservation(started, 19.6, 1017, 51, 5, 10_000, "Clear", "clear sky"),
            WeatherAtEnd = new WeatherObservation(started.AddHours(4), 14.1, 1009, 76, 88, 4_000, "Rain", "light rain"),
            Sunrise = started.AddHours(-3),
            Sunset = started.AddHours(9),
        };
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("Weather", page.Markup));
        Assert.Contains("clear sky, 19.6 °C, 5% cloud", page.Markup);
        Assert.Contains("light rain, 14.1 °C, 88% cloud", page.Markup);

        // Local time, and the length beside it -- the denominator for "did it use the day it had?".
        Assert.Contains("Daylight", page.Markup);
        Assert.Contains("12h 0m", page.Markup);
    }

    [Fact]
    public void Leaves_the_weather_out_when_no_provider_was_configured()
    {
        var session = TestSessions.Sample(startedAt: new DateTimeOffset(2026, 8, 12, 9, 0, 0, TimeSpan.Zero));
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("Peak charging power", page.Markup));
        Assert.DoesNotContain("Weather", page.Markup);
        Assert.DoesNotContain("Daylight", page.Markup);
    }

    [Fact]
    public void Shows_the_days_forecast_with_the_band_behind_it()
    {
        var session = TestSessions.Sample(startedAt: new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero)) with
        {
            DayForecast = new SolarForecast(
                new DateTimeOffset(2026, 8, 12, 6, 0, 0, TimeSpan.Zero),
                [
                    new SolarForecastPeriod(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), 4_000, 3_000, 5_000),
                    new SolarForecastPeriod(new DateTimeOffset(2026, 8, 12, 13, 0, 0, TimeSpan.Zero), TimeSpan.FromHours(1), 6_000, 5_000, 7_000),
                ]),
        };
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("Forecast for the day", page.Markup));
        Assert.Contains("10.0 kWh", page.Markup);
        Assert.Contains("p10 8.0", page.Markup);
        Assert.Contains("p90 12.0", page.Markup);
    }

    [Fact]
    public void Leaves_the_forecast_row_out_when_the_day_was_never_recorded()
    {
        var session = TestSessions.Sample(startedAt: new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero));
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("Peak charging power", page.Markup));
        Assert.DoesNotContain("Forecast for the day", page.Markup);
    }

    [Fact]
    public void Shows_the_session_header_facts()
    {
        var session = TestSessions.Sample(
            startedAt: new DateTimeOffset(2026, 8, 12, 15, 0, 0, TimeSpan.Zero),
            endedAt: new DateTimeOffset(2026, 8, 12, 17, 30, 0, TimeSpan.Zero),
            startMode: ChargeControlMode.Forecasted);
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Forecasted", page.Markup);
            Assert.Contains("2h 30m", page.Markup);
            Assert.Contains("60", page.Markup); // start SOC
            Assert.Contains("75", page.Markup); // end SOC
        });
    }

    [Fact]
    public void Reports_an_open_session_as_in_progress()
    {
        var session = TestSessions.Sample(startedAt: _time.Now.AddMinutes(-10), endedAt: null);
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("in progress", page.Markup));
    }

    [Fact]
    public void Shows_the_per_source_energy_split()
    {
        var session = TestSessions.Sample(
            startedAt: _time.Now.AddHours(-1),
            endedAt: _time.Now,
            energyDeliveredWh: 6_000,
            fromSolarWh: 4_000,
            fromGridWh: 1_500,
            fromBatteryWh: 500);
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() =>
        {
            Assert.Contains("6.00 kWh delivered", page.Markup);
            Assert.Contains("Solar: 4.00 kWh", page.Markup);
            Assert.Contains("Grid: 1.50 kWh", page.Markup);
            Assert.Contains("Battery: 0.50 kWh", page.Markup);
        });
    }

    [Fact]
    public void Mentions_the_battery_loan_when_the_forecast_mode_used_one()
    {
        var session = TestSessions.Sample(startedAt: _time.Now.AddHours(-1), endedAt: _time.Now, loanedWh: 800);
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("0.80 kWh was loaned from the battery", page.Markup));
    }

    [Fact]
    public void Says_so_when_no_energy_was_delivered()
    {
        var session = TestSessions.Sample(
            startedAt: _time.Now.AddHours(-1),
            endedAt: _time.Now,
            energyDeliveredWh: 0,
            fromSolarWh: 0,
            fromGridWh: 0,
            fromBatteryWh: 0);
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("No energy was delivered", page.Markup));
    }

    [Fact]
    public void Renders_a_chart_container_when_samples_exist()
    {
        var session = TestSessions.Sample(startedAt: _time.Now.AddHours(-1), endedAt: _time.Now);
        var samples = new[]
        {
            TestSessions.Sample(session.Id, _time.Now.AddMinutes(-60), 60),
            TestSessions.Sample(session.Id, _time.Now.AddMinutes(-30), 68),
            TestSessions.Sample(session.Id, _time.Now, 75),
        };
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, samples, []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#session-chart")));
    }

    [Fact]
    public void Says_so_when_no_samples_were_recorded()
    {
        var session = TestSessions.Sample(startedAt: _time.Now.AddHours(-1), endedAt: _time.Now);
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [], []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Contains("No samples were recorded", page.Markup));
        Assert.Empty(page.FindAll("#session-chart"));
    }

    [Fact]
    public void Lists_the_moments_the_chart_rules_beside_it()
    {
        // The rules on the canvas are pixels this suite cannot read; the list under them is the same
        // set of moments in words, which is both what makes them legible and what can be asserted.
        var session = TestSessions.Sample(startedAt: _time.Now.AddHours(-1), endedAt: _time.Now);
        var samples = new[] { TestSessions.Sample(session.Id, _time.Now.AddMinutes(-30), 68) };
        var events = new[]
        {
            new ChargingSessionEvent(session.Id, _time.Now.AddMinutes(-60), ChargingSessionEventKind.SessionStarted, "Opened"),
            new ChargingSessionEvent(session.Id, _time.Now.AddMinutes(-20), ChargingSessionEventKind.ChargingPaused, "Surplus below the floor"),
        };
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, samples, events);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.Single(page.FindAll(".chart-marks li")));
        Assert.Contains("Surplus below the floor", page.Markup);
    }

    [Fact]
    public void Explains_the_cars_lag_only_when_the_car_reported_anything()
    {
        var session = TestSessions.Sample(startedAt: _time.Now.AddHours(-1), endedAt: _time.Now);
        var quiet = new[] { TestSessions.Sample(session.Id, _time.Now.AddMinutes(-30), 68) };
        _store.Sessions.Add(session);
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, quiet, []);

        var page = RenderFor(session.Id);

        page.WaitForAssertion(() => Assert.NotEmpty(page.FindAll("#session-chart")));
        Assert.DoesNotContain("Car SOC", page.Markup);

        var reported = TestSessions.Sample(session.Id, _time.Now.AddMinutes(-30), 68,
            vehicleSocPercent: 44, vehicleSocCapturedAt: _time.Now.AddHours(-4));
        _store.Documents[session.Id] = ChargingSessionDocument.Create(session, [reported], []);

        var withCar = RenderFor(session.Id);

        withCar.WaitForAssertion(() => Assert.Contains("Car SOC", withCar.Markup));
    }
}
