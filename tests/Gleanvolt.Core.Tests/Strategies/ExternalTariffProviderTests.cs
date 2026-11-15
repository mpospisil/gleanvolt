using System.Text.Json;
using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Core.Tests.Strategies;

/// <summary>
/// The External tariff strategy (#401) held to the contract every strategy keeps, with windows a
/// provider could have published: a night tariff and an afternoon one, for the day and the next.
/// </summary>
public class ExternalTariffProviderTests : TariffProviderContract
{
    private readonly ExternalTariffProvider _external = new();

    protected override ITariffProvider Provider => _external;

    protected override JsonElement UsableSettings => default;

    protected override Task PrepareAsync(DateOnly date)
    {
        var now = Context(date, default).Now;
        var (windows, problems) = ExternalTariffRules.Check(Pattern(date), "CZK", "CZK", now);
        Assert.Empty(problems);
        _external.Publish(windows, new TariffOrigin("Test", now));
        return Task.CompletedTask;
    }

    [Fact]
    public void NothingPublishedIsNoTimeline()
    {
        Assert.Null(_external.Expand(Context(new DateOnly(2026, 10, 21), default)));
    }

    [Fact]
    public async Task ATimelineNamesWhereItCameFrom()
    {
        var date = new DateOnly(2026, 10, 21);
        await PrepareAsync(date);

        var timeline = _external.Expand(Context(date, default))!;

        Assert.Equal("Test", timeline.Origin!.Name);
        Assert.Equal(Prices, timeline.Prices);
        Assert.Equal(TariffRate.Low, timeline.RateAt(Context(date, default).Now.AddHours(1.5)));
    }

    [Fact]
    public async Task ItAnswersUntilTheLastWindowEndsAndThenNoLonger()
    {
        var date = new DateOnly(2026, 10, 21);
        await PrepareAsync(date);
        var end = _external.Windows![^1].Window.End;

        var justBefore = Context(date, default) with { Now = end.AddMinutes(-1), Today = date.AddDays(1) };
        var after = Context(date, default) with { Now = end, Today = date.AddDays(2) };

        Assert.NotNull(_external.Expand(justBefore));
        Assert.Null(_external.Expand(after));
    }

    [Fact]
    public async Task ARefreshAsksTheProviderAndDoesNotWait()
    {
        var requests = new CountingRequests();
        var external = new ExternalTariffProvider(requests);

        await external.RefreshAsync(default, CancellationToken.None);

        Assert.Equal(1, requests.Count);
    }

    // Rates alternating at 00:00, 06:00, 13:00, 15:00 and 22:00 local, low first, over two days; in UTC as sent.
    internal static List<TariffWindow> Pattern(DateOnly date)
    {
        var windows = new List<TariffWindow>();
        var rate = TariffRate.Low;
        var edges = new List<DateTimeOffset>();

        foreach (var day in new[] { date, date.AddDays(1) })
        {
            foreach (var hour in new[] { 0, 6, 13, 15, 22 })
            {
                edges.Add(Utc(day, hour));
            }
        }

        edges.Add(Utc(date.AddDays(2), 0));

        for (var i = 0; i < edges.Count - 1; i++)
        {
            windows.Add(new TariffWindow(new TimeWindow(edges[i], edges[i + 1]), rate));
            rate = rate == TariffRate.Low ? TariffRate.High : TariffRate.Low;
        }

        return windows;
    }

    private static DateTimeOffset Utc(DateOnly day, int hour)
    {
        var local = day.ToDateTime(new TimeOnly(hour, 0));
        return new DateTimeOffset(local, Prague.GetUtcOffset(local)).ToUniversalTime();
    }

    private sealed class CountingRequests : IExternalTariffRequests
    {
        public int Count { get; private set; }

        public Task<bool> RequestRefreshAsync(CancellationToken cancellationToken)
        {
            Count++;
            return Task.FromResult(true);
        }
    }
}
