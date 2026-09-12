using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
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

    private static DateTimeOffset At(int hour, int minute) => new(2026, 8, 9, hour, minute, 0, TimeSpan.Zero);

    private static (HttpStatusCode, string) Ok(params (DateTimeOffset End, double Kw)[] periods) =>
        (HttpStatusCode.OK, JsonSerializer.Serialize(new
        {
            forecasts = periods.Select(p => new { pv_estimate = p.Kw, period_end = p.End, period = "PT30M" }),
        }));

    private static SolcastForecastService NewService(HttpMessageHandler handler, TimeProvider clock)
    {
        var options = new SolcastOptions { ApiKey = "key", ResourceId = "site" };
        return new SolcastForecastService(
            new SingleClientFactory(handler, options.BaseUrl),
            Options.Create(options),
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

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++;
            var (status, body) = _responses.Dequeue();

            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body, Encoding.UTF8, "application/json"),
            });
        }
    }
}
