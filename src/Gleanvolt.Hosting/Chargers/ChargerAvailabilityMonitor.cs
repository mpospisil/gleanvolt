using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Chargers;

/// <summary>
/// Whether Gleanvolt can control the charger (#441), from each poll: the charger's read, and for a charger behind a
/// plugin, the plugin's session (connected, refused, what it reported about itself). One answer for the control
/// loop, the web UI, the API and Home Assistant.
///
/// <para><b>The grace period.</b> The charger's link drops tens of times a day for a poll or two, and none of that
/// may suspend anything: until it has been unavailable for <see cref="ChargeControlOptions.ChargerUnavailableAfter"/>
/// it reads as available, and the poll goes on reading Unknown and making no decision, as it always has. The
/// reason may change during an outage; the outage's start does not.</para>
/// </summary>
public sealed class ChargerAvailabilityMonitor : IChargerAvailability
{
    private readonly TimeProvider _time;
    private readonly TimeSpan _grace;
    private readonly Func<bool> _behindPlugin;
    private readonly Func<ExternalProviderStatus?> _plugin;
    private readonly ILogger<ChargerAvailabilityMonitor> _logger;
    private readonly Lock _gate = new();
    private ChargerAvailability _current = ChargerAvailability.Ready;
    private DateTimeOffset? _unavailableSince;

    public ChargerAvailabilityMonitor(
        TimeProvider time,
        IOptions<ChargeControlOptions> options,
        PvSystemInfo site,
        IServiceProvider services,
        ILogger<ChargerAvailabilityMonitor> logger)
        : this(
            time,
            options.Value.ChargerUnavailableAfter,
            () => ChargerHub.IsSelected(site),
            () => services.GetService<IExternalProviders>()?.Find(ChargerContract.Kind),
            logger)
    {
    }

    /// <param name="time">The clock the grace period is measured on.</param>
    /// <param name="grace">How long an outage may last before it counts.</param>
    /// <param name="behindPlugin">Whether the charger is behind a plugin.</param>
    /// <param name="plugin">The charger kind's session, or null when the host has none.</param>
    /// <param name="logger">For the transitions.</param>
    internal ChargerAvailabilityMonitor(
        TimeProvider time,
        TimeSpan grace,
        Func<bool> behindPlugin,
        Func<ExternalProviderStatus?> plugin,
        ILogger<ChargerAvailabilityMonitor> logger)
    {
        _time = time;
        _grace = grace < TimeSpan.Zero ? TimeSpan.Zero : grace;
        _behindPlugin = behindPlugin;
        _plugin = plugin;
        _logger = logger;
    }

    public ChargerAvailability Current
    {
        get
        {
            lock (_gate)
            {
                return _current;
            }
        }
    }

    public event Action? Changed;

    /// <summary>Takes in one poll's reading of the charger and returns the availability as of it.</summary>
    public ChargerAvailability Observe(EnergyState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        var now = _time.GetUtcNow();
        var raw = Assess(state.EvChargerReadError);
        ChargerAvailability next;
        ChargerAvailability previous;

        lock (_gate)
        {
            previous = _current;

            if (raw is null)
            {
                _unavailableSince = null;
                next = ChargerAvailability.Ready;
            }
            else
            {
                var since = _unavailableSince ??= now;
                next = now - since >= _grace ? raw with { Since = since } : ChargerAvailability.Ready;
            }

            _current = next;
        }

        if (next.State != previous.State || next.Reason != previous.Reason)
        {
            if (next.Available)
            {
                _logger.LogInformation("The charger is available again.");
            }
            else
            {
                _logger.LogWarning(
                    "The charger is unavailable ({State}) since {Since:HH:mm:ss}: {Reason}", next.State, next.Since, next.Reason);
            }

            Changed?.Invoke();
        }

        return next;
    }

    // What is wrong right now, before the grace period: null when nothing is.
    private ChargerAvailability? Assess(string? readError)
    {
        if (_behindPlugin() && _plugin() is { } plugin)
        {
            if (plugin.State != ExternalProviderState.Connected)
            {
                if (plugin.LastRefusal is { } refusal)
                {
                    var english = $"Gleanvolt refused the charger plugin {refusal.ProviderName}: {refusal.Reason}";
                    // One sentence quoted in the viewer's language; several (a limits refusal naming two settings) in English.
                    var phrase = Phrase.Of("Charger.PluginRefused", new PhraseArg.Text(refusal.ProviderName),
                        new PhraseArg.Said(refusal.Phrases is [{ } only] ? only : null, refusal.Reason));
                    return new ChargerAvailability(ChargerAvailabilityState.NeedsOwner, english, phrase);
                }

                return new ChargerAvailability(
                    ChargerAvailabilityState.NoPlugin, "No charger plugin is connected.", Phrase.Of("Charger.NoPlugin"));
            }

            if (plugin.Healthy == false)
            {
                var said = plugin.StatusSaid;
                var detail = said?.English ?? "it gave no reason";
                return new ChargerAvailability(
                    plugin.NeedsOwner ? ChargerAvailabilityState.NeedsOwner : ChargerAvailabilityState.Error,
                    $"The charger plugin {plugin.ProviderName} reports a problem: {detail}",
                    Phrase.Of("Charger.PluginUnhealthy", new PhraseArg.Text(plugin.ProviderName ?? plugin.ProviderId ?? string.Empty),
                        new PhraseArg.Said(said?.Phrase, detail)));
            }
        }

        return readError is null
            ? null
            : new ChargerAvailability(
                ChargerAvailabilityState.Error,
                $"Gleanvolt can't read the charger: {readError}",
                Phrase.Of("Charger.Unreadable", new PhraseArg.Said(null, readError)));
    }
}
