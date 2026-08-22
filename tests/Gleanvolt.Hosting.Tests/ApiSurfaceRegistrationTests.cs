using Gleanvolt.Api;
using Gleanvolt.Core.Models;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The composition root's own wiring for the HTTP API (#103): that it is off unless asked for, that
/// asking for it without a key stops the host rather than opening the charger to the network, and that
/// the socket now belongs to whichever surface wants one.
/// </summary>
public class ApiSurfaceRegistrationTests
{
    private static readonly (string Key, string? Value)[] MinimalDevices =
    [
        ("Solax:Inverter:Host", "127.0.0.1"),
        ("Solax:EvCharger:Host", "127.0.0.1"),
    ];

    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            MinimalDevices.Concat(settings).Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services.BuildServiceProvider();
    }

    [Fact]
    public async Task TheApiIsOffUnlessItIsAskedFor()
    {
        await using var provider = Build();

        Assert.False(provider.GetRequiredService<IOptions<ApiOptions>>().Value.Enabled);

        // Nothing was registered for it either -- an enabled API is what brings its services in.
        Assert.Null(provider.GetService<ApiHostInfo>());
    }

    [Fact]
    public void EnablingItWithNoKeyStopsTheHost()
    {
        var error = Assert.Throws<InvalidOperationException>(() => Build(("Api:Enabled", "true")));

        Assert.Contains("Api:Keys", error.Message);
    }

    [Fact]
    public async Task EnablingItWithAKeyRegistersWhatTheEndpointsRead()
    {
        await using var provider = Build(("Api:Enabled", "true"), ("Api:Keys:mcp", "a-key"));

        var api = provider.GetRequiredService<IOptions<ApiOptions>>().Value;
        Assert.True(api.Enabled);
        Assert.Equal("a-key", api.Keys["mcp"]);

        Assert.NotNull(provider.GetService<ApiHostInfo>());
        Assert.NotNull(provider.GetService<TargetedChargeRequestLimits>());
    }

    [Fact]
    public async Task TheApiCanBeTheOnlySurfaceListening()
    {
        await using var provider = Build(
            ("Web:Enabled", "false"), ("Api:Enabled", "true"), ("Api:Keys:mcp", "a-key"));

        // With the UI off but the API on there must still be a real server: the "nothing is listening"
        // fallback is for a host with no surface at all.
        Assert.IsNotType<NoListenServer>(provider.GetRequiredService<IServer>());
    }

    [Fact]
    public async Task NothingListensWhenNeitherSurfaceIsWanted()
    {
        await using var provider = Build(("Web:Enabled", "false"));

        Assert.IsType<NoListenServer>(provider.GetRequiredService<IServer>());
    }

    [Fact]
    public async Task TheTargetedLimitsCarryTheConfiguredSiteFigures()
    {
        await using var provider = Build(
            ("Api:Enabled", "true"),
            ("Api:Keys:mcp", "a-key"),
            ("Vehicle:BatteryCapacityKWh", "77"),
            ("ChargeControl:Targeted:MaxHorizon", "20:00:00"));

        var limits = provider.GetRequiredService<TargetedChargeRequestLimits>();

        Assert.Equal(77, limits.BatteryCapacityKWh);
        Assert.True(limits.CanTargetSoc);
        Assert.Equal(TimeSpan.FromHours(20), limits.MaxHorizon);
    }
}
