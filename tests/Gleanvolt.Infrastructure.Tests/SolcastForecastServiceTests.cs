using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.Solcast;

namespace Gleanvolt.Infrastructure.Tests;

/// <summary>
/// The day totals the dashboard reads, against a stubbed transport. They are worked out once per refresh
/// rather than per read, so what has to hold is that the number taken then is the one a sum over the
/// day's periods would give — and that it survives what a refresh can do to the periods behind it.
/// </summary>
public sealed class SolcastForecastServiceTests
{
    private static readonly TimeZoneInfo Prague = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");
    private static readonly DateOnly Today = new(2026, 8, 9);

    [Fact]
    public void NothingIsHeldBeforeTheFirstRefresh()
    {
        var service = NewService(new QueueHandler(), new Clock(At(6, 0)));

        Assert.Null(service.GetDayEnergyWattHours(Today));
    }

    [Fact]
    public async Task TheDayTotalKeepsTheMorningALaterRefreshNoLongerReports()
    {
        // Solcast answers only what is still to come, so the 14:00 fetch has nothing to say about 08:00.
        var clock = new Clock(At(5, 30));
        var handler = new QueueHandler(
            Ok((At(6, 0), 1.0), (At(11, 0), 4.0)),
            Ok((At(13, 0), 3.0)));
        var service = NewService(handler, clock);

        await service.RefreshAsync();
        clock.Now = At(12, 0);
        await service.RefreshAsync();

        Assert.Equal((1000 + 4000 + 3000) * 0.5, service.GetDayEnergyWattHours(Today)!.Value, 3);
        Assert.Equal(service.GetDayForecast(Today)!.ExpectedEnergyWattHours, service.GetDayEnergyWattHours(Today)!.Value, 3);
    }

    [Fact]
    public async Task TomorrowIsAlreadyTotalledSoMidnightNeedsNoRefresh()
    {
        // The evening fetch is the last for hours: the worker sleeps through the dark. After midnight the
        // dashboard asks for the new day, and the answer has to be there already.
        var clock = new Clock(At(17, 0));
        var handler = new QueueHandler(Ok((At(17, 30), 0.4), (At(10, 0).AddDays(1), 5.0)));
        var service = NewService(handler, clock);

        await service.RefreshAsync();
        clock.Now = At(23, 0);

        Assert.Equal(2500, service.GetDayEnergyWattHours(Today.AddDays(1))!.Value, 3);
        Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task AFailedRefreshKeepsTheTotalsItHad()
    {
        var clock = new Clock(At(6, 0));
        var handler = new QueueHandler(
            Ok((At(11, 0), 4.0)),
            (HttpStatusCode.TooManyRequests, """{"response_status":{"error_code":"TooManyRequests"}}"""));
        var service = NewService(handler, clock);

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(2, handler.Calls);
        Assert.Equal(2000, service.GetDayEnergyWattHours(Today)!.Value, 3);
    }

    [Fact]
    public async Task TheDaySummaryCarriesSolcastsBandsAndPeak()
    {
        var clock = new Clock(At(5, 30));
        var handler = new QueueHandler((HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            forecasts = new[]
            {
                new { pv_estimate = 2.0, pv_estimate10 = 1.0, pv_estimate90 = 3.0, period_end = At(10, 0), period = "PT30M" },
                new { pv_estimate = 5.0, pv_estimate10 = 4.0, pv_estimate90 = 5.5, period_end = At(11, 0), period = "PT30M" },
            },
        })));
        var service = NewService(handler, clock);

        await service.RefreshAsync();

        var day = service.GetDaySummary(Today)!;
        Assert.Equal((2000 + 5000) * 0.5, day.ExpectedWh, 3);
        Assert.Equal((1000 + 4000) * 0.5, day.LowWh, 3);
        Assert.Equal((3000 + 5500) * 0.5, day.HighWh, 3);
        Assert.Equal(5000, day.PeakWatts, 3);
        Assert.Equal(day.ExpectedWh, service.GetDayEnergyWattHours(Today)!.Value, 3);
    }

    [Fact]
    public async Task TheKeyIsTakenFromTheAccountOnEveryRequest()
    {
        // Saved on Settings between two refreshes: the second has to use it, with no restart between.
        var handler = new QueueHandler(Ok((At(11, 0), 4.0)), Ok((At(11, 0), 4.0)));
        var account = Account();
        var service = NewService(handler, new Clock(At(6, 0)), account);

        await service.RefreshAsync();
        account.Save(new SolcastAccount("new-key", "site"));
        await service.RefreshAsync();

        Assert.Equal(["key", "new-key"], handler.Keys);
    }

    [Fact]
    public async Task WithNoAccountNothingIsAsked()
    {
        var handler = new QueueHandler();
        var service = NewService(handler, new Clock(At(6, 0)), Account(new SolcastOptions()));

        await service.RefreshAsync();

        Assert.Equal(0, handler.Calls);
        Assert.Equal(SolcastFetchStatus.Never, service.Status);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, SolcastFetchFailure.KeyRefused)]
    [InlineData(HttpStatusCode.Forbidden, SolcastFetchFailure.KeyRefused)]
    [InlineData(HttpStatusCode.NotFound, SolcastFetchFailure.SiteNotFound)]
    public async Task ARefusedAccountIsNotAskedAgainUntilItChanges(HttpStatusCode refusal, SolcastFetchFailure failure)
    {
        // Every retry of a refused key is a call out of ten that nothing can come of.
        var handler = new QueueHandler((refusal, "{}"), Ok((At(11, 0), 4.0)));
        var account = Account();
        var service = NewService(handler, new Clock(At(6, 0)), account);

        await service.RefreshAsync();
        await service.RefreshAsync();

        Assert.Equal(1, handler.Calls);
        Assert.Equal(failure, service.Status.Failure);
        Assert.Equal((int)refusal, service.Status.HttpStatus);

        account.Save(new SolcastAccount("new-key", "site"));
        await service.RefreshAsync();

        Assert.Equal(2, handler.Calls);
        Assert.Equal(SolcastFetchFailure.None, service.Status.Failure);
        Assert.Equal(At(6, 0), service.Status.FetchedAt);
    }

    [Fact]
    public async Task AnExhaustedQuotaIsTriedAgainOnTheNextRefresh()
    {
        var handler = new QueueHandler((HttpStatusCode.TooManyRequests, "{}"), Ok((At(11, 0), 4.0)));
        var service = NewService(handler, new Clock(At(6, 0)));

        await service.RefreshAsync();
        Assert.Equal(SolcastFetchFailure.QuotaExhausted, service.Status.Failure);

        await service.RefreshAsync();

        Assert.Equal(2, handler.Calls);
        Assert.Equal(SolcastFetchFailure.None, service.Status.Failure);
    }

    [Fact]
    public async Task AFailureKeepsTheTimeOfTheForecastStillHeld()
    {
        var clock = new Clock(At(6, 0));
        var handler = new QueueHandler(Ok((At(11, 0), 4.0)), (HttpStatusCode.InternalServerError, "{}"));
        var service = NewService(handler, clock);

        await service.RefreshAsync();
        clock.Now = At(9, 0);
        await service.RefreshAsync();

        Assert.Equal(At(6, 0), service.Status.FetchedAt);
        Assert.Equal(At(9, 0), service.Status.FailedAt);
        Assert.Equal(SolcastFetchFailure.Other, service.Status.Failure);
    }

    [Fact]
    public async Task ForgettingDropsTheForecastButNotTheHistory()
    {
        var handler = new QueueHandler(Ok((At(11, 0), 4.0)));
        var service = NewService(handler, new Clock(At(6, 0)));
        var notified = 0;
        service.StatusChanged += () => notified++;

        await service.RefreshAsync();
        Assert.NotNull(service.ExpectedPowerWattsNow(At(10, 45)));
        service.Forget();

        Assert.Null(service.ExpectedPowerWattsNow(At(10, 45)));
        Assert.Equal(SolcastFetchStatus.Never, service.Status);
        Assert.NotNull(service.GetDayForecast(Today));
        Assert.Equal(2, notified);
    }

    private static SolcastAccountStore Account(SolcastOptions? options = null) =>
        new(new MemorySecretStore(), Options.Create(options ?? new SolcastOptions { ApiKey = "key", ResourceId = "site" }));

    private static DateTimeOffset At(int hour, int minute) => new(2026, 8, 9, hour, minute, 0, TimeSpan.Zero);

    private static (HttpStatusCode, string) Ok(params (DateTimeOffset End, double Kw)[] periods) =>
        (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            forecasts = periods.Select(p => new { pv_estimate = p.Kw, period_end = p.End, period = "PT30M" }),
        }));

    private static SolcastForecastService NewService(
        HttpMessageHandler handler, TimeProvider clock, SolcastAccountStore? account = null)
    {
        return new SolcastForecastService(
            new SingleClientFactory(handler, new SolcastOptions().BaseUrl),
            account ?? Account(),
            NullLogger<SolcastForecastService>.Instance,
            clock);
    }

    private sealed class Clock(DateTimeOffset now) : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = now;

        public override DateTimeOffset GetUtcNow() => Now;

        public override TimeZoneInfo LocalTimeZone => Prague;
    }

    private sealed class SingleClientFactory(HttpMessageHandler handler, string baseUrl) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(handler, disposeHandler: false) { BaseAddress = new Uri(baseUrl) };
    }

    /// <summary>Answers each request with the next response in line.</summary>
    private sealed class QueueHandler(params (HttpStatusCode Status, string Body)[] responses) : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses = new(responses);

        public int Calls { get; private set; }

        /// <summary>The bearer token each request carried, in order.</summary>
        public List<string?> Keys { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            Keys.Add(request.Headers.Authorization?.Parameter);
            var (status, body) = _responses.Dequeue();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
