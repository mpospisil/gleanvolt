using System.Text.Json;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The arithmetic behind the session chart on /sessions/{id} (#175). The samples are already watts,
/// so the conversion the day chart needed is not the risk here; the three ways this one can quietly
/// lie are — drawing a line across a stretch nobody sampled, putting the car's SOC at the time we
/// heard it rather than the time it was measured, and shading a hold band over samples that never
/// showed one.
/// </summary>
public class SessionChartSeriesTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly DateTimeOffset Started = new(2026, 8, 12, 12, 0, 0, TimeSpan.FromHours(2));
    private static readonly DateTimeOffset Now = Started.AddHours(3);

    private static readonly Guid SessionId = Guid.NewGuid();

    private static DateTimeOffset At(double minutes) => Started.AddMinutes(minutes);

    private static SessionChartSeries Build(
        IEnumerable<ChargingSessionSample> samples,
        IEnumerable<ChargingSessionEvent>? events = null,
        DateTimeOffset? endedAt = null)
    {
        var session = TestSessions.Sample(Started, endedAt) with { Id = SessionId };
        var document = ChargingSessionDocument.Create(session, [.. samples], [.. events ?? []]);

        return SessionChartSeries.From(document, Prague, Now);
    }

    private static ChargingSessionSample Sample(
        double minutes,
        double batterySocPercent = 60,
        double solarPowerWatts = 2_000,
        double gridPowerWatts = 0,
        double batteryPowerWatts = 0,
        double evChargerPowerWatts = 2_000,
        double? forecastPowerWatts = null,
        double? vehicleSocPercent = null,
        DateTimeOffset? vehicleSocCapturedAt = null,
        bool batteryHoldActive = false) =>
        TestSessions.Sample(
            SessionId,
            At(minutes),
            batterySocPercent,
            solarPowerWatts,
            gridPowerWatts,
            batteryPowerWatts,
            evChargerPowerWatts,
            forecastPowerWatts,
            vehicleSocPercent,
            vehicleSocCapturedAt,
            batteryHoldActive);

    [Fact]
    public void The_meters_are_drawn_with_the_signs_the_sample_recorded()
    {
        // Exporting 1.4 kW while the battery discharges 800 W: both belong below the line, and the
        // sample already says so. Nothing here re-derives a sign the store settled.
        var series = Build([Sample(0, gridPowerWatts: -1_400, batteryPowerWatts: -800, solarPowerWatts: 3_100)]);

        Assert.Equal(-1_400, series.Grid[0]);
        Assert.Equal(-800, series.Battery[0]);
        Assert.Equal(3_100, series.Solar[0]);
        Assert.Equal(At(0).ToUnixTimeSeconds(), series.Timestamps[0]);
    }

    [Fact]
    public void Samples_a_minute_apart_are_one_unbroken_line()
    {
        var series = Build([Sample(0), Sample(0.5), Sample(1)]);

        Assert.Equal(3, series.Timestamps.Length);
        Assert.All(series.Solar, w => Assert.NotNull(w));
    }

    [Fact]
    public void A_stretch_with_no_samples_becomes_a_hole_rather_than_a_slope()
    {
        // Twenty minutes without a row is the service not running, not the roof holding steady. The
        // readings either side are both true; the straight line between them would be an invention.
        var series = Build([Sample(0, solarPowerWatts: 4_000), Sample(20, solarPowerWatts: 1_000)]);

        Assert.Equal(3, series.Timestamps.Length);
        Assert.Equal(4_000, series.Solar[0]);
        Assert.Null(series.Solar[1]);
        Assert.Null(series.Grid[1]);
        Assert.Null(series.Soc[1]);
        Assert.Equal(1_000, series.Solar[2]);

        // ...and the hole opens a second after the last reading, not halfway across the gap: the line
        // is true right up to where it stops.
        Assert.Equal(At(0).ToUnixTimeSeconds() + 1, series.Timestamps[1]);
    }

    [Fact]
    public void The_cars_line_carries_on_across_a_gap_the_meters_do_not()
    {
        // The one series that survives a hole, because it is the only one not measured by us: the car
        // timestamps its own readings, and one taken before the outage is still where the car was
        // during it. The meters have nothing to say about a stretch nobody sampled; the car does.
        var series = Build(
        [
            Sample(0, vehicleSocPercent: 41, vehicleSocCapturedAt: At(0)),
            Sample(30, vehicleSocPercent: 41, vehicleSocCapturedAt: At(0)),
        ]);

        Assert.Null(series.Solar[1]);
        Assert.Equal(41, series.VehicleSoc[1]);
    }

    [Fact]
    public void The_cars_soc_steps_where_the_car_measured_it_not_where_we_heard_it()
    {
        // The feed lags: three samples in, the car is still repeating what it captured an hour before
        // the session opened, and the reading it took at 12:02 only reaches us at 12:03. Drawn against
        // the sample's own timestamp, this session would claim the car gained nothing until 12:03.
        var early = Started.AddHours(-1);
        var later = At(2);

        var series = Build(
        [
            Sample(0, vehicleSocPercent: 41, vehicleSocCapturedAt: early),
            Sample(1, vehicleSocPercent: 41, vehicleSocCapturedAt: early),
            Sample(2, vehicleSocPercent: 41, vehicleSocCapturedAt: early),
            Sample(3, vehicleSocPercent: 55, vehicleSocCapturedAt: later),
        ]);

        Assert.True(series.HasVehicleSoc);

        // A capture from before the session still stands at its start: it is where the car was.
        Assert.Equal(41, series.VehicleSoc[0]);
        Assert.Equal(41, series.VehicleSoc[1]);

        // The step lands on the sample at 12:02 -- the capture time -- and not on the 12:03 one that
        // carried the news.
        Assert.Equal(55, series.VehicleSoc[2]);
        Assert.Equal(55, series.VehicleSoc[3]);
    }

    [Fact]
    public void A_car_that_has_not_spoken_yet_has_not_said_zero()
    {
        var series = Build(
        [
            Sample(0),
            Sample(1, vehicleSocPercent: 62, vehicleSocCapturedAt: At(1)),
        ]);

        Assert.Null(series.VehicleSoc[0]);
        Assert.Equal(62, series.VehicleSoc[1]);
    }

    [Fact]
    public void A_session_the_car_never_reported_in_gets_no_car_line_at_all()
    {
        var series = Build([Sample(0), Sample(1)]);

        Assert.False(series.HasVehicleSoc);
        Assert.All(series.VehicleSoc, v => Assert.Null(v));

        // Same rule for the forecast: no forecast is a different fact from a flat zero, and the
        // legend should not offer a line that is nothing but a gap.
        Assert.False(series.HasForecast);

        Assert.True(Build([Sample(0, forecastPowerWatts: 2_400)]).HasForecast);
    }

    [Fact]
    public void The_hold_is_one_band_over_the_samples_that_showed_it_armed()
    {
        var series = Build(
        [
            Sample(0),
            Sample(1, batteryHoldActive: true),
            Sample(2, batteryHoldActive: true),
            Sample(3),
            Sample(4, batteryHoldActive: true),
        ]);

        Assert.Equal([At(1).ToUnixTimeSeconds(), At(4).ToUnixTimeSeconds()], series.HoldStarts);
        Assert.Equal([At(2).ToUnixTimeSeconds(), At(4).ToUnixTimeSeconds()], series.HoldEnds);
    }

    [Fact]
    public void A_hold_band_stops_at_a_recording_gap_rather_than_spanning_it()
    {
        // The hold may well have stayed armed across the restart. Nothing watched it, and a band is a
        // claim about what the pack was doing underneath it.
        var series = Build([Sample(0, batteryHoldActive: true), Sample(30, batteryHoldActive: true)]);

        Assert.Equal(2, series.HoldStarts.Length);
        Assert.Equal(At(0).ToUnixTimeSeconds(), series.HoldEnds[0]);
        Assert.Equal(At(30).ToUnixTimeSeconds(), series.HoldStarts[1]);
    }

    [Fact]
    public void Only_the_moments_that_explain_the_shape_get_a_rule()
    {
        var series = Build(
            [Sample(0)],
            [
                Event(0, ChargingSessionEventKind.SessionStarted, "Session opened"),
                Event(5, ChargingSessionEventKind.TargetCurrentChanged, "10 A → 8 A"),
                Event(10, ChargingSessionEventKind.ChargingPaused, "Surplus below the floor"),
                Event(12, ChargingSessionEventKind.BatteryHoldArmed, "Hold armed"),
                Event(20, ChargingSessionEventKind.ModeChanged, "Solar → FastNoBattery"),
            ]);

        // The setpoint moves most cycles on a sunny afternoon, and a chart ruled once a minute is a
        // hatched chart. The hold has its own band, and the session's ends are the chart's own edges.
        Assert.Equal([At(10).ToUnixTimeSeconds(), At(20).ToUnixTimeSeconds()], series.MarkTimes);
        Assert.Equal(["Surplus below the floor", "Solar → FastNoBattery"], series.MarkLabels);
    }

    [Fact]
    public void An_event_with_nothing_written_on_it_still_says_what_it_was()
    {
        var series = Build([Sample(0)], [Event(10, ChargingSessionEventKind.PlanUnusable, "")]);

        Assert.Equal(["Plan unusable"], series.MarkLabels);
    }

    [Fact]
    public void A_running_session_is_drawn_up_to_now_and_a_finished_one_up_to_its_end()
    {
        Assert.Equal(Now.ToUnixTimeSeconds(), Build([Sample(0)]).WindowEnd);

        var finished = Build([Sample(0)], endedAt: At(90));
        Assert.Equal(Started.ToUnixTimeSeconds(), finished.WindowStart);
        Assert.Equal(At(90).ToUnixTimeSeconds(), finished.WindowEnd);

        // A sample past the recorded end -- a clock that moved, a session closed from a stale status --
        // still gets drawn rather than falling off the right edge.
        Assert.Equal(At(120).ToUnixTimeSeconds(), Build([Sample(120)], endedAt: At(90)).WindowEnd);
    }

    [Fact]
    public void The_payload_carries_the_field_names_the_chart_reads()
    {
        // The only contract between this record and charts.js, and nothing else checks it: the field
        // names uPlot is handed are whatever Blazor's interop serialiser makes of the properties, and
        // a rename here would leave the chart silently empty rather than failing a build.
        var series = Build([Sample(0)]);

        var json = JsonSerializer.Serialize(series, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using var document = JsonDocument.Parse(json);

        foreach (var field in new[]
                 {
                     "timestamps", "solar", "forecast", "grid", "battery", "ev", "soc", "vehicleSoc",
                     "holdStarts", "holdEnds", "markTimes", "markLabels", "windowStart", "windowEnd",
                     "timeZoneId", "hasForecast", "hasVehicleSoc",
                 })
        {
            Assert.True(document.RootElement.TryGetProperty(field, out _), $"the chart reads session.{field}");
        }

        // IANA, because the browser reads it through Intl -- which has never heard of "Central Europe
        // Standard Time".
        Assert.Equal("Europe/Prague", series.TimeZoneId);
    }

    [Fact]
    public void A_session_with_nothing_sampled_draws_nothing_but_still_knows_its_window()
    {
        var series = Build([]);

        Assert.Empty(series.Timestamps);
        Assert.Empty(series.HoldStarts);
        Assert.False(series.HasVehicleSoc);
        Assert.Equal(Started.ToUnixTimeSeconds(), series.WindowStart);
    }

    private static ChargingSessionEvent Event(double minutes, ChargingSessionEventKind kind, string detail) =>
        new(SessionId, At(minutes), kind, detail);
}
