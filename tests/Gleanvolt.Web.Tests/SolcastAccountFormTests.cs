using Bunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;
using Gleanvolt.Web.Components;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The Solcast account on Settings (issue #286). What it has to get right: the status line says what
/// the last fetch came to in words the owner can act on; where the account comes from is shown; the
/// key is never rendered into the box and never shown at all without a login; a save with nothing
/// changed is not offered, since it would spend a call; and a fetch by the worker redraws the status.
/// </summary>
public class SolcastAccountFormTests : BunitContext
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    // 10:00 UTC, noon in Prague.
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly FakeSolcastAccountEditor _editor = new();

    public SolcastAccountFormTests()
    {
        Services.AddSingleton<ISolcastAccountEditor>(_editor);
        Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));

        // A login, which is what makes the form editable at all.
        Services.AddSingleton(Options.Create(new WebOptions { PasswordHash = "hash" }));
        Services.AddSingleton(new WebPasswordState("hash"));
    }

    private static SolcastAccountSettings Account(
        SolcastAccountSource source = SolcastAccountSource.Page, SolcastFetchStatus? status = null) =>
        new(source, "the-key", "1234-abcd", source == SolcastAccountSource.Environment, "an owner-only file",
            status ?? SolcastFetchStatus.Never);

    [Fact]
    public void WithNoAccountItSaysSo()
    {
        var form = Render<SolcastAccountForm>();

        Assert.Contains("Not configured", form.Find("#solcast-status").TextContent);
        Assert.Contains("Not configured", form.Find("#solcast-source").TextContent);
        Assert.Empty(form.FindAll("#solcast-remove"));
    }

    [Fact]
    public void AFetchedForecastIsReportedInLocalTime()
    {
        _editor.Settings = Account(status: new SolcastFetchStatus(Now.AddHours(-2), null, SolcastFetchFailure.None, 200));

        var form = Render<SolcastAccountForm>();

        Assert.Equal("Forecast fetched at 10:00.", form.Find("#solcast-status").TextContent.Trim());
    }

    [Theory]
    [InlineData(SolcastFetchFailure.KeyRefused, 401, "refused the key (401)")]
    [InlineData(SolcastFetchFailure.SiteNotFound, 404, "no rooftop site 1234-abcd")]
    [InlineData(SolcastFetchFailure.QuotaExhausted, 429, "calls are used up (429)")]
    public void AFailureIsNamed(SolcastFetchFailure failure, int status, string expected)
    {
        _editor.Settings = Account(status: new SolcastFetchStatus(Now.AddDays(-1), Now, failure, status));

        var form = Render<SolcastAccountForm>();

        var line = form.Find("#solcast-status");
        Assert.Contains(expected, line.TextContent);
        Assert.Contains("still in use", line.TextContent);
        Assert.Contains("error", line.ClassList);
    }

    [Fact]
    public void TheEnvironmentsAccountIsNamedAsSuchAndCannotBeRemovedHere()
    {
        _editor.Settings = Account(SolcastAccountSource.Environment);

        var form = Render<SolcastAccountForm>();

        Assert.Contains("SOLCAST_API_KEY", form.Find("#solcast-source").TextContent);
        Assert.Empty(form.FindAll("#solcast-remove"));
    }

    [Fact]
    public void TheKeyIsNeverRenderedIntoTheBox()
    {
        _editor.Settings = Account();

        var form = Render<SolcastAccountForm>();

        Assert.Equal(string.Empty, form.Find("#solcast-api-key").GetAttribute("value") ?? string.Empty);
        Assert.DoesNotContain("the-key", form.Markup);
        Assert.Equal("1234-abcd", form.Find("#solcast-resource-id").GetAttribute("value"));
    }

    [Fact]
    public void NothingChangedIsNotOfferedForSaving()
    {
        _editor.Settings = Account();

        var form = Render<SolcastAccountForm>();

        Assert.True(form.Find("#solcast-save").HasAttribute("disabled"));

        form.Find("#solcast-resource-id").Input("5678-efgh");
        Assert.False(form.Find("#solcast-save").HasAttribute("disabled"));
    }

    [Fact]
    public void ASaveHandsOverWhatWasTypedAndShowsWhatTheFetchCameTo()
    {
        _editor.NextStatus = new SolcastFetchStatus(Now, null, SolcastFetchFailure.None, 200);
        var form = Render<SolcastAccountForm>();

        form.Find("#solcast-api-key").Input("pasted-key");
        form.Find("#solcast-resource-id").Input("1234-abcd");
        form.Find("#solcast-save").Click();

        Assert.Equal([("pasted-key", "1234-abcd")], _editor.Saves);
        Assert.Contains("Forecast fetched at 12:00", form.Find("#solcast-status").TextContent);
        Assert.Contains("in use now", form.Find("#solcast-message").TextContent);
        Assert.Equal(string.Empty, form.Find("#solcast-api-key").GetAttribute("value") ?? string.Empty);
    }

    [Fact]
    public void RemoveForgetsThePagesAccount()
    {
        _editor.Settings = Account();
        var form = Render<SolcastAccountForm>();

        form.Find("#solcast-remove").Click();

        Assert.Equal(1, _editor.Removes);
        Assert.Contains("No forecast is held now", form.Find("#solcast-message").TextContent);
        Assert.Contains("Not configured", form.Find("#solcast-status").TextContent);
    }

    [Fact]
    public void AFetchByTheWorkerRedrawsTheStatusAndKeepsWhatIsTyped()
    {
        _editor.Settings = Account();
        var form = Render<SolcastAccountForm>();
        form.Find("#solcast-resource-id").Input("half-typed");

        _editor.Fetched(new SolcastFetchStatus(Now, Now, SolcastFetchFailure.QuotaExhausted, 429));

        form.WaitForAssertion(() => Assert.Contains("used up", form.Find("#solcast-status").TextContent));
        Assert.Equal("half-typed", form.Find("#solcast-resource-id").GetAttribute("value"));
    }

    [Fact]
    public void WithoutALoginTheStatusIsShownButNotTheAccount()
    {
        using var open = new BunitContext();
        var editor = new FakeSolcastAccountEditor { Settings = Account() };
        open.Services.AddSingleton<ISolcastAccountEditor>(editor);
        open.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        open.Services.AddSingleton(Options.Create(new WebOptions()));
        open.Services.AddSingleton(new WebPasswordState(null));

        var form = open.Render<SolcastAccountForm>();

        Assert.NotNull(form.Find("#solcast-readonly"));
        Assert.NotNull(form.Find("#solcast-status"));
        Assert.Empty(form.FindAll("#solcast-api-key"));
        Assert.Empty(form.FindAll("#solcast-save"));
        Assert.DoesNotContain("the-key", form.Markup);
    }

    [Fact]
    public void Every_string_on_the_form_comes_from_a_resource()
    {
        _editor.Settings = Account(status: new SolcastFetchStatus(Now.AddDays(-1), Now, SolcastFetchFailure.QuotaExhausted, 429));

        var form = PseudoLocale.Render(() => Render<SolcastAccountForm>());

        PseudoLocale.AssertTranslated(form.Markup);
    }

    // The source picker (#388): offered only where the host has the selection.
    private (FakeSolarForecastSourceSelection Sources, FakeExternalProviders Providers) WithSources(string selected = SolarForecastSourceIds.Solcast)
    {
        var sources = new FakeSolarForecastSourceSelection { Selected = selected };
        var providers = new FakeExternalProviders();
        providers.Set(new ExternalProviderStatus("solar-forecast", "Solar forecast", "/providers/solar-forecast", HasToken: false,
            selected == SolarForecastSourceIds.External ? ExternalProviderState.Waiting : ExternalProviderState.Off));
        Services.AddSingleton<ISolarForecastSourceSelection>(sources);
        Services.AddSingleton<IExternalProviders>(providers);
        JSInterop.Mode = JSRuntimeMode.Loose;
        return (sources, providers);
    }

    [Fact]
    public void DisposingAfterTheServicesAreGoneDoesNotThrow()
    {
        // A prerender's request scope is disposed before its components, and a lookup in it then throws
        // (#388, seen debugging). A transient that throws once the services are "gone" stands in for that:
        // Dispose must use what was resolved at the start, not look it up again.
        var sources = new FakeSolarForecastSourceSelection();
        var gone = false;
        Services.AddTransient<ISolarForecastSourceSelection>(_ =>
            gone ? throw new ObjectDisposedException("IServiceProvider") : sources);
        var providers = new FakeExternalProviders();
        providers.Set(new ExternalProviderStatus("solar-forecast", "Solar forecast", "/providers/solar-forecast", HasToken: false, ExternalProviderState.Off));
        Services.AddSingleton<IExternalProviders>(providers);
        var form = Render<SolcastAccountForm>();

        gone = true;
        form.Instance.Dispose();

        // And it let go of the selection's event.
        sources.Select(SolarForecastSourceIds.External);
        Assert.Equal(1, form.RenderCount);
    }

    [Fact]
    public void WithoutTheSelectionNoSourceIsOffered()
    {
        var form = Render<SolcastAccountForm>();

        Assert.Empty(form.FindAll("#forecast-source"));
        Assert.NotNull(form.Find("#solcast-status"));
    }

    [Fact]
    public void TheSourcesAreOfferedWithTheSelectedOneChecked()
    {
        WithSources();

        var form = Render<SolcastAccountForm>();

        Assert.True(form.Find("#forecast-source-solcast").HasAttribute("checked"));
        Assert.False(form.Find("#forecast-source-external").HasAttribute("checked"));
        Assert.Empty(form.FindAll("#forecast-source-save"));
        Assert.Empty(form.FindAll("#external-solar-forecast"));
    }

    [Fact]
    public void PickingExternalShowsItsPanelAndSavesOnlyWhenAsked()
    {
        var (sources, _) = WithSources();
        var form = Render<SolcastAccountForm>();

        form.Find("#forecast-source-external").Change(true);

        Assert.Empty(sources.Chosen);
        Assert.Contains("Solcast is still in use", form.Find("#forecast-source-unsaved").TextContent);
        Assert.NotNull(form.Find("#external-solar-forecast"));
        Assert.Empty(form.FindAll("#solcast-api-key"));

        form.Find("#forecast-source-save").Click();

        Assert.Equal([SolarForecastSourceIds.External], sources.Chosen);
        Assert.Equal("Saved. Forecast source: A plugin.", form.Find("#solcast-message").TextContent.Trim());
        Assert.Empty(form.FindAll("#forecast-source-save"));
    }

    [Fact]
    public void CancelGoesBackToTheSourceInUse()
    {
        var (sources, _) = WithSources();
        var form = Render<SolcastAccountForm>();

        form.Find("#forecast-source-external").Change(true);
        form.Find("#forecast-source-cancel").Click();

        Assert.Empty(sources.Chosen);
        Assert.NotNull(form.Find("#solcast-api-key"));
        Assert.Empty(form.FindAll("#external-solar-forecast"));
    }

    [Fact]
    public void ARefusedChoiceSaysWhy()
    {
        var (sources, _) = WithSources();
        sources.Refusal = SettingsSaveResult.Refused(["Could not write data/pv-system.json: disk full"]);
        var form = Render<SolcastAccountForm>();

        form.Find("#forecast-source-external").Change(true);
        form.Find("#forecast-source-save").Click();

        Assert.Contains("disk full", form.Find("#solcast-errors").TextContent);
        Assert.NotNull(form.Find("#forecast-source-save"));
    }

    [Fact]
    public void WithExternalSelectedTheProvidersPanelReplacesTheAccount()
    {
        WithSources(SolarForecastSourceIds.External);

        var form = Render<SolcastAccountForm>();

        Assert.True(form.Find("#forecast-source-external").HasAttribute("checked"));
        Assert.Contains("Solcast is not called", form.Find("#forecast-external-intro").TextContent);
        Assert.Contains("Waiting", form.Find("#external-solar-forecast-state").TextContent);
        Assert.Empty(form.FindAll("#solcast-status"));
    }

    [Fact]
    public void TheWizardDoesNotOfferTheSource()
    {
        WithSources(SolarForecastSourceIds.External);

        var form = Render<SolcastAccountForm>(parameters => parameters.Add(component => component.OfferSource, false));

        Assert.Empty(form.FindAll("#forecast-source"));
        Assert.NotNull(form.Find("#solcast-status"));
    }

    [Fact]
    public void WithoutALoginTheSourceCannotBeChanged()
    {
        using var open = new BunitContext();
        open.Services.AddSingleton<ISolcastAccountEditor>(new FakeSolcastAccountEditor { Settings = Account() });
        open.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Now, Prague));
        open.Services.AddSingleton(Options.Create(new WebOptions()));
        open.Services.AddSingleton(new WebPasswordState(null));
        open.Services.AddSingleton<ISolarForecastSourceSelection>(new FakeSolarForecastSourceSelection());

        var form = open.Render<SolcastAccountForm>();

        Assert.True(form.Find("#forecast-source-external").HasAttribute("disabled"));
    }

    [Fact]
    public void Every_string_of_the_external_source_comes_from_a_resource()
    {
        var (_, providers) = WithSources(SolarForecastSourceIds.External);
        providers.Set(new ExternalProviderStatus("solar-forecast", "Solar forecast", "/providers/solar-forecast", HasToken: true,
            ExternalProviderState.Disconnected, "solar-forecast-sample", "Sample", "1.0", Now.AddHours(-1), Now.AddHours(-2), Unanswered: true));

        var form = PseudoLocale.Render(() => Render<SolcastAccountForm>());

        PseudoLocale.AssertTranslated(form.Markup, "Sample", "http://localhost", "/providers/solar-forecast", "gvp");
    }
}
