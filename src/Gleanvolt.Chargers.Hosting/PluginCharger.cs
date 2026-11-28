using Gleanvolt.Providers.Client.Charger;
using Gleanvolt.Providers.Contracts;
using Gleanvolt.Providers.Contracts.Charger;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Gleanvolt.Chargers;

/// <summary>
/// The base of every charger here: an <see cref="IChargerSource"/> that keeps its newest reading and its status for
/// the page, and raises <see cref="Changed"/> for the page and for <see cref="StatusReporter"/>, which tells
/// Gleanvolt. A plugin implements the device: <see cref="ReadAsync"/>, <see cref="SetCurrentAsync"/> and
/// <see cref="SetChargingAllowedAsync"/>, calling <see cref="Healthy"/> or <see cref="Problem"/> as a read goes.
/// </summary>
public abstract class PluginCharger : IChargerSource, IPluginStatus
{
    /// <summary>Raised when the status, the newest reading or anything the page shows changes.</summary>
    public event Action? Changed;

    /// <summary>What the charger can do, sent in every hello.</summary>
    public abstract ChargerCapabilities Capabilities { get; }

    /// <summary>The newest reading, or null.</summary>
    public ChargerReading? LastReading { get; protected set; }

    /// <summary>How the plugin is doing, as told to Gleanvolt; null before the first read.</summary>
    public ProviderStatus? Status { get; private set; }

    /// <summary>What is wrong, in the page's language; null when healthy.</summary>
    public Words? StatusWords { get; private set; }

    public abstract Task<ChargerReading> ReadAsync(CancellationToken cancellationToken);

    public abstract Task SetCurrentAsync(int amps, CancellationToken cancellationToken);

    public abstract Task SetChargingAllowedAsync(bool allowed, CancellationToken cancellationToken);

    /// <summary>The address or the model changed on the page: what was learnt about the old charger is forgotten.</summary>
    public virtual void Reset()
    {
        LastReading = null;
        RaiseChanged();
    }

    /// <summary>The read worked. A <paramref name="warning"/> is something only the owner can fix, though the charger reads.</summary>
    protected void Healthy(Words? warning = null)
    {
        if (warning is { } said)
        {
            SetStatus(new ProviderStatus(false, said.En, NeedsOwner: true, Messages: said.All), said);
        }
        else
        {
            SetStatus(new ProviderStatus(true), null);
        }
    }

    /// <summary>The read failed for <paramref name="what"/>, one of <see cref="ProviderStatusCodes"/> as <paramref name="code"/>.</summary>
    protected void Problem(Words what, string code, bool needsOwner) =>
        SetStatus(new ProviderStatus(false, what.En, NeedsOwner: needsOwner, Code: code, Messages: what.All), what);

    /// <summary>Tells the page that something it shows changed.</summary>
    protected void RaiseChanged() => Changed?.Invoke();

    private void SetStatus(ProviderStatus status, Words? words)
    {
        Status = status;
        StatusWords = words;
        Changed?.Invoke();
    }
}

/// <summary>
/// What Gleanvolt said in the current session's welcome, for a charger that needs it: Huawei converts its setpoint
/// at the charge control's phases and voltage. Null between sessions.
/// </summary>
public sealed class GleanvoltSession
{
    /// <summary>The current session's welcome, or the last one.</summary>
    public ChargerWelcome? Welcome { get; internal set; }

    /// <summary>The charge control's limits, or null before the first welcome.</summary>
    public ChargerLimits? Limits => Welcome?.Limits;
}

/// <summary>
/// Tells Gleanvolt how the charger is: after every hello, since a new session starts with no status, and whenever the
/// charger's status changes. It also keeps the session's welcome for the page.
/// </summary>
public sealed class StatusReporter : PluginStatusReporter<ChargerWelcome>
{
    public StatusReporter(ChargerProviderConnection connection, PluginCharger charger, GleanvoltSession session, ILogger<StatusReporter> logger)
        : base(connection, charger, logger)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(session);

        // Here rather than at start: the first hello can come before it.
        connection.Welcomed += welcome => session.Welcome = welcome;
    }
}
