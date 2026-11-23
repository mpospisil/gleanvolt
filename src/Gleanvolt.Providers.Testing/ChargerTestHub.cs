using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Providers.Testing;

/// <summary>
/// A charger hub on a loopback port, for a charger plugin's tests (#439). It speaks the charger contract as
/// Gleanvolt does: the plugin connects with the ordinary SDK, says hello, and is then read and commanded through
/// <see cref="ReadAsync"/>, <see cref="SetCurrentAsync"/> and <see cref="SetChargingAllowedAsync"/>. Every reading
/// is checked by <see cref="ChargerReadingRules"/>, and one that breaks them is refused, as Gleanvolt refuses it.
/// </summary>
/// <remarks>
/// Gleanvolt's own safeguards (the setpoint threshold, the clamp to the charger's ceiling, dry-run) are not
/// here: they are Gleanvolt's, and a plugin must carry out what it is sent. One plugin at a time, as in Gleanvolt.
/// </remarks>
public sealed class ChargerTestHub : IAsyncDisposable
{
    /// <summary>The largest message the hub takes, as Gleanvolt: 1 MiB.</summary>
    public const long MaximumMessageBytes = 1024 * 1024;

    private readonly ChargerTestHubOptions _options;
    private readonly WebApplication _app;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ChargerReading>> _reads = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<ChargerCommandResult>> _commands = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<ChargerReading> _readings = [];
    private readonly List<RefusedReading> _refusals = [];
    private readonly List<ProviderStatus> _statuses = [];
    private TaskCompletionSource<ProviderHello> _hello = NewHello();
    private string? _connectionId;
    private ProviderHello? _attached;

    private ChargerTestHub(ChargerTestHubOptions options, WebApplication app, Uri endpoint, string token)
    {
        _options = options;
        _app = app;
        Endpoint = endpoint;
        Token = token;
    }

    /// <summary>Where the plugin connects: <c>http://127.0.0.1:&lt;port&gt;</c>, without the hub's path.</summary>
    public Uri Endpoint { get; }

    /// <summary>The token the plugin connects with.</summary>
    public string Token { get; }

    /// <summary>The hello of the plugin connected now, or null.</summary>
    public ProviderHello? Hello
    {
        get
        {
            lock (_gate)
            {
                return _attached;
            }
        }
    }

    /// <summary>Whether a plugin is connected and its hello was accepted.</summary>
    public bool IsAttached => Hello is not null;

    /// <summary>The newest status the plugin reported, or null.</summary>
    public ProviderStatus? Status
    {
        get
        {
            lock (_gate)
            {
                return _statuses.Count == 0 ? null : _statuses[^1];
            }
        }
    }

    /// <summary>Every status the plugin reported, oldest first.</summary>
    public IReadOnlyList<ProviderStatus> Statuses => Snapshot(_statuses);

    /// <summary>Every reading the hub accepted, asked for or not, oldest first.</summary>
    public IReadOnlyList<ChargerReading> Readings => Snapshot(_readings);

    /// <summary>Every reading the hub refused, with why, oldest first.</summary>
    public IReadOnlyList<RefusedReading> Refusals => Snapshot(_refusals);

    /// <summary>Starts a hub on a free loopback port.</summary>
    public static async Task<ChargerTestHub> StartAsync(ChargerTestHubOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ChargerTestHubOptions();
        var token = options.Token ?? Convert.ToHexString(RandomNumberGenerator.GetBytes(16));

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Logging.ClearProviders();
        builder.Services.AddSignalR(hub => hub.MaximumReceiveMessageSize = MaximumMessageBytes)
            .AddJsonProtocol(json => json.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

        // The instance is made before the app is built, so the hub can be handed it; the app and the address are
        // filled in once it runs.
        var holder = new Holder();
        builder.Services.AddSingleton(holder);

        var app = builder.Build();
        app.Use((context, next) => Authorised(context.Request, token)
            ? next(context)
            : Refuse(context.Response));
        app.MapHub<TestChargerHub>(ChargerContract.HubPath);

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var address = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        var hub = new ChargerTestHub(options, app, new Uri(address), token);
        holder.Hub = hub;
        return hub;
    }

    /// <summary>Points the plugin's SDK at this hub: its <see cref="Endpoint"/> and <see cref="Token"/>.</summary>
    public void Configure(ProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Endpoint = Endpoint;
        options.Token = Token;
    }

    /// <summary>Waits until a plugin has said hello and been accepted, and returns its hello.</summary>
    public Task<ProviderHello> WaitForHelloAsync(CancellationToken cancellationToken = default)
    {
        Task<ProviderHello> hello;
        lock (_gate)
        {
            hello = _hello.Task;
        }

        return WaitAsync(hello, "the plugin to say hello", cancellationToken);
    }

    /// <summary>Waits until the plugin reports a status that <paramref name="match"/> accepts (any, when null).</summary>
    public async Task<ProviderStatus> WaitForStatusAsync(Func<ProviderStatus, bool>? match = null, CancellationToken cancellationToken = default)
    {
        match ??= _ => true;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            while (true)
            {
                if (Statuses.LastOrDefault(match) is { } status)
                {
                    return status;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"The plugin reported no such status within {_options.Timeout.TotalSeconds:0} s.");
        }
    }

    /// <summary>
    /// Reads the charger as Gleanvolt does on every poll: sends <c>RefreshRequested</c> and waits for the
    /// <c>Publish</c> that quotes it. Throws <see cref="ChargerContractException"/> when the reading breaks the
    /// contract's rules, and <see cref="TimeoutException"/> when none comes.
    /// </summary>
    public async Task<ChargerReading> ReadAsync(CancellationToken cancellationToken = default)
    {
        var id = RequestId.New();
        var answer = Pending(_reads, id);

        try
        {
            await Client().RefreshRequested(new RefreshRequest(id, _options.Time.GetUtcNow())).ConfigureAwait(false);
            return await WaitAsync(answer.Task, "a reading", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reads.TryRemove(id.Value, out _);
        }
    }

    /// <summary>Sends <c>SetCurrent</c> and waits for its <c>CommandDone</c>. 0 A is a pause.</summary>
    public Task<ChargerCommandResult> SetCurrentAsync(int amps, string reason = "test", CancellationToken cancellationToken = default) =>
        CommandAsync(id => Client().SetCurrent(new ChargerCurrentCommand(id, amps, reason)), cancellationToken);

    /// <summary>Sends <c>SetChargingAllowed</c> and waits for its <c>CommandDone</c>.</summary>
    public Task<ChargerCommandResult> SetChargingAllowedAsync(bool allowed, string reason = "test", CancellationToken cancellationToken = default) =>
        CommandAsync(id => Client().SetChargingAllowed(new ChargerAllowedCommand(id, allowed, reason)), cancellationToken);

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    // ---- What the hub calls ----

    internal ChargerWelcome Welcome(ProviderHello hello, string connectionId)
    {
        var welcome = new ChargerWelcome
        {
            HostVersion = "test",
            ContractMajor = ChargerContract.Major,
            ContractMinor = ChargerContract.Minor,
            Limits = _options.Limits,
            DryRun = _options.DryRun,
            TimeZone = _options.TimeZone,
        };

        var (code, reason) = HelloRules.Check(hello);

        lock (_gate)
        {
            if (code is null && _attached is { } current && _connectionId != connectionId && current.ProviderId != hello.ProviderId)
            {
                (code, reason) = (RefusalCodes.AnotherProvider, $"Another charger provider, {current.Name}, is connected.");
            }

            if (code is not null)
            {
                return welcome with { Accepted = false, ReasonCode = code, Reason = reason };
            }

            _connectionId = connectionId;
            _attached = hello;
            _hello.TrySetResult(hello);
        }

        return welcome with { Accepted = true };
    }

    internal void Detached(string connectionId)
    {
        lock (_gate)
        {
            if (_connectionId != connectionId)
            {
                return;
            }

            _connectionId = null;
            _attached = null;
            _hello = NewHello();
        }
    }

    internal bool IsCurrent(string connectionId)
    {
        lock (_gate)
        {
            return _connectionId == connectionId;
        }
    }

    internal void Reported(ProviderStatus status)
    {
        lock (_gate)
        {
            _statuses.Add(status);
        }
    }

    internal PublishResult Published(ChargerReading? reading)
    {
        if (reading is null)
        {
            return PublishResult.Refused(["The reading is empty."]);
        }

        var problems = ChargerReadingRules.Check(reading, _options.Time.GetUtcNow());
        TaskCompletionSource<ChargerReading>? waiting = null;
        if (reading.InReplyTo is { } id)
        {
            _reads.TryGetValue(id.Value, out waiting);
        }

        lock (_gate)
        {
            if (problems.Count > 0)
            {
                _refusals.Add(new RefusedReading(reading, problems));
            }
            else
            {
                _readings.Add(reading);
            }
        }

        if (problems.Count > 0)
        {
            waiting?.TrySetException(new ChargerContractException("Gleanvolt would refuse the reading.", problems));
            return PublishResult.Refused(problems);
        }

        waiting?.TrySetResult(reading);
        return PublishResult.Ok;
    }

    internal void CommandDone(ChargerCommandResult? result)
    {
        if (result?.InReplyTo is { } id && _commands.TryGetValue(id.Value, out var waiting))
        {
            waiting.TrySetResult(result);
        }
    }

    // ---- Inside ----

    private async Task<ChargerCommandResult> CommandAsync(Func<RequestId, Task> send, CancellationToken cancellationToken)
    {
        var id = RequestId.New();
        var answer = Pending(_commands, id);

        try
        {
            await send(id).ConfigureAwait(false);
            return await WaitAsync(answer.Task, "the command's CommandDone", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _commands.TryRemove(id.Value, out _);
        }
    }

    private IChargerClient Client()
    {
        string? connectionId;
        lock (_gate)
        {
            connectionId = _connectionId;
        }

        if (connectionId is null)
        {
            throw new InvalidOperationException("No charger plugin is connected. Wait for it with WaitForHelloAsync first.");
        }

        return _app.Services.GetRequiredService<IHubContext<TestChargerHub, IChargerClient>>().Clients.Client(connectionId);
    }

    private async Task<T> WaitAsync<T>(Task<T> task, string what, CancellationToken cancellationToken)
    {
        try
        {
            return await task.WaitAsync(_options.Timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"Waited {_options.Timeout.TotalSeconds:0} s for {what}.");
        }
    }

    private static TaskCompletionSource<T> Pending<T>(ConcurrentDictionary<string, TaskCompletionSource<T>> pending, RequestId id)
    {
        var answer = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id.Value] = answer;
        return answer;
    }

    private IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }

    private static TaskCompletionSource<ProviderHello> NewHello() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    // As Gleanvolt: the token in the Authorization header, or as access_token for a WebSocket that can't set one.
    private static bool Authorised(HttpRequest request, string token)
    {
        var header = request.Headers.Authorization.ToString();
        var offered = header.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? header["Bearer ".Length..].Trim()
            : request.Query["access_token"].ToString();

        return CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(offered), Encoding.UTF8.GetBytes(token));
    }

    private static Task Refuse(HttpResponse response)
    {
        response.StatusCode = StatusCodes.Status401Unauthorized;
        return Task.CompletedTask;
    }

    internal sealed class Holder
    {
        public ChargerTestHub? Hub { get; set; }
    }
}
