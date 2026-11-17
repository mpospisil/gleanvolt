using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;

namespace Gleanvolt.Plugin.Volkswagen.Volkswagen;

/// <summary>
/// volkswagen.de as the car's source (#410): what Gleanvolt's SDK asks for a reading, and the plugin's
/// copy of the built-in <c>VwWebsiteUpdateService</c>'s rule.
///
/// <para><b>Any failure stops it and asks the owner.</b> A one-time code wanted, no usable answer, no
/// answer at all, or anything else unexpected: the feed reports <c>NeedsOwner</c>, stops reading, and
/// nothing is replayed at VW's identity provider on a clock; that is how accounts get locked. A sign-in
/// on the plugin's page calls <see cref="Resume"/>, which puts it back on its clock without a restart.</para>
///
/// <para>Changed from the built-in: it decides how often to read from the car's own last charge state
/// (it can't see whether Gleanvolt has a charging session open); it stops on any exception, not only on
/// HTTP failures and timeouts, since nothing here would otherwise stop a clock from signing in again;
/// a read asked for within <see cref="VolkswagenOptions.MinimumTimeBetweenReads"/> of the last answers
/// with that reading; a changed account stops it until the owner signs in; and it resumes when told to,
/// not by watching a sign-in counter, since the page and the reads share one process.</para>
/// </summary>
public sealed class VolkswagenFeed : IVehicleSource
{
    private readonly VolkswagenOptions _options;
    private readonly AccountStore _account;
    private readonly VwWebsiteClient _client;
    private readonly TimeProvider _time;
    private readonly ILogger<VolkswagenFeed> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);

    private volatile bool _blocked;
    private ProviderStatus? _status;
    private Words? _statusWords;
    private VehicleReading? _last;
    private DateTimeOffset? _lastReadAt;

    public VolkswagenFeed(VolkswagenOptions options, AccountStore account, VwWebsiteClient client, TimeProvider time, ILogger<VolkswagenFeed> logger)
    {
        _options = options;
        _account = account;
        _client = client;
        _time = time;
        _logger = logger;

        // Another account or car: read nothing until the owner has signed in with it.
        account.IdentityChanged += () => Stop(PageText.AccountChanged);
    }

    /// <summary>Raised when the status or the last reading changed: for the page, and for Gleanvolt.</summary>
    public event Action? Changed;

    /// <summary>The last reading VW gave, or null.</summary>
    public VehicleReading? LastReading => _last;

    /// <summary>When VW was last asked, or null.</summary>
    public DateTimeOffset? LastReadAt => _lastReadAt;

    /// <summary>What Gleanvolt is told about the plugin, or null before anything is known.</summary>
    public ProviderStatus? Status => !_account.Current.IsComplete ? NotSetUp : _status;

    /// <summary>Why it isn't reading, in both languages, for the page; null while it reads.</summary>
    public Words? StatusWords => !_account.Current.IsComplete ? PageText.NotSetUp : _statusWords;

    // Gleanvolt words the code itself (#415); the English is for a Gleanvolt older than vehicle 1.3.
    private static readonly ProviderStatus NotSetUp = new(
        false, PageText.NotSetUp.En, NeedsOwner: true, Code: ProviderStatusCodes.NotSetUp);

    /// <summary>Whether reads are stopped until the owner signs in.</summary>
    public bool IsBlocked => _blocked || !_account.Current.IsComplete;

    /// <summary>
    /// Whether Gleanvolt has a charging session open (#414), from the SDK. False from a Gleanvolt that
    /// doesn't say, which leaves the car's own charge state to decide.
    /// </summary>
    public bool ChargingSessionOpen { get; set; }

    /// <summary>
    /// How long to wait before the next read: never while stopped; <see cref="VolkswagenOptions.PollInterval"/>
    /// while Gleanvolt has a session open or the car last said it is charging (a charge started from the
    /// car or at a public charger), as the built-in feeds do; otherwise the idle interval.
    /// </summary>
    public TimeSpan NextDelay =>
        IsBlocked ? Timeout.InfiniteTimeSpan
        : ChargingSessionOpen || _last?.ChargeState == CarChargeState.Charging ? _options.PollInterval
        : _options.IdlePollInterval;

    /// <summary>The SDK's call: after a hello, for <i>Ask the car</i>, and from the poller.</summary>
    public Task<VehicleReading?> GetReadingAsync(VehicleInfo car, CancellationToken cancellationToken) => ReadAsync(cancellationToken);

    /// <summary>
    /// Reads the car from VW, or answers with the last reading when stopped or asked again too soon.
    /// </summary>
    public async Task<VehicleReading?> ReadAsync(CancellationToken cancellationToken)
    {
        if (IsBlocked || TooSoon())
        {
            return _last;
        }

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another caller may have read while this one waited.
            if (IsBlocked || TooSoon())
            {
                return _last;
            }

            _lastReadAt = _time.GetUtcNow();
            VehicleReading? reading;

            try
            {
                reading = await _client.GetReadingAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
            {
                _logger.LogWarning("Asking volkswagen.de for the car failed ({Reason}).", ex.Message);
                Stop(PageText.Unreachable.Format(ex.Message));
                return _last;
            }

            if (reading is null)
            {
                Stop(_client.AwaitingCode ? PageText.WantsCode : PageText.NoReading);
                return _last;
            }

            _last = reading;
            SetStatus(new ProviderStatus(true));
            return reading;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Stops reading until the owner signs in on the page, and tells Gleanvolt the owner is needed. Nothing
    /// is sent to VW until then.
    /// </summary>
    public void Stop(Words what)
    {
        _blocked = true;
        _logger.LogWarning("Stopped reading the car until the owner signs in on the plugin's page: {What}", what.En);

        // needs-sign-in, which Gleanvolt words itself, with what happened in both languages (#415). The
        // English sentence is what a Gleanvolt older than vehicle 1.3 shows.
        var detail = what.Then(PageText.ResumesAfterSignIn);
        _statusWords = detail;
        SetStatus(new ProviderStatus(
            false,
            $"{what.En} Sign in again on the plugin's page; the feed resumes once you have. The charge is unaffected.",
            NeedsOwner: true,
            Code: ProviderStatusCodes.NeedsSignIn,
            Messages: detail.All));
    }

    /// <summary>The owner signed in on the page: back on the clock, starting with a read now.</summary>
    public void Resume()
    {
        _blocked = false;
        _lastReadAt = null;
        SetStatus(new ProviderStatus(true));
    }

    /// <summary>For tests and the manual's pictures: a reading as if VW had just given it.</summary>
    internal void Seed(VehicleReading reading, DateTimeOffset readAt)
    {
        _last = reading;
        _lastReadAt = readAt;
        SetStatus(new ProviderStatus(true));
    }

    private bool TooSoon() => _lastReadAt is { } at && _time.GetUtcNow() - at < _options.MinimumTimeBetweenReads;

    private void SetStatus(ProviderStatus status)
    {
        if (status.Healthy)
        {
            _statusWords = null;
        }

        _status = status;
        Changed?.Invoke();
    }
}

/// <summary>
/// Reads the car on the feed's clock and tells Gleanvolt how the feed is doing (#410). Publishing goes
/// through the SDK, which asks <see cref="VolkswagenFeed"/>; without a session attached the car is still
/// read, so the plugin's page shows it.
/// </summary>
public sealed class VolkswagenPoller : BackgroundService
{
    private readonly VehicleProviderConnection _connection;
    private readonly VolkswagenFeed _feed;
    private readonly ILogger<VolkswagenPoller> _logger;
    private TaskCompletionSource _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private ProviderStatus? _reported;

    public VolkswagenPoller(VehicleProviderConnection connection, VolkswagenFeed feed, ILogger<VolkswagenPoller> logger)
    {
        _connection = connection;
        _feed = feed;
        _logger = logger;

        // Here rather than when the loop starts: the first hello can come before it. A new session starts
        // with no status in Gleanvolt, so it is said again after every hello.
        connection.Welcomed += welcome => _ = ReportAsync(force: true);
        connection.SessionChanged += OnSessionChanged;
        feed.Changed += OnChanged;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var wake = _wake.Task;

            // Ends early when woken or stopped; neither throws.
            await Task.WhenAny(Task.Delay(_feed.NextDelay, stoppingToken), wake).ConfigureAwait(false);

            if (wake.IsCompleted)
            {
                _wake = new(TaskCreationOptions.RunContinuationsAsynchronously);
            }

            if (stoppingToken.IsCancellationRequested || _feed.IsBlocked)
            {
                continue;
            }

            if (_connection.IsAttached)
            {
                await _connection.PublishNowAsync(stoppingToken).ConfigureAwait(false);
            }
            else
            {
                await _feed.ReadAsync(stoppingToken).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Reads now, rather than at the next tick: after a sign-in on the page.</summary>
    public void Wake() => _wake.TrySetResult();

    // A session opening reads the car at once, rather than at the end of an idle wait, and from then on
    // every PollInterval (#414). The car may not have reported the plug-in yet; the next read catches it.
    private void OnSessionChanged(ChargingSessionInfo? session)
    {
        var opened = session is { Open: true } && !_feed.ChargingSessionOpen;
        _feed.ChargingSessionOpen = session is { Open: true };

        if (opened)
        {
            Wake();
        }
    }

    private void OnChanged()
    {
        _ = ReportAsync(force: false);

        // Back from a stop: read at once rather than at the end of an infinite wait.
        if (!_feed.IsBlocked && _feed.LastReadAt is null)
        {
            Wake();
        }
    }

    private async Task ReportAsync(bool force)
    {
        if (_feed.Status is not { } status || (!force && status == _reported))
        {
            return;
        }

        try
        {
            await _connection.ReportStatusAsync(status).ConfigureAwait(false);
            _reported = status;
        }
        catch (Exception ex)
        {
            _logger.LogDebug("Could not report the status to Gleanvolt ({Reason}).", ex.Message);
        }
    }
}
