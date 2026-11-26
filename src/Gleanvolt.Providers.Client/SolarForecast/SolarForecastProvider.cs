using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.SolarForecast;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client.SolarForecast;

/// <summary>What a solar forecast provider's author writes: the forecast for a site, on demand.</summary>
public interface ISolarForecastSource
{
    /// <summary>
    /// The forecast for <paramref name="site"/>, in UTC periods. <see cref="SolarForecastData.InReplyTo"/>
    /// is filled in by the SDK.
    /// </summary>
    /// <param name="site">The installation, from the welcome or the latest <see cref="ISolarForecastClient.SiteChanged"/>.</param>
    Task<SolarForecastData> GetForecastAsync(PvSite site, CancellationToken cancellationToken);
}

/// <summary>
/// The solar forecast kind's connection (#388): publishes once after every accepted hello, answers
/// every <see cref="ISolarForecastClient.RefreshRequested"/>, publishes again when the site changes, and
/// can be asked to publish at any other time.
/// </summary>
public sealed class SolarForecastProviderConnection : ProviderConnection<SolarForecastWelcome>
{
    private readonly ISolarForecastSource _source;
    private PvSite? _site;

    public SolarForecastProviderConnection(
        IOptions<ProviderOptions> options, ISolarForecastSource source, ILogger<SolarForecastProviderConnection> logger,
        ProviderCredentials? credentials = null)
        : base(options.Value, SolarForecastContract.HubPath, SolarForecastContract.Major, SolarForecastContract.Minor, logger, credentials)
    {
        _source = source;
        Welcomed += welcome => _site = welcome.Site;
    }

    /// <summary>The installation to forecast for: the welcome's, or the latest change. Null before the first hello.</summary>
    public PvSite? Site => _site;

    /// <summary>
    /// Asks the source and publishes, unprompted: for a provider whose forecast changes on its own
    /// schedule. Null when no session is attached.
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

        connection.On<PvSite>(ProviderMethods.SiteChanged, site =>
        {
            _site = site;
            Logger.LogInformation("Gleanvolt's site changed; publishing a forecast for it.");
            _ = Task.Run(() => PublishAsync(null, CancellationToken.None));
            return Task.CompletedTask;
        });
    }

    protected override Task OnWelcomedAsync(SolarForecastWelcome welcome, CancellationToken cancellationToken) =>
        PublishAsync(null, cancellationToken);

    private async Task<PublishResult?> PublishAsync(RequestId? inReplyTo, CancellationToken cancellationToken)
    {
        if (_site is not { } site || !IsAttached)
        {
            return null;
        }

        try
        {
            var data = await _source.GetForecastAsync(site, cancellationToken).ConfigureAwait(false);
            var result = await Connection.InvokeAsync<PublishResult>(
                ProviderMethods.Publish, data with { InReplyTo = inReplyTo }, cancellationToken).ConfigureAwait(false);

            if (result.Accepted)
            {
                Logger.LogInformation("Published {Count} forecast periods.", data.Periods.Count);
            }
            else
            {
                Logger.LogWarning("Gleanvolt refused the forecast: {Problems}", string.Join(" ", result.Problems));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not publish the forecast ({Reason}).", ex.Message);
            await ReportStatusAsync(new ProviderStatus(false, ex.Message), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
    }
}

/// <summary>Registration.</summary>
public static class SolarForecastProviderServiceCollectionExtensions
{
    /// <summary>
    /// Runs a solar forecast provider in this host: <typeparamref name="TSource"/> supplies the forecast,
    /// and the connection to Gleanvolt runs as a hosted service. Resolve
    /// <see cref="SolarForecastProviderConnection"/> to publish unprompted.
    /// </summary>
    public static IServiceCollection AddGleanvoltSolarForecastProvider<TSource>(this IServiceCollection services, Action<ProviderOptions> configure)
        where TSource : class, ISolarForecastSource
    {
        services.Configure(configure);
        services.TryAddSingleton<ProviderCredentials>();
        services.AddSingleton<ISolarForecastSource, TSource>();
        services.AddSingleton<SolarForecastProviderConnection>();
        services.AddHostedService(provider => provider.GetRequiredService<SolarForecastProviderConnection>());
        return services;
    }
}
