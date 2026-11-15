using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// External providers on Settings (#406): the one token every plugin connects with, behind Reveal and
/// only behind a login, and each kind's session at a glance.
/// </summary>
public class ExternalProvidersFormTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly DateTimeOffset Now = new(2026, 11, 15, 9, 0, 0, TimeSpan.Zero);

    private readonly FakeExternalProviders _providers = new();

    public ExternalProvidersFormTests()
    {
        Services.AddSingleton<IExternalProviders>(_providers);
        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        JSInterop.Mode = JSRuntimeMode.Loose;
        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", HasToken: false, ExternalProviderState.Waiting));
        _providers.Set(new ExternalProviderStatus("solar-forecast", "Solar forecast", "/providers/solar-forecast", HasToken: false, ExternalProviderState.Off));
    }

    private IRenderedComponent<ExternalProvidersForm> RenderForm(bool signedIn = true)
    {
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = signedIn ? "hash" : null }));
        Services.AddSingleton(new WebPasswordState(signedIn ? "hash" : null));
        return Render<ExternalProvidersForm>();
    }

    private void WithToken(string token)
    {
        _providers.Token = token;

        foreach (var status in _providers.Statuses.ToList())
        {
            _providers.Set(status with { HasToken = true });
        }
    }

    [Fact]
    public void WithoutAnyKindThereIsNoSection()
    {
        _providers.Statuses.Clear();

        Assert.Empty(RenderForm().FindAll("#external-providers"));
    }

    [Fact]
    public void ShowsTheAddressAndEveryKindWithItsHubAndSession()
    {
        var form = RenderForm();

        Assert.Equal("http://localhost", form.Find("#external-providers-address").TextContent);
        Assert.Contains("/providers/tariff", form.Find("#external-providers-tariff").TextContent);
        Assert.Contains("Make the provider token first", form.Find("#external-providers-tariff").TextContent);
        Assert.Equal("#tariff-editor", form.Find("#external-providers-tariff a").GetAttribute("href"));
        Assert.Contains("/providers/solar-forecast", form.Find("#external-providers-solar-forecast").TextContent);
        Assert.Contains("Choose External and save", form.Find("#external-providers-solar-forecast").TextContent);
        Assert.Equal("#solcast-editor", form.Find("#external-providers-solar-forecast a").GetAttribute("href"));
    }

    [Fact]
    public void WithoutATokenItSaysToMakeOne()
    {
        var form = RenderForm();

        Assert.Equal("No token yet. Make one and give it to each provider.", form.Find("#external-providers-token-state").TextContent.Trim());
        Assert.Equal("Make a token", form.Find("#external-providers-token").TextContent.Trim());
    }

    [Fact]
    public void OneNewTokenServesEveryKindIsMaskedAndRevealedOnRequest()
    {
        var form = RenderForm();

        form.Find("#external-providers-token").Click();

        Assert.Equal(1, _providers.TokensMade);
        var token = _providers.Token!;
        Assert.DoesNotContain(token, form.Markup);
        Assert.All(_providers.Statuses, status => Assert.True(status.HasToken));
        Assert.Equal("Make a new token", form.Find("#external-providers-token").TextContent.Trim());
        Assert.Contains("disconnects every provider", form.Find("#external-providers-token-replaces").TextContent);

        form.Find("#external-providers-token-value button").Click();
        Assert.Contains(token, form.Find("#external-providers-token-value code").TextContent);
    }

    [Fact]
    public void TheTokenCanBeRevealedAgainAfterAReload()
    {
        WithToken("gvp_kept-in-the-secret-store");

        var form = RenderForm();
        form.Find("#external-providers-token-value button").Click();

        Assert.Contains("gvp_kept-in-the-secret-store", form.Find("#external-providers-token-value code").TextContent);
    }

    [Fact]
    public void WithoutALoginTheTokenIsNeitherShownNorMade()
    {
        WithToken("gvp_kept-in-the-secret-store");

        var form = RenderForm(signedIn: false);

        Assert.Empty(form.FindAll("#external-providers-token"));
        Assert.DoesNotContain("gvp_kept-in-the-secret-store", form.Markup);
        Assert.Equal("A token exists.", form.Find("#external-providers-token-state").TextContent.Trim());
    }

    [Fact]
    public void FollowsTheSessionsAsTheyChange()
    {
        WithToken("gvp_kept-in-the-secret-store");
        var form = RenderForm();

        _providers.Set(new ExternalProviderStatus("tariff", "Tariff", "/providers/tariff", true, ExternalProviderState.Connected,
            ProviderId: "spot", ProviderName: "Spot prices", ProviderVersion: "1.4", Since: Now));

        form.WaitForAssertion(() =>
            Assert.Contains("Spot prices 1.4 connected since 2026-11-15 10:00.", form.Find("#external-providers-tariff").TextContent));
    }

    [Fact]
    public void Every_string_on_the_form_comes_from_a_resource()
    {
        WithToken("gvp_kept-in-the-secret-store");
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));

        var form = PseudoLocale.Render(() => Render<ExternalProvidersForm>());

        // The address and the hub paths are what a provider is configured with, not text.
        PseudoLocale.AssertTranslated(form.Markup, "http", "localhost", "providers", "tariff", "solar", "forecast");
    }
}
