using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.OpenWeather;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// The <see cref="IWeatherService"/> every reader gets (#424): the session recorder, <c>/forecast</c> and
/// <c>/status</c>. It asks whichever source is selected now, so a switch on Settings applies to the next
/// session without a restart.
/// </summary>
public sealed class SelectedWeatherService(
    IWeatherSourceSelection selection,
    OpenWeatherMapService openWeather,
    ExternalWeatherService external)
    : IWeatherService
{
    public WeatherSource Source => selection.Selected;

    public bool IsConfigured => Source switch
    {
        WeatherSource.OpenWeather => openWeather.IsConfigured,
        WeatherSource.External => external.IsConfigured,
        _ => false,
    };

    public Task<WeatherReading?> GetCurrentAsync(CancellationToken cancellationToken = default) => Source switch
    {
        WeatherSource.OpenWeather => openWeather.GetCurrentAsync(cancellationToken),
        WeatherSource.External => external.GetCurrentAsync(cancellationToken),
        _ => Task.FromResult<WeatherReading?>(null),
    };
}
