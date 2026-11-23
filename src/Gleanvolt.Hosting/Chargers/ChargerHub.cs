using Gleanvolt.Core.Configuration;
using Gleanvolt.Core.Models;
using Gleanvolt.Devices;
using Gleanvolt.Hosting.Configuration;
using Gleanvolt.Hosting.Tariff.External;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Gleanvolt.Providers.Server;
using Microsoft.Extensions.Options;

namespace Gleanvolt.Hosting.Chargers;

/// <summary>
/// The charger's hub (#435): a plugin publishes readings of the charger and says how each command ended, and
/// <see cref="ExternalChargerLink"/> hands both to the driver that waits for them. Nothing here waits.
///
/// <para>On while the controlled charger's driver is <see cref="ExternalChargerDriverFactory.DriverId"/>. The
/// driver is chosen with the installation, so changing it takes a restart, as for every other brand.</para>
/// </summary>
public sealed class ChargerHub(
    ExternalProviderRegistry registry,
    ProviderHostInfo host,
    ExternalChargerLink link,
    IOptions<ChargeControlOptions> chargeControl,
    TimeProvider time)
    : ProviderHub<IChargerClient, ChargerWelcome>(registry, host), IChargerHub
{
    /// <summary>The kind, as registered.</summary>
    public static ProviderKind Kind { get; } = new(
        ChargerContract.Kind,
        "Charger",
        ChargerContract.Major,
        ChargerContract.Minor,
        ChargerContract.HubPath,
        services => IsSelected(services.GetRequiredService<PvSystemInfo>()))
    {
        // A plugin writes to a device: a second one started by mistake, a sample, must not take it over (#440).
        RemembersProvider = true,
    };

    /// <summary>Whether the controlled charger is one behind a plugin.</summary>
    public static bool IsSelected(PvSystemInfo site) =>
        site.Chargers is [var charger, ..] && charger.Driver == ExternalChargerDriverFactory.DriverId;

    protected override ChargerWelcome CreateWelcome() => new()
    {
        Limits = Limits,
        DryRun = chargeControl.Value.DryRun,
        TimeZone = TariffHub.IanaId(time.LocalTimeZone),
    };

    /// <summary>
    /// The charger's capabilities against the charge control's limits (#440), by the same rule the built-in drivers
    /// are checked by at startup. A 1.0 plugin, which says none, is taken as <see cref="ChargerCapabilities.Default"/>.
    /// </summary>
    protected override (string? Code, string? Reason, IReadOnlyList<Phrase?>? Phrases) Admit(ProviderHello hello)
    {
        var capabilities = hello.Charger ?? ChargerCapabilities.Default;

        if (capabilities.Check() is { Count: > 0 } invalid)
        {
            var detail = string.Join(" ", invalid);
            return (RefusalCodes.Invalid, "The charger's capabilities are not usable: " + detail,
                [Phrase.Of("Charger.Capabilities", new PhraseArg.Text(detail))]);
        }

        var options = chargeControl.Value;
        var problems = new ConfigurationProblems();
        PvSystemRules.CheckChargeLimits(
            Describe(hello.Name, capabilities), options.MinChargingCurrentAmps, options.MaxChargingCurrentAmps, options.Phases, problems);

        return problems.Count > 0
            ? (RefusalCodes.Limits, string.Join(" ", problems), problems.Phrases)
            : (null, null, null);
    }

    protected override void OnAttached(ProviderHello hello) => link.Attached(hello.Charger ?? ChargerCapabilities.Default);

    /// <summary>A plugin's charger as the startup rule sees a driver.</summary>
    internal static EvChargerDriverInfo Describe(string name, ChargerCapabilities capabilities) => new(
        ExternalChargerDriverFactory.DriverId,
        name,
        EvChargerTransport.External,
        null,
        ExternalChargerDriver.CapabilitiesOf(capabilities));

    private ChargerLimits Limits
    {
        get
        {
            var options = chargeControl.Value;
            return new ChargerLimits(options.MinChargingCurrentAmps, options.MaxChargingCurrentAmps, options.Phases, options.NominalVoltage);
        }
    }

    public Task<PublishResult> Publish(ChargerReading reading)
    {
        RequireAttached();

        if (reading is null)
        {
            var empty = new ConfigurationProblems("The reading is empty.", Phrase.Of("Charger.Empty"));
            Sessions.DataRefused(null, empty, empty.Phrases);
            return Task.FromResult(PublishResult.Refused([.. empty]));
        }

        var problems = ChargerReadingValidator.Check(reading, time.GetUtcNow());

        if (problems.Count > 0)
        {
            Sessions.DataRefused(reading.InReplyTo, problems, problems.Phrases);
            link.Refused(reading.InReplyTo, problems);
            return Task.FromResult(PublishResult.Refused([.. problems]));
        }

        link.Accepted(reading with { Mode = reading.Mode?.Trim() is { Length: > 0 } mode ? mode : null });
        Sessions.DataAccepted(reading.InReplyTo);
        return Task.FromResult(PublishResult.Ok);
    }

    public Task CommandDone(ChargerCommandResult result)
    {
        RequireAttached();

        if (result is not null)
        {
            link.CommandDone(result);
        }

        return Task.CompletedTask;
    }
}
