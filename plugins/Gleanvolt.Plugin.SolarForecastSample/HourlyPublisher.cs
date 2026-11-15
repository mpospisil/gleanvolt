using Gleanvolt.Providers.Client.SolarForecast;

namespace Gleanvolt.Plugin.SolarForecastSample;

/// <summary>
/// Publishes unprompted at the top of every hour, besides answering Gleanvolt's requests: what a
/// provider whose forecast is updated on its own schedule does.
/// </summary>
public sealed class HourlyPublisher(SolarForecastProviderConnection connection, TimeProvider time, ILogger<HourlyPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var now = time.GetUtcNow();
            var next = new DateTimeOffset(now.Year, now.Month, now.Day, now.Hour, 0, 0, TimeSpan.Zero).AddHours(1);

            await Task.Delay(next - now, time, stoppingToken);

            if (await connection.PublishNowAsync(stoppingToken) is null)
            {
                logger.LogInformation("Not connected at the top of the hour; the next hello publishes instead.");
            }
        }
    }
}
