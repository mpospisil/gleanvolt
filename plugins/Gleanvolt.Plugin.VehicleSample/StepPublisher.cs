using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;

namespace Gleanvolt.Plugin.VehicleSample;

/// <summary>
/// Publishes on every step of the pretend car's SOC, besides answering Gleanvolt's requests: what a
/// car provider does when the car reports a change. With <c>--simulate-login-expiry</c>, every
/// <see cref="ExpiryEvery"/>th step reports instead that the login expired, and the next one that it is
/// back, to show Gleanvolt's sign-in band.
/// </summary>
public sealed class StepPublisher(
    VehicleProviderConnection connection,
    SimulatedCar car,
    SampleOptions options,
    TimeProvider time,
    ILogger<StepPublisher> logger)
    : BackgroundService
{
    /// <summary>How often the login "expires" with <c>--simulate-login-expiry</c>, in steps.</summary>
    public const int ExpiryEvery = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var expired = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = car.NextStepAt - time.GetUtcNow();
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, time, stoppingToken);

            if (options.SimulateLoginExpiry && car.Steps % ExpiryEvery == 0)
            {
                expired = true;
                logger.LogInformation("Simulating an expired login for one step.");
                await connection.ReportStatusAsync(
                    new ProviderStatus(false, "The car account's login expired (simulated). It comes back by itself in one step.", NeedsOwner: true),
                    stoppingToken);
                continue;
            }

            if (expired)
            {
                expired = false;
                await connection.ReportStatusAsync(new ProviderStatus(true), stoppingToken);
            }

            if (await connection.PublishNowAsync(stoppingToken) is null)
            {
                logger.LogInformation("Not connected at this step; the next hello publishes instead.");
            }
        }
    }
}

/// <summary>The sample's own switches.</summary>
/// <param name="SimulateLoginExpiry">Report an expired login every few steps.</param>
public sealed record SampleOptions(bool SimulateLoginExpiry);
