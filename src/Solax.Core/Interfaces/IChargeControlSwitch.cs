namespace Solax.Core.Interfaces;

/// <summary>
/// The runtime on/off switch for charge control. Seeded from configuration at startup, then
/// toggleable at runtime (e.g. from Home Assistant) without restarting the service. The polling
/// loop reads <see cref="IsEnabled"/> each cycle.
/// </summary>
public interface IChargeControlSwitch
{
    /// <summary>Whether charge control is currently enabled.</summary>
    bool IsEnabled { get; }

    /// <summary>
    /// Sets the enabled state. <paramref name="source"/> names who changed it (for logging), e.g.
    /// "Home Assistant" or "configuration". No-op (and no event) if the value is unchanged.
    /// </summary>
    void Set(bool enabled, string source);

    /// <summary>Raised when the enabled state actually changes, with the new value.</summary>
    event Action<bool>? Changed;
}
