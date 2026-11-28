using Gleanvolt.Providers.Client.Vehicle;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Vehicle;

namespace Gleanvolt.Cars;

/// <summary>
/// What a car plugin's author extends (#473): <see cref="FetchAsync"/> reads the car from its maker, and everything
/// around it is here. However many reasons to read arrive at once (Gleanvolt's refresh, a charging session opening,
/// Read now on the page), the maker is asked once, and never sooner than
/// <see cref="CarPollOptions.MinimumTimeBetweenReads"/> after the last time. While the owner is needed
/// (<see cref="Block"/>: a sign-in, an emailed code) nothing is asked at all. A failed request is reported and the next
/// one may heal it.
/// </summary>
public abstract class PluginCar : IVehicleSource, IPluginStatus
{
    private readonly CarPollOptions _options;
    private readonly TimeProvider _time;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile VehicleReading? _last;
    private DateTimeOffset? _lastReadAt;
    private volatile bool _blocked;
    private VehicleInfo? _car;

    protected PluginCar(CarPollOptions options, TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(time);
        _options = options;
        _time = time;
    }

    /// <summary>The reading, the status or the block changed.</summary>
    public event Action? Changed;

    /// <summary>The owner unblocked the car (<see cref="Resume"/>): the poller reads at once.</summary>
    internal event Action? Resumed;

    /// <summary>The latest reading, or null before the first.</summary>
    public VehicleReading? LastReading => _last;

    /// <summary>When the maker was last asked, or null.</summary>
    public DateTimeOffset? LastReadAt => _lastReadAt;

    /// <summary>The status Gleanvolt is told, or null before the first read.</summary>
    public ProviderStatus? Status { get; private set; }

    /// <summary>The status in the viewer's language, when it is not healthy.</summary>
    public Words? StatusWords { get; private set; }

    /// <summary>Whether reading is stopped until the owner acts on the page.</summary>
    public bool IsBlocked => _blocked;

    /// <summary>Whether Gleanvolt has a charging session open (vehicle 1.2, #414); the poller keeps it.</summary>
    public bool ChargingSessionOpen { get; internal set; }

    /// <summary>
    /// How long until the next read: never while blocked; <see cref="CarPollOptions.PollInterval"/> while a session is
    /// open or the car says it is charging (at home or at a public charger); otherwise the idle interval.
    /// </summary>
    public virtual TimeSpan NextDelay =>
        IsBlocked ? Timeout.InfiniteTimeSpan
        : ChargingSessionOpen || _last?.ChargeState == CarChargeState.Charging ? _options.PollInterval
        : _options.IdlePollInterval;

    /// <summary>
    /// Reads the car, or answers with the last reading when blocked or asked again too soon. <paramref name="car"/> is
    /// Gleanvolt's car, or null for the last one known (none before the first welcome).
    /// </summary>
    public async Task<VehicleReading?> ReadAsync(VehicleInfo? car, CancellationToken cancellationToken)
    {
        if (car is not null)
        {
            Volatile.Write(ref _car, car);
        }

        if (_blocked)
        {
            return _last;
        }

        // Through the gate even when it is too soon: a read may be on its way, and its reading is the answer.
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            // Another caller may have read while this one waited.
            if (_blocked || TooSoon())
            {
                return _last;
            }

            var previous = _lastReadAt;
            _lastReadAt = _time.GetUtcNow();
            VehicleReading? reading;

            try
            {
                reading = await FetchAsync(Volatile.Read(ref _car), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A stop, not a problem: nothing is reported, and the next read is not held back by this one.
                _lastReadAt = previous;
                throw;
            }
            catch (Exception ex)
            {
                // A block the plugin set on the way (a sign-in refused) says more than the exception that followed it.
                if (!_blocked)
                {
                    OnFetchFailed(ex);
                }

                return _last;
            }

            if (reading is not null)
            {
                _last = reading;

                // A block set while the maker was asked (the owner changed the account) outlasts the answer.
                if (_blocked)
                {
                    RaiseChanged();
                }
                else
                {
                    Healthy();
                }
            }

            return _last;
        }
        finally
        {
            _gate.Release();
        }
    }

    Task<VehicleReading?> IVehicleSource.GetReadingAsync(VehicleInfo car, CancellationToken cancellationToken) =>
        ReadAsync(car, cancellationToken);

    /// <summary>
    /// Reads the car from its maker. <paramref name="car"/> is Gleanvolt's car, null before Gleanvolt's first welcome.
    /// Null when the maker had nothing new: the last reading stays, and so does the status.
    /// </summary>
    protected abstract Task<VehicleReading?> FetchAsync(VehicleInfo? car, CancellationToken cancellationToken);

    /// <summary>A read failed. By default the maker is reported unreachable and the next read tries again.</summary>
    protected virtual void OnFetchFailed(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        Problem(CarText.Unreachable.Format(exception.Message), ProviderStatusCodes.Unreachable, needsOwner: false);
    }

    /// <summary>The car reads fine.</summary>
    protected void Healthy() => SetStatus(new ProviderStatus(true), null);

    /// <summary>Something is wrong, in both languages, with Gleanvolt's code for it.</summary>
    protected void Problem(Words what, string code, bool needsOwner)
    {
        ArgumentNullException.ThrowIfNull(what);
        SetStatus(new ProviderStatus(false, what.En, NeedsOwner: needsOwner, Code: code, Messages: what.All), what);
    }

    /// <summary>
    /// Stops all reads until <see cref="Resume"/>, and tells Gleanvolt the owner is needed: a sign-in
    /// (<see cref="ProviderStatusCodes.NeedsSignIn"/>), a code (<see cref="ProviderStatusCodes.NeedsCode"/>), an account
    /// not set up (<see cref="ProviderStatusCodes.NotSetUp"/>). Nothing is sent to the maker until then.
    /// </summary>
    protected void Block(Words what, string code)
    {
        _blocked = true;
        Problem(what, code, needsOwner: true);
    }

    /// <summary>The owner acted on the page: back on the clock, starting with a read now.</summary>
    public void Resume()
    {
        _blocked = false;
        _lastReadAt = null;
        Healthy();
        Resumed?.Invoke();
    }

    /// <summary>Tells the page something of the plugin's own changed.</summary>
    protected void RaiseChanged() => Changed?.Invoke();

    private bool TooSoon() => _lastReadAt is { } at && _time.GetUtcNow() - at < _options.MinimumTimeBetweenReads;

    private void SetStatus(ProviderStatus status, Words? words)
    {
        Status = status;
        StatusWords = words;
        Changed?.Invoke();
    }
}
