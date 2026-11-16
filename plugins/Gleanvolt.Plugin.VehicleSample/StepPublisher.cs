using Gleanvolt.Providers.Client.Vehicle;

namespace Gleanvolt.Plugin.VehicleSample;

/// <summary>
/// Publishes on every step of the pretend car's SOC, besides answering Gleanvolt's requests: what a
/// car provider does when the car reports a change. Nothing is published while the pretend login is
/// expired (<see cref="SampleLogin"/>). With <c>--simulate-login-expiry</c>, every
/// <see cref="ExpiryEvery"/>th step expires the login, and the next one signs in again, to show
/// Gleanvolt's sign-in band without pressing anything.
/// </summary>
public sealed class StepPublisher(
    VehicleProviderConnection connection,
    SimulatedCar car,
    SampleLogin login,
    SampleOptions options,
    TimeProvider time,
    ILogger<StepPublisher> logger)
    : BackgroundService
{
    /// <summary>How often the login "expires" with <c>--simulate-login-expiry</c>, in steps.</summary>
    public const int ExpiryEvery = 5;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var expiredByClock = false;

        while (!stoppingToken.IsCancellationRequested)
        {
            var wait = car.NextStepAt - time.GetUtcNow();
            await Task.Delay(wait > TimeSpan.Zero ? wait : TimeSpan.Zero, time, stoppingToken);

            if (options.SimulateLoginExpiry && car.Steps % ExpiryEvery == 0)
            {
                expiredByClock = true;
                await login.ExpireAsync("The car account's login expired (simulated). It comes back by itself in one step.", stoppingToken);
                continue;
            }

            if (expiredByClock)
            {
                expiredByClock = false;
                await login.SignInAsync(stoppingToken);
                continue;
            }

            if (login.Expired)
            {
                logger.LogInformation("The pretend login is expired; sign in on the sample's page to publish again.");
                continue;
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
