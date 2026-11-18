using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The weather on Settings and in the wizard (#424, #425): no weather or an external provider, such as the
/// OpenWeather plugin, whose own page is shown here; a choice saved only when asked; and, after the
/// upgrade that removed the built-in OpenWeather, a notice until a provider connects.
/// </summary>
public class WeatherFormTests : BunitContext
{
    public WeatherFormTests()
    {
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void WithoutTheSelectionThereIsNothingToChoose()
    {
        var form = Render<WeatherForm>();

        Assert.Empty(form.FindAll("#weather-source-choice"));
        Assert.Empty(form.FindAll("#weather-built-in-retired"));
    }

    [Theory]
    [InlineData(WeatherSource.NotProvided, "#weather-not-provided")]
    [InlineData(WeatherSource.External, "#weather-external-intro")]
    public void TheSectionBelowTheChoiceFollowsTheSourceInUse(WeatherSource selected, string shown)
    {
        WithSources(selected);

        var form = Render<WeatherForm>();

        Assert.Equal(2, form.FindAll("#weather-source-choice input[type=radio]").Count);
        Assert.True(form.Find("#weather-source-" + Id(selected)).HasAttribute("checked"));
        Assert.NotNull(form.Find(shown));
        Assert.Empty(form.FindAll("#weather-source-save"));
        Assert.Empty(form.FindAll("#weather-api-key"));
        Assert.Equal(selected == WeatherSource.External, form.FindAll("#external-weather").Count == 1);
    }

    [Fact]
    public void ExternalNamesTheOpenWeatherPluginAsTheReadyMadeProvider()
    {
        WithSources(WeatherSource.External);

        var form = Render<WeatherForm>();

        Assert.Contains("OpenWeather plugin", form.Find("#weather-external-intro").TextContent);
    }

    [Fact]
    public void PickingASourceSavesOnlyWhenAsked()
    {
        var (sources, _) = WithSources(WeatherSource.NotProvided);
        var form = Render<WeatherForm>();

        form.Find("#weather-source-external").Change(true);

        Assert.Empty(sources.Chosen);
        Assert.Contains("Not provided is still in use", form.Find("#weather-source-unsaved").TextContent);
        Assert.NotNull(form.Find("#external-weather"));

        form.Find("#weather-source-save").Click();

        Assert.Equal([WeatherSource.External], sources.Chosen);
        Assert.Equal("Saved. Weather source: External.", form.Find("#weather-message").TextContent.Trim());
        Assert.Empty(form.FindAll("#weather-source-unsaved"));
    }

    [Fact]
    public void CancelGoesBackToTheSourceInUse()
    {
        var (sources, _) = WithSources(WeatherSource.NotProvided);
        var form = Render<WeatherForm>();

        form.Find("#weather-source-external").Change(true);
        form.Find("#weather-source-cancel").Click();

        Assert.Empty(sources.Chosen);
        Assert.NotNull(form.Find("#weather-not-provided"));
    }

    [Fact]
    public void ARefusedChoiceIsSaid()
    {
        var (sources, _) = WithSources(WeatherSource.NotProvided);
        sources.Refusal = SettingsSaveResult.Refused(["The overrides file could not be written."]);
        var form = Render<WeatherForm>();

        form.Find("#weather-source-external").Change(true);
        form.Find("#weather-source-save").Click();

        Assert.Contains("could not be written", form.Find("#weather-errors").TextContent);
    }

    [Fact]
    public void AConnectedProvidersPageIsShownInAFrame()
    {
        var (_, providers) = WithSources(WeatherSource.External);
        providers.Set(new ExternalProviderStatus("weather", "Weather", "/providers/weather", HasToken: true,
            ExternalProviderState.Connected, "openweather", "OpenWeather", "1.0", PageUrl: new Uri("http://localhost:8096/")));

        var form = Render<WeatherForm>();

        Assert.Equal("/external/weather/page", form.Find("#external-weather-frame").GetAttribute("src"));
        Assert.Contains("OpenWeather has a page of its own", form.Find("#external-weather-page").TextContent);
        Assert.Contains("API key", form.Find("#external-weather-page").TextContent);
    }

    [Fact]
    public void AfterTheUpgradeItSaysWhereTheWeatherComesFromNowUntilAProviderConnects()
    {
        // #425: an installation that used the built-in OpenWeather.
        var (sources, _) = WithSources(WeatherSource.External);
        sources.BuiltInRetired = true;
        var form = Render<WeatherForm>();

        var notice = form.Find("#weather-built-in-retired").TextContent;
        Assert.Contains("built-in OpenWeather is gone", notice);
        Assert.Contains("OpenWeather plugin", notice);
        Assert.Contains("key", notice);

        sources.BuiltInRetired = false;
        sources.RaiseChanged();

        form.WaitForAssertion(() => Assert.Empty(form.FindAll("#weather-built-in-retired")));
    }

    [Fact]
    public void InTheWizardExternalOnlyRecordsTheChoice()
    {
        WithSources(WeatherSource.External);

        var form = Render<WeatherForm>(parameters => parameters.Add(p => p.InWizard, true));

        Assert.NotNull(form.Find("#weather-external-wizard"));
        Assert.Empty(form.FindAll("#external-weather"));
    }

    [Fact]
    public void WithoutALoginTheChoiceCannotBeChanged()
    {
        WithSources(WeatherSource.External);
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "", RequireAuthentication = false }));
        Services.AddSingleton(new WebPasswordState(null));

        var form = Render<WeatherForm>();

        Assert.All(form.FindAll("#weather-source-choice input"), input => Assert.True(input.HasAttribute("disabled")));
    }

    [Theory]
    [InlineData(WeatherSource.NotProvided, false)]
    [InlineData(WeatherSource.External, false)]
    [InlineData(WeatherSource.External, true)]
    public void EveryStringComesFromAResource(WeatherSource selected, bool retired)
    {
        var (sources, _) = WithSources(selected);
        sources.BuiltInRetired = retired;

        var form = PseudoLocale.Render(() => Render<WeatherForm>());

        // The hub's address and path are data the owner copies into the provider.
        PseudoLocale.AssertTranslated(form.Markup, "OpenWeather", "http://localhost", "/providers/weather");
    }

    private (FakeWeatherSourceSelection Sources, FakeExternalProviders Providers) WithSources(WeatherSource selected)
    {
        var sources = new FakeWeatherSourceSelection { Selected = selected };
        var providers = new FakeExternalProviders();
        providers.Set(new ExternalProviderStatus("weather", "Weather", "/providers/weather", HasToken: false,
            selected == WeatherSource.External ? ExternalProviderState.Waiting : ExternalProviderState.Off));
        Services.AddSingleton<IWeatherSourceSelection>(sources);
        Services.AddSingleton<IExternalProviders>(providers);
        Services.AddSingleton<TimeProvider>(TimeProvider.System);
        return (sources, providers);
    }

    private static string Id(WeatherSource source) => source == WeatherSource.NotProvided ? "not-provided" : "external";
}
