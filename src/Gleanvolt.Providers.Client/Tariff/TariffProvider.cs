using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Tariff;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Providers.Client.Tariff;

/// <summary>What a tariff provider's author writes: the tariff, on demand.</summary>
public interface ITariffSource
{
    /// <summary>
    /// The tariff from now on, in UTC windows, priced (if at all) in <see cref="TariffWelcome.Currency"/>.
    /// <see cref="TariffData.InReplyTo"/> is filled in by the SDK.
    /// </summary>
    /// <param name="site">The installation's time zone and currency.</param>
    Task<TariffData> GetTariffAsync(TariffWelcome site, CancellationToken cancellationToken);
}

/// <summary>
/// The tariff kind's connection: publishes once after every accepted hello, answers every
/// <see cref="ITariffClient.RefreshRequested"/>, and can be asked to publish at any other time.
/// </summary>
public sealed class TariffProviderConnection : ProviderConnection<TariffWelcome>
{
    private readonly ITariffSource _source;

    public TariffProviderConnection(IOptions<ProviderOptions> options, ITariffSource source, ILogger<TariffProviderConnection> logger,
        ProviderCredentials? credentials = null)
        : base(options.Value, TariffContract.HubPath, TariffContract.Major, TariffContract.Minor, logger, credentials)
    {
        _source = source;
    }

    /// <summary>
    /// Asks the source and publishes, unprompted: for a provider whose data changes on its own
    /// schedule, such as day-ahead prices. Null when no session is attached.
    /// </summary>
    public Task<PublishResult?> PublishNowAsync(CancellationToken cancellationToken = default) => PublishAsync(null, cancellationToken);

    protected override void OnConfigure(HubConnection connection) =>
        connection.On<RefreshRequest>(ProviderMethods.RefreshRequested, request =>
        {
            // Answered from a task of its own: the receive loop is not held while the source works.
            _ = Task.Run(() => PublishAsync(request.Id, CancellationToken.None));
            return Task.CompletedTask;
        });

    protected override Task OnWelcomedAsync(TariffWelcome welcome, CancellationToken cancellationToken) =>
        PublishAsync(null, cancellationToken);

    private async Task<PublishResult?> PublishAsync(RequestId? inReplyTo, CancellationToken cancellationToken)
    {
        if (Welcome is not { } site || !IsAttached)
        {
            return null;
        }

        try
        {
            var data = await _source.GetTariffAsync(site, cancellationToken).ConfigureAwait(false);
            var result = await Connection.InvokeAsync<PublishResult>(
                ProviderMethods.Publish, data with { InReplyTo = inReplyTo }, cancellationToken).ConfigureAwait(false);

            if (result.Accepted)
            {
                Logger.LogInformation("Published {Count} tariff windows.", data.Windows.Count);
            }
            else
            {
                Logger.LogWarning("Gleanvolt refused the tariff: {Problems}", string.Join(" ", result.Problems));
            }

            return result;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Logger.LogWarning("Could not publish the tariff ({Reason}).", ex.Message);
            await ReportStatusAsync(new ProviderStatus(false, ex.Message), CancellationToken.None).ConfigureAwait(false);
            return null;
        }
    }
}

/// <summary>Registration.</summary>
public static class TariffProviderServiceCollectionExtensions
{
    /// <summary>
    /// Runs a tariff provider in this host: <typeparamref name="TSource"/> supplies the data, and the
    /// connection to Gleanvolt runs as a hosted service. Resolve <see cref="TariffProviderConnection"/>
    /// to publish unprompted.
    /// </summary>
    public static IServiceCollection AddGleanvoltTariffProvider<TSource>(this IServiceCollection services, Action<ProviderOptions> configure)
        where TSource : class, ITariffSource
    {
        services.Configure(configure);
        services.TryAddSingleton<ProviderCredentials>();
        services.AddSingleton<ITariffSource, TSource>();
        services.AddSingleton<TariffProviderConnection>();
        services.AddHostedService(provider => provider.GetRequiredService<TariffProviderConnection>());
        return services;
    }
}
