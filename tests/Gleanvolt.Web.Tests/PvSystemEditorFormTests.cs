using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The form that edits the installation from /pv-system (issue #204). What it has to get right: it
/// sends only what changed, shows the startup resolver's refusal instead of saving, probes a changed
/// device address before saving, asks before a change that moves topics, and says what is saved but
/// not yet running — with the restart that applies it beside it.
/// </summary>
public class PvSystemEditorFormTests : BunitContext
{
    private readonly FakePvSystemEditor _editor = new();
    private readonly FakeServiceShutdown _shutdown = new();

    public PvSystemEditorFormTests()
    {
        Services.AddSingleton<IPvSystemEditor>(_editor);
        Services.AddSingleton<IServiceShutdown>(_shutdown);

        // A login, which is what makes the form editable at all.
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));

        // The restart asks the browser to watch for the controller coming back; the script is not here.
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    private static string Field(string key) => "#pv-" + key.Replace(':', '-');

    [Fact]
    public void Says_where_each_value_comes_from()
    {
        _editor.Stored[PvSystemSettingKeys.TiltDegrees] = "30";

        var form = Render<PvSystemEditorForm>();

        var tilt = form.Find(Field(PvSystemSettingKeys.TiltDegrees)).ParentElement!;
        Assert.Contains("web UI", tilt.QuerySelector(".source")!.TextContent);
        Assert.Contains("Revert to 35", tilt.QuerySelector(".revert")!.TextContent);

        var host = form.Find(Field(PvSystemSettingKeys.InverterHost)).ParentElement!;
        Assert.Contains("environment", host.QuerySelector(".source")!.TextContent);
        Assert.Null(host.QuerySelector(".revert"));
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
    public void Lists_what_is_saved_but_not_running_with_the_restart_beside_it()
    {
        _editor.Stored[PvSystemSettingKeys.TiltDegrees] = "30";
        _editor.Stored[PvSystemSettingKeys.ChargerHost] = "192.168.2.20";

        var form = Render<PvSystemEditorForm>();

        var pending = form.Find("#pv-pending");
        Assert.Contains("Saved, not applied", pending.TextContent);
        Assert.Contains("2 changes take effect on restart", pending.TextContent);
        Assert.Contains("35 → 30", pending.TextContent);
        Assert.Contains("192.168.2.6 → 192.168.2.20", pending.TextContent);

        form.Find("#restart").Click();
        form.Find("#restart-confirm").Click();

        Assert.Equal(["Web UI"], _shutdown.Restarts);
        Assert.Empty(_shutdown.Requests);
        Assert.NotNull(form.Find("#restart-progress"));
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
}
