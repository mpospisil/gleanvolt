using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;

namespace Gleanvolt.Hosting.Tariff;

/// <summary>
/// The <see cref="ITariffSchedule"/> the controller and every surface read (issue #265): whichever
/// strategy the saved settings select, asked for today and tomorrow in the controller's time zone.
///
/// <para><b>Switched at runtime.</b> The strategy is resolved from the settings on every expansion,
/// not wired in at startup, so selecting another one on Settings takes effect on the next read.</para>
///
/// <para>Expanded once per local day, after a save and after a strategy's refresh, rather than on every
/// read: the poll loop, the Home Assistant publisher and an open Overview all ask, and the answer only
/// changes at those moments.</para>
///
/// <para><b>Falls back to no restriction.</b> A selected strategy this build lacks, one that throws,
/// and one with nothing to answer from all give a null timeline with <see cref="Unavailable"/> saying
/// why, logged as a warning once per change.</para>
/// </summary>
public sealed class TariffSchedule : ITariffSchedule
{
    private readonly ITariffEditor _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<TariffSchedule> _logger;
    private readonly Lock _gate = new();

    private Expansion? _cached;
    private string? _lastLogged;

    public TariffSchedule(ITariffEditor settings, TimeProvider time, ILogger<TariffSchedule> logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
        _settings.Saved += Invalidate;
    }

    public TariffTimeline? Timeline => Current().Timeline;

    public string? Unavailable => Current().Unavailable;

    public string Currency => _settings.Currency;

    /// <summary>Drops the cached expansion, for the refresh worker after a strategy fetched something new.</summary>
    public void Invalidate()
    {
        lock (_gate)
        {
            _cached = null;
        }
    }

    private Expansion Current()
    {
        var now = _time.GetUtcNow();
        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, _time.LocalTimeZone).DateTime);

        lock (_gate)
        {
            if (_cached is not { } cached || cached.Day != today)
            {
                cached = Expand(now, today);
                _cached = cached;
                Report(cached.Unavailable);
            }

            return cached;
        }
    }

    private Expansion Expand(DateTimeOffset now, DateOnly today)
    {
        var settings = _settings.Read();
        var provider = _settings.Providers.FirstOrDefault(candidate => settings.Selects(candidate.Id));

        if (provider is null)
        {
            return new Expansion(today, null,
                $"The selected tariff provider, {settings.Provider}, is not in this build, so there is no tariff restriction.");
        }

        TariffTimeline? timeline;

        try
        {
            timeline = provider.Expand(new TariffContext(
                now, today, _time.LocalTimeZone, settings.PricesIn(_settings.Currency), settings.SectionFor(provider.Id)));
        }
        catch (Exception ex)
        {
            return new Expansion(today, null,
                $"The {provider.Name} tariff provider failed ({ex.Message}), so there is no tariff restriction.");
        }

        if (timeline is null && provider.Id != TariffProviderIds.None)
        {
            return new Expansion(today, null,
                $"The {provider.Name} tariff provider has nothing to answer from yet, so there is no tariff restriction.");
        }

        return new Expansion(today, timeline is null ? null : timeline with { ProviderId = provider.Id }, null);
    }

    // Once per change, not once per expansion: a day of the same failure is one line, not one a day.
    private void Report(string? unavailable)
    {
        if (unavailable == _lastLogged)
        {
            return;
        }

        _lastLogged = unavailable;

        if (unavailable is not null)
        {
            _logger.LogWarning("{Reason}", unavailable);
        }
    }

    private sealed record Expansion(DateOnly Day, TariffTimeline? Timeline, string? Unavailable);
}
