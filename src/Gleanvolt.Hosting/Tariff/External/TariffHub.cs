using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Interfaces;
using Gleanvolt.Core.Models;
using Gleanvolt.Core.Strategies;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Tariff;
using Gleanvolt.Providers.Server;
using CoreRate = Gleanvolt.Core.Models.TariffRate;
using CoreWindow = Gleanvolt.Core.Models.TariffWindow;

namespace Gleanvolt.Hosting.Tariff.External;

/// <summary>
/// The low tariff's hub (#401): a provider publishes windows here, and once they pass every check they
/// replace what the <c>External</c> tariff strategy answers from. Nothing waits for the provider.
/// </summary>
public sealed class TariffHub(
    ExternalProviderRegistry registry,
    ProviderHostInfo host,
    ExternalTariffProvider tariff,
    TariffSchedule schedule,
    ITariffEditor editor,
    TimeProvider time)
    : ProviderHub<ITariffClient, TariffWelcome>(registry, host), ITariffHub
{
    /// <summary>The kind, as registered.</summary>
    public static ProviderKind Kind { get; } = new(
        TariffContract.Kind,
        "Tariff",
        TariffContract.Major,
        TariffContract.Minor,
        TariffContract.HubPath,
        services => services.GetRequiredService<ITariffEditor>().Read().Selects(TariffProviderIds.External));

    protected override TariffWelcome CreateWelcome() => new()
    {
        TimeZone = IanaId(time.LocalTimeZone),
        Currency = editor.Currency,
    };

    public Task<PublishResult> Publish(TariffData data)
    {
        RequireAttached();

        var windows = (data?.Windows ?? [])
            .Select(window => new CoreWindow(
                new TimeWindow(window.Start, window.End),
                window.Rate == Providers.Contracts.Tariff.TariffRate.Low ? CoreRate.Low : CoreRate.High,
                window.PricePerKwh))
            .ToList();

        var (joined, problems) = ExternalTariffRules.Check(windows, data?.Currency, editor.Currency, time.GetUtcNow());

        if (problems.Count > 0)
        {
            Sessions.DataRefused(data?.InReplyTo, [.. problems.Select(problem => problem.Message)], [.. problems.Select(problem => problem.Phrase)]);
            return Task.FromResult(PublishResult.Refused([.. problems.Select(problem => problem.Message)]));
        }

        tariff.Publish(joined, new TariffOrigin(Sessions.Status.ProviderName ?? Sessions.Status.ProviderId ?? "?", time.GetUtcNow()));
        schedule.Invalidate();
        Sessions.DataAccepted(data!.InReplyTo);
        return Task.FromResult(PublishResult.Ok);
    }

    // What the provider can look up in any language: Linux and the container already use these ids.
    internal static string IanaId(TimeZoneInfo zone) =>
        zone.HasIanaId ? zone.Id
        : TimeZoneInfo.TryConvertWindowsIdToIanaId(zone.Id, out var iana) ? iana
        : zone.Id;
}
