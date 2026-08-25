using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Infrastructure;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// How the composition root wires the installation (issue #111). Resolving a Modbus client here is
/// safe: constructing one opens no socket, and nothing started is a hosted service.
/// </summary>
public class PvSystemRegistrationTests
{
    private static ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            settings.Select(s => new KeyValuePair<string, string?>(s.Key, s.Value)));
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services.BuildServiceProvider();
    }

    private static ServiceProvider BuildWithPvDevices(params (string Key, string? Value)[] settings) =>
        Build([("Pv:Inverter:Host", "127.0.0.1"), ("Pv:Chargers:0:Host", "127.0.0.2"), .. settings]);

    [Fact]
    public async Task TheResolvedSiteIsInjectable()
    {
        await using var provider = BuildWithPvDevices(("Pv:Id", "home-roof"), ("Pv:Name", "Home Roof"));

        var site = provider.GetRequiredService<PvSystemInfo>();

        Assert.Equal("home-roof", site.Id);
        Assert.Equal("Home Roof", site.Name);
    }

    [Fact]
    public async Task TheChargerIsRegisteredUnderItsOwnIdAndTheFixedKeyIsTheSameClient()
    {
        // Two registrations, one client. Two would be two sockets to the same wallbox, which is the
        // desynchronised-stream failure a single client exists to prevent -- and [FromKeyedServices]
        // takes a compile-time constant, so the fixed key cannot simply be dropped.
        await using var provider = BuildWithPvDevices(("Pv:Chargers:0:Id", "wallbox"));

        var byId = provider.GetRequiredKeyedService<IModbusClient>("wallbox");
        var byFixedKey = provider.GetRequiredKeyedService<IModbusClient>(ModbusClientKeys.EvCharger);

        Assert.Same(byId, byFixedKey);
    }

    [Fact]
    public void NoChargerCanEverClaimTheFixedKey()
    {
        // What keeps the alias above an alias rather than a service that resolves itself: charger ids
        // are slugs, and the fixed key is not one, so the two keyspaces cannot meet.
        var error = Assert.Throws<InvalidOperationException>(
            () => Build(("Pv:Inverter:Host", "127.0.0.1"), ("Pv:Chargers:0:Id", ModbusClientKeys.EvCharger), ("Pv:Chargers:0:Host", "127.0.0.2")));

        Assert.Contains("must be a slug", error.Message);
    }

    [Fact]
    public void AKeyThatHasMovedStopsTheHostBeforeAnythingIsBound()
    {
        // The check runs first in AddGleanvolt, ahead of the section it replaces, so an operator who
        // upgraded without editing .env is told what to change rather than being pointed at a default.
        var error = Assert.Throws<InvalidOperationException>(() => Build(
            ("Pv:Inverter:Host", "127.0.0.1"),
            ("Pv:Chargers:0:Host", "127.0.0.2"),
            ("Solax:Inverter:Host", "127.0.0.9")));

        Assert.Contains("Solax__Inverter", error.Message);
        Assert.Contains("Pv__Inverter", error.Message);
    }

    [Fact]
    public void AHostWithNoDevicesConfiguredDoesNotStart()
    {
        // Registration time, not first-poll time: an address nobody set is a fact about the
        // configuration, and a configuration error should be visible before anything else is.
        var error = Assert.Throws<InvalidOperationException>(() => Build(("Pv:Name", "Nowhere")));

        Assert.Contains("Pv:Inverter:Host", error.Message);
    }
}
