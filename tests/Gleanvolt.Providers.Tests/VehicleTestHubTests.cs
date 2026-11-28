using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Models;
using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Gleanvolt.Providers.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// The testing package's vehicle hub (#473), as a car plugin's repository uses it: a plugin on the ordinary SDK,
/// connected over a real socket, welcomed with the car and the charging session, and read through it.
/// </summary>
public sealed class VehicleTestHubTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Captured = DateTimeOffset.UtcNow.AddMinutes(-3);

    private VehicleTestHub _hub = null!;
    private IHost? _plugin;

    public async Task InitializeAsync() =>
        _hub = await VehicleTestHub.StartAsync(new VehicleTestHubOptions { Timeout = TimeSpan.FromSeconds(5) });

    public async Task DisposeAsync()
    {
        if (_plugin is not null)
        {
            await _plugin.StopAsync();
            _plugin.Dispose();
        }

        await _hub.DisposeAsync();
    }

    [Fact]
    public async Task AHelloIsWelcomedWithTheCarAndTheSession()
    {
        var connection = await StartPluginAsync();

        await _hub.WaitForHelloAsync();

        await PluginHarness.Eventually(() => connection.Car is not null, "the welcome reaches the plugin");
        Assert.Equal("test-car", connection.Car!.Id);
        Assert.Equal(ChargingSessionInfo.Closed, connection.Session);
        Assert.True(_hub.IsAttached);
    }

    [Fact]
    public async Task TheFirstReadingFollowsTheHello()
    {
        await StartPluginAsync();

        var reading = await _hub.WaitForReadingAsync();

        Assert.Equal(55, reading.SocPercent);
        Assert.Null(reading.InReplyTo);
    }

    [Fact]
    public async Task ARefreshIsAnsweredInReplyToIt()
    {
        await StartPluginAsync();
        await _hub.WaitForHelloAsync();

        var reading = await _hub.RefreshAsync();

        Assert.NotNull(reading.InReplyTo);
        Assert.Contains(_hub.Readings, r => r.InReplyTo == reading.InReplyTo);
    }

    [Fact]
    public async Task ASessionChangeReachesTheConnection()
    {
        var connection = await StartPluginAsync();
        await _hub.WaitForHelloAsync();

        await _hub.SetSessionAsync(new ChargingSessionInfo(true, DateTimeOffset.UtcNow, "SolarOnly"));

        await PluginHarness.Eventually(() => connection.Session is { Open: true, Mode: "SolarOnly" }, "the session reaches the plugin");
    }

    [Fact]
    public async Task ACarChangeReachesTheConnectionAndIsReadAgain()
    {
        var connection = await StartPluginAsync();
        await _hub.WaitForReadingAsync();
        var before = _hub.Readings.Count;

        await _hub.SetCarAsync(new VehicleInfo("enyaq", "Enyaq", "Škoda", "Enyaq iV", 77, "Europe/Prague"));

        await PluginHarness.Eventually(() => connection.Car?.Id == "enyaq", "the car reaches the plugin");
        await PluginHarness.Eventually(() => _hub.Readings.Count > before, "the plugin publishes for the new car");
    }

    [Fact]
    public async Task AWrongMajorIsRefused()
    {
        await using var raw = new HubConnectionBuilder()
            .WithUrl(new Uri(_hub.Endpoint, VehicleContract.HubPath), options => options.AccessTokenProvider = () => Task.FromResult<string?>(_hub.Token))
            .Build();
        await raw.StartAsync();

        var welcome = await raw.InvokeAsync<VehicleWelcome>(
            ProviderMethods.Hello, new ProviderHello("future-car", "Future car", "9.0.0", VehicleContract.Major + 1, 0));

        Assert.False(welcome.Accepted);
        Assert.Equal(RefusalCodes.ContractMajor, welcome.ReasonCode);
        Assert.False(_hub.IsAttached);
    }

    [Fact]
    public async Task AReadingGleanvoltWouldRefuseIsRefusedHereToo()
    {
        await StartPluginAsync(new VehicleReading(Captured, SocPercent: 140));

        await PluginHarness.Eventually(() => _hub.Refusals.Count > 0, "the refused reading is kept");

        Assert.Empty(_hub.Readings);
        Assert.Contains("SOC", Assert.Single(_hub.Refusals).Problems[0], StringComparison.Ordinal);
    }

    // The test hub's copy of the rules says what Gleanvolt's own validator says, reading for reading.
    [Theory]
    [MemberData(nameof(Readings))]
    public void TheHubsRulesAgreeWithGleanvolts(VehicleReading reading)
    {
        var now = DateTimeOffset.UtcNow;
        var gleanvolt = VehicleReadingValidator.Check(
            new VehicleState(reading.CapturedAt, reading.SocPercent, reading.RangeMetres, reading.ChargeTimeRemaining,
                Map(reading.ChargeState), Map(reading.PlugState), "test"),
            held: null,
            now);

        Assert.Equal(gleanvolt.Count > 0, VehicleReadingRules.Check(reading, held: null, now).Count > 0);
    }

    public static TheoryData<VehicleReading> Readings() => new()
    {
        new VehicleReading(Captured, SocPercent: 55),
        new VehicleReading(Captured, SocPercent: 100.5),
        new VehicleReading(Captured, SocPercent: double.NaN),
        new VehicleReading(Captured, RangeMetres: -1),
        new VehicleReading(Captured, RangeMetres: 2_000_001),
        new VehicleReading(Captured, ChargeTimeRemaining: TimeSpan.FromHours(49), SocPercent: 10),
        new VehicleReading(Captured, ChargeState: CarChargeState.Charging),
        new VehicleReading(Captured, PlugState: CarPlugState.Connected),
        new VehicleReading(Captured),
        new VehicleReading(Captured, ChargeState: CarChargeState.Unknown, PlugState: CarPlugState.Unknown),
        new VehicleReading(default, SocPercent: 55),
        new VehicleReading(DateTimeOffset.UtcNow.AddMinutes(10), SocPercent: 55),
        new VehicleReading(DateTimeOffset.UtcNow.AddDays(-31), SocPercent: 55),
    };

    private static VehicleChargeState Map(CarChargeState? state) => state switch
    {
        CarChargeState.Idle => VehicleChargeState.Idle,
        CarChargeState.Charging => VehicleChargeState.Charging,
        CarChargeState.Complete => VehicleChargeState.Complete,
        _ => VehicleChargeState.Unknown,
    };

    private static VehiclePlugState Map(CarPlugState? state) => state switch
    {
        CarPlugState.Connected => VehiclePlugState.Connected,
        CarPlugState.Disconnected => VehiclePlugState.Disconnected,
        _ => VehiclePlugState.Unknown,
    };

    private async Task<VehicleProviderConnection> StartPluginAsync(VehicleReading? reading = null)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddSingleton(new FixedCar(reading ?? new VehicleReading(Captured, SocPercent: 55)));
        builder.Services.AddGleanvoltVehicleProvider<FixedCarSource>(options =>
        {
            _hub.Configure(options);
            options.ProviderId = "fixed-car";
            options.Name = "Fixed car";
            options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
        });

        _plugin = builder.Build();
        await _plugin.StartAsync();
        return _plugin.Services.GetRequiredService<VehicleProviderConnection>();
    }

    private sealed record FixedCar(VehicleReading Reading);

    private sealed class FixedCarSource(FixedCar car) : IVehicleSource
    {
        public Task<VehicleReading?> GetReadingAsync(VehicleInfo info, CancellationToken cancellationToken) =>
            Task.FromResult<VehicleReading?>(car.Reading);
    }
}
