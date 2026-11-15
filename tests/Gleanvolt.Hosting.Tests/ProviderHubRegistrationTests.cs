using System.Net;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Providers.Contracts.SolarForecast;
using Gleanvolt.Providers.Contracts.Tariff;
using Gleanvolt.Web;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The provider hubs (#386, #401, #388) as the composition root mounts them: behind the provider token,
/// one for every kind (#406), whether or not the web UI is on, past the password gate a fresh install has, and not behind the UI's cookie.
/// </summary>
public sealed class ProviderHubRegistrationTests : IDisposable
{
    private readonly string _data = Path.Combine(Path.GetTempPath(), "gleanvolt-provider-hub-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_data))
        {
            Directory.Delete(_data, recursive: true);
        }
    }

    private WebApplication Build(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Configuration.AddInMemoryCollection(
            UsableSite.Settings
                .Concat([(Key: "Secrets:Directory", Value: (string?)_data), (Key: "Tariff:Path", Value: (string?)Path.Combine(_data, "tariff.json")), .. settings])
                .GroupBy(setting => setting.Key)
                .Select(group => new KeyValuePair<string, string?>(group.Key, group.Last().Value)));
        builder.Services.AddGleanvolt(builder.Configuration);

        // Nothing here should poll a device or talk to a broker.
        builder.Services.RemoveAll<IHostedService>();

        return builder.Build();
    }

    private static async Task<HttpStatusCode> NegotiateAsync(WebApplication app, string? token, string hubPath = TariffContract.HubPath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, hubPath + "/negotiate?negotiateVersion=1");

        if (token is not null)
        {
            request.Headers.Authorization = new("Bearer", token);
        }

        return (await app.GetTestClient().SendAsync(request)).StatusCode;
    }

    [Fact]
    public async Task WithOnlyTheApiOnTheHubAnswersItsTokenAndNothingElse()
    {
        await using var app = Build(("Web:Enabled", "false"), ("Api:Enabled", "true"), ("Api:Keys:test", "an-api-key-0123456789"));
        app.UseGleanvolt();
        await app.StartAsync();
        var token = await app.Services.GetRequiredService<IExternalProviders>().CreateTokenAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(app, null));
        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(app, "an-api-key-0123456789"));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(app, token));
    }

    [Fact]
    public async Task WithTheUiOnAndNoPasswordYetTheHubIsNotSentToTheSetupPage()
    {
        await using var app = Build();
        var web = app.Services.GetRequiredService<IOptions<WebOptions>>().Value;

        // What UseGleanvolt mounts, without the UI's static assets a test host has none of.
        app.MapGleanvoltWebUi(web);
        Providers.Server.ExternalProviderExtensions.MapExternalProviders(app);
        await app.StartAsync();
        var token = await app.Services.GetRequiredService<IExternalProviders>().CreateTokenAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(app, null));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(app, token));
    }

    [Fact]
    public async Task TheTokenIsKeptInTheSecretStoreSoSettingsCanRevealIt()
    {
        await using var app = Build(("Web:Enabled", "false"), ("Api:Enabled", "true"), ("Api:Keys:test", "an-api-key-0123456789"));
        var providers = app.Services.GetRequiredService<IExternalProviders>();
        var token = await providers.CreateTokenAsync();

        Assert.Equal(token, app.Services.GetRequiredService<ISecretStore>().Read(SecretNames.ProviderToken));
        Assert.Equal(token, providers.ReadToken());
        Assert.True(providers.HasToken);
        Assert.All(providers.All, status => Assert.True(status.HasToken));
    }

    [Fact]
    public async Task OneTokenOpensEveryKindsHubAndANewOneClosesThemAll()
    {
        await using var app = Build(("Web:Enabled", "false"), ("Api:Enabled", "true"), ("Api:Keys:test", "an-api-key-0123456789"));
        app.UseGleanvolt();
        await app.StartAsync();
        var providers = app.Services.GetRequiredService<IExternalProviders>();
        var old = await providers.CreateTokenAsync();

        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(app, old, TariffContract.HubPath));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(app, old, SolarForecastContract.HubPath));

        var token = await providers.CreateTokenAsync();

        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(app, old, TariffContract.HubPath));
        Assert.Equal(HttpStatusCode.Unauthorized, await NegotiateAsync(app, old, SolarForecastContract.HubPath));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(app, token, SolarForecastContract.HubPath));
    }

    [Fact]
    public async Task ATariffTokenFromBefore406IsAdoptedSoItsPluginKeepsConnecting()
    {
        await using var app = Build(("Web:Enabled", "false"), ("Api:Enabled", "true"), ("Api:Keys:test", "an-api-key-0123456789"));
        var secrets = app.Services.GetRequiredService<ISecretStore>();
        secrets.Write(SecretNames.LegacyProviderToken("tariff"), "gvp_the-tariff-plugins-token");
        app.UseGleanvolt();
        await app.StartAsync();

        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(app, "gvp_the-tariff-plugins-token", TariffContract.HubPath));
        Assert.Equal(HttpStatusCode.OK, await NegotiateAsync(app, "gvp_the-tariff-plugins-token", SolarForecastContract.HubPath));
        Assert.Equal("gvp_the-tariff-plugins-token", secrets.Read(SecretNames.ProviderToken));
        Assert.Null(secrets.Read(SecretNames.LegacyProviderToken("tariff")));
    }
}
