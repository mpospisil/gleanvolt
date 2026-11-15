using Gleanvolt.Providers.Client.Tariff;

namespace Gleanvolt.Plugin.TariffSample;

/// <summary>
/// Publishes unprompted a minute after every local midnight, so tomorrow is always in what Gleanvolt
/// holds: what a provider whose data changes on its own schedule does.
/// </summary>
public sealed class MidnightPublisher(TariffProviderConnection connection, TimeProvider time, ILogger<MidnightPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var zone = connection.Welcome is { } site ? TimeZoneInfo.FindSystemTimeZoneById(site.TimeZone) : TimeZoneInfo.Local;
            var now = TimeZoneInfo.ConvertTime(time.GetUtcNow(), zone);
            var next = new DateTimeOffset(now.Date.AddDays(1).AddMinutes(1), zone.GetUtcOffset(now.Date.AddDays(1)));

            await Task.Delay(next - now, time, stoppingToken);

            if (await connection.PublishNowAsync(stoppingToken) is null)
            {
                logger.LogInformation("Not connected at midnight; the next hello publishes instead.");
            }
        }
    }
}
