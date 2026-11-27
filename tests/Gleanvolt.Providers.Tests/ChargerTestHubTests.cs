using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Gleanvolt.Chargers;
using Gleanvolt.Providers.Testing;
using System.Net.Http.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Tests;

/// <summary>
/// The testing package (#439) as a charger plugin's repository uses it: a plugin built on the ordinary SDK, connected
/// to <see cref="ChargerTestHub"/> over a real socket, read and commanded through it.
/// </summary>
public sealed class ChargerTestHubTests : IAsyncLifetime
{
    private ChargerTestHub _hub = null!;
    private IHost? _plugin;

    public async Task InitializeAsync() =>
        _hub = await ChargerTestHub.StartAsync(new ChargerTestHubOptions
        {
            Limits = new ChargerLimits(6, 20, 1, 230),
            Timeout = TimeSpan.FromSeconds(5),
        });

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
    public async Task A_plugin_says_hello_is_welcomed_with_the_limits_and_is_read_on_request()
    {
        var charger = await StartPluginAsync();
        charger.State = ChargerState.Charging;
        charger.PowerWatts = 2300;

        var hello = await _hub.WaitForHelloAsync();
        var reading = await _hub.ReadAsync();

        Assert.Equal("fake-charger", hello.ProviderId);
        Assert.Equal(new ChargerLimits(6, 20, 1, 230), PluginConnection().Limits);
        Assert.Equal(ChargerState.Charging, reading.Status);
        Assert.Equal(2300, reading.PowerWatts);
        Assert.NotNull(reading.InReplyTo);

        // The SDK publishes once after the hello, unprompted, and then once for the request.
        Assert.Contains(_hub.Readings, r => r.InReplyTo is null);
        Assert.Contains(_hub.Readings, r => r.InReplyTo == reading.InReplyTo);
    }

    [Fact]
    public async Task Commands_reach_the_charger_in_order_and_each_is_answered()
    {
        var charger = await StartPluginAsync();
        await _hub.WaitForHelloAsync();

        var allowed = await _hub.SetChargingAllowedAsync(true);
        var current = await _hub.SetCurrentAsync(10, "more sun");
        var pause = await _hub.SetCurrentAsync(0, "a cloud");

        Assert.True(allowed.Succeeded);
        Assert.True(current.Succeeded);
        Assert.True(pause.Succeeded);
        Assert.Equal(["allowed=True", "current=10", "current=0"], charger.Commands);
    }

    [Fact]
    public async Task A_command_the_charger_refuses_comes_back_failed_with_the_reason()
    {
        var charger = await StartPluginAsync();
        charger.RefuseCommands = "the charger said no";
        await _hub.WaitForHelloAsync();

        var result = await _hub.SetCurrentAsync(10);

        Assert.False(result.Succeeded);
        Assert.Equal("the charger said no", result.Error);
    }

    [Fact]
    public async Task A_reading_Gleanvolt_would_refuse_fails_the_read_with_every_problem()
    {
        var charger = await StartPluginAsync();
        await _hub.WaitForHelloAsync();

        // kW sent as W, from two minutes ago.
        charger.PowerWatts = 230_000;
        charger.Age = TimeSpan.FromMinutes(2);

        var refused = await Assert.ThrowsAsync<ChargerContractException>(() => _hub.ReadAsync());

        Assert.Equal(2, refused.Problems.Count);
        Assert.Contains(refused.Problems, p => p.Contains("seconds ago", StringComparison.Ordinal));
        Assert.Contains(refused.Problems, p => p.Contains("230000 W", StringComparison.Ordinal));
        Assert.Contains(_hub.Refusals, r => r.Reading.PowerWatts == 230_000);
    }

    [Fact]
    public async Task A_status_the_plugin_reports_is_kept()
    {
        await StartPluginAsync();
        await _hub.WaitForHelloAsync();

        // The hub accepts the hello a moment before the SDK has its welcome, and the SDK reports nothing until then.
        while (!PluginConnection().IsAttached)
        {
            await Task.Delay(20);
        }

        await PluginConnection().ReportStatusAsync(
            new ProviderStatus(false, "No address yet.", NeedsOwner: true, Code: ProviderStatusCodes.NotSetUp));
        var status = await _hub.WaitForStatusAsync(s => !s.Healthy);

        Assert.True(status.NeedsOwner);
        Assert.Equal(ProviderStatusCodes.NotSetUp, status.Code);
    }

    [Fact]
    public async Task A_plugin_with_the_wrong_token_never_gets_in()
    {
        await StartPluginAsync(token: "not-the-token");

        await Task.Delay(TimeSpan.FromSeconds(1));

        Assert.False(_hub.IsAttached);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _hub.ReadAsync());
    }

    [Fact]
    public async Task The_hello_carries_the_chargers_capabilities()
    {
        await StartPluginAsync(capabilities: new ChargerCapabilities(6, 32, 1));

        var hello = await _hub.WaitForHelloAsync();

        Assert.Equal(new ChargerCapabilities(6, 32, 1), hello.Charger);
        Assert.Equal(new ChargerCapabilities(6, 32, 1), _hub.Capabilities);
        Assert.Equal("fake-charger", _hub.RememberedProviderId);
    }

    [Fact]
    public async Task A_charger_short_of_the_limits_is_refused_with_limits()
    {
        // The hub's charge control wants 6-20 A on one phase.
        await StartPluginAsync(capabilities: new ChargerCapabilities(6, 16, 1));

        while (_hub.LastRefusal is null)
        {
            await Task.Delay(20);
        }

        Assert.Equal(RefusalCodes.Limits, _hub.LastRefusal.Value.Code);
        Assert.Contains("ChargeControl:MaxChargingCurrentAmps (20 A)", _hub.LastRefusal.Value.Reason);
        Assert.False(_hub.IsAttached);
    }

    [Fact]
    public async Task Another_plugin_is_refused_until_the_remembered_one_is_forgotten()
    {
        await StartPluginAsync();
        await _hub.WaitForHelloAsync();
        await _plugin!.StopAsync();
        _plugin.Dispose();

        while (_hub.IsAttached)
        {
            await Task.Delay(20);
        }

        await StartPluginAsync(id: "another-charger");

        while (_hub.LastRefusal is null)
        {
            await Task.Delay(20);
        }

        Assert.Equal(RefusalCodes.AnotherProvider, _hub.LastRefusal.Value.Code);

        _hub.Forget();
        var hello = await _hub.WaitForHelloAsync();

        Assert.Equal("another-charger", hello.ProviderId);
        Assert.Equal("another-charger", _hub.RememberedProviderId);
    }

    [Fact]
    public async Task A_plugin_without_a_token_pairs_with_the_test_hub_and_is_read()
    {
        await using var harness = new PluginHarness(new PluginInfo("paired-charger", "Paired charger", "Paired", 0, 502));
        var hub = await harness.ConnectAsync(new PairedCharger(), new ChargerLimits(6, 16, 1, 230), paired: true);

        var reading = await hub.ReadAsync();

        Assert.Equal(2300, reading.PowerWatts);
        Assert.Single(hub.PairingKnocks);
        Assert.Equal("paired-charger", hub.PairingKnocks[0].ProviderId);
        Assert.Equal(CredentialSource.Paired, harness.Credentials!.Source);
        Assert.Equal(hub.Token, harness.Credentials.Token);
    }

    [Fact]
    public async Task The_hub_answers_unknown_to_a_collect_with_the_wrong_secret()
    {
        using var http = new HttpClient { BaseAddress = _hub.Endpoint };
        var knock = new PairingKnock(ChargerContract.HubPath, "someone", "Someone", "1", null, PairingSecret.Hash("right"));
        var ticket = await (await http.PostAsJsonAsync(PairingPaths.Knock, knock)).Content.ReadFromJsonAsync<PairingTicket>();

        var wrong = await http.PostAsJsonAsync(PairingPaths.Collect(ticket!.RequestId), new PairingCollect("wrong"));
        var right = await http.PostAsJsonAsync(PairingPaths.Collect(ticket.RequestId), new PairingCollect("right"));

        Assert.Contains("\"unknown\"", await wrong.Content.ReadAsStringAsync());
        Assert.Contains("\"accepted\"", await right.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task A_hub_without_pairing_answers_a_knock_with_404_as_an_older_gleanvolt_does()
    {
        await using var hub = await ChargerTestHub.StartAsync(new ChargerTestHubOptions { AcceptPairing = false });
        using var http = new HttpClient { BaseAddress = hub.Endpoint };
        var knock = new PairingKnock(ChargerContract.HubPath, "someone", "Someone", "1", null, PairingSecret.Hash("s"));

        var response = await http.PostAsJsonAsync(PairingPaths.Knock, knock);

        Assert.Equal(System.Net.HttpStatusCode.NotFound, response.StatusCode);
    }

    private async Task<FakeCharger> StartPluginAsync(string? token = null, ChargerCapabilities? capabilities = null, string id = "fake-charger")
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Logging.ClearProviders();
        builder.Services.AddGleanvoltChargerProvider<FakeCharger>(options =>
        {
            _hub.Configure(options);
            options.Token = token ?? options.Token;
            options.ProviderId = id;
            options.Name = "Fake charger";
            options.InitialRetryDelay = TimeSpan.FromMilliseconds(100);
            options.RefusedRetryDelay = TimeSpan.FromMilliseconds(100);
        });

        _plugin = builder.Build();
        var charger = (FakeCharger)_plugin.Services.GetRequiredService<IChargerSource>();
        charger.Capabilities = capabilities ?? charger.Capabilities;
        await _plugin.StartAsync();
        return charger;
    }

    private ChargerProviderConnection PluginConnection() => _plugin!.Services.GetRequiredService<ChargerProviderConnection>();

    private sealed class PairedCharger : PluginCharger
    {
        public override ChargerCapabilities Capabilities { get; } = new(6, 16, 1);

        public override Task<ChargerReading> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ChargerReading(DateTimeOffset.UtcNow, ChargerState.Charging, 2300, MaxCurrentAmps: 16));

        public override Task SetCurrentAsync(int amps, CancellationToken cancellationToken) => Task.CompletedTask;

        public override Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class FakeCharger : IChargerSource
    {
        public ChargerCapabilities Capabilities { get; set; } = new(6, 32, 1);

        public ChargerState State { get; set; } = ChargerState.Available;

        public double PowerWatts { get; set; }

        public TimeSpan Age { get; set; }

        public string? RefuseCommands { get; set; }

        public List<string> Commands { get; } = [];

        public Task<ChargerReading> ReadAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new ChargerReading(DateTimeOffset.UtcNow - Age, State, PowerWatts, MaxCurrentAmps: 16));

        public Task SetCurrentAsync(int amps, CancellationToken cancellationToken) => Command($"current={amps}");

        public Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken) => Command($"allowed={allowed}");

        private Task Command(string command)
        {
            if (RefuseCommands is { } reason)
            {
                throw new InvalidOperationException(reason);
            }

            Commands.Add(command);
            return Task.CompletedTask;
        }
    }
}
