using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The form that configures the car from /car (issue #214). What it has to get right: one feed choice
/// shows exactly that choice's fields, switching disables the feed it leaves in the same save, the car's
/// own fields are editable whichever choice is made, what is saved but not yet running is said, and an
/// installation moved off a removed built-in feed (#477) is told which plugin to run.
/// </summary>
public class CarEditorFormTests : BunitContext
{
    private readonly FakeEvEditor _editor = new();
    private readonly FakeServiceShutdown _shutdown = new();
    private readonly FakeRetiredCarFeed _retired = new();

    public CarEditorFormTests()
    {
        Services.AddSingleton<IEvEditor>(_editor);
        Services.AddSingleton<IRetiredCarFeed>(_retired);
        Services.AddSingleton<IServiceShutdown>(_shutdown);

        // A login, which is what makes the form editable at all.
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));

        // The restart asks the browser to watch for the controller coming back; the script is not here.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static string Field(string key) => "#car-" + key.Replace(':', '-');

    private IRenderedComponent<CarEditorForm> Form(VehicleFeed? feed = null)
    {
        if (feed is { } chosen)
        {
            _editor.RunningFeed = chosen;

            if (VehicleFeeds.EnabledKey(chosen) is { } key)
            {
                _editor.Running[key] = "true";
            }
        }

        return Render<CarEditorForm>();
    }

    // -- The feed choice.

    [Fact]
    public void Charge_only_is_what_an_undescribed_installation_starts_on()
    {
        var form = Form();

        Assert.Equal("ChargeOnly", form.Find("#car-feed").GetAttribute("value"));

        // Not an error state, and it must not read like an unfinished form: every EV with a Type 2
        // inlet charges without a feed.
        Assert.Contains("supported setup", form.Find("#car-feed-note").TextContent);
        Assert.Empty(form.FindAll(Field(EvSettingKeys.TelemetryTopic)));
    }

    [Fact]
    public void The_choices_are_a_plugin_the_own_topic_or_charge_only()
    {
        var options = Form().Find("#car-feed").QuerySelectorAll("option").Select(option => option.GetAttribute("value"));

        Assert.Equal(["External", "OwnTopic", "ChargeOnly"], options);
    }

    [Fact]
    public void An_installation_moved_off_a_built_in_is_told_which_plugin_to_run()
    {
        // #477: the start moved it to "A plugin"; the notice names the plugin until one connects.
        _retired.Moved(CarFeedPlugin.VwGroup);
        var form = Form(VehicleFeed.External);

        var notice = form.Find("#car-feed-retired").TextContent;
        Assert.Contains("VW Group Data Act plugin", notice);
        Assert.Contains("A plugin", notice);
        Assert.Contains("password", notice);

        // A car plugin connects while the page is open.
        _retired.Connect();
        form.WaitForAssertion(() => Assert.Empty(form.FindAll("#car-feed-retired")));
    }

    [Fact]
    public void No_built_in_moved_means_no_notice() => Assert.Empty(Form(VehicleFeed.External).FindAll("#car-feed-retired"));

    [Fact]
    public void The_Skoda_notice_asks_for_the_API_key_not_a_password()
    {
        _retired.Moved(CarFeedPlugin.Skoda);
        var notice = Form(VehicleFeed.External).Find("#car-feed-retired").TextContent;

        Assert.Contains("API key", notice);
        Assert.DoesNotContain("password", notice);
    }

    [Theory]
    [InlineData(VehicleFeed.OwnTopic, EvSettingKeys.TelemetryTopic)]
    public void Each_choice_shows_its_own_fields_and_no_other(VehicleFeed feed, string owned)
    {
        var form = Form(feed);

        Assert.NotNull(form.Find(Field(owned)));

        var others = VehicleFeeds.All
            .Where(candidate => candidate != feed)
            .SelectMany(VehicleFeeds.Fields)
            .Where(key => !VehicleFeeds.Fields(feed).Contains(key));

        Assert.All(others, key => Assert.Empty(form.FindAll(Field(key))));
    }

    [Fact]
    public void Choosing_a_feed_changes_which_fields_exist_before_anything_is_saved()
    {
        var form = Form();

        form.Find("#car-feed").Change("OwnTopic");

        Assert.NotNull(form.Find(Field(EvSettingKeys.TelemetryTopic)));
        Assert.Empty(_editor.Saves);
    }

    [Fact]
    public void Switching_sends_the_choice_and_the_new_fields_as_one_save()
    {
        var form = Form(VehicleFeed.External);

        form.Find("#car-feed").Change("OwnTopic");
        form.Find(Field(EvSettingKeys.TelemetryTopic)).Input("cars/id4");
        form.Find("#car-save").Click();

        Assert.Equal([VehicleFeed.OwnTopic], _editor.Switches);
        Assert.Equal("cars/id4", Assert.Single(_editor.Saves)[EvSettingKeys.TelemetryTopic]);

        // The half-applied switch this exists to prevent.
        Assert.Equal("false", _editor.Stored[EvSettingKeys.ExternalEnabled]);
        Assert.Equal("true", _editor.Stored[EvSettingKeys.MqttEnabled]);
    }

    [Fact]
    public void Switching_alone_is_a_change_worth_saving()
    {
        var form = Form(VehicleFeed.External);

        form.Find("#car-feed").Change("ChargeOnly");
        form.Find("#car-save").Click();

        Assert.Equal([VehicleFeed.ChargeOnly], _editor.Switches);
    }

    // -- The car's own fields.

    [Fact]
    public void The_cars_fields_are_editable_with_no_feed_at_all()
    {
        // Phases, the currents and the pack are what make charging correct, and none of them depends
        // on anything reading the car.
        var form = Form();

        form.Find(Field(EvSettingKeys.Phases)).Change("1");
        form.Find("#car-save").Click();

        Assert.Equal("1", Assert.Single(_editor.Saves)[EvSettingKeys.Phases]);
        Assert.Empty(_editor.Switches);
    }

    [Fact]
    public void Phases_is_a_choice_rather_than_free_text()
    {
        // The one that goes wrong quietly: a single-phase car behind a three-phase wallbox has every
        // power figure overstated threefold.
        var form = Form();

        var options = form.Find(Field(EvSettingKeys.Phases)).QuerySelectorAll("option")
            .Select(option => option.GetAttribute("value"));

        Assert.Equal(["", "1", "2", "3"], options);
    }

    [Fact]
    public void The_installations_band_is_shown_beside_the_currents() =>
        Assert.Contains("6–16 A", Form().Find("#car-editor").TextContent);

    [Fact]
    public void Saves_only_what_changed()
    {
        var form = Form();

        form.Find(Field(EvSettingKeys.Model)).Input("ID.4 GTX");
        form.Find("#car-save").Click();

        var saved = Assert.Single(_editor.Saves);
        Assert.Equal(new Dictionary<string, string> { [EvSettingKeys.Model] = "ID.4 GTX" }, saved);
    }

    [Fact]
    public void Says_where_each_value_comes_from_and_reverts_what_this_page_set()
    {
        _editor.Stored[EvSettingKeys.Phases] = "1";

        var form = Form();
        var phases = form.Find(Field(EvSettingKeys.Phases)).ParentElement!;

        Assert.Contains("web UI", phases.QuerySelector(".source")!.TextContent);
        Assert.Contains("Revert to 3", phases.QuerySelector(".revert")!.TextContent);

        form.Find(".revert").Click();

        Assert.Equal([EvSettingKeys.Phases], _editor.Reverts);
        Assert.Equal("3", form.Find(Field(EvSettingKeys.Phases)).GetAttribute("value"));
    }

    // -- Saved, not applied.

    [Fact]
    public void Lists_what_is_saved_but_not_running_with_the_restart_beside_it()
    {
        _editor.RunningFeed = VehicleFeed.ChargeOnly;
        _editor.Stored[EvSettingKeys.ExternalEnabled] = "true";
        _editor.Stored[EvSettingKeys.Phases] = "1";

        var form = Form();
        var pending = form.Find("#car-pending");

        Assert.Contains("Saved, not applied", pending.TextContent);
        Assert.Contains("Another manufacturer — charge only → A plugin", pending.TextContent);
        Assert.Contains("3 → 1", pending.TextContent);

        form.Find("#restart").Click();
        form.Find("#restart-confirm").Click();

        Assert.Equal(["Web UI"], _shutdown.Restarts);
    }

    [Fact]
    public void Nothing_pending_means_no_banner() => Assert.Empty(Form().FindAll("#car-pending"));

    // -- Refusals and the read-only rule.

    [Fact]
    public void Shows_the_refusal_and_keeps_what_was_typed()
    {
        _editor.Refuse = ["Ev:Vehicles:0:Phases (4) must be 1, 2 or 3."];
        var form = Form();

        form.Find(Field(EvSettingKeys.MinChargingCurrentAmps)).Input("8");
        form.Find("#car-save").Click();

        Assert.Contains("must be 1, 2 or 3", form.Find("#car-problems").TextContent);
        Assert.Empty(_editor.Stored);
        Assert.Equal("8", form.Find(Field(EvSettingKeys.MinChargingCurrentAmps)).GetAttribute("value"));
    }

    [Fact]
    public void Without_a_login_the_form_is_read_only()
    {
        // Without a login anyone on the LAN could change what reads the car.
        Services.AddSingleton(Options.Create(new WebOptions()));
        Services.AddSingleton(new WebPasswordState(null));
        var form = Render<CarEditorForm>();

        Assert.Contains("no login", form.Find("#car-readonly").TextContent);
        Assert.All(form.FindAll("input, select"), control => Assert.True(control.HasAttribute("disabled")));
        Assert.Empty(form.FindAll("#car-save"));
    }

    [Fact]
    public void A_password_set_after_startup_makes_the_form_editable_without_a_restart()
    {
        // Issue #254: /setup records the hash in WebPasswordState, never in the startup WebOptions.
        Services.AddSingleton(Options.Create(new WebOptions()));
        var password = new WebPasswordState(null);
        Services.AddSingleton(password);
        password.Set("hash");

        var form = Form();

        Assert.Empty(form.FindAll("#car-readonly"));
        Assert.NotNull(form.Find("#car-save"));
    }

    [Fact]
    public void A_host_that_cannot_apply_edits_says_so_and_takes_none()
    {
        _editor.Unavailable = "This host does not read a web UI overrides file.";

        var form = Form();

        Assert.Contains("does not read", form.Find("#car-unavailable").TextContent);
        Assert.All(form.FindAll("input, select"), control => Assert.True(control.HasAttribute("disabled")));
        Assert.Empty(form.FindAll("#car-save"));
    }

    [Theory]
    [InlineData(VehicleFeed.External)]
    [InlineData(VehicleFeed.OwnTopic)]
    [InlineData(VehicleFeed.ChargeOnly)]
    public void Every_string_on_the_form_comes_from_a_resource(VehicleFeed feed)
    {
        _retired.Moved(CarFeedPlugin.Volkswagen);
        var form = PseudoLocale.Render(() => Form(feed));

        PseudoLocale.AssertTranslated(form.Markup, "Volkswagen");
    }
}
