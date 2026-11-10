using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;
using Gleanvolt.Web.Tests;

namespace Gleanvolt.Web.Screenshots;

/// <summary>
/// The manual's history pictures (#329): Sessions, one session's page, Energy and Forecast, at 18:00 on
/// the same sunny June Sunday as the charging pictures. Today's session is the Forecasted charge from
/// 11:30 to 16:00; the week before holds a charge in each of the other modes.
/// </summary>
public sealed class HistoryScreenshots : ScreenshotTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    /// <summary>18:00 in Prague.</summary>
    private static readonly DateTimeOffset Now = new(2026, 6, 14, 16, 0, 0, TimeSpan.Zero);

    private static readonly DateOnly Today = new(2026, 6, 14);

    private static readonly Guid TodaysSession = new("6a1f0c2e-4b7d-4e19-9c3a-1d2e3f4a5b6c");

    private readonly FakeChargingSessionStore _sessions = new();
    private readonly FakeEnergyIntervalStore _energy = new();
    private readonly FakeSolarDayForecasts _forecasts = new();

    public HistoryScreenshots()
    {
        AddSessions();
        AddTodaysEnergy();
        AddForecasts();

        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        Services.AddSingleton<IChargingSessionStore>(_sessions);
        Services.AddSingleton<IEnergyIntervalStore>(_energy);
        Services.AddSingleton<ISolarForecastService>(_forecasts);
        SignedIn();
    }

    [Fact]
    public void The_sessions_list()
    {
        var page = RenderPage<Sessions>("/sessions");
        page.WaitForAssertion(() => Assert.Contains("Forecasted", page.Markup));

        Capture(page, "sessions", selector: Main);
    }

    [Fact]
    public void One_session()
    {
        var page = RenderPage<SessionDetail>("/sessions/" + TodaysSession, new Dictionary<string, object?> { [nameof(SessionDetail.Id)] = TodaysSession });
        page.WaitForAssertion(() => Assert.Contains("session-chart", page.Markup));

        Capture(page, "session-detail", selector: Main);
    }

    [Fact]
    public void The_energy_day()
    {
        var page = RenderPage<Energy>("/energy");
        page.WaitForAssertion(() => Assert.Contains("energy-chart", page.Markup));

        Capture(page, "energy", selector: Main, height: 1150);
    }

    [Fact]
    public void The_forecast()
    {
        Capture(RenderPage<Forecast>("/forecast"), "forecast", selector: Main, height: 1000);
    }

    private const string Main = "main.content";

    private static DateTimeOffset Local(DateOnly day, int hour, int minute = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(2));

    // The sun over a clear June day in Prague, in watts: up at 05:00, 7.4 kW at 13:15, down by 21:00.
    private static double Sun(DateTimeOffset at, double peak = 7_400)
    {
        var hour = TimeZoneInfo.ConvertTime(at, Prague).TimeOfDay.TotalHours;
        return hour is < 5 or > 21 ? 0 : Math.Round(peak * Math.Pow(Math.Sin((hour - 5) / 16 * Math.PI), 1.6));
    }

    private void AddSessions()
    {
        var today = TestSessions.Sample(
            Local(Today, 11, 30), Local(Today, 16), ChargeControlMode.Forecasted,
            energyDeliveredWh: 18_200, fromSolarWh: 16_900, fromGridWh: 300, fromBatteryWh: 1_000, loanedWh: 1_000) with
        {
            Id = TodaysSession,
            EndReason = ChargingSessionEndReason.NoSunLeftToday,
            StartBatterySocPercent = 64,
            EndBatterySocPercent = 86,
            PeakChargingPowerWatts = 6_600,
            WeatherAtStart = new WeatherObservation(Local(Today, 11, 30), 22.4, 101_800, 48, 5, 10_000, "Clear", "clear sky"),
            WeatherAtEnd = new WeatherObservation(Local(Today, 16), 26.1, 101_600, 39, 20, 10_000, "Clouds", "few clouds"),
            Sunrise = Local(Today, 4, 52),
            Sunset = Local(Today, 21, 11),
        };
        _sessions.Sessions.Add(today);

        // The week before: one of each other mode, oldest last.
        (int Day, int Hour, int Minutes, ChargeControlMode Mode, double Wh, double Solar, double Grid, ChargingSessionEndReason End, string Id)[] week =
        [
            (1, 21, 600, ChargeControlMode.Targeted, 22_000, 3_100, 18_900, ChargingSessionEndReason.SessionComplete, "1b2c3d4e-0000-4000-8000-000000000001"),
            (2, 12, 270, ChargeControlMode.SolarGrid, 14_100, 11_800, 2_300, ChargingSessionEndReason.NoSunLeftToday, "1b2c3d4e-0000-4000-8000-000000000002"),
            (3, 7, 55, ChargeControlMode.FastNoBattery, 9_800, 1_200, 8_600, ChargingSessionEndReason.SessionComplete, "1b2c3d4e-0000-4000-8000-000000000003"),
            (5, 13, 180, ChargeControlMode.Solar, 10_400, 10_400, 0, ChargingSessionEndReason.CarUnplugged, "1b2c3d4e-0000-4000-8000-000000000004"),
            (6, 10, 330, ChargeControlMode.Forecasted, 21_500, 20_100, 0, ChargingSessionEndReason.NoSunLeftToday, "1b2c3d4e-0000-4000-8000-000000000005"),
        ];

        foreach (var (day, hour, minutes, mode, wh, solar, grid, end, id) in week)
        {
            var start = Local(Today.AddDays(-day), hour);
            _sessions.Sessions.Add(TestSessions.Sample(start, start.AddMinutes(minutes), mode,
                energyDeliveredWh: wh, fromSolarWh: solar, fromGridWh: grid, fromBatteryWh: wh - solar - grid) with
            {
                Id = new Guid(id),
                EndReason = end,
            });
        }

        // Today's session every five minutes: the car following the sun, the battery filling beside it.
        var samples = new List<ChargingSessionSample>();
        for (var at = Local(Today, 11, 30); at <= Local(Today, 16); at = at.AddMinutes(5))
        {
            var sun = Sun(at);
            var car = Math.Clamp(Math.Floor((sun - 1_900) / 690) * 690, 4_140, 6_600);
            var battery = Math.Round(sun - 1_900 - car);
            var soc = Math.Round(64 + (at - Local(Today, 11, 30)).TotalHours * 4.9, 1);
            samples.Add(TestSessions.Sample(TodaysSession, at, soc,
                solarPowerWatts: sun, gridPowerWatts: battery < 0 ? 0 : 0, batteryPowerWatts: battery,
                evChargerPowerWatts: car, forecastPowerWatts: Sun(at, 7_100), surplusWatts: sun - 1_900,
                loanPowerWatts: battery < 0 ? -battery : 0, planRequiredSocFloorPercent: Math.Round(50 + (at - Local(Today, 11, 30)).TotalHours * 5.5))
                with { Mode = ChargeControlMode.Forecasted });
        }

        _sessions.Documents[TodaysSession] = ChargingSessionDocument.Create(today, samples, []);
    }

    private void AddTodaysEnergy()
    {
        var soc = 31.0;
        for (var start = Local(Today, 0); start < Now; start = start.AddMinutes(15))
        {
            var solar = Sun(start.AddMinutes(7.5)) / 4_000;
            var house = 0.12 + (start.Hour is >= 4 and < 6 ? 0.15 : 0);
            var car = start >= Local(Today, 11, 30) && start < Local(Today, 16) ? Math.Clamp(Sun(start) - 1_900, 4_140, 6_600) / 4_000 : 0;
            var spare = solar - house - car;
            var charge = spare > 0 && soc < 100 ? Math.Min(spare, 1.3) : 0;
            var discharge = spare < 0 && soc > 20 && car == 0 ? Math.Min(-spare, 0.25) : 0;
            var import = Math.Max(0, -spare - discharge);
            var export = Math.Max(0, spare - charge);
            var socEnd = Math.Clamp(soc + (charge - discharge) / 11.6 * 100, 0, 100);
            _energy.Intervals.Add(TestIntervals.Sample(
                start, solarKwh: Math.Round(solar, 3), forecastSolarKwh: Math.Round(Sun(start.AddMinutes(7.5), 7_100) / 4_000, 3),
                gridImportKwh: Math.Round(import, 3), gridExportKwh: Math.Round(export, 3), evKwh: Math.Round(car, 3),
                batteryChargeKwh: Math.Round(charge, 3), batteryDischargeKwh: Math.Round(discharge, 3),
                socStartPercent: Math.Round(soc), socEndPercent: Math.Round(socEnd)));
            soc = socEnd;
        }
    }

    private void AddForecasts()
    {
        foreach (var (day, peak) in new[] { (Today, 7_100.0), (Today.AddDays(1), 5_200.0) })
        {
            var watts = new List<double>();
            for (var end = Local(day, 5, 30); end <= Local(day, 21); end = end.AddMinutes(30))
            {
                watts.Add(Sun(end.AddMinutes(-15), peak));
            }

            _forecasts.Days[day] = TestForecasts.Run(Local(Today, 13, 15), Local(day, 5, 30), [.. watts]);
        }
    }
}
