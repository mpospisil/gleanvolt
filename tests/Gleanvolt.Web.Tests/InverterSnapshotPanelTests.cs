using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// Settings → inverter → "Save register snapshot" (issue #319): where it is offered, that a click hands the
/// browser the file, and that a failure says what the link said.
/// </summary>
public class InverterSnapshotPanelTests : BunitContext
{
    private readonly FakeInverterSnapshot _snapshot = new();

    public InverterSnapshotPanelTests()
    {
        Services.AddSingleton<IPvSystemEditor>(new FakePvSystemEditor());
        Services.AddSingleton<IInverterSnapshot>(_snapshot);
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
        Assert.Empty(Form(offer: false).FindAll("#inverter-snapshot"));
    }

    [Fact]
    public void An_open_ui_does_not_offer_it()
    {
        Services.AddSingleton(Options.Create(new WebOptions()));
        Services.AddSingleton(new WebPasswordState(null));

        Assert.Empty(Form().FindAll("#inverter-snapshot"));
    }

    [Fact]
    public void A_click_hands_the_browser_the_file_with_the_note()
    {
        var form = Form();

        form.Find("#inverter-snapshot-note").Input("night, battery idle, importing");
        form.Find("#inverter-snapshot-save").Click();

        Assert.Equal(["night, battery idle, importing"], _snapshot.Notes);
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
    public void Every_string_comes_from_a_resource()
    {
        _snapshot.Failure = new InvalidOperationException("x");
        var form = PseudoLocale.Render(() =>
        {
            var rendered = Form();
            rendered.Find("#inverter-snapshot-save").Click();
            return rendered;
        });

        PseudoLocale.AssertTranslated(form.Find("#inverter-snapshot").OuterHtml + form.Find("#inverter-snapshot-error").OuterHtml, "x");
    }
}
