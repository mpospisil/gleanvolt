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


    [Fact]
    public void It_shows_what_matched_and_what_each_of_those_fields_said()
    {
        // The two lists answer different questions and the page needs both: a name missing from the
        // unrecognised list means either "never carried" or "carried empty", and only the values
        // below say which.
        var page = Render(new StubReader(VehiclePortalReading.Failed(
            "UnusableData",
            "the bundle was read and none of its 47 field(s) are ones this build recognises",
            worthRetrying: false,
            unmapped: ["settings.auto_unlock_ac"],
            matched: new Dictionary<string, string>
            {
                ["charging_state_report.charging_state"] = "invalid",
                ["mileage.value"] = "24680",
            })));

        page.Find("#portal-read").Click();

        // Read as text rather than as markup: the names carry <wbr> break opportunities between their
        // segments, which is a layout detail and not something every assertion should know about.
        var recognised = page.Find("dl.fields").TextContent;

        Assert.Contains("charging_state_report.charging_state", recognised);
        Assert.Contains("invalid", recognised);
        Assert.Contains("24680", recognised);
        Assert.Contains("settings.auto_unlock_ac", page.Markup);
    }

    [Fact]
    public void A_report_dropped_for_want_of_a_timestamp_is_named_with_the_fields_it_took_with_it()
    {
        var page = Render(new StubReader(VehiclePortalReading.Failed(
            "UnusableData",
            "the bundle held no dated report with any readings in it",
            worthRetrying: false,
            diagnostics: ["1 of 3 report(s) were dropped for carrying no timestamp this build recognises."],
            dropped: ["battery_level_HV.value"])));

        page.Find("#portal-read").Click();

        Assert.Contains("dropped for carrying no timestamp", page.Markup);
        Assert.Contains("battery_level_HV.value", page.Markup);
    }


    [Fact]
    public void The_delivery_is_described_even_when_nothing_could_be_mapped_out_of_it()
    {
        // How many snapshots arrived and what they span is how "this quarter-hour said nothing" is
        // told apart from "the portal handed us one report type and the battery is in another
        // delivery". On a failure that is half the diagnosis, so it cannot live in the success branch.
        var page = Render(new StubReader(new VehiclePortalReading(
            Succeeded: false,
            Vehicle: "…1234",
            SnapshotCount: 3,
            OldestSnapshot: new DateTimeOffset(2026, 9, 2, 9, 59, 0, TimeSpan.Zero),
            NewestSnapshot: new DateTimeOffset(2026, 9, 2, 10, 29, 0, TimeSpan.Zero),
            OdometerKm: 53065,
            TargetSocPercent: 80,
            FailureKind: "UnusableData",
            Message: "none of its 47 field(s) are ones this build recognises")));

        page.Find("#portal-read").Click();

        Assert.Contains("The delivery", page.Markup);
        Assert.Contains("…1234", page.Markup);
        Assert.Contains("53065", page.Markup);
        Assert.Contains("80%", page.Markup);
    }


    [Fact]
    public void Long_field_names_are_given_joints_to_break_at_and_a_column_that_can_shrink()
    {
        // A fifty-character dotted path in a column sized to its own content overran the value beside
        // it -- reported from the live page as text overlapping text. The list is .fields rather than
        // .facts for that reason, and each separator carries a break opportunity so the wrap lands on
        // a joint instead of mid-word.
        var page = Render(new StubReader(VehiclePortalReading.Failed(
            "UnusableData", "nothing recognised", worthRetrying: false,
            matched: new Dictionary<string, string>
            {
                ["energy_contents.maximal_energy_content.physical_value"] = "738.0",
            })));

        page.Find("#portal-read").Click();

        Assert.Contains("class=\"fields\"", page.Markup);
        Assert.Contains("energy_<wbr>contents.<wbr>maximal_<wbr>energy_<wbr>content.<wbr>physical_<wbr>value",
            page.Markup);
    }

    [Fact]
    public void A_field_name_from_the_portal_cannot_smuggle_markup_onto_the_page()
    {
        // These strings come from outside and this is the one place the page emits markup rather than
        // text, so the encoding is pinned rather than assumed.
        var page = Render(new StubReader(VehiclePortalReading.Failed(
            "UnusableData", "nothing recognised", worthRetrying: false,
            matched: new Dictionary<string, string> { ["<script>alert(1)</script>"] = "<b>x</b>" })));

        page.Find("#portal-read").Click();

        Assert.DoesNotContain("<script>alert(1)</script>", page.Markup);
        Assert.DoesNotContain("<b>x</b>", page.Markup);
        Assert.Contains("&lt;script&gt;", page.Markup);
    }


}
