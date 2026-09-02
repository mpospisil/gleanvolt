using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The page Phase 3 of #137 is read off (issue #141). It reports and decides nothing, so the tests are
/// about whether the four questions — cadence, agreement, coverage, survival — can actually be
/// answered from what it prints.
/// </summary>
public class VehicleFeedsPageTests : PageTest
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly DateTimeOffset Noon = new(2026, 9, 2, 10, 0, 0, TimeSpan.Zero);

    private readonly ChargeControlStatusHolder _status = new();
    private readonly FixedTimeProvider _time = new(Noon, Prague);
    private readonly VehicleStateHolder _vehicle;

    public VehicleFeedsPageTests()
    {
        _vehicle = new VehicleStateHolder(_time);

        Services.AddSingleton(_status);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton(_vehicle.Comparison);
    }

    /// <summary>A feed that answers only about itself; a render must never make it fetch.</summary>
    private sealed class StubFeed(VehicleFeedDiagnostics diagnostics)
        : IVehicleUpdateService, IVehicleFeedDiagnostics
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-group";

        public VehicleSourceHealth Health => VehicleSourceHealth.Ok("the portal answered");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(15);

        public VehicleFeedDiagnostics Diagnostics => diagnostics;

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    private void Feed(VehicleFeedDiagnostics diagnostics) =>
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed(diagnostics));

    private void Deliver(TimeSpan offset, string source, double soc, VehicleChargeState charge = VehicleChargeState.Idle) =>
        _vehicle.Set(new VehicleState(Noon + offset, SocPercent: soc, ChargeState: charge, SourceId: source));

    [Fact]
    public void Says_nothing_has_been_offered_rather_than_showing_empty_tables()
    {
        var page = Render<VehicleFeeds>();

        Assert.Contains("Nothing has been offered yet", page.Markup);
        Assert.Contains("Vehicle__DataAct__Enabled", page.Markup);
        Assert.DoesNotContain("Cadence", page.Markup);
    }

    [Fact]
    public void Names_both_feeds_and_counts_what_each_delivered()
    {
        Deliver(TimeSpan.Zero, "mqtt", 60);
        Deliver(TimeSpan.FromMinutes(15), "mqtt", 61);
        Deliver(TimeSpan.FromMinutes(2), "vw-group", 58);

        var page = Render<VehicleFeeds>();

        Assert.Contains("mqtt", page.Markup);
        Assert.Contains("vw-group", page.Markup);
        Assert.Contains("Cadence", page.Markup);
        Assert.Contains("Coverage", page.Markup);
    }

    [Fact]
    public void Marks_a_feed_whose_account_of_the_car_went_backwards()
    {
        // The reference install's case, and the one a "Repeats" column on its own would have hidden:
        // the portal answered every time and reached back four hours past a snapshot it had already
        // shown us, which is a data request that has stopped filling rather than a quiet car.
        Deliver(TimeSpan.FromHours(4), "vw-group", 78);
        Deliver(TimeSpan.Zero, "vw-group", 78);
        Deliver(TimeSpan.FromMinutes(3), "vw-group", 78);

        var page = Render<VehicleFeeds>();

        Assert.Contains("Went back", page.Markup);
        Assert.Contains("−4 h 00 m", page.Markup);
        Assert.Contains("stopped filling", page.Markup);
    }

    [Fact]
    public void Leaves_the_step_backwards_blank_for_a_feed_that_only_repeats_itself()
    {
        Deliver(TimeSpan.Zero, "mqtt", 60);
        Deliver(TimeSpan.Zero, "mqtt", 60);

        var page = Render<VehicleFeeds>();

        Assert.Contains("Went back", page.Markup);
        Assert.DoesNotContain("−", page.Markup);
    }

    [Fact]
    public void Reports_the_window_it_was_measured_over_before_anything_else()
    {
        // Every number on the page is worth exactly as much as the time behind it, and a restart puts
        // that back to zero -- which has to be visible rather than discovered.
        _time.Now = Noon.AddDays(7).AddHours(3);

        var page = Render<VehicleFeeds>();

        Assert.Contains("Measuring for", page.Markup);
        Assert.Contains("7 d 3 h", page.Markup);
        Assert.Contains("a gap that spans a restart is the restart", page.Markup);
    }

    [Fact]
    public void Shows_the_offset_between_two_feeds_with_its_direction()
    {
        // The finding: not that two numbers differ, but that one differs the same way every time.
        Deliver(TimeSpan.Zero, "mqtt", 60);
        Deliver(TimeSpan.FromMinutes(1), "vw-group", 57);
        Deliver(TimeSpan.FromMinutes(15), "mqtt", 60);
        Deliver(TimeSpan.FromMinutes(16), "vw-group", 57);

        var page = Render<VehicleFeeds>();

        Assert.Contains("Agreement", page.Markup);
        Assert.Contains("+3.0 pt", page.Markup);
        Assert.Contains("reads higher than", page.Markup);
    }

    [Fact]
    public void Says_why_there_is_nothing_to_compare_when_two_feeds_never_line_up()
    {
        Deliver(TimeSpan.Zero, "mqtt", 60);
        Deliver(TimeSpan.FromHours(4), "vw-group", 40);

        var page = Render<VehicleFeeds>();

        Assert.Contains("no two of their capture times", page.Markup);
    }

    [Fact]
    public void Counts_the_sign_ins_and_calls_more_than_one_out()
    {
        Feed(new VehicleFeedDiagnostics(96, 90, Noon, Noon, Noon.AddMinutes(15), 4, TimeSpan.FromHours(3)));
        Deliver(TimeSpan.Zero, "vw-group", 58);

        var page = Render<VehicleFeeds>();

        Assert.Contains("Survival", page.Markup);
        Assert.Contains("sessions are expiring", page.Markup);
        Assert.Contains("90 of 96", page.Markup);
    }

    [Fact]
    public void One_session_for_the_whole_run_is_the_healthy_answer()
    {
        Feed(new VehicleFeedDiagnostics(96, 96, Noon, Noon, Noon.AddMinutes(15), 1, TimeSpan.FromDays(4)));
        Deliver(TimeSpan.Zero, "vw-group", 58);

        var page = Render<VehicleFeeds>();

        Assert.Contains("one session has covered the whole run", page.Markup);
        Assert.DoesNotContain("sessions are expiring", page.Markup);
    }

    [Fact]
    public void Says_outright_when_the_car_has_never_reported_a_target_state_of_charge()
    {
        // #73 never established this for the reference car, and a week of nothing is the answer #141
        // has to write down.
        Feed(new VehicleFeedDiagnostics(96, 96, Noon, Noon, Noon.AddMinutes(15), 1, TimeSpan.FromDays(4)));
        Deliver(TimeSpan.Zero, "vw-group", 58);

        var page = Render<VehicleFeeds>();

        Assert.Contains("never reported", page.Markup);
    }

    [Fact]
    public void Shows_the_target_state_of_charge_and_how_often_it_arrived()
    {
        Feed(new VehicleFeedDiagnostics(
            96, 96, Noon, Noon, Noon.AddMinutes(15), 1, TimeSpan.FromDays(4),
            TargetSocReadings: 90, TargetSocPercent: 80));
        Deliver(TimeSpan.Zero, "vw-group", 58);

        var page = Render<VehicleFeeds>();

        Assert.Contains("80%", page.Markup);
        Assert.Contains("on 90 of 96 reads", page.Markup);
    }

    [Fact]
    public void Spells_out_that_the_handover_is_a_setting_and_nothing_is_deleted()
    {
        var page = Render<VehicleFeeds>();

        Assert.Contains("Vehicle__Enabled", page.Markup);
        Assert.Contains("No code goes away", page.Markup);
    }
}
