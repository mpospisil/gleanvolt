using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.HomeAssistant;
using Gleanvolt.Hosting.Vehicles;
using Gleanvolt.Hosting.Vehicles.External;
using Gleanvolt.Plugin.VehicleSample;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Server;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The car as an external provider (#408), end to end: the real hub, feed, holder and refresh on a
/// TestServer, with the vehicle sample as the provider, connecting the way a provider author's would.
/// </summary>
public sealed class ExternalVehicleHubTests : IAsyncDisposable
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private static readonly EvInfo Car = EvInfo.Unknown with
    {
        Id = "id4",
        Name = "Rosie",
        Make = "Volkswagen",
        Model = "ID.4",
        BatteryCapacityWh = 77_000,
    };

    private readonly FakeTimeProvider _time = new(new DateTimeOffset(2026, 11, 15, 9, 0, 0, TimeSpan.Zero));

    private WebApplication? _app;
    private string _token = string.Empty;

    private VehicleStateHolder Holder => _app!.Services.GetRequiredService<VehicleStateHolder>();

    private ConfiguredVehicleFeed Feed => _app!.Services.GetRequiredService<ConfiguredVehicleFeed>();

    private IVehicleStateRefresh Refresh => _app!.Services.GetRequiredService<IVehicleStateRefresh>();

    private ExternalProviderStatus Status => _app!.Services.GetRequiredService<ExternalProviderRegistry>().Find(VehicleContract.Kind)!;

    public async ValueTask DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }
    }

    [Fact]
    public async Task TheSampleConnectsLearnsTheCarAndItsReadingIsWhatTheTelemetryServes()
    {
        await StartAsync();
        using var sample = Sample();
        VehicleWelcome? welcome = null;
        sample.Welcomed += car => welcome = car;

        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Holder.GetCurrentState() is not null, "the sample's reading should reach the holder");

        Assert.Equal(new VehicleInfo("id4", "Rosie", "Volkswagen", "ID.4", 77, "Europe/Prague"), welcome!.Car);

        var reading = _app!.Services.GetRequiredService<IVehicleTelemetry>().GetCurrentState()!;
        Assert.Equal(SimulatedCar.StartSocPercent, reading.SocPercent);
        Assert.Equal(VehicleChargeState.Charging, reading.ChargeState);
        Assert.Equal(VehiclePlugState.Connected, reading.PlugState);
        Assert.Equal(_time.GetUtcNow(), reading.CapturedAt);
        // 40 % of 77 kWh at 18 kWh per 100 km.
        Assert.Equal(171_000, reading.RangeMetres);
        Assert.Equal("Vehicle sample", reading.SourceId);

        Assert.Equal(VehicleSourceState.Ok, Feed.Health!.State);
        Assert.Equal("Vehicle sample", Feed.Service!.DisplayName);

        await sample.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AskTheCarSendsARequestAndTheAnswerQuotingItIsFresh()
    {
        await StartAsync();
        await using var connection = await ConnectAsync();
        RefreshRequest? asked = null;
        connection.On<RefreshRequest>(ProviderMethods.RefreshRequested, request => asked = request);
        Assert.True((await connection.InvokeAsync<VehicleWelcome>("Hello", Hello())).Accepted);

        var asking = Refresh.RefreshAsync();
        await Eventually(() => asked is not null, "Ask the car should reach the provider");

        var result = await connection.InvokeAsync<PublishResult>("Publish", Reading(soc: 64, inReplyTo: asked!.Id));
        Assert.True(result.Accepted, string.Join(" ", result.Problems));

        var answer = await asking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(answer.Succeeded);
        Assert.True(answer.IsFresh);
        Assert.Equal(64, answer.State!.SocPercent);
        Assert.Equal("Raw client", answer.Source);
        Assert.False(Status.Unanswered);
    }

    [Fact]
    public async Task AMissingAnswerEndsDegradedAndKeepsTheLastReading()
    {
        await StartAsync();
        await using var connection = await ConnectAsync();
        RefreshRequest? asked = null;
        connection.On<RefreshRequest>(ProviderMethods.RefreshRequested, request => asked = request);
        await connection.InvokeAsync<VehicleWelcome>("Hello", Hello());
        Assert.True((await connection.InvokeAsync<PublishResult>("Publish", Reading(soc: 55))).Accepted);

        var asking = Refresh.RefreshAsync();
        await Eventually(() => asked is not null, "Ask the car should reach the provider");

        for (var i = 0; i < 100 && !asking.IsCompleted; i++)
        {
            _time.Advance(ExternalVehicleFeed.Timeout + TimeSpan.FromSeconds(1));
            await Task.Delay(25);
        }

        var answer = await asking.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(answer.Succeeded);
        Assert.Equal(55, answer.State!.SocPercent);
        Assert.Contains("didn't answer", answer.Message);
        Assert.Equal(VehicleSourceState.Degraded, Feed.Health!.State);
        Assert.Equal("ExternalVehicle.NoAnswer", Feed.Health.Phrase!.Key);
        Assert.Equal(55, Holder.GetCurrentState()!.SocPercent);

        // The next reading clears it.
        Assert.True((await connection.InvokeAsync<PublishResult>("Publish", Reading(soc: 56))).Accepted);
        Assert.Equal(VehicleSourceState.Ok, Feed.Health.State);
    }

    [Fact]
    public async Task AProviderThatNeedsItsOwnerBlocksTheFeedAndHomeAssistantSaysSo()
    {
        await StartAsync();
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<VehicleWelcome>("Hello", Hello());

        await connection.InvokeAsync("ReportStatus", new ProviderStatus(false, "The login expired.", NeedsOwner: true));
        await Eventually(() => Feed.Health!.IsBlocked, "NeedsOwner should block the feed");

        Assert.Contains("The login expired.", Feed.Health!.Message);
        Assert.Contains("The login expired.", Feed.Service!.OwnerAction);
        Assert.True(Status.NeedsOwner);

        var discovery = new HaDiscovery(new HomeAssistantOptions(), Sites.Home, vehicleFeedConfigured: true);
        using var json = JsonDocument.Parse(discovery.StateJson(HaStatus(), Feed.Health));
        Assert.Equal("NeedsOwner", json.RootElement.GetProperty("car_feed").GetString());

        // Unhealthy alone is something the provider is working on, not something for the owner.
        await connection.InvokeAsync("ReportStatus", new ProviderStatus(false, "The car's cloud is down."));
        await Eventually(() => !Feed.Health!.IsBlocked, "unhealthy without NeedsOwner should not block");
        Assert.Equal(VehicleSourceState.Degraded, Feed.Health!.State);
        Assert.False(Status.NeedsOwner);
    }

    [Fact]
    public async Task ARefusedReadingChangesNothingAndSaysWhy()
    {
        await StartAsync();
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<VehicleWelcome>("Hello", Hello());

        var result = await connection.InvokeAsync<PublishResult>("Publish", Reading(soc: 140));

        Assert.False(result.Accepted);
        Assert.Contains("SOC is 140", Assert.Single(result.Problems));
        Assert.Null(Holder.GetCurrentState());
        Assert.Equal("Vehicle.Soc", Status.LastProblemPhrases![0]!.Key);
        Assert.Equal("ExternalVehicle.Refused", Feed.Health!.Phrase!.Key);
    }

    [Fact]
    public async Task BesideTheMqttTopicTheNewestReadingWins()
    {
        await StartAsync();
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<VehicleWelcome>("Hello", Hello());

        // What the MQTT worker writes: a reading a minute ago.
        Holder.Set(new VehicleState(_time.GetUtcNow().AddMinutes(-1), SocPercent: 50, SourceId: "id4"));

        var older = await connection.InvokeAsync<PublishResult>("Publish", Reading(soc: 49, capturedAt: _time.GetUtcNow().AddMinutes(-2)));
        Assert.False(older.Accepted);
        Assert.Contains("older than the reading Gleanvolt holds", Assert.Single(older.Problems));
        Assert.Equal(50, Holder.GetCurrentState()!.SocPercent);

        Assert.True((await connection.InvokeAsync<PublishResult>("Publish", Reading(soc: 51))).Accepted);
        Assert.Equal(51, Holder.GetCurrentState()!.SocPercent);

        // A newer topic reading wins over the provider's in turn.
        Assert.True(Holder.Set(new VehicleState(_time.GetUtcNow().AddSeconds(1), SocPercent: 52, SourceId: "id4")));
        Assert.Equal("id4", Holder.GetCurrentState()!.SourceId);
    }

    [Fact]
    public async Task AfterADisconnectTheReadingStaysAndTheFeedSaysFromWhen()
    {
        await StartAsync();
        using var sample = Sample();
        await sample.StartAsync(CancellationToken.None);
        await Eventually(() => Holder.GetCurrentState() is not null, "the sample should publish");

        await sample.StopAsync(CancellationToken.None);
        await Eventually(() => Status.State == ExternalProviderState.Disconnected, "the session should end");

        Assert.NotNull(Holder.GetCurrentState());
        Assert.Equal(VehicleSourceState.Degraded, Feed.Health!.State);
        Assert.Equal("ExternalVehicle.Disconnected", Feed.Health.Phrase!.Key);
        Assert.StartsWith("external car provider disconnected, reading from", Feed.Health.Message);

        // Nothing to ask; the last reading comes back with the failure.
        var answer = await Refresh.RefreshAsync();
        Assert.False(answer.Succeeded);
        Assert.NotNull(answer.State);
    }

    [Fact]
    public async Task TheReadingTravelsWithNamesAndTimeSpanStrings()
    {
        await StartAsync();
        await using var connection = await ConnectAsync();
        await connection.InvokeAsync<VehicleWelcome>("Hello", Hello());

        // What a client in another language sends.
        var result = await connection.InvokeAsync<PublishResult>("Publish", new
        {
            capturedAt = _time.GetUtcNow(),
            socPercent = 72.5,
            chargeTimeRemaining = "01:30:00",
            chargeState = "charging",
            plugState = "connected",
        });

        Assert.True(result.Accepted, string.Join(" ", result.Problems));
        var held = Holder.GetCurrentState()!;
        Assert.Equal(72.5, held.SocPercent);
        Assert.Equal(TimeSpan.FromMinutes(90), held.ChargeTimeRemaining);
        Assert.Equal(VehicleChargeState.Charging, held.ChargeState);
        Assert.Equal(VehiclePlugState.Connected, held.PlugState);
        Assert.Null(held.RangeMetres);
    }

    [Fact]
    public async Task WithAnotherFeedChosenAHelloIsRefusedAsDisabled()
    {
        await StartAsync(external: false);
        await using var connection = await ConnectAsync();

        var welcome = await connection.InvokeAsync<VehicleWelcome>("Hello", Hello());

        Assert.False(welcome.Accepted);
        Assert.Equal(RefusalCodes.Disabled, welcome.ReasonCode);
        Assert.Equal(ExternalProviderState.Off, Status.State);
    }

    [Fact]
    public async Task ThePollingWorkerLeavesAPushedFeedAlone()
    {
        await StartAsync();
        var feed = (ExternalVehicleFeed)Feed.Service!;
        var worker = new VehicleUpdateWorker([feed], Holder, NullLogger<VehicleUpdateWorker>.Instance, time: _time);

        await worker.StartAsync(CancellationToken.None);
        await (worker.ExecuteTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5));
        await worker.StopAsync(CancellationToken.None);

        // It asked nothing: no request was ever started.
        Assert.False(Status.Unanswered);
        Assert.Null(Holder.GetCurrentState());
    }

    private async Task StartAsync(bool external = true)
    {
        _time.SetLocalTimeZone(Prague);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseTestServer();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton<TimeProvider>(_time);
        builder.Services.AddSingleton<ISecretStore>(new RecordingSecretStore());
        builder.Services.AddSingleton(new ProviderHostInfo("1.3.0-test"));
        builder.Services.AddSingleton(Car);
        builder.Services.AddSingleton(new VehicleStateHolder());
        builder.Services.AddSingleton<IVehicleTelemetry>(provider => provider.GetRequiredService<VehicleStateHolder>());

        if (external)
        {
            builder.Services.AddSingleton(provider => new ExternalVehicleFeed(
                provider, provider.GetRequiredService<VehicleStateHolder>(), Car.Id, _time, NullLogger<ExternalVehicleFeed>.Instance));
            builder.Services.AddSingleton(provider => new ConfiguredVehicleFeed(provider.GetRequiredService<ExternalVehicleFeed>()));
        }
        else
        {
            builder.Services.AddSingleton(ConfiguredVehicleFeed.None);
        }

        builder.Services.AddSingleton<IVehicleStateRefresh>(provider => new VehicleStateRefresh(
            provider.GetRequiredService<ConfiguredVehicleFeed>(), provider.GetRequiredService<VehicleStateHolder>()));
        builder.Services.AddExternalProvider<VehicleHub>(VehicleHub.Kind);

        _app = builder.Build();
        _app.UseAuthentication();
        _app.UseAuthorization();
        _app.MapExternalProviders();
        await _app.StartAsync();

        _token = await _app.Services.GetRequiredService<ExternalProviderRegistry>().CreateTokenAsync();
    }

    private VehicleReading Reading(double soc, RequestId? inReplyTo = null, DateTimeOffset? capturedAt = null) =>
        new(capturedAt ?? _time.GetUtcNow(), inReplyTo, soc, ChargeState: CarChargeState.Charging, PlugState: CarPlugState.Connected);

    private VehicleProviderConnection Sample() => new(
        Options.Create(new ProviderOptions
        {
            Endpoint = _app!.GetTestServer().BaseAddress,
            Token = _token,
            ProviderId = "vehicle-sample",
            Name = "Vehicle sample",
            Version = "1.0",
            InitialRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(200),
            RefusedRetryDelay = TimeSpan.FromMilliseconds(100),
            ConfigureHttp = Configure,
        }),
        new SimulatedCar(_time),
        NullLogger<VehicleProviderConnection>.Instance);

    private async Task<HubConnection> ConnectAsync()
    {
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(_app!.GetTestServer().BaseAddress, VehicleContract.HubPath), Configure)
            .Build();
        await connection.StartAsync();
        return connection;
    }

    private void Configure(Microsoft.AspNetCore.Http.Connections.Client.HttpConnectionOptions options)
    {
        options.HttpMessageHandlerFactory = _ => _app!.GetTestServer().CreateHandler();
        options.Transports = HttpTransportType.LongPolling;
        options.AccessTokenProvider = () => Task.FromResult<string?>(_token);
    }

    private static ProviderHello Hello() => new("raw-client", "Raw client", "0.1", VehicleContract.Major, VehicleContract.Minor);

    private static ChargeControlStatus HaStatus() =>
        new(ChargeControlMode.Solar, DryRun: true, HoldingControl: true, ChargeControlState.Charging, 4000, 6, 16, BatterySocPercent: 90,
            ChargerStatus: EvChargerStatus.Charging, CarConnected: true, SolarPowerWatts: 7000,
            ForecastSolarPowerWatts: 6500,
            EvChargerPowerWatts: 4000, EvChargingCurrentAmps: 6, BatteryPowerWatts: 0,
            GridPowerWatts: 0,
            BatteryHoldEnabled: false, BatteryHoldRequested: false, BatteryHoldActive: false,
            BatteryHoldTargetWatts: null, Plan: null, LoanPowerWatts: 0, SessionEnergyWh: 0,
            LoanedTodayWh: 0, TomorrowForecastWh: null, Timestamp: DateTimeOffset.UtcNow);

    private static async Task Eventually(Func<bool> condition, string because)
    {
        for (var i = 0; i < 200 && !condition(); i++)
        {
            await Task.Delay(25);
        }

        Assert.True(condition(), because);
    }
}
