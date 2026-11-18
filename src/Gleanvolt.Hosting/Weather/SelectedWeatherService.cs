using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The <see cref="IWeatherService"/> every reader gets (#424): the session recorder, <c>/forecast</c> and
/// <c>/status</c>. It asks the external provider while External is selected and nothing otherwise, so a
/// switch on Settings applies to the next session without a restart. Since #425 there is no built-in.
/// </summary>
public sealed class SelectedWeatherService(IWeatherSourceSelection selection, ExternalWeatherService external) : IWeatherService
{
    public WeatherSource Source => selection.Selected;

    public bool IsConfigured => Source == WeatherSource.External && external.IsConfigured;

    public Task<WeatherReading?> GetCurrentAsync(CancellationToken cancellationToken = default) =>
        Source == WeatherSource.External
            ? external.GetCurrentAsync(cancellationToken)
            : Task.FromResult<WeatherReading?>(null);
}
