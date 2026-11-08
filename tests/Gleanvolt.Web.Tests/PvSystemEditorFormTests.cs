using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The form that edits the installation from /pv-system (issue #204). What it has to get right: it
/// sends only what changed, shows the startup resolver's refusal instead of saving, probes a changed
/// device address before saving, asks before a change that moves topics, and says what is saved but
/// not yet running, and where the restart is.
/// </summary>
public class PvSystemEditorFormTests : BunitContext
{
    private readonly FakePvSystemEditor _editor = new();

    public PvSystemEditorFormTests()
    {
        Services.AddSingleton<IPvSystemEditor>(_editor);

        // A login, which is what makes the form editable at all.
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
    }

    private static string Field(string key) => "#pv-" + key.Replace(':', '-');

    // The guide the unmetered-phase tip links to lives in the repository, not in the app.
    private static string RepoRoot()
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Gleanvolt.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new InvalidOperationException("Gleanvolt.slnx not found above " + AppContext.BaseDirectory);
    }

    [Fact]
    public void Says_where_each_value_comes_from()
    {
        _editor.Stored[PvSystemSettingKeys.TiltDegrees] = "30";

        var form = Render<PvSystemEditorForm>();

        // In the (i) beside each label (#255), where the line under the box used to be.
        var tilt = form.Find(Field(PvSystemSettingKeys.TiltDegrees)).ParentElement!;
        Assert.Contains("comes from this page", tilt.QuerySelector(".info-pop")!.TextContent);
        Assert.Contains("Revert to 35", tilt.QuerySelector(".revert")!.TextContent);

        var host = form.Find(Field(PvSystemSettingKeys.InverterHost)).ParentElement!;
        Assert.Contains("comes from the environment file", host.QuerySelector(".info-pop")!.TextContent);
        Assert.Null(host.QuerySelector(".revert"));
    }

    [Fact]
    public void Every_field_says_what_it_means_before_where_it_comes_from()
    {
        var form = Render<PvSystemEditorForm>();

        foreach (var key in PvSystemSettingKeys.All)
        {
            var tip = form.Find(Field(key)).ParentElement!.QuerySelectorAll(".info-pop p");
            Assert.True(tip.Length == 3, $"{key}: expected a meaning and two lines of source, got {tip.Length} paragraphs.");
            Assert.False(string.IsNullOrWhiteSpace(tip[0].TextContent), $"{key} has no explanation.");
            Assert.DoesNotContain("Current value comes from", tip[0].TextContent);
        }

        var phase = form.Find(Field(PvSystemSettingKeys.InverterUnmeteredGridPhase)).ParentElement!;
        var link = phase.QuerySelector(".info-pop p a")!;
        Assert.EndsWith("docs/UNMETERED_GRID_PHASE.md", link.GetAttribute("href"));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", "UNMETERED_GRID_PHASE.md")));
        Assert.True(File.Exists(Path.Combine(RepoRoot(), "docs", "UNMETERED_GRID_PHASE.cs.md")));
    }

    [Fact]
    public void Saves_only_what_changed()
    {
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.TiltDegrees)).Input("30");
        form.Find("#pv-save").Click();

        var saved = Assert.Single(_editor.Saves);
        Assert.Equal(new Dictionary<string, string> { [PvSystemSettingKeys.TiltDegrees] = "30" }, saved);
        Assert.Contains("Restart the controller", form.Find("#pv-message").TextContent);
    }

    [Fact]
    public void Shows_the_resolvers_refusal_and_keeps_what_was_typed()
    {
        _editor.Refuse = ["Pv:LossFactor must be in (0, 1]; got 2."];
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.LossFactor)).Input("2");
        form.Find("#pv-save").Click();

        Assert.Contains("Pv:LossFactor must be in (0, 1]", form.Find("#pv-problems").TextContent);
        Assert.Empty(_editor.Stored);
        Assert.Equal("2", form.Find(Field(PvSystemSettingKeys.LossFactor)).GetAttribute("value"));
    }

    [Fact]
    public void Probes_a_changed_device_address_before_saving_and_saves_whatever_it_finds()
    {
        _editor.ProbeResult = new DeviceProbeResult(false, false, "192.168.2.20:502 did not answer.");
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.ChargerHost)).Input("192.168.2.20");
        form.Find("#pv-save").Click();

        var probe = Assert.Single(_editor.Probes);
        Assert.Equal((PvDeviceRole.Charger, "192.168.2.20", 502, (byte)1), probe);
        Assert.Contains("did not answer", form.Find("#pv-probes").TextContent);
        Assert.Equal("192.168.2.20", _editor.Stored[PvSystemSettingKeys.ChargerHost]);
    }

    // -- The inverter's brand (#318).

    [Fact]
    public void Offers_every_driver_with_the_default_selected_when_none_is_configured()
    {
        var form = Render<PvSystemEditorForm>();

        var select = form.Find(Field(PvSystemSettingKeys.InverterDriver));
        Assert.Equal("SELECT", select.TagName);
        Assert.Equal(["solax", "acme"], select.QuerySelectorAll("option").Select(option => option.GetAttribute("value")));
        Assert.Equal(["SolaX", "Acme"], select.QuerySelectorAll("option").Select(option => option.TextContent));
        Assert.Equal("solax", select.GetAttribute("value"));
    }

    [Fact]
    public void Choosing_a_brand_saves_it_after_probing_the_inverter_with_that_driver()
    {
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.InverterDriver)).Change("acme");
        form.Find("#pv-save").Click();

        Assert.Equal(new Dictionary<string, string> { [PvSystemSettingKeys.InverterDriver] = "acme" }, Assert.Single(_editor.Saves));
        var probe = Assert.Single(_editor.Probes);
        Assert.Equal(PvDeviceRole.Inverter, probe.Role);
        Assert.Equal("acme", Assert.Single(_editor.ProbeDrivers));
    }

    [Fact]
    public void Choosing_the_brand_already_in_force_is_not_an_edit()
    {
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.InverterDriver)).Change("acme");
        form.Find(Field(PvSystemSettingKeys.InverterDriver)).Change("solax");

        Assert.True(form.Find("#pv-save").HasAttribute("disabled"));
    }

    [Fact]
    public void An_id_this_host_does_not_have_is_shown_as_configured()
    {
        _editor.Running[PvSystemSettingKeys.InverterDriver] = "huawei";

        var form = Render<PvSystemEditorForm>();

        var select = form.Find(Field(PvSystemSettingKeys.InverterDriver));
        Assert.Equal("huawei", select.GetAttribute("value"));
        Assert.Contains("huawei", select.QuerySelectorAll("option").Select(option => option.GetAttribute("value")));
    }

    [Fact]
    public void The_unit_id_placeholder_is_the_selected_drivers_default()
    {
        var form = Render<PvSystemEditorForm>();
        Assert.Equal("1", form.Find(Field(PvSystemSettingKeys.InverterUnitId)).GetAttribute("placeholder"));

        form.Find(Field(PvSystemSettingKeys.InverterDriver)).Change("acme");

        Assert.Equal("0", form.Find(Field(PvSystemSettingKeys.InverterUnitId)).GetAttribute("placeholder"));
    }

    [Theory]
    [InlineData("en", "Inverter brand")]
    [InlineData("cs", "Značka střídače")]
    public void The_brand_field_is_labelled_in_the_viewers_language(string culture, string label)
    {
        var (previous, previousUi) = (System.Globalization.CultureInfo.CurrentCulture, System.Globalization.CultureInfo.CurrentUICulture);
        System.Globalization.CultureInfo.CurrentCulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo(culture);
        try
        {
            var form = Render<PvSystemEditorForm>();

            var field = form.Find(Field(PvSystemSettingKeys.InverterDriver)).ParentElement!;
            Assert.Equal(label, field.QuerySelector("label")!.TextContent);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentCulture = previous;
            System.Globalization.CultureInfo.CurrentUICulture = previousUi;
        }
    }

    [Fact]
    public void Does_not_probe_when_no_address_changed()
    {
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.TiltDegrees)).Input("30");
        form.Find("#pv-save").Click();

        Assert.Empty(_editor.Probes);
    }

    [Fact]
    public void Asks_before_changing_the_id_and_saves_once_confirmed()
    {
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.Id)).Input("cottage");
        form.Find("#pv-save").Click();

        Assert.Empty(_editor.Saves);
        Assert.Contains("MQTT topic", form.Find("#pv-warnings").TextContent);

        form.Find("#pv-save-anyway").Click();

        Assert.Equal("cottage", _editor.Stored[PvSystemSettingKeys.Id]);
    }

    [Fact]
    public void Asks_before_clearing_the_coordinates()
    {
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.Latitude)).Input(string.Empty);
        form.Find("#pv-save").Click();

        Assert.Empty(_editor.Saves);
        Assert.Contains("weather recording", form.Find("#pv-warnings").TextContent);
    }

    [Fact]
    public void Revert_takes_the_key_out_of_the_file()
    {
        _editor.Stored[PvSystemSettingKeys.TiltDegrees] = "30";
        var form = Render<PvSystemEditorForm>();

        form.Find(".revert").Click();

        Assert.Equal([PvSystemSettingKeys.TiltDegrees], _editor.Reverts);
        Assert.Equal("35", form.Find(Field(PvSystemSettingKeys.TiltDegrees)).GetAttribute("value"));
    }

    [Fact]
    public void Says_what_is_saved_but_not_running_and_where_the_restart_is()
    {
        _editor.Stored[PvSystemSettingKeys.TiltDegrees] = "30";
        _editor.Stored[PvSystemSettingKeys.ChargerHost] = "192.168.2.20";

        var form = Render<PvSystemEditorForm>();

        var pending = form.Find("#pv-pending");
        Assert.Contains("2 changes saved", pending.TextContent);
        Assert.Equal("/settings/health", pending.QuerySelector("a")!.GetAttribute("href"));

        // Each field still running something else says so under its box.
        var tilt = form.Find(Field(PvSystemSettingKeys.TiltDegrees)).ParentElement!;
        Assert.Contains("Running: 35", tilt.QuerySelector(".field-pending")!.TextContent);
        var host = form.Find(Field(PvSystemSettingKeys.ChargerHost)).ParentElement!;
        Assert.Contains("Running: 192.168.2.6", host.QuerySelector(".field-pending")!.TextContent);
        Assert.Equal(2, form.FindAll(".field-pending").Count);
    }

    [Theory]
    [InlineData("T", "= L3, in SolaX's naming")]
    [InlineData("r", "= L1, in SolaX's naming")]
    public void Spells_out_solaxs_name_for_a_phase(string typed, string expected)
    {
        // R/S/T is what the inverter's display says, and the resolver accepts it; the box should not
        // make it look wrong next to an "L1, L2, L3" hint.
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.InverterUnmeteredGridPhase)).Input(typed);

        var field = form.Find(Field(PvSystemSettingKeys.InverterUnmeteredGridPhase)).ParentElement!;
        Assert.Equal(expected, field.QuerySelector(".field-note")!.TextContent);
    }

    [Theory]
    [InlineData("-180", "= 180°, facing south")]
    [InlineData("172", "facing south")]
    [InlineData("270", "facing west")]
    public void Says_which_way_a_typed_azimuth_faces(string typed, string expected)
    {
        var form = Render<PvSystemEditorForm>();

        form.Find(Field(PvSystemSettingKeys.AzimuthDegrees)).Input(typed);

        var field = form.Find(Field(PvSystemSettingKeys.AzimuthDegrees)).ParentElement!;
        Assert.Equal(expected, field.QuerySelector(".field-note")!.TextContent);
    }

    [Fact]
    public void Nothing_pending_means_no_banner()
    {
        var form = Render<PvSystemEditorForm>();

        Assert.Empty(form.FindAll("#pv-pending"));
    }

    [Fact]
    public void A_host_that_cannot_apply_edits_says_so_and_takes_none()
    {
        _editor.Unavailable = "This host does not read a web UI overrides file.";

        var form = Render<PvSystemEditorForm>();

        Assert.Contains("does not read", form.Find("#pv-unavailable").TextContent);
        Assert.All(form.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.Empty(form.FindAll("#pv-save"));
    }

    [Fact]
    public void Without_a_login_the_form_is_read_only()
    {
        Services.AddSingleton(Options.Create(new WebOptions()));
        Services.AddSingleton(new WebPasswordState(null));

        var form = Render<PvSystemEditorForm>();

        Assert.Contains("no login", form.Find("#pv-readonly").TextContent);
        Assert.All(form.FindAll("input"), input => Assert.True(input.HasAttribute("disabled")));
        Assert.Empty(form.FindAll("#pv-save"));
    }

    [Fact]
    public void A_password_set_after_startup_makes_the_form_editable_without_a_restart()
    {
        // Issue #254: /setup records the hash in WebPasswordState, never in the startup WebOptions.
        Services.AddSingleton(Options.Create(new WebOptions()));
        var password = new WebPasswordState(null);
        Services.AddSingleton(password);
        password.Set("hash");

        var form = Render<PvSystemEditorForm>();

        Assert.Empty(form.FindAll("#pv-readonly"));
        Assert.NotNull(form.Find("#pv-save"));
    }

    [Fact]
    public void Authentication_switched_off_keeps_the_form_read_only_even_with_a_password()
    {
        Services.AddSingleton(Options.Create(new WebOptions { RequireAuthentication = false, PasswordHash = "hash" }));

        var form = Render<PvSystemEditorForm>();

        Assert.NotNull(form.Find("#pv-readonly"));
        Assert.Empty(form.FindAll("#pv-save"));
    }

    [Fact]
    public void Every_string_on_the_form_comes_from_a_resource()
    {
        var form = PseudoLocale.Render(() => Render<PvSystemEditorForm>());

        // The brands in the drop-down are names, and stay as they are in every language.
        PseudoLocale.AssertTranslated(form.Markup, [.. _editor.InverterDrivers.Select(driver => driver.DisplayName)]);
    }

    [Fact]
    public void A_refused_save_is_said_in_the_viewers_language()
    {
        // What the startup resolver refuses, carried with its phrase (#292) rather than split back out
        // of the startup message.
        _editor.Refuse = new Gleanvolt.Core.Configuration.ConfigurationProblems(
            "Pv:LossFactor (2) must be a fraction in (0, 1].",
            Phrase.Of("Config.Fraction", new PhraseArg.Text("Pv:LossFactor"), new PhraseArg.Text("2")));

        var form = PseudoLocale.Render(() =>
        {
            var rendered = Render<PvSystemEditorForm>();
            rendered.Find(Field(PvSystemSettingKeys.LossFactor)).Input("2");
            rendered.Find("#pv-save").Click();
            return rendered;
        });

        Assert.Contains("Pv:LossFactor (2)", form.Find("#pv-problems").TextContent);
        PseudoLocale.AssertTranslated(form.Find("#pv-problems").OuterHtml, "LossFactor");
    }
}
