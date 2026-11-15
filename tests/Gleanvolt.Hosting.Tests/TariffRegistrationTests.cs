using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Tariff;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The tariff through the composition root the controller boots on (issue #265): the real schedule
/// wins over the web UI's null object, every built-in strategy is offered in order, and the file is
/// where <c>Tariff:Path</c> says.
/// </summary>
public sealed class TariffRegistrationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-tariff-reg-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private ServiceProvider Start()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
        [
            .. UsableSite.Pairs,
            new("Tariff:Path", Path.Combine(_root, "tariff.json")),
            new("Tariff:Currency", "eur"),
            new("ChargeControl:SolarGrid:LowTariffOnly", "true"),
        ]);
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services.BuildServiceProvider();
    }

    [Fact]
    public void TheRealScheduleIsTheOneResolved()
    {
        using var services = Start();

        Assert.IsType<TariffSchedule>(services.GetRequiredService<ITariffSchedule>());
    }

    [Fact]
    public void EveryBuiltInStrategyIsOfferedNoneFirst()
    {
        using var services = Start();

        Assert.Equal(
            [TariffProviderIds.None, TariffProviderIds.Table, TariffProviderIds.External],
            services.GetRequiredService<ITariffEditor>().Providers.Select(provider => provider.Id));
    }

    [Fact]
    public void TheLowTariffFlagsAreSeededFromTheirModesSections()
    {
        using var services = Start();
        var flags = services.GetRequiredService<ILowTariffSettings>();

        Assert.True(flags.IsLowTariffOnly(Gleanvolt.Core.Enums.ChargeControlMode.SolarGrid));
        Assert.False(flags.IsLowTariffOnly(Gleanvolt.Core.Enums.ChargeControlMode.FastNoBattery));
        Assert.False(flags.IsLowTariffOnly(Gleanvolt.Core.Enums.ChargeControlMode.Targeted));
    }

    [Fact]
    public void OnlyFastAndSolarGridHaveTheOption()
    {
        using var services = Start();
        var flags = services.GetRequiredService<ILowTariffSettings>();

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            flags.SetLowTariffOnly(Gleanvolt.Core.Enums.ChargeControlMode.Targeted, true, "test"));
    }

    [Fact]
    public void TheFileAndCurrencyAreTheConfiguredOnes()
    {
        using var services = Start();
        var file = services.GetRequiredService<TariffSettingsFile>();

        Assert.Equal(Path.Combine(_root, "tariff.json"), file.Path);
        Assert.Equal("EUR", file.Currency);
    }

    [Fact]
    public void TheTariffIsAnExternalProviderKind()
    {
        using var services = Start();

        var tariff = services.GetRequiredService<IExternalProviders>().Find("tariff");

        Assert.NotNull(tariff);
        Assert.Equal("/providers/tariff", tariff.HubPath);
        Assert.Equal(ExternalProviderState.Off, tariff.State);
    }

    /// <summary>#401: the external windows are kept beside tariff.json, and a restart answers from them.</summary>
    [Fact]
    public void TheExternalWindowsAreKeptBesideTheTariffFileAcrossARestart()
    {
        var now = DateTimeOffset.UtcNow;
        var start = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero);
        IReadOnlyList<TariffWindow> windows =
        [
            new(new TimeWindow(start, start.AddHours(2)), TariffRate.Low),
            new(new TimeWindow(start.AddHours(2), start.AddHours(8)), TariffRate.High),
        ];

        using (var first = Start())
        {
            first.GetRequiredService<Gleanvolt.Core.Strategies.ExternalTariffProvider>().Publish(windows, new TariffOrigin("Spot prices", now));
        }

        Assert.True(File.Exists(Path.Combine(_root, "external-tariff.json")));

        using var restarted = Start();
        var tariff = restarted.GetRequiredService<Gleanvolt.Core.Strategies.ExternalTariffProvider>();
        Assert.Equal(windows, tariff.Windows);
        Assert.Equal("Spot prices", tariff.Origin!.Name);
    }
}
