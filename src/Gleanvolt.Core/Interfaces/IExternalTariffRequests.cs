namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// How <see cref="Strategies.ExternalTariffProvider"/> asks its provider for fresh data (#401), without
/// knowing how the provider is reached. Sending never waits for the answer: the answer is a publish of
/// its own, whenever it comes.
/// </summary>
public interface IExternalTariffRequests
{
    /// <summary>Asks the attached provider for fresh data. False when none is attached.</summary>
    Task<bool> RequestRefreshAsync(CancellationToken cancellationToken);
}
