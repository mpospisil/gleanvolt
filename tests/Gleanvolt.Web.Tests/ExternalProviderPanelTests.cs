using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The external provider panel on Settings (#386): where a provider connects, where the token is (one
/// for every kind since #406), and the session as it changes, without a reload.
/// </summary>
public class ExternalProviderPanelTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeExternalProviders _providers = new();

    public ExternalProviderPanelTests()
    {
        Services.AddSingleton<IExternalProviders>(_providers);
        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        JSInterop.Mode = JSRuntimeMode.Loose;
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", HasToken: false, ExternalProviderState.Waiting));
    }

    private IRenderedComponent<ExternalProviderPanel> RenderPanel(bool readOnly = false) =>
        Render<ExternalProviderPanel>(parameters => parameters.Add(panel => panel.Kind, "tariff").Add(panel => panel.ReadOnly, readOnly));

    [Fact]
    public void LeavesTheAddressAndHubToTheExternalProvidersSection()
    {
        // #457: where providers connect is said once, on Settings' External providers tab.
        var panel = RenderPanel();

        Assert.Empty(panel.FindAll("#external-tariff-address"));
        Assert.Empty(panel.FindAll("#external-tariff-hub"));
    }

    [Fact]
    public void WithoutATokenItWaitsForAPluginWithoutAWarning()
    {
        // Accepting a plugin makes the token (#464), so no token is nothing to warn about (#470).
        var panel = RenderPanel();

        Assert.Equal("Waiting for a plugin to connect.", panel.Find("#external-tariff-state").TextContent.Trim());
        Assert.Empty(panel.FindAll("#external-tariff-token-state"));
        Assert.Empty(panel.FindAll("#external-tariff-token"));
    }

    [Fact]
    public void WhileWaitingTheTipSaysWhatAPluginIsAndLinksToStartingOne()
    {
        var panel = RenderPanel();

        var tip = panel.Find("details.info");
        Assert.Contains("This data comes from a plugin", tip.TextContent);
        var link = tip.QuerySelector("a.plugin-help-tip-link")!;
        Assert.Equal("How to start a plugin", link.TextContent);
        Assert.EndsWith("/en/setting-up/plugins/", link.GetAttribute("href"));
        Assert.Empty(panel.FindAll(".plugin-help-link"));
    }

    [Fact]
    public void AChargerWaitingForItsPluginShowsTheLinkBesideTheState()
    {
        _providers.Set(new ExternalProviderStatus("charger", "Charger", "/providers/charger", HasToken: true, ExternalProviderState.Waiting));

        var panel = Render<ExternalProviderPanel>(parameters => parameters.Add(p => p.Kind, "charger"));

        var link = panel.Find("a.plugin-help-link");
        Assert.Equal("How to add your charger", link.TextContent);
        Assert.EndsWith("/en/setting-up/charger/", link.GetAttribute("href"));
    }

    [Fact]
    public void ConnectedItSaysWhereTheTokenIsAndNeverShowsIt()
    {
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", HasToken: true, ExternalProviderState.Connected,
            ProviderId: "sample", ProviderName: "Sample", Since: Now));
        _providers.Token = "gvp_kept-in-the-secret-store";

        var panel = RenderPanel();

        Assert.Contains("The token is under Plugins.", panel.Find("details.info").TextContent);
        Assert.DoesNotContain("gvp_kept-in-the-secret-store", panel.Markup);
    }

    [Fact]
    public void FollowsTheSessionAsItChanges()
    {
        var panel = RenderPanel();

        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", true, ExternalProviderState.Connected,
            ProviderId: "spot", ProviderName: "Spot prices", ProviderVersion: "1.4", Since: Now, LastDataAt: Now));

        panel.WaitForAssertion(() =>
            Assert.Equal("Spot prices 1.4 connected since 2026-11-15 10:00.", panel.Find("#external-tariff-state").TextContent.Trim()));
        Assert.Equal("Last data received: 2026-11-15 10:00.", panel.Find("#external-tariff-data").TextContent.Trim());
    }

    [Fact]
    public void SaysWhatWentWrong()
    {
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", true, ExternalProviderState.Disconnected,
            ProviderId: "spot", ProviderName: "Spot prices", Since: Now, Unanswered: true, Healthy: false, StatusMessage: "OTE is down.",
            LastProblem: ["There is a gap before window 3."], LastProblemPhrases: [Core.Models.Phrase.Of("ExternalTariff.Gap", new Core.Models.PhraseArg.Number(3))]));

        var panel = RenderPanel();

        Assert.Contains("disconnected at 2026-11-15 10:00", panel.Find("#external-tariff-state").TextContent);
        Assert.NotNull(panel.Find("#external-tariff-unanswered"));
        Assert.Contains("OTE is down.", panel.Find("#external-tariff-unhealthy").TextContent);
        Assert.Equal("There is a gap before window 3.", panel.Find("#external-tariff-problems li").TextContent);
    }

    [Fact]
    public void SaysTheStatusInTheViewersLanguage()
    {
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", true, ExternalProviderState.Connected,
            ProviderId: "spot", ProviderName: "Spot prices", Since: Now, Healthy: false, NeedsOwner: true,
            StatusMessage: "The login expired. Sign in again on the provider's page.", StatusCode: "needs-sign-in",
            StatusMessages: new Dictionary<string, string> { ["en"] = "The login expired.", ["cs"] = "Přihlášení vypršelo." }));

        Assert.Equal("The plugin needs you: sign in on the plugin's page. The login expired.",
            RenderPanel().Find("#external-tariff-unhealthy").TextContent.Trim());

        var ui = System.Globalization.CultureInfo.CurrentUICulture;
        System.Globalization.CultureInfo.CurrentUICulture = System.Globalization.CultureInfo.GetCultureInfo("cs");

        try
        {
            var czech = RenderPanel().Find("#external-tariff-unhealthy").TextContent.Trim();
            Assert.Contains("přihlaste se na stránce pluginu. Přihlášení vypršelo.", czech);
            Assert.DoesNotContain("expired", czech);
        }
        finally
        {
            System.Globalization.CultureInfo.CurrentUICulture = ui;
        }
    }

    [Fact]
    public void OffSaysHowToTurnItOn()
    {
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", true, ExternalProviderState.Off));

        Assert.Contains("Choose A plugin and save", RenderPanel().Find("#external-tariff-state").TextContent);
    }
}
