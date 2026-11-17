using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The OpenWeatherMap key on Settings and in the wizard: where the key comes from is shown, the key is
/// never rendered into the box, a save says what OpenWeatherMap made of it, and without a login it is
/// read-only.
/// </summary>
public class WeatherKeyFormTests : BunitContext
{
    private readonly FakeWeatherKeyEditor _editor = new();

    public WeatherKeyFormTests()
    {
        Services.AddSingleton<IWeatherKeyEditor>(_editor);
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
        JSInterop.Mode = JSRuntimeMode.Loose;
    }

    [Fact]
    public void WithNoKeyItSaysSo()
    {
        var form = Render<WeatherKeyForm>();

        Assert.Contains("Not configured", form.Find("#weather-status").TextContent);
        Assert.Contains("Not configured", form.Find("#weather-source").TextContent);
        Assert.Empty(form.FindAll("#weather-remove"));
        Assert.True(form.Find("#weather-save").HasAttribute("disabled"));
    }

    [Fact]
    public void TheEnvironmentsKeyIsNamedAndNotRenderedIntoTheBox()
    {
        _editor.Settings = new(WeatherKeySource.Environment, "env-secret", true, "an owner-only file");

        var form = Render<WeatherKeyForm>();

        Assert.Contains("WEATHER_API_KEY", form.Find("#weather-source").TextContent);
        Assert.Equal("", form.Find("#weather-api-key").GetAttribute("value") ?? "");
        Assert.DoesNotContain("env-secret", form.Find("#weather-api-key").OuterHtml);
        Assert.Empty(form.FindAll("#weather-remove"));
    }

    [Theory]
    [InlineData(WeatherKeyCheck.Accepted, "accepted the key", false)]
    [InlineData(WeatherKeyCheck.Refused, "couple of hours", true)]
    [InlineData(WeatherKeyCheck.Failed, "could not be reached", false)]
    public void ASaveSaysWhatOpenWeatherMapMadeOfTheKey(WeatherKeyCheck check, string expected, bool error)
    {
        _editor.NextCheck = check;
        var form = Render<WeatherKeyForm>();

        form.Find("#weather-api-key").Input(" new-key ");
        form.Find("#weather-save").Click();

        Assert.Equal([" new-key "], _editor.Saves);
        var message = form.Find("#weather-message");
        Assert.Contains(expected, message.TextContent);
        Assert.Equal(error, message.ClassList.Contains("error"));
        Assert.Contains("Saved here", form.Find("#weather-source").TextContent);
        Assert.Equal("", form.Find("#weather-api-key").GetAttribute("value") ?? "");
    }

    [Fact]
    public void ASavedKeyCanBeRemoved()
    {
        _editor.Settings = new(WeatherKeySource.Page, "page-key", false, "an owner-only file");
        var form = Render<WeatherKeyForm>();

        form.Find("#weather-remove").Click();

        Assert.Equal(1, _editor.Removes);
        Assert.Contains("without the weather", form.Find("#weather-message").TextContent);
    }

    [Fact]
    public void WithoutALoginItIsReadOnly()
    {
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "", RequireAuthentication = false }));
        Services.AddSingleton(new WebPasswordState(null));

        var form = Render<WeatherKeyForm>();

        Assert.NotNull(form.Find("#weather-readonly"));
        Assert.Empty(form.FindAll("#weather-api-key"));
    }

    [Fact]
    public void WithoutTheSelectionOnlyTheKeyIsOffered()
    {
        // A host without the selection (#424) keeps the form as it was: OpenWeatherMap alone.
        var form = Render<WeatherKeyForm>();

        Assert.Empty(form.FindAll("#weather-source-choice"));
        Assert.NotNull(form.Find("#weather-api-key"));
    }

    [Theory]
    [InlineData(WeatherSource.NotProvided, "#weather-not-provided")]
    [InlineData(WeatherSource.OpenWeather, "#weather-api-key")]
    [InlineData(WeatherSource.External, "#weather-external-intro")]
    public void TheSectionBelowTheChoiceFollowsTheSourceInUse(WeatherSource selected, string shown)
    {
        WithSources(selected);

        var form = Render<WeatherKeyForm>();

        Assert.Equal(3, form.FindAll("#weather-source-choice input[type=radio]").Count);
        Assert.True(form.Find("#weather-source-" + Id(selected)).HasAttribute("checked"));
        Assert.NotNull(form.Find(shown));
        Assert.Empty(form.FindAll("#weather-source-save"));
        Assert.Equal(selected == WeatherSource.OpenWeather, form.FindAll("#weather-api-key").Count == 1);
        Assert.Equal(selected == WeatherSource.External, form.FindAll("#external-weather").Count == 1);
    }

    [Fact]
    public void PickingASourceSavesOnlyWhenAsked()
    {
        var (sources, _) = WithSources(WeatherSource.OpenWeather);
        var form = Render<WeatherKeyForm>();

        form.Find("#weather-source-external").Change(true);

        Assert.Empty(sources.Chosen);
        Assert.Contains("OpenWeather is still in use", form.Find("#weather-source-unsaved").TextContent);
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
        var form = Render<WeatherKeyForm>();

        form.Find("#weather-source-openweather").Change(true);
        form.Find("#weather-source-cancel").Click();

        Assert.Empty(sources.Chosen);
        Assert.NotNull(form.Find("#weather-not-provided"));
    }

    [Fact]
    public void SavingAKeyUnderAPickedOpenWeatherAlsoChoosesIt()
    {
        var (sources, _) = WithSources(WeatherSource.NotProvided);
        var form = Render<WeatherKeyForm>();

        form.Find("#weather-source-openweather").Change(true);
        form.Find("#weather-api-key").Input("new-key");
        form.Find("#weather-save").Click();

        Assert.Equal(["new-key"], _editor.Saves);
        Assert.Equal([WeatherSource.OpenWeather], sources.Chosen);
        Assert.Contains("accepted the key", form.Find("#weather-message").TextContent);
    }

    [Fact]
    public void AConnectedProvidersPageIsShownInAFrame()
    {
        var (_, providers) = WithSources(WeatherSource.External);
        providers.Set(new ExternalProviderStatus("weather", "Weather", "/providers/weather", HasToken: true,
            ExternalProviderState.Connected, "openweather", "OpenWeather", "1.0", PageUrl: new Uri("http://localhost:8096/")));

        var form = Render<WeatherKeyForm>();

        Assert.Equal("/external/weather/page", form.Find("#external-weather-frame").GetAttribute("src"));
        Assert.Contains("OpenWeather has a page of its own", form.Find("#external-weather-page").TextContent);
        Assert.Contains("API key", form.Find("#external-weather-page").TextContent);
    }

    [Fact]
    public void InTheWizardExternalOnlyRecordsTheChoice()
    {
        WithSources(WeatherSource.External);

        var form = Render<WeatherKeyForm>(parameters => parameters.Add(p => p.InWizard, true));

        Assert.NotNull(form.Find("#weather-external-wizard"));
        Assert.Empty(form.FindAll("#external-weather"));
    }

    [Fact]
    public void WithoutALoginTheChoiceCannotBeChanged()
    {
        WithSources(WeatherSource.External);
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "", RequireAuthentication = false }));
        Services.AddSingleton(new WebPasswordState(null));

        var form = Render<WeatherKeyForm>();

        Assert.All(form.FindAll("#weather-source-choice input"), input => Assert.True(input.HasAttribute("disabled")));
    }

    [Theory]
    [InlineData(WeatherSource.NotProvided)]
    [InlineData(WeatherSource.External)]
    public void EveryStringOfTheChoiceComesFromAResource(WeatherSource selected)
    {
        WithSources(selected);

        var form = PseudoLocale.Render(() => Render<WeatherKeyForm>());

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

    private static string Id(WeatherSource source) => source switch
    {
        WeatherSource.NotProvided => "not-provided",
        WeatherSource.OpenWeather => "openweather",
        _ => "external",
    };

    [Fact]
    public void EveryStringComesFromAResource()
    {
        _editor.Settings = new(WeatherKeySource.Environment, "env-secret", true, "an owner-only file");

        var form = PseudoLocale.Render(() => Render<WeatherKeyForm>());

        PseudoLocale.AssertTranslated(form.Markup, "WEATHER_API_KEY", ".env", "an owner-only file");
    }
}
