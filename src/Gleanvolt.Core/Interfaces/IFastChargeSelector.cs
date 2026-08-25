using Gleanvolt.Core.Models;

namespace Gleanvolt.Core.Interfaces;

/// <summary>
/// The active <see cref="FastChargeLimit"/> — how much a fast charge should deliver before it stops
/// itself — held at runtime and changeable from any surface, exactly as
/// <see cref="ITargetedChargeSelector"/> holds the targeted request and
/// <see cref="IChargeControlModeSelector"/> holds the mode.
///
/// <para>Two seams rather than one, deliberately: the limit and the mode are set separately, so a
/// limit can be revised while the mode is running, and starting a fast charge with no limit at all is
/// the ordinary case — <see cref="Enums.FastChargeBasis.Full"/> — rather than a state that cannot be
/// expressed. A null <see cref="Limit"/> is therefore a normal, valid state and not a missing one.</para>
///
/// <para><b>Not persisted.</b> The limit drops on a restart exactly as the mode does, and for the same
/// reason: after a crash or a deploy the charger is left as its owner set it rather than being grabbed
/// by whatever was in a file.</para>
/// </summary>
public interface IFastChargeSelector
{
    /// <summary>The active limit, or null when the fast charge is unlimited (Full).</summary>
    FastChargeLimit? Limit { get; }

    /// <summary>Sets the limit. <paramref name="source"/> names who set it (for logging).</summary>
    void Set(FastChargeLimit limit, string source);

    /// <summary>
    /// Clears the limit, returning the mode to charging until the car itself stops. No-op (and no
    /// event) when there was none.
    /// </summary>
    void Clear(string source);

    /// <summary>Raised when the limit actually changes, with the new value (null when cleared).</summary>
    event Action<FastChargeLimit?>? Changed;
}
