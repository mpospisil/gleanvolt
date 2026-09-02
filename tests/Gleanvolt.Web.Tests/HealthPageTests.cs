using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The health page is phase 0's only page, and the thing it has to get right is the seam: it reads
/// the same <see cref="ChargeControlStatusHolder"/> singleton the polling loop writes to, and it
/// follows that holder rather than sampling it once.
/// </summary>
public class HealthPageTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly ChargeControlStatusHolder _holder = new();
    private readonly FixedTimeProvider _time = new(new DateTimeOffset(2026, 8, 12, 10, 0, 0, TimeSpan.Zero), Prague);
    private readonly FakeServiceShutdown _shutdown = new();

    public HealthPageTests()
    {
        Services.AddSingleton(_holder);
        Services.AddSingleton(new WebBuildInfo("1.4.2 (31bf347)"));
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IServiceShutdown>(_shutdown);
    }

    /// <summary>A feed that only ever reports how it is; the page must never make it fetch.</summary>
    private sealed class StubFeed(VehicleSourceHealth health) : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-group";

        public VehicleSourceHealth Health => health;

        public TimeSpan NextDelay => TimeSpan.FromMinutes(15);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    [Fact]
    public void Says_nothing_about_a_car_feed_that_is_not_configured()
    {
        // "Is anything wrong?" must not grow a row saying "no" for a feature nobody has turned on.
        Assert.DoesNotContain("Car feed", Render<Health>().Markup);
    }

    [Fact]
    public void Reports_a_feed_that_needs_the_owner_as_the_thing_that_will_not_fix_itself()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed(
            VehicleSourceHealth.NeedsOwner("The portal is showing a consent screen; open it in a browser.")));

        var page = Render<Health>();

        Assert.Contains("Car feed", page.Markup);
        Assert.Contains("NeedsOwner", page.Markup);
        Assert.Contains("consent screen", page.Markup);
        Assert.Contains("/vehicle-portal", page.Markup);
    }

    [Fact]
    public void Reports_a_healthy_feed_without_offering_a_remedy()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed(
            VehicleSourceHealth.Ok("The portal answered; the car reported at 09:45.")));

        var page = Render<Health>();

        Assert.Contains("Car feed", page.Markup);
        Assert.Contains("reported at 09:45", page.Markup);
        Assert.DoesNotContain("/vehicle-portal", page.Markup);
    }

    [Fact]
    public void Reports_the_build_the_host_handed_it()
    {
        var page = Render<Health>();

        Assert.Contains("1.4.2 (31bf347)", page.Markup);
    }

    [Fact]
    public void Reports_the_configured_zone_rather_than_the_machine_one()
    {
        var page = Render<Health>();

        Assert.Contains("Europe/Prague", page.Markup);
    }

    [Fact]
    public void Says_so_before_the_first_poll_has_landed()
    {
        var page = Render<Health>();

        Assert.Contains("no poll has completed yet", page.Markup);
    }

    [Fact]
    public void Shows_a_status_that_arrived_before_the_page_was_opened()
    {
        // 09:59:30 UTC is 11:59:30 in Prague in August. A page that prints 09:59:30 is formatting in
        // UTC, which is the bug this test exists for.
        _holder.Set(Statuses.Sample(
            new DateTimeOffset(2026, 8, 12, 9, 59, 30, TimeSpan.Zero),
            ChargeControlMode.Forecasted));

        var page = Render<Health>();

        Assert.Contains("2026-08-12 11:59:30", page.Markup);
        Assert.Contains("30 s ago", page.Markup);
        Assert.Contains("Forecasted", page.Markup);
    }

    [Fact]
    public void Follows_the_holder_instead_of_sampling_it_once()
    {
        var page = Render<Health>();
        Assert.Contains("no poll has completed yet", page.Markup);

        // What the polling loop does, one cycle later. Nothing here re-renders the page: if the
        // subscription is missing, the markup below is still the empty one.
        _holder.Set(Statuses.Sample(_time.Now, ChargeControlMode.Solar));

        page.WaitForAssertion(() => Assert.Contains("Solar", page.Markup));
        Assert.DoesNotContain("no poll has completed yet", page.Markup);
    }

    [Fact]
    public void Stops_following_the_holder_once_the_circuit_is_gone()
    {
        var page = Render<Health>();
        page.Dispose();

        // The holder is a singleton that outlives every browser session, so a page that stays
        // subscribed is both a leak and, once its renderer is gone, an exception on the polling
        // thread -- which is a great deal worse than a stale page.
        var exception = Record.Exception(() => _holder.Set(Statuses.Sample(_time.Now)));

        Assert.Null(exception);
    }

    [Fact]
    public void Reports_a_clock_that_went_backwards_as_zero_rather_than_negative()
    {
        // An NTP step, or a device timestamp slightly ahead of ours.
        _holder.Set(Statuses.Sample(_time.Now.AddSeconds(5)));

        var page = Render<Health>();

        Assert.Contains("0 s ago", page.Markup);
    }

    [Fact]
    public void Switches_from_seconds_to_minutes_once_a_poll_is_properly_stale()
    {
        _holder.Set(Statuses.Sample(_time.Now.AddMinutes(-7)));

        var page = Render<Health>();

        Assert.Contains("7 min ago", page.Markup);
    }

    // The stop control. What these are really guarding is that one click can't do it: the button is
    // one-way from the browser's point of view -- the page it lives on goes down with the service --
    // so a stray click has to be recoverable, and only the second one may reach the seam.

    [Fact]
    public void Does_not_stop_the_service_on_the_first_click()
    {
        var page = Render<Health>();

        page.Find("button.danger").Click();

        Assert.Empty(_shutdown.Requests);
        Assert.Contains("Yes, stop it", page.Markup);
    }

    [Fact]
    public void Stops_the_service_on_the_confirmation_and_says_who_asked()
    {
        var page = Render<Health>();

        page.Find("button.danger").Click();
        page.Find("button.danger").Click();

        Assert.Equal(["Web UI"], _shutdown.Requests);
    }

    [Fact]
    public void Cancelling_leaves_the_service_running()
    {
        var page = Render<Health>();

        page.Find("button.danger").Click();
        page.Find("button.secondary").Click();

        Assert.Empty(_shutdown.Requests);
        Assert.DoesNotContain("Yes, stop it", page.Markup);
        Assert.Contains("Stop service", page.Markup);
    }

    [Fact]
    public void Says_it_is_stopping_and_how_to_start_it_again()
    {
        var page = Render<Health>();

        page.Find("button.danger").Click();
        page.Find("button.danger").Click();

        // The request returns immediately, so this render is the last thing the browser sees. It has
        // to carry the one instruction the user now needs, because the UI is about to be gone.
        Assert.Contains("Stopping", page.Markup);
        Assert.Contains("docker compose start gleanvolt-controller", page.Markup);
        Assert.Empty(page.FindAll("button.danger"));
    }
    /// <summary>A feed that also reports how its running is going.</summary>
    private sealed class StubFeedWithDiagnostics(VehicleSourceHealth health, VehicleFeedDiagnostics diagnostics)
        : IVehicleUpdateService, IVehicleFeedDiagnostics
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-group";

        public VehicleSourceHealth Health => health;

        public TimeSpan NextDelay => TimeSpan.FromMinutes(15);

        public VehicleFeedDiagnostics Diagnostics => diagnostics;

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    [Fact]
    public void Reports_whether_the_feed_is_reading_on_its_clock_and_holding_one_session()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeedWithDiagnostics(
            VehicleSourceHealth.Ok("The portal answered."),
            new VehicleFeedDiagnostics(
                Attempts: 9,
                Readings: 8,
                LastAttemptAt: _time.Now.AddMinutes(-2),
                LastReadingAt: _time.Now.AddMinutes(-2),
                NextDueAt: _time.Now.AddMinutes(13),
                Sessions: 1,
                SessionAge: TimeSpan.FromHours(2.4))));

        var page = Render<Health>();

        Assert.Contains("8 of 9", page.Markup);
        Assert.Contains("2.4 h", page.Markup);
        Assert.Contains("1 sign-in", page.Markup);
        Assert.Contains("in 13 min", page.Markup);

        // One session is the healthy answer, so nothing shouts about it.
        Assert.DoesNotContain("sessions are expiring", page.Markup);
    }

    [Fact]
    public void Says_outright_when_sessions_are_expiring()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeedWithDiagnostics(
            VehicleSourceHealth.Ok("The portal answered."),
            VehicleFeedDiagnostics.None with { Attempts = 20, Readings = 20, Sessions = 6 }));

        Assert.Contains("sessions are expiring", Render<Health>().Markup);
    }

    [Fact]
    public void A_stopped_feed_has_no_next_read()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeedWithDiagnostics(
            VehicleSourceHealth.NeedsOwner("The portal wants you in a browser."),
            VehicleFeedDiagnostics.None with { Attempts = 1, NextDueAt = null }));

        var page = Render<Health>();

        Assert.Contains("never — the feed has stopped", page.Markup);
    }

    [Fact]
    public void A_feed_that_offers_no_diagnostics_is_not_described_with_zeroes()
    {
        // The contract stays five members: a service that reports none of this is not a broken one,
        // and a row of zeroes would read as a feed that has never run.
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed(VehicleSourceHealth.Ok("Fine.")));

        var page = Render<Health>();

        Assert.Contains("Car feed", page.Markup);
        Assert.DoesNotContain("Portal session", page.Markup);
    }


}
