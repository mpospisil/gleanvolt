using Gleanvolt.Core.Enums;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;

namespace Gleanvolt.Api.Tests;

/// <summary>
/// Stand-ins for the seams the API reads and drives. They record what was asked of them and answer
/// from memory: these tests are about the surface — what it accepts, what it refuses, what it maps and
/// who it says asked — and the control logic behind each seam has its own suite.
/// </summary>
internal sealed class FakeChargeActions : IChargeActions
{
    internal List<(ChargeControlMode Mode, string Source)> Starts { get; } = [];

    internal List<string> Stops { get; } = [];

    /// <summary>What the next start returns. A refused write is a case this surface has to report.</summary>
    internal ChargeActionResult NextResult { get; set; } = ChargeActionResult.Success;

    public Task<ChargeActionResult> StartAsync(ChargeControlMode mode, string source, CancellationToken cancellationToken = default)
    {
        Starts.Add((mode, source));
        return Task.FromResult(NextResult);
    }

    public Task<ChargeActionResult> StopAsync(string source, CancellationToken cancellationToken = default)
    {
        Stops.Add(source);
        return Task.FromResult(ChargeActionResult.Success);
    }
}

internal sealed class FakeTargetedChargeSelector : ITargetedChargeSelector
{
    public TargetedChargeRequest? Request { get; private set; }

    internal List<string> Sets { get; } = [];

    internal List<string> Clears { get; } = [];

    public void Set(TargetedChargeRequest request, string source)
    {
        Request = request;
        Sets.Add(source);
        Changed?.Invoke(request);
    }

    public void Clear(string source)
    {
        Request = null;
        Clears.Add(source);
        Changed?.Invoke(null);
    }

    public event Action<TargetedChargeRequest?>? Changed;
}

/// <summary>The fast charge's limit (#119), recording who set and cleared it.</summary>
internal sealed class FakeFastChargeSelector : IFastChargeSelector
{
    public FastChargeLimit? Limit { get; private set; }

    internal List<string> Sets { get; } = [];

    internal List<string> Clears { get; } = [];

    public void Set(FastChargeLimit limit, string source)
    {
        Limit = limit;
        Sets.Add(source);
        Changed?.Invoke(limit);
    }

    public void Clear(string source)
    {
        Limit = null;
        Clears.Add(source);
        Changed?.Invoke(null);
    }

    public event Action<FastChargeLimit?>? Changed;
}

internal sealed class FakeBatteryHoldSelector : IBatteryHoldSelector
{
    public bool Hold { get; private set; }

    internal List<(bool Hold, string Source)> Sets { get; } = [];

    public void Set(bool hold, string source)
    {
        Hold = hold;
        Sets.Add((hold, source));
        Changed?.Invoke(hold);
    }

    public event Action<bool>? Changed;
}

/// <summary>
/// A preview that answers with a canned plan, and remembers the request it was handed — which is the
/// part these tests are actually asserting on, because composing the request is the API's job and
/// planning is not.
/// </summary>
internal sealed class FakeTargetedChargePreview : ITargetedChargePreview
{
    internal List<TargetedChargeRequest> Requests { get; } = [];

    /// <summary>Null stands for "no poll has completed yet", which the endpoint has to report as such.</summary>
    internal bool HasTelemetry { get; set; } = true;

    /// <summary>
    /// A window with no room in it: limits that leave the charger no time at all to run (#128). The
    /// planner decides that for real; here it is a knob, because what this suite tests is what the
    /// endpoint does with the answer rather than how the answer was reached.
    /// </summary>
    internal bool WindowIsEmpty { get; set; }

    public TargetedChargePlan? Preview(TargetedChargeRequest request)
    {
        Requests.Add(request);

        if (!HasTelemetry)
        {
            return null;
        }

        var plan = Fixtures.Plan(request);

        return WindowIsEmpty
            ? plan with { CeilingEnergyWh = 0, ExpectedEnergyWh = 0, ShortfallWh = plan.RequiredEnergyWh }
            : plan;
    }
}

/// <summary>
/// A forecast source with the <b>two</b> stores a real one has: the live cache, which is the last thing
/// the provider said, and the retained history, which is every period a refresh has ever carried.
///
/// <para>It used to have one, answering the same object to all three accessors — and that is precisely
/// why issue #221 went unnoticed. A provider answers only <i>what is still to come</i>, so the two
/// diverge the moment a second refresh lands, and a fake that cannot express that cannot catch an
/// endpoint reading the wrong one.</para>
///
/// <para>The history is the real <see cref="SolarForecastHistory"/> rather than a stand-in for it, so
/// "what a refresh retains" is not re-implemented here with a different set of bugs. Setting
/// <see cref="Forecast"/> is one refresh: do it twice for a morning the live cache no longer reports.</para>
/// </summary>
internal sealed class FakeSolarForecastService : ISolarForecastService
{
    private readonly SolarForecastHistory _history = new();

    private IReadOnlyDictionary<DateOnly, SolarDayForecastSummary> _summaries =
        new Dictionary<DateOnly, SolarDayForecastSummary>();

    private SolarForecast? _cached;

    /// <summary>The zone days are reckoned in — the host's own, so the fake and the endpoint agree.</summary>
    internal TimeZoneInfo Zone { get; set; } = TimeZoneInfo.FindSystemTimeZoneById("Europe/Prague");

    /// <summary>
    /// The live cache. Assigning is a refresh: the forecast replaces the cache <i>and</i> is merged into
    /// the history, exactly as <c>SolcastForecastService.RefreshAsync</c> does.
    /// </summary>
    internal SolarForecast? Forecast
    {
        get => _cached;
        set
        {
            _cached = value;
            if (value is null)
            {
                return;
            }

            _history.Merge(value);
            _summaries = _history.DailySummaries(Zone);
        }
    }

    public SolarForecast? GetForecastForToday() => _cached;

    public SolarForecast? GetForecast(DateTimeOffset from, DateTimeOffset to) => _cached is null
        ? null
        : new SolarForecast(_cached.RetrievedAt, [.. _cached.Periods.Where(p => p.PeriodEnd > from && p.PeriodEnd <= to)]);

    public SolarForecast? GetDayForecast(DateOnly localDate) => _history.ForDate(localDate, Zone);

    public SolarDayForecastSummary? GetDaySummary(DateOnly localDate) =>
        _summaries.TryGetValue(localDate, out var summary) ? summary : null;

    public double? GetDayEnergyWattHours(DateOnly localDate) => GetDaySummary(localDate)?.ExpectedWh;
}

internal sealed class FakeWeatherService : IWeatherService
{
    public bool IsConfigured { get; set; }

    internal WeatherReading? Reading { get; set; }

    internal int Calls { get; private set; }

    public Task<WeatherReading?> GetCurrentAsync(CancellationToken cancellationToken = default)
    {
        Calls++;
        return Task.FromResult(Reading);
    }
}

/// <summary>An in-memory interval store. <see cref="Fails"/> is the "feature disabled" case.</summary>
internal sealed class FakeEnergyIntervalStore : IEnergyIntervalStore
{
    internal List<EnergyInterval> Intervals { get; } = [];

    internal bool Fails { get; set; }

    public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task AppendAsync(IReadOnlyList<EnergyInterval> intervals, CancellationToken cancellationToken)
    {
        Intervals.AddRange(intervals);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<EnergyInterval>> GetIntervalsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (Fails)
        {
            throw new InvalidOperationException("store unavailable");
        }

        return Task.FromResult<IReadOnlyList<EnergyInterval>>(
            [.. Intervals.Where(i => i.PeriodStart >= from && i.PeriodStart < to).OrderBy(i => i.PeriodStart)]);
    }

    public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
}

internal sealed class FakeChargingSessionStore : IChargingSessionStore
{
    internal List<ChargingSession> Sessions { get; } = [];

    internal Dictionary<Guid, ChargingSessionDocument> Documents { get; } = [];

    internal bool Fails { get; set; }

    public Task<int> InitializeAsync(CancellationToken cancellationToken) => Task.FromResult(0);

    public Task StartSessionAsync(ChargingSession session, CancellationToken cancellationToken)
    {
        Sessions.Add(session);
        return Task.CompletedTask;
    }

    public Task AppendAsync(
        IReadOnlyList<ChargingSessionSample> samples,
        IReadOnlyList<ChargingSessionEvent> events,
        CancellationToken cancellationToken) => Task.CompletedTask;

    public Task CompleteSessionAsync(ChargingSession session, CancellationToken cancellationToken) => Task.CompletedTask;

    public Task<IReadOnlyList<ChargingSession>> GetSessionsAsync(
        DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        if (Fails)
        {
            throw new InvalidOperationException("store unavailable");
        }

        return Task.FromResult<IReadOnlyList<ChargingSession>>(
            [.. Sessions.Where(s => s.StartedAt >= from && s.StartedAt < to).OrderByDescending(s => s.StartedAt)]);
    }

    public Task<ChargingSessionDocument?> ExportAsync(Guid sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(Documents.TryGetValue(sessionId, out var document) ? document : null);

    public Task<int> PruneAsync(TimeSpan retention, CancellationToken cancellationToken) => Task.FromResult(0);
}

/// <summary>
/// A secret store in memory. <c>/health</c> only ever asks it what protects the secrets, so what this
/// answers is the thing under test: the sentence, not the bytes.
/// </summary>
internal sealed class FakeSecretStore : ISecretStore
{
    private readonly Dictionary<string, string> _secrets = [];

    internal string Protection { get; set; } = "owner-only files (0600) in the data directory";

    public string? Read(string name) => _secrets.GetValueOrDefault(name);

    public bool Write(string name, string value)
    {
        _secrets[name] = value;
        return true;
    }

    public bool Delete(string name) => _secrets.Remove(name) || true;

    public string Describe() => Protection;
}
