using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Monitoring;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The composition root's own wiring for the energy monitor. Resolving the store is safe here because
/// constructing it touches no file — only <c>InitializeAsync</c> does, and the worker is the only
/// caller of that.
/// </summary>
public class EnergyMonitorRegistrationTests
{
    // Enough configuration for the whole graph to resolve, so enumerating the hosted services here
    // exercises the real composition root rather than a subset of it. The device addresses point at
    // the loopback and are never dialled: constructing a Modbus client opens no socket, and no hosted
    // service is started.
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
    public async Task TheStoreAndItsWorkerAreRegistered()
    {
        await using var provider = Build();

        Assert.NotNull(provider.GetService<IEnergyIntervalStore>());
        Assert.Single(provider.GetServices<IHostedService>().OfType<EnergyMonitorWorker>());
    }

    [Fact]
    public async Task ItRecordsAtAQuarterHourIntoItsOwnFileUnlessConfiguredOtherwise()
    {
        await using var provider = Build();

        var options = provider.GetRequiredService<IOptions<EnergyMonitorOptions>>().Value;

        Assert.True(options.Enabled);
        Assert.Equal(TimeSpan.FromMinutes(15), options.Interval);

        // Its own file, not the session store's -- see SqliteEnergyIntervalStore for why.
        Assert.NotEqual(
            provider.GetRequiredService<IOptions<SessionStoreOptions>>().Value.Path,
            options.Path);
    }

    [Fact]
    public async Task TheSectionBinds()
    {
        await using var provider = Build(
            ("EnergyMonitor:Enabled", "false"),
            ("EnergyMonitor:Path", "data/custom.db"),
            ("EnergyMonitor:Interval", "00:05:00"),
            ("EnergyMonitor:MaxGap", "00:02:00"),
            ("EnergyMonitor:RetentionDays", "90"));

        var options = provider.GetRequiredService<IOptions<EnergyMonitorOptions>>().Value;

        Assert.False(options.Enabled);
        Assert.Equal("data/custom.db", options.Path);
        Assert.Equal(TimeSpan.FromMinutes(5), options.Interval);
        Assert.Equal(TimeSpan.FromMinutes(2), options.MaxGap);
        Assert.Equal(90, options.RetentionDays);
    }
}
