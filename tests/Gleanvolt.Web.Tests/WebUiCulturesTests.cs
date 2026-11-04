using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Models;
using Gleanvolt.Web.Auth;

namespace Gleanvolt.Web.Tests;

/// <summary>
/// The viewer's language (#290) through the real pipeline: which culture a page is rendered in, the
/// picker's endpoint, and that the HTTP API is left out of it. The sign-in page is the page under
/// test because it renders with almost nothing registered.
/// </summary>
public sealed class WebUiCulturesTests : IAsyncDisposable
{
    private WebApplication? _app;

    private async Task<HttpClient> StartAsync(string environment)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions { EnvironmentName = environment });
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        var web = new WebOptions { Enabled = true, RequireAuthentication = true, PasswordHash = WebPasswordHasher.Hash("s3cret") };
        builder.Services.AddSingleton(Options.Create(web));
        builder.Services.AddSingleton(new ChargeControlStatusHolder());
        builder.Services.AddSingleton(Sites.Home);
        builder.Services.AddGleanvoltWebUi(web);

        _app = builder.Build();
        _app.MapGleanvoltWebUi(web);

        // Stands in for the API: what culture a request under /api runs in.
        _app.MapGet("/api/v1/culture", () => System.Globalization.CultureInfo.CurrentCulture.Name);

        await _app.StartAsync();
        return _app.GetTestClient();
    }

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task A_browser_with_no_match_gets_english()
    {
        var client = await StartAsync(Environments.Production);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd("de-DE");

        var html = await client.GetStringAsync("/login");

        Assert.Contains("<html lang=\"en\">", html);
    }

    [Fact]
    public async Task With_one_language_there_is_nothing_to_pick()
    {
        var client = await StartAsync(Environments.Production);

        var html = await client.GetStringAsync("/login");

        Assert.DoesNotContain("name=\"culture\"", html);
    }

    [Fact]
    public async Task Development_offers_the_pseudo_locale_and_accept_language_picks_it()
    {
        var client = await StartAsync(Environments.Development);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(WebUiCultures.Pseudo);

        var html = await client.GetStringAsync("/login");

        // ICU spells it "qps-Ploc".
        var pseudo = System.Globalization.CultureInfo.GetCultureInfo(WebUiCultures.Pseudo).Name;
        Assert.Contains($"<html lang=\"{pseudo}\">", html);
        Assert.Contains($"<option value=\"{pseudo}\" selected", html);
    }

    [Fact]
    public async Task The_picker_remembers_the_choice_and_goes_back_to_the_page()
    {
        var client = await StartAsync(Environments.Development);

        var response = await Pick(client, WebUiCultures.Pseudo, "/login?x=1");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/login?x=1", response.Headers.Location?.OriginalString);
        var cookie = Assert.Single(response.Headers.GetValues("Set-Cookie"), value => value.StartsWith(".AspNetCore.Culture=", StringComparison.Ordinal));
        Assert.Contains("qps-ploc", Uri.UnescapeDataString(cookie), StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("https://example.com/")]
    [InlineData("//example.com/")]
    [InlineData("/\\example.com/")]
    public async Task The_picker_never_redirects_off_the_site(string returnUrl)
    {
        var client = await StartAsync(Environments.Development);

        var response = await Pick(client, WebUiCultures.Pseudo, returnUrl);

        Assert.Equal("/", response.Headers.Location?.OriginalString);
    }

    [Fact]
    public async Task A_language_that_is_not_offered_is_ignored()
    {
        var client = await StartAsync(Environments.Production);

        var response = await Pick(client, WebUiCultures.Pseudo, "/login");

        Assert.False(response.Headers.Contains("Set-Cookie"));
    }

    [Fact]
    public async Task The_api_stays_invariant_whatever_the_browser_asks_for()
    {
        var client = await StartAsync(Environments.Development);
        client.DefaultRequestHeaders.AcceptLanguage.ParseAdd(WebUiCultures.Pseudo);

        var culture = await client.GetStringAsync("/api/v1/culture");

        Assert.Equal(string.Empty, culture);
    }

    private static Task<HttpResponseMessage> Pick(HttpClient client, string culture, string returnUrl) =>
        client.PostAsync(WebUiCultures.Path, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["culture"] = culture,
            ["returnUrl"] = returnUrl,
        }));
}
