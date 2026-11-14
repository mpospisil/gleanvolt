using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The external provider panel on Settings (#386): where a provider connects, its token shown once,
/// and the session as it changes, without a reload.
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
    public void ShowsWhereAProviderConnects()
    {
        var panel = RenderPanel();

        Assert.Equal("http://localhost", panel.Find("#external-tariff-address").TextContent);
        Assert.Equal("/providers/tariff", panel.Find("#external-tariff-hub").TextContent);
    }

    [Fact]
    public void WithoutATokenItSaysToMakeOne()
    {
        var panel = RenderPanel();

        Assert.Contains("Make a token first", panel.Find("#external-tariff-state").TextContent);
        Assert.Equal("Make a token", panel.Find("#external-tariff-token").TextContent.Trim());
    }

    [Fact]
    public void ANewTokenIsShownOnce()
    {
        var panel = RenderPanel();

        panel.Find("#external-tariff-token").Click();

        Assert.Equal(["tariff"], _providers.TokensMade);
        Assert.Contains("not shown again", panel.Find("#external-tariff-token-once").TextContent);
        Assert.Equal("Make a new token", panel.Find("#external-tariff-token").TextContent.Trim());

        // A fresh render, as after a reload: the token is gone, only the fact that it exists stays.
        var again = RenderPanel();
        Assert.Empty(again.FindAll("#external-tariff-token-once"));
        Assert.Contains("A token exists", again.Find("#external-tariff-token-state").TextContent);
    }

    [Fact]
    public void WithoutALoginNoTokenCanBeMade()
    {
        var panel = RenderPanel(readOnly: true);

        Assert.Empty(panel.FindAll("#external-tariff-token"));
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
    public void OffSaysHowToTurnItOn()
    {
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", true, ExternalProviderState.Off));

        Assert.Contains("Choose External and save", RenderPanel().Find("#external-tariff-state").TextContent);
    }
}
