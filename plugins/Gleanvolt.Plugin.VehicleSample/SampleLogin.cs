using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;

namespace Gleanvolt.Plugin.VehicleSample;

/// <summary>
/// The pretend car account's login (#410): what a real provider's page signs in to. While it is expired
/// the sample publishes nothing and Gleanvolt shows that the owner is needed; signing in on the sample's
/// page, or the next step with <c>--simulate-login-expiry</c>, brings it back.
/// </summary>
public sealed class SampleLogin(VehicleProviderConnection connection, ILogger<SampleLogin> logger)
{
    private volatile bool _expired;

    /// <summary>Whether the pretend login has expired.</summary>
    public bool Expired => _expired;

    /// <summary>Pretends the car account's login expired, and tells Gleanvolt the owner is needed.</summary>
    public async Task ExpireAsync(string why, CancellationToken cancellationToken)
    {
        _expired = true;
        logger.LogInformation("Simulating an expired login.");
        await connection.ReportStatusAsync(new ProviderStatus(false, why, NeedsOwner: true), cancellationToken);
    }

    /// <summary>Pretends the owner signed in again: healthy, and publishes at once.</summary>
    public async Task SignInAsync(CancellationToken cancellationToken)
    {
        _expired = false;
        logger.LogInformation("Signed in again (simulated).");
        await connection.ReportStatusAsync(new ProviderStatus(true), cancellationToken);
        await connection.PublishNowAsync(cancellationToken);
    }
}
