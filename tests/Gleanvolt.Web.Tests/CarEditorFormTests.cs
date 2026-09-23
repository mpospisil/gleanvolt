using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The form that configures the car from /car (issue #214). What it has to get right: one manufacturer
/// choice shows exactly that choice's fields, switching disables the feed it leaves in the same save,
/// the car's own fields are editable whichever choice is made, a password is never rendered back and
/// an empty one means unchanged, and what is saved but not yet running is said without its values.
/// </summary>
public class CarEditorFormTests : BunitContext
{
    private readonly FakeEvEditor _editor = new();
    private readonly FakeServiceShutdown _shutdown = new();

    public CarEditorFormTests()
    {
        Services.AddSingleton<IEvEditor>(_editor);
        Services.AddSingleton<IServiceShutdown>(_shutdown);

        // A login, which is what makes the form editable at all.
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));

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

    // -- The manufacturer choice.

    [Fact]
    public void Charge_only_is_what_an_undescribed_installation_starts_on()
    {
        var form = Form();

        Assert.Equal("ChargeOnly", form.Find("#car-feed").GetAttribute("value"));

        // Not an error state, and it must not read like an unfinished form: every EV with a Type 2
        // inlet charges without a feed.
        Assert.Contains("supported installation", form.Find("#car-feed-note").TextContent);
        Assert.Empty(form.FindAll(Field(EvSettingKeys.WebsiteVin)));
    }

    [Theory]
    [InlineData(VehicleFeed.Volkswagen, EvSettingKeys.WebsiteVin)]
    [InlineData(VehicleFeed.Skoda, EvSettingKeys.SkodaVin)]
    [InlineData(VehicleFeed.DataAct, EvSettingKeys.DataActVin)]
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
    public void Choosing_a_manufacturer_changes_which_fields_exist_before_anything_is_saved()
    {
        var form = Form();

        form.Find("#car-feed").Change("Skoda");

        Assert.NotNull(form.Find(Field(EvSettingKeys.SkodaVin)));
        Assert.Empty(_editor.Saves);
    }

    [Fact]
    public void The_brand_list_is_the_clients_own()
    {
        // Not a literal in this file: the day a brand is added to the table it appears here.
        var form = Form(VehicleFeed.DataAct);

        var options = form.Find(Field(EvSettingKeys.DataActBrand)).QuerySelectorAll("option")
            .Select(option => option.GetAttribute("value"))
            .ToList();

        Assert.Equal(["", "vw", "audi", "cupra"], options);
    }

    [Fact]
    public void The_client_id_is_behind_a_disclosure_rather_than_in_the_form()
    {
        // Documentation for a failure, not a field to fill.
        var form = Form(VehicleFeed.DataAct);

        Assert.NotNull(form.Find(".car-escape-hatch " + Field(EvSettingKeys.DataActClientId)));
    }

    [Fact]
    public void Switching_sends_the_choice_and_the_new_fields_as_one_save()
    {
        var form = Form(VehicleFeed.Volkswagen);

        form.Find("#car-feed").Change("Skoda");
        form.Find(Field(EvSettingKeys.SkodaVin)).Input("TMBJB9NY0M0000001");
        form.Find("#car-save").Click();

        Assert.Equal([VehicleFeed.Skoda], _editor.Switches);
        Assert.Equal("TMBJB9NY0M0000001", Assert.Single(_editor.Saves)[EvSettingKeys.SkodaVin]);

        // The half-applied switch this exists to prevent.
        Assert.Equal("false", _editor.Stored[EvSettingKeys.WebsiteEnabled]);
        Assert.Equal("true", _editor.Stored[EvSettingKeys.SkodaEnabled]);
    }

    [Fact]
    public void Switching_alone_is_a_change_worth_saving()
    {
        var form = Form(VehicleFeed.Skoda);
        _editor.Running[EvSettingKeys.SkodaVin] = "TMBJB9NY0M0000001";

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

    // -- Secrets.

    [Fact]
    public void A_password_field_renders_empty_even_when_one_is_stored()
    {
        _editor.Secrets[EvSettingKeys.WebsitePassword] = true;
        _editor.RunningSecrets[EvSettingKeys.WebsitePassword] = true;

        var form = Form(VehicleFeed.Volkswagen);
        var password = form.Find(Field(EvSettingKeys.WebsitePassword));

        Assert.Equal("password", password.GetAttribute("type"));
        Assert.Equal(string.Empty, password.GetAttribute("value"));
        Assert.Equal("unchanged", password.GetAttribute("placeholder"));
        Assert.Contains("secret store", password.ParentElement!.QuerySelector(".source")!.TextContent);
    }

    [Fact]
    public void An_empty_password_box_is_not_a_change()
    {
        _editor.Secrets[EvSettingKeys.WebsitePassword] = true;
        _editor.RunningSecrets[EvSettingKeys.WebsitePassword] = true;
        var form = Form(VehicleFeed.Volkswagen);

        form.Find(Field(EvSettingKeys.WebsiteVin)).Input("WVWZZZE2ZMP000001");
        form.Find("#car-save").Click();

        Assert.DoesNotContain(EvSettingKeys.WebsitePassword, Assert.Single(_editor.Saves).Keys);
        Assert.Empty(_editor.SavedPasswords);
    }

    [Fact]
    public void A_typed_password_is_sent_once_and_never_comes_back()
    {
        var form = Form(VehicleFeed.Volkswagen);

        form.Find(Field(EvSettingKeys.WebsitePassword)).Input("hunter2");
        form.Find("#car-save").Click();

        Assert.Equal(["hunter2"], _editor.SavedPasswords);
        Assert.DoesNotContain("hunter2", form.Markup, StringComparison.Ordinal);
        Assert.Equal(string.Empty, form.Find(Field(EvSettingKeys.WebsitePassword)).GetAttribute("value"));
    }

    [Fact]
    public void Remove_clears_a_stored_password()
    {
        _editor.Secrets[EvSettingKeys.WebsitePassword] = true;
        _editor.RunningSecrets[EvSettingKeys.WebsitePassword] = true;
        var form = Form(VehicleFeed.Volkswagen);

        form.Find(Field(EvSettingKeys.WebsitePassword) + "-remove").Click();

        Assert.Equal([EvSettingKeys.WebsitePassword], _editor.Reverts);
        Assert.Equal("not set", form.Find(Field(EvSettingKeys.WebsitePassword)).GetAttribute("placeholder"));
    }

    [Fact]
    public void The_pending_banner_names_a_password_change_without_its_value()
    {
        _editor.RunningFeed = VehicleFeed.Volkswagen;
        _editor.Running[EvSettingKeys.WebsiteEnabled] = "true";
        var form = Form();

        form.Find(Field(EvSettingKeys.WebsitePassword)).Input("hunter2");
        form.Find("#car-save").Click();

        var pending = form.Find("#car-pending");
        Assert.Contains("Password: set", pending.TextContent);
        Assert.DoesNotContain("hunter2", form.Markup, StringComparison.Ordinal);
    }

    // -- Saved, not applied.

    [Fact]
    public void Lists_what_is_saved_but_not_running_with_the_restart_beside_it()
    {
        _editor.RunningFeed = VehicleFeed.ChargeOnly;
        _editor.Stored[EvSettingKeys.SkodaEnabled] = "true";
        _editor.Stored[EvSettingKeys.Phases] = "1";

        var form = Form();
        var pending = form.Find("#car-pending");

        Assert.Contains("Saved, not applied", pending.TextContent);
        Assert.Contains("Another manufacturer — charge only → Škoda", pending.TextContent);
        Assert.Contains("3 → 1", pending.TextContent);

        form.Find("#restart").Click();
        form.Find("#restart-confirm").Click();

        Assert.Equal(["Web UI"], _shutdown.Restarts);
    }

    [Fact]
    public void Nothing_pending_means_no_banner() => Assert.Empty(Form().FindAll("#car-pending"));

    [Fact]
    public void A_save_points_at_where_the_account_is_actually_proved()
    {
        var form = Form();

        form.Find(Field(EvSettingKeys.Model)).Input("ID.4 GTX");
        form.Find("#car-save").Click();

        Assert.Contains("#account", form.Find("#car-message a").GetAttribute("href"));
    }

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
        // These fields are a manufacturer account: without a login anyone on the LAN could read the
        // car, or point it at their own.
        Services.AddSingleton(Options.Create(new WebOptions()));
        var form = Render<CarEditorForm>();

        Assert.Contains("no login", form.Find("#car-readonly").TextContent);
        Assert.All(form.FindAll("input, select"), control => Assert.True(control.HasAttribute("disabled")));
        Assert.Empty(form.FindAll("#car-save"));
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
}
