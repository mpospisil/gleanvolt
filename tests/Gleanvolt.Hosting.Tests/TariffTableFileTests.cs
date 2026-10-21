using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Tariff;

namespace Gleanvolt.Hosting.Tests;

/// <summary>
/// The owner's tariff on disk (issue #265): the service refuses what the rules refuse whoever is
/// asking, a saved table survives a restart, a file that cannot be read is no tariff rather than a
/// guess, and the schedule follows a save without one.
/// </summary>
public sealed class TariffTableFileTests : IDisposable
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    private readonly string _root = Path.Combine(Path.GetTempPath(), "gleanvolt-tariff-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }

    private string FilePath => Path.Combine(_root, "data", "tariff.json");

    private TariffTableFile Open() => new(FilePath, "CZK", NullLogger<TariffTableFile>.Instance);

    private static readonly TariffTable Night = new(
        TariffProvider.Table,
        [new("22:00", "24:00"), new("0:00", "06:00")],
        [new("00:00", "08:00")],
        2.10m,
        4.80m);

    [Fact]
    public void NoFileIsNoTariff()
    {
        Assert.Equal(TariffProvider.None, Open().Read().Provider);
    }

    [Fact]
    public void ASavedTableSurvivesARestartNormalized()
    {
        var result = Open().Save(Night);

        Assert.True(result.Saved);
        Assert.Empty(result.Problems);

        var reopened = Open().Read();

        Assert.Equal(TariffProvider.Table, reopened.Provider);
        Assert.Equal([new TariffPeriod("00:00", "06:00"), new TariffPeriod("22:00", "24:00")], reopened.Weekday);
        Assert.Equal([new TariffPeriod("00:00", "08:00")], reopened.Weekend);
        Assert.Equal(2.10m, reopened.LowPricePerKwh);
        Assert.Equal(4.80m, reopened.HighPricePerKwh);
    }

    [Fact]
    public void TheFileReadsTheWayTheReadmeShowsIt()
    {
        Open().Save(Night);

        var json = File.ReadAllText(FilePath);

        Assert.Contains("\"provider\": \"Table\"", json);
        Assert.Contains("\"start\": \"22:00\"", json);
        Assert.Contains("\"end\": \"24:00\"", json);
        Assert.Contains("\"lowPricePerKwh\": 2.10", json);
    }

    [Theory]
    [InlineData("22:00", "06:00")]
    [InlineData("06:00", "25:00")]
    public void AnInvalidTableWritesNothing(string start, string end)
    {
        var file = Open();

        var result = file.Save(Night with { Weekday = [new(start, end)] });

        Assert.False(result.Saved);
        Assert.Single(result.Errors);
        Assert.False(File.Exists(FilePath));
        Assert.Equal(TariffProvider.None, file.Read().Provider);
    }

    [Fact]
    public void AnOverlapIsRefusedByTheServiceToo()
    {
        var result = Open().Save(Night with { Weekday = [new("05:00", "07:00"), new("06:00", "08:00")] });

        Assert.False(result.Saved);
        Assert.Equal(2, result.Errors.Count());
    }

    [Fact]
    public void ANegativePriceIsRefusedByTheServiceToo()
    {
        Assert.False(Open().Save(Night with { HighPricePerKwh = -1m }).Saved);
    }

    [Fact]
    public void WarningsAreSavedThrough()
    {
        var result = Open().Save(Night with { Weekday = [], Weekend = [], LowPricePerKwh = 9m });

        Assert.True(result.Saved);
        Assert.Equal(2, result.Warnings.Count());
        Assert.Equal(9m, Open().Read().LowPricePerKwh);
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("null")]
    [InlineData("""{ "provider": "Table", "weekday": [ { "start": "22:00", "end": "06:00" } ] }""")]
    [InlineData("""{ "provider": "Sometimes" }""")]
    public void AnUnreadableFileIsNoTariff(string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, content);

        Assert.Equal(TariffTable.None, Open().Read());
    }

    [Fact]
    public void AFileWithOnlyAProviderIsAnEmptyTable()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.WriteAllText(FilePath, """{ "provider": "Table" }""");

        var table = Open().Read();

        Assert.Equal(TariffProvider.Table, table.Provider);
        Assert.Empty(table.Weekday);
        Assert.Null(table.LowPricePerKwh);
    }

    [Fact]
    public void TheScheduleFollowsASaveWithoutARestart()
    {
        var file = Open();

        // 23:00 in Prague; FakeTimeProvider takes its start in UTC.
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 21, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(Prague);
        var schedule = new TariffSchedule(file, time);

        Assert.Null(schedule.Timeline);

        file.Save(Night);

        var timeline = schedule.Timeline!;
        Assert.Equal(TariffRate.Low, timeline.RateAt(time.GetUtcNow()));
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 6, 0, 0, TimeSpan.FromHours(2)), timeline.NextChangeAfter(time.GetUtcNow()));
        Assert.Equal(new TariffPrices(2.10m, 4.80m, "CZK"), timeline.Prices);

        file.Save(Night with { Provider = TariffProvider.None });

        Assert.Null(schedule.Timeline);
    }

    [Fact]
    public void TheScheduleMovesOnAtMidnight()
    {
        var file = Open();
        file.Save(Night);
        var time = new FakeTimeProvider(new DateTimeOffset(2026, 10, 21, 10, 0, 0, TimeSpan.Zero));
        time.SetLocalTimeZone(Prague);
        var schedule = new TariffSchedule(file, time);

        var wednesday = schedule.Timeline!;
        time.Advance(TimeSpan.FromDays(1));
        var thursday = schedule.Timeline!;

        Assert.Equal(wednesday.Windows[0].Window.Start.AddDays(1), thursday.Windows[0].Window.Start);
    }

    [Fact]
    public void TheDefaultsAreTheReadmes()
    {
        var options = new TariffOptions();

        Assert.Equal("data/tariff.json", options.Path);
        Assert.Equal("CZK", options.Currency);
    }
}
