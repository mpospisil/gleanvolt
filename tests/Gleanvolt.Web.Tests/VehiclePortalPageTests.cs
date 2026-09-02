using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The on-demand portal read (issues #137/#139). What this page has to get right is the same thing
/// the harness got right on a console: that the <b>kinds</b> of failure stay apart. A consent screen
/// and an expired session look identical if a page only says "it didn't work", and they need
/// opposite things from the owner — one a browser, the other the button again.
/// </summary>
public class VehiclePortalPageTests : PageTest
{
    private static EvInfo Car() => new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77, 0.9, 3, 6, 16, "gleanvolt/vehicle/id4/state");

    /// <summary>A reader that answers whatever the test says, and counts how often it was asked.</summary>
    private sealed class StubReader(VehiclePortalReading? reading = null, bool configured = true)
        : IVehiclePortalReader
    {
        public int Reads { get; private set; }

        public string PortalName => "VW Group EU Data Act portal";

        public bool IsConfigured => configured;

        public string DescribeWhatIsMissing() =>
            configured ? string.Empty : "a brand (one of vw, audi, skoda, seat, cupra, bentley)";

        public Task<VehiclePortalReading> ReadAsync(CancellationToken cancellationToken = default)
        {
            Reads++;
            return Task.FromResult(reading ?? new VehiclePortalReading(true));
        }
    }

    private IRenderedComponent<VehiclePortal> Render(StubReader reader)
    {
        Services.AddSingleton<IVehiclePortalReader>(reader);
        Services.AddSingleton(Car());
        return Render<VehiclePortal>();
    }

    [Fact]
    public void An_unconfigured_reader_offers_no_button_and_says_what_is_missing()
    {
        var page = Render(new StubReader(configured: false));

        Assert.Empty(page.FindAll("#portal-read"));
        Assert.Contains("a brand", page.Markup);
        // The setting names, so the fix does not need the documentation open beside it.
        Assert.Contains("VW_BRAND", page.Markup);
    }

    [Fact]
    public void Nothing_is_read_until_the_button_is_pressed()
    {
        var reader = new StubReader();
        var page = Render(reader);

        Assert.Equal(0, reader.Reads);
        page.Find("#portal-read").Click();
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public void A_reading_shows_what_the_car_said()
    {
        var reader = new StubReader(new VehiclePortalReading(
            Succeeded: true,
            State: new VehicleState(
                new DateTimeOffset(2026, 9, 1, 5, 27, 11, TimeSpan.Zero),
                SocPercent: 64,
                RangeKm: 312,
                ChargeState: VehicleChargeState.Idle,
                PlugState: VehiclePlugState.Disconnected),
            Vehicle: "…1234",
            SnapshotCount: 6));

        var page = Render(reader);
        page.Find("#portal-read").Click();

        Assert.Contains("64", page.Markup);
        Assert.Contains("312", page.Markup);
        Assert.Contains("…1234", page.Markup);
    }

    /// <summary>
    /// Half of the distinction the failure taxonomy exists for: this one needs a browser, and the
    /// page must not invite the owner to press again. Its pair is the test below.
    /// </summary>
    [Fact]
    public void A_failure_that_needs_a_browser_says_pressing_again_will_not_help()
    {
        var page = Render(new StubReader(VehiclePortalReading.Failed(
            "OwnerActionRequired", "the portal is showing consent", worthRetrying: false)));

        page.Find("#portal-read").Click();

        Assert.Contains("OwnerActionRequired", page.Markup);
        Assert.Contains("will not help", page.Markup);
        Assert.DoesNotContain("Worth pressing again", page.Markup);
    }

    /// <summary>The other half: ordinary, self-clearing, and the button is the fix.</summary>
    [Fact]
    public void An_expired_session_invites_another_press()
    {
        var page = Render(new StubReader(VehiclePortalReading.Failed(
            "SessionExpired", "the session is gone", worthRetrying: true)));

        page.Find("#portal-read").Click();

        Assert.Contains("SessionExpired", page.Markup);
        Assert.Contains("Worth pressing again", page.Markup);
        Assert.DoesNotContain("will not help", page.Markup);
    }

    /// <summary>
    /// Unrecognised field names are the harness's most useful output and must survive onto the page:
    /// the portal's vocabulary was written from a description, so a null SOC usually means a name
    /// nothing here reads.
    /// </summary>
    [Fact]
    public void Unrecognised_field_names_are_listed()
    {
        var reader = new StubReader(new VehiclePortalReading(
            Succeeded: true,
            State: new VehicleState(DateTimeOffset.UnixEpoch, SocPercent: 50),
            UnmappedFields: ["some.unknown.field", "another_one"]));

        var page = Render(reader);
        page.Find("#portal-read").Click();

        Assert.Contains("some.unknown.field", page.Markup);
        Assert.Contains("another_one", page.Markup);
    }
    [Fact]
    public void A_bundle_nothing_matched_lists_the_names_it_did_not_match()
    {
        // The case observed live: the portal answered, the capture time was minutes old, and every
        // value was blank. It is a failure rather than a reading of dashes -- and it is exactly the
        // failure whose field list is the whole answer, so the page must not withhold it.
        var page = Render(new StubReader(VehiclePortalReading.Failed(
            "UnusableData",
            "the bundle was read and none of its 14 field(s) are ones this build recognises",
            worthRetrying: false,
            unmapped: ["odometer_km_v2", "battery.soc_pct"])));

        page.Find("#portal-read").Click();

        Assert.Contains("UnusableData", page.Markup);
        Assert.Contains("odometer_km_v2", page.Markup);
        Assert.Contains("battery.soc_pct", page.Markup);
        Assert.Contains("VwGroupFieldNames", page.Markup);
    }


}
