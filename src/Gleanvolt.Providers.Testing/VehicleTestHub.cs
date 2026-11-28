using System.Collections.Concurrent;
using Gleanvolt.Providers.Client;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;

namespace Gleanvolt.Providers.Testing;

/// <summary>
/// Gleanvolt's vehicle hub for a car plugin's tests (#473), on a loopback port: it welcomes a plugin with the car and
/// the charging session, keeps what the plugin publishes and reports, asks for a reading, tells the plugin the car or
/// the session changed, and pairs a plugin without a token at once. It checks hellos and readings as Gleanvolt does.
/// </summary>
public sealed class VehicleTestHub : IAsyncDisposable
{
    private readonly VehicleTestHubOptions _options;
    private readonly WebApplication _app;
    private readonly PairingDesk _pairing;
    private readonly ConcurrentDictionary<string, TaskCompletionSource<VehicleReading>> _reads = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private readonly List<VehicleReading> _readings = [];
    private readonly List<RefusedVehicleReading> _refusals = [];
    private readonly List<ProviderStatus> _statuses = [];
    private TaskCompletionSource<ProviderHello> _hello = NewHello();
    private string? _connectionId;
    private ProviderHello? _attached;

    private VehicleTestHub(VehicleTestHubOptions options, WebApplication app, Uri endpoint, string token)
    {
        _options = options;
        _app = app;
        Endpoint = endpoint;
        Token = token;
        _pairing = new PairingDesk(token);
    }

    /// <summary>Where the hub listens, e.g. <c>http://127.0.0.1:41234/</c>.</summary>
    public Uri Endpoint { get; }

    /// <summary>The token a plugin must send, and the one pairing hands over.</summary>
    public string Token { get; }

    /// <summary>The attached plugin's hello, or null.</summary>
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

    /// <summary>Whether a plugin is attached.</summary>
    public bool IsAttached => Hello is not null;

    /// <summary>The latest status the plugin reported, or null.</summary>
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

    /// <summary>Every reading the hub accepted, oldest first.</summary>
    public IReadOnlyList<VehicleReading> Readings => Snapshot(_readings);

    /// <summary>Every reading the hub refused, as Gleanvolt would, with why.</summary>
    public IReadOnlyList<RefusedVehicleReading> Refusals => Snapshot(_refusals);

    /// <summary>Every request to be paired, oldest first.</summary>
    public IReadOnlyList<PairingKnock> PairingKnocks => _pairing.Knocks;

    /// <summary>Starts a hub on a free loopback port.</summary>
    public static async Task<VehicleTestHub> StartAsync(VehicleTestHubOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new VehicleTestHubOptions();
        var token = TestHubs.Token(options.Token);
        var builder = TestHubs.CreateBuilder();

        // The instance is made before the app is built, so the hub can be handed it; the app and the address are
        // filled in once it runs.
        var holder = new Holder();
        builder.Services.AddSingleton(holder);

        var app = builder.Build();
        TestHubs.UseToken(app, token);
        app.MapHub<TestVehicleHub>(VehicleContract.HubPath);
        if (options.AcceptPairing)
        {
            TestHubs.MapPairing(app, () => holder.Hub!._pairing);
        }

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var hub = new VehicleTestHub(options, app, TestHubs.Address(app), token);
        holder.Hub = hub;
        return hub;
    }

    /// <summary>Points a plugin's options at this hub, with its token.</summary>
    public void Configure(ProviderOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Endpoint = Endpoint;
        options.Token = Token;
    }

    /// <summary>Waits for a plugin to say hello and be accepted.</summary>
    public Task<ProviderHello> WaitForHelloAsync(CancellationToken cancellationToken = default)
    {
        Task<ProviderHello> hello;
        lock (_gate)
        {
            hello = _hello.Task;
        }

        return WaitAsync(hello, "the plugin to say hello", cancellationToken);
    }

    /// <summary>Waits for a reported status that matches, or any status; the latest such one.</summary>
    public Task<ProviderStatus> WaitForStatusAsync(Func<ProviderStatus, bool>? match = null, CancellationToken cancellationToken = default) =>
        PollAsync(() => Statuses.LastOrDefault(match ?? (_ => true)), "the plugin reported no such status", cancellationToken);

    /// <summary>Waits for an accepted reading that matches, or any reading; the latest such one.</summary>
    public Task<VehicleReading> WaitForReadingAsync(Func<VehicleReading, bool>? match = null, CancellationToken cancellationToken = default) =>
        PollAsync(() => Readings.LastOrDefault(match ?? (_ => true)), "the plugin published no such reading", cancellationToken);

    /// <summary>Asks the plugin for a reading, as Gleanvolt's Refresh does, and waits for the one in reply.</summary>
    public async Task<VehicleReading> RefreshAsync(CancellationToken cancellationToken = default)
    {
        var id = RequestId.New();
        var answer = new TaskCompletionSource<VehicleReading>(TaskCreationOptions.RunContinuationsAsynchronously);
        _reads[id.Value] = answer;

        try
        {
            await Client().RefreshRequested(new RefreshRequest(id, _options.Time.GetUtcNow())).ConfigureAwait(false);
            return await WaitAsync(answer.Task, "a reading in reply", cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _reads.TryRemove(id.Value, out _);
        }
    }

    /// <summary>Tells the plugin the owner changed the car on Gleanvolt's Car page.</summary>
    public Task SetCarAsync(VehicleInfo car)
    {
        ArgumentNullException.ThrowIfNull(car);
        _options.Car = car;
        return Client().VehicleChanged(car);
    }

    /// <summary>Tells the plugin a charging session opened, closed or changed mode.</summary>
    public Task SetSessionAsync(ChargingSessionInfo session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _options.Session = session;
        return Client().ChargingSessionChanged(session);
    }

    public async ValueTask DisposeAsync()
    {
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    // ---- What the hub calls ----

    internal VehicleWelcome Welcome(ProviderHello hello, string connectionId)
    {
        var welcome = new VehicleWelcome
        {
            HostVersion = "test",
            ContractMajor = VehicleContract.Major,
            ContractMinor = VehicleContract.Minor,
            Car = _options.Car,
            Session = _options.Session,
        };

        var (code, reason) = HelloRules.Common(hello, VehicleContract.Major, VehicleContract.Minor);

        lock (_gate)
        {
            if (code is null && _attached is { } current && _connectionId != connectionId && current.ProviderId != hello.ProviderId)
            {
                (code, reason) = (RefusalCodes.AnotherProvider, $"Another car provider, {current.Name}, is connected.");
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

    internal PublishResult Published(VehicleReading? reading)
    {
        if (reading is null)
        {
            return PublishResult.Refused(["The reading is empty."]);
        }

        IReadOnlyList<string> problems;
        lock (_gate)
        {
            problems = VehicleReadingRules.Check(reading, _readings.Count == 0 ? null : _readings[^1], _options.Time.GetUtcNow());
            if (problems.Count > 0)
            {
                _refusals.Add(new RefusedVehicleReading(reading, problems));
            }
            else
            {
                _readings.Add(reading);
            }
        }

        TaskCompletionSource<VehicleReading>? waiting = null;
        if (reading.InReplyTo is { } id)
        {
            _reads.TryGetValue(id.Value, out waiting);
        }

        if (problems.Count > 0)
        {
            waiting?.TrySetException(new InvalidOperationException("Gleanvolt would refuse the reading: " + string.Join(" ", problems)));
            return PublishResult.Refused([.. problems]);
        }

        waiting?.TrySetResult(reading);
        return PublishResult.Ok;
    }

    // ---- Inside ----

    private IVehicleClient Client()
    {
        string? connectionId;
        lock (_gate)
        {
            connectionId = _connectionId;
        }

        if (connectionId is null)
        {
            throw new InvalidOperationException("No car plugin is connected. Wait for it with WaitForHelloAsync first.");
        }

        return _app.Services.GetRequiredService<IHubContext<TestVehicleHub, IVehicleClient>>().Clients.Client(connectionId);
    }

    private async Task<T> PollAsync<T>(Func<T?> find, string failure, CancellationToken cancellationToken)
        where T : class
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(_options.Timeout);

        try
        {
            while (true)
            {
                if (find() is { } found)
                {
                    return found;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(20), timeout.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException($"Waited {_options.Timeout.TotalSeconds:0} s: {failure}.");
        }
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

    private IReadOnlyList<T> Snapshot<T>(List<T> list)
    {
        lock (_gate)
        {
            return [.. list];
        }
    }

    private static TaskCompletionSource<ProviderHello> NewHello() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    internal sealed class Holder
    {
        public VehicleTestHub? Hub { get; set; }
    }
}
