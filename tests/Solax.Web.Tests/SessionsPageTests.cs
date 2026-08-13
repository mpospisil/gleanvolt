using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Solax.Core.Enums;
using Solax.Core.Interfaces;
using Solax.Web.Components.Pages;

namespace Solax.Web.Tests;

/// <summary>Phase 4 (#49): the session list, the first way to browse what SessionStore has recorded.</summary>
public class SessionsPageTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 18, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeChargingSessionStore _store = new();

    public SessionsPageTests()
    {
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IChargingSessionStore>(_store);
    }

    [Fact]
    public void Says_so_when_there_are_no_sessions_in_range()
    {
        var page = Render<Sessions>();

        page.WaitForAssertion(() => Assert.Contains("No sessions in this range", page.Markup));
    }

    [Fact]
    public void Lists_a_closed_session_with_its_headline_figures()
    {
        _store.Sessions.Add(TestSessions.Sample(
            startedAt: _time.Now.AddHours(-2),
            endedAt: _time.Now.AddHours(-1),
            startMode: ChargeControlMode.Forecasted,
            energyDeliveredWh: 6_000,
            fromSolarWh: 4_500,
            fromGridWh: 1_000,
            fromBatteryWh: 500));

        var page = Render<Sessions>();

        page.WaitForAssertion(() =>
        {
            Assert.Contains("Forecasted", page.Markup);
            Assert.Contains("6.00 kWh", page.Markup);
            Assert.Contains("75%", page.Markup); // 4500 / 6000 solar share
            Assert.Contains("1h 0m", page.Markup);
        });
    }

    [Fact]
    public void Marks_an_open_session_as_in_progress()
    {
        _store.Sessions.Add(TestSessions.Sample(startedAt: _time.Now.AddMinutes(-20), endedAt: null));

        var page = Render<Sessions>();

        page.WaitForAssertion(() => Assert.Contains("in progress", page.Markup));
    }

    [Fact]
    public void Shows_a_session_that_changed_mode_as_a_transition()
    {
        _store.Sessions.Add(TestSessions.Sample(
            startedAt: _time.Now.AddHours(-1),
            endedAt: _time.Now,
            startMode: ChargeControlMode.Forecasted,
            endMode: ChargeControlMode.FastNoBattery));

        var page = Render<Sessions>();

        page.WaitForAssertion(() => Assert.Contains("Forecasted → FastNoBattery", page.Markup));
    }

    [Fact]
    public void Excludes_sessions_outside_the_selected_range()
    {
        _store.Sessions.Add(TestSessions.Sample(startedAt: _time.Now.AddDays(-60), endedAt: _time.Now.AddDays(-60).AddHours(1)));

        var page = Render<Sessions>();

        // Default range is 30 days.
        page.WaitForAssertion(() => Assert.Contains("No sessions in this range", page.Markup));

        page.Find("#range").Change("90");

        page.WaitForAssertion(() => Assert.DoesNotContain("No sessions in this range", page.Markup));
    }

    [Fact]
    public void Reports_when_the_store_is_unavailable_rather_than_throwing()
    {
        _store.Unavailable = true;

        var page = Render<Sessions>();

        page.WaitForAssertion(() => Assert.Contains("isn't available right now", page.Markup));
    }

    [Fact]
    public void Links_each_session_to_its_detail_page()
    {
        var session = TestSessions.Sample(startedAt: _time.Now.AddHours(-1), endedAt: _time.Now);
        _store.Sessions.Add(session);

        var page = Render<Sessions>();

        page.WaitForAssertion(() =>
        {
            var link = page.Find("a[href^='/sessions/']");
            Assert.Equal($"/sessions/{session.Id}", link.GetAttribute("href"));
        });
    }
}
