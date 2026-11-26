using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Weather;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client.Weather;

/// <summary>What a weather provider's author writes: the conditions at a site now, on demand.</summary>
public interface IWeatherSource
{
    /// <summary>
    /// The current conditions at <paramref name="site"/>, or null when there is nothing to send (no key
    /// yet, the service down); say why with <see cref="ProviderConnection{TWelcome}.ReportStatusAsync"/>.
    /// <see cref="WeatherData.InReplyTo"/> is filled in by the SDK.
    /// </summary>
    /// <param name="site">The site, from the welcome or the latest <see cref="IWeatherClient.SiteChanged"/>.</param>
    Task<WeatherData?> GetReadingAsync(WeatherSite site, CancellationToken cancellationToken);
}

/// <summary>
/// The weather kind's connection (#424): publishes once after every accepted hello, answers every
/// <see cref="IWeatherClient.RefreshRequested"/>, publishes again when the site changes, and publishes
/// whenever the provider calls <see cref="PublishNowAsync"/>. Gleanvolt asks only when it records a
/// charging session, so a source that keeps its last reading for a few minutes spends a handful of calls
/// a day.
/// </summary>
public sealed class WeatherProviderConnection : ProviderConnection<WeatherWelcome>
{
    private readonly IWeatherSource _source;
    private WeatherSite? _site;

    public WeatherProviderConnection(
        IOptions<ProviderOptions> options, IWeatherSource source, ILogger<WeatherProviderConnection> logger,
        ProviderCredentials? credentials = null)
        : base(options.Value, WeatherContract.HubPath, WeatherContract.Major, WeatherContract.Minor, logger, credentials)
    {
        _source = source;
        Welcomed += welcome => _site = welcome.Site;
    }

    /// <summary>The site: the welcome's, or the latest change. Null before the first hello.</summary>
    public WeatherSite? Site => _site;

    /// <summary>
    /// Asks the source and publishes, unprompted. Null when no session is attached or the source had
    /// nothing.
    /// </summary>
    public Task<PublishResult?> PublishNowAsync(CancellationToken cancellationToken = default) => PublishAsync(null, cancellationToken);

    protected override void OnConfigure(HubConnection connection)
    {
        connection.On<RefreshRequest>(ProviderMethods.RefreshRequested, request =>
        {
            // Answered from a task of its own: the receive loop is not held while the source works.
            _ = Task.Run(() => PublishAsync(request.Id, CancellationToken.None));
            return Task.CompletedTask;
        });

        connection.On<WeatherSite>(ProviderMethods.SiteChanged, site =>
        {
            _site = site;
            Logger.LogInformation("Gleanvolt's site changed; publishing the weather there.");
            _ = Task.Run(() => PublishAsync(null, CancellationToken.None));
            return Task.CompletedTask;
        });
    }

    protected override Task OnWelcomedAsync(WeatherWelcome welcome, CancellationToken cancellationToken) =>
        PublishAsync(null, cancellationToken);

    private async Task<PublishResult?> PublishAsync(RequestId? inReplyTo, CancellationToken cancellationToken)
    {
        if (_site is not { } site || !IsAttached)
        {
            return null;
        }

        try
        {
            if (await _source.GetReadingAsync(site, cancellationToken).ConfigureAwait(false) is not { } reading)
            {
                Logger.LogDebug("The source had no reading to publish.");
                return null;
            }

            var result = await Connection.InvokeAsync<PublishResult>(
                ProviderMethods.Publish, reading with { InReplyTo = inReplyTo }, cancellationToken).ConfigureAwait(false);

            if (result.Accepted)
            {
                Logger.LogInformation("Published the weather observed {ObservedAt:O}.", reading.ObservedAt);
            }
            else
            {
                Logger.LogWarning("Gleanvolt refused the weather: {Problems}", string.Join(" ", result.Problems));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not publish the weather ({Reason}).", ex.Message);
            await ReportStatusAsync(new ProviderStatus(false, ex.Message), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
    }
}

/// <summary>Registration.</summary>
public static class WeatherProviderServiceCollectionExtensions
{
    /// <summary>
    /// Runs a weather provider in this host: <typeparamref name="TSource"/> supplies the readings, and the
    /// connection to Gleanvolt runs as a hosted service. Resolve <see cref="WeatherProviderConnection"/>
    /// to publish unprompted.
    /// </summary>
    public static IServiceCollection AddGleanvoltWeatherProvider<TSource>(this IServiceCollection services, Action<ProviderOptions> configure)
        where TSource : class, IWeatherSource
    {
        services.Configure(configure);
        services.TryAddSingleton<ProviderCredentials>();
        services.AddSingleton<IWeatherSource, TSource>();
        services.AddSingleton<WeatherProviderConnection>();
        services.AddHostedService(provider => provider.GetRequiredService<WeatherProviderConnection>());
        return services;
    }
}
