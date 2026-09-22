using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// The API over TestServer, wired the way <c>GleanvoltHostingExtensions</c> wires it but with the
/// hardware seams faked — the same routes, the same key check and the same generated document as
/// production, with no Modbus client, database or broker anywhere near it.
/// </summary>
internal sealed class ApiTestHost : IAsyncDisposable
{
    internal const string Key = "test-key-0123456789";

    internal const string KeyName = "tests";

    private WebApplication? _app;

    internal ChargeControlStatusHolder Status { get; } = new();

    internal FakeChargeActions Actions { get; } = new();

    internal FakeTargetedChargeSelector Target { get; } = new();

    internal FakeFastChargeSelector Fast { get; } = new();

    internal FakeBatteryHoldSelector Hold { get; } = new();

    internal FakeTargetedChargePreview Preview { get; } = new();

    internal FakeSolarForecastService Forecast { get; } = new();

    internal FakeWeatherService Weather { get; } = new();

    internal FakeEnergyIntervalStore Energy { get; } = new();

    internal FakeChargingSessionStore Sessions { get; } = new();

    internal VehicleStateHolder Vehicle { get; } = new();

    /// <summary>
    /// The configured car (#124) — what it <em>is</em>, as opposed to <see cref="Vehicle"/>, which is
    /// what it last <em>said</em>. Unknown unless a test describes one.
    /// </summary>
    internal EvInfo Car { get; set; } = EvInfo.Unknown;

    internal TimeZoneInfo Zone { get; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    /// <summary>Starts the host and returns a client that already presents a valid key.</summary>
    internal async Task<HttpClient> StartAsync(ApiOptions? options = null, double batteryCapacityKWh = 77)
    {
        var api = options ?? new ApiOptions
        {
            Enabled = true,
            Keys = new Dictionary<string, string> { [KeyName] = Key },
        };

        // Slim rather than the default builder: the default one watches appsettings.json for changes,
        // and a suite that stands a host up per test then runs out of the machine's inotify instances
        // long before it runs out of tests. Nothing here reads configuration files anyway.
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();

        builder.Services.AddSingleton(Options.Create(api));
        builder.Services.AddSingleton(Status);
        builder.Services.AddSingleton<IChargeActions>(Actions);
        builder.Services.AddSingleton<ITargetedChargeSelector>(Target);
        builder.Services.AddSingleton<IFastChargeSelector>(Fast);
        builder.Services.AddSingleton<IBatteryHoldSelector>(Hold);
        builder.Services.AddSingleton<ITargetedChargePreview>(Preview);
        builder.Services.AddSingleton<ISolarForecastService>(Forecast);
        builder.Services.AddSingleton<IWeatherService>(Weather);
        builder.Services.AddSingleton<IEnergyIntervalStore>(Energy);
        builder.Services.AddSingleton<IChargingSessionStore>(Sessions);
        builder.Services.AddSingleton<IVehicleTelemetry>(Vehicle);
        builder.Services.AddSingleton(new ApiHostInfo("1.2.3-test", TimeSpan.FromHours(12)));
        builder.Services.AddSingleton(Fixtures.Site);
        builder.Services.AddSingleton(Car);
        builder.Services.AddSingleton(new TargetedChargeRequestLimits(
            MaxHorizon: TimeSpan.FromHours(36),
            BatteryCapacityKWh: batteryCapacityKWh,
            ChargeEfficiency: 0.9,
            DefaultRestSocPercent: 80));
        builder.Services.AddSingleton<TimeProvider>(new FixedTimeProvider(Fixtures.Now, Zone));

        builder.Services.AddGleanvoltApi(api);

        _app = builder.Build();
        _app.MapGleanvoltApi(api, _app.Logger);

        await _app.StartAsync();

        var client = _app.GetTestClient();
        client.DefaultRequestHeaders.Add("Authorization", $"Bearer {Key}");

        return client;
    }

    /// <summary>A client with no key at all, for the endpoints-are-shut tests.</summary>
    internal HttpClient Anonymous() => _app!.GetTestClient();

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }
}
