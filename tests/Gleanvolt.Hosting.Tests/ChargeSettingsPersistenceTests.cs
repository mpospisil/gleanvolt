using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Forecasting;
using Gleanvolt.Hosting.Resume;
using Gleanvolt.Hosting.SolarGrid;
using Gleanvolt.Hosting.Tariff;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The runtime numbers that tune the modes outlive a restart (#421): minimum battery SOC, the SOC resume
/// margin, the minimum solar surplus and low tariff only. A value never changed keeps following its
/// configuration; a file that cannot be read falls back to configuration and says so.
/// </summary>
public sealed class ChargeSettingsPersistenceTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"charge-settings-{Guid.NewGuid():N}");

    private string SettingsPath => Path.Combine(_directory, "charge-settings.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public async Task EveryValueSetBeforeARestart_IsBackAfterIt()
    {
        await using (var before = Build())
        {
            var forecast = before.GetRequiredService<IForecastRuntimeSettings>();
            forecast.SetMinBatterySocFloorPercent(40, "test");
            forecast.SetFloorResumeMarginPercent(12, "test");
            before.GetRequiredService<ISolarGridSettings>().SetMinSurplusWatts(1_500, "test");
            var lowTariff = before.GetRequiredService<ILowTariffSettings>();
            lowTariff.SetLowTariffOnly(ChargeControlMode.FastNoBattery, true, "test");
            lowTariff.SetLowTariffOnly(ChargeControlMode.SolarGrid, true, "test");
        }

        Assert.True(File.Exists(SettingsPath));

        await using var after = Build();

        var restored = after.GetRequiredService<IForecastRuntimeSettings>();
        Assert.Equal(40, restored.MinBatterySocFloorPercent);
        Assert.Equal(12, restored.FloorResumeMarginPercent);
        Assert.Equal(1_500, after.GetRequiredService<ISolarGridSettings>().MinSurplusWatts);
        var restoredTariff = after.GetRequiredService<ILowTariffSettings>();
        Assert.True(restoredTariff.IsLowTariffOnly(ChargeControlMode.FastNoBattery));
        Assert.True(restoredTariff.IsLowTariffOnly(ChargeControlMode.SolarGrid));
    }

    [Fact]
    public async Task AFlagTurnedOffOverAConfiguredOn_StaysOff()
    {
        await using (var before = Build(("ChargeControl:Fast:LowTariffOnly", "true")))
        {
            before.GetRequiredService<ILowTariffSettings>()
                .SetLowTariffOnly(ChargeControlMode.FastNoBattery, false, "test");
        }

        await using var after = Build(("ChargeControl:Fast:LowTariffOnly", "true"));

        Assert.False(after.GetRequiredService<ILowTariffSettings>().IsLowTariffOnly(ChargeControlMode.FastNoBattery));
    }

    [Fact]
    public async Task AValueNobodyChanged_FollowsTheConfiguration()
    {
        await using (var before = Build())
        {
            before.GetRequiredService<IForecastRuntimeSettings>().SetMinBatterySocFloorPercent(40, "test");
        }

        // The config moves between the two runs: the changed value keeps what was set, the rest follow.
        await using var after = Build(
            ("ChargeControl:Forecast:MinBatterySocFloorPercent", "30"),
            ("ChargeControl:Forecast:FloorResumeMarginPercent", "8"),
            ("ChargeControl:SolarGrid:MinSurplusWatts", "2500"),
            ("ChargeControl:SolarGrid:LowTariffOnly", "true"));

        var forecast = after.GetRequiredService<IForecastRuntimeSettings>();
        Assert.Equal(40, forecast.MinBatterySocFloorPercent);
        Assert.Equal(8, forecast.FloorResumeMarginPercent);
        Assert.Equal(2_500, after.GetRequiredService<ISolarGridSettings>().MinSurplusWatts);
        Assert.True(after.GetRequiredService<ILowTariffSettings>().IsLowTariffOnly(ChargeControlMode.SolarGrid));
    }

    [Fact]
    public void NoFile_IsTheConfiguration_AndNothingIsWrittenUntilAChange()
    {
        var file = new ChargeSettingsFile(SettingsPath, NullLogger<ChargeSettingsFile>.Instance);
        var settings = new SolarGridSettings(
            Options.Create(new SolarGridChargeOptions { MinSurplusWatts = 1_800 }),
            NullLogger<SolarGridSettings>.Instance,
            file);

        Assert.Equal(1_800, settings.MinSurplusWatts);
        Assert.False(File.Exists(SettingsPath));

        // Setting the value it already has is no change either.
        settings.SetMinSurplusWatts(1_800, "test");
        Assert.False(File.Exists(SettingsPath));
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("null")]
    [InlineData("{\"minBatterySocFloorPercent\": \"forty\"}")]
    public void AnUnreadableFile_FallsBackToTheConfiguration_AndSaysSo(string content)
    {
        Directory.CreateDirectory(_directory);
        File.WriteAllText(SettingsPath, content);
        var log = new CapturingLogger<ChargeSettingsFile>();

        var file = new ChargeSettingsFile(SettingsPath, log);
        var settings = new ForecastRuntimeSettings(
            Options.Create(new ForecastChargeOptions { MinBatterySocFloorPercent = 35, FloorResumeMarginPercent = 6 }),
            NullLogger<ForecastRuntimeSettings>.Instance,
            file);

        Assert.Equal(35, settings.MinBatterySocFloorPercent);
        Assert.Equal(6, settings.FloorResumeMarginPercent);
        Assert.Contains(log.Messages, message => message.Contains("configured values apply", StringComparison.Ordinal));

        // A change after that replaces the bad file with a good one.
        settings.SetFloorResumeMarginPercent(10, "test");
        Assert.Equal(10, new ChargeSettingsFile(SettingsPath, log).Saved.FloorResumeMarginPercent);
    }

    [Fact]
    public void ARestoredValue_IsLoggedWithTheConfiguredOne()
    {
        var first = new ChargeSettingsFile(SettingsPath, NullLogger<ChargeSettingsFile>.Instance);
        new SolarGridSettings(Options.Create(new SolarGridChargeOptions()), NullLogger<SolarGridSettings>.Instance, first)
            .SetMinSurplusWatts(900, "test");

        var log = new CapturingLogger<SolarGridSettings>();
        var restored = new SolarGridSettings(
            Options.Create(new SolarGridChargeOptions { MinSurplusWatts = 2_000 }),
            log,
            new ChargeSettingsFile(SettingsPath, NullLogger<ChargeSettingsFile>.Instance));

        Assert.Equal(900, restored.MinSurplusWatts);
        Assert.Contains(log.Messages, message => message.Contains("900W as last set (configured 2000W)", StringComparison.Ordinal));
    }

    [Fact]
    public void TheFileSitsBesideTheModeByDefault()
    {
        var options = new ChargeControlOptions();

        Assert.Equal(Path.GetDirectoryName(options.ModeStatePath), Path.GetDirectoryName(options.SettingsStatePath));
    }

    private ServiceProvider Build(params (string Key, string? Value)[] settings)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Configuration.AddInMemoryCollection(
            UsableSite.Settings
                .Append((Key: "ChargeControl:SettingsStatePath", Value: (string?)SettingsPath))
                .Concat(settings)
                .GroupBy(s => s.Key)
                .Select(group => new KeyValuePair<string, string?>(group.Key, group.Last().Value)));
        builder.Services.AddGleanvolt(builder.Configuration);

        return builder.Services.BuildServiceProvider();
    }

    private sealed class CapturingLogger<T> : ILogger<T>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            Messages.Add(formatter(state, exception));
    }
}
