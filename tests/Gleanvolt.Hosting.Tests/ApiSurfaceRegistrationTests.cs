using Gleanvolt.Api;
using Gleanvolt.Web;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
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
        ("Pv:Inverter:Host", "127.0.0.1"),
        ("Pv:Chargers:0:Host", "127.0.0.1"),
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
            ("Ev:Vehicles:0:BatteryCapacityKWh", "77"),
            ("ChargeControl:Targeted:MaxHorizon", "20:00:00"));

        var limits = provider.GetRequiredService<TargetedChargeRequestLimits>();

        Assert.Equal(77, limits.BatteryCapacityKWh);
        Assert.True(limits.CanTargetSoc);
        Assert.Equal(TimeSpan.FromHours(20), limits.MaxHorizon);
    }

    [Fact]
    public async Task TheCarNarrowsTheBandTheControllersAreBuiltOn()
    {
        // The whole point of #124: a single-phase car behind a three-phase wallbox, and a car that will
        // not start below 8A. Asserted on the composed band rather than on any one controller, because
        // this is the value they are all built from.
        await using var provider = Build(
            ("ChargeControl:Phases", "3"),
            ("ChargeControl:MinChargingCurrentAmps", "6"),
            ("ChargeControl:MaxChargingCurrentAmps", "16"),
            ("Ev:Vehicles:0:Id", "id4"),
            ("Ev:Vehicles:0:Phases", "1"),
            ("Ev:Vehicles:0:MinChargingCurrentAmps", "8"),
            ("Ev:Vehicles:0:MaxChargingCurrentAmps", "32"));

        var limits = provider.GetRequiredService<ChargingLimits>();

        Assert.Equal(8, limits.MinAmps);    // the car refuses first
        Assert.Equal(16, limits.MaxAmps);   // the installation gives out first
        Assert.Equal(1, limits.Phases);     // the car can only use one

        // And the converter every power figure runs through is built on those phases, not the wallbox's.
        Assert.Equal(230, provider.GetRequiredService<ChargePowerConverter>().AmpsToWatts(1));
    }

    [Fact]
    public async Task AnUndescribedCarLeavesEverythingExactlyAsItWas()
    {
        await using var provider = Build(
            ("ChargeControl:Phases", "3"),
            ("ChargeControl:MinChargingCurrentAmps", "6"),
            ("ChargeControl:MaxChargingCurrentAmps", "16"));

        var limits = provider.GetRequiredService<ChargingLimits>();

        Assert.Equal(6, limits.MinAmps);
        Assert.Equal(16, limits.MaxAmps);
        Assert.Equal(3, limits.Phases);
        Assert.False(provider.GetRequiredService<EvInfo>().IsConfigured);
    }

    [Fact]
    public void ACarsFactsLeftInTheOldSectionAreRefused()
    {
        // Silently ignoring it would leave a capacity the operator's file says is set and the
        // controller has stopped reading -- which makes every SOC-based target quietly wrong.
        var error = Assert.Throws<InvalidOperationException>(
            () => Build(("Vehicle:BatteryCapacityKWh", "77")));

        Assert.Contains("Vehicle:BatteryCapacityKWh", error.Message);
        Assert.Contains("Ev:Vehicles:0:BatteryCapacityKWh", error.Message);
    }

    // -- What the configuration page is handed (#142).

    [Fact]
    public async Task TheSectionIsRegisteredEvenWithTheApiOff()
    {
        // "The API is off" is what the section most often has to say, so it cannot be registered behind
        // the API being on -- unlike ApiHostInfo above, which only an enabled API needs.
        await using var provider = Build();

        var api = provider.GetRequiredService<ApiDisplayOptions>();

        Assert.False(api.Enabled);
        Assert.Empty(api.Keys);
    }

    [Fact]
    public async Task NothingIsRegisteredForAPageThatWillNotBeServed()
    {
        await using var provider = Build(("Web:Enabled", "false"), ("Api:Enabled", "true"), ("Api:Keys:mcp", "a-key"));

        Assert.Null(provider.GetService<ApiDisplayOptions>());
    }

    [Fact]
    public async Task ThePathsComeFromTheRoutesRatherThanFromLiterals()
    {
        await using var provider = Build(("Api:Enabled", "true"), ("Api:Keys:mcp", "a-key"));

        var api = provider.GetRequiredService<ApiDisplayOptions>();

        Assert.Equal(GleanvoltApi.BasePath, api.BasePath);
        Assert.Equal(GleanvoltApi.DocumentPath, api.DocumentPath);

        // Web:Port, for the case where the address the page arrived on carries no port of its own.
        Assert.Equal(provider.GetRequiredService<IOptions<WebOptions>>().Value.Port, api.Port);
    }

    [Fact]
    public async Task TheKeyIsWithheldFromAnOpenUi()
    {
        // A key is bearer-equivalent to the stop button on the wallbox, and with no password the UI
        // admits anyone on the network. The page cannot disclose what it was never given.
        await using var provider = Build(("Api:Enabled", "true"), ("Api:Keys:mcp", "a-key"));

        var key = Assert.Single(provider.GetRequiredService<ApiDisplayOptions>().Keys);

        Assert.Equal("mcp", key.Name);
        Assert.Null(key.Secret);
    }

    [Fact]
    public async Task TheKeyIsHandedOverOnceALoginIsEnforced()
    {
        await using var provider = Build(
            ("Api:Enabled", "true"), ("Api:Keys:mcp", "a-key"), ("Web:PasswordHash", "a-hash"));

        Assert.Equal("a-key", Assert.Single(provider.GetRequiredService<ApiDisplayOptions>().Keys).Secret);
    }

    [Fact]
    public async Task SeveralClientsAreListedByNameInAStableOrder()
    {
        // Several keys is the configuration that makes the name worth showing at all -- it is what the
        // log attributes an action to -- and the table must not reshuffle itself between restarts.
        await using var provider = Build(
            ("Api:Enabled", "true"),
            ("Api:Keys:dashboard", "b-key"),
            ("Api:Keys:claude-mcp", "a-key"),
            ("Api:Keys:unused", ""));

        var keys = provider.GetRequiredService<ApiDisplayOptions>().Keys;

        // The empty one is not a key: HasKeys ignores it, so the page must not list it as one.
        Assert.Equal(["claude-mcp", "dashboard"], keys.Select(key => key.Name));
    }
}
