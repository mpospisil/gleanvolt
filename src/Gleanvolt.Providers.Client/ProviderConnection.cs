using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Providers.Contracts;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Client;

/// <summary>
/// One provider's connection to Gleanvolt, for any kind: connects, says hello, and when the connection
/// is lost or can't be made, connects again with a backoff, for as long as the provider runs. The
/// provider can start before Gleanvolt, and Gleanvolt can restart under it: either is a connection
/// that fails until Gleanvolt answers. It waits longer after a refusal.
///
/// <para>One path for every connection, rather than SignalR's automatic reconnect: that one gives up
/// after four tries in about 40 seconds, ignores the options' delays, and a reconnect needs a new hello
/// anyway, since it is a new connection to Gleanvolt.</para> A kind derives from it, registers its own
/// handlers in <see cref="OnConfigure"/> and publishes from <see cref="OnWelcomedAsync"/>.
///
/// <para>Handlers never block the receive loop: a host request is answered from a task of its own, so
/// the next message can be read while the answer is being made.</para>
/// </summary>
/// <typeparam name="TWelcome">The kind's answer to a hello.</typeparam>
public abstract class ProviderConnection<TWelcome> : BackgroundService
    where TWelcome : Welcome
{
    private readonly ProviderOptions _options;
    private readonly string _hubPath;
    private readonly int _contractMajor;
    private readonly int _contractMinor;
    private HubConnection? _connection;
    private TWelcome? _welcome;
    private bool _goodbye;

    protected ProviderConnection(ProviderOptions options, string hubPath, int contractMajor, int contractMinor, ILogger logger)
    {
        _options = options;
        _hubPath = hubPath;
        _contractMajor = contractMajor;
        _contractMinor = contractMinor;
        Logger = logger;
    }

    protected ILogger Logger { get; }

    /// <summary>The accepted welcome of the current session, or null between sessions.</summary>
    public TWelcome? Welcome => _welcome;

    /// <summary>Whether a session is attached right now.</summary>
    public bool IsAttached => _welcome is not null && _connection?.State == HubConnectionState.Connected;

    /// <summary>Raised after every accepted hello, for tests and for a provider that watches its own state.</summary>
    public event Action<TWelcome>? Welcomed;

    /// <summary>The connection of the current session, for a kind's calls.</summary>
    protected HubConnection Connection => _connection ?? throw new InvalidOperationException("Not connected.");

    /// <summary>Registers the kind's handlers on a new connection, before it starts.</summary>
    protected abstract void OnConfigure(HubConnection connection);

    /// <summary>
    /// The hello about to be sent, for a kind to add its own part to: the charger adds its capabilities (#440).
    /// Asked before every hello, so what it adds can change between sessions.
    /// </summary>
    protected virtual ProviderHello OnHello(ProviderHello hello) => hello;

    /// <summary>After every accepted hello: typically, publish once.</summary>
    protected virtual Task OnWelcomedAsync(TWelcome welcome, CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Tells Gleanvolt how the provider is doing. Does nothing between sessions.</summary>
    public async Task ReportStatusAsync(ProviderStatus status, CancellationToken cancellationToken = default)
    {
        if (IsAttached)
        {
            await Connection.InvokeAsync(ProviderMethods.ReportStatus, status, cancellationToken).ConfigureAwait(false);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        Validate();
        var delay = _options.InitialRetryDelay;

        while (!stoppingToken.IsCancellationRequested)
        {
            TimeSpan wait;

            try
            {
                wait = await RunSessionAsync(stoppingToken).ConfigureAwait(false);
                delay = _options.InitialRetryDelay;
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Logger.LogWarning("Could not reach Gleanvolt at {Endpoint} ({Reason}); trying again in {Delay}.",
                    HubUri(), ex.Message, delay);
                wait = delay;
                delay = TimeSpan.FromTicks(Math.Min(delay.Ticks * 2, _options.MaxRetryDelay.Ticks));
            }
            finally
            {
                _welcome = null;
                if (_connection is { } connection)
                {
                    _connection = null;
                    await connection.DisposeAsync().ConfigureAwait(false);
                }
            }

            try
            {
                await Task.Delay(wait, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    // One session: connect, hello, and stay until the connection is gone for good. Returns how long to
    // wait before the next one.
    private async Task<TimeSpan> RunSessionAsync(CancellationToken stoppingToken)
    {
        _goodbye = false;
        var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var connection = new HubConnectionBuilder()
            .WithUrl(HubUri(), options =>
            {
                options.AccessTokenProvider = () => Task.FromResult<string?>(_options.Token);
                _options.ConfigureHttp?.Invoke(options);
            })
            // Enums by name, as the host writes them and as the protocol page describes them.
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(
                new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)))
            .Build();

        _connection = connection;

        connection.On<string, string>(ProviderMethods.Goodbye, (code, reason) =>
        {
            _goodbye = true;
            Logger.LogWarning("Gleanvolt closed the session ({Code}): {Reason}", code, reason);
            return Task.CompletedTask;
        });

        connection.Closed += error =>
        {
            _welcome = null;
            if (error is not null && !_goodbye)
            {
                Logger.LogInformation("Lost the connection to Gleanvolt ({Reason}); connecting again.", error.Message);
            }

            closed.TrySetResult();
            return Task.CompletedTask;
        };

        OnConfigure(connection);

        await connection.StartAsync(stoppingToken).ConfigureAwait(false);
        Logger.LogInformation("Connected to Gleanvolt at {Endpoint}.", HubUri());

        if (!await HelloCoreAsync(stoppingToken).ConfigureAwait(false))
        {
            await connection.StopAsync(CancellationToken.None).ConfigureAwait(false);
            return _options.RefusedRetryDelay;
        }

        await closed.Task.WaitAsync(stoppingToken).ConfigureAwait(false);

        // Closed after a goodbye: the owner turned the kind off, or something replaced this session.
        // Otherwise the connection was lost, Gleanvolt restarting for one: try again soon.
        return _goodbye ? _options.RefusedRetryDelay : _options.InitialRetryDelay;
    }

    // True when accepted.
    private async Task<bool> HelloCoreAsync(CancellationToken stoppingToken)
    {
        var hello = new ProviderHello(
            _options.ProviderId,
            _options.Name,
            _options.Version ?? Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "0",
            _contractMajor,
            _contractMinor,
            PageUrl: _options.PageUrl);
        hello = OnHello(hello);

        var welcome = await Connection.InvokeAsync<TWelcome>(ProviderMethods.Hello, hello, stoppingToken).ConfigureAwait(false);

        if (!welcome.Accepted)
        {
            Logger.LogWarning("Gleanvolt refused the provider ({Code}): {Reason} Trying again in {Delay}.",
                welcome.ReasonCode, welcome.Reason, _options.RefusedRetryDelay);
            return false;
        }

        _welcome = welcome;
        Logger.LogInformation("Gleanvolt {Version} accepted the provider {Provider}.", welcome.HostVersion, _options.ProviderId);
        Welcomed?.Invoke(welcome);

        // Its own task, so a slow source does not hold up the receive loop or the session.
        _ = Task.Run(async () =>
        {
            try
            {
                await OnWelcomedAsync(welcome, stoppingToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Logger.LogWarning("Could not publish after the hello ({Reason}).", ex.Message);
            }
        }, stoppingToken);

        return true;
    }

    private Uri HubUri() => new(_options.Endpoint!, _hubPath);

    private void Validate()
    {
        if (_options.Endpoint is null)
        {
            throw new InvalidOperationException("ProviderOptions.Endpoint is required: Gleanvolt's web address, e.g. http://gleanvolt.local:8090.");
        }

        if (string.IsNullOrWhiteSpace(_options.Token))
        {
            throw new InvalidOperationException("ProviderOptions.Token is required: generate one on Gleanvolt's Settings.");
        }

        if (string.IsNullOrWhiteSpace(_options.ProviderId) || string.IsNullOrWhiteSpace(_options.Name))
        {
            throw new InvalidOperationException("ProviderOptions.ProviderId and Name are required.");
        }

        if (_options.PageUrl is { } page && (!page.IsAbsoluteUri || (page.Scheme != Uri.UriSchemeHttp && page.Scheme != Uri.UriSchemeHttps)))
        {
            throw new InvalidOperationException("ProviderOptions.PageUrl must be an absolute http or https address the owner's browser can reach.");
        }
    }

}
