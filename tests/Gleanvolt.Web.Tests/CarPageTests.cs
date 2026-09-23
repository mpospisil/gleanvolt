using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web;
using Gleanvolt.Web.Components.Pages;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The whole car, on one page (issues #214, #227).
///
/// <para>#227 folded <c>/vehicle-portal</c> into this page, and with it
/// <c>VehiclePortalPageTests</c> and <c>VehicleAccountSignInPageTests</c>: nothing they asserted is
/// dropped, because they are what proves the sign-in flow and the field diagnostics still work after
/// the move. The form at the top keeps its own tests in <see cref="CarEditorFormTests"/>.</para>
///
/// <para>What #227 adds is an answer for <b>every</b> feed. The old page's sign-in was registered for
/// two manufacturers and vanished silently for the other three; its read button was the Data Act
/// portal's alone and showed the other four a red error for a portal they never asked for. So the
/// cases below run once per <see cref="VehicleFeed"/> value, and what they check is that nothing is
/// silent and nothing is red for the wrong reason.</para>
/// </summary>
public class CarPageTests : PageTest
{
    /// <summary>When the car reported a diagnostic field, for the tests that show one.</summary>
    private static readonly DateTimeOffset Reported = new(2026, 9, 2, 10, 29, 46, TimeSpan.Zero);

    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly FakeEvEditor _editor = new();

    /// <summary>Now, for the reading ages the card prints (#178). Two hours after <see cref="Reported"/>.</summary>
    private readonly FixedTimeProvider _time =
        new(new DateTimeOffset(2026, 9, 2, 12, 29, 46, TimeSpan.Zero), Prague);

    /// <summary>
    /// What the feed is holding, which a Data Act press is shown against (#178). Empty by default:
    /// that is the install with no feed switched on, and the page has to say so rather than compare
    /// against nothing.
    /// </summary>
    private readonly VehicleStateHolder _held = new();

    /// <summary>The account, unconfigured until a test says otherwise — the commonest installation.</summary>
    private IVehicleAccountSignIn _account = new NoVehicleAccountSignIn();

    private IVehiclePortalReader _portal = new StubReader(configured: false);

    private IVehicleStateRefresh _refresh = new FakeVehicleStateRefresh(canRefresh: false);

    /// <summary>A UI with a login, which is what makes the form editable.</summary>
    private WebOptions _web = new() { PasswordHash = "hash" };

    public CarPageTests()
    {
        Services.AddSingleton<IEvEditor>(_editor);
        Services.AddSingleton<IServiceShutdown>(new FakeServiceShutdown());
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    /// <summary>An ID.4 Pro: 77 kWh usable, three phases, 6–16 A.</summary>
    private static EvInfo Id4 { get; } = new(
        "id4", "The ID.4", "Volkswagen", "ID.4 Pro", 77, 0.9, 3, 6, 16, string.Empty);

    private sealed class StubFeed : IVehicleUpdateService
    {
        public string VehicleId => "id4";

        public string Manufacturer => "vw-website";

        public string DisplayName => "volkswagen.de";

        public VehicleSourceHealth Health { get; } = VehicleSourceHealth.Ok("signed in");

        public TimeSpan NextDelay => TimeSpan.FromMinutes(5);

        public Task<VehicleState?> FetchAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException("A render must never fetch.");
    }

    /// <summary>A portal reader that answers whatever the test says, and counts how often it was asked.</summary>
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

    private IRenderedComponent<Car> Page(
        EvInfo? car = null,
        VehicleFeed feed = VehicleFeed.ChargeOnly,
        int minAmps = 6,
        int maxAmps = 16)
    {
        var resolved = car ?? Id4;
        _editor.RunningFeed = feed;

        Services.AddSingleton(resolved);
        Services.AddSingleton(Options.Create(_web));
        Services.AddSingleton(ChargingLimits.Intersect(minAmps, maxAmps, 3, resolved));
        Services.AddSingleton(_account);
        Services.AddSingleton(_portal);
        Services.AddSingleton(_refresh);
        Services.AddSingleton<TimeProvider>(_time);
        Services.AddSingleton<IVehicleTelemetry>(_held);

        return Render<Car>();
    }

    /// <summary>The Data Act installation: the one the portal reader belongs to.</summary>
    private IRenderedComponent<Car> DataActPage(VehiclePortalReading? reading = null, EvInfo? car = null)
    {
        _portal = new StubReader(reading);
        return Page(car ?? Id4 with { TelemetryTopic = string.Empty }, VehicleFeed.DataAct);
    }

    // ---------------------------------------------------------------- the car the process resolved

    [Fact]
    public void An_undescribed_car_is_said_to_be_a_supported_installation()
    {
        var page = Page(EvInfo.Unknown);

        Assert.Contains("supported installation", page.Find("#car-unconfigured").TextContent);
    }

    [Fact]
    public void The_car_is_shown_beside_what_the_installation_allows()
    {
        // Three columns, because the interesting number is the third: a 32 A car behind a 16 A supply
        // charges at 16, and a page showing only the car's figures leaves somebody wondering why.
        var page = Page(Id4 with { MaxChargingCurrentAmps = 32 });

        var row = page.FindAll("table.grid tbody tr")[1].TextContent;
        Assert.Contains("32 A", row);
        Assert.Contains("16 A", row);
    }

    [Fact]
    public void The_feed_that_actually_started_is_the_one_named()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.Volkswagen);

        Assert.Contains("volkswagen.de", page.Markup);
        Assert.Empty(page.FindAll("#no-feed"));
    }

    [Fact]
    public void No_feed_says_what_it_costs_and_what_it_does_not()
    {
        var page = Page();

        Assert.Contains("Charging is unaffected", page.Find("#no-feed").TextContent);
    }

    [Fact]
    public void A_portal_set_aside_by_a_live_feed_is_said_here_rather_than_only_in_the_log()
    {
        // #212 skips the Data Act portal beside a live feed with one startup line. This is the page
        // where an owner would otherwise wonder why the portal they configured does nothing.
        Services.AddSingleton(new ConfiguredVehicleFeed(new StubFeed(), "Vehicle:Website is configured, so the Data Act portal is not used."));

        var page = Page(feed: VehicleFeed.Volkswagen);

        Assert.Contains("not used", page.Find("#feed-set-aside").TextContent);
    }

    [Fact]
    public void An_own_topic_is_said_to_run_beside_a_manufacturer_feed()
    {
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(Id4 with { TelemetryTopic = "gleanvolt/vehicle/id4/state" }, VehicleFeed.Volkswagen);

        Assert.Contains("freshest reading wins", page.Find("#own-topic").TextContent);
    }

    // -------------------------------------------------------------------- one page, one outline (#227)

    /// <summary>
    /// The heading outline, which was wrong on the old page for exactly the two manufacturers that
    /// had an account: the sign-in's <c>h2</c> rendered above the page's own <c>h1</c>.
    /// </summary>
    [Theory]
    [InlineData(VehicleFeed.Volkswagen)]
    [InlineData(VehicleFeed.Skoda)]
    [InlineData(VehicleFeed.DataAct)]
    [InlineData(VehicleFeed.OwnTopic)]
    [InlineData(VehicleFeed.ChargeOnly)]
    public void There_is_one_h1_and_it_precedes_every_h2(VehicleFeed feed)
    {
        _account = new FakeVehicleAccountSignIn();

        var page = Page(feed: feed);
        var headings = page.FindAll("h1, h2");

        Assert.Equal("H1", headings[0].TagName);
        Assert.Single(headings, heading => heading.TagName == "H1");
    }

    /// <summary>
    /// The complaint #227 was opened with: on three of the five choices the account section rendered
    /// nothing at all, and silence is indistinguishable from a bug.
    /// </summary>
    [Theory]
    [InlineData(VehicleFeed.Volkswagen)]
    [InlineData(VehicleFeed.Skoda)]
    [InlineData(VehicleFeed.DataAct)]
    [InlineData(VehicleFeed.OwnTopic)]
    [InlineData(VehicleFeed.ChargeOnly)]
    public void Every_feed_gets_an_account_section_with_a_sentence_in_it(VehicleFeed feed)
    {
        var page = Page(feed: feed);

        var account = page.Find("#account").TextContent;

        Assert.Contains("Account", account);
        // A heading and a sentence, not a heading over a gap.
        Assert.True(account.Trim().Length > "Account".Length + 40, account);
    }

    /// <summary>
    /// The other half of the complaint. Four of five installations landed on a red <i>not
    /// configured</i> naming <c>VW_BRAND</c>, <c>VW_USERNAME</c> and <c>VW_PASSWORD</c> — advice that
    /// was wrong for them, because following it arranges a Data Act portal that #212 then sets aside.
    /// </summary>
    [Theory]
    [InlineData(VehicleFeed.Volkswagen)]
    [InlineData(VehicleFeed.Skoda)]
    [InlineData(VehicleFeed.OwnTopic)]
    [InlineData(VehicleFeed.ChargeOnly)]
    public void No_other_feed_is_told_a_data_act_portal_is_not_configured(VehicleFeed feed)
    {
        var page = Page(feed: feed);

        Assert.Empty(page.FindAll("#portal-unconfigured"));
        Assert.DoesNotContain("VW_BRAND", page.Markup);
        Assert.DoesNotContain("Not configured", page.Markup);
    }

    /// <summary>
    /// The one negative that has to hold for every feed: a render is not a press. Opening this page
    /// must never replay a password, email anybody a one-time code, or spend a manufacturer's quota.
    /// </summary>
    [Theory]
    [InlineData(VehicleFeed.Volkswagen)]
    [InlineData(VehicleFeed.Skoda)]
    [InlineData(VehicleFeed.DataAct)]
    [InlineData(VehicleFeed.OwnTopic)]
    [InlineData(VehicleFeed.ChargeOnly)]
    public void A_render_never_signs_in_submits_or_fetches(VehicleFeed feed)
    {
        var account = new FakeVehicleAccountSignIn();
        var reader = new StubReader();
        var refresh = new FakeVehicleStateRefresh(new VehicleState(Reported, SocPercent: 64));
        _account = account;
        _portal = reader;
        _refresh = refresh;
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: feed);
        page.Render();

        Assert.Equal(0, account.SignIns);
        Assert.Equal(0, account.CodeSubmissions);
        Assert.Equal(0, reader.Reads);
        Assert.Equal(0, refresh.Asks);
    }

    /// <summary>
    /// The rule #204 set for the device addresses, which #214 applied to these fields and the merge
    /// must not quietly drop: without a login, anyone on the LAN could change which manufacturer
    /// account this car is read from, so the form is shown and not usable.
    /// </summary>
    [Fact]
    public void Without_a_login_the_form_is_still_read_only()
    {
        _web = new WebOptions();

        var page = Page(feed: VehicleFeed.DataAct);

        Assert.Contains("Read-only", page.Find("#car-readonly").TextContent);
        Assert.Empty(page.FindAll("#car-save"));
    }

    /// <summary>Nothing on the page points at the route that is now a redirect.</summary>
    [Fact]
    public void Nothing_links_to_the_page_that_was_folded_in() =>
        Assert.DoesNotContain(
            Page().FindAll("a"), link => link.GetAttribute("href") == "/vehicle-portal");

    /// <summary>
    /// The bookmark, the README's anchors and <c>docs/VW_PORTAL_SETUP.md</c> have all been pointing at
    /// <c>/vehicle-portal</c> since #137. Cheaper to keep the route than to chase every one of them.
    /// </summary>
    [Fact]
    public void The_old_route_redirects_to_the_car()
    {
        Render<VehiclePortalRedirect>();

        Assert.EndsWith("/car", Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>().Uri);
    }

    // ------------------------------------------------------------------------------ the account

    [Fact]
    public void Signing_in_asks_once_and_then_wants_the_code()
    {
        var account = new FakeVehicleAccountSignIn();
        _account = account;

        var page = Page(feed: VehicleFeed.Volkswagen);
        page.Find("#account-signin").Click();

        Assert.Equal(1, account.SignIns);
        Assert.Single(page.FindAll("#account-code"));
        Assert.Contains("emailed", page.Markup);
    }

    [Fact]
    public void The_code_is_submitted_and_the_page_says_it_is_signed_in()
    {
        var account = new FakeVehicleAccountSignIn();
        _account = account;

        var page = Page(feed: VehicleFeed.Volkswagen);
        page.Find("#account-signin").Click();
        page.Find("#account-code").Input("806324");
        page.Find("#account-code-submit").Click();

        Assert.Equal("806324", account.LastCode);
        Assert.Contains("Signed in", page.Markup);
        Assert.Single(page.FindAll("#account-signout"));
    }

    /// <summary>
    /// A wrong code leaves the challenge open. Starting over would email a third code and invalidate
    /// the one the owner is still reading.
    /// </summary>
    [Fact]
    public void A_rejected_code_keeps_the_box_open()
    {
        _account = new FakeVehicleAccountSignIn(
            afterCode: VehicleSignInState.CodeRequired("That code was not accepted. Check the newest email"));

        var page = Page(feed: VehicleFeed.Volkswagen);
        page.Find("#account-signin").Click();
        page.Find("#account-code").Input("000000");
        page.Find("#account-code-submit").Click();

        Assert.Single(page.FindAll("#account-code"));
        Assert.Contains("not accepted", page.Markup);
    }

    [Fact]
    public void The_explanation_is_the_sign_ins_own()
    {
        _account = new FakeVehicleAccountSignIn();

        var page = Page(feed: VehicleFeed.Volkswagen);

        Assert.Contains("the code Volkswagen emails", page.Find("#account-explanation").TextContent);
    }

    /// <summary>A Škoda installation (#193): a key box straight away, and no word of Volkswagen.</summary>
    [Fact]
    public void A_key_sign_in_asks_for_a_key_in_a_password_box_without_being_pressed()
    {
        var account = new FakeKeySignIn();
        _account = account;

        var page = Page(Id4 with { Make = "Škoda", Model = "Enyaq" }, VehicleFeed.Skoda);

        var box = page.Find("#account-key");
        Assert.Equal("password", box.GetAttribute("type"));
        Assert.Equal("off", box.GetAttribute("autocomplete"));
        Assert.Empty(page.FindAll("#account-code"));
        Assert.Empty(page.FindAll("#account-signin"));

        // Read inside the section rather than over the whole page: /car names the make, the brands the
        // Data Act choice offers and the feed the form is set to, none of which is this car's account.
        Assert.DoesNotContain("Volkswagen", page.Find("#account").TextContent);
        Assert.Equal(0, account.Submissions);
    }

    [Fact]
    public void A_submitted_key_is_signed_in_and_never_rendered_back()
    {
        const string key = "sk-live-0123456789abcdef";
        var account = new FakeKeySignIn();
        _account = account;

        var page = Page(feed: VehicleFeed.Skoda);
        page.Find("#account-key").Input(key);
        page.Find("#account-key-submit").Click();

        Assert.Equal(key, account.LastAnswer);
        Assert.Contains("Key valid until 2027-03-01", page.Markup);
        Assert.Single(page.FindAll("#account-signout"));
        Assert.DoesNotContain(key, page.Markup);
    }

    [Fact]
    public void A_refused_key_keeps_the_box_open_and_empty()
    {
        const string key = "sk-live-expired";
        _account = new FakeKeySignIn(
            VehicleSignInState.KeyRequired("That key has expired — create a new one in the app."));

        var page = Page(feed: VehicleFeed.Skoda);
        page.Find("#account-key").Input(key);
        page.Find("#account-key-submit").Click();

        Assert.Contains("That key has expired", page.Markup);
        Assert.Single(page.FindAll("#account-key"));
        Assert.DoesNotContain(key, page.Markup);
    }

    [Fact]
    public void An_already_signed_in_account_offers_sign_out_rather_than_a_code_box()
    {
        _account = new FakeVehicleAccountSignIn(first: VehicleSignInState.SignedIn("session restored"));

        var page = Page(feed: VehicleFeed.Volkswagen);
        page.Find("#account-signin").Click();

        Assert.Empty(page.FindAll("#account-code"));
        Assert.Single(page.FindAll("#account-signout"));
    }

    /// <summary>
    /// The three feeds with nothing to sign in to each say <i>why</i>, in their own terms. The Data Act
    /// portal has an account and no session; an own topic has neither; charge-only has no reader at all
    /// and must not read as an unfinished form.
    /// </summary>
    [Theory]
    [InlineData(VehicleFeed.DataAct, "signs in on each read")]
    [InlineData(VehicleFeed.OwnTopic, "you publish the readings yourself")]
    [InlineData(VehicleFeed.ChargeOnly, "nothing reads this car")]
    public void A_feed_with_no_session_says_so_in_its_own_terms(VehicleFeed feed, string expected)
    {
        var page = Page(feed: feed);

        Assert.Contains(expected, page.Find("#account-none").TextContent);
        Assert.Empty(page.FindAll("#account-signin"));
    }

    /// <summary>
    /// The case <see cref="NoVehicleAccountSignIn.Explanation"/> exists for: a feed that wants an
    /// account, switched on without enough of one for the host to have registered a sign-in. It was
    /// <c>string.Empty</c> until #227, which is how an installation could read a heading over a gap.
    /// </summary>
    [Fact]
    public void A_feed_that_wants_an_account_it_has_not_got_says_that_rather_than_nothing()
    {
        var page = Page(feed: VehicleFeed.Volkswagen);

        Assert.Contains("nothing to sign in to", page.Find("#account-none").TextContent);
        Assert.Empty(page.FindAll("#account-signin"));
    }

    // ---------------------------------------------------------------- asking a live feed (#227)

    [Fact]
    public void A_live_feed_is_asked_with_the_same_call_the_worker_makes()
    {
        var refresh = new FakeVehicleStateRefresh(new VehicleState(
            Reported, SocPercent: 64, RangeKm: 312, SourceId: "vw-website"));
        _refresh = refresh;
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.Volkswagen);

        Assert.Equal(0, refresh.Asks);
        page.Find("#car-ask").Click();

        Assert.Equal(1, refresh.Asks);
        Assert.Contains("64%", page.Find("#car").TextContent);
        Assert.Contains("312 km", page.Find("#car").TextContent);
    }

    /// <summary>
    /// The consequence #227 asked to be settled out loud: a live feed's fetch writes to the holder, so
    /// a press here <b>does</b> reach the dashboard's card. The page must say that rather than carry
    /// the portal's "fed to nothing", which is false for this source.
    /// </summary>
    [Fact]
    public void A_live_feeds_press_says_the_dashboard_has_caught_up()
    {
        _refresh = new FakeVehicleStateRefresh(new VehicleState(Reported, SocPercent: 64, SourceId: "vw-website"));
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.Volkswagen);

        Assert.Contains("does", page.Markup);
        page.Find("#car-ask").Click();

        Assert.Contains("dashboard's card is now showing it", page.Find("#car-ask-source").TextContent);
    }

    /// <summary>
    /// A feed that cannot answer says what is stopping it, and what was already held stays on screen:
    /// an answer known to be old beats no answer, provided its age is beside it.
    /// </summary>
    [Fact]
    public void A_live_feed_that_cannot_answer_says_why_and_keeps_the_held_reading()
    {
        _refresh = new FakeVehicleStateRefresh(
            new VehicleState(Reported, SocPercent: 61),
            failure: "volkswagen.de: the session is gone — sign in again");
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.Volkswagen);
        page.Find("#car-ask").Click();

        Assert.Contains("the session is gone", page.Find("#car-ask-failed").TextContent);
        Assert.Contains("61%", page.Find("#car").TextContent);
        Assert.Contains("already holding", page.Find("#car-ask-source").TextContent);
    }

    /// <summary>
    /// A live feed switched on whose fields are not all filled in: nothing was registered, so there is
    /// nothing to ask. A sentence about the feed that <i>was</i> chosen, not an error about a portal.
    /// </summary>
    [Fact]
    public void A_live_feed_that_did_not_start_offers_no_button_and_says_why()
    {
        var page = Page(feed: VehicleFeed.Skoda);

        Assert.Empty(page.FindAll("#car-ask"));
        Assert.Contains("did not start", page.Find("#car-ask-unavailable").TextContent);
    }

    /// <summary>An own topic has nothing to fetch, so it gets what to publish and what last arrived.</summary>
    [Fact]
    public void An_own_topic_gets_the_payload_it_wants_and_the_last_thing_it_saw()
    {
        _held.Set(new VehicleState(Reported, SocPercent: 41));

        var page = Page(Id4 with { TelemetryTopic = "gleanvolt/vehicle/id4/state" }, VehicleFeed.OwnTopic);

        var sentence = page.Find("#car-ask-own-topic").TextContent;

        Assert.Empty(page.FindAll("#car-ask"));
        Assert.Contains("gleanvolt/vehicle/id4/state", sentence);
        Assert.Contains("captured_at", sentence);
        Assert.Contains("41%", sentence);
    }

    [Fact]
    public void An_own_topic_with_nothing_on_it_yet_says_so()
    {
        var page = Page(Id4 with { TelemetryTopic = "gleanvolt/vehicle/id4/state" }, VehicleFeed.OwnTopic);

        Assert.Contains("Nothing has arrived on it yet", page.Find("#car-ask-own-topic").TextContent);
    }

    /// <summary>Charge-only: nothing to press, and nothing that reads as an unfinished form.</summary>
    [Fact]
    public void Charge_only_reads_as_a_supported_installation_rather_than_an_error()
    {
        var page = Page();

        Assert.Empty(page.FindAll("#car-ask"));
        Assert.Empty(page.FindAll(".error"));
        Assert.Contains("nothing reads this car", page.Find("#car-ask-charge-only").TextContent);
    }

    // -------------------------------------------------- asking the Data Act portal (#137/#139/#178)

    /// <summary>
    /// The missing fields are still named — this is the one installation that asked for this portal —
    /// but the fix is the form above rather than three <c>VW_*</c> keys in <c>.env</c> (#214).
    /// </summary>
    [Fact]
    public void An_unconfigured_portal_offers_no_button_and_points_at_the_form()
    {
        _portal = new StubReader(configured: false);

        var page = Page(feed: VehicleFeed.DataAct);

        Assert.Empty(page.FindAll("#car-ask"));
        Assert.Contains("a brand", page.Find("#portal-unconfigured").TextContent);
        Assert.Contains("the form above", page.Markup);
        Assert.DoesNotContain("VW_BRAND", page.Markup);
    }

    [Fact]
    public void Nothing_is_read_until_the_button_is_pressed()
    {
        var reader = new StubReader();
        _portal = reader;

        var page = Page(feed: VehicleFeed.DataAct);

        Assert.Equal(0, reader.Reads);
        page.Find("#car-ask").Click();
        Assert.Equal(1, reader.Reads);
    }

    [Fact]
    public void A_reading_shows_what_the_car_said()
    {
        var page = DataActPage(new VehiclePortalReading(
            Succeeded: true,
            State: new VehicleState(
                new DateTimeOffset(2026, 9, 1, 5, 27, 11, TimeSpan.Zero),
                SocPercent: 64,
                RangeKm: 312,
                ChargeState: VehicleChargeState.Idle,
                PlugState: VehiclePlugState.Disconnected),
            Vehicle: "…1234",
            SnapshotCount: 6));

        page.Find("#car-ask").Click();

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
        var page = DataActPage(VehiclePortalReading.Failed(
            "OwnerActionRequired", "the portal is showing consent", worthRetrying: false));

        page.Find("#car-ask").Click();

        Assert.Contains("OwnerActionRequired", page.Markup);
        Assert.Contains("will not help", page.Markup);
        Assert.DoesNotContain("Worth pressing again", page.Markup);
    }

    /// <summary>The other half: ordinary, self-clearing, and the button is the fix.</summary>
    [Fact]
    public void An_expired_session_invites_another_press()
    {
        var page = DataActPage(VehiclePortalReading.Failed(
            "SessionExpired", "the session is gone", worthRetrying: true));

        page.Find("#car-ask").Click();

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
        var page = DataActPage(new VehiclePortalReading(
            Succeeded: true,
            State: new VehicleState(DateTimeOffset.UnixEpoch, SocPercent: 50),
            UnmappedFields: ["some.unknown.field", "another_one"]));

        page.Find("#car-ask").Click();

        Assert.Contains("some.unknown.field", page.Markup);
        Assert.Contains("another_one", page.Markup);
    }

    [Fact]
    public void A_bundle_nothing_matched_lists_the_names_it_did_not_match()
    {
        // The case observed live: the portal answered, the capture time was minutes old, and every
        // value was blank. It is a failure rather than a reading of dashes -- and it is exactly the
        // failure whose field list is the whole answer, so the page must not withhold it.
        var page = DataActPage(VehiclePortalReading.Failed(
            "UnusableData",
            "the bundle was read and none of its 14 field(s) are ones this build recognises",
            worthRetrying: false,
            unmapped: ["odometer_km_v2", "battery.soc_pct"]));

        page.Find("#car-ask").Click();

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
        var page = DataActPage(VehiclePortalReading.Failed(
            "UnusableData",
            "the bundle was read and none of its 47 field(s) are ones this build recognises",
            worthRetrying: false,
            unmapped: ["settings.auto_unlock_ac"],
            matched: new Dictionary<string, VehicleFieldReading>
            {
                ["charging_state_report.charging_state"] = new("invalid", Reported),
                ["mileage.value"] = new("24680", Reported),
            }));

        page.Find("#car-ask").Click();

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
        var page = DataActPage(VehiclePortalReading.Failed(
            "UnusableData",
            "the bundle held no dated report with any readings in it",
            worthRetrying: false,
            diagnostics: ["1 of 3 report(s) were dropped for carrying no timestamp this build recognises."],
            dropped: ["battery_level_HV.value"]));

        page.Find("#car-ask").Click();

        Assert.Contains("dropped for carrying no timestamp", page.Markup);
        Assert.Contains("battery_level_HV.value", page.Markup);
    }

    [Fact]
    public void The_delivery_is_described_even_when_nothing_could_be_mapped_out_of_it()
    {
        // How many snapshots arrived and what they span is how "this quarter-hour said nothing" is
        // told apart from "the portal handed us one report type and the battery is in another
        // delivery". On a failure that is half the diagnosis, so it cannot live in the success branch.
        var page = DataActPage(new VehiclePortalReading(
            Succeeded: false,
            Vehicle: "…1234",
            SnapshotCount: 3,
            OldestSnapshot: new DateTimeOffset(2026, 9, 2, 9, 59, 0, TimeSpan.Zero),
            NewestSnapshot: new DateTimeOffset(2026, 9, 2, 10, 29, 0, TimeSpan.Zero),
            OdometerKm: 53065,
            TargetSocPercent: 80,
            FailureKind: "UnusableData",
            Message: "none of its 47 field(s) are ones this build recognises"));

        page.Find("#car-ask").Click();

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
        var page = DataActPage(VehiclePortalReading.Failed(
            "UnusableData", "nothing recognised", worthRetrying: false,
            matched: new Dictionary<string, VehicleFieldReading>
            {
                ["energy_contents.maximal_energy_content.physical_value"] = new("738.0", Reported),
            }));

        page.Find("#car-ask").Click();

        Assert.Contains("class=\"fields\"", page.Markup);
        Assert.Contains("energy_<wbr>contents.<wbr>maximal_<wbr>energy_<wbr>content.<wbr>physical_<wbr>value",
            page.Markup);
    }

    [Fact]
    public void A_field_name_from_the_portal_cannot_smuggle_markup_onto_the_page()
    {
        // These strings come from outside and this is the one place the page emits markup rather than
        // text, so the encoding is pinned rather than assumed.
        var page = DataActPage(VehiclePortalReading.Failed(
            "UnusableData", "nothing recognised", worthRetrying: false,
            matched: new Dictionary<string, VehicleFieldReading>
            {
                ["<script>alert(1)</script>"] = new("<b>x</b>", Reported),
            }));

        page.Find("#car-ask").Click();

        Assert.DoesNotContain("<script>alert(1)</script>", page.Markup);
        Assert.DoesNotContain("<b>x</b>", page.Markup);
        Assert.Contains("&lt;script&gt;", page.Markup);
    }

    /// <summary>
    /// A successful read that carries a state of charge, for the owner-facing tests below. Its
    /// capture time is <see cref="Reported"/>, two hours before the fixture's clock.
    /// </summary>
    private static VehiclePortalReading Answered(
        double? soc = 64,
        double? targetSoc = 80,
        TimeSpan? timeLeft = null) =>
        new(
            Succeeded: true,
            State: new VehicleState(
                Reported,
                SocPercent: soc,
                RangeKm: 312,
                ChargeTimeRemaining: timeLeft,
                ChargeState: VehicleChargeState.Charging,
                PlugState: VehiclePlugState.Connected),
            Vehicle: "…1234",
            SnapshotCount: 6,
            TargetSocPercent: targetSoc,
            OdometerKm: 53065);

    /// <summary>
    /// The point of #178. The battery percentage used to be the fourth thing on the page, behind
    /// three developer-facing sections; the card leads the read, in the same labels the dashboard's
    /// card uses so the two can be read side by side without translating between them.
    /// </summary>
    [Fact]
    public void The_read_answers_in_the_dashboard_cards_own_figures()
    {
        var page = DataActPage(Answered());
        page.Find("#car-ask").Click();

        var card = page.Find("#car").TextContent;

        Assert.Contains("Car battery", card);
        Assert.Contains("64%", card);
        Assert.Contains("Car range", card);
        Assert.Contains("312 km", card);
        Assert.Contains("Car charge state", card);
        Assert.Contains("Charging", card);
        Assert.Contains("Car plug state", card);
        Assert.Contains("Connected", card);
    }

    /// <summary>
    /// The two the portal carries and the dashboard's card cannot. Their absence is what makes the
    /// card an incomplete answer, so a page that led with the car and dropped them would have moved
    /// the problem rather than solved it.
    /// </summary>
    [Fact]
    public void The_card_carries_the_two_figures_the_dashboard_has_no_source_for()
    {
        var page = DataActPage(Answered(timeLeft: TimeSpan.FromMinutes(95)));
        page.Find("#car-ask").Click();

        var card = page.Find("#car").TextContent;

        Assert.Contains("Target SOC", card);
        Assert.Contains("80%", card);
        Assert.Contains("Time left", card);
        Assert.Contains("95 min", card);
    }

    /// <summary>
    /// The age is the <b>car's</b>, counted from its capture time rather than from the press. A page
    /// that timed its own button would report a fresh reading of an hours-old fact, which is the one
    /// thing this data most needs stated.
    /// </summary>
    [Fact]
    public void The_reading_age_is_counted_from_the_cars_capture_time_not_the_press()
    {
        var page = DataActPage(Answered());
        page.Find("#car-ask").Click();

        // Reported at 10:29:46, the fixture's clock at 12:29:46.
        Assert.Contains("2.0 h", page.Find("#car").TextContent);
    }

    /// <summary>
    /// Kept, and out of the way. The delivery breakdown and the field lists are the only thing that
    /// answers "why is this field missing" (#140 needed them in anger), but an owner reading a
    /// battery percentage is not asking that.
    /// </summary>
    [Fact]
    public void A_read_that_worked_puts_the_field_lists_behind_a_collapsed_disclosure()
    {
        var page = DataActPage(Answered() with
        {
            UnmappedFields = ["settings.auto_unlock_ac"],
        });

        page.Find("#car-ask").Click();

        var diagnostics = page.Find("#diagnostics");

        Assert.False(diagnostics.HasAttribute("open"));
        Assert.Contains("Diagnostics", diagnostics.TextContent);

        // Still there, and still complete -- collapsed is not dropped.
        Assert.Contains("settings.auto_unlock_ac", diagnostics.TextContent);
        Assert.Contains("53065", diagnostics.TextContent);
    }

    /// <summary>
    /// The exception, and the reason the disclosure is not simply always shut: a bundle in which
    /// nothing matched is a failure whose field list <b>is</b> the diagnosis. Hiding it behind a
    /// click would be the page reporting "unusable data" while holding the answer in its hand.
    /// </summary>
    [Fact]
    public void A_read_that_failed_opens_the_disclosure_because_that_is_the_whole_answer()
    {
        var page = DataActPage(VehiclePortalReading.Failed(
            "UnusableData",
            "none of its 47 field(s) are ones this build recognises",
            worthRetrying: false,
            unmapped: ["odometer_km_v2"]));

        page.Find("#car-ask").Click();

        Assert.True(page.Find("#diagnostics").HasAttribute("open"));
        Assert.Contains("odometer_km_v2", page.Markup);
    }

    /// <summary>
    /// A lid over nothing. Not reachable from the real reader — a success is a bundle something was
    /// mapped out of — but the sections inside would answer "not one field is in the vocabulary" and
    /// "every field was recognised" together, so the disclosure is decided on content rather than on
    /// the success flag.
    /// </summary>
    [Fact]
    public void A_reading_with_nothing_in_it_gets_no_disclosure_rather_than_an_empty_one()
    {
        var page = DataActPage(new VehiclePortalReading(Succeeded: true));
        page.Find("#car-ask").Click();

        Assert.Empty(page.FindAll("#diagnostics"));
    }

    /// <summary>
    /// The diagnostics are Data-Act-shaped and appear only for a Data Act read. A live feed's
    /// equivalent is the Feed section's health, which is on the same page.
    /// </summary>
    [Fact]
    public void A_live_feeds_press_gets_no_portal_diagnostics()
    {
        _refresh = new FakeVehicleStateRefresh(new VehicleState(Reported, SocPercent: 64));
        Services.AddSingleton<IVehicleUpdateService>(new StubFeed());

        var page = Page(feed: VehicleFeed.Volkswagen);
        page.Find("#car-ask").Click();

        Assert.Empty(page.FindAll("#diagnostics"));
    }

    /// <summary>
    /// This press and the feed are different sessions asking at different moments, and the page says
    /// so: a couple of points apart is the expected outcome, not evidence of a fault. Without the
    /// comparison an owner has to open two tabs and do the subtraction themselves.
    /// </summary>
    [Fact]
    public void A_press_is_shown_against_what_the_feed_is_currently_holding()
    {
        _held.Set(new VehicleState(
            Reported - TimeSpan.FromMinutes(40), SocPercent: 61, SourceId: "vw-group …1234"));

        var page = DataActPage(Answered(soc: 64));
        page.Find("#car-ask").Click();

        var against = page.Find("#against-the-feed").TextContent;

        Assert.Contains("61%", against);
        Assert.Contains("vw-group …1234", against);
        Assert.Contains("3 points below this press", against);
    }

    /// <summary>Two sources agreeing is a finding too, and must not read as a disagreement of zero.</summary>
    [Fact]
    public void Two_sources_that_agree_are_said_to_agree()
    {
        _held.Set(new VehicleState(Reported - TimeSpan.FromMinutes(40), SocPercent: 64));

        var page = DataActPage(Answered(soc: 64));
        page.Find("#car-ask").Click();

        Assert.Contains("the same state of charge", page.Find("#against-the-feed").TextContent);
    }

    /// <summary>
    /// No feed is a supported install rather than a fault, so the page says there is nothing to
    /// compare against instead of comparing against a blank.
    /// </summary>
    [Fact]
    public void With_no_feed_the_page_says_there_is_nothing_to_hold_the_press_against()
    {
        var page = DataActPage(Answered());
        page.Find("#car-ask").Click();

        var against = page.Find("#against-the-feed").TextContent;

        Assert.Contains("Nothing has reached the dashboard's card yet", against);
        Assert.DoesNotContain("below this press", against);
    }
}
