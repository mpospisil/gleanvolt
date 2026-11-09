using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Settings → inverter / charger → "Save register snapshot" (issues #319, #350): where it is offered, that a
/// click hands the browser the file of the right device, and that a failure says what the link said.
/// </summary>
public class DeviceSnapshotPanelTests : BunitContext
{
    private readonly FakeDeviceSnapshot _snapshot = new();

    public DeviceSnapshotPanelTests()
    {
        Services.AddSingleton<IPvSystemEditor>(new FakePvSystemEditor());
        Services.AddSingleton<IDeviceSnapshot>(_snapshot);
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
        JSInterop.SetupVoid("gleanvolt.downloadText", _ => true).SetVoidResult();
    }

    private IRenderedComponent<PvSystemEditorForm> Form(bool offer = true) =>
        Render<PvSystemEditorForm>(parameters => parameters.Add(form => form.OfferSnapshot, offer));

    [Fact]
    public void Settings_offers_it_and_the_wizard_does_not()
    {
        Assert.NotNull(Form().Find("#inverter-snapshot-save"));
        Assert.NotNull(Form().Find("#charger-snapshot-save"));
        Assert.Empty(Form(offer: false).FindAll(".pv-snapshot"));
    }

    [Fact]
    public void An_open_ui_does_not_offer_it()
    {
        Services.AddSingleton(Options.Create(new WebOptions()));
        Services.AddSingleton(new WebPasswordState(null));

        Assert.Empty(Form().FindAll(".pv-snapshot"));
    }

    [Fact]
    public void A_click_hands_the_browser_the_file_with_the_note()
    {
        var form = Form();

        form.Find("#inverter-snapshot-note").Input("night, battery idle, importing");
        form.Find("#inverter-snapshot-save").Click();

        Assert.Equal([(PvDeviceRole.Inverter, "night, battery idle, importing")], _snapshot.Calls);
        var download = Assert.Single(JSInterop.Invocations, call => call.Identifier == "gleanvolt.downloadText");
        Assert.Equal("gleanvolt-registers-solax-20261108-213000.json", download.Arguments[0]);
        Assert.Contains("gleanvolt-register-dump", (string)download.Arguments[1]!);
        Assert.Contains("gleanvolt-registers-solax-20261108-213000.json", form.Find("#inverter-snapshot-saved").TextContent);
    }

    [Fact]
    public void A_failure_says_what_the_link_said_and_downloads_nothing()
    {
        _snapshot.Failure = new TimeoutException("Timed out connecting to Modbus device at 192.168.2.10:502.");
        var form = Form();

        form.Find("#inverter-snapshot-save").Click();

        Assert.Contains("Timed out connecting", form.Find("#inverter-snapshot-error").TextContent);
        Assert.DoesNotContain(JSInterop.Invocations, call => call.Identifier == "gleanvolt.downloadText");
    }

    [Fact]
    public void The_chargers_button_reads_the_charger()
    {
        var form = Form();

        form.Find("#charger-snapshot-note").Input("car plugged in, charging at 16 A");
        form.Find("#charger-snapshot-save").Click();

        Assert.Equal([(PvDeviceRole.Charger, "car plugged in, charging at 16 A")], _snapshot.Calls);
        var download = Assert.Single(JSInterop.Invocations, call => call.Identifier == "gleanvolt.downloadText");
        Assert.Equal("gleanvolt-registers-solax-charger-20261108-213000.json", download.Arguments[0]);
        Assert.Contains("gleanvolt-registers-solax-charger-20261108-213000.json", form.Find("#charger-snapshot-saved").TextContent);
        Assert.Empty(form.FindAll("#inverter-snapshot-saved"));
    }

    [Fact]
    public void A_charger_failure_names_the_charger()
    {
        _snapshot.Failure = new TimeoutException("Timed out connecting to Modbus device at 192.168.2.6:502.");
        var form = Form();

        form.Find("#charger-snapshot-save").Click();

        var error = form.Find("#charger-snapshot-error").TextContent;
        Assert.Contains("charger", error);
        Assert.Contains("192.168.2.6", error);
        Assert.Empty(form.FindAll("#inverter-snapshot-error"));
    }

    [Fact]
    public void Every_string_comes_from_a_resource()
    {
        _snapshot.Failure = new InvalidOperationException("x");
        var form = PseudoLocale.Render(() =>
        {
            var rendered = Form();
            rendered.Find("#inverter-snapshot-save").Click();
            rendered.Find("#charger-snapshot-save").Click();
            return rendered;
        });

        PseudoLocale.AssertTranslated(
            form.Find("#inverter-snapshot").OuterHtml + form.Find("#inverter-snapshot-error").OuterHtml
            + form.Find("#charger-snapshot").OuterHtml + form.Find("#charger-snapshot-error").OuterHtml,
            "x");
    }
}
