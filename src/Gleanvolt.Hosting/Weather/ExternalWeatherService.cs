using System.Collections.Concurrent;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Infrastructure.OpenWeather;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Weather;
using Gleanvolt.Providers.Server;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Weather;

/// <summary>
/// An external provider as the weather source (#424). The weather is wanted twice a charging session, so
/// it is <b>asked for</b>, not streamed: a reading the provider sent within
/// <see cref="WeatherOptions.ExternalReadingMaxAge"/> is used as it is; otherwise
/// <see cref="IWeatherClient.RefreshRequested"/> goes out through <c>IHubContext</c> and this waits, on the
/// host's side, up to <see cref="WeatherOptions.ExternalRequestTimeout"/> for a publish quoting the
/// request. The hub itself never waits.
///
/// <para>Decoration, as every weather source is: a provider that is disconnected, silent or refused only
/// means a session recorded without weather. Nothing falls back to OpenWeatherMap.</para>
/// </summary>
public sealed class ExternalWeatherService : IWeatherService
{
    private readonly IServiceProvider _services;
    private readonly WeatherOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<ExternalWeatherService> _logger;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<WeatherReading?>> _waiting = new(StringComparer.Ordinal);
    private volatile Held? _held;

    public ExternalWeatherService(
        IServiceProvider services,
        IOptions<WeatherOptions> options,
        TimeProvider time,
        ILogger<ExternalWeatherService> logger)
    {
        _services = services;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Always: the provider is the one to say what it lacks (a key, the site's location).</summary>
    public bool IsConfigured => true;

    public WeatherSource Source => WeatherSource.External;

    /// <summary>The newest reading the provider sent that was accepted, or null.</summary>
    public WeatherReading? Current => _held?.Reading;

    /// <summary>When <see cref="Current"/> arrived.</summary>
    public DateTimeOffset? ReceivedAt => _held?.ReceivedAt;

    public async Task<WeatherReading?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        // Fresh enough: judged by when it arrived, not by when it was observed. A weather service's own
        // observation is often a quarter of an hour old the moment it is read, and asking again would
        // only fetch the same one.
        if (_held is { } held && _time.GetUtcNow() - held.ReceivedAt <= _options.ExternalReadingMaxAge)
        {
            return held.Reading;
        }

        // Resolved here rather than injected: the registry is built from the kinds, and this kind's
        // switch reads the selection the service that holds this one follows.
        var sessions = _services.GetRequiredService<ExternalProviderRegistry>().For(WeatherContract.Kind);
        var timeout = _options.ExternalRequestTimeout;

        if (sessions?.BeginRequest(timeout) is not { } request)
        {
            _logger.LogInformation("Wanted the weather, but no external weather provider is connected.");
            return null;
        }

        var answer = new TaskCompletionSource<WeatherReading?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _waiting[request.Id.Value] = answer;

        try
        {
            await _services.GetRequiredService<IHubContext<WeatherHub, IWeatherClient>>()
                .Clients.Client(request.ConnectionId)
                .RefreshRequested(new RefreshRequest(request.Id, _time.GetUtcNow()))
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return await answer.Task.WaitAsync(timeout, _time, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            // The panel says "didn't answer" when the request runs out (BeginRequest); the session is
            // recorded without weather.
            _logger.LogWarning("The external weather provider did not answer within {Timeout}.", timeout);
            return null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        catch (Exception ex)
        {
            // Never throws for a provider-side problem: see IWeatherService.
            _logger.LogWarning("Could not ask the external weather provider for the weather ({Reason}).", ex.Message);
            return null;
        }
        finally
        {
            _waiting.TryRemove(request.Id.Value, out _);
        }
    }

    /// <summary>The hub accepted <paramref name="reading"/>; an ask waiting on <paramref name="inReplyTo"/> has its answer.</summary>
    internal void Accepted(RequestId? inReplyTo, WeatherReading reading)
    {
        _held = new Held(reading, _time.GetUtcNow());

        if (inReplyTo is not null && _waiting.TryGetValue(inReplyTo.Value, out var answer))
        {
            answer.TrySetResult(reading);
        }
    }

    /// <summary>The hub refused a reading; an ask waiting on <paramref name="inReplyTo"/> ends without one.</summary>
    internal void Refused(RequestId? inReplyTo)
    {
        if (inReplyTo is not null && _waiting.TryGetValue(inReplyTo.Value, out var answer))
        {
            answer.TrySetResult(null);
        }
    }

    private sealed record Held(WeatherReading Reading, DateTimeOffset ReceivedAt);
}
