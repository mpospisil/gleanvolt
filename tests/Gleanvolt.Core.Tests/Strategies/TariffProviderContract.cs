using System.Text.Json;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// What every tariff strategy promises (issue #265), as a test class each strategy's own tests inherit,
/// so a new one is held to the same contract by writing three members. The timeline, when there is
/// one, is ordered, half-open, without gaps, and never has two neighbours on the same rate; and nothing
/// a strategy can be handed as settings makes it throw.
/// </summary>
public abstract class TariffProviderContract
{
    protected static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    protected static readonly TariffPrices Prices = new(2.10m, 4.80m, "CZK");

    /// <summary>The strategy under test.</summary>
    protected abstract ITariffProvider Provider { get; }

    /// <summary>Settings it can expand.</summary>
    protected abstract JsonElement UsableSettings { get; }

    /// <summary>Whatever has to happen before <paramref name="date"/> can be expanded: a fetch, for a strategy that fetches.</summary>
    protected virtual Task PrepareAsync(DateOnly date) => Task.CompletedTask;

    /// <summary>
    /// The days worth expanding: an ordinary weekday, a Friday into the weekend, a Sunday into the
    /// week, and both nights the clocks change.
    /// </summary>
    public static TheoryData<DateOnly> Days() =>
    [
        new DateOnly(2026, 10, 21),
        new DateOnly(2026, 10, 23),
        new DateOnly(2026, 11, 1),
        new DateOnly(2026, 3, 29),
        new DateOnly(2026, 10, 25),
    ];

    // What a strategy can be handed by a hand-edited file or a careless API client.
    private static IEnumerable<JsonElement> Unusable() =>
        new[] { "42", "\"text\"", "[]", "true", """{ "weekday": 5 }""", """{ "weekday": [ { "start": 6 } ] }""", "{}" }
            .Select(json => JsonDocument.Parse(json).RootElement.Clone());

    protected TariffContext Context(DateOnly date, JsonElement settings) =>
        new(new DateTimeOffset(date.ToDateTime(new TimeOnly(12, 0)), Prague.GetUtcOffset(date.ToDateTime(new TimeOnly(12, 0)))),
            date, Prague, Prices, settings);

    [Fact]
    public void ItHasAnIdAndAName()
    {
        Assert.False(string.IsNullOrWhiteSpace(Provider.Id));
        Assert.False(string.IsNullOrWhiteSpace(Provider.Name));
    }

    [Fact]
    public void UsableSettingsPassItsOwnCheck()
    {
        Assert.DoesNotContain(Provider.Check(UsableSettings), problem => !problem.IsWarning);
    }

    [Fact]
    public void NoSettingsYetAreItsDefaultsNotAnError()
    {
        Assert.DoesNotContain(Provider.Check(default), problem => !problem.IsWarning);
        _ = Provider.Expand(Context(new DateOnly(2026, 10, 21), default));
    }

    [Fact]
    public void UnusableSettingsAreProblemsNotExceptions()
    {
        foreach (var settings in Unusable())
        {
            _ = Provider.Check(settings);
            _ = Provider.Normalize(settings);
            _ = Provider.Expand(Context(new DateOnly(2026, 10, 21), settings));
        }
    }

    [Fact]
    public void NormalizedSettingsAreStillUsable()
    {
        Assert.DoesNotContain(Provider.Check(Provider.Normalize(UsableSettings)), problem => !problem.IsWarning);
    }

    [Theory]
    [MemberData(nameof(Days))]
    public async Task TheTimelineTilesWithoutAGapAndNeverRepeatsARate(DateOnly date)
    {
        await PrepareAsync(date);

        if (Provider.Expand(Context(date, UsableSettings)) is not { } timeline)
        {
            return;
        }

        Assert.NotEmpty(timeline.Windows);

        for (var i = 0; i < timeline.Windows.Count; i++)
        {
            Assert.True(timeline.Windows[i].Window.End > timeline.Windows[i].Window.Start, $"window {i} is empty");

            if (i > 0)
            {
                Assert.Equal(timeline.Windows[i - 1].Window.End, timeline.Windows[i].Window.Start);
                Assert.NotEqual(timeline.Windows[i - 1].Rate, timeline.Windows[i].Rate);
            }
        }
    }
}
