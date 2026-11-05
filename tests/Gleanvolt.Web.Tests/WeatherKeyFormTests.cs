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
    public void EveryStringComesFromAResource()
    {
        _editor.Settings = new(WeatherKeySource.Environment, "env-secret", true, "an owner-only file");

        var form = PseudoLocale.Render(() => Render<WeatherKeyForm>());

        PseudoLocale.AssertTranslated(form.Markup, "WEATHER_API_KEY", ".env", "an owner-only file");
    }
}
