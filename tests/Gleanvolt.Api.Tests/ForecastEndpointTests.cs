using System.Text.Json;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The forecast endpoint, and the one property that makes it usable by a program: <b>the response
/// agrees with itself</b> (issue #221).
///
/// <para>It did not. The periods came from the live cache, which holds only what is still to come,
/// while the day totals came from the retained history, which holds the whole day — so a caller who
/// summed the periods for today got a smaller number than the response's own total beside it, and
/// <c>peakPowerWatts</c> fell towards zero as the afternoon wore on while the day's real peak stood.
/// The fake here holds both stores, so a test can put them out of step the only way reality does: two
/// refreshes.</para>
/// </summary>
public sealed class ForecastEndpointTests : IAsyncDisposable
{
    private readonly ApiTestHost _host = new();

    public ValueTask DisposeAsync() => _host.DisposeAsync();

    // Fixtures.Now is 2026-01-15 14:00 +01:00, which is the afternoon of the 15th in Prague -- late
    // enough that "this morning" is a thing the live cache can no longer report.
    private static readonly DateOnly Today = new(2026, 1, 15);
    private static readonly DateOnly Tomorrow = new(2026, 1, 16);

    /// <summary>A local Prague instant on a winter day (the offset is +1).</summary>
    private static DateTimeOffset Local(DateOnly day, int hour, int minute = 0) =>
        new(day.ToDateTime(new TimeOnly(hour, minute)), TimeSpan.FromHours(1));

    private static SolarForecast Refresh(DateTimeOffset at, params (DateTimeOffset End, double Watts)[] periods) =>
        new(at, [.. periods.Select(p => new SolarForecastPeriod(p.End, TimeSpan.FromMinutes(30), p.Watts, p.Watts * 0.6, p.Watts * 1.4))]);

    /// <summary>
    /// The morning fetched at dawn and then a midday one that no longer mentions it — the ordinary
    /// shape of a day, and the one the endpoint used to report two different answers about.
    /// </summary>
    private void TwoRefreshes()
    {
        _host.Forecast.Forecast = Refresh(
            Local(Today, 5, 30),
            (Local(Today, 9), 1000),
            (Local(Today, 12), 4000),
            (Local(Today, 15), 3000));

        _host.Forecast.Forecast = Refresh(
            Local(Today, 12, 10),
            (Local(Today, 15), 2000),
            (Local(Tomorrow, 12), 5000));
    }

    [Fact]
    public async Task The_periods_add_up_to_the_day_total_beside_them()
    {
        var client = await _host.StartAsync();
        TwoRefreshes();

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();

        var today = Day(body, Today);
        var summed = body.GetProperty("periods").EnumerateArray()
            .Where(p => OnDay(p, Today))
            .Sum(p => p.Number("energyWh"));

        // The property a caller can check, and the one the old shape broke: a day's periods are that
        // day. Not "the rest of it", which is a different question with its own field.
        Assert.Equal(today.Number("expectedWh"), summed, 3);
        Assert.Equal(body.Number("todayExpectedWh"), summed, 3);
    }

    [Fact]
    public async Task This_mornings_periods_survive_a_later_refresh()
    {
        var client = await _host.StartAsync();
        TwoRefreshes();

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();
        var starts = body.GetProperty("periods").EnumerateArray()
            .Select(p => p.GetProperty("periodStart").GetDateTimeOffset())
            .ToList();

        // 08:30 was forecast at dawn and never mentioned again. The provider has nothing more to say
        // about it; the controller does, and this is the endpoint that has to pass it on.
        Assert.Contains(Local(Today, 8, 30), starts);
        Assert.Contains(Local(Today, 11, 30), starts);
    }

    [Fact]
    public async Task The_peak_is_the_days_peak_and_not_the_peak_of_what_is_left()
    {
        var client = await _host.StartAsync();
        TwoRefreshes();

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();

        // Tomorrow's 5 kW is the strongest half hour returned. Against the live cache alone this was
        // whatever the afternoon had left, which shrank towards zero as the sun went down while the
        // day totals beside it went on reporting a day that peaked at four.
        Assert.Equal(5000, body.Number("peakPowerWatts"));
        Assert.Equal(4000, Day(body, Today).Number("peakWatts"));
    }

    [Fact]
    public async Task An_incomplete_day_says_so_and_says_where_it_starts()
    {
        var client = await _host.StartAsync();

        // A controller started at 10:44: nothing is retained for today before then.
        _host.Forecast.Forecast = Refresh(Local(Today, 10, 44), (Local(Today, 12), 4000));

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();
        var today = Day(body, Today);

        // A caller handed periods beginning at 11:30 with nothing to explain it reads the morning as
        // zero production and is wrong about the whole day. This is that explanation, as a field.
        Assert.False(today.GetProperty("complete").GetBoolean());
        Assert.Equal(Local(Today, 11, 30), today.GetProperty("heldFrom").GetDateTimeOffset());
    }

    [Fact]
    public async Task A_whole_day_is_complete_and_holds_nothing_back()
    {
        var client = await _host.StartAsync();

        // The first period of a whole day *ends* at its midnight, so it starts on the day before --
        // which must not be mistaken for a morning that is missing.
        _host.Forecast.Forecast = Refresh(
            Local(Today, 5, 30),
            (Local(Today, 0), 0),
            (Local(Today, 12), 4000));

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();
        var today = Day(body, Today);

        Assert.True(today.GetProperty("complete").GetBoolean());
        Assert.Equal(JsonValueKind.Null, today.GetProperty("heldFrom").ValueKind);
    }

    [Fact]
    public async Task Each_day_carries_the_bands_a_caller_would_otherwise_sum_for()
    {
        var client = await _host.StartAsync();
        _host.Forecast.Forecast = Refresh(Local(Today, 5, 30), (Local(Today, 12), 4000));

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();
        var today = Day(body, Today);

        // "How sure is it?" was answerable only by summing forty-eight periods; the top-level fields
        // carry the median alone.
        Assert.Equal(2000, today.Number("expectedWh"), 3);
        Assert.Equal(1200, today.Number("lowWh"), 3);
        Assert.Equal(2800, today.Number("highWh"), 3);
    }

    [Fact]
    public async Task A_day_nothing_is_held_for_is_absent_rather_than_zero()
    {
        var client = await _host.StartAsync();
        _host.Forecast.Forecast = Refresh(Local(Today, 5, 30), (Local(Today, 12), 4000));

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();

        // Past the provider's horizon is a day nothing is known about, not a day with no sun -- the
        // same reason GetDayForecast answers null rather than an empty curve.
        Assert.Single(body.GetProperty("days").EnumerateArray());
        Assert.Equal("2026-01-15", body.GetProperty("days")[0].Text("date"));
        Assert.Equal(JsonValueKind.Null, body.GetProperty("tomorrowExpectedWh").ValueKind);
    }

    [Fact]
    public async Task Nothing_held_at_all_is_an_empty_days_list_rather_than_a_day_of_zeroes()
    {
        var client = await _host.StartAsync();

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();

        Assert.Empty(body.GetProperty("days").EnumerateArray());
        Assert.Empty(body.GetProperty("periods").EnumerateArray());
        Assert.Equal(0, body.Number("peakPowerWatts"));
    }

    [Fact]
    public async Task What_is_left_of_today_is_still_what_is_left()
    {
        var client = await _host.StartAsync();
        TwoRefreshes();

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();

        // The one figure that is deliberately not a whole day. Only the 15:00 period is ahead of
        // 14:00, so it is 2000 W over half an hour -- not the day, and not the morning.
        Assert.Equal(1000, body.Number("todayRemainingWh"), 3);
        Assert.True(body.Number("todayExpectedWh") > body.Number("todayRemainingWh"));
    }

    [Fact]
    public async Task A_day_is_its_own_periods_and_not_the_other_days_too()
    {
        var client = await _host.StartAsync();
        TwoRefreshes();

        var body = await (await client.GetAsync("/api/v1/forecast")).ReadAsync();
        var periods = body.GetProperty("periods").EnumerateArray().ToList();

        // Three for today (09:00, 12:00, 15:00) and one for tomorrow, each appearing once: the days
        // are read separately and concatenated, so a period landing in both would double the total.
        Assert.Equal(4, periods.Count);
        Assert.Equal(3, periods.Count(p => OnDay(p, Today)));
        Assert.Equal(periods.Select(p => p.GetProperty("periodEnd").GetDateTimeOffset()).Distinct().Count(), periods.Count);

        // ...and oldest first, which is what the field has always promised.
        var ends = periods.Select(p => p.GetProperty("periodEnd").GetDateTimeOffset()).ToList();
        Assert.Equal(ends.Order(), ends);
    }

    private static JsonElement Day(JsonElement body, DateOnly date) =>
        body.GetProperty("days").EnumerateArray()
            .Single(d => d.Text("date") == date.ToString("yyyy-MM-dd"));

    /// <summary>Whether a period is counted under a day — by its end, as everything here counts it.</summary>
    private static bool OnDay(JsonElement period, DateOnly date) =>
        DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(
            period.GetProperty("periodEnd").GetDateTimeOffset(),
            TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague")).DateTime) == date;
}
