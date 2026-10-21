using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Tariff;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The tariff settings on disk (issue #265): the service refuses what the rules refuse whoever is
/// asking, saved settings survive a restart, a section per strategy is kept whichever is selected —
/// even one this build does not have — and a file that cannot be read is no tariff rather than a guess.
/// </summary>
public sealed class TariffSettingsFileTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-tariff-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_root, "data", "tariff.json");

    internal static ITariffProvider[] Providers => [new NoTariffProvider(), new TableTariffProvider()];

    private TariffSettingsFile Open() => new(FilePath, "CZK", Providers, NullLogger<TariffSettingsFile>.Instance);

    private void WriteRaw(string json)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, json);
    }

    internal static readonly TariffTable NightTable = new(
        [new("22:00", "24:00"), new("0:00", "06:00")],
        [new("00:00", "08:00")]);

    internal static TariffSettings Night(TariffTable? table = null) =>
        (TariffSettings.None with { Provider = TariffProviderIds.Table, LowPricePerKwh = 2.10m, HighPricePerKwh = 4.80m })
            .WithSection(TariffProviderIds.Table, TableTariffProvider.Write(table ?? NightTable));

    private static TariffTable TableOf(TariffSettings settings) =>
        TableTariffProvider.Read(settings.SectionFor(TariffProviderIds.Table))!;

    [Fact]
    public void NoFileIsNoTariff()
    {
        Assert.True(Open().Read().Selects(TariffProviderIds.None));
    }

    [Fact]
    public void SavedSettingsSurviveARestartWithTheTableNormalized()
    {
        var result = Open().Save(Night());

        Assert.True(result.Saved);
        Assert.Empty(result.Problems);

        var reopened = Open().Read();

        Assert.True(reopened.Selects(TariffProviderIds.Table));
        Assert.Equal([new TariffPeriod("00:00", "06:00"), new TariffPeriod("22:00", "24:00")], TableOf(reopened).Weekday);
        Assert.Equal([new TariffPeriod("00:00", "08:00")], TableOf(reopened).Weekend);
        Assert.Equal(2.10m, reopened.LowPricePerKwh);
        Assert.Equal(4.80m, reopened.HighPricePerKwh);
    }

    [Fact]
    public void TheFileReadsTheWayTheReadmeShowsIt()
    {
        Open().Save(Night());

        var json = File.ReadAllText(FilePath);

        Assert.Contains("\"provider\": \"Table\"", json);
        Assert.Contains("\"lowPricePerKwh\": 2.10", json);
        Assert.Contains("\"table\": {", json);
        Assert.Contains("\"start\": \"22:00\"", json);
        Assert.Contains("\"end\": \"24:00\"", json);
    }

    [Fact]
    public void SwitchingToNoneAndBackKeepsTheTable()
    {
        var file = Open();
        file.Save(Night());

        Assert.True(file.Save(file.Read() with { Provider = TariffProviderIds.None }).Saved);
        Assert.Equal(2, TableOf(Open().Read()).Weekday.Count);

        Assert.True(file.Save(file.Read() with { Provider = TariffProviderIds.Table }).Saved);
        Assert.True(Open().Read().Selects(TariffProviderIds.Table));
        Assert.Equal(2, TableOf(Open().Read()).Weekday.Count);
    }

    [Fact]
    public void ASectionForAStrategyThisBuildLacksIsReadAndKeptOnSave()
    {
        WriteRaw("""
            {
              "provider": "Table",
              "table": { "weekday": [ { "start": "00:00", "end": "06:00" } ] },
              "egd": { "postcode": "60200", "code": "A1B4DP1" }
            }
            """);
        var file = Open();

        Assert.True(file.Save(file.Read() with { LowPricePerKwh = 1.90m }).Saved);

        using var saved = JsonDocument.Parse(File.ReadAllText(FilePath));
        Assert.Equal("A1B4DP1", saved.RootElement.GetProperty("egd").GetProperty("code").GetString());
    }

    [Fact]
    public void ASelectedStrategyThisBuildLacksStaysSelectedAndCannotBeSavedAsIs()
    {
        WriteRaw("""{ "provider": "Egd", "egd": { "postcode": "60200" } }""");
        var file = Open();

        Assert.Equal("Egd", file.Read().Provider);

        var result = file.Save(file.Read());

        Assert.False(result.Saved);
        Assert.Equal(TariffFields.Provider, Assert.Single(result.Errors).Field);
    }

    [Theory]
    [InlineData("22:00", "06:00")]
    [InlineData("06:00", "25:00")]
    public void AnInvalidTableWritesNothing(string start, string end)
    {
        var file = Open();

        var result = file.Save(Night(new TariffTable([new(start, end)], [])));

        Assert.False(result.Saved);
        Assert.Single(result.Errors);
        Assert.False(File.Exists(FilePath));
        Assert.True(file.Read().Selects(TariffProviderIds.None));
    }

    [Fact]
    public void AnOverlapIsRefusedByTheServiceToo()
    {
        var result = Open().Save(Night(new TariffTable([new("05:00", "07:00"), new("06:00", "08:00")], [])));

        Assert.False(result.Saved);
        Assert.Equal(2, result.Errors.Count());
    }

    [Fact]
    public void ANegativePriceIsRefusedByTheServiceToo()
    {
        Assert.False(Open().Save(Night() with { HighPricePerKwh = -1m }).Saved);
    }

    [Fact]
    public void WithNoTariffNothingElseIsChecked()
    {
        Assert.True(Open().Save(Night(new TariffTable([new("nonsense", "")], [])) with
        {
            Provider = TariffProviderIds.None,
            LowPricePerKwh = -1m,
        }).Saved);
    }

    [Fact]
    public void WarningsAreSavedThrough()
    {
        var result = Open().Save(Night(TariffTable.Empty) with { LowPricePerKwh = 9m });

        Assert.True(result.Saved);
        Assert.Equal(2, result.Warnings.Count());
        Assert.Equal(9m, Open().Read().LowPricePerKwh);
    }

    [Fact]
    public void TheProviderIdIsMatchedRegardlessOfCase()
    {
        Assert.True(Open().Save(Night() with { Provider = "table" }).Saved);
        Assert.Equal(TariffProviderIds.Table, Open().Read().Provider);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("""{ "provider": 5 }""")]
    [InlineData("""{ "provider": "Table", "lowPricePerKwh": "cheap" }""")]
    public void AnUnreadableFileIsNoTariff(string content)
    {
        WriteRaw(content);

        Assert.Equal(TariffSettings.None, Open().Read());
    }

    [Fact]
    public void ASelectedTableThatFailsItsCheckIsNoTariffButIsKept()
    {
        WriteRaw("""{ "provider": "Table", "table": { "weekday": [ { "start": "22:00", "end": "06:00" } ] } }""");

        var settings = Open().Read();

        Assert.True(settings.Selects(TariffProviderIds.None));
        Assert.Equal("22:00", TableOf(settings).Weekday[0].Start);
    }

    [Fact]
    public void AFileWithOnlyAProviderIsAnEmptyTable()
    {
        WriteRaw("""{ "provider": "Table" }""");

        var settings = Open().Read();

        Assert.True(settings.Selects(TariffProviderIds.Table));
        Assert.Equal(TariffTable.Empty, TableOf(settings));
        Assert.Null(settings.LowPricePerKwh);
    }

    [Fact]
    public void TheDefaultsAreTheReadmes()
    {
        var options = new TariffOptions();

        Assert.Equal("data/tariff.json", options.Path);
        Assert.Equal("CZK", options.Currency);
    }
}
